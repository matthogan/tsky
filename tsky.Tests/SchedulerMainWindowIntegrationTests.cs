using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace tsky.Tests;

[CollectionDefinition("SchedulerMainWindowHarness", DisableParallelization = true)]
public sealed class SchedulerMainWindowHarnessCollection : ICollectionFixture<SchedulerMainWindowHarnessFixture>
{
}

[Collection("SchedulerMainWindowHarness")]
public sealed class SchedulerMainWindowIntegrationTests
{
    private readonly SchedulerMainWindowHarnessFixture _fixture;

    public SchedulerMainWindowIntegrationTests(SchedulerMainWindowHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PollSchedulerDueItemsAsync_GroupChainExecutesInSnapshotOrder()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        var markerFile = Path.Combine(Path.GetTempPath(), $"tsky-scheduler-seq-{Guid.NewGuid():N}.txt");

        try
        {
            await _fixture.InvokeAsync(async () =>
            {
                var window = new MainWindow();
                try
                {
                    InvokePrivateVoid(window, "StopSchedulerPoller");

                    var groupName = "SchedulerHarness";
                    var chainTasks = new List<LauncherTask>
                    {
                        CreateCmdTask("step-one", groupName, $"echo 1 >> {markerFile}"),
                        CreateCmdTask("step-two", groupName, $"echo 2 >> {markerFile}")
                    };

                    var scheduledItem = SchedulerSnapshotFactory.CreateGroupChainItem(
                        groupName,
                        chainTasks,
                        DateTime.UtcNow.AddSeconds(-8));
                    scheduledItem.TriggerWindowSeconds = 60;
                    scheduledItem.IsRecurring = false;
                    scheduledItem.IsEnabled = true;

                    SetPrivateField(window, "_schedulerConfiguration", new SchedulerConfiguration
                    {
                        PollIntervalSeconds = SchedulerTimingService.MinPollIntervalSeconds,
                        Items = [scheduledItem]
                    });

                    await InvokePrivateTaskAsync(window, "PollSchedulerDueItemsAsync", CancellationToken.None);

                    var schedulerConfig = GetPrivateField<SchedulerConfiguration>(window, "_schedulerConfiguration");
                    var runHistory = GetPrivateField<List<SchedulerRunHistoryEntry>>(window, "_schedulerRunHistory");

                    var updatedItem = Assert.Single(schedulerConfig.Items);
                    Assert.False(updatedItem.IsEnabled);
                    Assert.False(updatedItem.IsRunning);
                    Assert.Equal(TaskRunOutcome.Succeeded.ToString(), updatedItem.LastRunOutcome);

                    var runEntry = Assert.Single(runHistory);
                    Assert.Equal(2, runEntry.SnapshotTaskCount);
                    Assert.Equal(TaskRunOutcome.Succeeded, runEntry.Outcome);
                }
                finally
                {
                    window.Close();
                }
            });

            Assert.True(File.Exists(markerFile), "Expected scheduler chain marker file to be created.");
            var lines = File.ReadAllLines(markerFile)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            Assert.Equal(new[] { "1", "2" }, lines);
        }
        finally
        {
            if (File.Exists(markerFile))
            {
                File.Delete(markerFile);
            }
        }
    }

    [Fact]
    public async Task PollSchedulerDueItemsAsync_RecurringItem_DeduplicatesSameOccurrenceAcrossPolls()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        var markerFile = Path.Combine(Path.GetTempPath(), $"tsky-scheduler-dedup-{Guid.NewGuid():N}.txt");

        try
        {
            await _fixture.InvokeAsync(async () =>
            {
                var window = new MainWindow();
                try
                {
                    InvokePrivateVoid(window, "StopSchedulerPoller");

                    var snapshotTask = CreateCmdTask("dedup-step", "SchedulerHarness", $"echo once>>{markerFile}");
                    var scheduledItem = SchedulerSnapshotFactory.CreateTaskItem(
                        snapshotTask,
                        DateTime.UtcNow.AddSeconds(-12));
                    scheduledItem.IsRecurring = true;
                    scheduledItem.RecurrenceExpression = SchedulerTimingService.BuildEveryMinutesExpression(60);
                    scheduledItem.TriggerWindowSeconds = 60;
                    scheduledItem.IsEnabled = true;

                    SetPrivateField(window, "_schedulerConfiguration", new SchedulerConfiguration
                    {
                        PollIntervalSeconds = SchedulerTimingService.MinPollIntervalSeconds,
                        Items = [scheduledItem]
                    });

                    var occurrenceLedger = GetPrivateField<Dictionary<string, DateTime>>(window, "_schedulerOccurrenceLedger");
                    occurrenceLedger.Clear();

                    await InvokePrivateTaskAsync(window, "PollSchedulerDueItemsAsync", CancellationToken.None);

                    var afterFirstPollConfig = GetPrivateField<SchedulerConfiguration>(window, "_schedulerConfiguration");
                    var firstItem = Assert.Single(afterFirstPollConfig.Items);
                    var firstToken = firstItem.LastOccurrenceToken;

                    await InvokePrivateTaskAsync(window, "PollSchedulerDueItemsAsync", CancellationToken.None);

                    var runHistory = GetPrivateField<List<SchedulerRunHistoryEntry>>(window, "_schedulerRunHistory");
                    Assert.Single(runHistory);

                    var afterSecondPollConfig = GetPrivateField<SchedulerConfiguration>(window, "_schedulerConfiguration");
                    var secondItem = Assert.Single(afterSecondPollConfig.Items);
                    Assert.True(secondItem.IsEnabled);
                    Assert.False(string.IsNullOrWhiteSpace(secondItem.LastOccurrenceToken));
                    Assert.Equal(firstToken, secondItem.LastOccurrenceToken);
                }
                finally
                {
                    window.Close();
                }
            });

            Assert.True(File.Exists(markerFile), "Expected scheduler dedup marker file to be created.");
            var lines = File.ReadAllLines(markerFile)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            Assert.Single(lines);
            Assert.Equal("once", lines[0]);
        }
        finally
        {
            if (File.Exists(markerFile))
            {
                File.Delete(markerFile);
            }
        }
    }

    private static LauncherTask CreateCmdTask(string name, string group, string commandLine)
    {
        return new LauncherTask
        {
            Name = name,
            Group = group,
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = commandLine,
            ExecutionTargetMode = "local",
            IsEnabled = true
        };
    }

    private static Task InvokePrivateTaskAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        var result = method.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Method '{methodName}' returned null.");

        if (result is not Task task)
        {
            throw new InvalidOperationException($"Method '{methodName}' does not return Task.");
        }

        return task;
    }

    private static void InvokePrivateVoid(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        _ = method.Invoke(instance, args);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");

        var value = field.GetValue(instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' is null.");

        if (value is not T typed)
        {
            throw new InvalidOperationException($"Field '{fieldName}' is not of type {typeof(T).Name}.");
        }

        return typed;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");

        field.SetValue(instance, value);
    }
}

public sealed class SchedulerMainWindowHarnessFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? _dispatcher;

    public SchedulerMainWindowHarnessFixture()
    {
        _thread = new Thread(ThreadMain)
        {
            Name = "SchedulerMainWindowHarnessThread",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    public Task InvokeAsync(Func<Task> action)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException("Dispatcher is not initialized.");
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap();
    }

    public void Dispose()
    {
        if (_dispatcher is not null)
        {
            _dispatcher.InvokeAsync(() => Application.Current?.Shutdown());
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    private void ThreadMain()
    {
        try
        {
            _ = Application.Current ?? new App();
            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.SetResult();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }
}

internal sealed class SchedulerPersistenceScope : IDisposable
{
    private readonly FileSnapshot _schedulerConfigSnapshot;
    private readonly FileSnapshot _schedulerRunHistorySnapshot;
    private readonly FileSnapshot _taskRunHistorySnapshot;
    private readonly FileSnapshot _userProfileTasksSnapshot;
    private readonly FileSnapshot _appDataTemplatesSnapshot;

    public SchedulerPersistenceScope()
    {
        var localAppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tsky");

        _schedulerConfigSnapshot = FileSnapshot.Capture(Path.Combine(localAppDataDirectory, "scheduler.json"));
        _schedulerRunHistorySnapshot = FileSnapshot.Capture(Path.Combine(localAppDataDirectory, "scheduler-run-history.json"));
        _taskRunHistorySnapshot = FileSnapshot.Capture(Path.Combine(localAppDataDirectory, "run-history.json"));
        _userProfileTasksSnapshot = FileSnapshot.Capture(TaskConfiguration.GetUserProfileConfigPath());
        _appDataTemplatesSnapshot = FileSnapshot.Capture(Path.Combine(localAppDataDirectory, "templates.json"));

        Directory.CreateDirectory(localAppDataDirectory);

        SchedulerConfigurationStore.Save(new SchedulerConfiguration
        {
            PollIntervalSeconds = SchedulerTimingService.MinPollIntervalSeconds,
            Items = []
        });
        SchedulerRunHistoryStore.Save([]);
        TaskRunHistoryStore.Save([]);

        if (!File.Exists(TaskConfiguration.GetUserProfileConfigPath()))
        {
            TaskConfiguration.Save(TaskConfiguration.GetUserProfileConfigPath(), new TaskConfiguration());
        }
    }

    public void Dispose()
    {
        _taskRunHistorySnapshot.Restore();
        _schedulerRunHistorySnapshot.Restore();
        _schedulerConfigSnapshot.Restore();
        _userProfileTasksSnapshot.Restore();
        _appDataTemplatesSnapshot.Restore();
    }

    private sealed class FileSnapshot
    {
        private readonly string _path;
        private readonly bool _existed;
        private readonly byte[]? _content;

        private FileSnapshot(string path, bool existed, byte[]? content)
        {
            _path = path;
            _existed = existed;
            _content = content;
        }

        public static FileSnapshot Capture(string path)
        {
            if (!File.Exists(path))
            {
                return new FileSnapshot(path, existed: false, content: null);
            }

            return new FileSnapshot(path, existed: true, content: File.ReadAllBytes(path));
        }

        public void Restore()
        {
            if (_existed)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(_path, _content ?? []);
                return;
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
