using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace tsky;

public partial class TaskFormWindow : Window
{
    private const double DefaultDialogWidth = 760;
    private const double DefaultDialogHeight = 860;

    private sealed class TemplateChoice
    {
        public required string Label { get; init; }

        public LauncherTask? TemplateTask { get; init; }

        public override string ToString()
        {
            return Label;
        }
    }

    private sealed class TemplateSelectionState
    {
        public required string Name { get; init; }

        public required string Description { get; init; }

        public required string Group { get; init; }

        public required string Runner { get; init; }

        public required string CommandLine { get; init; }

        public required string WorkingDirectory { get; init; }

        public required bool? RequiresElevation { get; init; }

        public required bool? IgnoreExitCode { get; init; }

        public required bool? WaitForExit { get; init; }

        public required bool? KillProcessTreeOnCancel { get; init; }

        public required string TimeoutSeconds { get; init; }

        public required string Retries { get; init; }

        public required string MaxCapturedOutputChars { get; init; }

        public required bool? IsEnabled { get; init; }

        public required string EnvVars { get; init; }
    }

    private LauncherTask? _selectedTemplateTask;
    private TemplateSelectionState? _templateSelectionStateBuffer;

    public TaskFormWindow(
        IEnumerable<string>? existingGroups = null,
        string? defaultGroupName = null,
        IReadOnlyList<LauncherTask>? availableTemplates = null,
        bool showTemplatePicker = false)
    {
        InitializeComponent();
        ApplyPersistedWindowSize();
        InitializeGroupChoices(existingGroups, defaultGroupName);
        InitializeTemplateChoices(availableTemplates, showTemplatePicker);
        ApplyTheme();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        Closed += TaskFormWindow_Closed;
        PreviewKeyDown += TaskFormWindow_PreviewKeyDown;
    }

    public LauncherTask? Task { get; private set; }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void ApplyTheme()
    {
        foreach (var textBox in new[]
                 {
                     NameTextBox,
                     DescriptionTextBox,
                     CommandLineTextBox,
                     WorkingDirectoryTextBox,
                     TimeoutSecondsTextBox,
                     RetriesTextBox,
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

        TemplateComboBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        TemplateComboBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        TemplateComboBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
    }

    private void InitializeGroupChoices(IEnumerable<string>? existingGroups, string? defaultGroupName)
    {
        var defaultGroup = string.IsNullOrWhiteSpace(defaultGroupName)
            ? "General"
            : defaultGroupName.Trim();

        var sortedGroups = (existingGroups ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        GroupComboBox.ItemsSource = sortedGroups;

        var existingDefaultGroup = sortedGroups.FirstOrDefault(group =>
            string.Equals(group, defaultGroup, StringComparison.OrdinalIgnoreCase));

        if (existingDefaultGroup is not null)
        {
            GroupComboBox.SelectedItem = existingDefaultGroup;
            return;
        }

        GroupComboBox.Text = defaultGroup;
    }

    private void InitializeTemplateChoices(IReadOnlyList<LauncherTask>? availableTemplates, bool showTemplatePicker)
    {
        if (!showTemplatePicker)
        {
            TemplatePickerLabel.Visibility = Visibility.Collapsed;
            TemplateComboBox.Visibility = Visibility.Collapsed;
            TemplatePickerHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var orderedTemplates = (availableTemplates ?? [])
            .Where(task => !string.IsNullOrWhiteSpace(task.Name))
            .OrderBy(task => task.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Group?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(task => new TemplateChoice
            {
                Label = $"{task.Name?.Trim() ?? string.Empty} ({task.Group?.Trim() ?? "General"})",
                TemplateTask = CloneTask(task)
            })
            .ToList();

        var choices = new List<TemplateChoice>
        {
            new()
            {
                Label = "No template (blank task)",
                TemplateTask = null
            }
        };

        choices.AddRange(orderedTemplates);
        TemplateComboBox.ItemsSource = choices;
        TemplateComboBox.DisplayMemberPath = nameof(TemplateChoice.Label);
        TemplateComboBox.SelectedIndex = 0;

        if (orderedTemplates.Count == 0)
        {
            TemplateComboBox.IsEnabled = false;
            TemplatePickerHintText.Text = "No templates available yet. Add templates from the Task Templates view.";
        }
    }

    private void ApplyPersistedWindowSize()
    {
        var state = UiStateStore.Load();

        Width = state.AddTaskDialogWidth >= MinWidth
            ? state.AddTaskDialogWidth
            : DefaultDialogWidth;

        Height = state.AddTaskDialogHeight >= MinHeight
            ? state.AddTaskDialogHeight
            : DefaultDialogHeight;
    }

    private void TaskFormWindow_Closed(object? sender, EventArgs e)
    {
        var state = UiStateStore.Load();
        state.AddTaskDialogWidth = Math.Max(MinWidth, Width);
        state.AddTaskDialogHeight = Math.Max(MinHeight, Height);
        UiStateStore.Save(state);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            AppMessageBox.Show(this, "Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(CommandLineTextBox.Text))
        {
            AppMessageBox.Show(this, "Command line is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            CommandLineTextBox.Focus();
            return;
        }

        if (!TryParseNameValueLines(EnvVarsTextBox.Text, "env var", out var envVars, out var envError))
        {
            AppMessageBox.Show(this, envError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            EnvVarsTextBox.Focus();
            return;
        }

        if (!TryParsePositiveInt(TimeoutSecondsTextBox.Text, "Timeout (sec)", out var timeoutSeconds, out var timeoutError))
        {
            AppMessageBox.Show(this, timeoutError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            TimeoutSecondsTextBox.Focus();
            return;
        }

        if (!TryParsePositiveInt(MaxCapturedOutputCharsTextBox.Text, "Max Output Chars", out var maxCapturedOutputChars, out var outputCharsError))
        {
            AppMessageBox.Show(this, outputCharsError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaxCapturedOutputCharsTextBox.Focus();
            return;
        }

        if (!TryParseNonNegativeInt(RetriesTextBox.Text, "Retries", out var retries, out var retriesError))
        {
            AppMessageBox.Show(this, retriesError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            RetriesTextBox.Focus();
            return;
        }

        var runner = (RunnerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "exe";

        var task = _selectedTemplateTask is null
            ? new LauncherTask()
            : CloneTask(_selectedTemplateTask);

        task.Name = NameTextBox.Text.Trim();
        task.Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
        task.Group = string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "General" : GroupComboBox.Text.Trim();
        task.Runner = runner;
        task.CommandLine = CommandLineTextBox.Text.Trim();
        task.WorkingDirectory = WorkingDirectoryTextBox.Text.Trim();
        task.WaitForExit = WaitForExitCheckBox.IsChecked != false;
        task.RequiresElevation = RequiresElevationCheckBox.IsChecked == true;
        task.IgnoreExitCode = IgnoreExitCodeCheckBox.IsChecked == true;
        task.TimeoutSeconds = timeoutSeconds;
        task.Retries = retries;
        task.KillProcessTreeOnCancel = KillProcessTreeOnCancelCheckBox.IsChecked != false;
        task.MaxCapturedOutputChars = maxCapturedOutputChars;
        task.IsEnabled = IsEnabledCheckBox.IsChecked != false;
        task.Env = envVars;
        task.ImportedFromFile = null;
        task.ImportedAtUtc = null;

        Task = task;

        DialogResult = true;
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

    private static bool TryParseNonNegativeInt(string text, string fieldName, out int value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!int.TryParse(text.Trim(), out value) || value < 0)
        {
            error = $"{fieldName} must be a non-negative integer.";
            return false;
        }

        return true;
    }

    private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateComboBox.SelectedItem is not TemplateChoice choice)
        {
            _selectedTemplateTask = null;
            return;
        }

        if (choice.TemplateTask is null)
        {
            _selectedTemplateTask = null;

            if (_templateSelectionStateBuffer is not null)
            {
                RestoreTemplateSelectionState(_templateSelectionStateBuffer);
                _templateSelectionStateBuffer = null;
            }

            return;
        }

        if (_templateSelectionStateBuffer is null)
        {
            _templateSelectionStateBuffer = CaptureTemplateSelectionState();
        }

        _selectedTemplateTask = CloneTask(choice.TemplateTask);
        ApplyTemplateToForm(choice.TemplateTask);
    }

    private TemplateSelectionState CaptureTemplateSelectionState()
    {
        return new TemplateSelectionState
        {
            Name = NameTextBox.Text,
            Description = DescriptionTextBox.Text,
            Group = GroupComboBox.Text,
            Runner = GetSelectedRunner(),
            CommandLine = CommandLineTextBox.Text,
            WorkingDirectory = WorkingDirectoryTextBox.Text,
            RequiresElevation = RequiresElevationCheckBox.IsChecked,
            IgnoreExitCode = IgnoreExitCodeCheckBox.IsChecked,
            WaitForExit = WaitForExitCheckBox.IsChecked,
            KillProcessTreeOnCancel = KillProcessTreeOnCancelCheckBox.IsChecked,
            TimeoutSeconds = TimeoutSecondsTextBox.Text,
            Retries = RetriesTextBox.Text,
            MaxCapturedOutputChars = MaxCapturedOutputCharsTextBox.Text,
            IsEnabled = IsEnabledCheckBox.IsChecked,
            EnvVars = EnvVarsTextBox.Text
        };
    }

    private void RestoreTemplateSelectionState(TemplateSelectionState state)
    {
        NameTextBox.Text = state.Name;
        DescriptionTextBox.Text = state.Description;
        GroupComboBox.Text = state.Group;

        EnsureRunnerChoice(state.Runner);
        RunnerComboBox.SelectedItem = RunnerComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), state.Runner, StringComparison.OrdinalIgnoreCase));
        if (RunnerComboBox.SelectedItem is null)
        {
            RunnerComboBox.Text = state.Runner;
        }

        CommandLineTextBox.Text = state.CommandLine;
        WorkingDirectoryTextBox.Text = state.WorkingDirectory;
        RequiresElevationCheckBox.IsChecked = state.RequiresElevation;
        IgnoreExitCodeCheckBox.IsChecked = state.IgnoreExitCode;
        WaitForExitCheckBox.IsChecked = state.WaitForExit;
        KillProcessTreeOnCancelCheckBox.IsChecked = state.KillProcessTreeOnCancel;
        TimeoutSecondsTextBox.Text = state.TimeoutSeconds;
        RetriesTextBox.Text = state.Retries;
        MaxCapturedOutputCharsTextBox.Text = state.MaxCapturedOutputChars;
        IsEnabledCheckBox.IsChecked = state.IsEnabled;
        EnvVarsTextBox.Text = state.EnvVars;
    }

    private string GetSelectedRunner()
    {
        var selectedRunner = (RunnerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(selectedRunner))
        {
            return selectedRunner;
        }

        if (!string.IsNullOrWhiteSpace(RunnerComboBox.Text))
        {
            return RunnerComboBox.Text.Trim();
        }

        return "exe";
    }

    private void ApplyTemplateToForm(LauncherTask templateTask)
    {
        NameTextBox.Text = templateTask.Name;
        DescriptionTextBox.Text = templateTask.Description ?? string.Empty;
        GroupComboBox.Text = string.IsNullOrWhiteSpace(templateTask.Group) ? "General" : templateTask.Group;

        var runner = string.IsNullOrWhiteSpace(templateTask.Runner) ? "exe" : templateTask.Runner.Trim();
        EnsureRunnerChoice(runner);
        RunnerComboBox.SelectedItem = RunnerComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Content?.ToString(), runner, StringComparison.OrdinalIgnoreCase));

        CommandLineTextBox.Text = templateTask.CommandLine;
        WorkingDirectoryTextBox.Text = templateTask.WorkingDirectory;
        RequiresElevationCheckBox.IsChecked = templateTask.RequiresElevation;
        IgnoreExitCodeCheckBox.IsChecked = templateTask.IgnoreExitCode;
        WaitForExitCheckBox.IsChecked = templateTask.WaitForExit;
        KillProcessTreeOnCancelCheckBox.IsChecked = templateTask.KillProcessTreeOnCancel;
        TimeoutSecondsTextBox.Text = templateTask.TimeoutSeconds.ToString();
        RetriesTextBox.Text = templateTask.Retries.ToString();
        MaxCapturedOutputCharsTextBox.Text = templateTask.MaxCapturedOutputChars.ToString();
        IsEnabledCheckBox.IsChecked = templateTask.IsEnabled;
        EnvVarsTextBox.Text = string.Join(Environment.NewLine, templateTask.Env.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private void EnsureRunnerChoice(string runner)
    {
        if (RunnerComboBox.Items.OfType<ComboBoxItem>().Any(item =>
                string.Equals(item.Content?.ToString(), runner, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        RunnerComboBox.Items.Add(new ComboBoxItem { Content = runner });
    }

    private static LauncherTask CloneTask(LauncherTask source)
    {
        return new LauncherTask
        {
            Name = source.Name,
            Description = source.Description,
            Group = source.Group,
            CommandLine = source.CommandLine,
            Runner = source.Runner,
            Env = CloneStringDictionary(source.Env),
            WorkingDirectory = source.WorkingDirectory,
            WaitForExit = source.WaitForExit,
            RequiresElevation = source.RequiresElevation,
            IgnoreExitCode = source.IgnoreExitCode,
            TimeoutSeconds = source.TimeoutSeconds,
            Retries = source.Retries,
            KillProcessTreeOnCancel = source.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = source.MaxCapturedOutputChars,
            IsEnabled = source.IsEnabled,
            ResultEvaluatorScript = source.ResultEvaluatorScript,
            ImportedFromFile = source.ImportedFromFile,
            ImportedAtUtc = source.ImportedAtUtc,
            Inputs = CloneStringDictionary(source.Inputs),
            InputBindings = CloneStringDictionary(source.InputBindings),
            Outputs = CloneStringDictionary(source.Outputs)
        };
    }

    private static Dictionary<string, string> CloneStringDictionary(IReadOnlyDictionary<string, string> source)
    {
        return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TaskFormWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        e.Handled = true;
    }

    private void BrowseCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select command executable or script",
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            CommandLineTextBox.Text = QuoteIfHasSpaces(dialog.FileName);
        }
    }

    private void BrowseDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select working directory",
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "Select this folder"
        };

        if (dialog.ShowDialog() == true)
        {
            var directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                WorkingDirectoryTextBox.Text = directory;
            }
        }
    }

    private static string QuoteIfHasSpaces(string path)
    {
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
