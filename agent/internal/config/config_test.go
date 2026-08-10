package config

import (
	"os"
	"path/filepath"
	"testing"
)

func writeConfigFile(t *testing.T, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "config.yaml")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestLoadRejectsUnknownFields(t *testing.T) {
	path := writeConfigFile(t, "schemaVersion: 1\nunknownSecret: nope\n")
	if _, err := Load(path); err == nil {
		t.Fatal("expected unknown field error")
	}
}

func TestDefaultsValidate(t *testing.T) {
	cfg := Defaults()
	cfg.Controller.URL = "https://example.test:7341"
	cfg.Controller.ControllerID = "ctl"
	cfg.Identity.AgentID = "agt"
	cfg.Identity.KeyID = "key"
	cfg.Identity.KeyReference = "file:///tmp/key.pem"
	if err := cfg.Validate(); err != nil {
		t.Fatal(err)
	}
}

func TestLoadExpandsEmbeddedEnvironmentVariables(t *testing.T) {
	t.Setenv("TSKY_TEST_HOME", filepath.Join(t.TempDir(), "home"))

	path := writeConfigFile(t, `schemaVersion: 1
controller:
  url: "http://localhost:7341"
  controllerId: "ctl"
identity:
  agentId: "agt"
  keyId: "key"
  keyReference: "$TSKY_TEST_HOME/identity/agent-key.pem"
runtime:
  root: "%TSKY_TEST_HOME%/runtime"
execution:
  maxConcurrentRuns: 1
  defaultTimeoutSeconds: 60
  cancellationGraceSeconds: 1
  maximumOutputBytesPerStream: 1024
  allowedExecutors:
    - "process.v1"
  allowedWorkingRoots:
    - "${TSKY_TEST_HOME}/runtime/work"
connection:
  heartbeatSeconds: 10
  reconnectMinimumSeconds: 1
  reconnectMaximumSeconds: 2
logging:
  level: "info"
  format: "json"
`)

	cfg, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}

	wantRoot := filepath.Join(os.Getenv("TSKY_TEST_HOME"), "runtime")
	if filepath.Clean(filepath.FromSlash(cfg.Runtime.Root)) != filepath.Clean(filepath.FromSlash(wantRoot)) {
		t.Fatalf("runtime.root mismatch: got %q want %q", cfg.Runtime.Root, wantRoot)
	}

	wantKey := filepath.Join(os.Getenv("TSKY_TEST_HOME"), "identity", "agent-key.pem")
	if filepath.Clean(filepath.FromSlash(cfg.Identity.KeyReference)) != filepath.Clean(filepath.FromSlash(wantKey)) {
		t.Fatalf("identity.keyReference mismatch: got %q want %q", cfg.Identity.KeyReference, wantKey)
	}

	wantWork := filepath.Join(os.Getenv("TSKY_TEST_HOME"), "runtime", "work")
	if len(cfg.Execution.AllowedWorkingRoots) != 1 || filepath.Clean(filepath.FromSlash(cfg.Execution.AllowedWorkingRoots[0])) != filepath.Clean(filepath.FromSlash(wantWork)) {
		t.Fatalf("execution.allowedWorkingRoots mismatch: got %v want [%q]", cfg.Execution.AllowedWorkingRoots, wantWork)
	}
}

func TestApplyEnvSilentOverride(t *testing.T) {
	t.Setenv("TSKY_AGENT_SILENT", "true")

	cfg := Defaults()
	cfg.Logging.Silent = false

	applyEnv(&cfg)

	if !cfg.Logging.Silent {
		t.Fatal("expected logging.silent to be true from TSKY_AGENT_SILENT")
	}
}

func TestValidateRejectsNegativePendingQueueDepth(t *testing.T) {
	cfg := Defaults()
	cfg.Execution.MaxPendingQueueDepth = -1

	if err := cfg.Validate(); err == nil {
		t.Fatal("expected validation error for negative maxPendingQueueDepth")
	}
}

func TestValidateRejectsNonPositiveAdmissionQueueWaitTimeout(t *testing.T) {
	cfg := Defaults()
	cfg.Execution.AdmissionQueueWaitTimeoutSeconds = 0

	if err := cfg.Validate(); err == nil {
		t.Fatal("expected validation error for non-positive admissionQueueWaitTimeoutSeconds")
	}
}
