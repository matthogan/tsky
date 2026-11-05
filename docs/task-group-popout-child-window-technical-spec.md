# Task Group Pop-Out Child Window Technical Spec

Status: Proposed
Revision: 0.1
Date: 2026-11-05
Owners: Desktop (WPF) team
Scope: `src/Tsky.Desktop` task-group surface

## 1. Executive Summary

Introduce a non-modal child window that can host the selected task group surface (Task or Graph view) with near-identical behavior to the main surface.

The child window is opened from a new icon button in the main group header area (right-aligned near the group name/metadata section). The window includes a compact top bar with:

- Run Chain
- Task/Graph toggle
- Close

Main and child surfaces must remain visually and behaviorally synchronized for shared state (selected task, run state, enabled state, active view mode, task list changes).

The preferred implementation is a minimal modular extraction of existing surface logic so both windows reuse the same code path rather than duplicating rendering and command behavior.

## 2. Problem Statement

Current behavior keeps task-group interaction inside `MainWindow` only. Users cannot keep one group visible while working in another group or another pane. This limits multi-tasking and cross-group monitoring.

The app already contains rich task interactions (task cards, graph mode, menus, run actions, status visuals) in `MainWindow`. Copying this behavior into a second window directly would create high maintenance cost and regression risk.

## 3. Goals

- Enable opening the selected group in a non-modal child window.
- Preserve the same task/group operations and semantics as the main surface.
- Keep visual state synchronized between main and child surfaces.
- Reuse existing rendering and command logic as much as possible.
- Keep V1 architecture small, testable, and low-risk.

## 4. Non-Goals

- No change to task configuration schema (`tasks.json`) for V1.
- No new execution engine or run orchestration model.
- No separate state persistence for child window layout in V1.
- No full MVVM rewrite of `MainWindow` in this change.
- No multi-process or cross-dispatcher UI model.

## 5. UX Requirements

### 5.1 Pop-Out Trigger in Main Group Header

Add a new icon button in the main task group header area where `GroupTitleText` and `GroupMetaText` are shown.

Placement:

- Right side of header row (aligned to top-right of the group title area).

Behavior:

- Visible when a concrete group is selected.
- Disabled for non-group modes (Agents, Scheduler, Templates).
- Tooltip: `Open group in window`.
- Accessibility name: `Open selected group in window`.

Icon:

- Use an existing app icon style (`Segoe MDL2 Assets`) with an open/new-window glyph (for example `E8A7`) or equivalent visual matching the provided reference icon.

### 5.2 Child Window

The child window is modeless (`Show()`), closeable, and owned by the main window.

Top bar controls:

- Run Chain button (same semantics as `RibbonRunChainButton_Click` for the active group context).
- Task/Graph toggle (same semantics as `RibbonTaskSurfaceToggleButton_Click`).
- Standard close button.

Content area:

- Same task surface behavior as main view:
- Task card view with menus and current interactions.
- Graph view with node interactions, same selection/run semantics.
- Existing enable/disable state visuals.
- Existing running/success/failure indicators.

## 6. Functional Requirements

1. Opening pop-out creates a non-modal child window for the current selected group.
2. Closing child window does not affect group/task data or running tasks.
3. Main and child share a synchronized selected task index for the same group session.
4. Main and child share synchronized Task/Graph mode for the same group session.
5. Task state changes (running, completed, failed, enabled/disabled) propagate to both surfaces.
6. Structural changes (add/edit/delete/reorder) refresh both surfaces.
7. Run Chain in child triggers the same execution path as main.
8. Task run from either surface is reflected in both surfaces.
9. Graph selection and card selection represent the same selected task.
10. If no group is selected, pop-out open action is disabled.
11. Child window must remain non-modal and not block main window input.
12. No duplicate background schedulers or duplicate event subscriptions may be created per pop-out.

## 7. Engineering Principles Applied

### DRY

- Extract shared task-surface rendering/interaction orchestration into reusable module(s).
- Keep command execution paths centralized; child should call same action methods.

### SOLID

- Single Responsibility: separate state synchronization from view composition.
- Open/Closed: allow future additional host surfaces without changing core task-surface engine.
- Dependency Inversion: host windows depend on surface interface/adapter, not each other.

### KISS

- Keep one shared dispatcher and in-process synchronization.
- Avoid introducing event bus frameworks or broad architecture rewrites.

### YAGNI

- Defer advanced window docking/layout persistence and multi-owner distributed syncing.

## 8. Proposed Minimal Manageable Architecture

### 8.1 Design Summary

Use a shared `TaskGroupSurfaceSession` and a reusable `TaskSurfaceHost` control/adaptor. Both main and child windows bind to the same session instance for a selected group.

This is incremental: preserve most existing `MainWindow` logic, then extract only the task-surface-specific parts needed for dual hosting.

### 8.2 New Components

### A. `TaskGroupSurfaceSession` (new)

Responsibility:

- Shared in-memory state for one group surface session.

Suggested fields/events:

- `GroupName`
- `TaskSurfaceViewMode`
- `SelectedTaskIndex`
- `VersionStamp` (increment on mutations for cheap invalidation)
- `StateChanged` event with origin token

Notes:

- Origin token prevents event loops (`Main`, `Child`, `System`).
- No persistence required in V1.

### B. `TaskSurfaceHost` (new UserControl or composition helper)

Responsibility:

- Encapsulate surface visuals and interaction wiring currently split across:
- Task card rendering
- Graph rendering
- Selection visuals
- Surface mode visibility

The host exposes callbacks/commands to invoke existing behavior:

- Run task
- Run chain
- Edit/delete/toggle enable
- Open menus/editors

### C. `TaskGroupChildWindow` (new)

Responsibility:

- Own child chrome/top bar and host one `TaskSurfaceHost`.
- Subscribe/unsubscribe to session updates.
- Forward toolbar actions to shared command handlers.

### D. `TaskGroupPopoutManager` (new lightweight manager)

Responsibility:

- Open/activate/close child window for selected group.
- Track one active pop-out window in V1.

V1 constraint (intentional):

- Single active pop-out window at a time.

Rationale:

- Lowest regression risk and simplest synchronization semantics.
- Can be extended later to one-window-per-group by changing manager policy only.

### 8.3 Reuse of Existing `MainWindow` Code

Target existing code paths to reuse:

- `RenderSelectedGroupTasks()`
- `ApplyTaskSurfaceModeVisibility()`
- `RenderGraphView()`
- `RibbonRunChainButton_Click` execution branch (factored into callable method)
- Selection helpers and task action handlers

Refactoring approach:

- Step 1: factor code-behind methods into `TaskSurfaceHostController` methods without behavior changes.
- Step 2: wire main surface to controller.
- Step 3: host same controller contract in child window.

## 9. Synchronization Model

### 9.1 Shared State

Synchronized across both surfaces:

- Current group identity
- View mode (`Task`/`Graph`)
- Selected task index
- Task visual state derived from shared task data

### 9.2 Update Triggers

Events that trigger both surfaces to refresh:

- Task add/edit/delete/copy/paste/reorder
- Enable/disable changes
- Run lifecycle updates
- Group selection change (if child is pinned to selected group session in V1)
- View toggle change

### 9.3 Loop Prevention

Each state mutation includes an origin marker.
Listeners ignore no-op updates or their own origin when appropriate.

## 10. UI Structure Changes

### 10.1 `MainWindow.xaml`

- Replace current simple header stack for group title/meta with a small grid.
- Left/center region keeps existing title/meta/empty-state content.
- Right region hosts the pop-out icon button.

### 10.2 New Child Window XAML

`TaskGroupChildWindow.xaml` includes:

- Top command bar (`Run Chain`, `Task/Graph`, `Close`).
- Surface host container.

## 11. Behavior Details

### Open Behavior

- Clicking pop-out icon opens child window if absent.
- If already open, activate/focus existing window.

### Close Behavior

- Closing child only detaches view; no data mutation.
- Main continues as authoritative shell.

### Run Behavior

- Running from either surface uses same execution path and updates shared state.

### Mode Toggle Behavior

- Toggle in either window changes session mode and both windows update.

### Selection Behavior

- Selecting task/node in either surface updates both.

## 12. Error Handling and Edge Cases

- If selected group is deleted while child is open, child shows an empty/invalid state and closes automatically, or rebinds to the next valid group per current selection policy.
- If child receives updates after close, they are ignored because all handlers are unsubscribed.
- If rendering fails in child, log error and keep main surface unaffected.

## 13. Performance Considerations

- Avoid duplicate expensive graph recomputation when possible.
- Share graph model cache keyed by group + task version + view mode inputs.
- Keep existing debounce behavior for graph re-rendering.
- Ensure no additional scheduler timers are created in child scope.

## 14. Security and Operational Risk

Security:

- No new external interfaces.
- No change to command/process execution privileges.

Operational:

- Primary risk is UI-thread churn from dual refreshes.
- Mitigation: coalesced invalidation and diff-based refresh where practical.

## 15. Testing Strategy

### 15.1 Unit Tests

- Session sync semantics (mode/selection/origin token).
- Popout manager open/activate/close behavior.
- Controller no-op update guards.

### 15.2 UI Integration Tests

- Open pop-out, run task from child, assert reflected in main visuals.
- Toggle graph/task in child, assert main toggle label/state updates.
- Select task in graph node, assert selected card in main.
- Delete selected group while child open.

### 15.3 Manual Regression Checklist

- Main-only flows still behave identically when no child is open.
- Context menus and keyboard shortcuts remain valid.
- No memory leak after repeated open/close cycles.

### 15.4 Build Validation

- `dotnet build`
- Existing desktop test suite relevant to task surface interactions.

## 16. Rollout Plan

1. Extract minimal reusable surface controller from `MainWindow`.
2. Add pop-out icon and window manager.
3. Implement child window with top bar controls.
4. Wire shared session synchronization.
5. Add tests and run regression/build checks.
6. Gate behind temporary feature flag if needed (`EnableTaskGroupPopoutWindow`) for first release.

## 17. Alternatives Considered

### Alternative A: Duplicate MainWindow Surface Logic in Child

Rejected:

- High code duplication, high regression risk, diverging behavior over time.

### Alternative B: Full MVVM Refactor Before Feature

Rejected for V1:

- Too large in scope and risk for a targeted feature.

### Alternative C: Single Window with Detachable Panel/Docking Framework

Rejected for V1:

- Adds dependency/complexity with no immediate value over modeless child window.

## 18. Acceptance Criteria

- Pop-out button appears in group header and opens modeless child window.
- Child exposes Run Chain and Task/Graph controls in top bar.
- Main and child remain in sync for mode, selection, and task state.
- Running actions from either surface produce identical behavior.
- Closing child does not impact running tasks or main behavior.
- Build/tests pass without introducing regressions in existing task/graph flows.

## 19. Out of Scope for V1 Follow-Up

- Multiple concurrent pop-out windows.
- Persisted pop-out geometry and monitor placement.
- Independent child-specific filters/sort modes.
- Child-local output/history panes with independent docking behavior.
