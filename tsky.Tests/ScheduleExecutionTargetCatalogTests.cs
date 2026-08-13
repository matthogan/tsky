namespace tsky.Tests;

public sealed class ScheduleExecutionTargetCatalogTests
{
    [Fact]
    public void Build_ProvidesLocalAutomaticAgentsAndDistinctPools()
    {
        var registry = new AgentRegistry
        {
            Agents =
            [
                CreateAgent("agt-b", "Builder B", AgentDefinitionState.Offline, "build"),
                CreateAgent("agt-a", "Builder A", AgentDefinitionState.Online, "build"),
                CreateAgent("agt-c", "Ops", AgentDefinitionState.Busy, "ops")
            ]
        };

        var options = ScheduleExecutionTargetCatalog.Build(registry, new ScheduledItem());

        Assert.Contains(options, option => option.Mode == ExecutionTargetMode.Local);
        Assert.Contains(options, option => option.Mode == ExecutionTargetMode.Automatic);
        Assert.Equal(3, options.Count(option => option.Mode == ExecutionTargetMode.SpecificAgent));
        Assert.Equal(["build", "ops"], options
            .Where(option => option.Mode == ExecutionTargetMode.AgentPool)
            .Select(option => option.PoolId));
    }

    [Fact]
    public void Build_PreservesMissingConfiguredTargetButMakesItInvalid()
    {
        var item = new ScheduledItem { ExecutionTargetMode = "agent", TargetAgentId = "agt-missing" };

        var options = ScheduleExecutionTargetCatalog.Build(new AgentRegistry(), item);
        var selected = ScheduleExecutionTargetCatalog.FindSelected(options, item);

        Assert.NotNull(selected);
        Assert.False(selected.IsValid);
        Assert.Contains("no longer exists", ScheduleExecutionTargetCatalog.Validate(selected), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AgentDefinitionState.Pending)]
    [InlineData(AgentDefinitionState.Enrolling)]
    [InlineData(AgentDefinitionState.Incompatible)]
    [InlineData(AgentDefinitionState.Revoked)]
    [InlineData(AgentDefinitionState.Disabled)]
    public void Build_MarksAdministrativelyUnavailableAgentsInvalid(AgentDefinitionState state)
    {
        var registry = new AgentRegistry { Agents = [CreateAgent("agt-1", "Agent 1", state)] };
        var item = new ScheduledItem { ExecutionTargetMode = "agent", TargetAgentId = "agt-1" };

        var selected = ScheduleExecutionTargetCatalog.FindSelected(
            ScheduleExecutionTargetCatalog.Build(registry, item),
            item);

        Assert.NotNull(selected);
        Assert.False(selected.IsValid);
        Assert.NotNull(ScheduleExecutionTargetCatalog.Validate(selected));
    }

    [Fact]
    public void Apply_StoresOnlyTheFieldsUsedByTheSelectedMode()
    {
        var item = new ScheduledItem
        {
            ExecutionTargetMode = "agent",
            TargetAgentId = "agt-old",
            TargetPoolId = "old-pool"
        };

        ScheduleExecutionTargetCatalog.Apply(item, new ScheduleExecutionTargetOption
        {
            Mode = ExecutionTargetMode.AgentPool,
            PoolId = "build",
            DisplayName = "Pool — build"
        });

        Assert.Equal("pool", item.ExecutionTargetMode);
        Assert.Null(item.TargetAgentId);
        Assert.Equal("build", item.TargetPoolId);
    }

    [Fact]
    public void CanRetainPublishedAgent_UsesStablePoolAndAutomaticAssignments()
    {
        var registry = new AgentRegistry
        {
            Agents = [CreateAgent("agt-1", "Agent 1", AgentDefinitionState.Offline, "build")]
        };

        Assert.True(ScheduleExecutionTargetCatalog.CanRetainPublishedAgent(
            new ScheduledItem { ExecutionTargetMode = "pool", TargetPoolId = "build" },
            "agt-1",
            registry));
        Assert.True(ScheduleExecutionTargetCatalog.CanRetainPublishedAgent(
            new ScheduledItem { ExecutionTargetMode = "auto" },
            "agt-1",
            registry));
        Assert.False(ScheduleExecutionTargetCatalog.CanRetainPublishedAgent(
            new ScheduledItem { ExecutionTargetMode = "agent", TargetAgentId = "agt-2" },
            "agt-1",
            registry));
    }

    [Fact]
    public void DescribeReadiness_DistinguishesProtocolPublicationFromDesktopFallback()
    {
        var compatible = CreateAgent("agt-1", "Agent 1", AgentDefinitionState.Online);
        compatible.ProtocolVersion = "1.1";
        compatible.Extensions = ["schedule.v1"];
        var option = new ScheduleExecutionTargetOption
        {
            Mode = ExecutionTargetMode.SpecificAgent,
            AgentId = compatible.AgentId,
            DisplayName = "Agent — Agent 1"
        };

        Assert.Contains("Ready to publish", ScheduleExecutionTargetCatalog.DescribeReadiness(
            option,
            new AgentRegistry { Agents = [compatible] }));

        compatible.Extensions.Clear();
        Assert.Contains("legacy scheduling fallback", ScheduleExecutionTargetCatalog.DescribeReadiness(
            option,
            new AgentRegistry { Agents = [compatible] }));
    }

    private static AgentDefinition CreateAgent(
        string id,
        string displayName,
        AgentDefinitionState state,
        string? pool = null)
    {
        var agent = new AgentDefinition
        {
            AgentId = id,
            DisplayName = displayName,
            State = state
        };
        if (pool is not null)
        {
            agent.Labels["pool"] = pool;
        }

        return agent;
    }
}
