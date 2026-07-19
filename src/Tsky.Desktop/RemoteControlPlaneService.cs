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
        parameters.Fields["runner"] = Value.ForString(task.Runner ?? "exe");
        parameters.Fields["commandLine"] = Value.ForString(task.CommandLine ?? string.Empty);
        parameters.Fields["workingDirectory"] = Value.ForString(task.WorkingDirectory ?? string.Empty);

        foreach (var pair in task.Env)
        {
            parameters.Fields[$"env:{pair.Key}"] = Value.ForString(pair.Value ?? string.Empty);
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
                ExecutorId = string.Equals(task.Runner, "powershell", StringComparison.OrdinalIgnoreCase)
                    ? "powershell.v1"
                    : "process.v1",
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
            PlanDigestSha256 = Google.Protobuf.ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
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
}
