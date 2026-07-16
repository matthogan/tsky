using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace tsky;

public partial class GroupNamePromptWindow : Window
{
    private readonly HashSet<string> _existingGroupNames;

    public GroupNamePromptWindow(IEnumerable<string> existingGroupNames, string suggestedGroupName)
    {
        InitializeComponent();

        _existingGroupNames = new HashSet<string>(
            (existingGroupNames ?? []).Select(TaskIdentity.NormalizeGroupName),
            StringComparer.OrdinalIgnoreCase);

        GroupNameTextBox.Text = string.IsNullOrWhiteSpace(suggestedGroupName)
            ? "New Group"
            : suggestedGroupName.Trim();
        GroupNameTextBox.SelectAll();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;

        PreviewKeyDown += GroupNamePromptWindow_PreviewKeyDown;
        GroupNameTextBox.TextChanged += (_, _) => ValidateCurrentName(showMessage: false);

        Loaded += (_, _) =>
        {
            GroupNameTextBox.Focus();
            ValidateCurrentName(showMessage: false);
        };

        DebugTraceLog.Write($"[GroupNamePrompt] Opened with suggested='{GroupNameTextBox.Text}' and existingCount={_existingGroupNames.Count}.");
    }

    public string GroupName => TaskIdentity.NormalizeGroupName(GroupNameTextBox.Text);

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void GroupNamePromptWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DebugTraceLog.Write("[GroupNamePrompt] Cancelled via Escape key.");
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && TrySubmit())
        {
            e.Handled = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TrySubmit();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DebugTraceLog.Write("[GroupNamePrompt] Cancelled via Cancel button.");
        DialogResult = false;
    }

    private bool TrySubmit()
    {
        if (!ValidateCurrentName(showMessage: true))
        {
            DebugTraceLog.Write($"[GroupNamePrompt] Submit blocked. Raw input='{GroupNameTextBox.Text?.Trim() ?? string.Empty}'.");
            GroupNameTextBox.Focus();
            GroupNameTextBox.SelectAll();
            return false;
        }

        DebugTraceLog.Write($"[GroupNamePrompt] Accepted group name '{GroupName}'.");
        DialogResult = true;
        return true;
    }

    private bool ValidateCurrentName(bool showMessage)
    {
        var rawValue = GroupNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return SetValidationResult(false, "Group name is required.", showMessage);
        }

        var candidate = TaskIdentity.NormalizeGroupName(rawValue);

        if (_existingGroupNames.Contains(candidate))
        {
            return SetValidationResult(false, $"Group '{candidate}' already exists.", showMessage);
        }

        return SetValidationResult(true, string.Empty, showMessage);
    }

    private bool SetValidationResult(bool isValid, string message, bool showMessage)
    {
        OkButton.IsEnabled = isValid;

        if (isValid || !showMessage)
        {
            ValidationTextBlock.Visibility = Visibility.Collapsed;
            ValidationTextBlock.Text = string.Empty;
            return isValid;
        }

        ValidationTextBlock.Text = message;
        ValidationTextBlock.Visibility = Visibility.Visible;
        return false;
    }
}
