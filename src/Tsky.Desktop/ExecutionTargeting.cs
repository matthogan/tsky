namespace tsky;

public enum ExecutionTargetMode
{
    Local,
    SpecificAgent,
    AgentPool,
    Automatic
}

public sealed class ExecutionTargetSelection
{
    public ExecutionTargetMode Mode { get; set; } = ExecutionTargetMode.Local;

    public string? AgentId { get; set; }

    public string? PoolId { get; set; }

    public static ExecutionTargetSelection LocalOnly() => new();
}

public sealed class RemoteDispatchReceipt
{
    public bool Dispatched { get; init; }

    public string? AgentId { get; init; }

    public string? RunId { get; init; }

    public string? AttemptId { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class RemoteSchedulePublishReceipt
{
    public bool Sent { get; init; }
    public string? AgentId { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class ScheduleExecutionTargetOption
{
    public required ExecutionTargetMode Mode { get; init; }

    public string? AgentId { get; init; }

    public string? PoolId { get; init; }

    public required string DisplayName { get; init; }

    public bool IsValid { get; init; } = true;

    public string? ValidationMessage { get; init; }

    public string Key => Mode switch
    {
        ExecutionTargetMode.SpecificAgent => $"agent:{AgentId}",
        ExecutionTargetMode.AgentPool => $"pool:{PoolId}",
        ExecutionTargetMode.Automatic => "auto",
        _ => "local"
    };

    public override string ToString() => DisplayName;
}

public static class ScheduleExecutionTargetCatalog
{
    public static IReadOnlyList<ScheduleExecutionTargetOption> Build(AgentRegistry registry, ScheduledItem item)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(item);

        var options = new List<ScheduleExecutionTargetOption>
        {
            new() { Mode = ExecutionTargetMode.Local, DisplayName = "Local — this desktop" },
            new() { Mode = ExecutionTargetMode.Automatic, DisplayName = "Automatic — choose an available agent" }
        };

        foreach (var agent in registry.Agents
                     .Where(agent => !string.IsNullOrWhiteSpace(agent.AgentId))
                     .OrderBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase))
        {
            var valid = IsConfigurableAgent(agent);
            options.Add(new ScheduleExecutionTargetOption
            {
                Mode = ExecutionTargetMode.SpecificAgent,
                AgentId = agent.AgentId,
                DisplayName = $"Agent — {agent.EffectiveDisplayName} ({agent.State.ToString().ToLowerInvariant()})",
                IsValid = valid,
                ValidationMessage = valid ? null : $"Agent '{agent.EffectiveDisplayName}' cannot be selected while it is {agent.State.ToString().ToLowerInvariant()}."
            });
        }

        var pools = registry.Agents
            .Where(IsConfigurableAgent)
            .Select(agent => agent.Labels.TryGetValue("pool", out var pool) ? pool?.Trim() : null)
            .Where(pool => !string.IsNullOrWhiteSpace(pool))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pool => pool, StringComparer.OrdinalIgnoreCase);
        foreach (var pool in pools)
        {
            options.Add(new ScheduleExecutionTargetOption
            {
                Mode = ExecutionTargetMode.AgentPool,
                PoolId = pool,
                DisplayName = $"Pool — {pool}"
            });
        }

        AddMissingCurrentTarget(options, item);
        return options;
    }

    public static ScheduleExecutionTargetOption? FindSelected(IReadOnlyList<ScheduleExecutionTargetOption> options, ScheduledItem item)
    {
        var key = GetTargetKey(item);
        return options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static string? Validate(ScheduleExecutionTargetOption? option)
    {
        if (option is null)
        {
            return "Select an execution target.";
        }

        if (!option.IsValid)
        {
            return option.ValidationMessage ?? "The selected execution target is not available.";
        }

        if (option.Mode == ExecutionTargetMode.SpecificAgent && string.IsNullOrWhiteSpace(option.AgentId))
        {
            return "Select a specific agent.";
        }

        if (option.Mode == ExecutionTargetMode.AgentPool && string.IsNullOrWhiteSpace(option.PoolId))
        {
            return "Select an agent pool.";
        }

        return null;
    }

    public static void Apply(ScheduledItem item, ScheduleExecutionTargetOption option)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(option);

        item.ExecutionTargetMode = option.Mode switch
        {
            ExecutionTargetMode.SpecificAgent => "agent",
            ExecutionTargetMode.AgentPool => "pool",
            ExecutionTargetMode.Automatic => "auto",
            _ => "local"
        };
        item.TargetAgentId = option.Mode == ExecutionTargetMode.SpecificAgent ? option.AgentId : null;
        item.TargetPoolId = option.Mode == ExecutionTargetMode.AgentPool ? option.PoolId : null;
    }

    public static bool TargetsEqual(ScheduledItem left, ScheduledItem right) =>
        string.Equals(GetTargetKey(left), GetTargetKey(right), StringComparison.OrdinalIgnoreCase);

    public static bool CanRetainPublishedAgent(ScheduledItem item, string? publishedAgentId, AgentRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(publishedAgentId) || !item.IsAgentOwned)
        {
            return false;
        }

        var agent = registry.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentId, publishedAgentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null || !IsConfigurableAgent(agent))
        {
            return false;
        }

        return item.ExecutionTargetMode switch
        {
            "agent" => string.Equals(item.TargetAgentId, publishedAgentId, StringComparison.OrdinalIgnoreCase),
            "pool" => agent.Labels.TryGetValue("pool", out var pool)
                && string.Equals(pool?.Trim(), item.TargetPoolId?.Trim(), StringComparison.OrdinalIgnoreCase),
            "auto" => true,
            _ => false
        };
    }

    public static string DescribeReadiness(ScheduleExecutionTargetOption? option, AgentRegistry registry)
    {
        var validation = Validate(option);
        if (validation is not null)
        {
            return validation;
        }

        if (option!.Mode == ExecutionTargetMode.Local)
        {
            return "This desktop evaluates the schedule and starts each occurrence locally.";
        }

        var candidates = registry.Agents.Where(IsConfigurableAgent);
        if (option.Mode == ExecutionTargetMode.SpecificAgent)
        {
            candidates = candidates.Where(agent => string.Equals(agent.AgentId, option.AgentId, StringComparison.OrdinalIgnoreCase));
        }
        else if (option.Mode == ExecutionTargetMode.AgentPool)
        {
            candidates = candidates.Where(agent => agent.Labels.TryGetValue("pool", out var pool)
                && string.Equals(pool?.Trim(), option.PoolId?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var agents = candidates.ToList();
        var connectedScheduleAgents = agents.Count(agent =>
            (agent.State is AgentDefinitionState.Online or AgentDefinitionState.Busy)
            && !agent.IsDraining
            && RemoteControlPlaneService.SupportsScheduleV1(agent));
        if (connectedScheduleAgents > 0)
        {
            return connectedScheduleAgents == 1
                ? "Ready to publish through TAP 1.1 schedule.v1."
                : $"Ready to publish; {connectedScheduleAgents} compatible agents are currently available.";
        }

        if (agents.Any(RemoteControlPlaneService.SupportsScheduleV1))
        {
            return "Valid target, but no compatible target is connected. The desktop fallback remains active until publication is acknowledged.";
        }

        return "No schedule.v1-capable target is known. The desktop will retain legacy scheduling fallback.";
    }

    private static bool IsConfigurableAgent(AgentDefinition agent) => agent.State is not
        (AgentDefinitionState.Pending or AgentDefinitionState.Enrolling or AgentDefinitionState.Revoked or AgentDefinitionState.Disabled or AgentDefinitionState.Incompatible);

    private static string GetTargetKey(ScheduledItem item) => (item.ExecutionTargetMode ?? "local").Trim().ToLowerInvariant() switch
    {
        "agent" => $"agent:{item.TargetAgentId}",
        "pool" => $"pool:{item.TargetPoolId}",
        "auto" => "auto",
        _ => "local"
    };

    private static void AddMissingCurrentTarget(List<ScheduleExecutionTargetOption> options, ScheduledItem item)
    {
        var key = GetTargetKey(item);
        if (options.Any(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)) || key is "local" or "auto")
        {
            return;
        }

        var isAgent = key.StartsWith("agent:", StringComparison.OrdinalIgnoreCase);
        var value = isAgent ? item.TargetAgentId : item.TargetPoolId;
        options.Add(new ScheduleExecutionTargetOption
        {
            Mode = isAgent ? ExecutionTargetMode.SpecificAgent : ExecutionTargetMode.AgentPool,
            AgentId = isAgent ? value : null,
            PoolId = isAgent ? null : value,
            DisplayName = $"{(isAgent ? "Agent" : "Pool")} — {value ?? "missing"} (unavailable)",
            IsValid = false,
            ValidationMessage = $"The configured {(isAgent ? "agent" : "pool")} '{value ?? ""}' no longer exists or is not selectable. Choose another execution target."
        });
    }
}
