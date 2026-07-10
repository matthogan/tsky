using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace tsky;

public partial class RitualEditorWindow : Window
{
    private readonly ObservableCollection<RitualStepEditRow> _rows = [];

    public RitualEditorWindow(RitualDefinition ritual)
    {
        InitializeComponent();

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;

        NameTextBox.Text = ritual.Name;
        DescriptionTextBox.Text = ritual.Description ?? string.Empty;

        foreach (var step in ritual.Steps)
        {
            _rows.Add(new RitualStepEditRow
            {
                TaskName = step.TaskName,
                Group = step.Group,
                DelayMsText = step.DelayMs.ToString(),
                ContinueOnError = step.ContinueOnError
            });
        }

        StepsDataGrid.ItemsSource = _rows;
    }

    public RitualDefinition? UpdatedRitual { get; private set; }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            MessageBox.Show("Ritual name cannot be empty.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var steps = new List<RitualStepDefinition>();

        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            if (!int.TryParse(row.DelayMsText, out var delayMs) || delayMs < 0)
            {
                MessageBox.Show($"Step {index + 1} has invalid Delay (ms). Enter a non-negative integer.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            steps.Add(new RitualStepDefinition
            {
                TaskName = row.TaskName,
                Group = row.Group,
                DelayMs = delayMs,
                ContinueOnError = row.ContinueOnError
            });
        }

        UpdatedRitual = new RitualDefinition
        {
            Name = NameTextBox.Text.Trim(),
            Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim(),
            Steps = steps
        };

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private sealed class RitualStepEditRow : INotifyPropertyChanged
    {
        private string _delayMsText = "0";
        private bool _continueOnError;

        public required string TaskName { get; init; }

        public string? Group { get; init; }

        public string DelayMsText
        {
            get => _delayMsText;
            set
            {
                _delayMsText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DelayMsText)));
            }
        }

        public bool ContinueOnError
        {
            get => _continueOnError;
            set
            {
                _continueOnError = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContinueOnError)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
