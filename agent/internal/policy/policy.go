package policy

import (
	"fmt"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/matthogan/tsky/agent/internal/config"
)

type Policy struct {
	cfg config.Config
}

func New(cfg config.Config) Policy {
	return Policy{cfg: cfg}
}

func (p Policy) ExecutorAllowed(id string) bool {
	if isShell(id) && !p.cfg.Execution.AllowShellExecutor {
		return false
	}
	for _, allowed := range p.cfg.Execution.AllowedExecutors {
		if allowed == id {
			return true
		}
	}
	return false
}

func (p Policy) ValidateWorkingDirectory(path string) error {
	if path == "" {
		return nil
	}
	abs, err := filepath.Abs(path)
	if err != nil {
		return err
	}
	for _, root := range p.cfg.Execution.AllowedWorkingRoots {
		rootAbs, err := filepath.Abs(root)
		if err != nil {
			continue
		}
		if sameOrChild(abs, rootAbs) {
			return nil
		}
	}
	return fmt.Errorf("working directory %q is outside allowed roots", path)
}

func sameOrChild(path, root string) bool {
	if runtime.GOOS == "windows" {
		path = strings.ToLower(path)
		root = strings.ToLower(root)
	}
	if path == root {
		return true
	}
	rel, err := filepath.Rel(root, path)
	return err == nil && rel != "." && !strings.HasPrefix(rel, "..") && !filepath.IsAbs(rel)
}

func isShell(id string) bool {
	switch id {
	case "shell.v1", "powershell.v1", "cmd.v1", "bash.v1":
		return true
	default:
		return false
	}
}
