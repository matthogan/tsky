# Agent-Side Scheduled Execution Protocol Upgrade Spec

Status: Implemented (`schedule.v1` MVP)
Revision: 1.2
Date: 2026-08-13
Scope: TAP 1.1 `schedule.v1` extension for controller-managed schedules executed by agents while desktop may disconnect, with TAP 1.0 fallback.

Implementation status legend:

- **Done**: implemented for the `schedule.v1` MVP and covered by code or tests.
- **Partial**: core behavior exists, but one or more requirements or hardening items remain.
- **Not done**: no production implementation or required end-to-end coverage exists yet.

## 1. Executive Summary

Implementation status: **Done (MVP)**. The protocol, durable agent registry, offline execution, occurrence evidence, and desktop reconciliation path are implemented. Later sections identify remaining hardening beyond the MVP.

This document proposes a TAP protocol upgrade that allows the desktop scheduler to publish schedule definitions to one or more agents. Once published and acknowledged, an agent executes due occurrences locally from durable schedule state, even if the desktop disconnects.

The current system already supports controller-pushed run offers for immediate execution. It does not yet model scheduler definitions as first-class protocol entities.

This upgrade adds:
- Schedule definition transport.
- Agent-side durable schedule registry.
- Deterministic schedule lifecycle and occurrence lifecycle.
- Offline continuation semantics for disconnected desktops.
- Reconciliation and idempotent updates.

## 2. Problem Statement

Implementation status: **Done**. The stated gap has been addressed by first-class schedule messages and an agent-owned local scheduler.

Current behavior:
- Desktop scheduler metadata is persisted locally in scheduler.json.
- Desktop dispatches run offers to agents for execution.
- Agent executes accepted offers and reports run lifecycle events.

Gap:
- The protocol does not carry full scheduler-definition metadata as a first-class model.
- Agents cannot own and trigger schedules independently after desktop disconnect.

## 3. Goals

Implementation status: **Partial**. Publishing, updating, disabling, deleting, offline execution, shared run semantics, restart de-duplication, and TAP v1 compatibility are implemented. Reconciliation lacks force-resolution/operator controls, and some policy variants are only represented in the protocol.

- Allow desktop to publish, update, disable, and remove schedules on agents.
- Preserve schedule fidelity for core fields needed to trigger runs correctly.
- Ensure a due occurrence can execute while desktop is offline/disconnected.
- Keep run execution semantics aligned with existing run-offer execution model.
- Provide robust reconciliation after reconnect without duplicate occurrences.
- Preserve backward compatibility with existing TAP v1 run-offer behavior.

## 4. Non-Goals

Implementation status: **Done**. The implementation remains controller-authored, does not use quorum scheduling, does not claim global exactly-once behavior, preserves `RunOffer`, and does not migrate in-flight occurrences between agents.

- No autonomous agent-authored schedules in this phase.
- No distributed quorum scheduling across multiple agents.
- No global exactly-once guarantee across network partitions.
- No replacement of existing immediate RunOffer flow.
- No cross-agent migration of in-flight occurrences in this phase.

## 5. Pre-Implementation State Evidence

Implementation status: **Done (historical baseline)**. These statements describe the repository before this upgrade and are retained as the baseline that motivated the work; they are superseded by the implementation described in section 21.

- Desktop scheduler scope currently excludes remote schedule ownership.
- Controller pushes RunOffer over TAP; agent executes process.v1 steps.
- TAP run entities exist (RunOffer, RunPlan, RunStep, ExecutorCommand), but no first-class schedule messages.

## 6. Proposed Architecture

Implementation status: **Done (MVP)**. Desktop save/connect paths compile and publish `ScheduleSpec`; the Scheduled Item editor provides validated Local, Automatic, specific-agent, and pool target choices with publication readiness/status; the agent validates, persists, acknowledges, ticks, launches through the existing engine, and reports/replays occurrence evidence.

High-level flow:
1. Desktop creates or edits schedule in scheduler UI.
2. Desktop compiles canonical ScheduleSpec and sends a schedule upsert command to target agent.
3. Agent validates, persists, and acknowledges schedule version.
4. Agent local scheduler evaluates due occurrences and generates local run instances.
5. Agent reports occurrence status and resulting run lifecycle to desktop when connected.
6. On reconnect, desktop and agent reconcile schedule versions and occurrence checkpoints.

Operator workflow:
1. From a task or group menu, choose `Schedule...` or `Schedule Group Chain...`.
2. In the Scheduled Item dialog, choose an Execution Target: Local, Automatic, a named agent, or a discovered agent pool.
3. Invalid, removed, pending, disabled, revoked, or protocol-incompatible targets cannot be saved. Offline enrolled agents remain valid because publication can occur after reconnect.
4. The dialog shows whether `schedule.v1` can be published immediately, must wait for a compatible connection, or will use desktop legacy fallback.
5. After save, the Scheduler card shows `Pending`, `Accepted`/agent state, `DesktopFallback`, `Rejected`, or `Conflict` plus accepted/current revisions.
6. Retargeting removes the durable definition from the previous agent (or queues a tombstone until it reconnects) before assigning a different owner. Pool and Automatic targets retain a still-eligible published owner to avoid duplicate execution.

Control-plane ownership:
- Desktop remains source of truth for desired schedule configuration.
- Agent is source of truth for local execution progress while disconnected.

## 7. TAP Protocol Extensions

Implementation status: **Partial**. All proposed frames and contracts are defined and connected on both sides. Some declared policy/reason-code variants are not yet produced by runtime behavior, as noted in section 7.4.

Note: Message names below are proposed and can be adjusted to match existing TAP naming conventions.

### 7.1 New Controller-to-Agent Frame Types

Implementation status: **Done**. `ScheduleUpsert`, `ScheduleDelete`, `SchedulePause`, `ScheduleResume`, and `ScheduleSyncRequest` are defined and handled by the Go agent.

- ScheduleUpsert
- ScheduleDelete
- SchedulePause
- ScheduleResume
- ScheduleSyncRequest

### 7.2 New Agent-to-Controller Frame Types

Implementation status: **Done**. `ScheduleAck`, `ScheduleNack`, `ScheduleStatus`, `ScheduleOccurrenceEvent`, and `ScheduleSyncResponse` are defined, emitted by the agent, and handled by the desktop gateway/UI path.

- ScheduleAck
- ScheduleNack
- ScheduleStatus
- ScheduleOccurrenceEvent
- ScheduleSyncResponse

### 7.3 Proposed Message Contracts

Implementation status: **Done**. The contracts are implemented in `protocol/tsky/agent/v1/schedule.proto`. The implementation additionally carries occurrence lifecycle state/sequence and tombstone revision maps for durable replay and conflict handling.

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

Implementation status: **Partial**. The agent emits `INVALID_SPEC`, `UNSUPPORTED_FEATURE`, `REVISION_CONFLICT`, `STORAGE_FAILURE`, and `AUTHORIZATION_DENIED`; `CAPACITY_POLICY_REJECTED` is defined but not emitted by schedule mutation handlers. Occurrences emit `OVERLAP_POLICY` and `MISFIRE_POLICY`; explicit `DISABLED` and `DELETED` occurrence outcomes are not currently logged.

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

Implementation status: **Done (MVP)**. Definition and occurrence lifecycle states are persisted or emitted as specified.

### 8.1 Schedule Definition State (Agent)

Implementation status: **Done**. Upsert, pause, resume, enabled-state upsert, and durable delete tombstones implement the listed transitions with monotonic revision checks.

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

Implementation status: **Done**. The schedule runtime records planned, admitted, running, and terminal events; admission timeout/cancellation and run completion map into terminal outcomes.

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

Implementation status: **Partial**. Active schedules continue through desktop disconnect, definitions/checkpoints restore on agent restart, terminal occurrence IDs are rebuilt to prevent duplicates, and occurrence evidence replays after the desktop cursor. Recovery of a process that was already `RUNNING` when the agent process itself terminated remains limited to the pre-existing run recovery/reconciliation behavior; there is no process reattachment.

When desktop disconnects:
- Agent continues evaluating ACTIVE schedules from local persisted definitions.
- Agent records occurrence events durably in append-only occurrence log.
- Agent replays backlog on reconnect in event-time order per schedule.

When agent restarts:
- Agent restores schedule registry and next-evaluation checkpoint.
- Agent must not duplicate already-terminal occurrenceId values.
- Incomplete RUNNING occurrences follow existing run recovery policy.

## 10. Concurrency and Admission Integration

Implementation status: **Done (MVP)**. Scheduled occurrences enter the same bounded admission and execution engine as `RunOffer`, including active/pending limits, admission wait timeout, terminal timeout/cancellation mapping, and capacity telemetry. Overlapping `allow` runs are tracked independently; `queue-one` and catch-up counts above one are rejected as unsupported instead of silently applying different behavior. Dedicated schedule-overload integration coverage is still listed as a testing gap in section 17.

Agent-scheduled occurrences must use the same admission model as externally offered runs:
- bounded active slots (maxConcurrentRuns)
- bounded pending depth (maxPendingQueueDepth)
- admission wait timeout (admissionQueueWaitTimeoutSeconds)

Expected behavior under load:
- occurrence may reach TERMINAL with timed-out/cancelled outcome if admission cannot be obtained in time.
- telemetry increments rejected-by-capacity and queue/active metrics accordingly.

## 11. Persistence Model (Agent)

Implementation status: **Done (MVP)**. All three files exist under the runtime root; definitions/checkpoints use write-then-replace persistence, occurrence JSONL entries are flushed before emission, and a monotonic sequence drives replay. Protobuf payloads are stored in binary form inside `schedules.json` to preserve oneof values across restart.

New files under agent runtime state root:
- schedules.json (current desired schedule definitions, versioned)
- schedule-occurrence-log.jsonl (append-only occurrence events)
- schedule-checkpoints.json (per-schedule high-water marks and next due cursor)

Durability requirements:
- write schedule definition atomically before ScheduleAck.
- append occurrence event before emitting equivalent TAP frame when possible.
- use monotonic sequence numbers for replay ordering.

## 12. Security and Policy

Implementation status: **Partial**. Existing authenticated TAP sessions are reused, schedule source-desktop ownership is checked on upsert, and executor/working-directory policy is validated before persistence. There is no configurable per-schedule frequency ceiling, schedule-specific command allow-list, or schedule mutation authorization layer beyond the authenticated controller/source identity.

- Reuse existing TAP session authentication and authorization.
- Only authorized controller identities may mutate schedules.
- Agent validates RunPlan executor allow-list at upsert time where possible.
- Optional policy gate: reject schedules that exceed local max frequency or forbidden commands.

## 13. Backward Compatibility and Versioning

Implementation status: **Done**. The semantic protocol is bumped to TAP 1.1, both implementations advertise `[1.1, 1.0]`, select the highest exact common version, stamp session frames with the negotiated version, and require `schedule.v1` extension negotiation on TAP 1.1. The `tap-grpc` representation remains binding version 1. Legacy TAP 1.0 agents retain desktop polling plus ordinary `RunOffer`, and upgraded agents remain idle with legacy desktops.

- Existing run-offer flow remains unchanged.
- Capability negotiation advertises scheduler extension support (`schedule.v1`, extension version 1).
- Desktop only sends schedule frames when TAP 1.1 and `schedule.v1` have both been negotiated.
- TAP semantic protocol version and binding version are independent: this change is TAP 1.1 over `tap-grpc`/1, not a `tap-grpc`/2 binding.
- Discovery and enrolment advertise TAP 1.1 first and TAP 1.0 second. A peer proposing neither receives `incompatible_protocol_version`; a peer proposing the wrong binding version receives `incompatible_binding`.
- Mixed fleet behavior:
  - upgraded desktop + legacy agent: fallback to current desktop-driven immediate dispatch behavior.
  - legacy desktop + upgraded agent: scheduler extension remains idle.

## 14. Reconciliation Protocol

Implementation status: **Partial**. Sync request/response, revision maps, tombstone revisions, per-agent occurrence cursors, ordered replay, desktop-newer republish, and conflict surfacing are implemented. Operator decision/force controls, replay pagination/batching, and automatic resolution of a newer agent definition/tombstone are not implemented.

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

Implementation status: **Partial**. The requested schedule gauges and counters are exposed through capability labels and heartbeat health details, including terminal counts by outcome. They are not first-class fields on `AgentHeartbeat`/`AgentSnapshot`, counters reset on agent restart, and no external metrics exporter is included.

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

Implementation status: **Partial**. Phases 1-3 are substantially implemented, phase 4 is incomplete, and phase 5 remains open.

Phase 1: Protocol and storage scaffolding
- Phase status: **Partial**. Protocol, capability negotiation, parser, handlers, and storage exist; the proposed feature flag/no-op staging step was skipped in favor of the working runtime.
- Add TAP frame/message definitions and capability negotiation.
- Add no-op parser/handlers behind feature flag on agent.

Phase 2: Agent local scheduler runtime
- Phase status: **Done (MVP)**.
- Persist schedule definitions.
- Tick loop + due evaluation + occurrence logging.
- Reuse existing run execution pipeline for occurrence launches.

Phase 3: Desktop integration
- Phase status: **Done (MVP)**. Deployment state/errors are surfaced on scheduler cards and persisted in scheduler schema v3.
- Map scheduler.json items to ScheduleSpec.
- Publish upserts/deletes on save operations.
- Surface per-agent schedule status and errors.

Phase 4: Reconciliation hardening
- Phase status: **Partial**. Handshake, replay, tombstones, cursors, and conflict diagnostics exist; operator resolution/force tooling does not.
- Sync handshake and replay.
- Conflict tooling and operator diagnostics.

Phase 5: GA hardening
- Phase status: **Not done**. The required fault-injection, long-disconnect soak, and signed-off compatibility matrix have not been completed.
- Fault-injection tests.
- Long-disconnect and restart resilience tests.
- Backward compatibility matrix sign-off.

## 17. Testing Strategy

Implementation status: **Partial**. Unit/regression coverage exists for protocol negotiation, deterministic publication, target catalog validation/stable ownership, fallback handoff, storage-failure rollback/retry, durable restart behavior, overlap tracking, catch-up collapse, and reconnect queue cleanup. The full desktop-agent disconnect/reconnect matrix and sustained overload coverage remain incomplete.

Protocol tests:
- Test status: **Partial**. Schedule frame serialization round-trip and desktop capability/fallback behavior are covered; cross-version wire fixtures are not.
- serialization/deserialization compatibility for new messages.
- capability negotiation and fallback behavior.

Agent tests:
- Test status: **Partial**. Upsert/delete/pause/resume, one-shot/recurring cursor behavior, durable protobuf payload restoration, tombstones, revision conflicts, and restart terminal de-duplication are covered. Schedule-specific admission overload and fault-injection cases are not.
- upsert/delete/pause/resume state transitions.
- due-trigger correctness for one-shot and recurring schedules.
- admission timeout and overload outcomes.
- restart recovery without duplicate occurrenceId terminals.

Desktop-agent integration tests:
- Test status: **Not done**. Production paths exist, but there is no automated end-to-end gRPC test that disconnects the desktop, waits for an offline occurrence, reconnects, and verifies replay/conflict handling.
- publish, disconnect desktop, allow due run, reconnect, replay evidence.
- revision conflict and forced reconciliation behavior.

## 18. Open Questions

Implementation status: **Done (decisions recorded)**. MVP decisions are documented in section 21. Structured RRULE, richer command policy, and plan-digest reconciliation remain possible future protocol revisions rather than unresolved blockers.

- Should recurrenceRule stay string-based initially or move directly to structured RRULE fields?
- Should overlapPolicy default to skip-if-running for safety?
- Is misfire coalescing required in MVP or post-MVP?
- Do we require per-schedule command allow-lists in first implementation?
- Should ScheduleSyncResponse include digest of payloadRunPlanSnapshot for drift detection?

## 19. Implementation Work Areas

Implementation status: **Done (MVP)**. Each listed work area has a production implementation; remaining hardening is tracked in sections 14-17.

Desktop:
- Work-area status: **Done (MVP)**.
- scheduler model to TAP mapping in remote dispatch/scheduler service.
- validated execution-target selector and UI status surfaces for remote schedule deployment and health.

Protocol:
- Work-area status: **Done**.
- TAP frame and message definitions for schedule control and events.
- capability labels for schedule extension.

Agent:
- Work-area status: **Done (MVP)**.
- schedule registry persistence and tick loop.
- occurrence-to-run pipeline integration.
- reconnect replay and reconciliation handlers.

## 20. Acceptance Criteria

Implementation status: **Done (MVP implementation)**. All five behaviors have production code paths and unit/regression support. The full disconnect/reconnect scenario is not yet covered by an automated end-to-end test, as called out in section 17.

- Desktop can publish schedule definitions to a connected upgraded agent.
- Agent executes due occurrences while desktop is disconnected.
- On reconnect, desktop receives durable occurrence outcomes with no duplicate terminals per occurrenceId.
- Legacy agents continue functioning with existing RunOffer flow unchanged.
- Capacity and admission behavior for scheduled occurrences is observable in telemetry and consistent with existing execution controls.

## 21. Implemented Design Notes

Implementation status: **Done**. This section records the delivered design and the decisions used by the current implementation.

The `schedule.v1` implementation defines TAP 1.1 as an additive, wire-compatible minor revision of TAP 1.0. The protobuf package remains `tsky.agent.v1` because the major version did not change. The contracts live in `protocol/tsky/agent/v1/schedule.proto`, and schedule frames use the previously unused 50-54 field range in `AgentFrame` and `ControllerFrame`. Agents advertise TAP 1.1 and TAP 1.0 plus the extension in `SessionHello.extensions` and `CapabilityReport` (`features` plus `extensions`). The controller selects the highest exact common TAP version and negotiates `schedule.v1` only for TAP 1.1 sessions. TAP 1.0 sessions continue to use the desktop poller and ordinary `RunOffer` dispatch.

Version axes are deliberately separate: this document is revision 1.2, the semantic protocol is TAP 1.1, and the required gRPC binding remains `tap-grpc` version 1. A future incompatible semantic change would require TAP 2 and a new protobuf major-version package; a representation-only binding change would bump the affected binding without necessarily changing TAP Core.

For TAP 1.1, the authentication transcript also includes `binding_version`, so the device proof covers the independently selected semantic and binding versions. TAP 1.0 retains the original transcript shape. `SessionHello` uses a TAP 1.0 envelope as the pre-negotiation compatibility baseline, then all challenge, proof, accepted, and active-session frames use the selected version.

Agent state uses the runtime-root files proposed in section 11. Schedule definitions and checkpoints use write-then-rename replacement, while occurrence evidence is appended and flushed before its TAP frame is emitted. Protobuf payloads are stored in binary form inside `schedules.json` so `RunPlan` oneof values survive restart. Occurrence identifiers are deterministic from schedule ID and planned UTC time, and terminal IDs are rebuilt from the occurrence log during startup.

The local scheduler currently supports one-shot triggers and `every:{n}m` recurrence. Defaults are `fire-once-within-window`, `skip-if-running`, and one catch-up occurrence. Missed recurring intervals collapse to the latest due occurrence for that one-occurrence limit; unsupported `queue-one` or larger catch-up requests receive `UNSUPPORTED_FEATURE`. Scheduled runs enter the same execution engine, admission queue, timeout path, policy validation, durable run store, and capacity counters as controller-offered runs. Schedule state and occurrence evidence are persisted before admission, storage failures roll state back or retain terminal evidence for retry, and overlapping `allow` executions are tracked independently. The execution context is tied to the agent process rather than an individual gRPC connection, allowing accepted and locally scheduled work to continue through a disconnect.

Desktop `scheduler.json` schema version 3 retains the effective execution policy, monotonic revision, deployment status, accepted revision, agent assignment, pending previous owner during retarget, occurrence cursor, and durable delete tombstones. Schema v3 adds the persisted retarget handoff marker; older files load with no pending handoff. On capability report the desktop requests sync, replays occurrence evidence in sequence order, republishes newer desktop definitions, and surfaces newer agent revisions/tombstones as conflicts instead of overwriting execution history. A retarget waits for the previous owner's delete acknowledgement before publishing to a different owner or enabling desktop fallback, preventing two durable owners from triggering the same schedule.

Published run plans use deterministic step identifiers, so retrying the same revision and idempotency key reproduces the same protobuf payload. The desktop keeps evaluating an agent-targeted schedule until the current revision is acknowledged; only then does ownership transfer to a capable TAP 1.1 agent. Reconnect cleanup discards unsent in-memory frames because durable run reconciliation and schedule replay are the authoritative resend paths.

Implementation decisions for the open questions in section 18:

- Recurrence remains the compatible string form for v1.
- Overlap defaults to `skip-if-running`.
- Coalesce is represented and accepted in v1; the desktop emits the safer fire-once policy by default.
- Executor and working-directory policy are validated when an upsert is received.
- Persistence checksum is returned in `ScheduleAck`; sync includes definition revisions and tombstone revisions rather than full plan digests.
