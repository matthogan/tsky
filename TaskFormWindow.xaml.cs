using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace tsky;

public partial class TaskFormWindow : Window
{
    public TaskFormWindow()
    {
        InitializeComponent();
        ApplyTheme();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
    }

    public LauncherTask? Task { get; private set; }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void ApplyTheme()
    {
        foreach (var textBox in new[] { NameTextBox, DescriptionTextBox, GroupTextBox, CommandLineTextBox, WorkingDirectoryTextBox, EnvVarsTextBox })
        {
            textBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
            textBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            textBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
        }

        RunnerComboBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        RunnerComboBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        RunnerComboBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
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

        if (!TryParseEnvLines(EnvVarsTextBox.Text, out var envVars, out var envError))
        {
            AppMessageBox.Show(this, envError, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            EnvVarsTextBox.Focus();
            return;
        }

        var runner = (RunnerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "exe";

        Task = new LauncherTask
        {
            Name = NameTextBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim(),
            Group = string.IsNullOrWhiteSpace(GroupTextBox.Text) ? "General" : GroupTextBox.Text.Trim(),
            Runner = runner,
            CommandLine = CommandLineTextBox.Text.Trim(),
            WorkingDirectory = WorkingDirectoryTextBox.Text.Trim(),
            RequiresElevation = RequiresElevationCheckBox.IsChecked == true,
            IgnoreExitCode = IgnoreExitCodeCheckBox.IsChecked == true,
            Env = envVars
        };

        DialogResult = true;
    }

    private static bool TryParseEnvLines(string text, out Dictionary<string, string> envVars, out string error)
    {
        envVars = [];
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
                error = $"Invalid env var line: '{line}'. Use NAME=VALUE format.";
                return false;
            }

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..];

            if (name.Length == 0)
            {
                error = $"Invalid env var line: '{line}'. Name cannot be empty.";
                return false;
            }

            envVars[name] = value;
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
