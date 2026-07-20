namespace tsky;

public enum AgentTransientStateKind
{
    Idle,
    Dispatching,
    Running,
    Completed,
    Failed
}

public static class AgentTransientStatePolicy
{
    public static AgentTransientStateKind CoerceForConcurrency(
        AgentTransientStateKind current,
        AgentTransientStateKind requested,
        int activeRunningAttempts)
    {
        if (activeRunningAttempts <= 0)
        {
            return requested;
        }

        // While any attempts are still active, prevent terminal/non-active labels
        // from overriding the row and keep the state stable as Running.
        return requested switch
        {
            AgentTransientStateKind.Running => AgentTransientStateKind.Running,
            _ => AgentTransientStateKind.Running
        };
    }
}
