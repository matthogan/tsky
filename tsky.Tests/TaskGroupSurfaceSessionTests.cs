namespace tsky.Tests;

public sealed class TaskGroupSurfaceSessionTests
{
    [Fact]
    public void Update_PublishesStateChanged_WhenStateActuallyChanges()
    {
        var session = new TaskGroupSurfaceSession();
        TaskGroupSurfaceSessionChangedEventArgs? args = null;

        session.StateChanged += (_, changedArgs) => args = changedArgs;

        var changed = session.Update(" Group A ", isGraphMode: true, selectedTaskIndex: 7, origin: "Main");

        Assert.True(changed);
        Assert.NotNull(args);
        Assert.Equal("Main", args!.Origin);
        Assert.Equal(1L, args.VersionStamp);
        Assert.Equal("Group A", session.GroupName);
        Assert.True(session.IsGraphMode);
        Assert.Equal(7, session.SelectedTaskIndex);
    }

    [Fact]
    public void Update_DoesNotPublish_WhenStateIsUnchanged()
    {
        var session = new TaskGroupSurfaceSession();
        var eventCount = 0;

        session.StateChanged += (_, _) => eventCount++;

        session.Update("Group A", isGraphMode: false, selectedTaskIndex: 2, origin: "Main");
        var changed = session.Update("Group A", isGraphMode: false, selectedTaskIndex: 2, origin: "Main");

        Assert.False(changed);
        Assert.Equal(1, eventCount);
        Assert.Equal(1L, session.VersionStamp);
    }

    [Fact]
    public void Update_NormalizesInvalidInputs()
    {
        var session = new TaskGroupSurfaceSession();

        session.Update("   ", isGraphMode: true, selectedTaskIndex: -1, origin: "  ");

        Assert.Null(session.GroupName);
        Assert.Null(session.SelectedTaskIndex);
        Assert.True(session.IsGraphMode);
        Assert.Equal(1L, session.VersionStamp);
    }
}
