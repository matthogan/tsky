package transport

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"fmt"
	"io"
	"log/slog"
	"math/rand/v2"
	"net/url"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/execution"
	"github.com/matthogan/tsky/agent/internal/identity"
	tapproto "github.com/matthogan/tsky/agent/internal/protocol"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	"github.com/matthogan/tsky/agent/internal/schedule"
	"github.com/matthogan/tsky/agent/internal/telemetry"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/keepalive"
)

const unreachableRetryInterval = 60 * time.Second

type Client struct {
	cfg          config.Config
	identity     *identity.Manager
	store        *runtimefs.Store
	engine       *execution.Engine
	telemetry    *telemetry.Collector
	log          *slog.Logger
	outbound     chan *agentv1.AgentFrame
	outboundMu   sync.Mutex
	draining     atomic.Bool
	hasConnected bool
	connected    atomic.Bool
	runtimeCtx   context.Context
	scheduler    *schedule.Runtime
}

func New(cfg config.Config, id *identity.Manager, store *runtimefs.Store, engine *execution.Engine, tel *telemetry.Collector, scheduler *schedule.Runtime, log *slog.Logger) *Client {
	c := &Client{
		cfg:       cfg,
		identity:  id,
		store:     store,
		engine:    engine,
		telemetry: tel,
		log:       log,
		outbound:  make(chan *agentv1.AgentFrame, 1024),
		scheduler: scheduler,
	}
	if scheduler != nil {
		scheduler.SetSender(c)
	}
	return c
}

func (c *Client) Send(frame *agentv1.AgentFrame) error {
	// Run and occurrence state are durable. While disconnected there is no
	// stream to write to; reconciliation replays the durable evidence later.
	c.outboundMu.Lock()
	defer c.outboundMu.Unlock()
	if !c.connected.Load() {
		return nil
	}
	select {
	case c.outbound <- frame:
		return nil
	default:
		return fmt.Errorf("outbound TAP queue is full")
	}
}

func (c *Client) Run(ctx context.Context) error {
	if c.cfg.Controller.URL == "" {
		return fmt.Errorf("controller.url is required")
	}
	c.runtimeCtx = ctx
	if c.scheduler != nil {
		go c.scheduler.Run(ctx)
	}
	caSource := "(system trust store)"
	if caPath := strings.TrimSpace(c.cfg.Controller.TLSCACertPath); caPath != "" {
		caSource = caPath
	}
	minBackoff := time.Duration(c.cfg.Connection.ReconnectMinimumSeconds) * time.Second
	maxBackoff := time.Duration(c.cfg.Connection.ReconnectMaximumSeconds) * time.Second
	backoff := minBackoff
	attempt := 0
	for {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		attempt++
		c.log.Info("TAP connection attempt", "attempt", attempt, "controller", c.cfg.Controller.URL, "caCertPath", caSource)
		err := c.connectOnce(ctx)
		if err == nil || ctx.Err() != nil {
			return err
		}

		sleep := jitter(backoff)
		if isListenerUnreachable(err) {
			sleep = unreachableRetryInterval
			c.log.Warn("TAP listener unreachable; retrying on fixed interval", "error", err, "sleep", sleep, "attempt", attempt)
		} else {
			c.log.Warn("TAP connection ended; reconnecting", "error", err, "sleep", sleep, "attempt", attempt)
		}
		timer := time.NewTimer(sleep)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}

		if isListenerUnreachable(err) {
			backoff = minBackoff
			continue
		}

		backoff *= 2
		if backoff > maxBackoff {
			backoff = maxBackoff
		}
	}
}

func (c *Client) connectOnce(ctx context.Context) error {
	target, tlsConfig, err := grpcTarget(c.cfg.Controller.URL, c.cfg.Controller.TLSPinnedSHA256, c.cfg.Controller.TLSCACertPath)
	if err != nil {
		return err
	}
	var transportCreds credentials.TransportCredentials
	if tlsConfig == nil {
		transportCreds = insecure.NewCredentials()
	} else {
		transportCreds = credentials.NewTLS(tlsConfig)
	}
	conn, err := grpc.NewClient(target,
		grpc.WithTransportCredentials(transportCreds),
		grpc.WithKeepaliveParams(keepalive.ClientParameters{Time: 30 * time.Second, Timeout: 10 * time.Second}),
	)
	if err != nil {
		return err
	}
	defer conn.Close()
	client := agentv1.NewAgentControlClient(conn)
	stream, err := client.Connect(ctx, grpc.MaxCallRecvMsgSize(64*1024*1024), grpc.MaxCallSendMsgSize(64*1024*1024))
	if err != nil {
		return err
	}
	agentNonce, err := tapproto.RandomNonce()
	if err != nil {
		return err
	}
	if err := stream.Send(&agentv1.AgentFrame{
		// SessionHello precedes version selection, so its envelope uses the TAP
		// 1.0 compatibility baseline while the body advertises 1.1 first.
		Header: tapproto.HeaderForVersion(c.cfg.Identity.AgentID, tapproto.LegacyVersion()),
		Body: &agentv1.AgentFrame_SessionHello{SessionHello: &agentv1.SessionHello{
			AgentId:               c.cfg.Identity.AgentID,
			KeyId:                 c.cfg.Identity.KeyID,
			AgentNonce:            agentNonce,
			SupportedTapVersions:  tapproto.SupportedVersions(),
			SupportedBindings:     []*agentv1.ProtocolBinding{{Profile: tapproto.BindingProfile, Version: tapproto.BindingVersion, Endpoint: c.cfg.Controller.URL}},
			Extensions:            []*agentv1.ExtensionSupport{{Identifier: "schedule.v1", Version: "1", Required: false}},
			ImplementationName:    "tsky-agent",
			ImplementationVersion: c.telemetry.Snapshot(c.engine.ActiveRuns(), c.engine.QueuedRuns(), c.engine.RejectedByCapacity(), c.draining.Load()).ImplementationVersion,
		}},
	}); err != nil {
		return err
	}
	challengeFrame, err := stream.Recv()
	if err != nil {
		return err
	}
	challenge := challengeFrame.GetSessionChallenge()
	if challenge == nil {
		return fmt.Errorf("controller did not send SessionChallenge")
	}
	if !tapproto.IsSupportedVersion(challenge.SelectedTapVersion) {
		return fmt.Errorf("controller selected unsupported TAP version %d.%d", challenge.GetSelectedTapVersion().GetMajor(), challenge.GetSelectedTapVersion().GetMinor())
	}
	if !tapproto.IsSupportedBinding(challenge.SelectedBinding) {
		return fmt.Errorf("controller selected unsupported TAP binding %s:%s", challenge.GetSelectedBinding().GetProfile(), challenge.GetSelectedBinding().GetVersion())
	}
	if !tapproto.IsFrameHeaderValid(challengeFrame.GetHeader(), challenge.SelectedTapVersion) {
		return fmt.Errorf("SessionChallenge frame header does not match selected TAP version")
	}
	if len(challenge.ControllerNonce) < 16 {
		return fmt.Errorf("controller challenge nonce must contain at least 128 bits")
	}
	if challenge.ExpiresAt == nil || !challenge.ExpiresAt.AsTime().After(time.Now()) {
		return fmt.Errorf("controller challenge has expired")
	}
	if challenge.SecurityProfile != "tap-es256-p1363-v1" {
		return fmt.Errorf("controller selected unsupported security profile %q", challenge.SecurityProfile)
	}
	transcript, err := tapproto.AuthTranscript(challenge, c.cfg.Identity.AgentID, c.cfg.Identity.KeyID, agentNonce)
	if err != nil {
		return err
	}
	sig, err := c.identity.SignP1363(transcript)
	if err != nil {
		return err
	}
	if err := stream.Send(&agentv1.AgentFrame{
		Header: tapproto.HeaderForVersion(challenge.SessionId, challenge.SelectedTapVersion),
		Body: &agentv1.AgentFrame_SessionProof{SessionProof: &agentv1.SessionProof{
			SessionId:       challenge.SessionId,
			AgentId:         c.cfg.Identity.AgentID,
			KeyId:           c.cfg.Identity.KeyID,
			SecurityProfile: challenge.SecurityProfile,
			Signature:       sig,
		}},
	}); err != nil {
		return err
	}
	acceptedFrame, err := stream.Recv()
	if err != nil {
		return err
	}
	if rejected := acceptedFrame.GetSessionRejected(); rejected != nil {
		return fmt.Errorf("session rejected: %s", rejected.Error.GetMessage())
	}
	accepted := acceptedFrame.GetSessionAccepted()
	if accepted == nil {
		return fmt.Errorf("controller did not accept the session")
	}
	if !tapproto.VersionsEqual(accepted.SelectedTapVersion, challenge.SelectedTapVersion) {
		return fmt.Errorf("controller accepted a TAP version different from the authenticated challenge")
	}
	if !tapproto.IsFrameHeaderValid(acceptedFrame.GetHeader(), accepted.SelectedTapVersion) {
		return fmt.Errorf("SessionAccepted frame header does not match negotiated TAP version")
	}
	if !tapproto.IsSupportedBinding(accepted.SelectedBinding) || !tapproto.BindingsEqual(accepted.SelectedBinding, challenge.SelectedBinding) {
		return fmt.Errorf("controller accepted a TAP binding different from the authenticated challenge")
	}
	selectedVersion := accepted.SelectedTapVersion
	var sendMu sync.Mutex
	send := func(frame *agentv1.AgentFrame) error {
		sendMu.Lock()
		defer sendMu.Unlock()
		tapproto.SetFrameVersion(frame, selectedVersion)
		return stream.Send(frame)
	}
	scheduleNegotiated := tapproto.SupportsScheduleV1(selectedVersion) && hasExtension(accepted.NegotiatedExtensions, "schedule.v1", "1")
	if c.hasConnected {
		c.log.Info("TAP reconnected successfully",
			"sessionId", accepted.GetSessionId(),
			"binding", accepted.GetSelectedBinding().GetProfile(),
			"controller", c.cfg.Controller.URL)
	} else {
		c.log.Info("TAP session established",
			"sessionId", accepted.GetSessionId(),
			"binding", accepted.GetSelectedBinding().GetProfile(),
			"controller", c.cfg.Controller.URL)
	}
	c.hasConnected = true
	c.outboundMu.Lock()
	c.connected.Store(true)
	c.outboundMu.Unlock()
	defer c.disconnect()
	if err := send(&agentv1.AgentFrame{Header: tapproto.Header(c.cfg.Identity.AgentID), Body: &agentv1.AgentFrame_AgentSnapshot{AgentSnapshot: c.telemetry.Snapshot(c.engine.ActiveRuns(), c.engine.QueuedRuns(), c.engine.RejectedByCapacity(), c.draining.Load())}}); err != nil {
		return err
	}
	if err := send(&agentv1.AgentFrame{Header: tapproto.Header(c.cfg.Identity.AgentID), Body: &agentv1.AgentFrame_CapabilityReport{CapabilityReport: c.telemetry.Capability(c.engine.ActiveRuns(), c.engine.QueuedRuns(), c.engine.RejectedByCapacity())}}); err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	errs := make(chan error, 3)
	go c.sender(ctx, send, errs)
	go c.heartbeat(ctx, send, errs)
	go c.receiver(ctx, stream, selectedVersion, scheduleNegotiated, errs)
	err = <-errs
	cancel()
	if err == io.EOF {
		return fmt.Errorf("controller closed TAP stream")
	}
	return err
}

func (c *Client) disconnect() {
	c.outboundMu.Lock()
	defer c.outboundMu.Unlock()
	c.connected.Store(false)
	for {
		select {
		case <-c.outbound:
		default:
			return
		}
	}
}

func (c *Client) sender(ctx context.Context, send func(*agentv1.AgentFrame) error, errs chan<- error) {
	for {
		select {
		case <-ctx.Done():
			return
		case frame := <-c.outbound:
			if err := send(frame); err != nil {
				errs <- err
				return
			}
		}
	}
}

func (c *Client) heartbeat(ctx context.Context, send func(*agentv1.AgentFrame) error, errs chan<- error) {
	ticker := time.NewTicker(c.cfg.HeartbeatInterval())
	defer ticker.Stop()
	var seq uint64
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			seq++
			frame := &agentv1.AgentFrame{
				Header: tapproto.Header(c.cfg.Identity.AgentID),
				Body:   &agentv1.AgentFrame_AgentHeartbeat{AgentHeartbeat: c.telemetry.Heartbeat(seq, c.engine.ActiveRuns(), c.engine.QueuedRuns(), c.engine.RejectedByCapacity(), c.draining.Load())},
			}
			if err := send(frame); err != nil {
				errs <- err
				return
			}
		}
	}
}

func (c *Client) receiver(ctx context.Context, stream agentv1.AgentControl_ConnectClient, selectedVersion *agentv1.ProtocolVersion, scheduleNegotiated bool, errs chan<- error) {
	for {
		frame, err := stream.Recv()
		if err != nil {
			errs <- err
			return
		}
		if !tapproto.IsFrameHeaderValid(frame.GetHeader(), selectedVersion) {
			errs <- fmt.Errorf("controller frame TAP version does not match negotiated version %d.%d", selectedVersion.GetMajor(), selectedVersion.GetMinor())
			return
		}
		switch body := frame.Body.(type) {
		case *agentv1.ControllerFrame_RunOffer:
			runCtx := c.runtimeCtx
			if runCtx == nil {
				runCtx = ctx
			}
			c.engine.HandleOffer(runCtx, c, body.RunOffer)
		case *agentv1.ControllerFrame_CancelRun:
			c.engine.Cancel(body.CancelRun.RunId, body.CancelRun.AttemptId)
		case *agentv1.ControllerFrame_EventAcknowledgement:
			attemptCount := len(body.EventAcknowledgement.GetAttempts())
			runID := ""
			attemptID := ""
			if attemptCount > 0 {
				runID = body.EventAcknowledgement.GetAttempts()[0].GetRunId()
				attemptID = body.EventAcknowledgement.GetAttempts()[0].GetAttemptId()
			}
			c.log.Warn(
				"controller frame type not implemented by agent",
				"frameType", "EventAcknowledgement",
				"attemptCount", attemptCount,
				"firstRunId", runID,
				"firstAttemptId", attemptID,
			)
		case *agentv1.ControllerFrame_ReconciliationRequest:
			_ = c.Send(&agentv1.AgentFrame{Header: tapproto.Header(body.ReconciliationRequest.ReconciliationId), Body: &agentv1.AgentFrame_ReconciliationResponse{ReconciliationResponse: c.reconcile(body.ReconciliationRequest)}})
		case *agentv1.ControllerFrame_DrainRequest:
			c.draining.Store(body.DrainRequest.Draining)
		case *agentv1.ControllerFrame_ArtifactUploadGrant:
			c.log.Warn(
				"controller frame type not implemented by agent",
				"frameType", "ArtifactUploadGrant",
				"artifactId", body.ArtifactUploadGrant.GetArtifactId(),
				"runId", body.ArtifactUploadGrant.GetRunId(),
				"attemptId", body.ArtifactUploadGrant.GetAttemptId(),
			)
		case *agentv1.ControllerFrame_PolicyNotice:
			c.log.Warn(
				"controller frame type not implemented by agent",
				"frameType", "PolicyNotice",
				"code", body.PolicyNotice.GetCode(),
				"message", body.PolicyNotice.GetMessage(),
			)
		case *agentv1.ControllerFrame_Ping:
			_ = c.Send(&agentv1.AgentFrame{Header: tapproto.Header(body.Ping.PingId), Body: &agentv1.AgentFrame_Pong{Pong: &agentv1.ProtocolPong{PingId: body.Ping.PingId}}})
		case *agentv1.ControllerFrame_ProtocolError:
			c.log.Warn("controller reported protocol error", "code", body.ProtocolError.Error.GetCode(), "message", body.ProtocolError.Error.GetMessage())
		case *agentv1.ControllerFrame_ScheduleUpsert:
			if scheduleNegotiated && c.scheduler != nil {
				c.scheduler.HandleUpsert(body.ScheduleUpsert)
			}
		case *agentv1.ControllerFrame_ScheduleDelete:
			if scheduleNegotiated && c.scheduler != nil {
				c.scheduler.HandleDelete(body.ScheduleDelete)
			}
		case *agentv1.ControllerFrame_SchedulePause:
			if scheduleNegotiated && c.scheduler != nil {
				c.scheduler.HandlePause(body.SchedulePause)
			}
		case *agentv1.ControllerFrame_ScheduleResume:
			if scheduleNegotiated && c.scheduler != nil {
				c.scheduler.HandleResume(body.ScheduleResume)
			}
		case *agentv1.ControllerFrame_ScheduleSyncRequest:
			if scheduleNegotiated && c.scheduler != nil {
				_ = c.Send(&agentv1.AgentFrame{Header: tapproto.Header(body.ScheduleSyncRequest.SyncId), Body: &agentv1.AgentFrame_ScheduleSyncResponse{ScheduleSyncResponse: c.scheduler.Sync(body.ScheduleSyncRequest)}})
			}
		}
	}
}

func hasExtension(extensions []*agentv1.ExtensionSupport, identifier, version string) bool {
	for _, extension := range extensions {
		if extension.GetIdentifier() == identifier && extension.GetVersion() == version {
			return true
		}
	}
	return false
}

func (c *Client) reconcile(req *agentv1.ReconciliationRequest) *agentv1.ReconciliationResponse {
	states, _ := c.store.Attempts(req.IncludeRecentTerminalAttempts)
	attempts := make([]*agentv1.ReconciledAttempt, 0, len(states))
	for _, st := range states {
		digest, _ := hex.DecodeString(st.PlanDigestSHA256)
		attempts = append(attempts, &agentv1.ReconciledAttempt{
			RunId:                     st.RunID,
			AttemptId:                 st.AttemptID,
			PlanDigestSha256:          digest,
			State:                     st.State,
			HighestLocalEventSequence: st.HighestEventSeq,
			ProcessIsKnownRunning:     st.ProcessKnownRunning,
			HighestLocalOutputSequences: []*agentv1.OutputAcknowledgement{
				{StepId: "", Stream: agentv1.OutputStream_OUTPUT_STREAM_STDOUT, HighestContiguousSequence: st.StdoutSequence},
				{StepId: "", Stream: agentv1.OutputStream_OUTPUT_STREAM_STDERR, HighestContiguousSequence: st.StderrSequence},
			},
		})
	}
	return &agentv1.ReconciliationResponse{ReconciliationId: req.ReconciliationId, Attempts: attempts}
}

func grpcTarget(rawURL, pin, caCertPath string) (string, *tls.Config, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return "", nil, err
	}
	if u.Host == "" {
		return "", nil, fmt.Errorf("controller URL must include host")
	}
	switch u.Scheme {
	case "http":
		if strings.TrimSpace(caCertPath) != "" {
			return "", nil, fmt.Errorf("controller.tlsCaCertPath requires controller.url to use https")
		}
		return u.Host, nil, nil
	case "https":
		cfg := &tls.Config{MinVersion: tls.VersionTLS12, ServerName: u.Hostname()}
		if strings.TrimSpace(caCertPath) != "" {
			rootCAs, err := loadRootCAsFromPEM(caCertPath)
			if err != nil {
				return "", nil, err
			}
			cfg.RootCAs = rootCAs
		}
		if pin != "" {
			expected := normalizePin(pin)
			cfg.VerifyConnection = func(cs tls.ConnectionState) error {
				if len(cs.PeerCertificates) == 0 {
					return fmt.Errorf("server did not provide a certificate")
				}
				sum := sha256.Sum256(cs.PeerCertificates[0].Raw)
				if hex.EncodeToString(sum[:]) != expected {
					return fmt.Errorf("server certificate pin mismatch")
				}
				return nil
			}
		}
		return u.Host, cfg, nil
	default:
		return "", nil, fmt.Errorf("unsupported controller URL scheme %q", u.Scheme)
	}
}

func loadRootCAsFromPEM(path string) (*x509.CertPool, error) {
	pemData, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("failed to read controller.tlsCaCertPath %q: %w", path, err)
	}

	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(pemData) {
		return nil, fmt.Errorf("controller.tlsCaCertPath %q does not contain valid PEM certificates", path)
	}

	return pool, nil
}

func jitter(d time.Duration) time.Duration {
	if d <= 0 {
		return time.Second
	}
	return time.Duration(rand.Int64N(int64(d))) + d/2
}

func normalizePin(pin string) string {
	pin = strings.TrimSpace(strings.TrimPrefix(pin, "sha256/"))
	return strings.ToLower(strings.ReplaceAll(pin, ":", ""))
}

func isListenerUnreachable(err error) bool {
	if err == nil {
		return false
	}

	message := strings.ToLower(err.Error())
	return strings.Contains(message, "dial tcp") ||
		strings.Contains(message, "connectex") ||
		strings.Contains(message, "connection refused") ||
		strings.Contains(message, "actively refused") ||
		strings.Contains(message, "no connection could be made") ||
		strings.Contains(message, "transport: error while dialing")
}
