package enrolment

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"runtime"
	"strings"
	"time"

	"github.com/matthogan/tsky/agent/internal/identity"
	tapproto "github.com/matthogan/tsky/agent/internal/protocol"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/encoding/protojson"
)

type Options struct {
	ControllerURL string
	PendingAgent  string
	Token         string
	ControllerID  string
	TLSPin        string
	Identity      *identity.Manager
	Version       string
}

func Enrol(ctx context.Context, opts Options) (*agentv1.EnrolmentResponse, error) {
	if opts.Identity == nil {
		return nil, fmt.Errorf("identity manager is required")
	}
	nonce, err := tapproto.RandomNonce()
	if err != nil {
		return nil, err
	}
	host, _ := os.Hostname()
	req := &agentv1.EnrolmentRequest{
		RequestedProtocolVersion: tapproto.CurrentVersion(),
		PendingAgentId:           opts.PendingAgent,
		EnrolmentToken:           opts.Token,
		ControllerId:             opts.ControllerID,
		Device: &agentv1.DeviceDescriptor{
			Hostname:              host,
			OperatingSystem:       runtime.GOOS,
			Architecture:          runtime.GOARCH,
			ImplementationName:    "tsky-agent",
			ImplementationVersion: opts.Version,
		},
		PublicKeyJwk: opts.Identity.PublicJWK(),
		Nonce:        nonce,
	}
	body, err := protojson.MarshalOptions{UseProtoNames: false}.Marshal(req)
	if err != nil {
		return nil, err
	}
	endpoint, err := enrolURL(opts.ControllerURL)
	if err != nil {
		return nil, err
	}
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	httpReq.Header.Set("Content-Type", "application/json")
	client := &http.Client{Timeout: 30 * time.Second, Transport: transportWithPin(opts.TLSPin)}
	resp, err := client.Do(httpReq)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	respBody, err := io.ReadAll(io.LimitReader(resp.Body, 4*1024*1024))
	if err != nil {
		return nil, err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("enrolment failed with HTTP %d: %s", resp.StatusCode, string(respBody))
	}
	var out agentv1.EnrolmentResponse
	if err := protojson.Unmarshal(respBody, &out); err != nil {
		return nil, err
	}
	if out.Error != nil && out.Error.Code != "" {
		return &out, fmt.Errorf("enrolment rejected: %s: %s", out.Error.Code, out.Error.Message)
	}
	if tapproto.SelectHighestCommon(out.SupportedTapVersions) == nil {
		return &out, fmt.Errorf("controller enrolment response has no compatible TAP semantic protocol version")
	}
	compatibleBinding := false
	for _, binding := range out.SupportedBindings {
		if tapproto.IsSupportedBinding(binding) {
			compatibleBinding = true
			break
		}
	}
	if !compatibleBinding {
		return &out, fmt.Errorf("controller enrolment response has no compatible TAP binding")
	}
	return &out, nil
}

func enrolURL(base string) (string, error) {
	u, err := url.Parse(base)
	if err != nil {
		return "", err
	}
	if u.Scheme == "" || u.Host == "" {
		return "", fmt.Errorf("controller URL must include scheme and host")
	}
	u.Path = strings.TrimRight(u.Path, "/") + "/api/agent/v1/enrol"
	return u.String(), nil
}

func transportWithPin(pin string) http.RoundTripper {
	tr := http.DefaultTransport.(*http.Transport).Clone()
	if pin == "" {
		return tr
	}
	expected := normalizePin(pin)
	tr.TLSClientConfig = &tls.Config{
		MinVersion: tls.VersionTLS12,
		VerifyConnection: func(cs tls.ConnectionState) error {
			if len(cs.PeerCertificates) == 0 {
				return fmt.Errorf("server did not provide a certificate")
			}
			opts := x509.VerifyOptions{DNSName: cs.ServerName, Roots: nil, Intermediates: x509.NewCertPool()}
			for _, cert := range cs.PeerCertificates[1:] {
				opts.Intermediates.AddCert(cert)
			}
			if _, err := cs.PeerCertificates[0].Verify(opts); err != nil {
				return err
			}
			sum := sha256.Sum256(cs.PeerCertificates[0].Raw)
			if hex.EncodeToString(sum[:]) != expected {
				return fmt.Errorf("server certificate pin mismatch")
			}
			return nil
		},
	}
	return tr
}

func normalizePin(pin string) string {
	pin = strings.TrimSpace(strings.TrimPrefix(pin, "sha256/"))
	if raw, err := base64.StdEncoding.DecodeString(pin); err == nil && len(raw) == sha256.Size {
		return hex.EncodeToString(raw)
	}
	if raw, err := base64.RawURLEncoding.DecodeString(pin); err == nil && len(raw) == sha256.Size {
		return hex.EncodeToString(raw)
	}
	return strings.ToLower(strings.ReplaceAll(pin, ":", ""))
}
