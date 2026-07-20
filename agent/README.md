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

- `install-service` and `uninstall-service` are defined but not implemented.
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
tsky-agent status --config ./config.yaml
tsky-agent doctor --config ./config.yaml
tsky-agent rotate-key --config ./config.yaml
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

The agent stores durable run data under `runtime.root`, including accepted plans,
state snapshots, event NDJSON, and stdout/stderr logs.

## Configuration (`config.yaml`)

### Resolution and precedence

Config values are applied in this order (later wins):

1. Built-in defaults.
2. YAML file (`--config`, default path, or `TSKY_AGENT_CONFIG`).
3. Environment overrides.
4. CLI flag overrides (`--log-level`, `--runtime-root`).

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

- `execution.maxConcurrentRuns`
	- Must be `>= 1`.
	- Controls concurrent attempt execution capacity.

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
