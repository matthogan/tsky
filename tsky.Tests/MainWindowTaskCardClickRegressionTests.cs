using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace tsky.Tests;

[Collection("SchedulerMainWindowHarness")]
public sealed class MainWindowTaskCardClickRegressionTests
{
    private readonly SchedulerMainWindowHarnessFixture _fixture;

    public MainWindowTaskCardClickRegressionTests(SchedulerMainWindowHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TaskButtonClick_RunningTask_SelectsCardWithoutExecuting()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        var markerFile = Path.Combine(Path.GetTempPath(), $"tsky-running-card-click-{Guid.NewGuid():N}.txt");

        try
        {
            await _fixture.InvokeAsync(async () =>
            {
                var window = new MainWindow();
                try
                {
                    InvokePrivateVoid(window, "StopSchedulerPoller");

                    var task = new LauncherTask
                    {
                        Name = "running-card-click",
                        Group = "Regression",
                        Runner = "cmd",
                        WaitForExit = true,
                        CommandLine = $"echo clicked>>\"{markerFile}\"",
                        ExecutionTargetMode = "local",
                        IsEnabled = true
                    };

                    SetPrivateField(window, "_currentConfiguration", new TaskConfiguration
                    {
                        Tasks = [task]
                    });

                    InvokePrivateVoid(window, "SelectGroup", "Regression");
                    SetPrivateField(window, "_selectedTaskIndex", null);
                    SetTaskRunState(window, task, "Running");

                    var button = GetRenderedTaskMainButton(window, taskIndex: 0);

                    InvokePrivateVoid(window, "TaskButton_Click", button, new RoutedEventArgs(Button.ClickEvent, button));

                    // TaskButton_Click is async void; wait briefly for any accidental execution side-effects.
                    await Task.Delay(350);

                    Assert.Equal(0, GetPrivateField<int?>(window, "_selectedTaskIndex"));
                    Assert.False(File.Exists(markerFile), "Running task card click should not execute task command.");

                    SetTaskRunState(window, task, "NeverRun");
                }
                finally
                {
                    window.Close();
                }
            });
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
    public async Task PasteTaskIntoSelectedGroup_SelectsPastedTask()
    {
        using var persistenceScope = new SchedulerPersistenceScope();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");

                const string groupName = "PasteRegression";
                var existingTask = new LauncherTask
                {
                    Name = "existing-task",
                    Group = groupName,
                    Runner = "cmd",
                    WaitForExit = true,
                    CommandLine = "echo existing",
                    ExecutionTargetMode = "local",
                    IsEnabled = true
                };

                SetPrivateField(window, "_currentConfiguration", new TaskConfiguration
                {
                    Tasks = [existingTask]
                });

                InvokePrivateVoid(window, "SelectGroup", groupName);

                var copiedTask = new LauncherTask
                {
                    Name = "pasted-task",
                    Group = "OtherGroup",
                    Runner = "cmd",
                    WaitForExit = true,
                    CommandLine = "echo pasted",
                    ExecutionTargetMode = "local",
                    IsEnabled = true
                };

                SetPrivateField(window, "_copiedTaskJson", TaskConfiguration.SerializeTask(copiedTask));

                var pasteSucceeded = InvokePrivateResult<bool>(window, "PasteTaskIntoSelectedGroup");
                Assert.True(pasteSucceeded);

                var selectedIndex = GetPrivateField<int?>(window, "_selectedTaskIndex");
                Assert.True(selectedIndex.HasValue);

                var currentConfiguration = GetPrivateField<TaskConfiguration>(window, "_currentConfiguration");
                var selectedTask = currentConfiguration.Tasks[selectedIndex.Value];
                Assert.Equal("pasted-task", selectedTask.Name);
                Assert.Equal(groupName, selectedTask.Group);

                var visibleTaskIndexes = GetPrivateField<List<int>>(window, "_visibleTaskIndexes");
                Assert.Contains(selectedIndex.Value, visibleTaskIndexes);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static Button GetRenderedTaskMainButton(MainWindow window, int taskIndex)
    {
        var cardsByIndex = GetPrivateField<Dictionary<int, Grid>>(window, "_taskCardsByIndex");
        Assert.True(cardsByIndex.TryGetValue(taskIndex, out var card));

        var taskButton = card.Children
            .OfType<Button>()
            .FirstOrDefault(candidate => candidate.Tag is LauncherTask);

        return taskButton ?? throw new InvalidOperationException("Task card main button was not rendered.");
    }

    private static void SetTaskRunState(MainWindow window, LauncherTask task, string stateName)
    {
        var method = window.GetType().GetMethod("SetTaskRunState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Method 'SetTaskRunState' was not found.");

        var stateType = method.GetParameters()[1].ParameterType;
        var state = Enum.Parse(stateType, stateName, ignoreCase: false);
        _ = method.Invoke(window, [task, state]);
    }

    private static void InvokePrivateVoid(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        _ = method.Invoke(instance, args);
    }

    private static T InvokePrivateResult<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        var value = method.Invoke(instance, args);
        if (value is not T typed)
        {
            throw new InvalidOperationException($"Method '{methodName}' did not return {typeof(T).Name}.");
        }

        return typed;
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");

        field.SetValue(instance, value);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");

        var value = field.GetValue(instance);
        if (value is not T typed)
        {
            throw new InvalidOperationException($"Field '{fieldName}' is not of type {typeof(T).Name}.");
        }

        return typed;
    }
}