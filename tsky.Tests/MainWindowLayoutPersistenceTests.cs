namespace tsky.Tests;

public sealed class MainWindowLayoutPersistenceTests
{
    [Fact]
    public void ResolveOutputHeightCacheKey_GroupView_UsesGroupKey()
    {
        const string groupKey = "Build";

        var key = MainWindow.ResolveOutputHeightCacheKey(isAgentViewSelected: false, groupKey);

        Assert.Equal(groupKey, key);
    }

    [Fact]
    public void ResolveOutputHeightCacheKey_AgentView_UsesAgentKey()
    {
        const string groupKey = "Build";

        var key = MainWindow.ResolveOutputHeightCacheKey(isAgentViewSelected: true, groupKey);

        Assert.Equal("__AGENTS_VIEW__", key);
    }
}
