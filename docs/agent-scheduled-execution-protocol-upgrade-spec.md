# Agent-Side Scheduled Execution Protocol Upgrade Spec

Status: Proposed  
Revision: 0.1  
Date: 2026-08-10  
Scope: TAP v1.x extension for controller-managed schedules executed by agents while desktop may disconnect.

## 1. Executive Summary

This document proposes a TAP protocol upgrade that allows the desktop scheduler to publish schedule definitions to one or more agents. Once published and acknowledged, an agent executes due occurrences locally from durable schedule state, even if the desktop disconnects.

The current system already supports controller-pushed run offers for immediate execution. It does not yet model scheduler definitions as first-class protocol entities.

This upgrade adds:
- Schedule definition transport.
- Agent-side durable schedule registry.
- Deterministic schedule lifecycle and occurrence lifecycle.
- Offline continuation semantics for disconnected desktops.
- Reconciliation and idempotent updates.

## 2. Problem Statement

Current behavior:
- Desktop scheduler metadata is persisted locally in scheduler.json.
- Desktop dispatches run offers to agents for execution.
- Agent executes accepted offers and reports run lifecycle events.

Gap:
- The protocol does not carry full scheduler-definition metadata as a first-class model.
- Agents cannot own and trigger schedules independently after desktop disconnect.

## 3. Goals

- Allow desktop to publish, update, disable, and remove schedules on agents.
- Preserve schedule fidelity for core fields needed to trigger runs correctly.
- Ensure a due occurrence can execute while desktop is offline/disconnected.
- Keep run execution semantics aligned with existing run-offer execution model.
- Provide robust reconciliation after reconnect without duplicate occurrences.
- Preserve backward compatibility with existing TAP v1 run-offer behavior.

## 4. Non-Goals

- No autonomous agent-authored schedules in this phase.
- No distributed quorum scheduling across multiple agents.
- No global exactly-once guarantee across network partitions.
- No replacement of existing immediate RunOffer flow.
- No cross-agent migration of in-flight occurrences in this phase.

## 5. Current State Evidence

- Desktop scheduler scope currently excludes remote schedule ownership.
- Controller pushes RunOffer over TAP; agent executes process.v1 steps.
- TAP run entities exist (RunOffer, RunPlan, RunStep, ExecutorCommand), but no first-class schedule messages.

## 6. Proposed Architecture

High-level flow:
1. Desktop creates or edits schedule in scheduler UI.
2. Desktop compiles canonical ScheduleSpec and sends a schedule upsert command to target agent.
3. Agent validates, persists, and acknowledges schedule version.
4. Agent local scheduler evaluates due occurrences and generates local run instances.
5. Agent reports occurrence status and resulting run lifecycle to desktop when connected.
6. On reconnect, desktop and agent reconcile schedule versions and occurrence checkpoints.

Control-plane ownership:
- Desktop remains source of truth for desired schedule configuration.
- Agent is source of truth for local execution progress while disconnected.

## 7. TAP Protocol Extensions

Note: Message names below are proposed and can be adjusted to match existing TAP naming conventions.

### 7.1 New Controller-to-Agent Frame Types

- ScheduleUpsert
- ScheduleDelete
- SchedulePause
- ScheduleResume
- ScheduleSyncRequest

### 7.2 New Agent-to-Controller Frame Types

- ScheduleAck
- ScheduleNack
- ScheduleStatus
- ScheduleOccurrenceEvent
- ScheduleSyncResponse

### 7.3 Proposed Message Contracts

ScheduleIdentity:
- scheduleId (stable UUID)
- revision (monotonic int64 or ULID-based version token)
- sourceDesktopInstanceId

ScheduleSpec:
- scheduleId
- displayName
- enabled
- triggerType (one-shot | recurring)
- triggerAtUtc (for one-shot)
- recurrenceRule (initially support every:{n}m compatible format)
- triggerWindowSeconds
- targetAgentId or targetPoolId policy snapshot
- payloadRunPlanSnapshot (immutable run plan payload)
- misfirePolicy (fire-once-within-window | skip | coalesce)
- overlapPolicy (allow | skip-if-running | queue-one)
- maxCatchUpOccurrences
- createdAtUtc
- updatedAtUtc

ScheduleUpsert:
- identity
- spec
- idempotencyKey
- expectedPreviousRevision (optional compare-and-set)

ScheduleDelete:
- scheduleId
- revision
- idempotencyKey

ScheduleAck:
- scheduleId
- acceptedRevision
- persistenceChecksum (optional)
- acceptedAtUtc

ScheduleNack:
- scheduleId
- rejectedRevision
- reasonCode
- reasonMessage
- retryable (bool)

ScheduleOccurrenceEvent:
- scheduleId
- occurrenceId (scheduleId + plannedOccurrenceUtc)
- plannedOccurrenceUtc
- admittedAtUtc
- startedAtUtc
- completedAtUtc
- outcome (SUCCEEDED | FAILED | SKIPPED | TIMED_OUT | CANCELLED)
- runId (if run started)
- exitCode (optional)
- message

### 7.4 Error / Reason Codes

ScheduleNack reasonCode set:
- INVALID_SPEC
- UNSUPPORTED_FEATURE
- REVISION_CONFLICT
- CAPACITY_POLICY_REJECTED
- STORAGE_FAILURE
- AUTHORIZATION_DENIED

Occurrence outcome reasons for skipped/coalesced:
- OVERLAP_POLICY
- MISFIRE_POLICY
- DISABLED
- DELETED

## 8. State Models

### 8.1 Schedule Definition State (Agent)

- ABSENT
- ACTIVE
- PAUSED
- TOMBSTONED

Transitions:
- ABSENT -> ACTIVE via accepted ScheduleUpsert(enabled=true)
- ABSENT -> PAUSED via accepted ScheduleUpsert(enabled=false)
- ACTIVE <-> PAUSED via Pause/Resume or Upsert(enabled)
- ACTIVE/PAUSED -> TOMBSTONED via ScheduleDelete

### 8.2 Occurrence State

- PLANNED
- ADMITTED
- RUNNING
- TERMINAL

Transitions:
- PLANNED -> ADMITTED when local scheduler admits occurrence
- ADMITTED -> RUNNING when execution slot acquired
- ADMITTED -> TERMINAL on admission timeout/cancel
- RUNNING -> TERMINAL on run completion

## 9. Disconnect and Recovery Semantics

When desktop disconnects:
- Agent continues evaluating ACTIVE schedules from local persisted definitions.
- Agent records occurrence events durably in append-only occurrence log.
- Agent replays backlog on reconnect in event-time order per schedule.

When agent restarts:
- Agent restores schedule registry and next-evaluation checkpoint.
- Agent must not duplicate already-terminal occurrenceId values.
- Incomplete RUNNING occurrences follow existing run recovery policy.

## 10. Concurrency and Admission Integration

Agent-scheduled occurrences must use the same admission model as externally offered runs:
- bounded active slots (maxConcurrentRuns)
- bounded pending depth (maxPendingQueueDepth)
- admission wait timeout (admissionQueueWaitTimeoutSeconds)

Expected behavior under load:
- occurrence may reach TERMINAL with timed-out/cancelled outcome if admission cannot be obtained in time.
- telemetry increments rejected-by-capacity and queue/active metrics accordingly.

## 11. Persistence Model (Agent)

New files under agent runtime state root:
- schedules.json (current desired schedule definitions, versioned)
- schedule-occurrence-log.jsonl (append-only occurrence events)
- schedule-checkpoints.json (per-schedule high-water marks and next due cursor)

Durability requirements:
- write schedule definition atomically before ScheduleAck.
- append occurrence event before emitting equivalent TAP frame when possible.
- use monotonic sequence numbers for replay ordering.

## 12. Security and Policy

- Reuse existing TAP session authentication and authorization.
- Only authorized controller identities may mutate schedules.
- Agent validates RunPlan executor allow-list at upsert time where possible.
- Optional policy gate: reject schedules that exceed local max frequency or forbidden commands.

## 13. Backward Compatibility and Versioning

- Existing run-offer flow remains unchanged.
- Capability negotiation advertises scheduler extension support (for example: schedule.v1).
- Desktop only sends schedule frames when capability is present.
- Mixed fleet behavior:
  - upgraded desktop + legacy agent: fallback to current desktop-driven immediate dispatch behavior.
  - legacy desktop + upgraded agent: scheduler extension remains idle.

## 14. Reconciliation Protocol

On session establish:
1. Desktop sends ScheduleSyncRequest with known scheduleId -> revision map.
2. Agent returns ScheduleSyncResponse with local map and dirty occurrence ranges.
3. Desktop resolves divergence:
- if desktop revision newer: send ScheduleUpsert.
- if agent has unknown tombstone/newer local revision: request operator decision or force policy.
4. Agent replays missing ScheduleOccurrenceEvent entries since last acknowledged sequence.

Conflict policy (default):
- desktop desired-state wins for definition fields.
- agent execution history is append-only evidence and never overwritten.

## 15. Observability

Add heartbeat/snapshot fields:
- schedule.active_count
- schedule.paused_count
- schedule.occurrence_backlog_count
- schedule.last_tick_utc
- schedule.replay_queue_depth

Add counters:
- schedule_upsert_accepted_total
- schedule_upsert_rejected_total
- schedule_occurrence_started_total
- schedule_occurrence_terminal_total{outcome=*}

## 16. Rollout Plan

Phase 1: Protocol and storage scaffolding
- Add TAP frame/message definitions and capability negotiation.
- Add no-op parser/handlers behind feature flag on agent.

Phase 2: Agent local scheduler runtime
- Persist schedule definitions.
- Tick loop + due evaluation + occurrence logging.
- Reuse existing run execution pipeline for occurrence launches.

Phase 3: Desktop integration
- Map scheduler.json items to ScheduleSpec.
- Publish upserts/deletes on save operations.
- Surface per-agent schedule status and errors.

Phase 4: Reconciliation hardening
- Sync handshake and replay.
- Conflict tooling and operator diagnostics.

Phase 5: GA hardening
- Fault-injection tests.
- Long-disconnect and restart resilience tests.
- Backward compatibility matrix sign-off.

## 17. Testing Strategy

Protocol tests:
- serialization/deserialization compatibility for new messages.
- capability negotiation and fallback behavior.

Agent tests:
- upsert/delete/pause/resume state transitions.
- due-trigger correctness for one-shot and recurring schedules.
- admission timeout and overload outcomes.
- restart recovery without duplicate occurrenceId terminals.

Desktop-agent integration tests:
- publish, disconnect desktop, allow due run, reconnect, replay evidence.
- revision conflict and forced reconciliation behavior.

## 18. Open Questions

- Should recurrenceRule stay string-based initially or move directly to structured RRULE fields?
- Should overlapPolicy default to skip-if-running for safety?
- Is misfire coalescing required in MVP or post-MVP?
- Do we require per-schedule command allow-lists in first implementation?
- Should ScheduleSyncResponse include digest of payloadRunPlanSnapshot for drift detection?

## 19. Implementation Work Areas

Desktop:
- scheduler model to TAP mapping in remote dispatch/scheduler service.
- UI status surfaces for remote schedule deployment and health.

Protocol:
- TAP frame and message definitions for schedule control and events.
- capability labels for schedule extension.

Agent:
- schedule registry persistence and tick loop.
- occurrence-to-run pipeline integration.
- reconnect replay and reconciliation handlers.

## 20. Acceptance Criteria

- Desktop can publish schedule definitions to a connected upgraded agent.
- Agent executes due occurrences while desktop is disconnected.
- On reconnect, desktop receives durable occurrence outcomes with no duplicate terminals per occurrenceId.
- Legacy agents continue functioning with existing RunOffer flow unchanged.
- Capacity and admission behavior for scheduled occurrences is observable in telemetry and consistent with existing execution controls.
