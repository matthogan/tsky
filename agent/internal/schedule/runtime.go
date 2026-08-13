package schedule

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/execution"
	tapproto "github.com/matthogan/tsky/agent/internal/protocol"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"
)

const tickInterval = time.Second

var everyRule = regexp.MustCompile(`^every:([1-9][0-9]*)m$`)

type Sender interface {
	Send(*agentv1.AgentFrame) error
}

type Metrics struct {
	ActiveCount            uint32
	PausedCount            uint32
	OccurrenceBacklogCount uint64
	ReplayQueueDepth       uint64
	LastTickUTC            time.Time
	UpsertAcceptedTotal    uint64
	UpsertRejectedTotal    uint64
	OccurrenceStartedTotal uint64
	OccurrenceTerminal     map[string]uint64
}

type persistedRegistry struct {
	SchemaVersion int               `json:"schemaVersion"`
	Schedules     []*scheduleRecord `json:"schedules"`
	Tombstones    map[string]uint64 `json:"tombstones,omitempty"`
}

type scheduleRecord struct {
	Identity       *agentv1.ScheduleIdentity       `json:"identity"`
	Spec           *agentv1.ScheduleSpec           `json:"spec"`
	State          agentv1.ScheduleDefinitionState `json:"state"`
	IdempotencyKey string                          `json:"idempotencyKey,omitempty"`
}

// Protobuf binary payloads preserve oneof values in RunPlan across restarts;
// encoding/json cannot reconstruct generated protobuf oneof interfaces.
func (r *scheduleRecord) MarshalJSON() ([]byte, error) {
	identity, err := proto.Marshal(r.Identity)
	if err != nil {
		return nil, err
	}
	spec, err := proto.Marshal(r.Spec)
	if err != nil {
		return nil, err
	}
	return json.Marshal(struct {
		Identity       []byte                          `json:"identityProto"`
		Spec           []byte                          `json:"specProto"`
		State          agentv1.ScheduleDefinitionState `json:"state"`
		IdempotencyKey string                          `json:"idempotencyKey,omitempty"`
	}{identity, spec, r.State, r.IdempotencyKey})
}

func (r *scheduleRecord) UnmarshalJSON(data []byte) error {
	var wire struct {
		Identity       []byte                          `json:"identityProto"`
		Spec           []byte                          `json:"specProto"`
		State          agentv1.ScheduleDefinitionState `json:"state"`
		IdempotencyKey string                          `json:"idempotencyKey,omitempty"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	r.Identity, r.Spec = &agentv1.ScheduleIdentity{}, &agentv1.ScheduleSpec{}
	if err := proto.Unmarshal(wire.Identity, r.Identity); err != nil {
		return err
	}
	if err := proto.Unmarshal(wire.Spec, r.Spec); err != nil {
		return err
	}
	r.State, r.IdempotencyKey = wire.State, wire.IdempotencyKey
	return nil
}

type checkpointFile struct {
	SchemaVersion int                    `json:"schemaVersion"`
	NextSequence  uint64                 `json:"nextSequence"`
	Schedules     map[string]*checkpoint `json:"schedules"`
}

type checkpoint struct {
	NextDueUTC        time.Time `json:"nextDueUtc"`
	LastPlannedUTC    time.Time `json:"lastPlannedUtc,omitempty"`
	LastTerminalID    string    `json:"lastTerminalOccurrenceId,omitempty"`
	LastOccurrenceSeq uint64    `json:"lastOccurrenceSequence"`
}

type loggedOccurrence struct {
	Sequence             uint64                            `json:"sequence"`
	ScheduleID           string                            `json:"scheduleId"`
	OccurrenceID         string                            `json:"occurrenceId"`
	PlannedOccurrenceUTC time.Time                         `json:"plannedOccurrenceUtc"`
	AdmittedAtUTC        *time.Time                        `json:"admittedAtUtc,omitempty"`
	StartedAtUTC         *time.Time                        `json:"startedAtUtc,omitempty"`
	CompletedAtUTC       *time.Time                        `json:"completedAtUtc,omitempty"`
	State                agentv1.ScheduleOccurrenceState   `json:"state"`
	Outcome              agentv1.ScheduleOccurrenceOutcome `json:"outcome,omitempty"`
	RunID                string                            `json:"runId,omitempty"`
	ExitCode             *int32                            `json:"exitCode,omitempty"`
	Message              string                            `json:"message,omitempty"`
	ReasonCode           string                            `json:"reasonCode,omitempty"`
}

type Runtime struct {
	cfg              config.Config
	engine           *execution.Engine
	log              *slog.Logger
	root             string
	sender           Sender
	mu               sync.Mutex
	records          map[string]*scheduleRecord
	tombstones       map[string]uint64
	checkpoints      map[string]*checkpoint
	terminalIDs      map[string]struct{}
	pendingTerminals map[string]*loggedOccurrence
	running          map[string]map[string]struct{}
	nextSeq          uint64
	ackedSeq         uint64
	lastTick         time.Time
	upsertOK         uint64
	upsertNack       uint64
	started          uint64
	terminal         map[agentv1.ScheduleOccurrenceOutcome]uint64
}

func New(cfg config.Config, engine *execution.Engine, log *slog.Logger) (*Runtime, error) {
	r := &Runtime{
		cfg: cfg, engine: engine, log: log, root: cfg.Runtime.Root,
		records: map[string]*scheduleRecord{}, tombstones: map[string]uint64{},
		checkpoints: map[string]*checkpoint{}, terminalIDs: map[string]struct{}{}, pendingTerminals: map[string]*loggedOccurrence{},
		running: map[string]map[string]struct{}{}, terminal: map[agentv1.ScheduleOccurrenceOutcome]uint64{},
	}
	if err := r.load(); err != nil {
		return nil, err
	}
	return r, nil
}

func (r *Runtime) SetSender(sender Sender) { r.mu.Lock(); r.sender = sender; r.mu.Unlock() }

func (r *Runtime) Run(ctx context.Context) {
	ticker := time.NewTicker(tickInterval)
	defer ticker.Stop()
	r.tick(ctx, time.Now().UTC())
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			r.tick(ctx, now.UTC())
		}
	}
}

func (r *Runtime) HandleUpsert(upsert *agentv1.ScheduleUpsert) {
	if err := r.upsert(upsert); err != nil {
		atomic.AddUint64(&r.upsertNack, 1)
		r.sendNack(scheduleIDFromUpsert(upsert), revisionFromUpsert(upsert), classifyNack(err), err.Error(), errors.Is(err, errStorage))
		return
	}
	atomic.AddUint64(&r.upsertOK, 1)
	id := upsert.Identity.ScheduleId
	checksum, _ := deterministicChecksum(upsert.Spec)
	r.send(&agentv1.AgentFrame{Header: tapproto.Header(id), Body: &agentv1.AgentFrame_ScheduleAck{ScheduleAck: &agentv1.ScheduleAck{
		ScheduleId: id, AcceptedRevision: upsert.Identity.Revision, PersistenceChecksumSha256: checksum, AcceptedAtUtc: timestamppb.Now(),
	}}})
	r.sendStatus(id)
}

func (r *Runtime) HandleDelete(req *agentv1.ScheduleDelete) {
	if req == nil || strings.TrimSpace(req.ScheduleId) == "" || req.Revision == 0 {
		r.sendNack(scheduleIDFromDelete(req), revisionFromDelete(req), agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_INVALID_SPEC, "schedule delete requires schedule_id and revision", false)
		return
	}
	if err := r.mutate(req.ScheduleId, req.Revision, req.IdempotencyKey, agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_TOMBSTONED); err != nil {
		r.sendNack(req.ScheduleId, req.Revision, classifyNack(err), err.Error(), errors.Is(err, errStorage))
		return
	}
	r.sendAck(req.ScheduleId, req.Revision)
}

func (r *Runtime) HandlePause(req *agentv1.SchedulePause) {
	if req == nil {
		r.sendNack("", 0, agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_INVALID_SPEC, "schedule pause is missing", false)
		return
	}
	if err := r.mutate(req.ScheduleId, req.Revision, req.IdempotencyKey, agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_PAUSED); err != nil {
		r.sendNack(req.ScheduleId, req.Revision, classifyNack(err), err.Error(), errors.Is(err, errStorage))
		return
	}
	r.sendAck(req.ScheduleId, req.Revision)
	r.sendStatus(req.ScheduleId)
}

func (r *Runtime) HandleResume(req *agentv1.ScheduleResume) {
	if req == nil {
		r.sendNack("", 0, agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_INVALID_SPEC, "schedule resume is missing", false)
		return
	}
	if err := r.mutate(req.ScheduleId, req.Revision, req.IdempotencyKey, agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE); err != nil {
		r.sendNack(req.ScheduleId, req.Revision, classifyNack(err), err.Error(), errors.Is(err, errStorage))
		return
	}
	r.sendAck(req.ScheduleId, req.Revision)
	r.sendStatus(req.ScheduleId)
}

func (r *Runtime) Sync(req *agentv1.ScheduleSyncRequest) *agentv1.ScheduleSyncResponse {
	r.mu.Lock()
	defer r.mu.Unlock()
	if req != nil && req.AcknowledgedOccurrenceSequence > r.ackedSeq {
		r.ackedSeq = min(req.AcknowledgedOccurrenceSequence, r.nextSeq)
	}
	resp := &agentv1.ScheduleSyncResponse{LocalRevisions: map[string]uint64{}, TombstonedRevisions: map[string]uint64{}, LatestOccurrenceSequence: r.nextSeq}
	if req != nil {
		resp.SyncId = req.SyncId
	}
	for id, record := range r.records {
		resp.LocalRevisions[id] = record.Identity.Revision
	}
	for id, revision := range r.tombstones {
		resp.TombstonedScheduleIds = append(resp.TombstonedScheduleIds, id)
		resp.TombstonedRevisions[id] = revision
	}
	sort.Strings(resp.TombstonedScheduleIds)
	events, err := r.readOccurrencesAfter(r.ackedSeq)
	if err == nil {
		resp.OccurrenceEvents = events
	}
	return resp
}

func (r *Runtime) Metrics() Metrics {
	r.mu.Lock()
	defer r.mu.Unlock()
	m := Metrics{LastTickUTC: r.lastTick, UpsertAcceptedTotal: atomic.LoadUint64(&r.upsertOK), UpsertRejectedTotal: atomic.LoadUint64(&r.upsertNack), OccurrenceStartedTotal: atomic.LoadUint64(&r.started), OccurrenceTerminal: map[string]uint64{}}
	for _, rec := range r.records {
		if rec.State == agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE {
			m.ActiveCount++
		} else if rec.State == agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_PAUSED {
			m.PausedCount++
		}
	}
	if r.nextSeq > r.ackedSeq {
		m.OccurrenceBacklogCount = r.nextSeq - r.ackedSeq
		m.ReplayQueueDepth = m.OccurrenceBacklogCount
	}
	for outcome, count := range r.terminal {
		m.OccurrenceTerminal[outcome.String()] = count
	}
	return m
}

var errStorage = errors.New("schedule storage failure")
var errRevision = errors.New("schedule revision conflict")
var errAuthorization = errors.New("schedule source desktop is not authorized")
var errUnsupported = errors.New("schedule feature is not supported")

type runtimeStateSnapshot struct {
	records     map[string]*scheduleRecord
	tombstones  map[string]uint64
	checkpoints map[string]*checkpoint
}

func (r *Runtime) snapshotLocked() runtimeStateSnapshot {
	snapshot := runtimeStateSnapshot{
		records:     make(map[string]*scheduleRecord, len(r.records)),
		tombstones:  make(map[string]uint64, len(r.tombstones)),
		checkpoints: make(map[string]*checkpoint, len(r.checkpoints)),
	}
	for id, record := range r.records {
		snapshot.records[id] = cloneRecord(record)
	}
	for id, revision := range r.tombstones {
		snapshot.tombstones[id] = revision
	}
	for id, value := range r.checkpoints {
		clone := *value
		snapshot.checkpoints[id] = &clone
	}
	return snapshot
}

func (r *Runtime) restoreLocked(snapshot runtimeStateSnapshot) {
	r.records = snapshot.records
	r.tombstones = snapshot.tombstones
	r.checkpoints = snapshot.checkpoints
}

func cloneRecord(record *scheduleRecord) *scheduleRecord {
	if record == nil {
		return nil
	}
	clone := &scheduleRecord{State: record.State, IdempotencyKey: record.IdempotencyKey}
	if record.Identity != nil {
		clone.Identity = proto.Clone(record.Identity).(*agentv1.ScheduleIdentity)
	}
	if record.Spec != nil {
		clone.Spec = proto.Clone(record.Spec).(*agentv1.ScheduleSpec)
	}
	return clone
}

func (r *Runtime) upsert(req *agentv1.ScheduleUpsert) error {
	if req == nil || req.Identity == nil || req.Spec == nil {
		return fmt.Errorf("schedule upsert requires identity and spec")
	}
	id := strings.TrimSpace(req.Identity.ScheduleId)
	if id == "" || req.Identity.Revision == 0 || strings.TrimSpace(req.Identity.SourceDesktopInstanceId) == "" || req.Spec.ScheduleId != id {
		return fmt.Errorf("schedule identity is invalid or does not match spec")
	}
	if req.Spec.TriggerAtUtc == nil || req.Spec.TriggerAtUtc.CheckValid() != nil {
		return fmt.Errorf("trigger_at_utc is required and must be valid")
	}
	if req.Spec.PayloadRunPlanSnapshot == nil {
		return fmt.Errorf("payload_run_plan_snapshot is required")
	}
	if target := strings.TrimSpace(req.Spec.TargetAgentId); target != "" &&
		strings.TrimSpace(r.cfg.Identity.AgentID) != "" &&
		target != r.cfg.Identity.AgentID {
		return fmt.Errorf("%w: schedule targets agent %q", errAuthorization, target)
	}
	if err := r.engine.ValidatePlan(req.Spec.PayloadRunPlanSnapshot); err != nil {
		return err
	}
	if req.Spec.TriggerType == agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_RECURRING {
		if _, err := recurrenceDuration(req.Spec.RecurrenceRule); err != nil {
			return err
		}
	} else if req.Spec.TriggerType != agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_ONE_SHOT {
		return fmt.Errorf("unsupported trigger type")
	}
	if req.Spec.TriggerWindowSeconds == 0 {
		req.Spec.TriggerWindowSeconds = 60
	}
	if req.Spec.OverlapPolicy == agentv1.ScheduleOverlapPolicy_SCHEDULE_OVERLAP_POLICY_UNSPECIFIED {
		req.Spec.OverlapPolicy = agentv1.ScheduleOverlapPolicy_SCHEDULE_OVERLAP_POLICY_SKIP_IF_RUNNING
	}
	if req.Spec.MisfirePolicy == agentv1.ScheduleMisfirePolicy_SCHEDULE_MISFIRE_POLICY_UNSPECIFIED {
		req.Spec.MisfirePolicy = agentv1.ScheduleMisfirePolicy_SCHEDULE_MISFIRE_POLICY_FIRE_ONCE_WITHIN_WINDOW
	}
	if req.Spec.MaxCatchUpOccurrences == 0 {
		req.Spec.MaxCatchUpOccurrences = 1
	}
	if req.Spec.MaxCatchUpOccurrences != 1 {
		return fmt.Errorf("%w: only max_catch_up_occurrences=1 is currently supported", errUnsupported)
	}
	if req.Spec.OverlapPolicy == agentv1.ScheduleOverlapPolicy_SCHEDULE_OVERLAP_POLICY_QUEUE_ONE {
		return fmt.Errorf("%w: overlap policy queue-one is not currently supported", errUnsupported)
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	current := r.records[id]
	timingChanged := current == nil || !scheduleTimingEqual(current.Spec, req.Spec)
	if current != nil {
		if current.Identity.SourceDesktopInstanceId != "" && current.Identity.SourceDesktopInstanceId != req.Identity.SourceDesktopInstanceId {
			return fmt.Errorf("%w: schedule belongs to desktop %q", errAuthorization, current.Identity.SourceDesktopInstanceId)
		}
		if current.Identity.Revision == req.Identity.Revision && current.IdempotencyKey == req.IdempotencyKey {
			if proto.Equal(current.Spec, req.Spec) {
				return nil
			}
			return fmt.Errorf("%w: idempotency key was reused with a different schedule payload", errRevision)
		}
		if req.ExpectedPreviousRevision != nil && *req.ExpectedPreviousRevision != current.Identity.Revision {
			return fmt.Errorf("%w: expected previous revision %d, found %d", errRevision, *req.ExpectedPreviousRevision, current.Identity.Revision)
		}
		if req.Identity.Revision <= current.Identity.Revision {
			return fmt.Errorf("%w: revision must increase beyond %d", errRevision, current.Identity.Revision)
		}
	} else if tombstone := r.tombstones[id]; tombstone >= req.Identity.Revision {
		return fmt.Errorf("%w: tombstone revision is %d", errRevision, tombstone)
	}

	record := &scheduleRecord{Identity: proto.Clone(req.Identity).(*agentv1.ScheduleIdentity), Spec: proto.Clone(req.Spec).(*agentv1.ScheduleSpec), IdempotencyKey: req.IdempotencyKey}
	if req.Spec.Enabled {
		record.State = agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE
	} else {
		record.State = agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_PAUSED
	}
	previous := r.snapshotLocked()
	r.records[id] = record
	delete(r.tombstones, id)
	cp := r.checkpoints[id]
	if cp == nil {
		cp = &checkpoint{}
		r.checkpoints[id] = cp
	}
	if timingChanged || cp.NextDueUTC.IsZero() && cp.LastPlannedUTC.IsZero() {
		cp.NextDueUTC = req.Spec.TriggerAtUtc.AsTime().UTC()
		if timingChanged {
			cp.LastPlannedUTC = time.Time{}
		}
	}
	if err := r.persistLocked(); err != nil {
		r.restoreLocked(previous)
		_ = r.persistLocked()
		return fmt.Errorf("%w: %v", errStorage, err)
	}
	return nil
}

func (r *Runtime) mutate(id string, revision uint64, key string, state agentv1.ScheduleDefinitionState) error {
	id = strings.TrimSpace(id)
	if id == "" || revision == 0 {
		return fmt.Errorf("schedule id and revision are required")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	previous := r.snapshotLocked()
	record := r.records[id]
	if state == agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_TOMBSTONED {
		current := uint64(0)
		if record != nil {
			current = record.Identity.Revision
		} else {
			current = r.tombstones[id]
		}
		if revision < current {
			return fmt.Errorf("%w: revision %d is older than %d", errRevision, revision, current)
		}
		delete(r.records, id)
		r.tombstones[id] = revision
		delete(r.checkpoints, id)
	} else {
		if record == nil {
			return fmt.Errorf("schedule %q does not exist", id)
		}
		if revision < record.Identity.Revision {
			return fmt.Errorf("%w: revision %d is older than %d", errRevision, revision, record.Identity.Revision)
		}
		if revision == record.Identity.Revision && key != record.IdempotencyKey {
			return fmt.Errorf("%w: revision %d already exists", errRevision, revision)
		}
		record.Identity.Revision = revision
		record.IdempotencyKey = key
		record.State = state
		record.Spec.Enabled = state == agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE
	}
	if err := r.persistLocked(); err != nil {
		r.restoreLocked(previous)
		_ = r.persistLocked()
		return fmt.Errorf("%w: %v", errStorage, err)
	}
	return nil
}

func (r *Runtime) tick(ctx context.Context, now time.Time) {
	type due struct {
		id      string
		planned time.Time
		spec    *agentv1.ScheduleSpec
	}
	r.flushPendingTerminals()
	var work []due
	r.mu.Lock()
	previous := r.snapshotLocked()
	r.lastTick = now
	for id, rec := range r.records {
		if rec.State != agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE {
			continue
		}
		cp := r.checkpoints[id]
		if cp == nil {
			cp = &checkpoint{NextDueUTC: rec.Spec.TriggerAtUtc.AsTime().UTC()}
			r.checkpoints[id] = cp
		}
		if cp.NextDueUTC.IsZero() || cp.NextDueUTC.After(now) {
			continue
		}
		planned := cp.NextDueUTC
		if rec.Spec.TriggerType == agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_RECURRING {
			interval, _ := recurrenceDuration(rec.Spec.RecurrenceRule)
			if now.Sub(planned) >= interval {
				planned = planned.Add(time.Duration(int64(now.Sub(planned)/interval)) * interval)
			}
			cp.NextDueUTC = planned.Add(interval)
		} else {
			cp.NextDueUTC = time.Time{}
			rec.State = agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_PAUSED
			rec.Spec.Enabled = false
		}
		cp.LastPlannedUTC = planned
		work = append(work, due{id: id, planned: planned, spec: proto.Clone(rec.Spec).(*agentv1.ScheduleSpec)})
	}
	if err := r.persistLocked(); err != nil {
		r.restoreLocked(previous)
		_ = r.persistLocked()
		r.mu.Unlock()
		r.log.Error("failed to persist schedule tick; due occurrences were not admitted", "error", err)
		return
	}
	r.mu.Unlock()
	for _, item := range work {
		r.admit(ctx, item.id, item.planned, item.spec, now)
	}
}

func (r *Runtime) admit(ctx context.Context, scheduleID string, planned time.Time, spec *agentv1.ScheduleSpec, now time.Time) {
	occurrenceID := occurrenceID(scheduleID, planned)
	r.mu.Lock()
	_, terminal := r.terminalIDs[occurrenceID]
	running := len(r.running[scheduleID]) > 0
	r.mu.Unlock()
	if terminal {
		return
	}
	window := time.Duration(spec.TriggerWindowSeconds) * time.Second
	if now.Sub(planned) > window && spec.MisfirePolicy != agentv1.ScheduleMisfirePolicy_SCHEDULE_MISFIRE_POLICY_COALESCE {
		if err := r.recordTerminal(scheduleID, occurrenceID, planned, "", agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SKIPPED, nil, "MISFIRE_POLICY", "Occurrence was outside its trigger window."); err != nil {
			r.restoreDue(scheduleID, planned, spec)
		}
		return
	}
	if running && spec.OverlapPolicy != agentv1.ScheduleOverlapPolicy_SCHEDULE_OVERLAP_POLICY_ALLOW {
		if err := r.recordTerminal(scheduleID, occurrenceID, planned, "", agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SKIPPED, nil, "OVERLAP_POLICY", "A previous occurrence is still running."); err != nil {
			r.restoreDue(scheduleID, planned, spec)
		}
		return
	}
	runID := "run_sched_" + shortHash(occurrenceID)
	attemptID := "attempt_" + shortHash(occurrenceID+":1")
	offerID := "offer_" + shortHash(occurrenceID)
	digest, err := tapproto.DigestPlan(spec.PayloadRunPlanSnapshot)
	if err != nil {
		if terminalErr := r.recordTerminal(scheduleID, occurrenceID, planned, runID, agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_FAILED, nil, "INVALID_SPEC", err.Error()); terminalErr != nil {
			r.restoreDue(scheduleID, planned, spec)
		}
		return
	}
	r.mu.Lock()
	if r.running[scheduleID] == nil {
		r.running[scheduleID] = map[string]struct{}{}
	}
	r.running[scheduleID][occurrenceID] = struct{}{}
	r.mu.Unlock()
	event := &loggedOccurrence{ScheduleID: scheduleID, OccurrenceID: occurrenceID, PlannedOccurrenceUTC: planned, State: agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_PLANNED, RunID: runID}
	if err := r.record(event); err != nil {
		r.mu.Lock()
		delete(r.running[scheduleID], occurrenceID)
		if len(r.running[scheduleID]) == 0 {
			delete(r.running, scheduleID)
		}
		r.restoreDueLocked(scheduleID, planned, spec)
		_ = r.persistLocked()
		r.mu.Unlock()
		return
	}
	offer := &agentv1.RunOffer{OfferId: offerID, RunId: runID, AttemptId: attemptID, PlanDigestSha256: digest, Plan: proto.Clone(spec.PayloadRunPlanSnapshot).(*agentv1.RunPlan), DisconnectPolicy: agentv1.DisconnectPolicy_DISCONNECT_POLICY_CONTINUE_AND_REPORT}
	go r.engine.HandleOffer(ctx, &occurrenceSender{runtime: r, occurrence: event}, offer)
}

func (r *Runtime) restoreDue(scheduleID string, planned time.Time, spec *agentv1.ScheduleSpec) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.restoreDueLocked(scheduleID, planned, spec)
	_ = r.persistLocked()
}

func (r *Runtime) restoreDueLocked(scheduleID string, planned time.Time, spec *agentv1.ScheduleSpec) {
	cp := r.checkpoints[scheduleID]
	if cp == nil {
		cp = &checkpoint{}
		r.checkpoints[scheduleID] = cp
	}
	if cp.NextDueUTC.IsZero() || cp.NextDueUTC.After(planned) {
		cp.NextDueUTC = planned
	}
	if spec.TriggerType == agentv1.ScheduleTriggerType_SCHEDULE_TRIGGER_TYPE_ONE_SHOT {
		if record := r.records[scheduleID]; record != nil {
			record.State = agentv1.ScheduleDefinitionState_SCHEDULE_DEFINITION_STATE_ACTIVE
			record.Spec.Enabled = true
		}
	}
}

type occurrenceSender struct {
	runtime           *Runtime
	occurrence        *loggedOccurrence
	mu                sync.Mutex
	admitted, started bool
}

func (s *occurrenceSender) Send(frame *agentv1.AgentFrame) error {
	s.mu.Lock()
	if frame.GetRunAccepted() != nil && !s.admitted {
		s.admitted = true
		now := time.Now().UTC()
		s.occurrence.AdmittedAtUTC = &now
		s.occurrence.State = agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_ADMITTED
		if err := s.runtime.record(s.occurrence); err != nil {
			s.mu.Unlock()
			return err
		}
	}
	if ev := frame.GetRunEvent(); ev != nil && ev.AttemptState == agentv1.RunAttemptState_RUN_ATTEMPT_STATE_RUNNING && !s.started {
		s.started = true
		atomic.AddUint64(&s.runtime.started, 1)
		now := time.Now().UTC()
		s.occurrence.StartedAtUTC = &now
		s.occurrence.State = agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_RUNNING
		if err := s.runtime.record(s.occurrence); err != nil {
			s.mu.Unlock()
			return err
		}
	}
	if completed := frame.GetRunCompleted(); completed != nil {
		outcome := outcomeFor(completed.TerminalState)
		var exit *int32
		if completed.HasExitCode {
			v := completed.ExitCode
			exit = &v
		}
		message := ""
		if completed.Error != nil {
			message = completed.Error.Message
		}
		if err := s.runtime.recordTerminal(s.occurrence.ScheduleID, s.occurrence.OccurrenceID, s.occurrence.PlannedOccurrenceUTC, completed.RunId, outcome, exit, "", message); err != nil {
			s.mu.Unlock()
			return err
		}
	}
	s.mu.Unlock()
	return s.runtime.send(frame)
}

func (r *Runtime) recordTerminal(scheduleID, occurrenceID string, planned time.Time, runID string, outcome agentv1.ScheduleOccurrenceOutcome, exit *int32, reason, message string) error {
	now := time.Now().UTC()
	ev := &loggedOccurrence{ScheduleID: scheduleID, OccurrenceID: occurrenceID, PlannedOccurrenceUTC: planned, CompletedAtUTC: &now, State: agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_TERMINAL, Outcome: outcome, RunID: runID, ExitCode: exit, ReasonCode: reason, Message: message}
	r.mu.Lock()
	if _, exists := r.terminalIDs[occurrenceID]; exists {
		r.mu.Unlock()
		return nil
	}
	clone, err := r.persistOccurrenceLocked(ev)
	if err != nil {
		r.pendingTerminals[occurrenceID] = ev
		r.mu.Unlock()
		r.log.Error("failed to persist terminal schedule occurrence", "error", err, "scheduleId", scheduleID, "occurrenceId", occurrenceID)
		return err
	}
	r.terminalIDs[occurrenceID] = struct{}{}
	delete(r.running[scheduleID], occurrenceID)
	if len(r.running[scheduleID]) == 0 {
		delete(r.running, scheduleID)
	}
	r.terminal[outcome]++
	r.mu.Unlock()
	r.sendOccurrence(clone)
	return nil
}

func (r *Runtime) flushPendingTerminals() {
	var durable []*loggedOccurrence
	r.mu.Lock()
	for occurrenceID, ev := range r.pendingTerminals {
		if _, exists := r.terminalIDs[occurrenceID]; exists {
			delete(r.pendingTerminals, occurrenceID)
			continue
		}
		clone, err := r.persistOccurrenceLocked(ev)
		if err != nil {
			continue
		}
		r.terminalIDs[occurrenceID] = struct{}{}
		delete(r.running[ev.ScheduleID], occurrenceID)
		if len(r.running[ev.ScheduleID]) == 0 {
			delete(r.running, ev.ScheduleID)
		}
		r.terminal[ev.Outcome]++
		delete(r.pendingTerminals, occurrenceID)
		durable = append(durable, clone)
	}
	r.mu.Unlock()
	for _, ev := range durable {
		r.sendOccurrence(ev)
	}
}

func (r *Runtime) record(ev *loggedOccurrence) error {
	r.mu.Lock()
	clone, err := r.persistOccurrenceLocked(ev)
	r.mu.Unlock()
	if err != nil {
		r.log.Error("failed to persist schedule occurrence", "error", err, "scheduleId", ev.ScheduleID)
		return err
	}
	r.sendOccurrence(clone)
	return nil
}

func (r *Runtime) persistOccurrenceLocked(ev *loggedOccurrence) (*loggedOccurrence, error) {
	clone := *ev
	clone.Sequence = r.nextSeq + 1
	if err := appendJSONLine(r.occurrencePath(), &clone); err != nil {
		return nil, err
	}
	r.nextSeq = clone.Sequence
	if cp := r.checkpoints[ev.ScheduleID]; cp != nil {
		cp.LastOccurrenceSeq = clone.Sequence
		if clone.State == agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_TERMINAL {
			cp.LastTerminalID = clone.OccurrenceID
		}
	}
	if err := r.persistCheckpointsLocked(); err != nil {
		r.log.Warn("schedule occurrence log is durable but checkpoint update failed", "error", err, "scheduleId", ev.ScheduleID, "sequence", clone.Sequence)
	}
	return &clone, nil
}

func (r *Runtime) sendOccurrence(ev *loggedOccurrence) {
	r.send(&agentv1.AgentFrame{Header: tapproto.Header(ev.OccurrenceID), Body: &agentv1.AgentFrame_ScheduleOccurrenceEvent{ScheduleOccurrenceEvent: ev.toProto()}})
}

func (e *loggedOccurrence) toProto() *agentv1.ScheduleOccurrenceEvent {
	p := &agentv1.ScheduleOccurrenceEvent{Sequence: e.Sequence, ScheduleId: e.ScheduleID, OccurrenceId: e.OccurrenceID, PlannedOccurrenceUtc: timestamppb.New(e.PlannedOccurrenceUTC), State: e.State, Outcome: e.Outcome, RunId: e.RunID, ExitCode: e.ExitCode, Message: e.Message, ReasonCode: e.ReasonCode}
	if e.AdmittedAtUTC != nil {
		p.AdmittedAtUtc = timestamppb.New(*e.AdmittedAtUTC)
	}
	if e.StartedAtUTC != nil {
		p.StartedAtUtc = timestamppb.New(*e.StartedAtUTC)
	}
	if e.CompletedAtUTC != nil {
		p.CompletedAtUtc = timestamppb.New(*e.CompletedAtUTC)
	}
	return p
}

func (r *Runtime) load() error {
	if err := os.MkdirAll(r.root, 0o700); err != nil {
		return err
	}
	var registry persistedRegistry
	if err := readJSON(r.registryPath(), &registry); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("load schedules: %w", err)
	}
	for _, rec := range registry.Schedules {
		if rec != nil && rec.Identity != nil {
			r.records[rec.Identity.ScheduleId] = rec
		}
	}
	if registry.Tombstones != nil {
		r.tombstones = registry.Tombstones
	}
	var cps checkpointFile
	if err := readJSON(r.checkpointPath(), &cps); err == nil {
		r.nextSeq = cps.NextSequence
		if cps.Schedules != nil {
			r.checkpoints = cps.Schedules
		}
	} else if !os.IsNotExist(err) {
		return fmt.Errorf("load schedule checkpoints: %w", err)
	}
	file, err := os.Open(r.occurrencePath())
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	defer file.Close()
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		var ev loggedOccurrence
		if json.Unmarshal(scanner.Bytes(), &ev) == nil {
			if ev.Sequence > r.nextSeq {
				r.nextSeq = ev.Sequence
			}
			if ev.State == agentv1.ScheduleOccurrenceState_SCHEDULE_OCCURRENCE_STATE_TERMINAL {
				r.terminalIDs[ev.OccurrenceID] = struct{}{}
				r.terminal[ev.Outcome]++
			}
		}
	}
	return scanner.Err()
}

func (r *Runtime) persistLocked() error {
	records := make([]*scheduleRecord, 0, len(r.records))
	for _, rec := range r.records {
		records = append(records, rec)
	}
	sort.Slice(records, func(i, j int) bool { return records[i].Identity.ScheduleId < records[j].Identity.ScheduleId })
	if err := atomicJSON(r.registryPath(), &persistedRegistry{SchemaVersion: 1, Schedules: records, Tombstones: r.tombstones}); err != nil {
		return err
	}
	return r.persistCheckpointsLocked()
}
func (r *Runtime) persistCheckpointsLocked() error {
	return atomicJSON(r.checkpointPath(), &checkpointFile{SchemaVersion: 1, NextSequence: r.nextSeq, Schedules: r.checkpoints})
}
func (r *Runtime) registryPath() string   { return filepath.Join(r.root, "schedules.json") }
func (r *Runtime) checkpointPath() string { return filepath.Join(r.root, "schedule-checkpoints.json") }
func (r *Runtime) occurrencePath() string {
	return filepath.Join(r.root, "schedule-occurrence-log.jsonl")
}

func (r *Runtime) readOccurrencesAfter(seq uint64) ([]*agentv1.ScheduleOccurrenceEvent, error) {
	file, err := os.Open(r.occurrencePath())
	if os.IsNotExist(err) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer file.Close()
	var out []*agentv1.ScheduleOccurrenceEvent
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		var ev loggedOccurrence
		if json.Unmarshal(scanner.Bytes(), &ev) == nil && ev.Sequence > seq {
			out = append(out, ev.toProto())
		}
	}
	return out, scanner.Err()
}

func (r *Runtime) send(frame *agentv1.AgentFrame) error {
	r.mu.Lock()
	sender := r.sender
	r.mu.Unlock()
	if sender == nil {
		return nil
	}
	return sender.Send(frame)
}
func (r *Runtime) sendAck(id string, revision uint64) {
	r.send(&agentv1.AgentFrame{Header: tapproto.Header(id), Body: &agentv1.AgentFrame_ScheduleAck{ScheduleAck: &agentv1.ScheduleAck{ScheduleId: id, AcceptedRevision: revision, AcceptedAtUtc: timestamppb.Now()}}})
}
func (r *Runtime) sendNack(id string, rev uint64, code agentv1.ScheduleNackReasonCode, msg string, retry bool) {
	r.send(&agentv1.AgentFrame{Header: tapproto.Header(id), Body: &agentv1.AgentFrame_ScheduleNack{ScheduleNack: &agentv1.ScheduleNack{ScheduleId: id, RejectedRevision: rev, ReasonCode: code, ReasonMessage: msg, Retryable: retry}}})
}
func (r *Runtime) sendStatus(id string) {
	r.mu.Lock()
	rec := cloneRecord(r.records[id])
	var cp *checkpoint
	if current := r.checkpoints[id]; current != nil {
		clone := *current
		cp = &clone
	}
	last := r.lastTick
	r.mu.Unlock()
	if rec == nil {
		return
	}
	status := &agentv1.ScheduleStatus{ScheduleId: id, Revision: rec.Identity.Revision, State: rec.State}
	if !last.IsZero() {
		status.LastTickUtc = timestamppb.New(last)
	}
	if cp != nil && !cp.NextDueUTC.IsZero() {
		status.NextDueUtc = timestamppb.New(cp.NextDueUTC)
		status.LastOccurrenceSequence = cp.LastOccurrenceSeq
	}
	r.send(&agentv1.AgentFrame{Header: tapproto.Header(id), Body: &agentv1.AgentFrame_ScheduleStatus{ScheduleStatus: status}})
}

func recurrenceDuration(rule string) (time.Duration, error) {
	m := everyRule.FindStringSubmatch(strings.TrimSpace(rule))
	if len(m) != 2 {
		return 0, fmt.Errorf("recurrence_rule must use every:{n}m")
	}
	var n int
	if _, err := fmt.Sscanf(m[1], "%d", &n); err != nil || n < 1 {
		return 0, fmt.Errorf("recurrence interval must be positive")
	}
	return time.Duration(n) * time.Minute, nil
}
func scheduleTimingEqual(left, right *agentv1.ScheduleSpec) bool {
	if left == nil || right == nil || left.TriggerAtUtc == nil || right.TriggerAtUtc == nil {
		return false
	}
	return left.TriggerType == right.TriggerType &&
		left.TriggerAtUtc.AsTime().Equal(right.TriggerAtUtc.AsTime()) &&
		strings.TrimSpace(left.RecurrenceRule) == strings.TrimSpace(right.RecurrenceRule)
}
func occurrenceID(id string, planned time.Time) string {
	return id + ":" + planned.UTC().Format(time.RFC3339Nano)
}
func shortHash(value string) string {
	sum := sha256.Sum256([]byte(value))
	return hex.EncodeToString(sum[:12])
}
func deterministicChecksum(m proto.Message) ([]byte, error) {
	data, err := proto.MarshalOptions{Deterministic: true}.Marshal(m)
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256(data)
	return sum[:], nil
}
func outcomeFor(state agentv1.RunAttemptState) agentv1.ScheduleOccurrenceOutcome {
	switch state {
	case agentv1.RunAttemptState_RUN_ATTEMPT_STATE_SUCCEEDED:
		return agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_SUCCEEDED
	case agentv1.RunAttemptState_RUN_ATTEMPT_STATE_TIMED_OUT:
		return agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_TIMED_OUT
	case agentv1.RunAttemptState_RUN_ATTEMPT_STATE_CANCELLED:
		return agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_CANCELLED
	default:
		return agentv1.ScheduleOccurrenceOutcome_SCHEDULE_OCCURRENCE_OUTCOME_FAILED
	}
}
func classifyNack(err error) agentv1.ScheduleNackReasonCode {
	if errors.Is(err, errRevision) {
		return agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_REVISION_CONFLICT
	}
	if errors.Is(err, errStorage) {
		return agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_STORAGE_FAILURE
	}
	if errors.Is(err, errAuthorization) {
		return agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_AUTHORIZATION_DENIED
	}
	if errors.Is(err, errUnsupported) {
		return agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_UNSUPPORTED_FEATURE
	}
	return agentv1.ScheduleNackReasonCode_SCHEDULE_NACK_REASON_CODE_INVALID_SPEC
}
func scheduleIDFromUpsert(v *agentv1.ScheduleUpsert) string {
	if v != nil && v.Identity != nil {
		return v.Identity.ScheduleId
	}
	return ""
}
func revisionFromUpsert(v *agentv1.ScheduleUpsert) uint64 {
	if v != nil && v.Identity != nil {
		return v.Identity.Revision
	}
	return 0
}
func scheduleIDFromDelete(v *agentv1.ScheduleDelete) string {
	if v != nil {
		return v.ScheduleId
	}
	return ""
}
func revisionFromDelete(v *agentv1.ScheduleDelete) uint64 {
	if v != nil {
		return v.Revision
	}
	return 0
}
func readJSON(path string, value any) error {
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, value)
}
func atomicJSON(path string, value any) error {
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err = os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	if err = os.Rename(tmp, path); err == nil {
		return nil
	}
	if removeErr := os.Remove(path); removeErr != nil && !os.IsNotExist(removeErr) {
		return err
	}
	return os.Rename(tmp, path)
}
func appendJSONLine(path string, value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return err
	}
	defer file.Close()
	if _, err = file.Write(append(data, '\n')); err != nil {
		return err
	}
	return file.Sync()
}
