//go:build windows

package execution

import "os/exec"

func configureProcess(_ *exec.Cmd) {}

func terminateProcessTree(command *exec.Cmd) {
	if command.Process == nil {
		return
	}
	_ = command.Process.Kill()
}
