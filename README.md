# tsky

<img src="hero.png" alt="tsky hero" width="720" />

Tsky is a Windows desktop task launcher built with .NET 8 WPF.
It supports grouped tasks, chain execution, dataflow ports/connectors, history replay, import/share workflows, graph visualization, scheduler snapshots, agent-targeted remote dispatch, and task templates.

## Prerequisites

- Windows
- .NET 8 SDK or newer

## Run

```powershell
dotnet run --project .\src\Tsky.Desktop\Tsky.Desktop.csproj
```

## Configuration Files and Merge

At startup, the app loads and merges:

1. Workspace config: `./tasks.json`
2. User profile config: `%USERPROFILE%\tasks.json`

User profile tasks override workspace tasks when group+name identity matches.

Additional persisted app state:

- Task templates: `%LOCALAPPDATA%\tsky\templates.json`
- Scheduler config: `%LOCALAPPDATA%\tsky\scheduler.json`
- Scheduler run history: `%LOCALAPPDATA%\tsky\scheduler-run-history.json`
- Task run history: `%LOCALAPPDATA%\tsky\run-history.json`

## Task Configuration Schema

Each task supports:

- `name`
- `description`
- `group`
- `commandLine`
- `runner` (`exe`, `cmd`, `powershell`, `python`)
- `waitForExit` (default `true`)
- `ignoreExitCode` (default `false`)
- `requiresElevation` (default `false`)
- `env`
- `workingDirectory`
- `inputs`
- `inputBindings`
- `outputs`
- `isEnabled` (default `true`)

Top-level configuration supports:

- `groups`

Legacy flat `tasks` format is also accepted and normalized into grouped form.

## Runner Behavior

- `exe`: direct process launch.
- `cmd`: `cmd.exe /c "<commandLine>"`.
- `powershell`: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "<commandLine>"`.
- `python`: `python.exe <commandLine>` (falls back to `py.exe` when available).

Notes:

- `waitForExit=false` runs fire-and-forget.
- `ignoreExitCode=true` treats non-zero process exit codes as success.
- Elevated launches (`requiresElevation=true`) do not capture stdout/stderr.

## Dataflow

Dataflow allows downstream tasks to consume values from previous tasks.

Supported binding forms:

- `TaskName.outputName`
- `output:TaskName.outputName`
- `port:sharedName`

Supported placeholders in command/env/input/output values:

- `${input:name}`
- `${port:name}`
- `${output:TaskName.outputName}`

Runtime placeholders for command output in `outputs` values:

- `${stdout}`
- `${stderr}`
- `${exitCode}`

Aliases:

- `${command:stdout}`, `${command:stderr}`, `${command:exitcode}`
- `${runtime:stdout}`, `${runtime:stderr}`, `${runtime:exitcode}`

Example:

```json
{
  "name": "Compile",
  "group": "Build",
  "runner": "cmd",
  "commandLine": "dotnet build -c Release",
  "outputs": {
    "buildLog": "${stdout}",
    "buildExit": "${exitCode}"
  }
}
```

## UI Highlights

- Ribbon actions for add/edit/enable-disable/delete/run-chain/groups/history/share/import.
- Task surface mode toggle:
  - `Graph` when task-card mode is active
  - `Task` when graph mode is active
- Share menu:
  - Share Task to clipboard
  - Share Group to clipboard
  - Share Backup (Tasks)
- Import menu:
  - Import from File
  - Import from Clipboard
- Bottom pane tabs:
  - Task Output
  - History
  - Input Ports
  - Output Ports
  - Variables
  - Result Evaluator
  - Agents (dispatch)
- History supports multi-select replay and details preview.
- Sidebar and bottom pane are resizable.

## Graph View

- Graph view is an alternate presentation of the selected group task surface.
- Nodes represent visible tasks in the selected group.
- Node labels use task names; descriptions are shown as node tooltip/detail text.
- Dataflow relationships are rendered as directed edges when bindings are resolvable.
- Running/editing/deleting/enabling/replaying tasks in Graph mode keeps the same task semantics as Task mode.

## Scheduler

- Schedule snapshots from existing tasks and group chains:
  - Task card menu: `Schedule...`
  - Group menu: `Schedule Group Chain...`
- Dedicated Scheduler view in the left panel with scheduler-specific cards.
- Supports one-shot and repeatable schedules (with trigger-window tolerance).
- Scheduler persistence is isolated from `tasks.json` in `%LOCALAPPDATA%\tsky\scheduler.json`.
- Scheduler run history is persisted in `%LOCALAPPDATA%\tsky\scheduler-run-history.json`.
- Scheduler view command gating is restricted to scheduler-appropriate actions (for example, delete, enable/disable, history).
- The Scheduled Item dialog has an Execution Target selector for Local, Automatic, named agents, and discovered pools. It validates unavailable targets and shows protocol publication/fallback status before save.
- Tasks or chains with an agent, pool, or automatic execution target are published as durable `schedule.v1` definitions when the selected agent advertises support.
- Upgraded agents execute published occurrences from local durable state while the desktop is disconnected; outcomes reconcile into the scheduler state after reconnect.
- Legacy agents retain the desktop poller plus ordinary `RunOffer` behavior, so mixed fleets remain compatible.
- The current semantic protocol is TAP 1.1 with TAP 1.0 fallback; the required `tap-grpc` binding remains version 1.

## Agent Dispatch (Task View)

- In Task/Groups context, the bottom `Agents` tab lists known agents and per-agent dispatch controls.
- A selected task can be run on a specific online agent.
- Non-runnable agents are visibly non-playable based on state.
- Remote execution lifecycle markers and stream output are written to Task Output.

## Task Templates

- Dedicated Task Templates view and sidebar entry.
- Templates are non-executable definitions reused by Add Task.
- Template CRUD, copy/paste, import/share, and result evaluator editing are supported.
- Templates are persisted to `%LOCALAPPDATA%\tsky\templates.json`.

## Dataflow Connectors and Reordering

- Connectors are rendered between output and input ports in the selected group.
- Groups with dataflow wiring (`inputBindings` or `outputs`) are reorder-locked in the UI.

Disabled tasks are skipped in chains.

Schemas:

- Task schema: `docs/tasks.schema.json`

## Validation Logging

Tasks JSON reads/writes are validated in startup, import, share, and save flows.
Validation results are written to the output pane so users can see pass/fail events.

## Example Files

- `docs/tasks.json`
- `docs/tasks-import.json`
- `docs/tasks-dataflow-demo.json`

## Troubleshooting

- No tasks shown:
  - Check `./tasks.json` and `%USERPROFILE%\tasks.json` for valid JSON.
  - Review output for validation events.
- No templates shown:
  - Check `%LOCALAPPDATA%\tsky\templates.json` for valid JSON.
  - Review output for template validation/import events.
- Command fails:
  - Verify runner, quoting, and working directory.
  - Review stdout/stderr in Task Output.
- Dataflow value missing:
  - Ensure upstream task ran earlier in the same chain context.
  - Verify output names and binding references.

## Release Workflow

Release automation is defined in `.github/workflows/release.yml`.
Tag versions with a `v` prefix.

```powershell
git tag v1.0.0
git push origin v1.0.0
```
