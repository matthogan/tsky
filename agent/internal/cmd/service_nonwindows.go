//go:build !windows && !linux

package cmd

import (
	"context"
	"fmt"
	"log/slog"

	"github.com/spf13/cobra"
)

func installService(_ string, _ string, _ string, _ string, _ *cobra.Command) error {
	return fmt.Errorf("install-service is only supported on Windows and Linux (systemd)")
}

func uninstallService(_ string) error {
	return fmt.Errorf("uninstall-service is only supported on Windows and Linux (systemd)")
}

func startService(_ string) error {
	return fmt.Errorf("start-service is only supported on Windows and Linux (systemd)")
}

func stopService(_ string) error {
	return fmt.Errorf("stop-service is only supported on Windows and Linux (systemd)")
}

func runAsService(_ string, _ *slog.Logger, _ func(context.Context) error) error {
	return fmt.Errorf("service-run is only supported on Windows and Linux (systemd)")
}
