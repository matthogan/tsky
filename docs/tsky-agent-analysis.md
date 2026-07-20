# tsky-agent - analysis

## Task Execution on Agents - Phase 1

### Prompt

I believe initially - as a first build to flesh out the edge cases - that since tasks run for specific custom needs, that the agents will also run in specific contexts and configuration, which leads to the conclusion that task executions should be assigned to specific agents.

This requires the user to know which agent to use for a given task. In future iterations we can enable capabilities based assignments, but it's not a goal at the moment. This is a simpler approach.

A simple approach could be to create a new tab on the bottom panel named Agents present in the groups / tasks view only. It would contain a list of Agents. The list of agents is not a simple list, but it has features. There is a play button beside each agent name and when the user clicks play the currently selected task runs on that agent. The agents will appear unplayable when they are offline. The list will reflect the current list of agents regardless of their stage or status. The execution of the task on the agent will be reported on the Task Output tab.

The agent on the agents view will reflect that it is executing or not.

### Response

**MVP Decision**
1. Task execution is explicitly agent-targeted.
2. No capability-based auto routing in this iteration.
3. User chooses the target agent at run time from a new bottom-panel tab called Agents.
4. Running a task from this tab sends output to Task Output, same as local runs.

**Agents Tab In Task View**
1. Visibility:
- Shown only in Groups and Tasks mode.
- Hidden in the dedicated Agents view.

2. Row content per agent:
- Agent display name and agent id.
- Current state badge: Online, Busy, Offline, Draining, Disabled, Revoked, Pending, Enrolling, Incompatible.
- Play button on each row.

3. Play button behavior:
- Enabled only when agent is runnable.
- Disabled for Offline, Disabled, Revoked, Pending, Enrolling, Incompatible.
- For Busy or Draining:
  - Option A: disable for MVP.
  - Option B: allow if active runs is below max concurrency.
- Recommended for first build: disable on Busy and Draining to avoid surprises.

4. Action binding:
- Clicking Play runs the currently selected task on that specific agent.
- If no task is selected, show a small inline message in the tab: Select a task first.
- If selected task is disabled, block and show: Task is disabled.

**Execution And Output Contract**
1. One remote attempt is created with a clear correlation id including task and agent.
2. Task Output tab receives:
- Start marker: TaskName on AgentName started.
- Live output stream.
- Completion marker with succeeded or failed.
3. If dispatch fails before acceptance, show immediate failure in Task Output.
4. If accepted then stream drops, show disconnected status and rely on reconciliation behavior already present.

**Agent Row Runtime Indicators**
1. Each row should indicate execution status:
- Idle
- Dispatching
- Running
- Completed
- Failed
2. This is per selected task attempt, not global workload only.
3. Keep the global state badge too, so both are visible:
- Global state from heartbeat and snapshot
- Per-attempt state from current run lifecycle

**State Gating Rules For MVP**
1. Runnable states:
- Online only
2. Not runnable states:
- Offline, Busy, Draining, Disabled, Revoked, Pending, Enrolling, Incompatible
3. If state flips to non-runnable during dispatch:
- Fail fast with explicit message in Task Output
- Keep UI consistent by resetting row from Dispatching to Failed

**Edge Cases To Handle Early**
1. No agents configured: show empty-state message in Agents tab.
2. No selected task: no-op with inline guidance.
3. Agent becomes offline between click and offer: fail with clear reason.
4. Multiple rapid clicks on same row: lock row while Dispatching.
5. User switches selected task mid-run: do not rebind running attempt; it stays tied to original task.
6. Task deleted while run in progress: keep output stream and completion handling intact.

**Minimal Acceptance Criteria**
1. Agents tab appears only in Groups and Tasks mode.
2. Every known agent is listed regardless of state.
3. Play is enabled only for runnable agents.
4. Clicking Play dispatches current selected task to that specific agent.
5. Task Output shows remote run start, stream, and completion.
6. Agent row reflects execution status transitions.
7. Failures are visible and understandable without opening debug logs.

## Implementation Checklist (MVP: Explicit Agent Selection)

Marker legend: [DONE], [PARTIAL], [TODO]

UI Layer

[DONE] Add new bottom-panel tab: Agents.

[DONE] Show tab only in Groups/Tasks mode; hide in Agents view.

[DONE] Render all known agents in a list with:

[DONE] Name + ID.

[DONE] Global state badge.

[DONE] Per-row play button.

[DONE] Disable play for non-runnable states (Offline, Busy, Draining, Disabled, Revoked, Pending, Enrolling, Incompatible).

On play click:

[DONE] Validate selected task exists and is enabled.

[DONE] Set row status to Dispatching.

[DONE] Lock that row button until dispatch result.

[DONE] Route all run output/events to Task Output tab.
Completion note: RunEvent and RunOutput frames are now routed from gateway runtime callbacks to the correlated task output panel using run/attempt tracking.

[DONE] Add inline tab message area for user-visible errors (no task selected, agent unavailable, dispatch failed).

State Layer

[DONE] Add per-agent transient execution UI state (Idle, Dispatching, Running, Completed, Failed).

[DONE] Keep this separate from global agent lifecycle state from heartbeat/snapshot.

[DONE] Add a mapping function from agent global state to runnable/non-runnable.

Reset transient state correctly on:

[DONE] Dispatch rejection.

[DONE] Stream disconnect.

[DONE] Completion events.

[DONE] Guard against double-submit on same row while Dispatching/Running.

[DONE] Keep task-agent binding immutable once dispatch starts.
Completion note: Added immutable per-attempt binding records keyed by task/attempt and explicit guardrails that reject retarget attempts with user-visible diagnostics while an attempt is active.

[DONE] Refresh row state on live registry updates without losing active dispatch tracking.

Dispatch Layer

[DONE] Implement RunSelectedTaskOnAgent(selectedTask, agentId) entrypoint.
Completion note: Added a dedicated reusable RunSelectedTaskOnAgent(...) method that centralizes task/agent validation, immutable binding guard, dispatch invocation, marker-emitting execution call, and transient-state transitions.

[DONE] Build remote offer payload from selected task config and current variables.

[DONE] Use existing remote control plane send path to target exact agentId.

[DONE] Emit deterministic correlation IDs (task + agent + attempt).

Handle outcomes:

[DONE] Offer accepted -> row Running + output start marker.

[DONE] Offer rejected/fail -> row Failed + output error marker.

[DONE] Completion -> row Completed/Failed + output completion marker.

[DONE] Disconnect during run -> row Failed (or Unknown) + reconnection/reconciliation note.
Completion note: Disconnect path now emits explicit RECONCILE status lines in Task Output and closes unresolved attempts with END state=Unknown when completion cannot be inferred; row/task state remains conservative (Failed) for safety.

[DONE] Ensure local task execution path is untouched.

Output/Observability

[DONE] Standardize Task Output markers:

[DONE] START task=<name> agent=<name/id>

[DONE] ERROR task=<name> agent=<id> reason=<...>

[DONE] END task=<name> agent=<id> state=<Succeeded|Failed|Cancelled>

Add debug logs for key actions:

[DONE] Play click intent.

[DONE] Validation failure reason.
Completion note: Added structured AgentDispatchValidation debug trace entries across validation branches with normalized task, agent, reason, and message fields (no selected task, task unavailable/disabled, agent unavailable/not eligible, immutable binding retarget blocked).

[DONE] Dispatch request/response.
Completion note: Added structured AgentDispatchRequest and AgentDispatchResponse trace records for each dispatch flow, including normalized corr/run/attempt fields plus request payload summary metadata (mode, target, pool, runner, timeout, input/output counts).

[DONE] State transitions.

Incremental Build Plan (Safe Slices)

[DONE] Slice 1: UI tab scaffold + list rendering + state badges.

[DONE] Slice 2: Play button gating + selected-task validation + inline errors.

[DONE] Slice 3: Dispatch call wiring to specific agent (no output streaming yet).

[DONE] Slice 4: Task Output integration for start/error/end markers.

[DONE] Slice 5: Row transient states + locking + completion transitions.

[DONE] Slice 6: Disconnect/reconcile edge-case handling.
Completion note: Added typed ReconciliationResponse forwarding from gateway runtime to desktop handling, reconnect-triggered reconciliation requests for pending attempts, and explicit RECONCILE status annotations for recovered terminal vs unrecovered attempts.

[DONE] Slice 7: polish (empty states, accessibility labels, telemetry/debug logs).
Completion note: Improved Agents tab empty-state and guidance copy, added automation labels/help text for tab controls and per-row dispatch controls, and normalized dispatch tracing taxonomy to `AgentDispatch.<Event>` (`Action`, `Validation`, `Request`, `Response`, `Binding`, `State`).

Done Criteria

[DONE] User can select a task and run it on one explicit agent from Agents tab.

[DONE] Non-runnable agents are visibly non-playable.

[DONE] Task Output reliably reflects remote execution lifecycle.
Completion note: Added lifecycle marker scenario tests for accepted, rejected, disconnect, and late-completion reconciliation flows, and consolidated marker generation into a shared formatter used by desktop runtime paths to guarantee expected START/ERROR/RECONCILE/END sequences.

[DONE] Row execution status updates correctly without app instability.
Completion note: Added a concurrency guardrail policy that prevents stale/terminal state flips from overriding active-running agent rows when concurrent attempts are still in flight, and added stress-oriented tests for rapid state-flip sequences and multi-agent isolation behavior.

[DONE] No regression in existing local task run behavior.
Completion note: Added a targeted local-run regression suite (`LocalTaskRunRegressionTests`) covering success output capture, non-zero exit handling (strict and ignore-exit), fire-and-forget behavior, and timeout behavior for local `cmd` execution; validated with `dotnet test .\\tsky.Tests\\tsky.Tests.csproj --filter "FullyQualifiedName~LocalTaskRunRegressionTests|FullyQualifiedName~TaskExecutionServiceTests|FullyQualifiedName~TaskProcessStartInfoFactoryTests|FullyQualifiedName~TaskVariableServiceTests"` (16/16 passed) and full-suite verification `dotnet test .\\tsky.Tests\\tsky.Tests.csproj` (46/46 passed).
