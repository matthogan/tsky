using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace tsky;

public partial class AgentGatewaySettingsWindow : Window
{
    public AgentGatewaySettingsWindow(AgentGatewaySettings settings)
    {
        InitializeComponent();

        EnableTlsCheckBox.IsChecked = settings.EnableTlsListener;
        HostTextBox.Text = settings.BindHost;
        PortTextBox.Text = settings.Port.ToString();
        CertPathTextBox.Text = settings.CertificatePath ?? string.Empty;
        CertPasswordBox.Password = settings.CertificatePassword ?? string.Empty;
        DevFallbackCheckBox.IsChecked = settings.IsDevLoopbackFallbackEnabled;

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
    }

    public AgentGatewaySettings? Settings { get; private set; }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void BrowseCertButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select TLS certificate",
            Filter = "PFX certificate (*.pfx)|*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            CertPathTextBox.Text = dialog.FileName;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            AppMessageBox.Show(this, "Port must be between 1 and 65535.", "Gateway Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var certPath = string.IsNullOrWhiteSpace(CertPathTextBox.Text) ? null : CertPathTextBox.Text.Trim();
        if (EnableTlsCheckBox.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
            {
                AppMessageBox.Show(this, "TLS mode requires a valid PFX certificate path.", "Gateway Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Settings = new AgentGatewaySettings
        {
            EnableTlsListener = EnableTlsCheckBox.IsChecked == true,
            BindHost = string.IsNullOrWhiteSpace(HostTextBox.Text) ? "localhost" : HostTextBox.Text.Trim(),
            Port = port,
            CertificatePath = certPath,
            CertificatePassword = string.IsNullOrWhiteSpace(CertPasswordBox.Password) ? null : CertPasswordBox.Password,
            IsDevLoopbackFallbackEnabled = DevFallbackCheckBox.IsChecked == true
        };

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
