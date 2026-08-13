package schedule

import (
	"context"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"testing"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/execution"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/structpb"
	"google.golang.org/protobuf/types/known/timestamppb"
)

type captureSender struct {
	mu     sync.Mutex
	frames []*agentv1.AgentFrame
}

func (s *captureSender) Send(frame *agentv1.AgentFrame) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.frames = append(s.frames, proto.Clone(frame).(*agentv1.AgentFrame))
	return nil
}
func (s *captureSender) snapshot() []*agentv1.AgentFrame {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]*agentv1.AgentFrame(nil), s.frames...)
}

func TestUpsertPersistsPlanOneofsAndRejectsRevisionConflict(t *testing.T) {
	r, _, sender := newTestRuntime(t)
	req := testUpsert(t, "sched-1", 1, time.Now().UTC().Add(time.Hour))
	r.HandleUpsert(req)
	if got := sender.snapshot(); len(got) < 1 || got[0].GetScheduleAck() == nil {
		t.Fatalf("expected ScheduleAck, got %#v", got)
	}

	r2, err := New(r.cfg, r.engine, r.log)
	if err != nil {
		t.Fatalf("restart runtime: %v", err)
	}
	syncResponse := r2.Sync(&agentv1.ScheduleSyncRequest{SyncId: "sync"})
	if syncResponse.LocalRevisions["sched-1"] != 1 {
		t.Fatalf("restored revision = %d", syncResponse.LocalRevisions["sched-1"])
	}
	restored := r2.records["sched-1"].Spec.PayloadRunPlanSnapshot.Steps[0].Command.Parameters.Fields["arguments"].GetListValue().Values
	if len(restored) == 0 {
		t.Fatal("protobuf oneof command arguments were not restored")
	}

	conflict := proto.Clone(req).(*agentv1.ScheduleUpsert)
	conflict.IdempotencyKey = "different-operation"
	r2.SetSender(sender)
	r2.HandleUpsert(conflict)
	frames := sender.snapshot()
	if nack := frames[len(frames)-1].GetScheduleNack(); nack == nil || nack.ReasonCode != agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_REVISION_CONFLICT {
		t.Fatalf("expected revision conflict nack, got %#v", frames[len(frames)-1])
	}

	reusedKey := proto.Clone(req).(*agentv1.ScheduleUpsert)
	reusedKey.Spec.DisplayName = "different payload"
	r2.HandleUpsert(reusedKey)
	frames = sender.snapshot()
	if nack := frames[len(frames)-1].GetScheduleNack(); nack == nil || nack.ReasonCode != agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_REVISION_CONFLICT {
		t.Fatalf("expected payload mismatch revision conflict nack, got %#v", frames[len(frames)-1])
	}
}

func TestDeleteCreatesDurableTombstone(t *testing.T) {
	r, _, sender := newTestRuntime(t)
	r.HandleUpsert(testUpsert(t, "sched-delete", 1, time.Now().UTC().Add(time.Hour)))
	r.HandleDelete(&agentv1.ScheduleDelete{ScheduleId: "sched-delete", Revision: 2, IdempotencyKey: "delete-2"})
	restarted, err := New(r.cfg, r.engine, r.log)
	if err != nil {
		t.Fatal(err)
	}
	response := restarted.Sync(&agentv1.ScheduleSyncRequest{})
	if len(response.TombstonedScheduleIds) != 1 || response.TombstonedScheduleIds[0] != "sched-delete" {
		t.Fatalf("tombstones = %#v", response.TombstonedScheduleIds)
	}
	_ = sender
}

func TestPauseResumeAndRecurringCursor(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	trigger := time.Now().UTC().Add(-2 * time.Minute)
	req := testUpsert(t, "sched-repeat", 1, trigger)
	req.Spec.TriggerType = agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_RECURRING
	req.Spec.RecurrenceRule = "every:5m"
	req.Spec.TriggerWindowSeconds = 1
	r.HandleUpsert(req)
	r.HandlePause(&agentv1.SchedulePause{ScheduleId: "sched-repeat", Revision: 2, IdempotencyKey: "pause-2"})
	if r.records["sched-repeat"].State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_PAUSED {
		t.Fatal("schedule did not pause")
	}
	r.HandleResume(&agentv1.ScheduleResume{ScheduleId: "sched-repeat", Revision: 3, IdempotencyKey: "resume-3"})
	if r.records["sched-repeat"].State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE {
		t.Fatal("schedule did not resume")
	}
	now := time.Now().UTC()
	r.tick(context.Background(), now)
	if got := r.checkpoints["sched-repeat"].NextDueUTC; !got.Equal(trigger.Add(5 * time.Minute)) {
		t.Fatalf("next due = %s, want %s", got, trigger.Add(5*time.Minute))
	}
}

func TestUpsertWithChangedTimingResetsDueCursor(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	firstTrigger := time.Now().UTC().Add(time.Hour)
	first := testUpsert(t, "sched-retimed", 1, firstTrigger)
	first.Spec.TriggerType = agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_RECURRING
	first.Spec.RecurrenceRule = "every:5m"
	r.HandleUpsert(first)
	r.checkpoints["sched-retimed"].LastPlannedUTC = firstTrigger.Add(-5 * time.Minute)

	secondTrigger := firstTrigger.Add(30 * time.Minute)
	second := proto.Clone(first).(*agentv1.ScheduleUpsert)
	second.Identity.Revision = 2
	second.IdempotencyKey = "retimed-2"
	second.ExpectedPreviousRevision = proto.Uint64(1)
	second.Spec.TriggerAtUtc = timestamppb.New(secondTrigger)
	r.HandleUpsert(second)

	if got := r.checkpoints["sched-retimed"].NextDueUTC; !got.Equal(secondTrigger) {
		t.Fatalf("next due = %s, want changed trigger %s", got, secondTrigger)
	}
	if !r.checkpoints["sched-retimed"].LastPlannedUTC.IsZero() {
		t.Fatal("last planned cursor was not reset after a timing change")
	}
}

func TestDueOneShotRunsOnceAndTerminalEvidenceSurvivesRestart(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	planned := time.Now().UTC().Add(-100 * time.Millisecond)
	r.HandleUpsert(testUpsert(t, "sched-once", 1, planned))
	r.tick(context.Background(), time.Now().UTC())

	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if r.Metrics().OccurrenceTerminal[agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SUCCEEDED.String()] == 1 {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if got := r.Metrics().OccurrenceTerminal[agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SUCCEEDED.String()]; got != 1 {
		t.Fatalf("successful terminal count = %d", got)
	}

	restarted, err := New(r.cfg, r.engine, r.log)
	if err != nil {
		t.Fatal(err)
	}
	restarted.tick(context.Background(), time.Now().UTC().Add(time.Minute))
	time.Sleep(100 * time.Millisecond)
	events := restarted.Sync(&agentv1.ScheduleSyncRequest{}).OccurrenceEvents
	terminal := 0
	for _, event := range events {
		if event.State == agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_TERMINAL {
			terminal++
		}
	}
	if terminal != 1 {
		t.Fatalf("terminal occurrence events after restart = %d, want 1", terminal)
	}
}

func TestMutationStorageFailureRollsBackMemoryAndDisk(t *testing.T) {
	r, _, sender := newTestRuntime(t)
	r.HandleUpsert(testUpsert(t, "sched-rollback", 1, time.Now().UTC().Add(time.Hour)))
	blockFilePath(t, r.checkpointPath())

	r.HandlePause(&agentv1.SchedulePause{ScheduleId: "sched-rollback", Revision: 2, IdempotencyKey: "pause-2"})

	record := r.records["sched-rollback"]
	if record.Identity.Revision != 1 || record.State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE {
		t.Fatalf("in-memory schedule changed after failed persistence: revision=%d state=%s", record.Identity.Revision, record.State)
	}
	frames := sender.snapshot()
	if nack := frames[len(frames)-1].GetScheduleNack(); nack == nil || nack.ReasonCode != agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_STORAGE_FAILURE {
		t.Fatalf("expected storage failure nack, got %#v", frames[len(frames)-1])
	}

	unblockFilePath(t, r.checkpointPath())
	restarted, err := New(r.cfg, r.engine, r.log)
	if err != nil {
		t.Fatal(err)
	}
	restored := restarted.records["sched-rollback"]
	if restored.Identity.Revision != 1 || restored.State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE {
		t.Fatalf("durable schedule changed after failed persistence: revision=%d state=%s", restored.Identity.Revision, restored.State)
	}
}

func TestOccurrenceLogFailureRestoresDueCursorForRetry(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	planned := time.Now().UTC().Add(-100 * time.Millisecond)
	r.HandleUpsert(testUpsert(t, "sched-log-retry", 1, planned))
	blockFilePath(t, r.occurrencePath())

	r.tick(context.Background(), time.Now().UTC())

	if record := r.records["sched-log-retry"]; record.State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE || !record.Spec.Enabled {
		t.Fatalf("one-shot schedule was not restored after occurrence-log failure: state=%s enabled=%v", record.State, record.Spec.Enabled)
	}
	if got := r.checkpoints["sched-log-retry"].NextDueUTC; !got.Equal(planned) {
		t.Fatalf("restored next due = %s, want %s", got, planned)
	}
	if r.engine.ActiveRuns() != 0 {
		t.Fatal("run was admitted without durable planned occurrence evidence")
	}

	unblockFilePath(t, r.occurrencePath())
	r.tick(context.Background(), time.Now().UTC())
	waitForTerminalCount(t, r, agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SUCCEEDED, 1)
}

func TestRecurringCatchUpCollapsesToLatestDueOccurrence(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	now := time.Now().UTC()
	req := testUpsert(t, "sched-catchup", 1, now.Add(-22*time.Minute))
	req.Spec.TriggerType = agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_RECURRING
	req.Spec.RecurrenceRule = "every:5m"
	r.HandleUpsert(req)

	r.tick(context.Background(), now)

	nextDue := r.checkpoints["sched-catchup"].NextDueUTC
	if !nextDue.After(now) || nextDue.After(now.Add(5*time.Minute)) {
		t.Fatalf("next due = %s; expected first interval after now", nextDue)
	}
}

func TestUnsupportedSchedulePoliciesReturnUnsupportedFeature(t *testing.T) {
	r, _, sender := newTestRuntime(t)
	req := testUpsert(t, "sched-unsupported", 1, time.Now().UTC().Add(time.Hour))
	req.Spec.MaxCatchUpOccurrences = 2

	r.HandleUpsert(req)

	frames := sender.snapshot()
	if nack := frames[len(frames)-1].GetScheduleNack(); nack == nil || nack.ReasonCode != agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_UNSUPPORTED_FEATURE {
		t.Fatalf("expected unsupported feature nack, got %#v", frames[len(frames)-1])
	}
}

func TestSyncClampsAcknowledgementAndOverlapTrackingKeepsOtherRuns(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	r.nextSeq = 2
	r.Sync(&agentv1.ScheduleSyncRequest{AcknowledgedOccurrenceSequence: 999})
	if r.ackedSeq != 2 {
		t.Fatalf("acked sequence = %d, want 2", r.ackedSeq)
	}

	r.running["sched-overlap"] = map[string]struct{}{"occurrence-1": {}, "occurrence-2": {}}
	if err := r.recordTerminal("sched-overlap", "occurrence-1", time.Now().UTC(), "run-1", agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SUCCEEDED, nil, "", ""); err != nil {
		t.Fatal(err)
	}
	if len(r.running["sched-overlap"]) != 1 {
		t.Fatalf("running occurrences = %#v; terminal completion removed another overlapping run", r.running["sched-overlap"])
	}
}

func TestTerminalPersistenceFailureRetriesOnNextTick(t *testing.T) {
	r, _, _ := newTestRuntime(t)
	r.running["sched-pending-terminal"] = map[string]struct{}{"occurrence-pending": {}}
	blockFilePath(t, r.occurrencePath())

	err := r.recordTerminal("sched-pending-terminal", "occurrence-pending", time.Now().UTC(), "run-pending", agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_FAILED, nil, "TEST", "pending")
	if err == nil || r.pendingTerminals["occurrence-pending"] == nil {
		t.Fatal("failed terminal occurrence was not retained for retry")
	}

	unblockFilePath(t, r.occurrencePath())
	r.tick(context.Background(), time.Now().UTC())
	if _, exists := r.pendingTerminals["occurrence-pending"]; exists {
		t.Fatal("pending terminal was not flushed after storage recovered")
	}
	if _, exists := r.terminalIDs["occurrence-pending"]; !exists {
		t.Fatal("retried terminal occurrence was not committed")
	}
}

func waitForTerminalCount(t *testing.T, r *Runtime, outcome agentv1.ScheduleOccurrenceOutcome, want uint64) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if r.Metrics().OccurrenceTerminal[outcome.String()] == want {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatalf("terminal count for %s did not reach %d", outcome, want)
}

func blockFilePath(t *testing.T, path string) {
	t.Helper()
	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		t.Fatal(err)
	}
	if err := os.Mkdir(path, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(path, "blocker"), []byte("block"), 0o600); err != nil {
		t.Fatal(err)
	}
}

func unblockFilePath(t *testing.T, path string) {
	t.Helper()
	if err := os.RemoveAll(path); err != nil {
		t.Fatal(err)
	}
}

func newTestRuntime(t *testing.T) (*Runtime, *execution.Engine, *captureSender) {
	t.Helper()
	cfg := config.Defaults()
	cfg.Runtime.Root = t.TempDir()
	cfg.Execution.MaxConcurrentRuns = 1
	cfg.Execution.MaxPendingQueueDepth = 2
	cfg.Execution.AdmissionQueueWaitTimeoutSeconds = 2
	cfg.Execution.AllowedWorkingRoots = nil
	store := runtimefs.New(cfg.Runtime.Root)
	if err := store.Init(); err != nil {
		t.Fatal(err)
	}
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	engine := execution.New(cfg, store, logger)
	r, err := New(cfg, engine, logger)
	if err != nil {
		t.Fatal(err)
	}
	sender := &captureSender{}
	r.SetSender(sender)
	return r, engine, sender
}

func testUpsert(t *testing.T, id string, revision uint64, trigger time.Time) *agentv1.ScheduleUpsert {
	t.Helper()
	executable, arguments := "sh", []any{"-c", "true"}
	if runtime.GOOS == "windows" {
		executable, arguments = "cmd.exe", []any{"/c", "exit", "0"}
	}
	params, err := structpb.NewStruct(map[string]any{"executable": executable, "arguments": arguments})
	if err != nil {
		t.Fatal(err)
	}
	plan := &agentv1.RunPlan{SchemaVersion: 1, Source: &agentv1.RunSource{Kind: "task", SourceId: id, Revision: revision}, Steps: []*agentv1.RunStep{{StepId: "step-1", DisplayName: "test", Command: &agentv1.ExecutorCommand{ExecutorId: "process.v1", ExecutorVersion: "1", Parameters: params}}}}
	return &agentv1.ScheduleUpsert{Identity: &agentv1.ScheduleIdentity{ScheduleId: id, Revision: revision, SourceDesktopInstanceId: "desktop-test"}, Spec: &agentv1.ScheduleSpec{ScheduleId: id, DisplayName: id, Enabled: true, TriggerType: agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_ONE_SHOT, TriggerAtUtc: timestamppb.New(trigger), TriggerWindowSeconds: 60, PayloadRunPlanSnapshot: plan, MisfirePolicy: agentv1.ScheduleMisfirePolicy_SCHEDULE_MISFIRE_POLICY_FIRE_ONCE_WITHIN_WINDOW, OverlapPolicy: agentv1.ScheduleOverlapPolicy_SCHEDULE_OVERLAP_POLICY_SKIP_IF_RUNNING, MaxCatchUpOccurrences: 1, CreatedAtUtc: timestamppb.Now(), UpdatedAtUtc: timestamppb.Now()}, IdempotencyKey: id + "-operation"}
}
