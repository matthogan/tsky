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
	"strings"
	"sync"
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
	cfg     config.Config
	store   *runtimefs.Store
	policy  policy.Policy
	log     *slog.Logger
	mu      sync.Mutex
	active  map[string]context.CancelFunc
	running chan struct{}
}

func New(cfg config.Config, store *runtimefs.Store, log *slog.Logger) *Engine {
	return &Engine{
		cfg:     cfg,
		store:   store,
		policy:  policy.New(cfg),
		log:     log,
		active:  map[string]context.CancelFunc{},
		running: make(chan struct{}, cfg.Execution.MaxConcurrentRuns),
	}
}

func (e *Engine) HandleOffer(ctx context.Context, sender Sender, offer *agentv1.RunOffer) {
	acceptance, duplicate, err := e.accept(offer)
	if err != nil {
		_ = sender.Send(&agentv1.AgentFrame{
			Header: tapproto.Header(offer.OfferId),
			Body: &agentv1.AgentFrame_RunRejected{RunRejected: &agentv1.RunRejected{
				OfferId:   offer.OfferId,
				RunId:     offer.RunId,
				AttemptId: offer.AttemptId,
				Error:     tapError("run_rejected", err.Error(), false),
			}},
		})
		return
	}
	_ = sender.Send(&agentv1.AgentFrame{
		Header: tapproto.Header(offer.OfferId),
		Body: &agentv1.AgentFrame_RunAccepted{RunAccepted: &agentv1.RunAccepted{
			OfferId:          acceptance.OfferID,
			RunId:            acceptance.RunID,
			AttemptId:        acceptance.AttemptID,
			PlanDigestSha256: offer.PlanDigestSha256,
			AcceptedAt:       timestamppb.New(acceptance.AcceptedAt),
		}},
	})
	if duplicate {
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
	for _, step := range offer.Plan.Steps {
		if !e.policy.ExecutorAllowed(step.Command.GetExecutorId()) {
			return nil, false, fmt.Errorf("executor %q is not allowed", step.Command.GetExecutorId())
		}
		params := step.Command.GetParameters()
		if params != nil {
			if wd := stringParam(params, "workingDirectory"); wd != "" {
				if err := e.policy.ValidateWorkingDirectory(wd); err != nil {
					return nil, false, err
				}
			}
		}
	}
	return e.store.Accept(offer)
}

func (e *Engine) execute(parent context.Context, sender Sender, offer *agentv1.RunOffer) {
	e.running <- struct{}{}
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
	sendState := func(st agentv1.RunAttemptState, stepID string, detail map[string]any) {
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
		_ = sender.Send(&agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunEvent{RunEvent: ev}})
	}
	sendState(agentv1.RunAttemptState_RUN_ATTEMPT_STATE_PREPARING, "", nil)
	var final agentv1.RunAttemptState
	var exitCode int32
	var hasExit bool
	for _, step := range sortedSteps(offer.Plan.Steps) {
		sendState(agentv1.RunAttemptState_RUN_ATTEMPT_STATE_RUNNING, step.StepId, map[string]any{"displayName": step.DisplayName})
		code, err := e.runStep(ctx, sender, offer, state, step)
		exitCode, hasExit = code, true
		if err != nil {
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT
			} else if errors.Is(ctx.Err(), context.Canceled) {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED
			} else {
				final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_FAILED
			}
			state.ErrorCode = "process_failed"
			state.ErrorMessage = err.Error()
			if !step.ContinueOnError {
				break
			}
		}
	}
	if final == agentv1.RunAttemptState_RUN_ATTEMPT_STATE_UNSPECIFIED {
		final = agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED
	}
	sendState(final, "", nil)
	state.ExitCode = exitCode
	state.HasExitCode = hasExit
	_ = e.store.WriteState(*state)
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
	_ = sender.Send(&agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunCompleted{RunCompleted: completed}})
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
	wg.Add(2)
	go e.copyOutput(&wg, sender, offer, state, step.StepId, agentv1.OutputStream_OUTPUT_STREAM_STDOUT, stdout)
	go e.copyOutput(&wg, sender, offer, state, step.StepId, agentv1.OutputStream_OUTPUT_STREAM_STDERR, stderr)
	err = command.Wait()
	close(done)
	wg.Wait()
	exitCode := int32(0)
	if command.ProcessState != nil {
		exitCode = int32(command.ProcessState.ExitCode())
	}
	if err != nil {
		return exitCode, err
	}
	return exitCode, nil
}

func (e *Engine) copyOutput(wg *sync.WaitGroup, sender Sender, offer *agentv1.RunOffer, state *runtimefs.State, stepID string, stream agentv1.OutputStream, reader io.Reader) {
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
		_ = sender.Send(&agentv1.AgentFrame{Header: tapproto.Header(offer.AttemptId), Body: &agentv1.AgentFrame_RunOutput{RunOutput: out}})
	}
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
