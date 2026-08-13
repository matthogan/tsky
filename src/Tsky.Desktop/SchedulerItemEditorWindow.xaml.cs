using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace tsky;

public partial class SchedulerItemEditorWindow : Window
{
    private readonly ScheduledItem _draft;
    private readonly AgentRegistry _agentRegistry;
    private readonly IReadOnlyList<ScheduleExecutionTargetOption> _targetOptions;

    public SchedulerItemEditorWindow(ScheduledItem source, bool isNew, AgentRegistry? agentRegistry = null)
    {
        InitializeComponent();

        _draft = CloneScheduledItem(source);
        _agentRegistry = agentRegistry ?? new AgentRegistry();
        _targetOptions = ScheduleExecutionTargetCatalog.Build(_agentRegistry, _draft);
        DialogTitleTextBlock.Text = isNew ? "Create scheduled item" : "Edit scheduled item";
        TargetSummaryTextBlock.Text = BuildTargetSummary(_draft);

        ExecutionTargetComboBox.ItemsSource = _targetOptions;
        ExecutionTargetComboBox.SelectedItem = ScheduleExecutionTargetCatalog.FindSelected(_targetOptions, _draft)
            ?? _targetOptions.First(option => option.Mode == ExecutionTargetMode.Local);

        DisplayNameTextBox.Text = _draft.DisplayName;
        var localTrigger = _draft.TriggerAtUtc.ToLocalTime();
        TriggerDatePicker.SelectedDate = localTrigger.Date;
        TriggerTimeTextBox.Text = localTrigger.ToString("HH:mm", CultureInfo.InvariantCulture);
        TriggerWindowSecondsTextBox.Text = SchedulerTimingService
            .NormalizeTriggerWindowSeconds(_draft.TriggerWindowSeconds)
            .ToString(CultureInfo.InvariantCulture);

        RecurringCheckBox.IsChecked = _draft.IsRecurring;
        RecurrenceMinutesTextBox.Text = SchedulerTimingService.TryParseEveryMinutesExpression(_draft.RecurrenceExpression, out var recurrenceMinutes)
            ? recurrenceMinutes.ToString(CultureInfo.InvariantCulture)
            : "60";
        EnabledCheckBox.IsChecked = _draft.IsEnabled;

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        PreviewKeyDown += SchedulerItemEditorWindow_PreviewKeyDown;

        UpdateRecurrenceUi();
        UpdateExecutionTargetStatus();
        Loaded += (_, _) => DisplayNameTextBox.Focus();
    }

    public ScheduledItem ScheduledItem => CloneScheduledItem(_draft);

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void SchedulerItemEditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (TrySubmit())
            {
                e.Handled = true;
            }
        }
    }

    private void RecurringCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateRecurrenceUi();
    }

    private void ExecutionTargetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateExecutionTargetStatus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TrySubmit();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool TrySubmit()
    {
        ValidationTextBlock.Visibility = Visibility.Collapsed;
        ValidationTextBlock.Text = string.Empty;

        var displayName = DisplayNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return ShowValidation("Display name is required.");
        }

        var selectedTarget = ExecutionTargetComboBox.SelectedItem as ScheduleExecutionTargetOption;
        var targetError = ScheduleExecutionTargetCatalog.Validate(selectedTarget);
        if (targetError is not null)
        {
            return ShowValidation(targetError);
        }

        if (!TryResolveTriggerUtc(out var triggerUtc))
        {
            return false;
        }

        if (!int.TryParse(TriggerWindowSecondsTextBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var triggerWindowSeconds)
            || triggerWindowSeconds <= 0)
        {
            return ShowValidation("Run window must be a positive number of seconds.");
        }

        var isRecurring = RecurringCheckBox.IsChecked == true;
        string? recurrenceExpression = null;
        if (isRecurring)
        {
            if (!int.TryParse(RecurrenceMinutesTextBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var recurrenceMinutes)
                || recurrenceMinutes <= 0)
            {
                return ShowValidation("Recurrence minutes must be a positive integer.");
            }

            recurrenceExpression = SchedulerTimingService.BuildEveryMinutesExpression(recurrenceMinutes);
        }

        _draft.DisplayName = displayName;
        var targetChanged = !string.Equals(
            ScheduleExecutionTargetCatalog.FindSelected(_targetOptions, _draft)?.Key,
            selectedTarget!.Key,
            StringComparison.OrdinalIgnoreCase);
        ScheduleExecutionTargetCatalog.Apply(_draft, selectedTarget);
        if (targetChanged)
        {
            _draft.PublishedAgentId = null;
            _draft.AcceptedRevision = 0;
            _draft.RemoteDeploymentState = null;
            _draft.RemoteDeploymentMessage = null;
            _draft.LastAcknowledgedOccurrenceSequence = 0;
        }
        _draft.TriggerAtUtc = triggerUtc;
        _draft.IsRecurring = isRecurring;
        _draft.RecurrenceExpression = recurrenceExpression;
        _draft.TriggerWindowSeconds = SchedulerTimingService.NormalizeTriggerWindowSeconds(triggerWindowSeconds);
        _draft.IsEnabled = EnabledCheckBox.IsChecked == true;
        _draft.NextRunAtUtc = SchedulerTimingService.TryResolveNextRunUtc(_draft, DateTime.UtcNow);

        DialogResult = true;
        return true;
    }

    private bool TryResolveTriggerUtc(out DateTime triggerUtc)
    {
        triggerUtc = default;

        if (!TriggerDatePicker.SelectedDate.HasValue)
        {
            return ShowValidation("Select a trigger date.");
        }

        var timeText = TriggerTimeTextBox.Text?.Trim() ?? string.Empty;
        if (!DateTime.TryParseExact(
                timeText,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            return ShowValidation("Time must be in HH:mm format.");
        }

        var localDate = TriggerDatePicker.SelectedDate.Value.Date;
        var localDateTime = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            parsedTime.Hour,
            parsedTime.Minute,
            0,
            DateTimeKind.Local);

        triggerUtc = localDateTime.ToUniversalTime();
        return true;
    }

    private bool ShowValidation(string message)
    {
        ValidationTextBlock.Text = message;
        ValidationTextBlock.Visibility = Visibility.Visible;
        return false;
    }

    private void UpdateRecurrenceUi()
    {
        var isRecurring = RecurringCheckBox.IsChecked == true;
        RecurrenceMinutesTextBox.IsEnabled = isRecurring;
        RecurrenceMinutesTextBox.Opacity = isRecurring ? 1 : 0.6;
    }

    private void UpdateExecutionTargetStatus()
    {
        if (ExecutionTargetStatusTextBlock is null || PublicationStatusBorder is null)
        {
            return;
        }

        var selected = ExecutionTargetComboBox.SelectedItem as ScheduleExecutionTargetOption;
        ExecutionTargetStatusTextBlock.Text = ScheduleExecutionTargetCatalog.DescribeReadiness(selected, _agentRegistry);

        var isRemote = selected is not null && selected.Mode != ExecutionTargetMode.Local;
        PublicationStatusBorder.Visibility = isRemote ? Visibility.Visible : Visibility.Collapsed;
        if (!isRemote)
        {
            PublicationStatusTextBlock.Text = string.Empty;
            return;
        }

        var original = ScheduleExecutionTargetCatalog.FindSelected(_targetOptions, _draft);
        var targetChanged = original is null || !string.Equals(original.Key, selected!.Key, StringComparison.OrdinalIgnoreCase);
        PublicationStatusTextBlock.Text = targetChanged
            ? "Not published for this target. Saving will publish when a compatible target is connected."
            : !string.IsNullOrWhiteSpace(_draft.RemoteDeploymentState)
                ? $"{_draft.RemoteDeploymentState}: {_draft.RemoteDeploymentMessage ?? "No additional detail."}"
                : "Not published yet. The desktop fallback remains active until the agent acknowledges this revision.";
    }

    private static string BuildTargetSummary(ScheduledItem item)
    {
        var targetLabel = item.TargetKind == ScheduledTargetKind.GroupChainSnapshot
            ? "Group chain snapshot"
            : "Task snapshot";

        return $"Target: {targetLabel} | Group: {item.GroupName}";
    }

    private static ScheduledItem CloneScheduledItem(ScheduledItem source)
    {
        return new ScheduledItem
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            GroupName = source.GroupName,
            TargetKind = source.TargetKind,
            TriggerAtUtc = source.TriggerAtUtc,
            IsRecurring = source.IsRecurring,
            RecurrenceExpression = source.RecurrenceExpression,
            TriggerWindowSeconds = source.TriggerWindowSeconds,
            IsEnabled = source.IsEnabled,
            IsRunning = source.IsRunning,
            CreatedAtUtc = source.CreatedAtUtc,
			UpdatedAtUtc = source.UpdatedAtUtc,
			Revision = source.Revision,
			ExecutionTargetMode = source.ExecutionTargetMode,
			TargetAgentId = source.TargetAgentId,
			TargetPoolId = source.TargetPoolId,
			PublishedAgentId = source.PublishedAgentId,
			PendingPreviousAgentId = source.PendingPreviousAgentId,
			AcceptedRevision = source.AcceptedRevision,
			RemoteDeploymentState = source.RemoteDeploymentState,
			RemoteDeploymentMessage = source.RemoteDeploymentMessage,
			LastAcknowledgedOccurrenceSequence = source.LastAcknowledgedOccurrenceSequence,
            LastTriggeredAtUtc = source.LastTriggeredAtUtc,
            LastCompletedAtUtc = source.LastCompletedAtUtc,
            LastOccurrenceToken = source.LastOccurrenceToken,
            NextRunAtUtc = source.NextRunAtUtc,
            LastRunOutcome = source.LastRunOutcome,
            LastRunMessage = source.LastRunMessage,
            SourceTaskName = source.SourceTaskName,
            TaskSnapshot = source.TaskSnapshot is null ? null : SchedulerSnapshotFactory.CloneAsLocalSnapshot(source.TaskSnapshot),
            ChainSnapshot = source.ChainSnapshot.Select(SchedulerSnapshotFactory.CloneAsLocalSnapshot).ToList()
        };
    }
}
