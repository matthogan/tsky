namespace tsky.Tests;

public sealed class AgentTransientStatePolicyTests
{
    [Theory]
    [InlineData(AgentTransientStateKind.Completed)]
    [InlineData(AgentTransientStateKind.Failed)]
    [InlineData(AgentTransientStateKind.Idle)]
    [InlineData(AgentTransientStateKind.Dispatching)]
    public void CoerceForConcurrency_WithActiveAttempts_CoercesToRunning(AgentTransientStateKind requested)
    {
        var resolved = AgentTransientStatePolicy.CoerceForConcurrency(
            AgentTransientStateKind.Running,
            requested,
            activeRunningAttempts: 2);

        Assert.Equal(AgentTransientStateKind.Running, resolved);
    }

    [Fact]
    public void CoerceForConcurrency_NoActiveAttempts_AllowsTerminalTransition()
    {
        var resolved = AgentTransientStatePolicy.CoerceForConcurrency(
            AgentTransientStateKind.Running,
            AgentTransientStateKind.Completed,
            activeRunningAttempts: 0);

        Assert.Equal(AgentTransientStateKind.Completed, resolved);
    }

    [Fact]
    public void CoerceForConcurrency_RapidStateFlipSequence_RemainsStableWhileActive()
    {
        var state = AgentTransientStateKind.Idle;

        state = AgentTransientStatePolicy.CoerceForConcurrency(state, AgentTransientStateKind.Dispatching, activeRunningAttempts: 1);
        state = AgentTransientStatePolicy.CoerceForConcurrency(state, AgentTransientStateKind.Running, activeRunningAttempts: 1);
        state = AgentTransientStatePolicy.CoerceForConcurrency(state, AgentTransientStateKind.Failed, activeRunningAttempts: 1);
        state = AgentTransientStatePolicy.CoerceForConcurrency(state, AgentTransientStateKind.Completed, activeRunningAttempts: 1);

        Assert.Equal(AgentTransientStateKind.Running, state);

        state = AgentTransientStatePolicy.CoerceForConcurrency(state, AgentTransientStateKind.Completed, activeRunningAttempts: 0);
        Assert.Equal(AgentTransientStateKind.Completed, state);
    }

    [Fact]
    public void CoerceForConcurrency_MultiAgentIndependence_ByInputIsolation()
    {
        var agentA = AgentTransientStatePolicy.CoerceForConcurrency(
            AgentTransientStateKind.Running,
            AgentTransientStateKind.Completed,
            activeRunningAttempts: 2);
        var agentB = AgentTransientStatePolicy.CoerceForConcurrency(
            AgentTransientStateKind.Running,
            AgentTransientStateKind.Completed,
            activeRunningAttempts: 0);

        Assert.Equal(AgentTransientStateKind.Running, agentA);
        Assert.Equal(AgentTransientStateKind.Completed, agentB);
    }
}
