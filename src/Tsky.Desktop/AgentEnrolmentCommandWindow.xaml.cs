using System.Windows;
using System.Windows.Input;

namespace tsky;

public partial class AgentEnrolmentCommandWindow : Window
{
    public AgentEnrolmentCommandWindow(string command, DateTime expiresAtUtc)
    {
        InitializeComponent();
        CommandTextBox.Text = command;
        ExpiryTextBlock.Text = $"Token expires {expiresAtUtc.ToLocalTime():g}. It is shown only once.";

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        PreviewKeyDown += AgentEnrolmentCommandWindow_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            CommandTextBox.Focus();
            CommandTextBox.SelectAll();
        };
    }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CommandTextBox.Text);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AgentEnrolmentCommandWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
