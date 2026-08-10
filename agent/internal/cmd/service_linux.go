//go:build linux

package cmd

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/coreos/go-systemd/v22/dbus"
	"github.com/coreos/go-systemd/v22/unit"
	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/spf13/cobra"
)

const (
	linuxSystemdUnitDirectory = "/etc/systemd/system"
	linuxDelayedStartSeconds  = 15
	linuxServiceActionTimeout = 20 * time.Second
)

type linuxStartMode int

const (
	linuxStartModeManual linuxStartMode = iota
	linuxStartModeAuto
	linuxStartModeAutoDelayed
)

func installService(serviceName, displayName, description, startMode string, cmd *cobra.Command) error {
	if err := ensureSystemdAvailable(); err != nil {
		return err
	}

	mode, err := parseLinuxServiceStartMode(startMode)
	if err != nil {
		return err
	}

	configPath, _ := cmd.Flags().GetString("config")
	configPath = strings.TrimSpace(configPath)
	if configPath == "" {
		configPath = config.DefaultPath()
	}

	cfg, err := config.Load(configPath)
	if err != nil {
		return fmt.Errorf("load config for service install: %w", err)
	}
	if err := cfg.Validate(); err != nil {
		return fmt.Errorf("validate config for service install: %w", err)
	}

	exePath, err := os.Executable()
	if err != nil {
		return fmt.Errorf("resolve executable path: %w", err)
	}
	exePath, err = filepath.Abs(exePath)
	if err != nil {
		return fmt.Errorf("resolve absolute executable path: %w", err)
	}

	unitName, err := normalizeServiceUnitName(serviceName)
	if err != nil {
		return err
	}

	args := []string{"service-run", "--name", serviceName, "--config", configPath}

	logLevel, _ := cmd.Flags().GetString("log-level")
	logLevel = strings.TrimSpace(logLevel)
	if logLevel != "" {
		args = append(args, "--log-level", logLevel)
	}

	runtimeRoot, _ := cmd.Flags().GetString("runtime-root")
	runtimeRoot = strings.TrimSpace(runtimeRoot)
	if runtimeRoot != "" {
		args = append(args, "--runtime-root", runtimeRoot)
	}

	silentMode, _ := cmd.Flags().GetBool("silent")
	if silentMode {
		args = append(args, "--silent")
	}

	execStart := buildSystemdExecStart(exePath, args)

	opts := []*unit.UnitOption{
		unit.NewUnitOption("Unit", "Description", displayName),
		unit.NewUnitOption("Unit", "After", "network-online.target"),
		unit.NewUnitOption("Unit", "Wants", "network-online.target"),
		unit.NewUnitOption("Service", "Type", "simple"),
		unit.NewUnitOption("Service", "ExecStart", execStart),
		unit.NewUnitOption("Service", "Restart", "always"),
		unit.NewUnitOption("Service", "RestartSec", "5s"),
		unit.NewUnitOption("Service", "KillSignal", "SIGTERM"),
		unit.NewUnitOption("Install", "WantedBy", "multi-user.target"),
	}

	if strings.TrimSpace(description) != "" {
		opts = append(opts, unit.NewUnitOption("Unit", "Documentation", description))
	}

	if mode == linuxStartModeAutoDelayed {
		delayCmd := fmt.Sprintf("/bin/sleep %d", linuxDelayedStartSeconds)
		opts = append(opts, unit.NewUnitOption("Service", "ExecStartPre", delayCmd))
	}

	rendered, err := io.ReadAll(unit.Serialize(opts))
	if err != nil {
		return fmt.Errorf("serialize systemd unit: %w", err)
	}

	unitPath := filepath.Join(linuxSystemdUnitDirectory, unitName)
	if _, err := os.Stat(unitPath); err == nil {
		return fmt.Errorf("service %q already exists (%s)", serviceName, unitPath)
	} else if !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("check existing unit file %s: %w", unitPath, err)
	}

	if err := writeFileAtomic(unitPath, rendered, 0o644); err != nil {
		return fmt.Errorf("write unit file %s: %w", unitPath, err)
	}

	dbusConn, err := dbus.NewSystemConnectionContext(context.Background())
	if err != nil {
		return fmt.Errorf("connect to systemd dbus: %w", err)
	}
	defer dbusConn.Close()

	if err := dbusConn.ReloadContext(context.Background()); err != nil {
		return fmt.Errorf("reload systemd daemon: %w", err)
	}

	if mode == linuxStartModeManual {
		return nil
	}

	if _, _, err := dbusConn.EnableUnitFilesContext(context.Background(), []string{unitName}, false, true); err != nil {
		return fmt.Errorf("enable systemd unit %s: %w", unitName, err)
	}

	return nil
}

func uninstallService(serviceName string) error {
	if err := ensureSystemdAvailable(); err != nil {
		return err
	}

	unitName, err := normalizeServiceUnitName(serviceName)
	if err != nil {
		return err
	}

	dbusConn, err := dbus.NewSystemConnectionContext(context.Background())
	if err != nil {
		return fmt.Errorf("connect to systemd dbus: %w", err)
	}
	defer dbusConn.Close()

	_ = stopUnitByName(context.Background(), dbusConn, unitName)
	if _, err := dbusConn.DisableUnitFilesContext(context.Background(), []string{unitName}, false); err != nil {
		return fmt.Errorf("disable systemd unit %s: %w", unitName, err)
	}

	unitPath := filepath.Join(linuxSystemdUnitDirectory, unitName)
	if err := os.Remove(unitPath); err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("remove unit file %s: %w", unitPath, err)
	}

	if err := dbusConn.ReloadContext(context.Background()); err != nil {
		return fmt.Errorf("reload systemd daemon: %w", err)
	}

	return nil
}

func startService(serviceName string) error {
	if err := ensureSystemdAvailable(); err != nil {
		return err
	}

	unitName, err := normalizeServiceUnitName(serviceName)
	if err != nil {
		return err
	}

	dbusConn, err := dbus.NewSystemConnectionContext(context.Background())
	if err != nil {
		return fmt.Errorf("connect to systemd dbus: %w", err)
	}
	defer dbusConn.Close()

	ctx, cancel := context.WithTimeout(context.Background(), linuxServiceActionTimeout)
	defer cancel()

	result := make(chan string, 1)
	if _, err := dbusConn.StartUnitContext(ctx, unitName, "replace", result); err != nil {
		return fmt.Errorf("start unit %s: %w", unitName, err)
	}

	select {
	case status := <-result:
		if status != "done" {
			return fmt.Errorf("start unit %s did not complete successfully: %s", unitName, status)
		}
	case <-ctx.Done():
		return fmt.Errorf("start unit %s timed out: %w", unitName, ctx.Err())
	}

	return nil
}

func stopService(serviceName string) error {
	if err := ensureSystemdAvailable(); err != nil {
		return err
	}

	unitName, err := normalizeServiceUnitName(serviceName)
	if err != nil {
		return err
	}

	dbusConn, err := dbus.NewSystemConnectionContext(context.Background())
	if err != nil {
		return fmt.Errorf("connect to systemd dbus: %w", err)
	}
	defer dbusConn.Close()

	return stopUnitByName(context.Background(), dbusConn, unitName)
}

func runAsService(_ string, _ *slog.Logger, run func(context.Context) error) error {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	return run(ctx)
}

func parseLinuxServiceStartMode(mode string) (linuxStartMode, error) {
	switch strings.ToLower(strings.TrimSpace(mode)) {
	case "manual", "demand":
		return linuxStartModeManual, nil
	case "auto", "automatic":
		return linuxStartModeAuto, nil
	case "auto-delayed", "automatic-delayed", "delayed":
		return linuxStartModeAutoDelayed, nil
	default:
		return linuxStartModeManual, fmt.Errorf("invalid --start value %q (expected auto, auto-delayed, or manual)", mode)
	}
}

func normalizeServiceUnitName(serviceName string) (string, error) {
	name := strings.TrimSpace(serviceName)
	if name == "" {
		return "", fmt.Errorf("--name is required")
	}
	if strings.ContainsRune(name, '/') {
		return "", fmt.Errorf("service name %q must not contain '/'", serviceName)
	}
	if strings.HasSuffix(strings.ToLower(name), ".service") {
		return name, nil
	}
	return name + ".service", nil
}

func buildSystemdExecStart(executable string, args []string) string {
	allArgs := make([]string, 0, len(args)+1)
	allArgs = append(allArgs, executable)
	allArgs = append(allArgs, args...)

	quoted := make([]string, 0, len(allArgs))
	for _, arg := range allArgs {
		quoted = append(quoted, strconv.Quote(arg))
	}

	return strings.Join(quoted, " ")
}

func writeFileAtomic(path string, content []byte, mode os.FileMode) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}

	tmpPath := path + ".tmp"
	if err := os.WriteFile(tmpPath, content, mode); err != nil {
		return err
	}
	return os.Rename(tmpPath, path)
}

func ensureSystemdAvailable() error {
	if _, err := os.Stat("/run/systemd/system"); err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return fmt.Errorf("systemd is not available on this host")
		}
		return fmt.Errorf("check systemd runtime directory: %w", err)
	}

	proc1, err := os.ReadFile("/proc/1/comm")
	if err != nil {
		return fmt.Errorf("verify init system: %w", err)
	}

	if strings.TrimSpace(string(proc1)) != "systemd" {
		return fmt.Errorf("systemd is not the active init system (pid 1)")
	}

	return nil
}

func stopUnitByName(ctx context.Context, conn *dbus.Conn, unitName string) error {
	unitState, err := conn.GetUnitPropertyContext(ctx, unitName, "ActiveState")
	if err == nil {
		if activeState, ok := unitState.Value.Value().(string); ok && (activeState == "inactive" || activeState == "failed") {
			return nil
		}
	}

	stopCtx, cancel := context.WithTimeout(ctx, linuxServiceActionTimeout)
	defer cancel()

	result := make(chan string, 1)
	if _, err := conn.StopUnitContext(stopCtx, unitName, "replace", result); err != nil {
		return fmt.Errorf("stop unit %s: %w", unitName, err)
	}

	select {
	case status := <-result:
		if status != "done" {
			return fmt.Errorf("stop unit %s did not complete successfully: %s", unitName, status)
		}
	case <-stopCtx.Done():
		return fmt.Errorf("stop unit %s timed out: %w", unitName, stopCtx.Err())
	}

	return nil
}
