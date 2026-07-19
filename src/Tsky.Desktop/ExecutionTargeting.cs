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
