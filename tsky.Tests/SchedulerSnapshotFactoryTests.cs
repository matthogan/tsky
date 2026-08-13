namespace tsky.Tests;

public sealed class SchedulerSnapshotFactoryTests
{
    [Fact]
    public void CreateTaskItem_ClonesAsLocalSnapshot()
    {
        var source = new LauncherTask
        {
            Name = "Deploy",
            Group = "Ops",
            ExecutionTargetMode = "agent",
            TargetAgentId = "agent-01",
            TargetPoolId = "pool-a",
            CommandLine = "echo hi",
            Runner = "cmd"
        };

        var scheduled = SchedulerSnapshotFactory.CreateTaskItem(source, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(ScheduledTargetKind.TaskSnapshot, scheduled.TargetKind);
        Assert.NotNull(scheduled.TaskSnapshot);
        Assert.Equal("local", scheduled.TaskSnapshot!.ExecutionTargetMode);
        Assert.Null(scheduled.TaskSnapshot.TargetAgentId);
        Assert.Null(scheduled.TaskSnapshot.TargetPoolId);
        Assert.Equal("Deploy", scheduled.TaskSnapshot.Name);
		Assert.Equal("agent", scheduled.ExecutionTargetMode);
		Assert.Equal("agent-01", scheduled.TargetAgentId);
		Assert.True(scheduled.IsAgentOwned);
		Assert.Equal(1UL, scheduled.Revision);
    }

    [Fact]
    public void CreateGroupChainItem_ClonesAllSnapshotsAsLocal()
    {
        var tasks = new List<LauncherTask>
        {
            new()
            {
                Name = "One",
                Group = "Group-A",
                ExecutionTargetMode = "pool",
                TargetPoolId = "pool-01",
                CommandLine = "echo one",
                Runner = "cmd"
            },
            new()
            {
                Name = "Two",
                Group = "Group-A",
                ExecutionTargetMode = "agent",
                TargetAgentId = "agent-2",
                CommandLine = "echo two",
                Runner = "cmd"
            }
        };

        var scheduled = SchedulerSnapshotFactory.CreateGroupChainItem("Group-A", tasks, DateTime.UtcNow.AddMinutes(10));

        Assert.Equal(ScheduledTargetKind.GroupChainSnapshot, scheduled.TargetKind);
        Assert.Equal(2, scheduled.ChainSnapshot.Count);
        Assert.All(scheduled.ChainSnapshot, snapshot => Assert.Equal("local", snapshot.ExecutionTargetMode));
        Assert.All(scheduled.ChainSnapshot, snapshot => Assert.Null(snapshot.TargetAgentId));
        Assert.All(scheduled.ChainSnapshot, snapshot => Assert.Null(snapshot.TargetPoolId));
		Assert.Equal("local", scheduled.ExecutionTargetMode); // mixed remote policies require an explicit effective selection from the UI
    }
}
