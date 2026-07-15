using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace tsky;

public partial class TaskFormWindow : Window
{
    private const double DefaultDialogWidth = 760;
    private const double DefaultDialogHeight = 860;

    public TaskFormWindow(IEnumerable<string>? existingGroups = null, string? defaultGroupName = null)
    {
        InitializeComponent();
        ApplyPersistedWindowSize();
        InitializeGroupChoices(existingGroups, defaultGroupName);
        ApplyTheme();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        Closed += TaskFormWindow_Closed;
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

        var runner = (RunnerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "exe";

        Task = new LauncherTask
        {
            Name = NameTextBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim(),
            Group = string.IsNullOrWhiteSpace(GroupComboBox.Text) ? "General" : GroupComboBox.Text.Trim(),
            Runner = runner,
            CommandLine = CommandLineTextBox.Text.Trim(),
            WorkingDirectory = WorkingDirectoryTextBox.Text.Trim(),
            WaitForExit = WaitForExitCheckBox.IsChecked != false,
            RequiresElevation = RequiresElevationCheckBox.IsChecked == true,
            IgnoreExitCode = IgnoreExitCodeCheckBox.IsChecked == true,
            TimeoutSeconds = timeoutSeconds,
            KillProcessTreeOnCancel = KillProcessTreeOnCancelCheckBox.IsChecked != false,
            MaxCapturedOutputChars = maxCapturedOutputChars,
            IsEnabled = IsEnabledCheckBox.IsChecked != false,
            Env = envVars
        };

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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
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
