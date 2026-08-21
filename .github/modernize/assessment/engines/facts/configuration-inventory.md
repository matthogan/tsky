# Configuration & Externalized Settings Inventory

This repository uses file-based JSON/YAML configuration with layered overrides rather than framework-native appsettings/application profiles. Configuration is split between the .NET desktop app (tasks, scheduler, UI, gateway, agent registry) and the Go agent (strict `config.yaml` with environment and CLI overrides), with selective handling for sensitive values.

## Configuration Sources

| Source | Type | Path/Location | Notes |
| --- | --- | --- | --- |
| Workspace task config | JSON file | `./tasks.json` | Loaded at desktop startup; merged with user profile tasks. |
| User profile task config | JSON file | `%USERPROFILE%/tasks.json` | Overlay source; overrides workspace task definitions on identity match. |
| Task schema | JSON schema | `docs/tasks.schema.json` | Used for validation of task configuration flows. |
| Task templates | JSON file | `%LOCALAPPDATA%/tsky/templates.json` | Persisted template definitions used by Add Task flows. |
| Scheduler config | JSON file | `%LOCALAPPDATA%/tsky/scheduler.json` | Stores schedule snapshots and scheduler metadata/state. |
| Scheduler run history | JSON file | `%LOCALAPPDATA%/tsky/scheduler-run-history.json` | Stores scheduler execution history entries. |
| Task run history | JSON file | `%LOCALAPPDATA%/tsky/run-history.json` | Stores task execution snapshots and outcomes. |
| UI state | JSON file | `%LOCALAPPDATA%/tsky/ui-state.json` | Persists window/layout/favorites selections. |
| Agent gateway settings | JSON file | `%LOCALAPPDATA%/tsky/agents/gateway-settings.json` | Stores listener host/port and TLS listener configuration. |
| Agent registry | JSON file | `%LOCALAPPDATA%/tsky/agents/registry.json` | Stores controller identity, agent metadata, hashed enrolment token state. |
| Desktop debug logs | Log files | `%LOCALAPPDATA%/tsky/logs/debug-*.log` | Diagnostic output and startup/runtime traces. |
| Go agent config | YAML file | `--config <path>`; defaults include `%PROGRAMDATA%/Tsky/Agent/config.yaml`, `/etc/tsky-agent/config.yaml`, `${XDG_CONFIG_HOME}/tsky-agent/config.yaml` | Strict schema decode (`KnownFields(true)`), default/env/CLI layering. |
| Sample agent config | YAML file | `docs/config.yaml` | Example/fixture showing populated controller, identity, runtime, execution, connection, logging fields. |
| CI build/release config | GitHub workflow YAML | `.github/workflows/pr-ci.yml`, `.github/workflows/release.yml` | Defines restore/build/test/publish behavior and runtime matrix. |
| Go release metadata | GoReleaser YAML | `agent/.goreleaser.yaml` | Build/release metadata source for agent deliverables. |
| Build-wide analyzer settings | MSBuild props | `Directory.Build.props` | Enables analyzers and style enforcement at build time. |

## Build Profiles

| Profile | Activation | Purpose | Key Dependencies/Plugins |
| --- | --- | --- | --- |
| .NET Debug | `dotnet build -c Debug` (manual/default local build) | Developer iteration build configuration. | .NET SDK build pipeline; analyzers enabled via `Directory.Build.props`. |
| .NET Release | `dotnet build -c Release` (manual/CI) | Production-oriented compile/package mode used in CI/release. | .NET SDK; solution build (`Tsky.sln`), analyzer SARIF generation. |
| CI Build/Test (PR) | GitHub Actions `pr-ci` trigger + path filters | Restore/build/test with analyzer and coverage gates for changed areas. | `actions/setup-dotnet@v4`, `actions/setup-go@v5`, Coverlet (`coverlet.collector`, `coverlet.msbuild`). |
| Release Quality Gates | Tag push `v*` -> `release-quality-gates` job | Mandatory restore/build/test gates prior to packaging. | Same .NET toolchain and scripts as PR CI. |
| Release Publish Matrix | Tag push `v*` -> matrix runtime (`win-x64`, `win-arm64`) | Runtime-specific publish and artifact zip packaging. | `dotnet publish` with runtime matrix, single-file publish flags. |
| Go Agent Build/Test | CI `go-build-test` job or local `go test/go build` | Compile and verify agent module. | Go toolchain from `agent/go.mod` (`go 1.26`). |

## Runtime Profiles

| Profile | Activation Method | Config Files | Key Overrides |
| --- | --- | --- | --- |
| Desktop merged task config | Automatic at app startup | `./tasks.json` + `%USERPROFILE%/tasks.json` | User profile task entries override workspace task entries for matching group+name identity. |
| Desktop local persisted state | Automatic at app startup and save events | `%LOCALAPPDATA%/tsky/*.json` | Runtime state/config persisted independently of task definitions (`scheduler`, `templates`, `run-history`, `ui-state`, `agents`). |
| Agent defaults | Automatic when loading config | In-code defaults (`agent/internal/config/config.go`) | Baseline values for runtime/execution/connection/logging fields. |
| Agent YAML-configured | `--config <path>` or default path resolution | `config.yaml` | Structured overrides for controller/identity/runtime/execution/connection/logging. |
| Agent env-overridden | Process environment at config load | `TSKY_AGENT_*` variables + embedded `%VAR%`, `$VAR`, `${VAR}` forms in selected YAML fields | Env values can override controller URL, runtime root, log level, and silent mode; embedded substitution resolves selected path/URL fields. |
| Agent CLI-overridden | CLI flags (`--log-level`, `--runtime-root`, `--silent`) | Command-line arguments | Final override layer after defaults, YAML, and env. |

## Properties Inventory

### Desktop App (`tsky`)

| Property Key | Default | Profiles | Source |
| --- | --- | --- | --- |
| `schemaVersion` (tasks model) | `1` | Workspace/user profile task files | `src/Tsky.Desktop/TaskConfiguration.cs` |
| `groups[]` | `[]` | Workspace/user profile task files | `tasks.json` structure + `TaskConfiguration` |
| `tasks[]` (legacy flat) | `[]` | Workspace/user profile task files | `TaskConfiguration` normalization path |
| `task.name` | required string | Workspace/user profile | `tasks.json` schema/model |
| `task.group` | normalized to `General` when empty | Workspace/user profile | `TaskConfiguration.NormalizeTask` path |
| `task.runner` | `exe` (model default) | Workspace/user profile | Task model + UI editors |
| `task.waitForExit` | `true` | Workspace/user profile | Task model/runtime |
| `task.requiresElevation` | `false` | Workspace/user profile | Task model/runtime |
| `task.ignoreExitCode` | `false` | Workspace/user profile | Task model/runtime |
| `task.timeoutSeconds` | `600` | Workspace/user profile | `src/Tsky.Desktop/TaskRunHistoryStore.cs` snapshot defaults; task execution timeout handling |
| `task.retries` | `0` | Workspace/user profile | Task model/runtime |
| `task.killProcessTreeOnCancel` | `true` | Workspace/user profile | Task execution behavior |
| `task.maxCapturedOutputChars` | `65536` | Workspace/user profile | Output capture truncation threshold |
| `task.env.*` | `{}` | Workspace/user profile | Per-task environment map (supports placeholder expansion at execution). |
| `task.inputs.*` | `{}` | Workspace/user profile | Per-task input map for dataflow execution. |
| `task.inputBindings.*` | `{}` | Workspace/user profile | Dataflow binding references (`Task.output`, `port:name`, etc.). |
| `task.outputs.*` | `{}` | Workspace/user profile | Declared outputs including runtime placeholders. |
| `scheduler.schemaVersion` | `3` | Local persisted state | `src/Tsky.Desktop/SchedulerConfiguration.cs` |
| `scheduler.pollIntervalSeconds` | `SchedulerTimingService.DefaultPollIntervalSeconds` | Local persisted state | Scheduler config normalization |
| `scheduler.desktopInstanceId` | generated `desktop_<guid>` | Local persisted state | Scheduler config normalization |
| `scheduledItem.triggerWindowSeconds` | `60` | Local persisted state | Scheduled item defaults/normalization |
| `scheduledItem.executionTargetMode` | `local` | Local persisted state | Scheduler snapshot + remote scheduling flow |
| `gateway.enableTlsListener` | `false` | Local persisted state | `src/Tsky.Desktop/AgentGatewaySettings.cs` |
| `gateway.bindHost` | `localhost` | Local persisted state | Agent gateway settings service |
| `gateway.port` | `AgentGatewayHost.DefaultPort` | Local persisted state | Agent gateway settings service |
| `gateway.certificatePath` | `null` | Local persisted state | Agent gateway settings service |
| `gateway.certificatePassword` | `null` | Local persisted state | Agent gateway settings service |
| `gateway.isDevLoopbackFallbackEnabled` | `true` | Local persisted state | Agent gateway settings service |
| `agentRegistry.schemaVersion` | `1` | Local persisted state | `src/Tsky.Desktop/AgentRegistry.cs` |
| `agent.state` | `Pending` | Local persisted state | Agent registry model |
| `agent.maximumConcurrentRuns` | `1` | Local persisted state | Agent registry model |
| `agent.allowedExecutors[]` | `['process.v1']` | Local persisted state | Agent registry model |
| `agent.enrolmentTokenHash` | `null` until generated | Local persisted state | Hashed token persisted; raw token not persisted. |
| `ui.windowWidth` | `700` | Local persisted state | `src/Tsky.Desktop/UiStateStore.cs` |
| `ui.windowHeight` | `420` | Local persisted state | Ui state |
| `ui.outputHeight` | `220` | Local persisted state | Ui state |
| `ui.sidebarWidth` | `220` | Local persisted state | Ui state |
| `ui.historyListPaneRatio` | `0.4` | Local persisted state | Ui state |

### Go Agent (`tsky-agent`)

| Property Key | Default | Profiles | Source |
| --- | --- | --- | --- |
| `schemaVersion` | `1` | Defaults/YAML/env/CLI | `agent/internal/config/config.go` |
| `controller.url` | empty | YAML/env | Required at runtime (`run`) and overridable by `TSKY_AGENT_CONTROLLER_URL`. |
| `controller.controllerId` | empty | YAML/enrolment writeback | Set during enrolment and persisted. |
| `controller.tlsPinnedSha256` | empty | YAML | Optional cert pin (`sha256/...` or hex forms). |
| `controller.tlsCaCertPath` | empty | YAML + embedded env expansion | Optional PEM trust anchor path. |
| `identity.agentId` | empty | YAML/enrolment writeback | Required for run after enrolment. |
| `identity.keyId` | empty | YAML/enrolment writeback | Required for run after enrolment. |
| `identity.keyReference` | empty | YAML/enrolment writeback + embedded env expansion | Path/ref to local key material. |
| `runtime.root` | OS-specific default | YAML/env/CLI + embedded env expansion | Overridable via `TSKY_AGENT_RUNTIME_ROOT` and `--runtime-root`. |
| `runtime.completedRunRetentionDays` | `30` | YAML | Retention setting. |
| `runtime.outputRetentionDays` | `14` | YAML | Retention setting. |
| `runtime.artifactRetentionDays` | `7` | YAML | Retention setting. |
| `runtime.warningFreeDiskBytes` | `5368709120` | YAML | Disk-pressure threshold. |
| `runtime.criticalFreeDiskBytes` | `1073741824` | YAML | Disk-pressure threshold. |
| `execution.maxConcurrentRuns` | `1` | YAML | Must be `>=1`. |
| `execution.maxPendingQueueDepth` | `16` | YAML | Must be `>=0`. |
| `execution.admissionQueueWaitTimeoutSeconds` | `30` | YAML | Must be `>=1`. |
| `execution.defaultTimeoutSeconds` | `3600` | YAML | Must be `>=1`. |
| `execution.cancellationGraceSeconds` | `10` | YAML | Must be `>=0`. |
| `execution.maximumOutputBytesPerStream` | `104857600` | YAML | Must be `>0`. |
| `execution.allowedExecutors[]` | `['process.v1']` | YAML | Must contain at least one executor. |
| `execution.allowedWorkingRoots[]` | `[<runtime.root>/work]` | YAML + embedded env expansion | Empty/blank entries invalid. |
| `execution.allowShellExecutor` | `false` | YAML | Shell executor guard toggle. |
| `connection.heartbeatSeconds` | `10` | YAML | Must be `>=1`. |
| `connection.reconnectMinimumSeconds` | `1` | YAML | Backoff lower bound. |
| `connection.reconnectMaximumSeconds` | `60` | YAML | Backoff upper bound (`>= min`). |
| `logging.level` | `info` | YAML/env/CLI | Env override via `TSKY_AGENT_LOG_LEVEL`; CLI `--log-level`. |
| `logging.format` | `json` | YAML | Required. |
| `logging.silent` | `false` | YAML/env/CLI | Env override `TSKY_AGENT_SILENT`; CLI `--silent`. |

> Note: Some configuration sources or properties could not be fully identified.

## Startup Parameters & Resource Requirements

| Service | JVM/Runtime Options | Memory | Instance Count |
| --- | --- | --- | --- |
| Desktop app (`Tsky.Desktop`) | `.NET 8` desktop process; no JVM flags; task-level `timeoutSeconds`, `retries`, and cancellation controls influence spawned process behavior. | No static process memory limit found in repo config. | Not explicitly configured. |
| Agent (`tsky-agent`) | CLI runtime options: `--config`, `--log-level`, `--runtime-root`, `--silent`; service entrypoint `service-run --name <name> --config <path>` plus optional overrides. Connection keepalive and reconnect settings from config. | No explicit container/Kubernetes/cgroup allocation found in repository config. | Not explicitly configured; one service instance per host installation implied. |
| CI build runners | `dotnet build/test/publish` and `go test/build` commands in GitHub workflows. | Runner sizing external to repository (GitHub runner or self-hosted labels). | Matrix defines publish targets (`win-x64`, `win-arm64`), not service runtime scale. |

## Startup Dependency Chain

1. Desktop app startup -> loads workspace and user-profile task configuration (`tasks.json`) and local persisted state (`templates`, `scheduler`, `history`, `ui-state`).
2. If agent dispatch is used -> desktop gateway listener settings (`gateway-settings.json`) are loaded and optional TLS listener is validated/applied before accepting remote agent sessions.
3. Agent bootstrap -> `config.yaml` is loaded (defaults -> YAML -> env -> CLI), validated, and embedded env placeholders are resolved for selected fields.
4. Agent enrolment (prerequisite for run) -> requires controller URL, pending agent ID, token, and controller ID; generates local key and persists identity/controller fields.
5. Agent run loop -> requires populated `identity.*` and `controller.url`; establishes TAP gRPC session to controller and starts heartbeat/sender/receiver loops.
6. Reconnect behavior -> backoff between `connection.reconnectMinimumSeconds` and `connection.reconnectMaximumSeconds`, with fixed retry interval when listener is unreachable.
7. Scheduler behavior -> if negotiated (`schedule.v1` on TAP 1.1), local scheduler runtime starts and occurrences are reconciled with controller stream state.

## Secrets & Sensitive Configuration

| Secret Reference | Type | Storage (masked) |
| --- | --- | --- |
| `gateway.certificatePassword` | TLS certificate secret | `%LOCALAPPDATA%/tsky/agents/gateway-settings.json` -> `[MASKED]` |
| `controller.tlsPinnedSha256` | Certificate pin material | `config.yaml` / `docs/config.yaml` -> `[MASKED]` |
| `identity.keyReference` (private key path reference) | Key material locator | Agent config path reference -> `[MASKED_PATH]` |
| Enrolment token input (`--token`) | One-time auth secret | CLI input at enrolment time -> `[MASKED]`; persisted as hash only in desktop registry. |
| `agent.enrolmentTokenHash` | Derived secret hash | `%LOCALAPPDATA%/tsky/agents/registry.json` -> `[HASHED_VALUE]` |

### Secrets Provisioning Workflow

Source -> identity/access -> binding -> services:

1. Desktop operator configures gateway TLS certificate path/password in gateway settings; desktop stores listener settings for local controller endpoint publication.
2. Desktop generates one-time enrolment secret material for a pending agent and exposes it via enrolment command (token displayed for operational use).
3. Agent operator executes `tsky-agent enrol ... --token ... --controller-id ...`; agent generates local P-256 private key and sends public identity proof to controller.
4. Controller validates enrolment token and returns agent identity metadata; agent persists non-secret operational config and key reference.
5. Subsequent agent sessions use persisted identity/key reference and TAP proof-of-possession; desktop registry stores hashed enrolment token state, not reusable raw token.

## Feature Flags

| Flag Name | Default | Controlled By |
| --- | --- | --- |
| `execution.allowShellExecutor` | `false` | Agent `config.yaml` (`execution.allowShellExecutor`). |
| `logging.silent` | `false` | Agent `config.yaml`, `TSKY_AGENT_SILENT`, CLI `--silent`. |
| `gateway.enableTlsListener` | `false` | Desktop gateway settings JSON/UI. |
| `gateway.isDevLoopbackFallbackEnabled` | `true` | Desktop gateway settings JSON/UI. |
| `task.waitForExit` | `true` | Task JSON/UI (`waitForExit`). |
| `task.requiresElevation` | `false` | Task JSON/UI (`requiresElevation`). |
| `task.ignoreExitCode` | `false` | Task JSON/UI (`ignoreExitCode`). |

## Framework & Runtime Versions

| Component | Version | Source |
| --- | --- | --- |
| .NET target framework (desktop) | `net8.0-windows` | `src/Tsky.Desktop/Tsky.Desktop.csproj` |
| .NET target framework (tests) | `net8.0-windows` | `tsky.Tests/tsky.Tests.csproj` |
| .NET SDK in CI | `8.0.x` | `.github/workflows/pr-ci.yml`, `.github/workflows/release.yml` |
| Go language version (agent module) | `1.26` | `agent/go.mod` |
| gRPC (Go) | `google.golang.org/grpc v1.82.1` | `agent/go.mod` |
| Protobuf runtime (Go) | `google.golang.org/protobuf v1.36.11` | `agent/go.mod` |
| YAML parsing (Go) | `gopkg.in/yaml.v3 v3.0.1` | `agent/go.mod` |
| CLI/config framework (Go) | `cobra v1.10.1`, `viper v1.20.1` | `agent/go.mod` |
| Protobuf runtime (.NET) | `Google.Protobuf 3.31.1` | `src/Tsky.Desktop/Tsky.Desktop.csproj` |
| gRPC stack (.NET) | `Grpc.AspNetCore 2.71.0`, `Grpc.Tools 2.71.0` | `src/Tsky.Desktop/Tsky.Desktop.csproj` |
| Test tooling (.NET) | `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`, `coverlet 6.0.4` | `tsky.Tests/tsky.Tests.csproj` |
| UI/editor/runtime libs (.NET) | `AvalonEdit 6.3.1.120`, `Jint 4.4.1` | `src/Tsky.Desktop/Tsky.Desktop.csproj` |
| Build analyzer level | `AnalysisLevel=latest-recommended` | `Directory.Build.props` |
