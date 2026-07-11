# tsky

<img src="hero.png" alt="tsky hero" width="720" />

Tsky is a Windows desktop task launcher built with .NET 8 WPF.
It supports grouped tasks, chain execution, rituals, dataflow ports/connectors, history replay, and import/share workflows.

## Prerequisites

- Windows
- .NET 8 SDK or newer

## Run

```powershell
dotnet run
```

## Configuration Files and Merge

At startup, the app loads and merges:

1. Workspace config: `./tasks.json`
2. User profile config: `%USERPROFILE%\tasks.json`

User profile tasks override workspace tasks when group+name identity matches.

## Task Configuration Schema

Each task supports:

- `name`
- `description`
- `group`
- `commandLine`
- `runner` (`exe`, `cmd`, `powershell`)
- `waitForExit` (default `true`)
- `requiresElevation` (default `false`)
- `env`
- `workingDirectory`
- `inputs`
- `inputBindings`
- `outputs`
- `isEnabled` (default `true`)

Top-level configuration supports:

- `groups`
- `rituals`

Legacy flat `tasks` format is also accepted and normalized into grouped form.

## Runner Behavior

- `exe`: direct process launch.
- `cmd`: `cmd.exe /c "<commandLine>"`.
- `powershell`: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "<commandLine>"`.

Notes:

- `waitForExit=false` runs fire-and-forget.
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

- Ribbon actions for add/edit/enable-disable/delete/run-chain/groups/rituals/history/share/import.
- Share menu:
  - Share Task to clipboard
  - Share Group to clipboard
- Import menu:
  - Import from File
  - Import from Clipboard
- Bottom pane tabs:
  - Task Output
  - History
- History supports multi-select replay and details preview.
- Sidebar and bottom pane are resizable.

## Dataflow Connectors and Reordering

- Connectors are rendered between output and input ports in the selected group.
- Groups with dataflow wiring (`inputBindings` or `outputs`) are reorder-locked in the UI.

## Rituals

Rituals are named step sequences:

- `rituals[].name`
- `rituals[].description`
- `rituals[].steps[]`

Step fields:

- `taskName`
- optional `group`
- optional `delayMs`
- optional `continueOnError`

Disabled tasks are skipped in chains and rituals.

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
- Command fails:
  - Verify runner, quoting, and working directory.
  - Review stdout/stderr in Task Output.
- Dataflow value missing:
  - Ensure upstream task ran earlier in the same chain/ritual context.
  - Verify output names and binding references.

## Release Workflow

Release automation is defined in `.github/workflows/release.yml`.
Tag versions with a `v` prefix.

```powershell
git tag v1.0.0
git push origin v1.0.0
```
