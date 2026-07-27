using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace tsky;

public partial class SchedulerItemEditorWindow : Window
{
    private readonly ScheduledItem _draft;

    public SchedulerItemEditorWindow(ScheduledItem source, bool isNew)
    {
        InitializeComponent();

        _draft = CloneScheduledItem(source);
        DialogTitleTextBlock.Text = isNew ? "Create scheduled item" : "Edit scheduled item";
        TargetSummaryTextBlock.Text = BuildTargetSummary(_draft);

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
