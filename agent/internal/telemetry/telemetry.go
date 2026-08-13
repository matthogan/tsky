package telemetry

import (
	"os"
	"runtime"
	"strconv"
	"time"

	"github.com/matthogan/tsky/agent/internal/config"
	"github.com/matthogan/tsky/agent/internal/runtimefs"
	agentv1 "github.com/matthogan/tsky/agent/protocol/tsky/agent/v1"
	"google.golang.org/protobuf/types/known/durationpb"
)

type Collector struct {
	cfg             config.Config
	store           *runtimefs.Store
	version         string
	started         time.Time
	scheduleMetrics func() ScheduleMetrics
}

type ScheduleMetrics struct {
	ActiveCount, PausedCount                                         uint32
	OccurrenceBacklogCount, ReplayQueueDepth                         uint64
	LastTickUTC                                                      time.Time
	UpsertAcceptedTotal, UpsertRejectedTotal, OccurrenceStartedTotal uint64
	OccurrenceTerminal                                               map[string]uint64
}

func New(cfg config.Config, store *runtimefs.Store, version string) *Collector {
	return &Collector{cfg: cfg, store: store, version: version, started: time.Now()}
}

func (c *Collector) SetScheduleMetrics(provider func() ScheduleMetrics) {
	c.scheduleMetrics = provider
}

func (c *Collector) Snapshot(active, queued uint32, rejectedByCapacity uint64, draining bool) *agentv1.AgentSnapshot {
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
		Health:                c.health(active, queued, rejectedByCapacity),
		Uptime:                durationpb.New(time.Since(c.started)),
	}
}

func (c *Collector) Heartbeat(seq uint64, active, queued uint32, rejectedByCapacity uint64, draining bool) *agentv1.AgentHeartbeat {
	return &agentv1.AgentHeartbeat{
		HeartbeatSequence: seq,
		Uptime:            durationpb.New(time.Since(c.started)),
		Capacity:          c.Capacity(active),
		Draining:          draining,
		Health:            c.health(active, queued, rejectedByCapacity),
	}
}

func (c *Collector) Capability(active, queued uint32, rejectedByCapacity uint64) *agentv1.CapabilityReport {
	report := &agentv1.CapabilityReport{
		AgentId:               c.cfg.Identity.AgentID,
		ImplementationName:    "tsky-agent",
		ImplementationVersion: c.version,
		Bindings: []*agentv1.ProtocolBinding{{
			Profile: "tap-grpc",
			Version: "1",
		}},
		Executors: c.executors(),
		Features:  []string{"durable-runtime-store", "process-output-spooling", "schedule.v1"},
		Labels: map[string]string{
			"os":                             runtime.GOOS,
			"arch":                           runtime.GOARCH,
			"admission.active_runs":          strconv.FormatUint(uint64(active), 10),
			"admission.queued_runs":          strconv.FormatUint(uint64(queued), 10),
			"admission.rejected_by_capacity": strconv.FormatUint(rejectedByCapacity, 10),
			"admission.max_concurrent_runs":  strconv.Itoa(c.cfg.Execution.MaxConcurrentRuns),
			"admission.max_pending_queue":    strconv.Itoa(c.cfg.Execution.MaxPendingQueueDepth),
		},
		Capacity:   c.Capacity(active),
		Extensions: []*agentv1.ExtensionSupport{{Identifier: "schedule.v1", Version: "1", Required: false}},
	}
	c.addScheduleLabels(report.Labels)
	return report
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

func (c *Collector) health(active, queued uint32, rejectedByCapacity uint64) *agentv1.AgentHealth {
	health := &agentv1.AgentHealth{
		State: agentv1.HealthState_HEALTH_STATE_HEALTHY,
		Issues: []*agentv1.HealthIssue{{
			Code:    "admission_control",
			Message: "admission counters",
			Detail: map[string]string{
				"activeRuns":         strconv.FormatUint(uint64(active), 10),
				"queuedRuns":         strconv.FormatUint(uint64(queued), 10),
				"rejectedByCapacity": strconv.FormatUint(rejectedByCapacity, 10),
			},
		}},
	}
	if c.scheduleMetrics != nil {
		m := c.scheduleMetrics()
		detail := map[string]string{
			"activeCount": strconv.FormatUint(uint64(m.ActiveCount), 10), "pausedCount": strconv.FormatUint(uint64(m.PausedCount), 10),
			"occurrenceBacklogCount": strconv.FormatUint(m.OccurrenceBacklogCount, 10), "replayQueueDepth": strconv.FormatUint(m.ReplayQueueDepth, 10),
			"lastTickUtc": formatTime(m.LastTickUTC), "upsertAcceptedTotal": strconv.FormatUint(m.UpsertAcceptedTotal, 10),
			"upsertRejectedTotal": strconv.FormatUint(m.UpsertRejectedTotal, 10), "occurrenceStartedTotal": strconv.FormatUint(m.OccurrenceStartedTotal, 10),
		}
		for outcome, count := range m.OccurrenceTerminal {
			detail["occurrenceTerminalTotal."+outcome] = strconv.FormatUint(count, 10)
		}
		health.Issues = append(health.Issues, &agentv1.HealthIssue{Code: "schedule_runtime", Message: "agent schedule counters", Detail: detail})
	}
	return health
}

func (c *Collector) addScheduleLabels(labels map[string]string) {
	if c.scheduleMetrics == nil {
		return
	}
	m := c.scheduleMetrics()
	labels["schedule.active_count"] = strconv.FormatUint(uint64(m.ActiveCount), 10)
	labels["schedule.paused_count"] = strconv.FormatUint(uint64(m.PausedCount), 10)
	labels["schedule.occurrence_backlog_count"] = strconv.FormatUint(m.OccurrenceBacklogCount, 10)
	labels["schedule.replay_queue_depth"] = strconv.FormatUint(m.ReplayQueueDepth, 10)
	labels["schedule.last_tick_utc"] = formatTime(m.LastTickUTC)
	labels["schedule_upsert_accepted_total"] = strconv.FormatUint(m.UpsertAcceptedTotal, 10)
	labels["schedule_upsert_rejected_total"] = strconv.FormatUint(m.UpsertRejectedTotal, 10)
	labels["schedule_occurrence_started_total"] = strconv.FormatUint(m.OccurrenceStartedTotal, 10)
	for outcome, count := range m.OccurrenceTerminal {
		labels["schedule_occurrence_terminal_total."+outcome] = strconv.FormatUint(count, 10)
	}
}

func formatTime(value time.Time) string {
	if value.IsZero() {
		return ""
	}
	return value.UTC().Format(time.RFC3339Nano)
}
