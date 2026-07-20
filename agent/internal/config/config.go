package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"time"

	"gopkg.in/yaml.v3"
)

const SchemaVersion = 1

type Config struct {
	SchemaVersion int              `yaml:"schemaVersion" json:"schemaVersion"`
	Controller    ControllerConfig `yaml:"controller" json:"controller"`
	Identity      IdentityConfig   `yaml:"identity" json:"identity"`
	Runtime       RuntimeConfig    `yaml:"runtime" json:"runtime"`
	Execution     ExecutionConfig  `yaml:"execution" json:"execution"`
	Connection    ConnectionConfig `yaml:"connection" json:"connection"`
	Logging       LoggingConfig    `yaml:"logging" json:"logging"`
}

type ControllerConfig struct {
	URL             string `yaml:"url" json:"url"`
	ControllerID    string `yaml:"controllerId" json:"controllerId"`
	TLSPinnedSHA256 string `yaml:"tlsPinnedSha256" json:"tlsPinnedSha256"`
	TLSCACertPath   string `yaml:"tlsCaCertPath" json:"tlsCaCertPath"`
}

type IdentityConfig struct {
	AgentID      string `yaml:"agentId" json:"agentId"`
	KeyID        string `yaml:"keyId" json:"keyId"`
	KeyReference string `yaml:"keyReference" json:"keyReference"`
}

type RuntimeConfig struct {
	Root                      string `yaml:"root" json:"root"`
	CompletedRunRetentionDays int    `yaml:"completedRunRetentionDays" json:"completedRunRetentionDays"`
	OutputRetentionDays       int    `yaml:"outputRetentionDays" json:"outputRetentionDays"`
	ArtifactRetentionDays     int    `yaml:"artifactRetentionDays" json:"artifactRetentionDays"`
	WarningFreeDiskBytes      uint64 `yaml:"warningFreeDiskBytes" json:"warningFreeDiskBytes"`
	CriticalFreeDiskBytes     uint64 `yaml:"criticalFreeDiskBytes" json:"criticalFreeDiskBytes"`
}

type ExecutionConfig struct {
	MaxConcurrentRuns           int      `yaml:"maxConcurrentRuns" json:"maxConcurrentRuns"`
	DefaultTimeoutSeconds       int      `yaml:"defaultTimeoutSeconds" json:"defaultTimeoutSeconds"`
	CancellationGraceSeconds    int      `yaml:"cancellationGraceSeconds" json:"cancellationGraceSeconds"`
	MaximumOutputBytesPerStream uint64   `yaml:"maximumOutputBytesPerStream" json:"maximumOutputBytesPerStream"`
	AllowedExecutors            []string `yaml:"allowedExecutors" json:"allowedExecutors"`
	AllowedWorkingRoots         []string `yaml:"allowedWorkingRoots" json:"allowedWorkingRoots"`
	AllowShellExecutor          bool     `yaml:"allowShellExecutor" json:"allowShellExecutor"`
}

type ConnectionConfig struct {
	HeartbeatSeconds        int `yaml:"heartbeatSeconds" json:"heartbeatSeconds"`
	ReconnectMinimumSeconds int `yaml:"reconnectMinimumSeconds" json:"reconnectMinimumSeconds"`
	ReconnectMaximumSeconds int `yaml:"reconnectMaximumSeconds" json:"reconnectMaximumSeconds"`
}

type LoggingConfig struct {
	Level  string `yaml:"level" json:"level"`
	Format string `yaml:"format" json:"format"`
}

func Defaults() Config {
	root := defaultRuntimeRoot()
	return Config{
		SchemaVersion: SchemaVersion,
		Runtime: RuntimeConfig{
			Root:                      root,
			CompletedRunRetentionDays: 30,
			OutputRetentionDays:       14,
			ArtifactRetentionDays:     7,
			WarningFreeDiskBytes:      5 * 1024 * 1024 * 1024,
			CriticalFreeDiskBytes:     1 * 1024 * 1024 * 1024,
		},
		Execution: ExecutionConfig{
			MaxConcurrentRuns:           1,
			DefaultTimeoutSeconds:       3600,
			CancellationGraceSeconds:    10,
			MaximumOutputBytesPerStream: 100 * 1024 * 1024,
			AllowedExecutors:            []string{"process.v1"},
			AllowedWorkingRoots:         []string{filepath.Join(root, "work")},
			AllowShellExecutor:          false,
		},
		Connection: ConnectionConfig{
			HeartbeatSeconds:        10,
			ReconnectMinimumSeconds: 1,
			ReconnectMaximumSeconds: 60,
		},
		Logging: LoggingConfig{
			Level:  "info",
			Format: "json",
		},
	}
}

func DefaultPath() string {
	if p := os.Getenv("TSKY_AGENT_CONFIG"); p != "" {
		return p
	}
	switch runtime.GOOS {
	case "windows":
		if v := os.Getenv("PROGRAMDATA"); v != "" {
			return filepath.Join(v, "Tsky", "Agent", "config.yaml")
		}
	}
	if runtime.GOOS == "linux" && os.Geteuid() == 0 {
		return "/etc/tsky-agent/config.yaml"
	}
	if v := os.Getenv("XDG_CONFIG_HOME"); v != "" {
		return filepath.Join(v, "tsky-agent", "config.yaml")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".config", "tsky-agent", "config.yaml")
}

func Load(path string) (Config, error) {
	if path == "" {
		path = DefaultPath()
	}
	cfg := Defaults()
	f, err := os.Open(path)
	if err != nil {
		return cfg, err
	}
	defer f.Close()
	dec := yaml.NewDecoder(f)
	dec.KnownFields(true)
	if err := dec.Decode(&cfg); err != nil {
		return cfg, err
	}
	applyEnv(&cfg)
	applyEmbeddedEnv(&cfg)
	if err := cfg.Validate(); err != nil {
		return cfg, err
	}
	return cfg, nil
}

func Save(path string, cfg Config) error {
	if path == "" {
		path = DefaultPath()
	}
	if err := cfg.Validate(); err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp := path + ".tmp"
	data, err := yaml.Marshal(cfg)
	if err != nil {
		return err
	}
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func (c Config) Validate() error {
	var errs []error
	if c.SchemaVersion != SchemaVersion {
		errs = append(errs, fmt.Errorf("unsupported schemaVersion %d", c.SchemaVersion))
	}
	if c.Runtime.Root == "" {
		errs = append(errs, errors.New("runtime.root is required"))
	}
	if c.Execution.MaxConcurrentRuns < 1 {
		errs = append(errs, errors.New("execution.maxConcurrentRuns must be at least 1"))
	}
	if c.Execution.DefaultTimeoutSeconds < 1 {
		errs = append(errs, errors.New("execution.defaultTimeoutSeconds must be positive"))
	}
	if c.Execution.CancellationGraceSeconds < 0 {
		errs = append(errs, errors.New("execution.cancellationGraceSeconds must not be negative"))
	}
	if c.Execution.MaximumOutputBytesPerStream == 0 {
		errs = append(errs, errors.New("execution.maximumOutputBytesPerStream must be positive"))
	}
	if len(c.Execution.AllowedExecutors) == 0 {
		errs = append(errs, errors.New("execution.allowedExecutors must contain at least one executor"))
	}
	if c.Connection.HeartbeatSeconds < 1 {
		errs = append(errs, errors.New("connection.heartbeatSeconds must be positive"))
	}
	if c.Connection.ReconnectMinimumSeconds < 1 || c.Connection.ReconnectMaximumSeconds < c.Connection.ReconnectMinimumSeconds {
		errs = append(errs, errors.New("connection reconnect bounds are invalid"))
	}
	if c.Logging.Format == "" {
		errs = append(errs, errors.New("logging.format is required"))
	}
	for _, root := range c.Execution.AllowedWorkingRoots {
		if strings.TrimSpace(root) == "" {
			errs = append(errs, errors.New("execution.allowedWorkingRoots contains an empty path"))
		}
	}
	return errors.Join(errs...)
}

func (c Config) DefaultTimeout() time.Duration {
	return time.Duration(c.Execution.DefaultTimeoutSeconds) * time.Second
}

func (c Config) CancellationGrace() time.Duration {
	return time.Duration(c.Execution.CancellationGraceSeconds) * time.Second
}

func (c Config) HeartbeatInterval() time.Duration {
	return time.Duration(c.Connection.HeartbeatSeconds) * time.Second
}

func applyEnv(cfg *Config) {
	if v := os.Getenv("TSKY_AGENT_CONTROLLER_URL"); v != "" {
		cfg.Controller.URL = v
	}
	if v := os.Getenv("TSKY_AGENT_RUNTIME_ROOT"); v != "" {
		cfg.Runtime.Root = v
	}
	if v := os.Getenv("TSKY_AGENT_LOG_LEVEL"); v != "" {
		cfg.Logging.Level = v
	}
}

var (
	percentEnvPattern = regexp.MustCompile(`%([A-Za-z_][A-Za-z0-9_]*)%`)
	dollarEnvPattern  = regexp.MustCompile(`\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)`)
)

func applyEmbeddedEnv(cfg *Config) {
	cfg.Controller.URL = expandEmbeddedEnv(cfg.Controller.URL)
	cfg.Controller.TLSCACertPath = expandEmbeddedEnv(cfg.Controller.TLSCACertPath)
	cfg.Identity.KeyReference = expandEmbeddedEnv(cfg.Identity.KeyReference)
	cfg.Runtime.Root = expandEmbeddedEnv(cfg.Runtime.Root)
	for idx := range cfg.Execution.AllowedWorkingRoots {
		cfg.Execution.AllowedWorkingRoots[idx] = expandEmbeddedEnv(cfg.Execution.AllowedWorkingRoots[idx])
	}
}

func expandEmbeddedEnv(value string) string {
	if strings.TrimSpace(value) == "" {
		return value
	}
	expanded := percentEnvPattern.ReplaceAllStringFunc(value, func(match string) string {
		groups := percentEnvPattern.FindStringSubmatch(match)
		if len(groups) != 2 {
			return match
		}
		if resolved, ok := os.LookupEnv(groups[1]); ok {
			return resolved
		}
		return match
	})
	expanded = dollarEnvPattern.ReplaceAllStringFunc(expanded, func(match string) string {
		groups := dollarEnvPattern.FindStringSubmatch(match)
		if len(groups) != 3 {
			return match
		}
		name := groups[1]
		if name == "" {
			name = groups[2]
		}
		if resolved, ok := os.LookupEnv(name); ok {
			return resolved
		}
		return match
	})
	return expanded
}

func defaultRuntimeRoot() string {
	switch runtime.GOOS {
	case "windows":
		if v := os.Getenv("PROGRAMDATA"); v != "" {
			return filepath.Join(v, "Tsky", "Agent", "runtime")
		}
		if v := os.Getenv("LOCALAPPDATA"); v != "" {
			return filepath.Join(v, "Tsky", "Agent", "runtime")
		}
	case "linux":
		if os.Geteuid() == 0 {
			return "/var/lib/tsky-agent/runtime"
		}
	}
	if v := os.Getenv("XDG_DATA_HOME"); v != "" {
		return filepath.Join(v, "tsky-agent", "runtime")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".local", "share", "tsky-agent", "runtime")
}
