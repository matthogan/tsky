using System.Security.Cryptography;
using System.IO;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Tsky.Agent.V1;

namespace tsky;

public sealed class RemoteControlPlaneService
{
    private readonly AgentGatewayHost _gatewayHost;
    private readonly AgentRegistryService _registryService;

    public RemoteControlPlaneService(AgentGatewayHost gatewayHost, AgentRegistryService registryService)
    {
        _gatewayHost = gatewayHost;
        _registryService = registryService;
    }

    public RemoteDispatchReceipt TryDispatchTask(
        LauncherTask task,
        ExecutionTargetSelection selection,
        IReadOnlyDictionary<string, string> resolvedInputs,
        IReadOnlyDictionary<string, string> resolvedOutputs)
    {
        if (!_gatewayHost.IsRunning)
        {
            return new RemoteDispatchReceipt
            {
                Dispatched = false,
                Message = "Gateway is not running."
            };
        }

        var registry = _registryService.Load();
        var target = ResolveTargetAgent(registry, selection, task);
        if (target is null)
        {
            return new RemoteDispatchReceipt
            {
                Dispatched = false,
                Message = BuildUnavailableMessage(selection)
            };
        }

        var runId = "run_" + Guid.NewGuid().ToString("N");
        var attemptId = "attempt_" + Guid.NewGuid().ToString("N");
        var offerId = "offer_" + Guid.NewGuid().ToString("N");

        var runPlan = new RunPlan
        {
            SchemaVersion = 1,
            Source = new RunSource
            {
                Kind = "task",
                SourceId = TaskIdentity.BuildKey(task),
                Revision = 1
            },
            Policy = new RunPolicy
            {
                DisconnectPolicy = DisconnectPolicy.ContinueAndReport,
                MaximumDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(60, task.TimeoutSeconds))),
                AllowShellExecutors = true,
                MaximumOutputBytesPerStream = (ulong)Math.Max(1024, task.MaxCapturedOutputChars)
            }
        };

        var parameters = new Struct();
        if (!TryBuildProcessV1Parameters(task, parameters, out var commandError))
        {
            return new RemoteDispatchReceipt
            {
                Dispatched = false,
                Message = commandError
            };
        }

        var step = new RunStep
        {
            StepId = "step_" + Guid.NewGuid().ToString("N"),
            SourceTaskId = TaskIdentity.BuildKey(task),
            DisplayName = task.Name,
            Timeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(60, task.TimeoutSeconds))),
            ContinueOnError = task.IgnoreExitCode,
            Command = new ExecutorCommand
            {
                ExecutorId = "process.v1",
                ExecutorVersion = "1",
                Parameters = parameters
            }
        };

        foreach (var pair in resolvedInputs)
        {
            step.Inputs[pair.Key] = new PortValue { StringValue = pair.Value ?? string.Empty };
        }

        foreach (var pair in resolvedOutputs)
        {
            step.DeclaredOutputs.Add(new DeclaredOutput
            {
                OutputId = pair.Key,
                Name = pair.Key,
                Kind = "port",
                MediaType = "text/plain",
                Required = false
            });
        }

        runPlan.Steps.Add(step);

        var offer = new RunOffer
        {
            OfferId = offerId,
            RunId = runId,
            AttemptId = attemptId,
            PlanDigestSha256 = ByteString.CopyFrom(ComputePlanDigest(runPlan)),
            Plan = runPlan,
            OfferExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(2)),
            DisconnectPolicy = DisconnectPolicy.ContinueAndReport
        };

        if (!_gatewayHost.TryDispatchRunOffer(target.AgentId, offer, out var reason))
        {
            return new RemoteDispatchReceipt
            {
                Dispatched = false,
                Message = reason
            };
        }

        return new RemoteDispatchReceipt
        {
            Dispatched = true,
            AgentId = target.AgentId,
            RunId = runId,
            AttemptId = attemptId,
            Message = $"Dispatched to {target.EffectiveDisplayName}."
        };
    }

    public bool TryCancel(string agentId, string runId, string attemptId, out string message)
    {
        var cancel = new CancelRun
        {
            RunId = runId,
            AttemptId = attemptId,
            Reason = "user_cancel",
            GracefulTerminationPeriod = Duration.FromTimeSpan(TimeSpan.FromSeconds(5))
        };

        var cancelled = _gatewayHost.TrySendCancel(agentId, cancel, out var reason);
        message = cancelled ? "Remote cancellation sent." : reason;
        return cancelled;
    }

    public int RequestReconciliationAll()
    {
        return _gatewayHost.RequestReconciliationFromAll();
    }

    public RemoteSchedulePublishReceipt TryPublishSchedule(ScheduledItem item, string desktopInstanceId)
    {
        if (!item.IsAgentOwned) { return new RemoteSchedulePublishReceipt { Message = "Schedule uses desktop-local execution." }; }
        if (!string.IsNullOrWhiteSpace(item.PendingPreviousAgentId)) { return new RemoteSchedulePublishReceipt { Message = $"Waiting for agent '{item.PendingPreviousAgentId}' to acknowledge removal before publishing the new target." }; }
        if (!_gatewayHost.IsRunning) { return new RemoteSchedulePublishReceipt { Message = "Gateway is not running." }; }

        var selection = BuildScheduleTargetSelection(item);
        var target = ResolveScheduleTargetAgent(_registryService.Load(), selection, item.TaskSnapshot ?? item.ChainSnapshot.FirstOrDefault() ?? new LauncherTask());
        if (target is null) { return new RemoteSchedulePublishReceipt { Message = BuildUnavailableMessage(selection) }; }
        if (!SupportsScheduleV1(target)) { return new RemoteSchedulePublishReceipt { AgentId = target.AgentId, Message = "Selected agent does not advertise schedule.v1; desktop scheduling remains available as fallback." }; }

        if (!TryBuildScheduleRunPlan(item, out var plan, out var error)) { return new RemoteSchedulePublishReceipt { AgentId = target.AgentId, Message = error }; }
        var revision = Math.Max(1UL, item.Revision);
        var upsert = new ScheduleUpsert
        {
            Identity = new ScheduleIdentity { ScheduleId = item.Id, Revision = revision, SourceDesktopInstanceId = desktopInstanceId },
            IdempotencyKey = $"{desktopInstanceId}:{item.Id}:{revision}",
            Spec = new ScheduleSpec
            {
                ScheduleId = item.Id,
                DisplayName = item.DisplayName,
                Enabled = item.IsEnabled,
                TriggerType = item.IsRecurring ? ScheduleTriggerType.Recurring : ScheduleTriggerType.OneShot,
                TriggerAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(item.TriggerAtUtc, DateTimeKind.Utc)),
                RecurrenceRule = item.IsRecurring ? item.RecurrenceExpression ?? string.Empty : string.Empty,
                TriggerWindowSeconds = (uint)Math.Max(1, item.TriggerWindowSeconds),
                TargetAgentId = target.AgentId,
                TargetPoolId = item.TargetPoolId ?? string.Empty,
                PayloadRunPlanSnapshot = plan,
                MisfirePolicy = ScheduleMisfirePolicy.FireOnceWithinWindow,
                OverlapPolicy = ScheduleOverlapPolicy.SkipIfRunning,
                MaxCatchUpOccurrences = 1,
                CreatedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(item.CreatedAtUtc, DateTimeKind.Utc)),
                UpdatedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(item.UpdatedAtUtc, DateTimeKind.Utc))
            }
        };
        if (item.AcceptedRevision > 0) { upsert.ExpectedPreviousRevision = item.AcceptedRevision; }
        if (!_gatewayHost.TrySendScheduleUpsert(target.AgentId, upsert, out var reason)) { return new RemoteSchedulePublishReceipt { AgentId = target.AgentId, Message = reason }; }
        return new RemoteSchedulePublishReceipt { Sent = true, AgentId = target.AgentId, Message = $"Schedule revision {revision} published to {target.EffectiveDisplayName}." };
    }

    public RemoteSchedulePublishReceipt TryDeleteSchedule(ScheduledItem item)
    {
        var agentId = item.PublishedAgentId ?? item.TargetAgentId;
        if (string.IsNullOrWhiteSpace(agentId)) { return new RemoteSchedulePublishReceipt { Message = "Schedule has no deployed agent." }; }
        var revision = Math.Max(1UL, item.Revision + 1);
        var request = new ScheduleDelete { ScheduleId = item.Id, Revision = revision, IdempotencyKey = $"delete:{item.Id}:{revision}" };
        return _gatewayHost.TrySendScheduleDelete(agentId, request, out var reason)
            ? new RemoteSchedulePublishReceipt { Sent = true, AgentId = agentId, Message = "Schedule deletion sent." }
            : new RemoteSchedulePublishReceipt { AgentId = agentId, Message = reason };
    }

	public RemoteSchedulePublishReceipt TryDeleteSchedule(DesktopScheduleTombstone tombstone)
	{
		var request = new ScheduleDelete { ScheduleId = tombstone.ScheduleId, Revision = tombstone.Revision, IdempotencyKey = $"delete:{tombstone.ScheduleId}:{tombstone.Revision}" };
		return _gatewayHost.TrySendScheduleDelete(tombstone.AgentId, request, out var reason)
			? new RemoteSchedulePublishReceipt { Sent = true, AgentId = tombstone.AgentId, Message = "Schedule deletion sent." }
			: new RemoteSchedulePublishReceipt { AgentId = tombstone.AgentId, Message = reason };
	}

    public bool TryRequestScheduleSync(string agentId, SchedulerConfiguration configuration, out string reason)
    {
        var request = new ScheduleSyncRequest { SyncId = "schedule_sync_" + Guid.NewGuid().ToString("N") };
		var acknowledged = configuration.AgentOccurrenceCursors.TryGetValue(agentId, out var cursor) ? cursor : 0UL;
        foreach (var item in configuration.Items.Where(item => item.IsAgentOwned && (string.Equals(item.PublishedAgentId, agentId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.TargetAgentId, agentId, StringComparison.OrdinalIgnoreCase))))
        {
            request.KnownRevisions[item.Id] = item.Revision;
            acknowledged = Math.Max(acknowledged, item.LastAcknowledgedOccurrenceSequence);
        }
        request.AcknowledgedOccurrenceSequence = acknowledged;
        return _gatewayHost.TrySendScheduleSyncRequest(agentId, request, out reason);
    }

    internal static bool SupportsScheduleV1(AgentDefinition agent) =>
        TapProtocolVersioning.SupportsScheduleV1(agent.ProtocolVersion)
        && (agent.Features.Contains("schedule.v1", StringComparer.OrdinalIgnoreCase)
            || agent.Extensions.Contains("schedule.v1", StringComparer.OrdinalIgnoreCase));

    internal static bool ShouldDesktopEvaluateSchedule(ScheduledItem item, AgentRegistry registry)
    {
        if (!string.IsNullOrWhiteSpace(item.PendingPreviousAgentId))
        {
            return false;
        }

        if (!item.IsAgentOwned || item.AcceptedRevision < item.Revision)
        {
            return true;
        }

        var agentId = item.PublishedAgentId ?? item.TargetAgentId;
        var agent = registry.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        return agent is null || !SupportsScheduleV1(agent);
    }

	private static AgentDefinition? ResolveScheduleTargetAgent(AgentRegistry registry, ExecutionTargetSelection selection, LauncherTask task)
	{
		if (selection.Mode == ExecutionTargetMode.SpecificAgent) { return ResolveTargetAgent(registry, selection, task); }
		var capable = registry.Agents
			.Where(agent => agent.State is AgentDefinitionState.Online or AgentDefinitionState.Busy)
			.Where(agent => !agent.IsDraining && SupportsScheduleV1(agent));
		if (selection.Mode == ExecutionTargetMode.AgentPool)
		{
			capable = capable.Where(agent => agent.Labels.TryGetValue("pool", out var pool) && string.Equals(pool, selection.PoolId?.Trim(), StringComparison.OrdinalIgnoreCase));
		}
		return capable.OrderBy(agent => agent.ActiveRuns).ThenBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
			?? ResolveTargetAgent(registry, selection, task);
	}

    private static ExecutionTargetSelection BuildScheduleTargetSelection(ScheduledItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PublishedAgentId)) { return new ExecutionTargetSelection { Mode = ExecutionTargetMode.SpecificAgent, AgentId = item.PublishedAgentId }; }
        return item.ExecutionTargetMode switch
        {
            "agent" => new ExecutionTargetSelection { Mode = ExecutionTargetMode.SpecificAgent, AgentId = item.TargetAgentId },
            "pool" => new ExecutionTargetSelection { Mode = ExecutionTargetMode.AgentPool, PoolId = item.TargetPoolId },
            "auto" => new ExecutionTargetSelection { Mode = ExecutionTargetMode.Automatic },
            _ => ExecutionTargetSelection.LocalOnly()
        };
    }

    internal static bool TryBuildScheduleRunPlan(ScheduledItem item, out RunPlan plan, out string error)
    {
        plan = new RunPlan
        {
            SchemaVersion = 1,
            Source = new RunSource { Kind = item.TargetKind == ScheduledTargetKind.TaskSnapshot ? "task" : "chain", SourceId = item.Id, Revision = item.Revision },
            Policy = new RunPolicy { DisconnectPolicy = DisconnectPolicy.ContinueAndReport, AllowShellExecutors = true, MaximumOutputBytesPerStream = 1024 * 1024 }
        };
        error = string.Empty;
        var tasks = item.TargetKind == ScheduledTargetKind.TaskSnapshot
            ? (item.TaskSnapshot is null ? [] : new List<LauncherTask> { item.TaskSnapshot })
            : item.ChainSnapshot;
        if (tasks.Count == 0) { error = "Schedule snapshot contains no tasks."; return false; }
        string? previousStepId = null;
        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            var task = tasks[taskIndex];
            var parameters = new Struct();
            if (!TryBuildProcessV1Parameters(task, parameters, out error)) { return false; }
            var stepId = $"step_{taskIndex + 1:D4}";
            var step = new RunStep
            {
                StepId = stepId,
                SourceTaskId = TaskIdentity.BuildKey(task),
                DisplayName = task.Name,
                Timeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(60, task.TimeoutSeconds))),
                ContinueOnError = task.IgnoreExitCode,
                Command = new ExecutorCommand { ExecutorId = "process.v1", ExecutorVersion = "1", Parameters = parameters }
            };
            if (previousStepId is not null) { step.DependsOnStepIds.Add(previousStepId); }
            foreach (var pair in task.Inputs) { step.Inputs[pair.Key] = new PortValue { StringValue = pair.Value ?? string.Empty }; }
            foreach (var pair in task.Outputs) { step.DeclaredOutputs.Add(new DeclaredOutput { OutputId = pair.Key, Name = pair.Key, Kind = "port", MediaType = "text/plain" }); }
            plan.Steps.Add(step);
            previousStepId = stepId;
        }
        var maximumSeconds = tasks.Sum(task => Math.Max(60, task.TimeoutSeconds));
        plan.Policy.MaximumDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(maximumSeconds));
        plan.Policy.MaximumOutputBytesPerStream = (ulong)Math.Max(1024, tasks.Max(task => task.MaxCapturedOutputChars));
        return true;
    }

    private static AgentDefinition? ResolveTargetAgent(AgentRegistry registry, ExecutionTargetSelection selection, LauncherTask task)
    {
        var eligible = registry.Agents
            .Where(agent => agent.State is AgentDefinitionState.Online or AgentDefinitionState.Busy)
            .Where(agent => !agent.IsDraining)
            .ToList();

        if (selection.Mode == ExecutionTargetMode.SpecificAgent)
        {
            return eligible.FirstOrDefault(agent => string.Equals(agent.AgentId, selection.AgentId, StringComparison.OrdinalIgnoreCase));
        }

        if (selection.Mode == ExecutionTargetMode.AgentPool)
        {
            if (string.IsNullOrWhiteSpace(selection.PoolId))
            {
                return null;
            }

            var poolId = selection.PoolId.Trim();
            return eligible
                .Where(agent => agent.Labels.TryGetValue("pool", out var value) && string.Equals(value, poolId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(agent => agent.ActiveRuns)
                .ThenBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        if (selection.Mode == ExecutionTargetMode.Automatic)
        {
            return eligible
                .OrderBy(agent => agent.ActiveRuns)
                .ThenBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return null;
    }

    private static string BuildUnavailableMessage(ExecutionTargetSelection selection)
    {
        return selection.Mode switch
        {
            ExecutionTargetMode.SpecificAgent => "Selected agent is unavailable or offline.",
            ExecutionTargetMode.AgentPool => "No eligible agent found in the selected pool.",
            ExecutionTargetMode.Automatic => "No eligible online agent is currently available.",
            _ => "No remote execution target was selected."
        };
    }

    private static bool TryBuildProcessV1Parameters(LauncherTask task, Struct parameters, out string error)
    {
        error = string.Empty;

        var runner = TaskProcessStartInfoFactory.NormalizeRunner(task.Runner);
        var commandLine = task.CommandLine?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            error = "Remote dispatch requires a non-empty commandLine.";
            return false;
        }

        string executable;
        List<string> arguments;
        switch (runner)
        {
            case "exe":
                var tokens = WindowsCommandLineParser.Tokenize(commandLine);
                if (tokens.Count == 0)
                {
                    error = "Runner 'exe' requires an executable in commandLine.";
                    return false;
                }

                executable = tokens[0];
                arguments = tokens.Skip(1).ToList();
                break;

            case "cmd":
                executable = "cmd.exe";
                arguments = ["/c", commandLine];
                break;

            case "powershell":
                executable = "pwsh";
                arguments = ["-ExecutionPolicy", "Bypass", "-Command", commandLine];
                break;

            case "python":
                executable = "python";
                arguments = WindowsCommandLineParser.Tokenize(commandLine);
                break;

            default:
                error = "Runner must be one of: exe, cmd, powershell, python.";
                return false;
        }

        parameters.Fields["executable"] = Value.ForString(executable);
        parameters.Fields["arguments"] = new Value
        {
            ListValue = new ListValue
            {
                Values = { arguments.Select(Value.ForString) }
            }
        };

        if (!string.IsNullOrWhiteSpace(task.WorkingDirectory))
        {
            parameters.Fields["workingDirectory"] = Value.ForString(task.WorkingDirectory.Trim());
        }

        if (task.Env.Count > 0)
        {
            var environment = new Struct();
            foreach (var pair in task.Env)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                environment.Fields[pair.Key.Trim()] = Value.ForString(pair.Value ?? string.Empty);
            }

            if (environment.Fields.Count > 0)
            {
                parameters.Fields["environment"] = Value.ForStruct(environment);
            }
        }

        return true;
    }

    internal static byte[] ComputePlanDigest(RunPlan plan)
    {
        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream, leaveOpen: true)
        {
            Deterministic = true
        };
        plan.WriteTo(output);
        output.Flush();
        return SHA256.HashData(stream.ToArray());
    }
}
