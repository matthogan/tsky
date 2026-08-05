using System.Reflection;
using System.Windows;

namespace tsky.Tests;

[Collection("SchedulerMainWindowHarness")]
public sealed class TemplateViewCommandGatingRegressionTests
{
    private readonly SchedulerMainWindowHarnessFixture _fixture;

    public TemplateViewCommandGatingRegressionTests(SchedulerMainWindowHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SelectTemplatesView_RibbonCommandGating_MatchesSpec()
    {
        using var persistenceScope = new SchedulerPersistenceScope();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                SetPrivateField(window, "_templateConfiguration", new TaskConfiguration
                {
                    Tasks = [CreateTemplateTask("template-gating")]
                });

                InvokePrivateVoid(window, "SelectTemplatesView");

                Assert.True(window.RibbonAddTaskButton.IsEnabled);
                Assert.True(window.RibbonEditTaskButton.IsEnabled);
                Assert.True(window.RibbonDeleteTaskButton.IsEnabled);
                Assert.Equal("Add", window.RibbonAddActionLabel.Text);
                Assert.Equal("Edit", window.RibbonEditActionLabel.Text);
                Assert.False(window.RibbonRunChainButton.IsEnabled);
                Assert.False(window.RibbonToggleTaskEnabledButton.IsEnabled);
                Assert.False(window.RibbonGroupsDropDownButton.IsEnabled);
                Assert.False(window.RibbonTaskSurfaceToggleButton.IsEnabled);
                Assert.True(window.RibbonShareButton.IsEnabled);
                Assert.True(window.RibbonImportButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectTemplatesView_BottomTabGating_OnlyOutputAndResultEvaluatorRemainVisible()
    {
        using var persistenceScope = new SchedulerPersistenceScope();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                SetPrivateField(window, "_templateConfiguration", new TaskConfiguration
                {
                    Tasks = [CreateTemplateTask("template-tabs")]
                });

                window.BottomPaneTabControl.SelectedItem = window.HistoryPaneTabItem;
                InvokePrivateVoid(window, "SelectTemplatesView");

                Assert.Equal(Visibility.Visible, window.OutputPaneTabItem.Visibility);
                Assert.Equal(Visibility.Visible, window.ResultEvaluatorPaneTabItem.Visibility);

                Assert.Equal(Visibility.Collapsed, window.HistoryPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.InputPortsPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.OutputPortsPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.VariablesPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AgentsDispatchPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AgentRuntimePaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.GatewaySettingsPaneTabItem.Visibility);

                Assert.True(ReferenceEquals(window.BottomPaneTabControl.SelectedItem, window.OutputPaneTabItem)
                    || ReferenceEquals(window.BottomPaneTabControl.SelectedItem, window.ResultEvaluatorPaneTabItem));
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectAgentsView_CommandLabels_AreGenericAddEdit()
    {
        using var persistenceScope = new SchedulerPersistenceScope();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                InvokePrivateVoid(window, "SelectAgentsView");

                Assert.Equal("Add", window.RibbonAddActionLabel.Text);
                Assert.Equal("Edit", window.RibbonEditActionLabel.Text);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectTemplatesView_SelectNextTemplateTask_CyclesSelection()
    {
        using var persistenceScope = new SchedulerPersistenceScope();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                SetPrivateField(window, "_templateConfiguration", new TaskConfiguration
                {
                    Tasks =
                    [
                        CreateTemplateTask("template-a"),
                        CreateTemplateTask("template-b"),
                        CreateTemplateTask("template-c")
                    ]
                });

                InvokePrivateVoid(window, "SelectTemplatesView");
                SetPrivateField(window, "_selectedTemplateTaskIndex", 0);

                InvokePrivateVoid(window, "SelectNextTemplateTask", 1);
                Assert.Equal(1, GetPrivateField<int?>(window, "_selectedTemplateTaskIndex"));

                InvokePrivateVoid(window, "SelectNextTemplateTask", -1);
                Assert.Equal(0, GetPrivateField<int?>(window, "_selectedTemplateTaskIndex"));
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static LauncherTask CreateTemplateTask(string name)
    {
        return new LauncherTask
        {
            Name = name,
            Group = "Templates",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo template",
            ExecutionTargetMode = "local",
            IsEnabled = true
        };
    }

    private static void InvokePrivateVoid(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        _ = method.Invoke(instance, args);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
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
