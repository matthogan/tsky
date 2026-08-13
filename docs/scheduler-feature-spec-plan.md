# Scheduler Feature Spec and Implementation Plan

_Last updated: 2026-08-13_

## 0. Completion Snapshot
- Overall status: **Complete** for the scoped Scheduler feature.
- Runtime poller and due-window execution: **Complete**.
- Scheduler authoring (create/edit, one-shot/repeat): **Complete**.
- Scheduler execution-target authoring and publication readiness/status: **Complete**.
- Scheduler run history and Scheduler-history UI details: **Complete**.
- Test coverage status:
  - **Complete**: snapshot normalization, timing/recurrence helpers, sequencing + dedup integration harness.
  - **Complete**: explicit isolated UI command-gating regression assertions for Scheduler mode.

## 1. Objective
Add a Scheduler feature that lets users schedule task snapshots and group-chain snapshots to run at a specific time as either one-shot or repeatable schedules.

Status: **Complete**.

Key constraints:
- Scheduler configuration is persisted separately from tasks.json. **Complete**
- Polling is acceptable (no strict timer-wheel/background service requirement). **Complete**
- Polling interval must be less than 60s and not less than 10s. **Complete**
- Execution uses a trigger window (for example, a scheduled `2026-07-27 12:02` should still run if polled up to 1 minute late). **Complete**
- Polling and execution orchestration run on asynchronous background threads. **Complete**
- Scheduler view appears in the left panel (below Agents), and has scheduler-specific cards. **Complete**
- Snapshot task payloads remain normalized, while scheduler schema v3 stores the effective target policy and safe-retarget handoff separately and publishes it through `schedule.v1` when supported. **Upgraded**

## 2. Implemented Features
Status: **Complete**.

- Scheduler persistence model and store in a dedicated file (`%LocalAppData%/tsky/scheduler.json`).
- Left panel Scheduler entry (below Agents) with a dedicated Scheduler main surface.
- Scheduler cards render scheduled items (snapshot metadata), not live task cards.
- Scheduler selection and delete behavior from:
  - top ribbon Delete action,
  - Delete keyboard action,
  - scheduler card context menu.
- Scheduler card context menu actions:
  - `Edit...`
  - `Enable`/`Disable`
  - `Delete`
- Ribbon gating in Scheduler view:
  - Disabled: Add, Edit, Run Chain, Groups drop-down, Share, Import, Task/Graph toggle.
  - Conditional: Enable/Disable is enabled when a scheduled item is selected and toggles `isEnabled` in scheduler configuration.
  - Enabled: Delete, History, Listener.
- Bottom tabs in Scheduler view restricted to:
  - Task Output
  - History
- Schedule entry points:
  - Task card ellipsis menu: `Schedule...`
  - Group header context menu: `Schedule Group Chain...`
- Schedule authoring now uses the scheduler editor dialog (not scaffold defaults) for:
  - one-shot vs repeatable,
  - local date/time,
  - recurrence minutes,
  - run-window seconds,
  - enabled state,
  - Local, Automatic, named-agent, or discovered-pool execution target,
  - target validity and current `schedule.v1` publication/fallback status.

## 3. Persistence Model
Status: **Complete**.

Scheduler config file:
- Path: `%LocalAppData%/tsky/scheduler.json`
- Root type: `SchedulerConfiguration`
- Fields:
  - `schemaVersion`
  - `pollIntervalSeconds`
  - `desktopInstanceId`
  - `agentOccurrenceCursors{}`
  - `items[]`
  - `deletedSchedules[]`

Scheduled item (`ScheduledItem`) fields:
- `id`
- `displayName`
- `groupName`
- `targetKind`: `TaskSnapshot` | `GroupChainSnapshot`
- `triggerAtUtc`
- `isRecurring`
- `recurrenceExpression` (reserved for repeat rules)
- `triggerWindowSeconds`
- `isEnabled`
- `isRunning`
- `createdAtUtc`
- `updatedAtUtc`
- `revision`
- `executionTargetMode`, `targetAgentId`, `targetPoolId`
- `publishedAgentId`, `pendingPreviousAgentId`, `acceptedRevision`
- `remoteDeploymentState`, `remoteDeploymentMessage`
- `lastAcknowledgedOccurrenceSequence`
- `lastTriggeredAtUtc`
- `lastCompletedAtUtc`
- `lastOccurrenceToken`
- `nextRunAtUtc`
- `lastRunOutcome`
- `lastRunMessage`
- `sourceTaskName`
- `taskSnapshot`
- `chainSnapshot[]`

Scheduler run history file:
- Path: `%LocalAppData%/tsky/scheduler-run-history.json`
- Root type: `List<SchedulerRunHistoryEntry>`
- Captured fields include:
  - scheduled item identity and display name,
  - occurrence timestamp,
  - start/completion timestamps,
  - outcome and optional exit code,
  - message,
  - snapshot task count.

Snapshot and execution-policy rule:
- Embedded task snapshots remain normalized to avoid coupling a payload to mutable task targeting fields:
  - `executionTargetMode = local`
  - `targetAgentId = null`
  - `targetPoolId = null`
- The scheduled item stores the effective target separately. Upgraded agents receive a durable immutable `RunPlan`; legacy agents use the desktop poller and ordinary remote dispatch.

## 4. Polling and Trigger Semantics
Status: **Complete**.

- Dedicated scheduler polling loop starts during desktop startup and stops on shutdown.
- Poll interval is normalized to `[10s, 59s]` (default `30s`).
- Each poll evaluates due items using trigger-window tolerance.
- The desktop poller skips definitions acknowledged by a `schedule.v1` agent; those definitions continue from the agent runtime while desktop is disconnected.
- One-shot items:
  - execute once when due,
  - record run state,
  - disable after completion.
- Repeatable items:
  - parse recurrence expression (`every:{n}m`),
  - compute due occurrence,
  - compute and persist next run.
- Dedup guard:
  - occurrence token per item (`id + occurrenceUtc`),
  - persisted last token + in-memory ledger to avoid duplicate launches in overlapping polls.
- Startup recovery:
  - stale persisted `isRunning` flags are reset on app startup.

## 5. Execution Flow
Status: **Complete**.

When due item is triggered:
- Task snapshot:
  - execute the persisted task snapshot via existing task execution pipeline.
- Group chain snapshot:
  - execute snapshot tasks in saved order via existing chain flow.
- Output/history behavior:
  - append task output to bottom Task Output tab.
  - create scheduler execution history records.
  - visually reflect active/running/completed state on scheduler card.

## 6. UI/UX Behavior
Status: **Complete**.

Main surface behavior:
- Tasks view: unchanged.
- Agents view: unchanged.
- Scheduler view:
  - cards represent scheduled items (not tasks).
  - cards display snapshot type, trigger/next-run timestamp, run-window metadata, enabled state, and last-run status.
  - entering Scheduler view preserves the current left-panel width (no forced resize back to persisted default while visible).

Command behavior in Scheduler view:
- Delete removes selected scheduled item.
- Enable/Disable toggles the selected scheduled item's `isEnabled` state and persists to scheduler configuration.
- History and Listener remain available.
- Other top ribbon commands remain disabled.

Context menu behavior:
- Task card menu contains `Schedule...`.
- Group header right-click menu contains `Schedule Group Chain...`.
- Scheduled-item card menu contains `Edit...`, `Enable`/`Disable`, and `Delete`.

## 7. Next Implementation Steps
Status: **Complete**.

1. [x] Add scheduler edit/create dialog:
- choose one-shot vs repeat,
- configure local date/time,
- configure recurrence and run-window,
- enable/disable scheduled item,
- choose and validate Local, Automatic, named-agent, or pool execution,
- show publication readiness and persisted deployment status.

2. [x] Add scheduler runtime state fields:
- `lastTriggeredAtUtc`,
- `lastRunOutcome`,
- `nextRunAtUtc` materialization.

3. [x] Add asynchronous poller service:
- lifecycle: start on window load, stop on shutdown,
- interval and dedup guards,
- resilient error handling and logging.

4. [x] Add scheduler run history store:
- independent from task run history,
- include occurrence timestamp and snapshot id,
- bind to Scheduler view History tab.

5. [x] Add tests:
- [x] due-window execution checks,
- [x] recurrence occurrence calculations,
- [x] dedup behavior across polls,
- [x] scheduler card/view mode command gating regressions.
- [x] execution-target catalog, WPF selector, invalid-target, and safe-retarget fallback regressions.

## 8. Risks and Mitigations
Status: **Tracked and mitigated for current scope**.

Risk: duplicate execution on overlapping polls.
- Mitigation: occurrence-level dedup key, last occurrence token, and in-memory occurrence ledger.

Status: **Mitigated**.

Risk: stale snapshot drift from edited source tasks.
- Mitigation: intentional snapshot semantics; card should indicate it is a snapshot captured at schedule time.

Status: **Accepted by design**.

Risk: confusion between task history and scheduler history during transition.
- Mitigation: dedicated scheduler history records and Scheduler-specific history details in History tab.

Status: **Mitigated**.

## 9. Acceptance Criteria for Full Feature
- [x] User can schedule task/group snapshots with one-shot and repeat rules.
- [x] Schedule persists outside tasks.json and survives restart.
- [x] Poller runs async with interval in [10s, 59s].
- [x] Trigger window executes missed polls up to configured tolerance (default 60s).
- [x] Scheduler-triggered runs are visible in Scheduler view and Task Output.
- [x] Scheduler view command restrictions match spec.

Overall acceptance status: **Met**.

## 10. Remaining Follow-up (Optional)
- Richer scheduler-card deployment diagnostics and operator controls for resolving a newer agent-side revision can be added without changing the v1 protocol.
