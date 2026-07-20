namespace tsky;

public sealed record AgentDispatchLifecycleContext(
    string? TaskName,
    string? AgentId,
    string? RunId,
    string? AttemptId);

public static class AgentDispatchLifecycleMarkers
{
    public static string Start(AgentDispatchLifecycleContext context)
    {
        return $"START task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} corr={BuildCorrelationId(context.TaskName, context.AgentId, context.AttemptId)}";
    }

    public static string Error(string? taskName, string? agentId, string? reason, string? correlationAttemptToken = "dispatch-failed")
    {
        return $"ERROR task={NormalizeToken(taskName)} agent={NormalizeToken(agentId)} corr={BuildCorrelationId(taskName, agentId, correlationAttemptToken)} reason={NormalizeToken(reason)}";
    }

    public static string End(AgentDispatchLifecycleContext context, string terminalState, string reason)
    {
        return $"END task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} corr={BuildCorrelationId(context.TaskName, context.AgentId, context.AttemptId)} state={terminalState} reason={NormalizeToken(reason)}";
    }

    public static string ReconcileRequested(AgentDispatchLifecycleContext context, int probes)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=requested probes={probes} reason=disconnect_detected";
    }

    public static string ReconcileAwaitingReconnect(AgentDispatchLifecycleContext context)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=awaiting_reconnect note=completion_not_inferred";
    }

    public static string ReconcileRequestedOnReconnect(AgentDispatchLifecycleContext context, int probes)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=requested_on_reconnect probes={probes}";
    }

    public static string ReconcileRecovered(AgentDispatchLifecycleContext context, string terminalState, string reconciliationId)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=recovered state={terminalState} reconciliationId={NormalizeToken(reconciliationId)}";
    }

    public static string ReconcileUnrecovered(AgentDispatchLifecycleContext context, string reconciliationId)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=unrecovered reconciliationId={NormalizeToken(reconciliationId)}";
    }

    public static string ReconcileUnrecoveredInFlight(AgentDispatchLifecycleContext context, string state, bool processIsKnownRunning, string reconciliationId)
    {
        return $"RECONCILE task={NormalizeToken(context.TaskName)} agent={NormalizeToken(context.AgentId)} run={NormalizeToken(context.RunId)} attempt={NormalizeToken(context.AttemptId)} status=unrecovered state={NormalizeToken(state)} knownRunning={processIsKnownRunning} reconciliationId={NormalizeToken(reconciliationId)}";
    }

    public static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "n/a";
        }

        var token = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        token = System.Text.RegularExpressions.Regex.Replace(token, "\\s+", "_");
        return token.Length == 0 ? "n/a" : token;
    }

    public static string BuildCorrelationId(string? taskName, string? agentId, string? attemptId)
    {
        return $"{NormalizeToken(taskName)}-{NormalizeToken(agentId)}-{NormalizeToken(attemptId)}";
    }
}
