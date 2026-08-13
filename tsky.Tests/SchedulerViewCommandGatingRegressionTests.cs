using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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
    public async Task ScheduledItemEditor_ExposesEveryExecutionTargetKindAndPublicationReadiness()
    {
        await _fixture.InvokeAsync(() =>
        {
            var compatible = new AgentDefinition
            {
                AgentId = "agt-editor",
                DisplayName = "Editor Agent",
                State = AgentDefinitionState.Online,
                ProtocolVersion = "1.1",
                Extensions = ["schedule.v1"]
            };
            compatible.Labels["pool"] = "editors";
            var item = new ScheduledItem { ExecutionTargetMode = "agent", TargetAgentId = compatible.AgentId };
            var window = new SchedulerItemEditorWindow(
                item,
                isNew: true,
                new AgentRegistry { Agents = [compatible] });
            try
            {
                var options = window.ExecutionTargetComboBox.Items.Cast<ScheduleExecutionTargetOption>().ToList();
                Assert.Contains(options, option => option.Mode == ExecutionTargetMode.Local);
                Assert.Contains(options, option => option.Mode == ExecutionTargetMode.Automatic);
                Assert.Contains(options, option => option.Mode == ExecutionTargetMode.SpecificAgent && option.AgentId == compatible.AgentId);
                Assert.Contains(options, option => option.Mode == ExecutionTargetMode.AgentPool && option.PoolId == "editors");
                Assert.Equal("Agent — Editor Agent (online)", window.ExecutionTargetComboBox.SelectionBoxItem?.ToString());
                Assert.Contains("Ready to publish", window.ExecutionTargetStatusTextBlock.Text);
                Assert.Equal(Visibility.Visible, window.PublicationStatusBorder.Visibility);
            }
            finally
            {
                window.Close();
            }

            var defaultWindow = new SchedulerItemEditorWindow(new ScheduledItem(), isNew: true, new AgentRegistry());
            try
            {
                var selected = Assert.IsType<ScheduleExecutionTargetOption>(defaultWindow.ExecutionTargetComboBox.SelectedItem);
                Assert.Equal(ExecutionTargetMode.Local, selected.Mode);
                Assert.Equal("Local — this desktop", defaultWindow.ExecutionTargetComboBox.SelectionBoxItem?.ToString());
            }
            finally
            {
                defaultWindow.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ScheduledItemEditor_RejectsAnUnavailableConfiguredAgent()
    {
        await _fixture.InvokeAsync(() =>
        {
            var unavailable = new AgentDefinition
            {
                AgentId = "agt-revoked",
                DisplayName = "Revoked Agent",
                State = AgentDefinitionState.Revoked
            };
            var item = new ScheduledItem { ExecutionTargetMode = "agent", TargetAgentId = unavailable.AgentId };
            var window = new SchedulerItemEditorWindow(
                item,
                isNew: false,
                new AgentRegistry { Agents = [unavailable] });
            try
            {
                var submit = window.GetType().GetMethod("TrySubmit", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Method 'TrySubmit' was not found.");
                var accepted = (bool)(submit.Invoke(window, null) ?? true);

                Assert.False(accepted);
                Assert.Equal(Visibility.Visible, window.ValidationTextBlock.Visibility);
                Assert.Contains("cannot be selected", window.ValidationTextBlock.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
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
                Assert.Equal(SchedulerConfiguration.CurrentSchemaVersion, updatedConfiguration.SchemaVersion);
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

    [Fact]
    public async Task SelectSchedulerView_ViewOutputToggle_IsScopedToSchedulerKey()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");

                SetPrivateField(window, "_selectedGroupName", "LeakGroup");
                var outputVisibility = GetPrivateField<Dictionary<string, bool>>(window, "_groupOutputVisibility");
                outputVisibility["LeakGroup"] = true;
                outputVisibility["__SCHEDULER__"] = true;

                InvokePrivateVoid(window, "SelectSchedulerView");
                window.ViewOutputMenuItem.IsChecked = false;
                InvokePrivateVoid(window, "ViewOutputMenuItem_Click", window.ViewOutputMenuItem, new RoutedEventArgs());

                Assert.True(outputVisibility["LeakGroup"]);
                Assert.False(outputVisibility["__SCHEDULER__"]);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SelectSchedulerView_KeyE_IsHandledBySchedulerEditPath()
    {
        using var persistenceScope = new SchedulerPersistenceScope();
        var scheduledItemId = SeedSchedulerWithSingleEnabledItem();

        await _fixture.InvokeAsync(() =>
        {
            var window = new MainWindow();
            DispatcherTimer? closeDialogTimer = null;
            try
            {
                InvokePrivateVoid(window, "StopSchedulerPoller");
                InvokePrivateVoid(window, "SelectSchedulerView");
                SetPrivateField(window, "_selectedScheduledItemId", scheduledItemId);
                window.Show();
                window.UpdateLayout();

                // Ensure modal editor dialogs opened by the edit path are closed during the test.
                closeDialogTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(25)
                };
                closeDialogTimer.Tick += (_, _) =>
                {
                    foreach (Window candidate in Application.Current?.Windows ?? [])
                    {
                        if (candidate.GetType().Name == "SchedulerItemEditorWindow")
                        {
                            candidate.Close();
                            break;
                        }
                    }
                };
                closeDialogTimer.Start();

                var keyEvent = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.E)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };

                InvokePrivateVoid(window, "MainWindow_PreviewKeyDown", window, keyEvent);

                Assert.True(keyEvent.Handled);
            }
            finally
            {
                closeDialogTimer?.Stop();
                window.Close();
            }

            return Task.CompletedTask;
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

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");

        field.SetValue(instance, value);
    }
}
