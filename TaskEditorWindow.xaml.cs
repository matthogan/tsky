using System.Windows;
using System.Windows.Controls;

namespace tsky;

public partial class TaskEditorWindow : Window
{
    public TaskEditorWindow(string taskJson)
    {
        InitializeComponent();
        ApplyTheme();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;

        TaskDefinitionTextBox.Text = taskJson;
        TaskDefinitionTextBox.Select(0, 0);
        TaskDefinitionTextBox.Focus();
    }

    public string TaskJsonText => TaskDefinitionTextBox.Text;

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void ApplyTheme()
    {
        TaskDefinitionTextBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        TaskDefinitionTextBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        TaskDefinitionTextBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
