using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;

namespace tsky;

public partial class AgentDefinitionWindow : Window
{
    private readonly AgentDefinition _agent;

    public AgentDefinitionWindow(AgentDefinition agent, bool isNew)
    {
        InitializeComponent();

        _agent = Clone(agent);
        StateComboBox.ItemsSource = GetEditableStates();
        DisplayNameTextBox.Text = _agent.DisplayName;
        DescriptionTextBox.Text = _agent.Description ?? string.Empty;
        ControllerUrlTextBox.Text = _agent.ControllerUrl ?? "http://localhost:7341";
        MaximumRunsTextBox.Text = _agent.MaximumConcurrentRuns.ToString(CultureInfo.CurrentCulture);
        RequiresApprovalCheckBox.IsChecked = _agent.RequiresApproval;
        AllowedExecutorsTextBox.Text = string.Join(", ", _agent.AllowedExecutors);
        StateComboBox.SelectedItem = _agent.State;
        RegenerateTokenCheckBox.IsChecked = isNew || _agent.State is AgentDefinitionState.Pending or AgentDefinitionState.Enrolling;

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;
        PreviewKeyDown += AgentDefinitionWindow_PreviewKeyDown;
        Loaded += (_, _) => DisplayNameTextBox.Focus();
    }

    public AgentDefinition Agent => _agent;

    public bool RegenerateEnrolmentToken => RegenerateTokenCheckBox.IsChecked == true;

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void AgentDefinitionWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApply())
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool TryApply()
    {
        var name = DisplayNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return SetValidation("Agent name is required.");
        }

        if (!int.TryParse(MaximumRunsTextBox.Text.Trim(), out var maximumRuns) || maximumRuns < 1)
        {
            return SetValidation("Max runs must be a positive integer.");
        }

        var executors = AllowedExecutorsTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (executors.Count == 0)
        {
            return SetValidation("At least one executor is required.");
        }

        _agent.DisplayName = name;
        _agent.Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
        _agent.ControllerUrl = string.IsNullOrWhiteSpace(ControllerUrlTextBox.Text) ? null : ControllerUrlTextBox.Text.Trim();
        _agent.MaximumConcurrentRuns = maximumRuns;
        _agent.RequiresApproval = RequiresApprovalCheckBox.IsChecked == true;
        _agent.AllowedExecutors = executors;
        if (StateComboBox.SelectedItem is AgentDefinitionState state)
        {
            _agent.State = state;
        }

        ValidationTextBlock.Visibility = Visibility.Collapsed;
        ValidationTextBlock.Text = string.Empty;
        return true;
    }

    private bool SetValidation(string message)
    {
        ValidationTextBlock.Text = message;
        ValidationTextBlock.Visibility = Visibility.Visible;
        return false;
    }

    private static AgentDefinition Clone(AgentDefinition source)
    {
        return new AgentDefinition
        {
            AgentId = source.AgentId,
            DisplayName = source.DisplayName,
            Description = source.Description,
            State = source.State,
            ControllerId = source.ControllerId,
            ControllerUrl = source.ControllerUrl,
            ProtocolVersion = source.ProtocolVersion,
            CreatedAtUtc = source.CreatedAtUtc,
            EnrolledAtUtc = source.EnrolledAtUtc,
            RevokedAtUtc = source.RevokedAtUtc,
            PendingAgentId = source.PendingAgentId,
            EnrolmentTokenHash = source.EnrolmentTokenHash,
            EnrolmentTokenExpiresAtUtc = source.EnrolmentTokenExpiresAtUtc,
            RequiresApproval = source.RequiresApproval,
            KeyId = source.KeyId,
            PublicKeyJwkJson = source.PublicKeyJwkJson,
            LastSeenSummary = source.LastSeenSummary,
            LastSeenAtUtc = source.LastSeenAtUtc,
            SessionId = source.SessionId,
            Hostname = source.Hostname,
            OperatingSystem = source.OperatingSystem,
            Architecture = source.Architecture,
            ImplementationName = source.ImplementationName,
            ImplementationVersion = source.ImplementationVersion,
            ActiveRuns = source.ActiveRuns,
            CpuPercent = source.CpuPercent,
            MemoryUsedBytes = source.MemoryUsedBytes,
            MemoryTotalBytes = source.MemoryTotalBytes,
            RuntimeDiskFreeBytes = source.RuntimeDiskFreeBytes,
            IsDraining = source.IsDraining,
            MaximumConcurrentRuns = source.MaximumConcurrentRuns,
            AllowedExecutors = source.AllowedExecutors.ToList(),
            Labels = new Dictionary<string, string>(source.Labels, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<AgentDefinitionState> GetEditableStates()
    {
        return
        [
            AgentDefinitionState.Pending,
            AgentDefinitionState.Enrolling,
            AgentDefinitionState.Disabled,
            AgentDefinitionState.Revoked,
            AgentDefinitionState.Incompatible,
            AgentDefinitionState.Offline
        ];
    }
}
