# Graph View Technical Spec

## Summary

Add a Graph view as an alternative presentation of the existing selected-group task cards view. Users can switch between the current Task view and the new Graph view with a command button in the menu bar. Application behavior outside the visual task surface remains unchanged.

In Task view, the app continues to show the existing task cards. In Graph view, each visible task is shown as a node. The node label is the task name. The task description is exposed as the node tooltip.

The graph is a view-mode change, not a new task execution model. Running tasks, editing tasks, deleting tasks, enabling/disabling tasks, history, import/export, dataflow resolution, group selection, output panes, keyboard behavior where applicable, and persistence behavior should continue to follow the existing app semantics.

## Goals

- Provide a graph-oriented alternative to the current task-card layout.
- Preserve existing task behavior and workflows.
- Let users switch views with a single menu-bar command button.
- Reuse existing task selection, command execution, task state, and dataflow concepts.
- Keep the first implementation intentionally small: task names as node labels and descriptions as tooltips.

## Non-Goals

- Do not introduce a new task dependency engine.
- Do not change task execution order.
- Do not change dataflow placeholder resolution.
- Do not replace the current Task view.
- Do not require users to configure graph coordinates manually.
- Do not add graph editing, node linking, edge creation, or drag-to-connect behavior in the initial version.
- Do not change the persisted task configuration schema for the initial version.

## User Experience

### View Toggle

Add a command button in the menu bar.

- When the current view is Task view, the button text is `Graph`.
- When the current view is Graph view, the button text is `Task`.
- Clicking the button toggles the selected group surface between Task view and Graph view.
- The button should be disabled when no group is selected, matching existing group-dependent view commands.

Suggested placement:

- Near the existing `View` menu controls, or as a top-level menu-bar command near other view-oriented actions.
- The control should be visually consistent with existing menu-bar command styling.

### Task View

Task view is the current behavior:

- Existing cards are rendered.
- Existing card commands and context menus are preserved.
- Existing dataflow connector rendering remains unchanged.
- Existing drag/reorder behavior remains unchanged.

### Graph View

Graph view renders the currently selected group's visible tasks as graph nodes.

Node behavior:

- Node label: `LauncherTask.Name`.
- Node tooltip: `LauncherTask.Description`, when present.
- Empty or missing descriptions should result in no tooltip or a simple default tooltip using the task name.
- Disabled tasks should remain visible and should visually communicate disabled state using the same semantic state as Task view.
- Running/succeeded/failed state should be reflected consistently with Task view where practical.
- Selecting a node should select the corresponding task.
- Double-clicking a node, pressing Enter when a node is selected, or using the existing run command should run the corresponding task using the existing execution path.
- Right-click/context menu behavior should match task cards as closely as practical.

Edge behavior:

- If a group has dataflow bindings, Graph view should display directed edges from producer tasks to consumer tasks where the relationship can be resolved from existing task output and input binding metadata.
- Edges are visual only. They do not change execution order.
- For ambiguous or unresolved bindings, show no edge or an unresolved-style edge if the existing dataflow graph service can provide that distinction.
- Groups with no dataflow bindings should still show nodes, either with no edges or with an optional simple sequence layout. The initial implementation should prefer no synthetic edges unless the product explicitly wants order edges.

### Layout

Initial layout should be automatic and deterministic.

Recommended first-pass layout:

- For tasks with dataflow relationships, use a left-to-right layered layout based on upstream/downstream relationships.
- For tasks without dataflow relationships, use a simple grid or flow layout preserving current visible task order.
- Keep layout deterministic for the same group and task order.
- Avoid requiring persisted positions in the first version.

Viewport behavior:

- Graph view should use the same main content area as Task view.
- Support pan and zoom only if it can be implemented cleanly. They are optional for the first version.
- At minimum, nodes must fit in the available scrollable surface and remain readable.
- The bottom output/history pane and sidebar behavior should remain unchanged.

## Functional Requirements

1. The app stores an in-memory view mode with at least two values: `Task` and `Graph`.
2. The menu-bar toggle switches between `Task` and `Graph` mode.
3. The toggle text is `Graph` while in Task mode.
4. The toggle text is `Task` while in Graph mode.
5. Switching view modes does not change the selected group.
6. Switching view modes should preserve the selected task where possible.
7. In Graph mode, every visible task in the selected group is represented by one node.
8. Node labels use task names.
9. Node tooltips use task descriptions.
10. Running a task from Graph mode uses the same execution path as running from Task mode.
11. Editing, deleting, enabling/disabling, replaying, and running chains should preserve existing behavior.
12. Import/export, output tabs, history, and dataflow resolution behavior remain unchanged.
13. Returning to Task mode restores the existing card layout and connectors.
14. The feature should not modify the task JSON schema in its initial version.

## Recommended Architecture

### View Mode State

Introduce a small enum:

```csharp
private enum TaskSurfaceViewMode
{
    Task,
    Graph
}
```

The state can initially live in `MainWindow` beside existing UI state. If persisted view mode is desired later, add it to `UiState`; persistence is not required for the first implementation unless product behavior asks for it.

### Rendering Boundary

Keep a single "selected group surface" concept and branch at render time:

- `RenderSelectedGroupTasks()` continues to prepare selected group task data.
- It delegates to either `RenderTaskCardView(...)` or `RenderGraphView(...)`.
- Task view keeps existing card rendering.
- Graph view renders into a dedicated graph panel/canvas within the same workspace surface.

Avoid duplicating business behavior. Node click handlers should call the same command methods used by task cards.

### Graph Model

Create a UI-neutral graph model before rendering WPF elements:

```csharp
internal sealed record TaskGraphNode(
    int TaskIndex,
    string TaskName,
    string? Description,
    bool IsEnabled);

internal sealed record TaskGraphEdge(
    int SourceTaskIndex,
    int TargetTaskIndex,
    string? Label,
    bool IsResolved);
```

Recommended owner:

- If `DataflowGraphService` already owns binding parsing and connector model logic, extend it to produce graph edges.
- Keep WPF-specific coordinates, brushes, controls, and event wiring outside the service.

### Graph Edges From Existing Dataflow

Derive graph edges from existing metadata:

- Source task: task in the selected group that declares an output matching a downstream binding.
- Target task: task with an `InputBindings` reference to that output or port.
- Edge label: optional input/output/port name.
- Edge status: resolved versus unresolved if existing validation can distinguish it.

The implementation should avoid treating task list order as a dataflow dependency unless the task metadata says so.

### WPF Surface

Recommended XAML structure:

- Keep `TaskScrollViewer`.
- Keep `TaskWorkspaceSurface`.
- Add a graph host panel, such as `Canvas x:Name="TaskGraphCanvas"` or `Grid x:Name="TaskGraphHost"`.
- In Task mode, show `TaskPanel` and `TaskConnectorOverlay`.
- In Graph mode, hide `TaskPanel` and `TaskConnectorOverlay`, then show the graph host.

This approach keeps the toggle scoped to the task surface and minimizes effects on sidebar/history/output layout.

## Interaction Details

### Selection

- Node click updates `_selectedTaskIndex` using the existing selected task mechanism.
- Existing bottom-pane synchronization should run after selection changes.
- Selected graph node should have a visible selected style.

### Execution

- Node double-click should run the task if enabled.
- Existing keyboard run behavior should continue to work for the selected task in Graph mode.
- Disabled tasks should not run and should display the same user feedback as Task view.

### Context Menu

Graph nodes should expose the same task actions as cards where practical:

- Run
- Run chain
- Replay last snapshot
- Edit
- Enable/disable
- Delete
- Edit input/output ports where applicable

If the first implementation does not include the full context menu, it should at least support selection and the existing menu-bar/ribbon actions for the selected task.

### Reordering

Initial recommendation: do not support drag reordering in Graph view.

Reasons:

- Graph layout is not inherently ordered in the same way as the task card list.
- Existing dataflow-locked groups already constrain reorder behavior.
- Supporting reorder in graph mode would require clear product semantics.

Users can switch back to Task view to reorder tasks. This should be documented in release notes if implemented that way.

## Visual Design

Graph nodes should be compact and readable.

Recommended node properties:

- Fixed minimum width with text trimming for long names.
- Tooltip for full task description.
- Visual state for selected, disabled, running, succeeded, and failed.
- Consistent colors with the existing app theme.
- Directional edge arrows for dataflow relationships.

Avoid introducing a visually separate product style. Graph view should feel like another mode of the same task surface.

## Accessibility

- Nodes must be keyboard focusable.
- Node automation names should include the task name.
- Node tooltip content should not be the only access path for important information.
- Toggle button should expose a clear automation name, such as `Switch to Graph view` or `Switch to Task view`.
- Selected state should not rely on color alone.

## State and Persistence

Initial version:

- View mode can reset to Task view when the app starts.
- View mode can be global rather than per group.
- No task schema changes are required.

Optional later enhancement:

- Persist `TaskSurfaceViewMode` in `UiState`.
- Consider whether the mode should be global or per group.

## Error Handling

Graph rendering should fail gracefully:

- If graph edge construction fails, log a non-fatal message and still render task nodes.
- If a specific binding cannot be represented, skip that edge or mark it unresolved.
- The app should never block task execution because the graph view failed to draw.

## Testing Plan

### Unit Tests

- View-mode toggle state changes from Task to Graph and back.
- Toggle text resolves to `Graph` in Task mode and `Task` in Graph mode.
- Graph model contains one node per visible task.
- Node labels match task names.
- Node tooltip values match task descriptions.
- Dataflow bindings generate expected graph edges.
- Unresolved bindings do not crash graph model construction.
- Disabled task state is represented in the graph node model.

### Integration/UI Tests

- Selecting a group renders Task view by default.
- Clicking `Graph` switches to Graph view.
- Clicking `Task` switches back to Task view.
- Selecting a graph node updates the selected task.
- Running a selected node uses the same execution path as Task view.
- History and output panes still update after running from Graph view.
- Switching view modes preserves selected group and, where possible, selected task.

### Regression Tests

- Existing task-card rendering still works.
- Existing dataflow connectors in Task view still work.
- Existing drag/reorder behavior in Task view still works.
- Existing import/export, replay, and history behavior is unchanged.

## Acceptance Criteria

- A menu-bar command button toggles the selected group surface between Task and Graph views.
- The button displays `Graph` in Task mode and `Task` in Graph mode.
- Graph view displays all visible tasks in the selected group as nodes.
- Each node label is the task name.
- Each node tooltip uses the task description when available.
- Existing task actions continue to operate with the same behavior.
- Switching views does not alter task configuration or execution semantics.
- Task view remains the default and continues to behave as before.
- No task JSON schema changes are required for this feature.

## Open Questions

- Should Graph view mode persist across app launches?
- Should Graph view mode be global or remembered per group?
- Should non-dataflow groups show sequence edges, or should they show disconnected nodes?
- Should graph nodes support drag repositioning in a later version?
- Should graph layout eventually support pan/zoom for large groups?
