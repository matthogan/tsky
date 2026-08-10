package execution

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	tapproto "github.com/matthogan/tsky/agent/internal/protocol"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/types/known/structpb"
	"google.golang.org/protobuf/types/known/timestamppb"
)

const (
	helperEnvEnabled     = "TSKY_TEST_HELPER_PROCESS"
	helperEnvReleaseFile = "TSKY_TEST_RELEASE_FILE"
)

func TestEngine_ConcurrentOffersRunInParallelUpToConfiguredLimit(t *testing.T) {
	const (
		totalOffers    = 4
		maxConcurrent  = 3
		conditionWait  = 5 * time.Second
		completionWait = 10 * time.Second
	)

	runtimeRoot := t.TempDir()
	releaseFile := filepath.Join(runtimeRoot, "release.signal")

	cfg := config.Defaults()
	cfg.Runtime.Root = runtimeRoot
	cfg.Execution.MaxConcurrentRuns = maxConcurrent
	cfg.Execution.AllowedWorkingRoots = []string{runtimeRoot}

	store := runtimefs.New(runtimeRoot)
	if err := store.Init(); err != nil {
		t.Fatalf("initialize runtime store: %v", err)
	}

	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	engine := New(cfg, store, log)
	sender := newFrameRecorder(totalOffers)

	exePath, err := os.Executable()
	if err != nil {
		t.Fatalf("resolve current executable: %v", err)
	}

	start := make(chan struct{})
	var submitWG sync.WaitGroup
	for i := 0; i < totalOffers; i++ {
		offer := buildBlockingOffer(t, exePath, runtimeRoot, releaseFile, i)
		submitWG.Add(1)
		go func(of *agentv1.RunOffer) {
			defer submitWG.Done()
			<-start
			engine.HandleOffer(context.Background(), sender, of)
		}(offer)
	}
	close(start)
	submitWG.Wait()

	if err := waitForStable(conditionWait, 300*time.Millisecond, func() bool {
		return int(engine.ActiveRuns()) == maxConcurrent
	}); err != nil {
		t.Fatalf("active runs did not remain at configured concurrency %d: %v", maxConcurrent, err)
	}

	if err := os.WriteFile(releaseFile, []byte("release\n"), 0o600); err != nil {
		t.Fatalf("write release marker: %v", err)
	}

	for i := 0; i < totalOffers; i++ {
		select {
		case completed := <-sender.completed:
			if completed.GetTerminalState() != agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED {
				t.Fatalf("attempt %s finished in state %s (error=%v)", completed.GetAttemptId(), completed.GetTerminalState().String(), completed.GetError())
			}
		case rejected := <-sender.rejected:
			t.Fatalf("offer unexpectedly rejected for attempt %s: %v", rejected.GetAttemptId(), rejected.GetError())
		case <-time.After(completionWait):
			t.Fatalf("timed out waiting for attempt completion (%d/%d received)", i, totalOffers)
		}
	}
}

func TestEngine_BoundedPendingQueueTimesOutAndTracksCapacityRejection(t *testing.T) {
	const (
		totalOffers      = 3
		maxConcurrent    = 1
		maxPendingQueue  = 1
		completionWait   = 6 * time.Second
		timeoutStateWait = 4 * time.Second
	)

	runtimeRoot := t.TempDir()
	releaseFile := filepath.Join(runtimeRoot, "release-timeout.signal")

	cfg := config.Defaults()
	cfg.Runtime.Root = runtimeRoot
	cfg.Execution.MaxConcurrentRuns = maxConcurrent
	cfg.Execution.MaxPendingQueueDepth = maxPendingQueue
	cfg.Execution.AdmissionQueueWaitTimeoutSeconds = 1
	cfg.Execution.AllowedWorkingRoots = []string{runtimeRoot}

	store := runtimefs.New(runtimeRoot)
	if err := store.Init(); err != nil {
		t.Fatalf("initialize runtime store: %v", err)
	}

	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	engine := New(cfg, store, log)
	sender := newFrameRecorder(totalOffers)

	exePath, err := os.Executable()
	if err != nil {
		t.Fatalf("resolve current executable: %v", err)
	}

	for i := 0; i < totalOffers; i++ {
		offer := buildBlockingOffer(t, exePath, runtimeRoot, releaseFile, i+100)
		go engine.HandleOffer(context.Background(), sender, offer)
	}

	var timedOutAttempt string
	select {
	case completed := <-sender.completed:
		if completed.GetTerminalState() != agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT {
			t.Fatalf("expected one timed-out attempt due to full admission queue, got %s", completed.GetTerminalState().String())
		}
		timedOutAttempt = completed.GetAttemptId()
	case <-time.After(timeoutStateWait):
		t.Fatal("timed out waiting for admission-timeout attempt")
	}

	if timedOutAttempt == "" {
		t.Fatal("expected a timed-out attempt id")
	}

	if got := engine.RejectedByCapacity(); got != 1 {
		t.Fatalf("expected rejected-by-capacity metric to be 1, got %d", got)
	}

	if err := os.WriteFile(releaseFile, []byte("release\n"), 0o600); err != nil {
		t.Fatalf("write release marker: %v", err)
	}

	succeeded := 0
	for succeeded < 2 {
		select {
		case completed := <-sender.completed:
			if completed.GetTerminalState() == agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT {
				continue
			}
			if completed.GetTerminalState() != agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED {
				t.Fatalf("expected non-timed-out attempts to succeed, got %s", completed.GetTerminalState().String())
			}
			succeeded++
		case rejected := <-sender.rejected:
			t.Fatalf("unexpected RunRejected for attempt %s: %v", rejected.GetAttemptId(), rejected.GetError())
		case <-time.After(completionWait):
			t.Fatalf("timed out waiting for successful attempts (%d/2)", succeeded)
		}
	}
}

func TestEngineConcurrencyHelperProcess(t *testing.T) {
	if os.Getenv(helperEnvEnabled) != "1" {
		t.Skip("helper subprocess")
	}

	releaseFile := os.Getenv(helperEnvReleaseFile)
	if releaseFile == "" {
		t.Fatalf("helper env is missing required paths")
	}

	deadline := time.Now().Add(10 * time.Second)
	for {
		if _, err := os.Stat(releaseFile); err == nil {
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for release file %q", releaseFile)
		}
		time.Sleep(10 * time.Millisecond)
	}
}

type frameRecorder struct {
	completed chan *agentv1.RunCompleted
	rejected  chan *agentv1.RunRejected
}

func newFrameRecorder(expected int) *frameRecorder {
	return &frameRecorder{
		completed: make(chan *agentv1.RunCompleted, expected),
		rejected:  make(chan *agentv1.RunRejected, expected),
	}
}

func (r *frameRecorder) Send(frame *agentv1.AgentFrame) error {
	switch body := frame.GetBody().(type) {
	case *agentv1.AgentFrame_RunCompleted:
		r.completed <- body.RunCompleted
	case *agentv1.AgentFrame_RunRejected:
		r.rejected <- body.RunRejected
	}
	return nil
}

func buildBlockingOffer(t *testing.T, executable, workingDirectory, releaseFile string, idx int) *agentv1.RunOffer {
	t.Helper()

	params, err := structpb.NewStruct(map[string]any{
		"executable":       executable,
		"arguments":        []any{"-test.run=TestEngineConcurrencyHelperProcess"},
		"workingDirectory": workingDirectory,
		"environment": map[string]any{
			helperEnvEnabled:     "1",
			helperEnvReleaseFile: releaseFile,
		},
	})
	if err != nil {
		t.Fatalf("build executor parameters: %v", err)
	}

	plan := &agentv1.RunPlan{
		SchemaVersion: 1,
		Steps: []*agentv1.RunStep{{
			StepId:          "step-1",
			DisplayName:     "concurrency-helper",
			Command:         &agentv1.ExecutorCommand{ExecutorId: "process.v1", ExecutorVersion: "1", Parameters: params},
			ContinueOnError: false,
		}},
	}
	digest, err := tapproto.DigestPlan(plan)
	if err != nil {
		t.Fatalf("digest run plan: %v", err)
	}

	runID := fmt.Sprintf("run-%d", idx)
	attemptID := fmt.Sprintf("attempt-%d", idx)
	offerID := fmt.Sprintf("offer-%d", idx)

	return &agentv1.RunOffer{
		OfferId:          offerID,
		RunId:            runID,
		AttemptId:        attemptID,
		OfferExpiresAt:   timestamppb.New(time.Now().Add(2 * time.Minute)),
		PlanDigestSha256: digest,
		Plan:             plan,
	}
}

func waitFor(timeout time.Duration, condition func() bool) error {
	deadline := time.Now().Add(timeout)
	for {
		if condition() {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("condition was not met within %s", timeout)
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func waitForStable(timeout, stableFor time.Duration, condition func() bool) error {
	deadline := time.Now().Add(timeout)
	stableSince := time.Time{}
	for {
		now := time.Now()
		if condition() {
			if stableSince.IsZero() {
				stableSince = now
			}
			if now.Sub(stableSince) >= stableFor {
				return nil
			}
		} else {
			stableSince = time.Time{}
		}
		if now.After(deadline) {
			return fmt.Errorf("condition did not remain true for %s within %s", stableFor, timeout)
		}
		time.Sleep(10 * time.Millisecond)
	}
}
