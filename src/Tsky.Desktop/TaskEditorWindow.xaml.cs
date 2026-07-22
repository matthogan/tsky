using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace tsky;

public partial class TaskEditorWindow : Window
{
    private string _taskJsonText;
    private LauncherTask _structuredBaselineTask = new();
    private bool _isEditorInitialized;
    private List<string> _groupChoices = [];

    public TaskEditorWindow(string taskJson, IEnumerable<string>? existingGroups = null)
    {
        InitializeComponent();
        ApplyTheme();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        PreviewKeyDown += TaskEditorWindow_PreviewKeyDown;

        _taskJsonText = taskJson;
        TaskDefinitionTextBox.Text = taskJson;

        LauncherTask? parsedTask = null;
        if (TaskConfiguration.TryDeserializeTask(taskJson, out var deserializedTask, out _))
        {
            parsedTask = deserializedTask;
        }

        InitializeGroupChoices(existingGroups, parsedTask?.Group);

        if (parsedTask is not null)
        {
            _structuredBaselineTask = CloneTask(parsedTask);
            PopulateFormFromTask(parsedTask);
        }

        _isEditorInitialized = true;
        SetSourceMode(isSourceMode: false, syncFromForm: false);
    }

    public string TaskJsonText => _taskJsonText;

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void ApplyTheme()
    {
        TaskDefinitionTextBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        TaskDefinitionTextBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        TaskDefinitionTextBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");

        foreach (var textBox in new[]
                 {
                     NameTextBox,
                     DescriptionTextBox,
                     CommandLineTextBox,
                     WorkingDirectoryTextBox,
                     TimeoutSecondsTextBox,
                     MaxCapturedOutputCharsTextBox,
                     EnvVarsTextBox
                 })
        {
            textBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
            textBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            textBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
        }

        RunnerComboBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        RunnerComboBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        RunnerComboBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");

        GroupComboBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        GroupComboBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        GroupComboBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
    }

    private void InitializeGroupChoices(IEnumerable<string>? existingGroups, string? taskGroup)
    {
        _groupChoices = (existingGroups ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedTaskGroup = string.IsNullOrWhiteSpace(taskGroup)
            ? null
            : DecodeSafeJsonString(taskGroup).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedTaskGroup)
            && !_groupChoices.Contains(normalizedTaskGroup, StringComparer.OrdinalIgnoreCase))
        {
            _groupChoices.Add(normalizedTaskGroup);
            _groupChoices = _groupChoices
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        GroupComboBox.ItemsSource = _groupChoices;
    }

    private void PopulateFormFromTask(LauncherTask task)
    {
        NameTextBox.Text = DecodeSafeJsonString(task.Name);
        DescriptionTextBox.Text = DecodeSafeJsonString(task.Description);
        SetGroupSelection(DecodeSafeJsonString(task.Group));

        SetRunnerSelection(DecodeSafeJsonString(task.Runner));

        CommandLineTextBox.Text = DecodeSafeJsonString(task.CommandLine);
        WorkingDirectoryTextBox.Text = DecodeSafeJsonString(task.WorkingDirectory);

        WaitForExitCheckBox.IsChecked = task.WaitForExit;
        RequiresElevationCheckBox.IsChecked = task.RequiresElevation;
        IgnoreExitCodeCheckBox.IsChecked = task.IgnoreExitCode;
        TimeoutSecondsTextBox.Text = task.TimeoutSeconds.ToString();
        KillProcessTreeOnCancelCheckBox.IsChecked = task.KillProcessTreeOnCancel;
        MaxCapturedOutputCharsTextBox.Text = task.MaxCapturedOutputChars.ToString();
        IsEnabledCheckBox.IsChecked = task.IsEnabled;

        EnvVarsTextBox.Text = string.Join(Environment.NewLine,
            task.Env.Select(pair => $"{pair.Key}={DecodeSafeJsonString(pair.Value)}"));
    }

    private void SetGroupSelection(string? group)
    {
        var normalizedGroup = string.IsNullOrWhiteSpace(group) ? "General" : group.Trim();

        if (!_groupChoices.Contains(normalizedGroup, StringComparer.OrdinalIgnoreCase))
        {
            _groupChoices.Add(normalizedGroup);
            _groupChoices = _groupChoices
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            GroupComboBox.ItemsSource = null;
            GroupComboBox.ItemsSource = _groupChoices;
        }

        var matchingGroup = GroupComboBox.Items
            .OfType<string>()
            .FirstOrDefault(item => string.Equals(item, normalizedGroup, StringComparison.OrdinalIgnoreCase));

        if (matchingGroup is not null)
        {
            GroupComboBox.SelectedItem = matchingGroup;
            return;
        }

        GroupComboBox.SelectedItem = null;
        GroupComboBox.Text = normalizedGroup;
    }

    private void SetRunnerSelection(string? runner)
    {
        var normalizedRunner = string.IsNullOrWhiteSpace(runner) ? "exe" : runner.Trim();

        foreach (var item in RunnerComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), normalizedRunner, StringComparison.OrdinalIgnoreCase))
            {
                RunnerComboBox.SelectedItem = item;
                return;
            }
        }

        RunnerComboBox.SelectedIndex = 0;
    }

    private LauncherTask? BuildTaskFromForm()
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            AppMessageBox.Show(this, "Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameTextBox.Focus();
            return null;
        }

        if (string.IsNullOrWhiteSpace(CommandLineTextBox.Text))
        {
            AppMessageBox.Show(this, "Command line is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            CommandLineTextBox.Focus();
            return null;
        }

        if (!TryParseNameValueLines(EnvVarsTextBox.Text, "env var", out var envVars, out var envError))
        {
            AppMessageBox.Show(this, envError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            EnvVarsTextBox.Focus();
            return null;
        }

        if (!TryParsePositiveInt(TimeoutSecondsTextBox.Text, "Timeout (sec)", out var timeoutSeconds, out var timeoutError))
        {
            AppMessageBox.Show(this, timeoutError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            TimeoutSecondsTextBox.Focus();
            return null;
        }

        if (!TryParsePositiveInt(MaxCapturedOutputCharsTextBox.Text, "Max Output Chars", out var maxCapturedOutputChars, out var outputCharsError))
        {
            AppMessageBox.Show(this, outputCharsError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaxCapturedOutputCharsTextBox.Focus();
            return null;
        }

        var runner = (RunnerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "exe";

        var mergedTask = CloneTask(_structuredBaselineTask);

        mergedTask.Name = NameTextBox.Text.Trim();
        mergedTask.Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
        mergedTask.Group = string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "General" : GroupComboBox.Text.Trim();
        mergedTask.Runner = runner;
        mergedTask.CommandLine = CommandLineTextBox.Text.Trim();
        mergedTask.WorkingDirectory = WorkingDirectoryTextBox.Text.Trim();
        mergedTask.WaitForExit = WaitForExitCheckBox.IsChecked != false;
        mergedTask.RequiresElevation = RequiresElevationCheckBox.IsChecked == true;
        mergedTask.IgnoreExitCode = IgnoreExitCodeCheckBox.IsChecked == true;
        mergedTask.TimeoutSeconds = timeoutSeconds;
        mergedTask.KillProcessTreeOnCancel = KillProcessTreeOnCancelCheckBox.IsChecked != false;
        mergedTask.MaxCapturedOutputChars = maxCapturedOutputChars;
        mergedTask.IsEnabled = IsEnabledCheckBox.IsChecked != false;
        mergedTask.Env = envVars;

        return mergedTask;
    }

    private static LauncherTask CloneTask(LauncherTask source)
    {
        return new LauncherTask
        {
            Name = source.Name,
            Description = source.Description,
            Group = source.Group,
            ExecutionTargetMode = source.ExecutionTargetMode,
            TargetAgentId = source.TargetAgentId,
            TargetPoolId = source.TargetPoolId,
            CommandLine = source.CommandLine,
            Runner = source.Runner,
            Env = new Dictionary<string, string>(source.Env, StringComparer.OrdinalIgnoreCase),
            WorkingDirectory = source.WorkingDirectory,
            WaitForExit = source.WaitForExit,
            RequiresElevation = source.RequiresElevation,
            IgnoreExitCode = source.IgnoreExitCode,
            TimeoutSeconds = source.TimeoutSeconds,
            KillProcessTreeOnCancel = source.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = source.MaxCapturedOutputChars,
            IsEnabled = source.IsEnabled,
            ImportedFromFile = source.ImportedFromFile,
            ImportedAtUtc = source.ImportedAtUtc,
            Inputs = new Dictionary<string, string>(source.Inputs, StringComparer.OrdinalIgnoreCase),
            InputBindings = new Dictionary<string, string>(source.InputBindings, StringComparer.OrdinalIgnoreCase),
            Outputs = new Dictionary<string, string>(source.Outputs, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void SetSourceMode(bool isSourceMode, bool syncFromForm = true)
    {
        if (isSourceMode && syncFromForm)
        {
            var taskFromForm = BuildTaskFromForm();
            if (taskFromForm is not null)
            {
                TaskDefinitionTextBox.Text = TaskConfiguration.SerializeTask(taskFromForm);
            }
        }
        else
        {
            if (!TaskConfiguration.TryDeserializeTask(TaskDefinitionTextBox.Text, out var sourceTask, out var parseError))
            {
                AppMessageBox.Show(this, $"Could not switch to form mode because source JSON is invalid.\n\n{parseError}", "Task Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                SourceModeToggleButton.IsChecked = true;
                return;
            }

            _structuredBaselineTask = CloneTask(sourceTask);
            PopulateFormFromTask(sourceTask);
        }

        TaskDefinitionTextBox.Visibility = isSourceMode ? Visibility.Visible : Visibility.Collapsed;
        StructuredEditorBorder.Visibility = isSourceMode ? Visibility.Collapsed : Visibility.Visible;
        EditorTitleTextBlock.Text = isSourceMode ? "Task Source" : "Task Fields";

        if (isSourceMode)
        {
            TaskDefinitionTextBox.Select(0, 0);
            TaskDefinitionTextBox.Focus();
        }
        else
        {
            NameTextBox.Focus();
        }
    }

    private static string DecodeSafeJsonString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (!LooksEncodedForSafeJson(value))
        {
            return value;
        }

        try
        {
            var jsonLiteral = $"\"{value.Replace("\"", "\\\"")}\"";
            return JsonSerializer.Deserialize<string>(jsonLiteral) ?? value;
        }
        catch
        {
            return value;
        }
    }

    private static bool LooksEncodedForSafeJson(string value)
    {
        return value.Contains("\\\\", StringComparison.Ordinal)
               || value.Contains("\\\"", StringComparison.Ordinal)
               || value.Contains("\\n", StringComparison.Ordinal)
               || value.Contains("\\r", StringComparison.Ordinal)
               || value.Contains("\\u", StringComparison.Ordinal);
    }

    private static bool TryParseNameValueLines(string text, string entryTypeLabel, out Dictionary<string, string> values, out string error)
    {
        values = [];
        error = string.Empty;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                error = $"Invalid {entryTypeLabel} line: '{line}'. Use NAME=VALUE format.";
                return false;
            }

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..];

            if (name.Length == 0)
            {
                error = $"Invalid {entryTypeLabel} line: '{line}'. Name cannot be empty.";
                return false;
            }

            values[name] = value;
        }

        return true;
    }

    private static bool TryParsePositiveInt(string text, string fieldName, out int value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!int.TryParse(text.Trim(), out value) || value <= 0)
        {
            error = $"{fieldName} must be a positive integer.";
            return false;
        }

        return true;
    }

    private void SourceModeToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isEditorInitialized)
        {
            return;
        }

        if (SourceModeToggleButton.IsChecked != true)
        {
            SetSourceMode(isSourceMode: false);
            return;
        }

        SetSourceMode(isSourceMode: true);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceModeToggleButton.IsChecked == true)
        {
            if (!TaskConfiguration.TryDeserializeTask(TaskDefinitionTextBox.Text, out _, out var parseError))
            {
                AppMessageBox.Show(this, $"Could not save task because the JSON is invalid.\n\n{parseError}", "Task Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _taskJsonText = TaskDefinitionTextBox.Text;
            DialogResult = true;
            return;
        }

        var taskFromFields = BuildTaskFromForm();
        if (taskFromFields is null)
        {
            return;
        }

        _taskJsonText = TaskConfiguration.SerializeTask(taskFromFields);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TaskEditorWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        e.Handled = true;
    }
}
