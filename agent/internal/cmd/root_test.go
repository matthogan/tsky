package cmd

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/matthogan/tsky/agent/internal/config"
)

func TestLoggerSilentModeSkipsPersistentLogSetup(t *testing.T) {
	runtimeRoot := t.TempDir()
	cfg := config.Defaults()
	cfg.Runtime.Root = runtimeRoot
	cfg.Logging.Silent = true

	log := logger(cfg)
	log.Info("this should be discarded")

	logPath := filepath.Join(runtimeRoot, "logs", "agent.log")
	if _, err := os.Stat(logPath); err == nil {
		t.Fatalf("expected no persistent log file in silent mode, but found %s", logPath)
	} else if !os.IsNotExist(err) {
		t.Fatalf("unexpected stat error: %v", err)
	}
}
