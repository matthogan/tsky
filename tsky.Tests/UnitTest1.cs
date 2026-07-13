namespace tsky.Tests;

public sealed class RunHistoryServiceTests
{
    [Fact]
    public void StartEntry_KeepsOnlyNewestConfiguredEntries()
    {
        var entries = new List<TaskRunHistoryEntry>
        {
            CreateEntry("oldest", DateTime.UtcNow.AddMinutes(-3)),
            CreateEntry("older", DateTime.UtcNow.AddMinutes(-2))
        };

        List<TaskRunHistoryEntry>? savedEntries = null;
        var service = new RunHistoryService(entries, maxEntries: 2, saveAction: list =>
        {
            savedEntries = list.ToList();
        });

        service.StartEntry(CreateTask("newest"), isReplay: false, replayFromRunId: null);

        Assert.NotNull(savedEntries);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, savedEntries!.Count);
        Assert.DoesNotContain(entries, entry => entry.Snapshot.Name == "oldest");
        Assert.Contains(entries, entry => entry.Snapshot.Name == "older");
        Assert.Contains(entries, entry => entry.Snapshot.Name == "newest");
    }

    [Fact]
    public void CompleteEntry_WhenListAlreadyExceedsLimit_TrimsBeforeSaving()
    {
        var entries = new List<TaskRunHistoryEntry>
        {
            CreateEntry("oldest", DateTime.UtcNow.AddMinutes(-4)),
            CreateEntry("older", DateTime.UtcNow.AddMinutes(-3)),
            CreateEntry("newer", DateTime.UtcNow.AddMinutes(-2)),
            CreateEntry("newest", DateTime.UtcNow.AddMinutes(-1))
        };

        List<TaskRunHistoryEntry>? savedEntries = null;
        var service = new RunHistoryService(entries, maxEntries: 2, saveAction: list =>
        {
            savedEntries = list.ToList();
        });

        service.CompleteEntry(entries[0], TaskRunOutcome.Succeeded, exitCode: 0);

        Assert.NotNull(savedEntries);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, savedEntries!.Count);
        Assert.Contains(entries, entry => entry.Snapshot.Name == "newer");
        Assert.Contains(entries, entry => entry.Snapshot.Name == "newest");
        Assert.DoesNotContain(entries, entry => entry.Snapshot.Name == "oldest");
        Assert.DoesNotContain(entries, entry => entry.Snapshot.Name == "older");
    }

    private static LauncherTask CreateTask(string name)
    {
        return new LauncherTask
        {
            Name = name,
            Group = "Tests",
            Runner = "cmd",
            CommandLine = "echo test"
        };
    }

    private static TaskRunHistoryEntry CreateEntry(string taskName, DateTime startedAtUtc)
    {
        return new TaskRunHistoryEntry
        {
            StartedAtUtc = startedAtUtc,
            Snapshot = new TaskRunSnapshot
            {
                Name = taskName,
                Group = "Tests",
                Runner = "cmd",
                CommandLine = "echo test"
            }
        };
    }
}
