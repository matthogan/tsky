//go:build windows

package execution

import (
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

func configureProcess(_ *exec.Cmd) {}

func resolveExecutable(executable string) string {
	base := strings.ToLower(filepath.Base(strings.TrimSpace(executable)))
	if base != "pwsh" && base != "pwsh.exe" {
		return executable
	}

	if resolved, err := exec.LookPath("pwsh.exe"); err == nil {
		// Windows App Execution Alias paths can fail in non-interactive contexts.
		if !strings.Contains(strings.ToLower(resolved), `\windowsapps\`) {
			return resolved
		}
	}

	if candidate := filepath.Join(os.Getenv("ProgramFiles"), "PowerShell", "7", "pwsh.exe"); candidate != "" {
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
	}

	if candidate := filepath.Join(os.Getenv("ProgramW6432"), "PowerShell", "7", "pwsh.exe"); candidate != "" {
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
	}

	// Prefer a broadly available fallback over a known-bad alias path.
	return "powershell.exe"
}

func terminateProcessTree(command *exec.Cmd) {
	if command.Process == nil {
		return
	}
	_ = command.Process.Kill()
}
