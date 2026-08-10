//go:build windows

package cmd

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/spf13/cobra"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

const serviceStopWaitTimeout = 20 * time.Second

type windowsServiceRunner struct {
	log *slog.Logger
	run func(context.Context) error
}

func installService(serviceName, displayName, description, startMode string, cmd *cobra.Command) error {
	startType, delayedAutoStart, err := parseServiceStartMode(startMode)
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

	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to service manager: %w", err)
	}
	defer manager.Disconnect()

	existing, err := manager.OpenService(serviceName)
	if err == nil {
		existing.Close()
		return fmt.Errorf("service %q already exists", serviceName)
	}

	service, err := manager.CreateService(serviceName, exePath, mgr.Config{
		DisplayName:      displayName,
		Description:      description,
		StartType:        startType,
		DelayedAutoStart: delayedAutoStart,
	}, args...)
	if err != nil {
		return fmt.Errorf("create service %q: %w", serviceName, err)
	}
	defer service.Close()

	return nil
}

func uninstallService(serviceName string) error {
	if err := stopService(serviceName); err != nil {
		return err
	}

	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to service manager: %w", err)
	}
	defer manager.Disconnect()

	service, err := manager.OpenService(serviceName)
	if err != nil {
		return fmt.Errorf("open service %q: %w", serviceName, err)
	}
	defer service.Close()

	if err := service.Delete(); err != nil {
		return fmt.Errorf("delete service %q: %w", serviceName, err)
	}

	return nil
}

func startService(serviceName string) error {
	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to service manager: %w", err)
	}
	defer manager.Disconnect()

	service, err := manager.OpenService(serviceName)
	if err != nil {
		return fmt.Errorf("open service %q: %w", serviceName, err)
	}
	defer service.Close()

	state, err := service.Query()
	if err == nil && (state.State == svc.Running || state.State == svc.StartPending) {
		return nil
	}

	if err := service.Start(); err != nil {
		return fmt.Errorf("start service %q: %w", serviceName, err)
	}

	deadline := time.Now().Add(serviceStopWaitTimeout)
	for time.Now().Before(deadline) {
		time.Sleep(300 * time.Millisecond)
		updated, queryErr := service.Query()
		if queryErr != nil {
			return fmt.Errorf("query service %q after start: %w", serviceName, queryErr)
		}
		if updated.State == svc.Running {
			return nil
		}
	}

	return fmt.Errorf("service %q did not reach running state within %s", serviceName, serviceStopWaitTimeout)
}

func stopService(serviceName string) error {
	manager, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("connect to service manager: %w", err)
	}
	defer manager.Disconnect()

	service, err := manager.OpenService(serviceName)
	if err != nil {
		return fmt.Errorf("open service %q: %w", serviceName, err)
	}
	defer service.Close()

	state, err := service.Query()
	if err == nil && state.State == svc.Stopped {
		return nil
	}

	if _, err := service.Control(svc.Stop); err != nil {
		return fmt.Errorf("stop service %q: %w", serviceName, err)
	}

	deadline := time.Now().Add(serviceStopWaitTimeout)
	for time.Now().Before(deadline) {
		time.Sleep(300 * time.Millisecond)
		updated, queryErr := service.Query()
		if queryErr != nil {
			return fmt.Errorf("query service %q during stop: %w", serviceName, queryErr)
		}
		if updated.State == svc.Stopped {
			return nil
		}
	}

	return fmt.Errorf("service %q did not stop within %s", serviceName, serviceStopWaitTimeout)

}

func runAsService(serviceName string, log *slog.Logger, run func(context.Context) error) error {
	if strings.TrimSpace(serviceName) == "" {
		serviceName = defaultWindowsServiceName
	}

	return svc.Run(serviceName, &windowsServiceRunner{log: log, run: run})
}

func parseServiceStartMode(mode string) (uint32, bool, error) {
	switch strings.ToLower(strings.TrimSpace(mode)) {
	case "auto", "automatic", "auto-delayed", "automatic-delayed", "delayed":
		return mgr.StartAutomatic, true, nil
	case "manual", "demand":
		return mgr.StartManual, false, nil
	default:
		return 0, false, fmt.Errorf("invalid --start value %q (expected auto, auto-delayed, or manual)", mode)
	}
}

func (r *windowsServiceRunner) Execute(_ []string, requests <-chan svc.ChangeRequest, statusChanges chan<- svc.Status) (bool, uint32) {
	const accepts = svc.AcceptStop | svc.AcceptShutdown

	statusChanges <- svc.Status{State: svc.StartPending}

	runCtx, cancel := context.WithCancel(context.Background())
	defer cancel()

	runCompleted := make(chan error, 1)
	go func() {
		runCompleted <- r.run(runCtx)
	}()

	currentStatus := svc.Status{State: svc.Running, Accepts: accepts}
	statusChanges <- currentStatus

	for {
		select {
		case req := <-requests:
			switch req.Cmd {
			case svc.Interrogate:
				statusChanges <- currentStatus
			case svc.Stop, svc.Shutdown:
				currentStatus.State = svc.StopPending
				statusChanges <- currentStatus
				cancel()

				err := <-runCompleted
				if err != nil {
					r.log.Error("agent service stopped with error", "error", err)
				}
				return false, 0
			}
		case err := <-runCompleted:
			if err != nil {
				r.log.Error("agent service terminated with error", "error", err)
				return false, 1
			}
			return false, 0
		}
	}
}
