package telemetry

import (
	"os"
	"runtime"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/types/known/durationpb"
)

type Collector struct {
	cfg     config.Config
	store   *runtimefs.Store
	version string
	started time.Time
}

func New(cfg config.Config, store *runtimefs.Store, version string) *Collector {
	return &Collector{cfg: cfg, store: store, version: version, started: time.Now()}
}

func (c *Collector) Snapshot(active uint32, draining bool) *agentv1.AgentSnapshot {
	host, _ := os.Hostname()
	availability := agentv1.AgentAvailability_AGENT_AVAILABILITY_IDLE
	if active > 0 {
		availability = agentv1.AgentAvailability_AGENT_AVAILABILITY_BUSY
	}
	if draining {
		availability = agentv1.AgentAvailability_AGENT_AVAILABILITY_DRAINING
	}
	return &agentv1.AgentSnapshot{
		AgentId:               c.cfg.Identity.AgentID,
		Hostname:              host,
		OperatingSystem:       runtime.GOOS,
		Architecture:          runtime.GOARCH,
		ImplementationName:    "tsky-agent",
		ImplementationVersion: c.version,
		Availability:          availability,
		Capacity:              c.Capacity(active),
		Health:                &agentv1.AgentHealth{State: agentv1.HealthState_HEALTH_STATE_HEALTHY},
		Uptime:                durationpb.New(time.Since(c.started)),
	}
}

func (c *Collector) Heartbeat(seq uint64, active uint32, draining bool) *agentv1.AgentHeartbeat {
	return &agentv1.AgentHeartbeat{
		HeartbeatSequence: seq,
		Uptime:            durationpb.New(time.Since(c.started)),
		Capacity:          c.Capacity(active),
		Draining:          draining,
		Health:            &agentv1.AgentHealth{State: agentv1.HealthState_HEALTH_STATE_HEALTHY},
	}
}

func (c *Collector) Capability(active uint32) *agentv1.CapabilityReport {
	return &agentv1.CapabilityReport{
		AgentId:               c.cfg.Identity.AgentID,
		ImplementationName:    "tsky-agent",
		ImplementationVersion: c.version,
		Bindings: []*agentv1.ProtocolBinding{{
			Profile: "tap-grpc",
			Version: "1",
		}},
		Executors: c.executors(),
		Features:  []string{"durable-runtime-store", "process-output-spooling"},
		Labels: map[string]string{
			"os":   runtime.GOOS,
			"arch": runtime.GOARCH,
		},
		Capacity: c.Capacity(active),
	}
}

func (c *Collector) Capacity(active uint32) *agentv1.Capacity {
	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)
	return &agentv1.Capacity{
		ActiveRuns:           active,
		MaximumRuns:          uint32(c.cfg.Execution.MaxConcurrentRuns),
		MemoryUsedBytes:      mem.Sys,
		RuntimeDiskFreeBytes: diskFree(c.store.Root()),
	}
}

func (c *Collector) executors() []*agentv1.ExecutorCapability {
	out := make([]*agentv1.ExecutorCapability, 0, len(c.cfg.Execution.AllowedExecutors))
	for _, id := range c.cfg.Execution.AllowedExecutors {
		out = append(out, &agentv1.ExecutorCapability{
			ExecutorId:      id,
			ExecutorVersion: "1",
			Platforms:       []string{runtime.GOOS + "/" + runtime.GOARCH},
		})
	}
	return out
}
