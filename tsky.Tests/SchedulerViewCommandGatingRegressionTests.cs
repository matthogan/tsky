using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace tsky.Tests;

[Collection("SchedulerMainWindowHarness")]
public sealed class SchedulerViewCommandGatingRegressionTests
{
    private readonly SchedulerMainWindowHarnessFixture _fixture;

    public SchedulerViewCommandGatingRegressionTests(SchedulerMainWindowHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SelectSchedulerView_RibbonCommandGating_MatchesSpec()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                InvokePrivateVoid(window, "SelectSchedulerView");

                Assert.False(window.RibbonAddTaskButton.IsEnabled);
                Assert.False(window.RibbonEditTaskButton.IsEnabled);
                Assert.True(window.RibbonToggleTaskEnabledButton.IsEnabled);
                Assert.Equal("Disable", window.RibbonToggleTaskEnabledLabel.Text);
                Assert.False(window.RibbonRunChainButton.IsEnabled);
                Assert.False(window.RibbonGroupsDropDownButton.IsEnabled);
                Assert.False(window.RibbonShareButton.IsEnabled);
                Assert.False(window.RibbonImportButton.IsEnabled);
                Assert.False(window.RibbonTaskSurfaceToggleButton.IsEnabled);

                Assert.True(window.RibbonDeleteTaskButton.IsEnabled);
                Assert.True(window.RibbonHistoryButton.IsEnabled);
                Assert.True(window.RibbonGatewayListenerToggleButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectSchedulerView_BottomTabGating_OnlyOutputAndHistoryRemainVisible()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");

                window.BottomPaneTabControl.SelectedItem = window.InputPortsPaneTabItem;
                InvokePrivateVoid(window, "SelectSchedulerView");

                Assert.Equal(Visibility.Visible, window.OutputPaneTabItem.Visibility);
                Assert.Equal(Visibility.Visible, window.HistoryPaneTabItem.Visibility);

                Assert.Equal(Visibility.Collapsed, window.InputPortsPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.OutputPortsPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.VariablesPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.ResultEvaluatorPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AgentsDispatchPaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.AgentRuntimePaneTabItem.Visibility);
                Assert.Equal(Visibility.Collapsed, window.GatewaySettingsPaneTabItem.Visibility);

                Assert.True(ReferenceEquals(window.BottomPaneTabControl.SelectedItem, window.OutputPaneTabItem)
                    || ReferenceEquals(window.BottomPaneTabControl.SelectedItem, window.HistoryPaneTabItem));
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SchedulerRibbonToggle_PersistsScheduledItemEnabledState_ToSchedulerConfiguration()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        var scheduledItemId = SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                InvokePrivateVoid(window, "SelectSchedulerView");

                Assert.True(window.RibbonToggleTaskEnabledButton.IsEnabled);
                Assert.Equal("Disable", window.RibbonToggleTaskEnabledLabel.Text);

                window.RibbonToggleTaskEnabledButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var updatedConfiguration = SchedulerConfigurationStore.Load();
                var updatedItem = updatedConfiguration.Items.Single(item =>
                    string.Equals(item.Id, scheduledItemId, StringComparison.OrdinalIgnoreCase));

                Assert.False(updatedItem.IsEnabled);
                Assert.Null(updatedItem.NextRunAtUtc);
                Assert.Equal("Enable", window.RibbonToggleTaskEnabledLabel.Text);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectSchedulerView_DoesNotResetSidebarWidthToPersistedValue()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(async () =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");

                const double currentSidebarWidth = 360;
                const double stalePersistedWidth = 220;

                window.SidebarColumnDefinition.Width = new GridLength(currentSidebarWidth);

                var uiStateField = window.GetType().GetField("_uiState", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Field '_uiState' was not found.");
                var uiState = uiStateField.GetValue(window)
                    ?? throw new InvalidOperationException("Field '_uiState' is null.");
                var sidebarWidthProperty = uiState.GetType().GetProperty("SidebarWidth", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new InvalidOperationException("Property 'SidebarWidth' was not found.");
                sidebarWidthProperty.SetValue(uiState, stalePersistedWidth);

                InvokePrivateVoid(window, "SelectSchedulerView");
                await Task.Delay(220);

                var resultingWidth = window.SidebarColumnDefinition.Width.Value;
                Assert.InRange(resultingWidth, currentSidebarWidth - 0.5, currentSidebarWidth + 0.5);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string SeedSchedulerWithSingleEnabledItem()
    {
        var seedTask = new LauncherTask
        {
            Name = "scheduler-toggle-seed",
            Group = "SchedulerHarness",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo seed",
            ExecutionTargetMode = "local",
            IsEnabled = true
        };

        var scheduledItem = SchedulerSnapshotFactory.CreateTaskItem(seedTask, DateTime.UtcNow.AddMinutes(15));
        scheduledItem.IsEnabled = true;
        scheduledItem.NextRunAtUtc = scheduledItem.TriggerAtUtc;

        SchedulerConfigurationStore.Save(new SchedulerConfiguration
        {
            PollIntervalSeconds = SchedulerTimingService.MinPollIntervalSeconds,
            Items = [scheduledItem]
        });

        return scheduledItem.Id;
    }

    private static void InvokePrivateVoid(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

        _ = method.Invoke(instance, args);
    }
}
