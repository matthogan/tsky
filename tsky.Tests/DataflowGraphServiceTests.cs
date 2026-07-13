namespace tsky.Tests;

public sealed class DataflowGraphServiceTests
{
    [Fact]
    public void BuildTaskGraphNodes_ReturnsOneNodePerVisibleTask()
    {
        var service = new DataflowGraphService();
        var tasks = new List<LauncherTask>
        {
            new() { Name = "A", Group = "G", IsEnabled = true, Description = "first" },
            new() { Name = "B", Group = "G", IsEnabled = false, Description = "second" },
            new() { Name = "C", Group = "G", IsEnabled = true }
        };

        var nodes = service.BuildTaskGraphNodes(tasks, [0, 2]);

        Assert.Collection(
            nodes,
            first =>
            {
                Assert.Equal(0, first.TaskIndex);
                Assert.Equal("A", first.TaskName);
                Assert.Equal("first", first.Description);
                Assert.True(first.IsEnabled);
            },
            second =>
            {
                Assert.Equal(2, second.TaskIndex);
                Assert.Equal("C", second.TaskName);
                Assert.Null(second.Description);
                Assert.True(second.IsEnabled);
            });
    }

    [Fact]
    public void BuildTaskGraphEdges_MapsResolvedAndUnresolvedEdgesFromBindings()
    {
        var service = new DataflowGraphService();
        var tasks = new List<LauncherTask>
        {
            new()
            {
                Name = "A",
                Group = "G",
                Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["foo"] = "value"
                }
            },
            new()
            {
                Name = "B",
                Group = "G",
                InputBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["input"] = "A.foo"
                }
            },
            new()
            {
                Name = "C",
                Group = "G",
                InputBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["input"] = "A.missing"
                }
            }
        };

        var edges = service.BuildTaskGraphEdges(tasks, [0, 1, 2]);

        Assert.Collection(
            edges,
            first =>
            {
                Assert.Equal(0, first.SourceTaskIndex);
                Assert.Equal(1, first.TargetTaskIndex);
                Assert.Equal("foo", first.Label);
                Assert.True(first.IsResolved);
            },
            second =>
            {
                Assert.Equal(0, second.SourceTaskIndex);
                Assert.Equal(2, second.TargetTaskIndex);
                Assert.Equal("missing", second.Label);
                Assert.False(second.IsResolved);
            });
    }
}
