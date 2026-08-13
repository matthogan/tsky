package execution

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/policy"
	tapproto "github.com/matthogan/tsky/agent/internal/protocol"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/types/known/structpb"
	"google.golang.org/protobuf/types/known/timestamppb"
)

type Sender interface {
	Send(*agentv1.AgentFrame) error
}

type Engine struct {
	cfg                config.Config
	store              *runtimefs.Store
	policy             policy.Policy
	log                *slog.Logger
	mu                 sync.Mutex
	active             map[string]context.CancelFunc
	running            chan struct{}
	admitted           chan struct{}
	rejectedByCapacity uint64
}

var errAdmissionQueueTimeout = errors.New("admission queue wait timed out")

func New(cfg config.Config, store *runtimefs.Store, log *slog.Logger) *Engine {
	return &Engine{
		cfg:      cfg,
		store:    store,
		policy:   policy.New(cfg),
		log:      log,
		active:   map[string]context.CancelFunc{},
		running:  make(chan struct{}, cfg.Execution.MaxConcurrentRuns),
		admitted: make(chan struct{}, cfg.Execution.MaxConcurrentRuns+cfg.Execution.MaxPendingQueueDepth),
	}
}

func (e *Engine) HandleOffer(ctx context.Context, sender Sender, offer *agentv1.RunOffer) {
	acceptance, duplicate, err := e.accept(offer)
	if err != nil {
		if sendErr := e.sendFrame(sender, &agentv1.AgentFrame{
			Header: tapproto.Header(offer.OfferId),
			Body: &agentv1.AgentFrame_RunRejected{RunRejected: &agentv1.RunRejected{
				OfferId:   offer.OfferId,
				RunId:     offer.RunId,
				AttemptId: offer.AttemptId,
				Error:     tapError("run_rejected", err.Error(), false),
			}},
		}, "RunRejected", offer.RunId, offer.AttemptId); sendErr != nil {
			e.log.Error("failed to send run rejection", "error", sendErr, "runId", offer.RunId, "attemptId", offer.AttemptId, "offerId", offer.OfferId)
		}
		return
	}
	if err := e.sendFrame(sender, &agentv1.AgentFrame{
		Header: tapproto.Header(offer.OfferId),
		Body: &agentv1.AgentFrame_RunAccepted{RunAccepted: &agentv1.RunAccepted{
			OfferId:          acceptance.OfferID,
			RunId:            acceptance.RunID,
			AttemptId:        acceptance.AttemptID,
			PlanDigestSha256: offer.PlanDigestSha256,
			AcceptedAt:       timestamppb.New(acceptance.AcceptedAt),
		}},
	}, "RunAccepted", offer.RunId, offer.AttemptId); err != nil {
		e.log.Error("failed to send run acceptance", "error", err, "runId", offer.RunId, "attemptId", offer.AttemptId, "offerId", offer.OfferId)
		return
	}
	if duplicate {
		return
	}
	if err := e.acquireAdmission(ctx); err != nil {
		if errors.Is(err, errAdmissionQueueTimeout) {
			atomic.AddUint64(&e.rejectedByCapacity, 1)
			e.failAdmission(sender, offer, agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT, "admission_queue_timeout", err.Error())
			return
		}
		e.failAdmission(sender, offer, agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED, "admission_cancelled", err.Error())
		return
	}
	go e.execute(ctx, sender, offer)
}

func (e *Engine) Cancel(runID, attemptID string) {
	key := runID + "/" + attemptID
	e.mu.Lock()
	cancel := e.active[key]
	e.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

func (e *Engine) ActiveRuns() uint32 {
	e.mu.Lock()
	defer e.mu.Unlock()
	return uint32(len(e.active))
}

func (e *Engine) QueuedRuns() uint32 {
	e.mu.Lock()
	active := len(e.active)
	e.mu.Unlock()
	admitted := len(e.admitted)
	if admitted <= active {
		return 0
	}
	return uint32(admitted - active)
}

func (e *Engine) RejectedByCapacity() uint64 {
	return atomic.LoadUint64(&e.rejectedByCapacity)
}

// ValidatePlan applies the same executor and working-directory policy used when
// accepting a RunOffer. Schedule definitions call this before they are made
// durable so an invalid payload cannot fail repeatedly at every occurrence.
func (e *Engine) ValidatePlan(plan *agentv1.RunPlan) error {
	if plan == nil || len(plan.Steps) == 0 {
		return fmt.Errorf("run plan must contain at least one step")
	}
	for _, step := range plan.Steps {
		if step == nil || step.Command == nil {
			return fmt.Errorf("run step is missing its command")
		}
		if !e.policy.ExecutorAllowed(step.Command.GetExecutorId()) {
			return fmt.Errorf("executor %q is not allowed", step.Command.GetExecutorId())
		}
		if wd := stringParam(step.Command.GetParameters(), "workingDirectory"); wd != "" {
			if err := e.policy.ValidateWorkingDirectory(wd); err != nil {
				return err
			}
		}
	}
	return nil
}

func (e *Engine) accept(offer *agentv1.RunOffer) (*runtimefs.Acceptance, bool, error) {
	if offer == nil || offer.Plan == nil {
		return nil, false, fmt.Errorf("run offer missing plan")
	}
	if offer.OfferExpiresAt != nil && time.Now().After(offer.OfferExpiresAt.AsTime()) {
		return nil, false, fmt.Errorf("run offer has expired")
	}
	digest, err := tapproto.DigestPlan(offer.Plan)
	if err != nil {
		return nil, false, err
	}
	if !bytes.Equal(digest, offer.PlanDigestSha256) {
		return nil, false, fmt.Errorf("plan digest mismatch")
	}
	if err := e.ValidatePlan(offer.Plan); err != nil {
		return nil, false, err
	}
	return e.store.Accept(offer)
}

func (e *Engine) execute(parent context.Context, sender Sender, offer *agentv1.RunOffer) {
	defer func() { <-e.admitted }()
	select {
	case e.running <- struct{}{}:
	case <-parent.Done():
		e.failAdmission(sender, offer, agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED, "admission_cancelled", parent.Err().Error())
		return
	}
	defer func() { <-e.running }()
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	if offer.Plan.Policy != nil && offer.Plan.Policy.MaximumDuration != nil {
		ctx, cancel = context.WithTimeout(ctx, offer.Plan.Policy.MaximumDuration.AsDuration())
		defer cancel()
	}
	key := offer.RunId + "/" + offer.AttemptId
	e.mu.Lock()
	e.active[key] = cancel
	e.mu.Unlock()
	defer func() {
		e.mu.Lock()
		delete(e.active, key)
		e.mu.Unlock()
	}()
	state, _ := e.store.ReadState(offer.RunId, offer.AttemptId)
	if state == nil {
		state = &runtimefs.State{RunID: offer.RunId, AttemptID: offer.AttemptId}
	}
	sendState := func(st agentv1.RunAttemptState, stepID string, detail map[string]any) error {
		state.State = st
		state.HighestEventSeq++
		state.ProcessKnownRunning = st == agentv1.RunAttemptState_RUN_ATTEMPT_STATE_RUNNING
		_ = e.store.WriteState(*state)
		ev := &agentv1.RunEvent{
			RunId:         offer.RunId,
			AttemptId:     offer.AttemptId,
			StepId:        stepID,
			EventSequence: state.HighestEventSeq,
			Type:          agentv1.RunEventType_RUN_EVENT_TYPE_ATTEMPT_STATE_CHANGED,
			AttemptState:  st,
			OccurredAt:    timestamppb.Now(),
			Detail:        toStruct(detail),
		}
		_ = e.store.AppendRunEvent(ev)
		return e.sendFrame(sender, &agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunEvent{RunEvent: ev}}, "RunEvent", offer.RunId, offer.AttemptId)
	}
	if err := sendState(agentv1.RunAttemptState_RUN_ATTEMPT_STATE_PREPARING, "", nil); err != nil {
		final := agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED
		state.ErrorCode = "frame_send_failed"
		state.ErrorMessage = err.Error()
		state.State = final
		state.ProcessKnownRunning = false
		_ = e.store.WriteState(*state)
		e.sendTerminalFailure(sender, offer, state, final, 0, false)
		return
	}
	var final agentv1.RunAttemptState
	var exitCode int32
	var hasExit bool
	for _, step := range sortedSteps(offer.Plan.Steps) {
		if err := sendState(agentv1.RunAttemptState_RUN_ATTEMPT_STATE_RUNNING, step.StepId, map[string]any{"displayName": step.DisplayName}); err != nil {
			final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED
			state.ErrorCode = "frame_send_failed"
			state.ErrorMessage = err.Error()
			break
		}
		code, err := e.runStep(ctx, sender, offer, state, step)
		exitCode, hasExit = code, true
		if err != nil {
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT
				state.ErrorCode = "timed_out"
				state.ErrorMessage = err.Error()
				break
			} else if errors.Is(ctx.Err(), context.Canceled) {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED
				state.ErrorCode = "cancelled"
				state.ErrorMessage = err.Error()
				break
			}

			var exitErr *exec.ExitError
			if step.ContinueOnError && errors.As(err, &exitErr) {
				e.log.Info("ignoring non-zero process exit due to continue-on-error policy",
					"runId", offer.RunId,
					"attemptId", offer.AttemptId,
					"stepId", step.StepId,
					"exitCode", code)
				continue
			} else {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED
				state.ErrorCode = "process_failed"
				state.ErrorMessage = err.Error()
				if !step.ContinueOnError {
					break
				}
			}
		}
	}
	if final == agentv1.RunAttemptState_RUN_ATTEMPT_STATE_UNSPECIFIED {
		final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED
	}
	if err := sendState(final, "", nil); err != nil {
		final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED
		state.ErrorCode = "frame_send_failed"
		state.ErrorMessage = err.Error()
		state.State = final
		state.ProcessKnownRunning = false
	}
	state.ExitCode = exitCode
	state.HasExitCode = hasExit
	_ = e.store.WriteState(*state)
	e.sendTerminalFailure(sender, offer, state, final, exitCode, hasExit)
}

func (e *Engine) acquireAdmission(ctx context.Context) error {
	waitTimeout := e.cfg.AdmissionQueueWaitTimeout()
	timer := time.NewTimer(waitTimeout)
	defer timer.Stop()

	select {
	case e.admitted <- struct{}{}:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return errAdmissionQueueTimeout
	}
}

func (e *Engine) failAdmission(sender Sender, offer *agentv1.RunOffer, final agentv1.RunAttemptState, errorCode, msg string) {
	state, _ := e.store.ReadState(offer.RunId, offer.AttemptId)
	if state == nil {
		state = &runtimefs.State{RunID: offer.RunId, AttemptID: offer.AttemptId}
	}
	state.State = final
	state.ErrorCode = errorCode
	state.ErrorMessage = msg
	state.ProcessKnownRunning = false
	state.HighestEventSeq++
	_ = e.store.WriteState(*state)

	detail := map[string]any{
		"activeRuns":             e.ActiveRuns(),
		"queuedRuns":             e.QueuedRuns(),
		"rejectedByCapacity":     strconv.FormatUint(e.RejectedByCapacity(), 10),
		"maxConcurrentRuns":      e.cfg.Execution.MaxConcurrentRuns,
		"maxPendingQueueDepth":   e.cfg.Execution.MaxPendingQueueDepth,
		"queueWaitTimeoutSecond": e.cfg.Execution.AdmissionQueueWaitTimeoutSeconds,
	}
	ev := &agentv1.RunEvent{
		RunId:         offer.RunId,
		AttemptId:     offer.AttemptId,
		EventSequence: state.HighestEventSeq,
		Type:          agentv1.RunEventType_RUN_EVENT_TYPE_ATTEMPT_STATE_CHANGED,
		AttemptState:  final,
		OccurredAt:    timestamppb.Now(),
		Detail:        toStruct(detail),
	}
	_ = e.store.AppendRunEvent(ev)
	_ = e.sendFrame(sender, &agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunEvent{RunEvent: ev}}, "RunEvent", offer.RunId, offer.AttemptId)
	e.sendTerminalFailure(sender, offer, state, final, 0, false)
}

func (e *Engine) runStep(ctx context.Context, sender Sender, offer *agentv1.RunOffer, state *runtimefs.State, step *agentv1.RunStep) (int32, error) {
	params := step.Command.GetParameters()
	if step.Command.GetExecutorId() != "process.v1" {
		return 0, fmt.Errorf("executor %s is not implemented in this reference agent", step.Command.GetExecutorId())
	}
	exe := stringParam(params, "executable")
	if exe == "" {
		return 0, fmt.Errorf("process.v1 requires executable")
	}
	exe = resolveExecutable(exe)
	args := stringSliceParam(params, "arguments")
	wd := stringParam(params, "workingDirectory")
	if wd == "" {
		wd = e.store.WorkDir(offer.RunId, offer.AttemptId)
	} else if err := e.policy.ValidateWorkingDirectory(wd); err != nil {
		return 0, err
	}
	timeout := e.cfg.DefaultTimeout()
	if step.Timeout != nil {
		timeout = step.Timeout.AsDuration()
	}
	stepCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	command := exec.CommandContext(stepCtx, exe, args...)
	command.Dir = wd
	command.Env = append(command.Environ(), envParam(params)...)
	configureProcess(command)
	stdout, err := command.StdoutPipe()
	if err != nil {
		return 0, err
	}
	stderr, err := command.StderrPipe()
	if err != nil {
		return 0, err
	}
	if err := command.Start(); err != nil {
		return 0, err
	}
	done := make(chan struct{})
	go func() {
		select {
		case <-stepCtx.Done():
			terminateProcessTree(command)
		case <-done:
		}
	}()
	var wg sync.WaitGroup
	sendError := make(chan error, 1)
	reportSendError := func(err error) {
		select {
		case sendError <- err:
		default:
		}
		cancel()
	}
	wg.Add(2)
	go e.copyOutput(&wg, sender, offer, state, step.StepId, agentv1.OutputStream_OUTPUT_STREAM_STDOUT, stdout, reportSendError)
	go e.copyOutput(&wg, sender, offer, state, step.StepId, agentv1.OutputStream_OUTPUT_STREAM_STDERR, stderr, reportSendError)
	err = command.Wait()
	close(done)
	wg.Wait()
	exitCode := int32(0)
	if command.ProcessState != nil {
		exitCode = int32(command.ProcessState.ExitCode())
	}
	select {
	case sendErr := <-sendError:
		return exitCode, fmt.Errorf("failed to send run output: %w", sendErr)
	default:
	}
	if err != nil {
		return exitCode, err
	}
	return exitCode, nil
}

func (e *Engine) copyOutput(wg *sync.WaitGroup, sender Sender, offer *agentv1.RunOffer, state *runtimefs.State, stepID string, stream agentv1.OutputStream, reader io.Reader, reportSendError func(error)) {
	defer wg.Done()
	scanner := bufio.NewScanner(reader)
	buf := make([]byte, 64*1024)
	scanner.Buffer(buf, 1024*1024)
	for scanner.Scan() {
		data := append(scanner.Bytes(), '\n')
		if stream == agentv1.OutputStream_OUTPUT_STREAM_STDERR {
			state.StderrSequence++
		} else {
			state.StdoutSequence++
		}
		seq := state.StdoutSequence
		if stream == agentv1.OutputStream_OUTPUT_STREAM_STDERR {
			seq = state.StderrSequence
		}
		out := &agentv1.RunOutput{
			RunId:          offer.RunId,
			AttemptId:      offer.AttemptId,
			StepId:         stepID,
			Stream:         stream,
			OutputSequence: seq,
			Data:           data,
		}
		_ = e.store.AppendOutput(out)
		if err := e.sendFrame(sender, &agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunOutput{RunOutput: out}}, "RunOutput", offer.RunId, offer.AttemptId); err != nil {
			reportSendError(err)
			return
		}
	}
	if err := scanner.Err(); err != nil {
		e.log.Warn("failed to scan process output stream", "error", err, "runId", offer.RunId, "attemptId", offer.AttemptId, "stepId", stepID, "stream", stream.String())
	}
}

func (e *Engine) sendTerminalFailure(sender Sender, offer *agentv1.RunOffer, state *runtimefs.State, final agentv1.RunAttemptState, exitCode int32, hasExit bool) {
	completed := &agentv1.RunCompleted{
		RunId:              offer.RunId,
		AttemptId:          offer.AttemptId,
		PlanDigestSha256:   offer.PlanDigestSha256,
		TerminalState:      final,
		ExitCode:           exitCode,
		HasExitCode:        hasExit,
		CompletedAt:        timestamppb.Now(),
		FinalEventSequence: state.HighestEventSeq,
	}
	if final != agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED {
		completed.Error = tapError(state.ErrorCode, state.ErrorMessage, false)
	}
	if err := e.sendFrame(sender, &agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunCompleted{RunCompleted: completed}}, "RunCompleted", offer.RunId, offer.AttemptId); err != nil {
		e.log.Error("failed to send terminal run completion", "error", err, "runId", offer.RunId, "attemptId", offer.AttemptId)
	}
}

func (e *Engine) sendFrame(sender Sender, frame *agentv1.AgentFrame, frameType, runID, attemptID string) error {
	if err := sender.Send(frame); err != nil {
		e.log.Error("failed to send agent frame", "error", err, "frameType", frameType, "runId", runID, "attemptId", attemptID)
		return err
	}

	return nil
}

func sortedSteps(steps []*agentv1.RunStep) []*agentv1.RunStep {
	// The desktop sends an immutable plan. This preserves plan order for v1 while
	// still allowing the later graph scheduler to become stricter.
	return steps
}

func stringParam(s *structpb.Struct, name string) string {
	if s == nil || s.Fields[name] == nil {
		return ""
	}
	return s.Fields[name].GetStringValue()
}

func stringSliceParam(s *structpb.Struct, name string) []string {
	if s == nil || s.Fields[name] == nil {
		return nil
	}
	var out []string
	for _, v := range s.Fields[name].GetListValue().GetValues() {
		out = append(out, v.GetStringValue())
	}
	return out
}

func envParam(s *structpb.Struct) []string {
	if s == nil || s.Fields["environment"] == nil {
		return nil
	}
	var out []string
	for k, v := range s.Fields["environment"].GetStructValue().GetFields() {
		if strings.Contains(k, "=") {
			continue
		}
		out = append(out, k+"="+v.GetStringValue())
	}
	return out
}

func toStruct(v map[string]any) *structpb.Struct {
	if len(v) == 0 {
		return nil
	}
	s, err := structpb.NewStruct(v)
	if err != nil {
		return nil
	}
	return s
}

func tapError(code, msg string, retryable bool) *agentv1.TapError {
	if code == "" {
		code = "error"
	}
	return &agentv1.TapError{Code: code, Message: msg, Retryable: retryable}
}
