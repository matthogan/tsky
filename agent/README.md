# Tsky Agent

Reference Go implementation of the Tsky Agent Protocol worker described in
`tsky-remote-agent-technical-spec-v0.2.md`.

## What this module implements

This module provides a cross-platform TAP worker that:

- Establishes outbound TAP gRPC sessions to a controller (`http://` or `https://`).
- Enrols with one-time credentials and creates a local P-256 identity key.
- Executes immutable run plans using the `process.v1` executor.
- Enforces local execution policy (allowed executors, working directory roots, shell executor guard).
- Persists durable attempt state, acceptance metadata, run events, and stdout/stderr logs.
- Recovers and reconciles local attempt state with the controller after reconnects.
- Emits heartbeats, capability reports, and health/capacity snapshots.

Current limitations in this reference implementation:

- Only `process.v1` execution is implemented in the engine.

## Build

```sh
make proto
make build
```

Run tests:

```sh
make test
```

## Commands

```sh
tsky-agent enrol --controller https://host:7341 --pending-agent agt_pending_... --token tsky_enrol_... --controller-id ctl_...
tsky-agent validate-config --config ./config.yaml
tsky-agent run --config ./config.yaml
tsky-agent run --config ./config.yaml --silent
tsky-agent status --config ./config.yaml
tsky-agent doctor --config ./config.yaml
tsky-agent rotate-key --config ./config.yaml
tsky-agent install-service --config ./config.yaml --name TskyAgent --display-name "Tsky Agent" --description "Tsky remote execution agent" --start auto-delayed
tsky-agent start-service --name TskyAgent
tsky-agent stop-service --name TskyAgent
tsky-agent uninstall-service --name TskyAgent
tsky-agent version
```

### Command notes

- `enrol`
	- Requires `--controller`, `--pending-agent`, `--token`, and `--controller-id`.
	- Generates a local private key at `<runtime.root>/identity/agent-key.pem`.
	- Persists identity and controller settings into `config.yaml`.
- `run`
	- Requires a valid config including `identity.agentId`, `identity.keyId`, and `identity.keyReference`.
	- Starts the TAP control loop, receives offers, executes steps, and streams events/output.
- `validate-config`
	- Loads config, applies overrides, and validates schema/values.
- `status`
	- Prints local runtime status as JSON (agent/controller/runtime root + known attempts).
- `doctor`
	- Prints diagnostics JSON (runtime writable check, identity key load check, platform metadata).
- `rotate-key`
	- Generates a new key file for future rotation workflows.
	- This does not rotate controller-side trust automatically.
- `install-service`
	- Windows: installs the current executable as a native Windows Service (no `sc.exe`/`nssm` required).
	- Linux (systemd): installs a native `systemd` unit at `/etc/systemd/system/<name>.service`.
	- Service command line is configured as `tsky-agent service-run --name <name> --config <path>` plus any install-time logging/runtime overrides.
	- Requires sufficient privileges to create services/units.
	- Default start mode is delayed automatic (`--start auto-delayed`).
	- Default service name: `TskyAgent` on Windows, `tsky-agent` on Linux.
	- Common flags: `--name`, `--display-name`, `--description`, `--start auto|auto-delayed|manual`.
- `start-service`
	- Starts the installed service by name.
	- Windows: via SCM.
	- Linux (systemd): via `systemd` D-Bus start.
- `stop-service`
	- Stops the running service by name.
	- Windows: via SCM.
	- Linux (systemd): via `systemd` D-Bus stop.
- `uninstall-service`
	- Stops (if running) and removes the installed service by name.
	- Windows: deletes the service from SCM.
	- Linux (systemd): disables/removes unit file and reloads daemon state.
	- Requires sufficient privileges to remove services/units.
	- On Linux hosts without active `systemd` (or where `systemd` is not PID 1), lifecycle commands fail with a clear unsupported-environment error.

The agent stores durable run data under `runtime.root`, including accepted plans,
state snapshots, event NDJSON, and stdout/stderr logs.

## Configuration (`config.yaml`)

### Resolution and precedence

Config values are applied in this order (later wins):

1. Built-in defaults.
2. YAML file (`--config`, default path, or `TSKY_AGENT_CONFIG`).
3. Environment overrides.
4. CLI flag overrides (`--log-level`, `--runtime-root`, `--silent`).

`config.yaml` uses strict YAML decoding (unknown fields are rejected).

Embedded environment variables inside selected config values are expanded at load time.
Supported forms:

- `%VAR%`
- `$VAR`
- `${VAR}`

Expanded fields: `controller.url`, `identity.keyReference`, `runtime.root`, and `execution.allowedWorkingRoots`.

### Default config path

- Windows: `%PROGRAMDATA%\\Tsky\\Agent\\config.yaml`
- Linux (root): `/etc/tsky-agent/config.yaml`
- Linux/macOS (non-root): `${XDG_CONFIG_HOME}/tsky-agent/config.yaml` or `~/.config/tsky-agent/config.yaml`

Set `TSKY_AGENT_CONFIG` to override the path globally.

### Environment variables

- `TSKY_AGENT_CONFIG`: config file path.
- `TSKY_AGENT_CONTROLLER_URL`: overrides `controller.url`.
- `TSKY_AGENT_RUNTIME_ROOT`: overrides `runtime.root`.
- `TSKY_AGENT_LOG_LEVEL`: overrides `logging.level`.
- `TSKY_AGENT_SILENT`: overrides `logging.silent` (`true`/`false`).

### Desktop Gateway TLS certificate (PFX)

To encrypt agent traffic with `https://`, configure the desktop gateway TLS listener with a `.pfx` certificate in the desktop app Gateway Listener Settings.

Notes:

- The `.pfx` is used by the desktop gateway listener (server TLS).
- This is separate from the agent identity key (`agent-key.pem`) used for TAP proof-of-possession.

#### Option A: Private CA / self-managed certificates (internal use)

1. Create a private CA key + cert (one-time):

```sh
openssl genrsa -out private-ca.key 4096
openssl req -x509 -new -nodes -key private-ca.key -sha256 -days 3650 -out private-ca.crt -subj "/CN=Tsky Private CA"
```

2. Create a server key + CSR for the desktop host (CN/SAN should match the host used by agents):

```sh
openssl genrsa -out gateway.key 2048
openssl req -new -key gateway.key -out gateway.csr -subj "/CN=desktop.example.internal"
```

3. Sign the CSR with your private CA:

```sh
openssl x509 -req -in gateway.csr -CA private-ca.crt -CAkey private-ca.key -CAcreateserial -out gateway.crt -days 825 -sha256
```

4. Export to PFX for desktop gateway settings:

```sh
openssl pkcs12 -export -out gateway.pfx -inkey gateway.key -in gateway.crt -certfile private-ca.crt
```

5. Trust your private CA on agent machines:

- Import `private-ca.crt` into the OS trust store on each agent host.
- Or set `controller.tlsPinnedSha256` on agents to pin the gateway certificate.

#### Option B: Official/public CA certificate

If your cert is issued by a public/enterprise CA and you already have cert + private key:

```sh
openssl pkcs12 -export -out gateway.pfx -inkey gateway.key -in gateway.crt -certfile intermediate-chain.crt
```

If the certificate is in Windows Certificate Store, export to PFX (PowerShell):

```powershell
$pwd = ConvertTo-SecureString "changeit" -AsPlainText -Force
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*desktop.example.com*" } | Select-Object -First 1
Export-PfxCertificate -Cert $cert -FilePath "C:\certs\gateway.pfx" -Password $pwd
```

Then in desktop gateway settings:

- Enable TLS listener.
- Set PFX path and password.
- Prefer disabling HTTP loopback fallback outside local development.

### Example config (post-enrol)

```yaml
schemaVersion: 1

controller:
	url: "https://controller.example:7341"
	controllerId: "ctl_1234567890"
	tlsCaCertPath: "" # optional PEM file containing CA/server certs trusted for this agent only
	tlsPinnedSha256: "" # optional; supports sha256/ or hex formats

identity:
	agentId: "agt_1234567890"
	keyId: "key_1234567890"
	keyReference: "file://C:\\ProgramData\\Tsky\\Agent\\runtime\\identity\\agent-key.pem"

runtime:
	root: "C:\\ProgramData\\Tsky\\Agent\\runtime"
	completedRunRetentionDays: 30
	outputRetentionDays: 14
	artifactRetentionDays: 7
	warningFreeDiskBytes: 5368709120
	criticalFreeDiskBytes: 1073741824

execution:
	maxConcurrentRuns: 1
	maxPendingQueueDepth: 16
	admissionQueueWaitTimeoutSeconds: 30
	defaultTimeoutSeconds: 3600
	cancellationGraceSeconds: 10
	maximumOutputBytesPerStream: 104857600
	allowedExecutors:
		- "process.v1"
	allowedWorkingRoots:
		- "C:\\ProgramData\\Tsky\\Agent\\runtime\\work"
	allowShellExecutor: false

connection:
	heartbeatSeconds: 10
	reconnectMinimumSeconds: 1
	reconnectMaximumSeconds: 60

logging:
	level: "info"
	format: "json" # "json" or "text"
	silent: false # when true, suppresses agent log output (stderr + persistent log file)
```

### Field-by-field guide

- `schemaVersion`
	- Must be `1`.

- `controller.url`
	- Required for `run`.
	- Supported schemes: `http` (insecure) and `https` (TLS).

- `controller.controllerId`
	- Required during enrolment.
	- Persisted from enrolment response when provided.

- `controller.tlsPinnedSha256`
	- Optional certificate pin for HTTPS.
	- Accepted formats include `sha256/<hex>` or colon-separated hex.

- `controller.tlsCaCertPath`
	- Optional path to a PEM file used as custom Root CAs for HTTPS verification.
	- Useful when you do not want to install certificates into the OS trust store.
	- Requires `controller.url` to use `https`.

- `identity.agentId`, `identity.keyId`, `identity.keyReference`
	- Populated by successful enrolment.
	- `keyReference` currently supports file paths (`file://...` or plain path).
	- Embedded env vars are supported (`%VAR%`, `$VAR`, `${VAR}`).

- `runtime.root`
	- Required.
	- Base directory for all durable local state and logs.

- `runtime.*RetentionDays`
	- Present for retention policy configuration.
	- Current reference implementation persists values; cleanup enforcement can be extended separately.

- `runtime.warningFreeDiskBytes`, `runtime.criticalFreeDiskBytes`
	- Threshold values for disk-pressure policy/telemetry integration.

- `logging.silent`
	- Optional, default `false`.
	- When `true`, suppresses all agent logging output, including stderr and persistent agent log file writes.

- `execution.maxConcurrentRuns`
	- Must be `>= 1`.
	- Controls concurrent attempt execution capacity.

- `execution.maxPendingQueueDepth`
	- Must be `>= 0`.
	- Bounds pending admitted attempts waiting for an execution slot.
	- Total admitted attempts is bounded by `maxConcurrentRuns + maxPendingQueueDepth`.

- `execution.admissionQueueWaitTimeoutSeconds`
	- Must be `>= 1`.
	- Maximum time an offer can wait to enter the bounded admission window.
	- On timeout, the attempt is deterministically completed in terminal `TIMED_OUT` with admission error details.

- `execution.defaultTimeoutSeconds`
	- Must be `>= 1`.
	- Default step timeout if a plan step does not provide its own timeout.

- `execution.cancellationGraceSeconds`
	- Must be `>= 0`.
	- Cancellation grace policy value.

- `execution.maximumOutputBytesPerStream`
	- Must be `> 0`.
	- Output budget policy value.

- `execution.allowedExecutors`
	- Must contain at least one executor.
	- Each run step executor is checked against this allow-list.

- `execution.allowedWorkingRoots`
	- All configured roots must be non-empty.
	- Requested step working directories must be at or under one of these roots.

- `execution.allowShellExecutor`
	- If `false`, shell-style executors (`shell.v1`, `powershell.v1`, `cmd.v1`, `bash.v1`) are blocked even if listed.

- `connection.heartbeatSeconds`
	- Must be `>= 1`.
	- Interval for TAP heartbeat frames.

- `connection.reconnectMinimumSeconds`, `connection.reconnectMaximumSeconds`
	- Minimum must be `>= 1`.
	- Maximum must be `>=` minimum.
	- Used for reconnect backoff with jitter.

- `logging.level`
	- Common values: `debug`, `info`, `warn`, `error`.

- `logging.format`
	- Required.
	- `json` (default) or `text`.

### Validation checklist

```sh
tsky-agent validate-config --config ./config.yaml
```

Common validation failures:

- Unknown YAML keys.
- Missing `runtime.root`.
- Empty `execution.allowedExecutors`.
- Invalid connection reconnect bounds.
- Empty path in `execution.allowedWorkingRoots`.

### Admission Control And Telemetry

The engine uses two bounded controls:

- Running slots: `execution.maxConcurrentRuns`.
- Admission window: `execution.maxConcurrentRuns + execution.maxPendingQueueDepth`.

Behavior summary:

- If admission capacity is available, an accepted offer enters the admission window.
- If admission capacity is full, the offer waits up to `execution.admissionQueueWaitTimeoutSeconds`.
- If that wait expires, the attempt transitions to terminal `TIMED_OUT` with admission error code `admission_queue_timeout`.
- Running-slot acquisition is context-aware and cancellation-aware.

Client-visible frame semantics:

- Admission overload is not emitted as `RunRejected`.
- The offer is first acknowledged with `RunAccepted`, then the attempt is completed with:
	- `RunEvent` (`ATTEMPT_STATE_CHANGED`) to terminal `TIMED_OUT` or `CANCELLED`.
	- `RunCompleted` carrying the terminal state and admission error details.
- This guarantees the client receives a terminal attempt outcome for admission failures.

Telemetry exposure:

- `activeRuns`: exposed via `capacity.active_runs`.
- `queuedRuns`: exposed in heartbeat/snapshot health issue details (`admission_control`).
- `rejectedByCapacity`: exposed in heartbeat/snapshot health issue details and capability labels (`admission.rejected_by_capacity`).

### Concurrency Theory Notes

Design rationale:

- The bounded admission model prevents unbounded goroutine accumulation and gives deterministic overload behavior.
- Queue wait timeout makes overload outcomes explicit rather than implicit indefinite blocking.
- Capacity-based admission plus explicit queue depth follows standard backpressure patterns used in high-availability systems.

Useful references:

- Go channels and `select`: The Go Programming Language Specification, "Channel types" and "Select statements".
- Go memory model: "The Go Memory Model" (go.dev).
- Queueing fundamentals: Little's Law, $L = \lambda W$ (Little, 1961).
- Practical queueing for finite-capacity systems: Gross, Shortle, Thompson, Harris, "Fundamentals of Queueing Theory".

## Runtime data layout

Under `runtime.root`, the agent creates:

- `runs/<runId>_<attemptId>/plan.json`
- `runs/<runId>_<attemptId>/acceptance.json`
- `runs/<runId>_<attemptId>/state.json`
- `runs/<runId>_<attemptId>/events.ndjson`
- `runs/<runId>_<attemptId>/stdout.log`
- `runs/<runId>_<attemptId>/stderr.log`
- `runs/<runId>_<attemptId>/work/`
- `runs/<runId>_<attemptId>/artifacts/`

This durable store is used for status reporting, replay/reconciliation support, and
local diagnostics.
