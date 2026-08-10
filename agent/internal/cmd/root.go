package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/enrolment"
	"github.com/matthogan/tsky/agent/internal/execution"
	"github.com/matthogan/tsky/agent/internal/identity"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	"github.com/matthogan/tsky/agent/internal/telemetry"
	"github.com/matthogan/tsky/agent/internal/transport"
	"github.com/spf13/cobra"
	"github.com/spf13/viper"
	"google.golang.org/grpc/grpclog"
)

type buildInfo struct {
	Version string
	Commit  string
	Date    string
}

type options struct {
	configPath string
	logLevel   string
	runtime    string
	silentMode bool
	build      buildInfo
}

const (
	agentLogMaxBytes                 int64 = 10 * 1024 * 1024
	agentLogMaxBackups                     = 5
	defaultWindowsServiceName              = "TskyAgent"
	defaultLinuxServiceName                = "tsky-agent"
	defaultWindowsServiceDisplayName       = "Tsky Agent"
	defaultWindowsServiceDescription       = "Tsky remote execution agent"
)

func defaultServiceName() string {
	if runtime.GOOS == "linux" {
		return defaultLinuxServiceName
	}

	return defaultWindowsServiceName
}

func Execute(version, commit, date string) {
	opts := &options{build: buildInfo{Version: version, Commit: commit, Date: date}}
	root := newRoot(opts)
	if err := root.Execute(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func newRoot(opts *options) *cobra.Command {
	cmd := &cobra.Command{
		Use:           "tsky-agent",
		Short:         "Tsky remote execution agent",
		SilenceUsage:  true,
		SilenceErrors: true,
	}
	cmd.PersistentFlags().StringVar(&opts.configPath, "config", config.DefaultPath(), "configuration file path")
	cmd.PersistentFlags().StringVar(&opts.logLevel, "log-level", "", "log level override")
	cmd.PersistentFlags().StringVar(&opts.runtime, "runtime-root", "", "runtime root override")
	cmd.PersistentFlags().BoolVar(&opts.silentMode, "silent", false, "disable agent logging output")
	_ = viper.BindPFlag("config", cmd.PersistentFlags().Lookup("config"))
	_ = viper.BindPFlag("log-level", cmd.PersistentFlags().Lookup("log-level"))
	_ = viper.BindPFlag("runtime-root", cmd.PersistentFlags().Lookup("runtime-root"))
	viper.SetEnvPrefix("TSKY_AGENT")
	viper.SetEnvKeyReplacer(strings.NewReplacer("-", "_"))
	viper.AutomaticEnv()
	cmd.AddCommand(
		newEnrolCommand(opts),
		newRunCommand(opts),
		newServiceRunCommand(opts),
		newValidateConfigCommand(opts),
		newStatusCommand(opts),
		newDoctorCommand(opts),
		newRotateKeyCommand(opts),
		newInstallServiceCommand(),
		newStartServiceCommand(),
		newStopServiceCommand(),
		newUninstallServiceCommand(),
		newVersionCommand(opts),
	)
	return cmd
}

func newEnrolCommand(opts *options) *cobra.Command {
	var controller, pendingAgent, token, controllerID, tlsPin string
	cmd := &cobra.Command{
		Use:   "enrol",
		Short: "Enrol this agent with a Tsky controller",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg := config.Defaults()
			if _, err := os.Stat(opts.configPath); err == nil {
				loaded, err := config.Load(opts.configPath)
				if err != nil {
					return err
				}
				cfg = loaded
			}
			overrideConfig(&cfg, opts)
			if controller != "" {
				cfg.Controller.URL = controller
			}
			if controllerID != "" {
				cfg.Controller.ControllerID = controllerID
			}
			if tlsPin != "" {
				cfg.Controller.TLSPinnedSHA256 = tlsPin
			}
			if cfg.Controller.URL == "" || pendingAgent == "" || token == "" || cfg.Controller.ControllerID == "" {
				return fmt.Errorf("--controller, --pending-agent, --token, and --controller-id are required")
			}
			keyPath := filepath.Join(cfg.Runtime.Root, "identity", "agent-key.pem")
			id, err := identity.GenerateFile(keyPath)
			if err != nil {
				return err
			}
			resp, err := enrolment.Enrol(cmd.Context(), enrolment.Options{
				ControllerURL: cfg.Controller.URL,
				PendingAgent:  pendingAgent,
				Token:         token,
				ControllerID:  cfg.Controller.ControllerID,
				TLSPin:        cfg.Controller.TLSPinnedSHA256,
				Identity:      id,
				Version:       opts.build.Version,
			})
			if err != nil {
				return err
			}
			cfg.Identity.AgentID = resp.AgentId
			cfg.Identity.KeyReference = id.Reference()
			if resp.ControllerId != "" {
				cfg.Controller.ControllerID = resp.ControllerId
			}
			if err := config.Save(opts.configPath, cfg); err != nil {
				return err
			}
			fmt.Fprintf(cmd.OutOrStdout(), "enrolled agent %s\n", resp.AgentId)
			return nil
		},
	}
	cmd.Flags().StringVar(&controller, "controller", "", "controller base URL")
	cmd.Flags().StringVar(&pendingAgent, "pending-agent", "", "pending agent ID from the desktop enrolment command")
	cmd.Flags().StringVar(&token, "token", "", "one-time enrolment token")
	cmd.Flags().StringVar(&controllerID, "controller-id", "", "expected controller ID")
	cmd.Flags().StringVar(&tlsPin, "tls-pin", "", "expected server certificate SHA-256 pin")
	return cmd
}

func newRunCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "run",
		Short: "Run the agent and connect to the controller",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			log := logger(cfg)
			client, err := newAgentClient(cfg, opts, log)
			if err != nil {
				return err
			}
			ctx, stop := signal.NotifyContext(cmd.Context(), os.Interrupt, syscall.SIGTERM)
			defer stop()
			log.Info("starting Tsky agent", "agentId", cfg.Identity.AgentID, "controller", cfg.Controller.URL)
			return client.Run(ctx)
		},
	}
}

func newServiceRunCommand(opts *options) *cobra.Command {
	var serviceName string
	cmd := &cobra.Command{
		Use:    "service-run",
		Short:  "Run the agent as a service entrypoint",
		Hidden: true,
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			log := logger(cfg)
			client, err := newAgentClient(cfg, opts, log)
			if err != nil {
				return err
			}
			log.Info("starting Tsky agent service", "serviceName", serviceName, "agentId", cfg.Identity.AgentID, "controller", cfg.Controller.URL)
			return runAsService(serviceName, log, client.Run)
		},
	}
	cmd.Flags().StringVar(&serviceName, "name", defaultServiceName(), "service name")
	return cmd
}

func newValidateConfigCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "validate-config",
		Short: "Validate the agent configuration",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			if err := cfg.Validate(); err != nil {
				return err
			}
			fmt.Fprintln(cmd.OutOrStdout(), "configuration is valid")
			return nil
		},
	}
}

func newStatusCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "status",
		Short: "Print local runtime status",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			store := runtimefs.New(cfg.Runtime.Root)
			states, err := store.Attempts(true)
			if err != nil {
				return err
			}
			out := map[string]any{
				"agentId":     cfg.Identity.AgentID,
				"controller":  cfg.Controller.URL,
				"runtimeRoot": cfg.Runtime.Root,
				"attempts":    states,
			}
			data, _ := json.MarshalIndent(out, "", "  ")
			fmt.Fprintln(cmd.OutOrStdout(), string(data))
			return nil
		},
	}
}

func newDoctorCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "doctor",
		Short: "Run local diagnostics",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			report := map[string]any{
				"schemaVersion": config.SchemaVersion,
				"goos":          runtime.GOOS,
				"goarch":        runtime.GOARCH,
				"configPath":    opts.configPath,
				"runtimeRoot":   cfg.Runtime.Root,
				"checks":        map[string]string{},
			}
			checks := report["checks"].(map[string]string)
			if err := os.MkdirAll(cfg.Runtime.Root, 0o700); err != nil {
				checks["runtimeWritable"] = err.Error()
			} else {
				checks["runtimeWritable"] = "ok"
			}
			if cfg.Identity.KeyReference != "" {
				if _, err := identity.Load(cfg.Identity.KeyReference); err != nil {
					checks["identityKey"] = err.Error()
				} else {
					checks["identityKey"] = "ok"
				}
			} else {
				checks["identityKey"] = "not configured"
			}
			data, _ := json.MarshalIndent(report, "", "  ")
			fmt.Fprintln(cmd.OutOrStdout(), string(data))
			return nil
		},
	}
}

func newRotateKeyCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "rotate-key",
		Short: "Generate a new local key file for a future controller rotation flow",
		RunE: func(cmd *cobra.Command, args []string) error {
			cfg, err := loadConfig(opts)
			if err != nil {
				return err
			}
			path := filepath.Join(cfg.Runtime.Root, "identity", "agent-key-"+time.Now().UTC().Format("20060102150405")+".pem")
			id, err := identity.GenerateFile(path)
			if err != nil {
				return err
			}
			fmt.Fprintf(cmd.OutOrStdout(), "generated key %s; controller-side rotation is not yet implemented\n", id.Reference())
			return nil
		},
	}
}

func newInstallServiceCommand() *cobra.Command {
	var serviceName, displayName, description, startMode string
	cmd := &cobra.Command{
		Use:   "install-service",
		Short: "Install the agent as a system service",
		RunE: func(cmd *cobra.Command, args []string) error {
			return installService(serviceName, displayName, description, startMode, cmd)
		},
		PreRunE: func(cmd *cobra.Command, args []string) error {
			serviceName = strings.TrimSpace(serviceName)
			displayName = strings.TrimSpace(displayName)
			description = strings.TrimSpace(description)
			startMode = strings.TrimSpace(startMode)
			if serviceName == "" {
				return fmt.Errorf("--name is required")
			}
			if displayName == "" {
				return fmt.Errorf("--display-name is required")
			}
			if startMode == "" {
				return fmt.Errorf("--start is required")
			}
			return nil
		},
	}
	cmd.Flags().StringVar(&serviceName, "name", defaultServiceName(), "service name")
	cmd.Flags().StringVar(&displayName, "display-name", defaultWindowsServiceDisplayName, "service display name")
	cmd.Flags().StringVar(&description, "description", defaultWindowsServiceDescription, "service description")
	cmd.Flags().StringVar(&startMode, "start", "auto-delayed", "service start mode: auto-delayed or manual")
	return cmd
}

func newStartServiceCommand() *cobra.Command {
	var serviceName string
	cmd := &cobra.Command{
		Use:   "start-service",
		Short: "Start the installed system service",
		RunE: func(cmd *cobra.Command, args []string) error {
			serviceName = strings.TrimSpace(serviceName)
			if serviceName == "" {
				return fmt.Errorf("--name is required")
			}
			return startService(serviceName)
		},
	}
	cmd.Flags().StringVar(&serviceName, "name", defaultServiceName(), "service name")
	return cmd
}

func newStopServiceCommand() *cobra.Command {
	var serviceName string
	cmd := &cobra.Command{
		Use:   "stop-service",
		Short: "Stop the installed system service",
		RunE: func(cmd *cobra.Command, args []string) error {
			serviceName = strings.TrimSpace(serviceName)
			if serviceName == "" {
				return fmt.Errorf("--name is required")
			}
			return stopService(serviceName)
		},
	}
	cmd.Flags().StringVar(&serviceName, "name", defaultServiceName(), "service name")
	return cmd
}

func newUninstallServiceCommand() *cobra.Command {
	var serviceName string
	cmd := &cobra.Command{
		Use:   "uninstall-service",
		Short: "Uninstall the system service",
		RunE: func(cmd *cobra.Command, args []string) error {
			serviceName = strings.TrimSpace(serviceName)
			if serviceName == "" {
				return fmt.Errorf("--name is required")
			}
			return uninstallService(serviceName)
		},
	}
	cmd.Flags().StringVar(&serviceName, "name", defaultServiceName(), "service name")
	return cmd
}

func newAgentClient(cfg config.Config, opts *options, log *slog.Logger) (*transport.Client, error) {
	id, err := identity.Load(cfg.Identity.KeyReference)
	if err != nil {
		return nil, err
	}
	store := runtimefs.New(cfg.Runtime.Root)
	if err := store.Init(); err != nil {
		return nil, err
	}
	engine := execution.New(cfg, store, log)
	tel := telemetry.New(cfg, store, opts.build.Version)
	client := transport.New(cfg, id, store, engine, tel, log)
	return client, nil
}

func newVersionCommand(opts *options) *cobra.Command {
	return &cobra.Command{
		Use:   "version",
		Short: "Print version information",
		RunE: func(cmd *cobra.Command, args []string) error {
			fmt.Fprintf(cmd.OutOrStdout(), "tsky-agent %s commit=%s date=%s\n", opts.build.Version, opts.build.Commit, opts.build.Date)
			return nil
		},
	}
}

func loadConfig(opts *options) (config.Config, error) {
	cfg, err := config.Load(opts.configPath)
	if err != nil {
		return cfg, err
	}
	overrideConfig(&cfg, opts)
	return cfg, cfg.Validate()
}

func overrideConfig(cfg *config.Config, opts *options) {
	if opts.logLevel != "" {
		cfg.Logging.Level = opts.logLevel
	}
	if opts.runtime != "" {
		cfg.Runtime.Root = opts.runtime
	}
	if opts.silentMode {
		cfg.Logging.Silent = true
	}
	if v := viper.GetString("log-level"); v != "" {
		cfg.Logging.Level = v
	}
	if v := viper.GetString("runtime-root"); v != "" {
		cfg.Runtime.Root = v
	}
	if viper.IsSet("silent") {
		cfg.Logging.Silent = viper.GetBool("silent")
	}
}

func logger(cfg config.Config) *slog.Logger {
	if cfg.Logging.Silent {
		grpclog.SetLoggerV2(grpclog.NewLoggerV2(io.Discard, io.Discard, io.Discard))
		return slog.New(slog.NewJSONHandler(io.Discard, &slog.HandlerOptions{Level: slog.LevelError}))
	}

	level := slog.LevelInfo
	switch strings.ToLower(cfg.Logging.Level) {
	case "debug":
		level = slog.LevelDebug
	case "warn", "warning":
		level = slog.LevelWarn
	case "error":
		level = slog.LevelError
	}
	writer := io.Writer(os.Stderr)
	logPath := filepath.Join(cfg.Runtime.Root, "logs", "agent.log")
	if err := os.MkdirAll(filepath.Dir(logPath), 0o700); err == nil {
		if err := rotateLogFile(logPath, agentLogMaxBytes, agentLogMaxBackups); err != nil {
			fmt.Fprintf(os.Stderr, "warning: cannot rotate persistent log file %q: %v\n", logPath, err)
		}
		if file, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600); err == nil {
			writer = io.MultiWriter(os.Stderr, file)
		} else {
			fmt.Fprintf(os.Stderr, "warning: cannot open persistent log file %q: %v\n", logPath, err)
		}
	} else {
		fmt.Fprintf(os.Stderr, "warning: cannot create log directory for %q: %v\n", logPath, err)
	}
	opts := &slog.HandlerOptions{Level: level}
	if cfg.Logging.Format == "text" {
		return slog.New(slog.NewTextHandler(writer, opts))
	}
	return slog.New(slog.NewJSONHandler(writer, opts))
}

func rotateLogFile(path string, maxBytes int64, maxBackups int) error {
	if maxBytes <= 0 || maxBackups < 1 {
		return nil
	}

	info, err := os.Stat(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}

	if info.Size() < maxBytes {
		return nil
	}

	oldest := fmt.Sprintf("%s.%d", path, maxBackups)
	_ = os.Remove(oldest)

	for idx := maxBackups - 1; idx >= 1; idx-- {
		src := fmt.Sprintf("%s.%d", path, idx)
		dst := fmt.Sprintf("%s.%d", path, idx+1)

		if _, err := os.Stat(src); err == nil {
			if err := os.Rename(src, dst); err != nil {
				return err
			}
			continue
		} else if !os.IsNotExist(err) {
			return err
		}
	}

	return os.Rename(path, path+".1")
}
