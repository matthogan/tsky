# Engineering Assessment

Reviewed: 2026-07-13

## Scope

This assessment reviews the current repository as a Windows desktop task launcher built with .NET 8 WPF. The review is based on static inspection of the project files, representative source paths, documentation, and release workflow.

No source code changes were made as part of this assessment. A local build could not be run in this environment because `dotnet` was not available on `PATH`.

## Executive Summary

The application has a clear product direction and a useful set of task-launcher capabilities: grouped tasks, user/workspace configuration merging, rituals, dataflow ports, command history, import/share flows, and a GitHub release workflow. The codebase also has several good extraction points already, especially around configuration, process start info creation, dataflow resolution, identity handling, and persistent UI/history stores.

The main engineering risk is that most orchestration still lives in `MainWindow.xaml.cs`, which is over 6,000 lines and mixes UI rendering, command execution, persistence, validation, dataflow presentation, history management, import/export, task editing, keyboard navigation, and animation state. That makes the system harder to test, reason about, and safely extend.

The second major risk is lack of automated verification. The project currently has no test project, no CI build/test workflow for pull requests, and no guardrail around high-value behavior such as task merging, dataflow resolution, command-line construction, and JSON import/save semantics.

## Strengths

- The project is small enough to improve incrementally without a large migration.
- `tsky.csproj` uses modern defaults: .NET 8, nullable reference types, implicit usings, deterministic builds, and WPF.
- The domain model is explicit. `LauncherTask`, `RitualDefinition`, `TaskRunHistoryEntry`, and related types are easy to understand.
- Configuration loading supports both legacy flat `tasks` and grouped `groups` formats, then normalizes into a single in-memory model.
- `TaskProcessStartInfoFactory` is a good boundary for runner-specific process configuration.
- `DataflowPortResolver` is already separated from WPF and is a strong candidate for unit tests.
- The release workflow publishes `win-x64` and `win-arm64` assets from version tags.
- User-facing output/validation logs show care for diagnosability.

## Key Recommendations

### 1. Split `MainWindow.xaml.cs` Into Application Services

Status: Implemented (phase 2) on 2026-07-13.

Completed in this phase:

- `TaskExecutionService` extracted from `MainWindow.xaml.cs` process-launch/execution internals.
- `RunHistoryService` extracted from `MainWindow.xaml.cs` run snapshot lifecycle, retention trimming, and persistence.
- `TaskCatalogService` extracted for task indexing, enable/disable, delete, reorder, identity checks, and group dataflow lock checks.
- `ImportExportService` extracted for share serialization and import merge/persist operations.
- `DataflowGraphService` extracted for binding parsing and non-UI graph validation logic.
- Connector computation/model-building extracted into `DataflowGraphService`, while WPF drawing/interaction remains in `MainWindow.xaml.cs`.
- Connector relation traversal/highlight graph selection (upstream/downstream set computation) extracted into `DataflowGraphService`; `MainWindow.xaml.cs` now applies styles only.
- Connector tooltip text composition and connector status text formatting extracted into `DataflowGraphService`; `MainWindow.xaml.cs` retains Path creation and event hookup.
- `MainWindow.xaml.cs` rewired to orchestrate UI state while delegating execution/history responsibilities to services.
  
Additional phase-2 rewiring completed:

- `MainWindow.xaml.cs` now delegates catalog operations, import/export merge flow, and dataflow binding validation/parsing to service boundaries.

Remaining scope for full recommendation completion:

- Continue moving stateful UI orchestration to view models incrementally.
- Continue extracting connector rendering and remaining non-UI catalog/import concerns from `MainWindow.xaml.cs`.

`MainWindow.xaml.cs` currently owns too many responsibilities. It contains large state dictionaries for groups, outputs, selection, history, connector visuals, drag/drop, running overlays, and dataflow UI; it also executes processes and writes configuration/history files.

Recommended extraction order:

- `TaskExecutionService`: run a `LauncherTask`, handle output capture, elevation, cancellation, exit-code policy, and published outputs.
- `TaskCatalogService`: load, merge, save, reorder, add, edit, delete, enable/disable tasks and rituals.
- `RunHistoryService`: own history append, completion, trimming, replay snapshot conversion, and persistence.
- `ImportExportService`: own clipboard/file import/export and merge rules.
- `DataflowGraphService`: resolve bindings, validate graph order, compute connector models.
- View models for groups, tasks, output tabs, history rows, port editor rows, and rituals.

Keep the first extraction narrow: move logic that already has few WPF dependencies, such as history and task execution, before attempting broader MVVM changes.

### 2. Add Automated Tests Around Existing Pure Logic

The highest-return tests do not require UI automation:

- `TaskConfiguration`: legacy format normalization, grouped format round-trip, overlay precedence, duplicate task identity handling, ritual normalization, invalid JSON behavior.
- `TaskIdentity`: case-insensitive group/name identity behavior.
- `TaskProcessStartInfoFactory`: `exe`, `cmd`, `powershell`, quoted executable paths, working directory resolution, environment placeholder expansion, invalid runners.
- `DataflowPortResolver`: input defaults, bindings, shared ports, task output references, runtime token deferral, unresolved placeholders.
- `RunHistoryService` after extraction: start/complete lifecycle, replay snapshots, trimming to `MaxRunHistoryEntries`.
- Import/merge behavior: imported tasks versus existing user-profile tasks.

Add a test project such as `tsky.Tests` and run it from CI. This would immediately reduce risk while refactoring the large window class.

### 3. Make JSON Loading and Validation Explicit

`JsonFileStorage.LoadOrDefault<T>` catches all exceptions and returns a new default object. That makes corrupted, unreadable, or malformed JSON indistinguishable from an intentionally empty config. `MainWindow.LoadConfigurationWithValidation` can therefore report a successful load even when the storage layer silently fell back.

Recommended changes:

- Replace silent fallback with a result type carrying `Value`, `Succeeded`, `Error`, and `WasMissing`.
- Treat missing files, empty files, invalid JSON, and semantically invalid configuration as distinct cases.
- Keep backup/reset behavior explicit and visible to the user.
- Add schema/business validation beyond deserialization: duplicate names, empty command lines, invalid runners, invalid bindings, duplicate output port names, impossible ritual references.

### 4. Use Atomic Persistence for User Data

Status: Implemented (phase 2) on 2026-07-13.

Completed in this phase:

- `JsonFileStorage.Save` now writes to a temp file, flushes to disk, and atomically replaces the target file.
- User profile import merge no longer overwrites with direct `File.Copy`; it now persists through atomic `TaskConfiguration.Save`.
- Atomic persistence now applies to configuration, run history, and UI state stores through shared storage helpers.
- Cross-instance file locking added around JSON load/save operations to avoid concurrent writes from multiple app instances.
- Bounded backup history is now enforced per persisted file target.
- App shutdown now explicitly releases any remaining lock handles.

Current save paths use direct `File.WriteAllText` and import uses `File.Copy(..., overwrite: true)`. A process crash, power loss, disk error, antivirus interference, or concurrent app instance can leave user configuration/history partially written.

Recommended approach:

- Write to a temp file in the same directory.
- Flush and close the temp file.
- Replace the target with `File.Replace` when possible, or `File.Move` after deleting only once the temp file is valid.
- Keep a bounded backup history for `tasks.json`, run history, and UI state.
- Consider a simple process-wide or file lock to prevent two app instances from writing at the same time.

### 5. Improve Task Execution Control

Status: Implemented on 2026-07-13.

Completed in this phase:

- Added per-task cancellation support with Escape-triggered cancellation for the selected running task.
- Added task-level timeout and cancellation policy fields (`timeoutSeconds`, `killProcessTreeOnCancel`).
- Added incremental stdout/stderr streaming into run output tabs during execution.
- Added configurable per-stream output capture caps (`maxCapturedOutputChars`) with truncation notices.
- Added distinct `Started` outcome for fire-and-forget runs.
- Expanded run outcome taxonomy to track `LaunchFailed`, `TimedOut`, `Cancelled`, `FailedNonZeroExit`, and `IgnoredNonZeroExit`.
- Updated history persistence and replay snapshots to include new execution-control fields.

### 6. Harden Trust and Secret Handling

This application intentionally runs arbitrary local commands, which is powerful but high-risk.

Recommended controls:

- Show clear trust prompts for imported tasks before first run, especially tasks requiring elevation.
- Redact likely secret environment variable names in output, history, previews, and share payloads.
- Avoid writing sensitive environment values into run history unless the user opts in.
- Consider a per-task "trusted source" marker for imported configurations.
- Reconsider defaulting PowerShell to `-ExecutionPolicy Bypass`; if kept, surface that clearly in documentation and UI.

### 7. Clarify Dataflow Semantics

The current dataflow implementation is promising, but some semantics will become hard to maintain without stricter rules:

- Shared ports use a global name map, so unrelated outputs with the same name can collide.
- Unresolved placeholders are replaced with empty strings, which can hide broken bindings.
- `TaskName.outputName` references split on the first dot, making task names with dots ambiguous.
- Dataflow validation is partly in UI code and partly in resolver behavior.

Recommended direction:

- Define a formal reference grammar and reject ambiguous references.
- Treat unresolved required bindings as validation errors before execution.
- Consider namespacing shared ports by group or chain unless global ports are intentional.
- Move graph validation into a testable non-WPF component.

### 8. Introduce a Schema Version and JSON Schema

Status: Implemented on 2026-07-13.

Completed in this phase:

- Added top-level `schemaVersion` to persisted task configuration files.
- Added legacy compatibility for files without `schemaVersion`.
- Added schema validation in `TaskConfiguration.Load` and reset validation paths.
- Added `docs/tasks.schema.json` as the canonical JSON Schema document.
- Added unit tests for schema version persistence, legacy load compatibility, and invalid schema rejection.

### 9. Finish History Retention

Status: Implemented on 2026-07-13.

Completed in this phase:

- Retention trimming is now enforced in `RunHistoryService` on every save path.
- History completion writes (`CompleteEntry`) also pass through retention trimming before persistence.
- Added unit tests verifying only the newest configured entries are retained.

### 10. Improve Editor Consistency

The typed task form exposes common fields, while advanced fields such as `waitForExit`, `isEnabled`, inputs, bindings, outputs, and import metadata are handled elsewhere or through raw JSON editing. This can make features feel inconsistent and increases support/debug cost.

Recommended path:

- Make the typed editor cover all user-editable fields.
- Keep raw JSON editing as an advanced escape hatch.
- Prevent users from editing system-owned metadata unless explicitly intended.
- Share validation between typed forms and JSON editing.

### 11. Build CI Before Expanding Features

The release workflow builds tagged releases, but there is no visible pull-request or main-branch CI workflow. Add a separate workflow that runs on pull requests and pushes:

- `dotnet restore`
- `dotnet build --configuration Release`
- `dotnet test --configuration Release`
- Optional formatting/analyzer checks once the codebase is ready

For releases, consider adding:

- Artifact checksums.
- Signing or documented unsigned-app behavior.
- A clean generated-artifact policy. The working tree currently contains an untracked `*_wpftmp.csproj` file pattern; if that is generated locally, add an ignore rule for it.

### 12. Plan a Gradual UI Architecture Cleanup

`MainWindow.xaml` and `MainWindow.xaml.cs` contain a large amount of custom UI construction and state synchronization. A full rewrite is not necessary, but future UI work will be easier if state moves toward data binding.

Recommended sequence:

- Extract data models/view models for displayed groups, task cards, history rows, output tabs, and port rows.
- Move repeated styles/templates out of the main XAML file into resource dictionaries.
- Replace imperative refresh methods with observable collections where practical.
- Add UI automation smoke tests only after key logic is under unit test.

## Suggested Roadmap

### Near Term

- Add `tsky.Tests` and cover `TaskConfiguration`, `TaskIdentity`, `TaskProcessStartInfoFactory`, and `DataflowPortResolver`.
- Add CI for build and tests.
- Make JSON load failures explicit instead of silently returning default configs.
- Call history trimming before saving history.
- Document the current config schema and import precedence.

### Medium Term

- Extract `TaskExecutionService`, `RunHistoryService`, and `ImportExportService`.
- Add cancellation, timeouts, output streaming, and clearer task outcomes.
- Implement atomic saves for config, history, and UI state.
- Add schema versioning and JSON Schema validation.

### Longer Term

- Move toward MVVM for the main window.
- Split large XAML resources into dictionaries.
- Add accessibility review and UI automation smoke tests.
- Add release signing/checksums and stronger trust UX around imported/elevated tasks.

## Verification Notes

- Static inspection completed.
- `dotnet build tsky.csproj --no-restore` could not be run because `dotnet` was not installed or not on `PATH` in this environment.
- The repository had pre-existing modified and untracked files before this document was created. This assessment does not attempt to classify those changes.
