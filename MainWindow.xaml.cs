using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace tsky;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double DefaultWindowWidth = 700;
    private const double DefaultWindowHeight = 420;
    private const double DefaultOutputHeight = 220;
    private const double MinOutputHeight = 120;
    private const string TaskEditorTitle = "Task Editor";
    private const string TaskUnavailableMessage = "The selected task is no longer available.";
    private const int MaxRunHistoryEntries = 300;
    private const bool EnableLayoutDebugLogging = true;

    private readonly string _configPath;
    private TaskConfiguration _currentConfiguration = new();
    private UiState _uiState = new();
    private Point _dragStartPoint;
    private bool _isDraggingTaskCard;
    private Border? _activeDropCueBorder;
    private string? _selectedGroupName;
    private readonly List<int> _visibleTaskIndexes = [];
    private readonly Dictionary<string, TabControl> _groupOutputTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupExpandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskRunState> _taskRunStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupHistoryVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupOutputVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupSidebarVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _groupOutputHeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Grid> _taskCardsByIndex = [];
    private readonly Dictionary<string, TaskRunningOverlayVisual> _taskRunningOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConnectorVisual> _connectorVisuals = [];
    private readonly Dictionary<System.Windows.Shapes.Path, ConnectorVisual> _connectorByPath = [];
    private ConnectorVisual? _pinnedConnector;
    private List<TaskRunHistoryEntry> _runHistory = [];
    private bool _isReplayingHistoryBatch;
    private bool _isRunningRitual;
    private readonly DispatcherTimer _sidebarStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _sidebarSpinnerFrameIndex;
    private static readonly string[] RunningIndicatorFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] SidebarRunningFrames = ["◐", "◓", "◑", "◒"];

    public static readonly RoutedUICommand ToggleHistoryCommand = new(
        "Toggle History",
        nameof(ToggleHistoryCommand),
        typeof(MainWindow),
        [new KeyGesture(Key.H, ModifierKeys.Control)]);

    public static readonly RoutedUICommand ToggleOutputCommand = new(
        "Toggle Task Output",
        nameof(ToggleOutputCommand),
        typeof(MainWindow),
        [new KeyGesture(Key.O, ModifierKeys.Control)]);

    public static readonly RoutedUICommand NextGroupCommand = new(
        "Next Group",
        nameof(NextGroupCommand),
        typeof(MainWindow),
        [new KeyGesture(Key.PageDown, ModifierKeys.Control)]);

    public static readonly RoutedUICommand PreviousGroupCommand = new(
        "Previous Group",
        nameof(PreviousGroupCommand),
        typeof(MainWindow),
        [new KeyGesture(Key.PageUp, ModifierKeys.Control)]);

    private enum TaskRunState
    {
        NeverRun,
        Running,
        Succeeded,
        Failed
    }

    private sealed class TaskTabContext
    {
        public required TabControl HostTabControl { get; init; }

        public required TabItem TabItem { get; init; }

        public required TextBox OutputTextBox { get; init; }

        public required TextBlock StatusText { get; init; }

        public required DispatcherTimer SpinnerTimer { get; init; }

        public int SpinnerFrameIndex { get; set; }
    }

    private sealed class TaskCardVisual
    {
        public required int DisplayIndex { get; init; }

        public required int TaskIndex { get; init; }

        public required Border DropCueBorder { get; init; }
    }

    private sealed class TaskExecutionResult
    {
        public bool Succeeded { get; init; }

        public int? ExitCode { get; init; }

        public Dictionary<string, string> PublishedOutputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TaskRunningOverlayVisual
    {
        public required Border OverlayBorder { get; init; }

        public required Storyboard PulseStoryboard { get; init; }
    }

    private sealed class ConnectorVisual
    {
        public required System.Windows.Shapes.Path Path { get; init; }

        public string? SourceTaskName { get; init; }

        public string? SourceOutputName { get; init; }

        public required string TargetTaskName { get; init; }

        public required string TargetInputName { get; init; }

        public required string BindingReference { get; init; }

        public required bool Resolved { get; init; }

        public required string StatusDetail { get; init; }
    }

    private sealed class GroupSidebarItem
    {
        public required string Name { get; init; }

        public required List<TaskSidebarPreview> Tasks { get; init; }

        public int TaskCount => Tasks.Count;

        public bool IsExpanded { get; set; }
    }

    private sealed class TaskSidebarPreview
    {
        public required string Name { get; init; }

        public required TaskRunState State { get; init; }
    }

    private sealed class HistoryListItem
    {
        public required TaskRunHistoryEntry Entry { get; init; }

        public string DisplayText
        {
            get
            {
                var localTime = Entry.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");
                var statusText = Entry.Outcome switch
                {
                    TaskRunOutcome.Succeeded => "OK",
                    TaskRunOutcome.Failed => "ERR",
                    _ => "RUN"
                };

                var replayMarker = Entry.IsReplay ? "[Replay] " : string.Empty;
                return $"{localTime}  {statusText}  {replayMarker}{Entry.Snapshot.Name}";
            }
        }

        public override string ToString() => DisplayText;
    }

    private static readonly Color[] PastelPalette =
    [
        Color.FromRgb(0xF8, 0xD7, 0xDA),
        Color.FromRgb(0xD1, 0xEC, 0xF1),
        Color.FromRgb(0xD4, 0xED, 0xDA),
        Color.FromRgb(0xFF, 0xEE, 0xCC),
        Color.FromRgb(0xE8, 0xD8, 0xF7),
        Color.FromRgb(0xFF, 0xD9, 0xC8),
        Color.FromRgb(0xD8, 0xEC, 0xFF),
        Color.FromRgb(0xE1, 0xF5, 0xD0)
    ];

    public MainWindow()
    {
        InitializeComponent();

        _configPath = ResolveConfigPath();
        _sidebarStatusTimer.Tick += SidebarStatusTimer_Tick;
        CommandBindings.Add(new CommandBinding(ToggleHistoryCommand, ToggleHistoryCommand_Executed, ToggleViewCommand_CanExecute));
        CommandBindings.Add(new CommandBinding(ToggleOutputCommand, ToggleOutputCommand_Executed, ToggleViewCommand_CanExecute));
        CommandBindings.Add(new CommandBinding(NextGroupCommand, NextGroupCommand_Executed, NavigateGroupCommand_CanExecute));
        CommandBindings.Add(new CommandBinding(PreviousGroupCommand, PreviousGroupCommand_Executed, NavigateGroupCommand_CanExecute));
        InputBindings.Add(new KeyBinding(ToggleHistoryCommand, new KeyGesture(Key.H, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ToggleOutputCommand, new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(NextGroupCommand, new KeyGesture(Key.PageDown, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(PreviousGroupCommand, new KeyGesture(Key.PageUp, ModifierKeys.Control)));
        SourceInitialized += MainWindow_SourceInitialized;
        App.ThemeChanged += OnThemeChanged;
        Closing += MainWindow_Closing;
        _runHistory = TaskRunHistoryStore.Load();
        TaskScrollViewer.ScrollChanged += TaskScrollViewer_ScrollChanged;
        TaskScrollViewer.SizeChanged += (_, _) =>
        {
            SyncTaskSurfaceToViewport();
            UpdateMainScrollBarVisibility("sizeChanged");
            ScheduleConnectorRefresh();
        };
        TaskWorkspaceSurface.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        TaskPanel.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        SizeChanged += (_, _) => ScheduleConnectorRefresh();

        _uiState = UiStateStore.Load();
        ApplyPersistedUiState();

        TaskConfiguration.EnsureValidFileOrReset(_configPath);

        LoadTaskButtons();
        SyncTaskSurfaceToViewport();
    }

    private void TaskScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
        {
            SyncTaskSurfaceToViewport();
        }

        UpdateMainScrollBarVisibility("scrollChanged");
        ScheduleConnectorRefresh();
    }

    private void SyncTaskSurfaceToViewport()
    {
        var scrollHostWidth = TaskScrollViewer.ActualWidth;
        if (double.IsNaN(scrollHostWidth) || double.IsInfinity(scrollHostWidth) || scrollHostWidth <= 0)
        {
            TaskWorkspaceSurface.Width = double.NaN;
            TaskPanel.Width = double.NaN;
            TaskPanel.MaxWidth = double.PositiveInfinity;
            return;
        }

        var stableWidth = Math.Max(0, scrollHostWidth);

        TaskWorkspaceSurface.Width = stableWidth;
        TaskPanel.Width = double.NaN;
        TaskPanel.MaxWidth = stableWidth;
    }

    private void UpdateMainScrollBarVisibility(string reason)
    {
        if (TaskScrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Hidden)
        {
            TaskScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }

        if (TaskScrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Hidden)
        {
            TaskScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }

        if (EnableLayoutDebugLogging)
        {
            Debug.WriteLine($"[MainViewLayout:{reason}] viewportW={TaskScrollViewer.ViewportWidth:F1} viewportH={TaskScrollViewer.ViewportHeight:F1} extentW={TaskScrollViewer.ExtentWidth:F1} extentH={TaskScrollViewer.ExtentHeight:F1} panelW={TaskPanel.ActualWidth:F1} panelH={TaskPanel.ActualHeight:F1} scroll=Hidden");
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyTitleBarTheme(App.IsDarkThemeEnabled());
    }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() =>
        {
            ApplyTitleBarTheme(useDarkTheme);
            ApplyConnectorThemeRefresh();
        });
    }

    private void ApplyConnectorThemeRefresh()
    {
        if (_pinnedConnector is not null)
        {
            ApplyConnectorHighlight(_pinnedConnector);
            return;
        }

        ApplyConnectorDefaultStyles();
    }

    private static Brush ResolveThemeBrush(string key, Color fallbackColor)
    {
        if (Application.Current?.Resources[key] is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    private static Brush GetResolvedConnectorBrush()
    {
        return ResolveThemeBrush("ConnectorResolvedBrush", Color.FromRgb(0x2E, 0x7D, 0x7A));
    }

    private static Brush GetUnresolvedConnectorBrush()
    {
        return ResolveThemeBrush("ConnectorUnresolvedBrush", Color.FromRgb(0xD3, 0x2F, 0x2F));
    }

    private static Brush GetFocusedConnectorBrush()
    {
        return ResolveThemeBrush("ConnectorFocusedBrush", Color.FromRgb(0xF9, 0xA8, 0x25));
    }

    private void ApplyTitleBarTheme(bool useDarkTheme)
    {
        WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        _sidebarStatusTimer.Stop();
        PersistUiState();
    }

    private void SidebarStatusTimer_Tick(object? sender, EventArgs e)
    {
        _sidebarSpinnerFrameIndex++;
        RebuildGroupNavigator(GetGroupSidebarItems(GetGroupNames()));
    }

    private void ApplyPersistedUiState()
    {
        if (!double.IsNaN(_uiState.WindowWidth) && _uiState.WindowWidth >= MinWidth)
        {
            Width = _uiState.WindowWidth;
        }
        else
        {
            Width = DefaultWindowWidth;
        }

        if (!double.IsNaN(_uiState.WindowHeight) && _uiState.WindowHeight >= MinHeight)
        {
            Height = _uiState.WindowHeight;
        }
        else
        {
            Height = DefaultWindowHeight;
        }

        if (!double.IsNaN(_uiState.WindowLeft) && !double.IsNaN(_uiState.WindowTop) && IsPointOnAnyScreen(_uiState.WindowLeft, _uiState.WindowTop))
        {
            Left = _uiState.WindowLeft;
            Top = _uiState.WindowTop;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        var persistedHeight = _uiState.OutputHeight;
        if (double.IsNaN(persistedHeight) || persistedHeight < MinOutputHeight)
        {
            persistedHeight = DefaultOutputHeight;
        }

        OutputRowDefinition.Height = new GridLength(persistedHeight);

        if (_uiState.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void PersistUiState()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        _uiState.WindowWidth = Math.Max(MinWidth, bounds.Width);
        _uiState.WindowHeight = Math.Max(MinHeight, bounds.Height);
        _uiState.WindowLeft = bounds.Left;
        _uiState.WindowTop = bounds.Top;
        _uiState.IsMaximized = WindowState == WindowState.Maximized;

        var height = OutputRowDefinition.ActualHeight;
        if (height <= 0)
        {
            height = OutputRowDefinition.Height.Value;
        }

        _uiState.OutputHeight = Math.Max(MinOutputHeight, height);
        UiStateStore.Save(_uiState);
    }

    private static bool IsPointOnAnyScreen(double x, double y)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return x >= screenLeft && x <= screenRight && y >= screenTop && y <= screenBottom;
    }

    private static string ResolveConfigPath()
    {
        var currentDirectoryPath = System.IO.Path.Combine(Environment.CurrentDirectory, "tasks.json");
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        var appDirectoryPath = System.IO.Path.Combine(AppContext.BaseDirectory, "tasks.json");
        if (File.Exists(appDirectoryPath))
        {
            return appDirectoryPath;
        }

        return currentDirectoryPath;
    }

    private void LoadTaskButtons()
    {
        _currentConfiguration = TaskConfiguration.Load(_configPath);

        TaskPanel.AllowDrop = true;
        TaskPanel.DragOver -= TaskPanel_DragOver;
        TaskPanel.Drop -= TaskPanel_Drop;
        TaskPanel.DragLeave -= TaskPanel_DragLeave;
        TaskPanel.DragOver += TaskPanel_DragOver;
        TaskPanel.Drop += TaskPanel_Drop;
        TaskPanel.DragLeave += TaskPanel_DragLeave;

        var groupNames = GetGroupNames();
        if (string.IsNullOrWhiteSpace(_selectedGroupName) || !groupNames.Contains(_selectedGroupName, StringComparer.OrdinalIgnoreCase))
        {
            _selectedGroupName = groupNames.FirstOrDefault();
        }

        RebuildGroupsMenu(groupNames);
        RebuildRitualsMenu();
        RebuildGroupNavigator(GetGroupSidebarItems(groupNames));
        RenderSelectedGroupTasks();
        ShowSelectedGroupOutputTabControl();
        RefreshHistoryView();
        ApplyGroupViewState();
    }

    private void RebuildGroupNavigator(IReadOnlyList<GroupSidebarItem> groups)
    {
        GroupListPanel.Children.Clear();

        foreach (var group in groups)
        {
            var groupName = group.Name;

            var headerButton = new Button
            {
                Style = (Style)Resources["SidebarGroupHeaderButtonStyle"],
                Tag = groupName,
                Background = string.Equals(groupName, _selectedGroupName, StringComparison.OrdinalIgnoreCase)
                    ? (Brush)Resources["TabHeaderHoverBackgroundBrush"]
                    : (Brush)Resources["PanelBackgroundBrush"]
            };

            headerButton.Click += GroupHeaderButton_Click;

            var headerLayout = new DockPanel();

            var badge = new Border
            {
                Style = (Style)Resources["GroupCountBadgeStyle"]
            };
            DockPanel.SetDock(badge, Dock.Right);
            badge.Child = new TextBlock
            {
                Text = group.TaskCount.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var groupNameText = new TextBlock
            {
                Text = groupName,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            };

            headerLayout.Children.Add(badge);
            headerLayout.Children.Add(groupNameText);
            headerButton.Content = headerLayout;

            var previewPanel = new StackPanel();
            foreach (var preview in group.Tasks)
            {
                var row = new DockPanel
                {
                    Margin = new Thickness(2, 2, 2, 0)
                };

                var stateText = new TextBlock
                {
                    Text = GetStateIcon(preview.State),
                    Width = 18,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = GetStateBrush(preview.State)
                };
                DockPanel.SetDock(stateText, Dock.Left);

                var taskText = new TextBlock
                {
                    Text = preview.Name,
                    Style = (Style)Resources["GroupPreviewTextStyle"],
                    Margin = new Thickness(4, 0, 0, 0)
                };

                row.Children.Add(stateText);
                row.Children.Add(taskText);
                previewPanel.Children.Add(row);
            }

            var expander = new Expander
            {
                Style = (Style)Resources["SidebarGroupExpanderStyle"],
                Header = headerButton,
                Content = previewPanel,
                IsExpanded = group.IsExpanded,
                Tag = groupName
            };

            expander.Expanded += (_, _) => _groupExpandedStates[groupName] = true;
            expander.Collapsed += (_, _) => _groupExpandedStates[groupName] = false;
            expander.PreviewMouseLeftButtonUp += GroupExpander_PreviewMouseLeftButtonUp;
            GroupListPanel.Children.Add(expander);
        }
    }

    private void GroupExpander_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Expander { Tag: string groupName })
        {
            return;
        }

        if (string.Equals(_selectedGroupName, groupName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectGroup(groupName);
    }

    private void GroupHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string selectedGroup })
        {
            return;
        }

        SelectGroup(selectedGroup);
    }

    private void GroupsMenuEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string groupName })
        {
            return;
        }

        SelectGroup(groupName);
    }

    private void RebuildGroupsMenu(IReadOnlyList<string> groupNames)
    {
        GroupsMenuItem.Items.Clear();

        var sortedGroupNames = groupNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sortedGroupNames.Count == 0)
        {
            GroupsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No groups)",
                IsEnabled = false,
                Style = (Style)Resources["AppMenuItemStyle"]
            });

            GroupsMenuItem.IsEnabled = false;
            return;
        }

        GroupsMenuItem.IsEnabled = true;

        foreach (var groupName in sortedGroupNames)
        {
            var count = _currentConfiguration.Tasks.Count(task => string.Equals(NormalizeGroupName(task.Group), groupName, StringComparison.OrdinalIgnoreCase));

            var item = new MenuItem
            {
                Header = $"{groupName} ({count})",
                IsCheckable = true,
                IsChecked = string.Equals(groupName, _selectedGroupName, StringComparison.OrdinalIgnoreCase),
                Tag = groupName,
                Style = (Style)Resources["AppMenuItemStyle"]
            };

            item.Click += GroupsMenuEntry_Click;
            GroupsMenuItem.Items.Add(item);
        }

        GroupsMenuItem.Items.Add(new Separator());
        GroupsMenuItem.Items.Add(new MenuItem
        {
            Header = "Previous Group",
            InputGestureText = "Ctrl+PgUp",
            Command = PreviousGroupCommand,
            Style = (Style)Resources["AppMenuItemStyle"]
        });
        GroupsMenuItem.Items.Add(new MenuItem
        {
            Header = "Next Group",
            InputGestureText = "Ctrl+PgDn",
            Command = NextGroupCommand,
            Style = (Style)Resources["AppMenuItemStyle"]
        });
    }

    private void RebuildRitualsMenu()
    {
        RitualsMenuItem.Items.Clear();

        var rituals = _currentConfiguration.Rituals
            .OrderBy(ritual => ritual.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rituals.Count == 0)
        {
            RitualsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No rituals)",
                IsEnabled = false,
                Style = (Style)Resources["AppMenuItemStyle"]
            });
        }

        foreach (var ritual in rituals)
        {
            var parentItem = new MenuItem
            {
                Header = ritual.Name,
                ToolTip = ritual.Description,
                Tag = ritual,
                Style = (Style)Resources["AppMenuItemStyle"]
            };

            var runItem = new MenuItem
            {
                Header = "Run",
                Tag = ritual,
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            runItem.Click += RitualMenuEntry_Click;

            var editItem = new MenuItem
            {
                Header = "Edit...",
                Tag = ritual,
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            editItem.Click += EditRitualMenuEntry_Click;

            parentItem.Items.Add(runItem);
            parentItem.Items.Add(editItem);

            RitualsMenuItem.Items.Add(parentItem);
        }

        RitualsMenuItem.Items.Add(new Separator());
        RitualsMenuItem.Items.Add(new MenuItem
        {
            Header = "Define Ritual From Current Group",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = HasSelectedGroupTasks(),
            Command = null
        });

        if (RitualsMenuItem.Items[^1] is MenuItem defineItem)
        {
            defineItem.Click += DefineRitualFromCurrentGroupMenuItem_Click;
        }

        RitualsMenuItem.IsEnabled = true;
    }

    private bool HasSelectedGroupTasks()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return false;
        }

        return _currentConfiguration.Tasks.Any(task =>
            string.Equals(NormalizeGroupName(task.Group), _selectedGroupName, StringComparison.OrdinalIgnoreCase));
    }

    private void DefineRitualFromCurrentGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            MessageBox.Show("Select a group first.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = NormalizeGroupName(_selectedGroupName);

        var orderedGroupTasks = _visibleTaskIndexes.Count > 0
            ? _visibleTaskIndexes.Select(index => _currentConfiguration.Tasks[index]).ToList()
            : _currentConfiguration.Tasks
                .Where(task => string.Equals(NormalizeGroupName(task.Group), groupName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (orderedGroupTasks.Count == 0)
        {
            MessageBox.Show($"No tasks are available in group '{groupName}'.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ritualName = $"{groupName} Ritual";
        var ritual = new RitualDefinition
        {
            Name = ritualName,
            Description = $"Auto-generated from group '{groupName}'.",
            Steps = orderedGroupTasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Name))
                .Select(task => new RitualStepDefinition
                {
                    TaskName = task.Name.Trim(),
                    Group = NormalizeGroupName(task.Group),
                    DelayMs = 0,
                    ContinueOnError = false
                })
                .ToList()
        };

        if (ritual.Steps.Count == 0)
        {
            MessageBox.Show($"Group '{groupName}' has no named tasks to include in a ritual.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existingIndex = _currentConfiguration.Rituals.FindIndex(existing =>
            string.Equals(existing.Name, ritualName, StringComparison.OrdinalIgnoreCase));

        var wasUpdate = existingIndex >= 0;
        if (wasUpdate)
        {
            _currentConfiguration.Rituals[existingIndex] = ritual;
        }
        else
        {
            _currentConfiguration.Rituals.Add(ritual);
        }

        try
        {
            TaskConfiguration.Save(_configPath, _currentConfiguration);
            LoadTaskButtons();

            var actionText = wasUpdate ? "updated" : "created";
            MessageBox.Show($"Ritual '{ritualName}' {actionText} from group '{groupName}'.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save ritual.\n{ex.Message}", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RitualMenuEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RitualDefinition ritual })
        {
            return;
        }

        await ExecuteRitualAsync(ritual);
    }

    private void EditRitualMenuEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RitualDefinition ritual })
        {
            return;
        }

        var editor = new RitualEditorWindow(ritual) { Owner = this };
        if (editor.ShowDialog() != true || editor.UpdatedRitual is null)
        {
            return;
        }

        var updated = editor.UpdatedRitual;
        var index = _currentConfiguration.Rituals.FindIndex(existing =>
            string.Equals(existing.Name, ritual.Name, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            MessageBox.Show("Ritual no longer exists. Reload and try again.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(updated.Name, ritual.Name, StringComparison.OrdinalIgnoreCase) &&
            _currentConfiguration.Rituals.Any(existing => string.Equals(existing.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"A ritual named '{updated.Name}' already exists.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentConfiguration.Rituals[index] = updated;

        try
        {
            TaskConfiguration.Save(_configPath, _currentConfiguration);
            LoadTaskButtons();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save ritual edits.\n{ex.Message}", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExecuteRitualAsync(RitualDefinition ritual)
    {
        if (_isRunningRitual)
        {
            MessageBox.Show("A ritual is already running.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ritual.Steps.Count == 0)
        {
            MessageBox.Show($"Ritual '{ritual.Name}' has no steps.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isRunningRitual = true;
        var tabContext = CreateOutputTab($"Ritual: {ritual.Name}");
        SetTabRunning(tabContext);

        try
        {
            AppendLog(tabContext.OutputTextBox, $"[Ritual] Starting '{ritual.Name}' with {ritual.Steps.Count} steps.");

            for (var stepIndex = 0; stepIndex < ritual.Steps.Count; stepIndex++)
            {
                var step = ritual.Steps[stepIndex];
                var label = $"{stepIndex + 1}/{ritual.Steps.Count}";

                if (step.DelayMs > 0)
                {
                    AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: waiting {step.DelayMs}ms before '{step.TaskName}'.");
                    await Task.Delay(step.DelayMs);
                }

                var stepTask = ResolveRitualStepTask(step);
                if (stepTask is null)
                {
                    var missingText = string.IsNullOrWhiteSpace(step.Group)
                        ? $"Task '{step.TaskName}' was not found."
                        : $"Task '{step.TaskName}' in group '{step.Group}' was not found.";

                    AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: {missingText}");

                    if (!step.ContinueOnError)
                    {
                        AppendLog(tabContext.OutputTextBox, "[Ritual] Stopped due to missing task and continueOnError=false.");
                        SetTabStopped(tabContext, false);
                        return;
                    }

                    continue;
                }

                var effectiveTask = CreateRitualStepExecutionTask(stepTask, step);
                AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: running '{effectiveTask.Name}' (waitForExit={effectiveTask.WaitForExit}).");
                SelectGroup(NormalizeGroupName(effectiveTask.Group));
                var result = await ExecuteTaskAsync(effectiveTask, null, isReplay: false, chainStepLabel: $"ritual {label}");

                if (!result.Succeeded)
                {
                    AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: task '{effectiveTask.Name}' failed.");

                    if (!step.ContinueOnError)
                    {
                        AppendLog(tabContext.OutputTextBox, "[Ritual] Stopped due to continueOnError=false.");
                        SetTabStopped(tabContext, false);
                        return;
                    }
                }
            }

            AppendLog(tabContext.OutputTextBox, $"[Ritual] '{ritual.Name}' completed.");
            SetTabStopped(tabContext, true);
        }
        finally
        {
            _isRunningRitual = false;
        }
    }

    private LauncherTask? ResolveRitualStepTask(RitualStepDefinition step)
    {
        var matches = _currentConfiguration.Tasks
            .Where(task => string.Equals(task.Name.Trim(), step.TaskName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(step.Group))
        {
            matches = matches
                .Where(task => string.Equals(NormalizeGroupName(task.Group), NormalizeGroupName(step.Group), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return matches.FirstOrDefault();
    }

    private static LauncherTask CreateRitualStepExecutionTask(LauncherTask sourceTask, RitualStepDefinition step)
    {
        return new LauncherTask
        {
            Name = sourceTask.Name,
            Description = sourceTask.Description,
            Group = sourceTask.Group,
            CommandLine = sourceTask.CommandLine,
            Runner = sourceTask.Runner,
            Env = new Dictionary<string, string>(sourceTask.Env, StringComparer.OrdinalIgnoreCase),
            WorkingDirectory = sourceTask.WorkingDirectory,
            Inputs = new Dictionary<string, string>(sourceTask.Inputs, StringComparer.OrdinalIgnoreCase),
            InputBindings = new Dictionary<string, string>(sourceTask.InputBindings, StringComparer.OrdinalIgnoreCase),
            Outputs = new Dictionary<string, string>(sourceTask.Outputs, StringComparer.OrdinalIgnoreCase),
            WaitForExit = step.WaitForExit ?? false
        };
    }

    private void SelectGroup(string groupName)
    {
        _selectedGroupName = groupName;
        var groupNames = GetGroupNames();
        RebuildGroupsMenu(groupNames);
        RebuildGroupNavigator(GetGroupSidebarItems(groupNames));
        RenderSelectedGroupTasks();
        ShowSelectedGroupOutputTabControl();
        RefreshHistoryView();
        ApplyGroupViewState();
    }

    private void NextGroupCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        NavigateGroup(step: 1);
    }

    private void PreviousGroupCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        NavigateGroup(step: -1);
    }

    private void NavigateGroupCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = GetSortedGroupNames().Count > 0;
    }

    private void NavigateGroup(int step)
    {
        var sortedGroups = GetSortedGroupNames();
        if (sortedGroups.Count == 0)
        {
            return;
        }

        var currentIndex = sortedGroups.FindIndex(name => string.Equals(name, _selectedGroupName, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            SelectGroup(sortedGroups[0]);
            return;
        }

        var targetIndex = (currentIndex + step + sortedGroups.Count) % sortedGroups.Count;
        SelectGroup(sortedGroups[targetIndex]);
    }

    private List<string> GetSortedGroupNames()
    {
        return GetGroupNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ViewGroupsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var groupKey = NormalizeGroupName(_selectedGroupName);
        _groupSidebarVisibility[groupKey] = ViewGroupsMenuItem.IsChecked;
        ApplyGroupViewState();
    }

    private void ViewHistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var groupKey = NormalizeGroupName(_selectedGroupName);
        _groupHistoryVisibility[groupKey] = ViewHistoryMenuItem.IsChecked;
        ApplyGroupViewState();
    }

    private void ViewOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var groupKey = NormalizeGroupName(_selectedGroupName);
        _groupOutputVisibility[groupKey] = ViewOutputMenuItem.IsChecked;
        ApplyGroupViewState();
    }

    private void ToggleHistoryCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ViewHistoryMenuItem.IsEnabled)
        {
            return;
        }

        ViewHistoryMenuItem.IsChecked = !ViewHistoryMenuItem.IsChecked;
        ViewHistoryMenuItem_Click(ViewHistoryMenuItem, new RoutedEventArgs());
    }

    private void ToggleOutputCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ViewOutputMenuItem.IsEnabled)
        {
            return;
        }

        ViewOutputMenuItem.IsChecked = !ViewOutputMenuItem.IsChecked;
        ViewOutputMenuItem_Click(ViewOutputMenuItem, new RoutedEventArgs());
    }

    private void ToggleViewCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = !string.IsNullOrWhiteSpace(_selectedGroupName);
    }

    private void ApplyGroupViewState()
    {
        var groupKey = NormalizeGroupName(_selectedGroupName);

        if (!_groupSidebarVisibility.TryGetValue(groupKey, out var sidebarVisible))
        {
            sidebarVisible = true;
            _groupSidebarVisibility[groupKey] = true;
        }

        if (!_groupHistoryVisibility.TryGetValue(groupKey, out var historyVisible))
        {
            historyVisible = true;
            _groupHistoryVisibility[groupKey] = true;
        }

        if (!_groupOutputVisibility.TryGetValue(groupKey, out var outputVisible))
        {
            outputVisible = true;
            _groupOutputVisibility[groupKey] = true;
        }

        ApplySidebarVisibility(sidebarVisible);
        ApplyHistoryVisibility(historyVisible);
        ApplyOutputVisibility(groupKey, outputVisible);
        UpdateViewMenuState(sidebarVisible, historyVisible, outputVisible);
    }

    private void ApplySidebarVisibility(bool isVisible)
    {
        if (isVisible)
        {
            SidebarPaneBorder.Visibility = Visibility.Visible;
            AnimateColumnWidth(SidebarColumnDefinition, 220);
            AnimateColumnWidth(SidebarSpacerColumnDefinition, 12, RecalibrateTaskScrollViewport);
            return;
        }

        SidebarPaneBorder.Visibility = Visibility.Visible;
        AnimateColumnWidth(SidebarColumnDefinition, 0, () =>
        {
            SidebarPaneBorder.Visibility = Visibility.Collapsed;
            RecalibrateTaskScrollViewport();
        });
        AnimateColumnWidth(SidebarSpacerColumnDefinition, 0);
    }

    private void ApplyHistoryVisibility(bool isVisible)
    {
        if (isVisible)
        {
            HistoryPanelBorder.Visibility = Visibility.Visible;
            AnimateColumnWidth(HistorySpacerColumnDefinition, 10);
            AnimateColumnWidth(HistoryColumnDefinition, 300, RecalibrateTaskScrollViewport);
            return;
        }

        HistoryPanelBorder.Visibility = Visibility.Visible;
        AnimateColumnWidth(HistorySpacerColumnDefinition, 0);
        AnimateColumnWidth(HistoryColumnDefinition, 0, () =>
        {
            HistoryPanelBorder.Visibility = Visibility.Collapsed;
            RecalibrateTaskScrollViewport();
        });
    }

    private void AnimateColumnWidth(ColumnDefinition columnDefinition, double targetWidth, Action? onCompleted = null)
    {
        const double epsilon = 0.2;
        var currentWidth = GetCurrentColumnWidth(columnDefinition);

        if (Math.Abs(currentWidth - targetWidth) <= epsilon)
        {
            columnDefinition.BeginAnimation(ColumnDefinition.WidthProperty, null);
            columnDefinition.Width = new GridLength(targetWidth);
            onCompleted?.Invoke();
            return;
        }

        var animation = new GridLengthAnimation
        {
            From = new GridLength(currentWidth, GridUnitType.Pixel),
            To = new GridLength(targetWidth, GridUnitType.Pixel),
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            columnDefinition.BeginAnimation(ColumnDefinition.WidthProperty, null);
            columnDefinition.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            onCompleted?.Invoke();
        };

        columnDefinition.BeginAnimation(ColumnDefinition.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double GetCurrentColumnWidth(ColumnDefinition columnDefinition)
    {
        if (columnDefinition.ActualWidth > 0)
        {
            return columnDefinition.ActualWidth;
        }

        return columnDefinition.Width.IsAbsolute ? columnDefinition.Width.Value : 0;
    }

    private void ApplyOutputVisibility(string groupKey, bool isVisible)
    {
        if (!isVisible)
        {
            var currentHeight = OutputRowDefinition.ActualHeight > 0
                ? OutputRowDefinition.ActualHeight
                : OutputRowDefinition.Height.Value;

            if (currentHeight > 0)
            {
                _groupOutputHeights[groupKey] = currentHeight;
            }

            OutputGridSplitter.Visibility = Visibility.Visible;
            OutputContainer.Visibility = Visibility.Visible;
            OutputRowDefinition.MinHeight = 0;
            AnimateRowHeight(OutputRowDefinition, 0, () =>
            {
                OutputGridSplitter.Visibility = Visibility.Collapsed;
                OutputContainer.Visibility = Visibility.Collapsed;
                RecalibrateTaskScrollViewport();
            });
            return;
        }

        var restoredHeight = _groupOutputHeights.TryGetValue(groupKey, out var storedHeight) && storedHeight >= MinOutputHeight
            ? storedHeight
            : DefaultOutputHeight;

        OutputRowDefinition.MinHeight = 0;
        OutputGridSplitter.Visibility = Visibility.Visible;
        OutputContainer.Visibility = Visibility.Visible;
        AnimateRowHeight(OutputRowDefinition, restoredHeight, () =>
        {
            OutputRowDefinition.MinHeight = MinOutputHeight;
            RecalibrateTaskScrollViewport();
        });
    }

    private void AnimateRowHeight(RowDefinition rowDefinition, double targetHeight, Action? onCompleted = null)
    {
        const double epsilon = 0.2;
        var currentHeight = GetCurrentRowHeight(rowDefinition);

        if (Math.Abs(currentHeight - targetHeight) <= epsilon)
        {
            rowDefinition.BeginAnimation(RowDefinition.HeightProperty, null);
            rowDefinition.Height = new GridLength(targetHeight);
            onCompleted?.Invoke();
            return;
        }

        var animation = new GridLengthAnimation
        {
            From = new GridLength(currentHeight, GridUnitType.Pixel),
            To = new GridLength(targetHeight, GridUnitType.Pixel),
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            rowDefinition.BeginAnimation(RowDefinition.HeightProperty, null);
            rowDefinition.Height = new GridLength(targetHeight, GridUnitType.Pixel);
            onCompleted?.Invoke();
        };

        rowDefinition.BeginAnimation(RowDefinition.HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double GetCurrentRowHeight(RowDefinition rowDefinition)
    {
        if (rowDefinition.ActualHeight > 0)
        {
            return rowDefinition.ActualHeight;
        }

        return rowDefinition.Height.IsAbsolute ? rowDefinition.Height.Value : 0;
    }

    private void RecalibrateTaskScrollViewport()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SyncTaskSurfaceToViewport();
            TaskWorkspaceSurface.InvalidateMeasure();
            TaskWorkspaceSurface.InvalidateArrange();
            TaskPanel.InvalidateMeasure();
            TaskPanel.InvalidateArrange();
            TaskScrollViewer.UpdateLayout();
            UpdateMainScrollBarVisibility("recalibrate");
            ScheduleConnectorRefresh();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateViewMenuState(bool sidebarVisible, bool historyVisible, bool outputVisible)
    {
        ViewGroupsMenuItem.IsChecked = sidebarVisible;
        ViewHistoryMenuItem.IsChecked = historyVisible;
        ViewOutputMenuItem.IsChecked = outputVisible;
        ViewGroupsMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(_selectedGroupName);
        ViewHistoryMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(_selectedGroupName);
        ViewOutputMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(_selectedGroupName);
    }

    private void RefreshHistoryView()
    {
        IEnumerable<TaskRunHistoryEntry> filtered = _runHistory;

        if (!string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            filtered = filtered.Where(entry => string.Equals(entry.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase));
        }

        var items = filtered
            .OrderByDescending(entry => entry.StartedAtUtc)
            .Take(100)
            .Select(entry => new HistoryListItem { Entry = entry })
            .ToList();

        HistoryListBox.ItemsSource = items;
        UpdateHistoryActionState();
    }

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHistoryActionState();
    }

    private void UpdateHistoryActionState()
    {
        var hasSelection = HistoryListBox.SelectedItems.Count > 0;
        ReplaySelectedButton.IsEnabled = hasSelection && !_isReplayingHistoryBatch;
        ClearHistorySelectionButton.IsEnabled = hasSelection;
    }

    private void ClearHistorySelectionButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryListBox.SelectedItems.Clear();
        UpdateHistoryActionState();
    }

    private async void ReplaySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedEntries = HistoryListBox.SelectedItems
            .OfType<HistoryListItem>()
            .Select(item => item.Entry)
            .OrderBy(entry => entry.StartedAtUtc)
            .ToList();

        if (selectedEntries.Count == 0)
        {
            return;
        }

        _isReplayingHistoryBatch = true;
        UpdateHistoryActionState();

        try
        {
            foreach (var entry in selectedEntries)
            {
                var replayTask = ToTask(entry.Snapshot);
                SelectGroup(NormalizeGroupName(replayTask.Group));

                await ExecuteTaskAsync(replayTask, null, isReplay: true, replayFromEntryId: entry.Id);
            }
        }
        finally
        {
            _isReplayingHistoryBatch = false;
            UpdateHistoryActionState();
        }
    }

    private void RenderSelectedGroupTasks()
    {
        TaskPanel.Children.Clear();
        _taskCardsByIndex.Clear();
        _taskRunningOverlays.Clear();
        TaskConnectorOverlay.Children.Clear();
        _visibleTaskIndexes.Clear();
        SyncTaskSurfaceToViewport();

        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            GroupTitleText.Text = "No Groups";
            GroupMetaText.Text = "Replay snapshots are available from each task menu.";
            EmptyStateText.Text = "No groups are configured. Add a task to create your first group.";
            EmptyStateText.Visibility = Visibility.Visible;
            OutputHeaderText.Text = "Task Output";
            return;
        }

        GroupTitleText.Text = _selectedGroupName;
        var snapshotCount = _runHistory.Count(entry => string.Equals(entry.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase));
        GroupMetaText.Text = $"Snapshots in this group: {snapshotCount}";
        OutputHeaderText.Text = $"Task Output · {_selectedGroupName}";

        for (var index = 0; index < _currentConfiguration.Tasks.Count; index++)
        {
            var task = _currentConfiguration.Tasks[index];
            if (!string.Equals(NormalizeGroupName(task.Group), _selectedGroupName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _visibleTaskIndexes.Add(index);
        }

        EmptyStateText.Text = $"No tasks in '{_selectedGroupName}'. Use Add Task to populate this group.";
        EmptyStateText.Visibility = _visibleTaskIndexes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var displayIndex = 0; displayIndex < _visibleTaskIndexes.Count; displayIndex++)
        {
            var index = _visibleTaskIndexes[displayIndex];
            var task = _currentConfiguration.Tasks[index];
            var cardColor = PastelPalette[displayIndex % PastelPalette.Length];
            var card = new Grid
            {
                Width = 130,
                Height = 130,
                Margin = new Thickness(0, 0, 14, 14)
            };
            var dropCueBorder = new Border
            {
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Opacity = 0.45,
                IsHitTestVisible = false
            };
            dropCueBorder.SetResourceReference(Border.BorderBrushProperty, "PrimaryTextBrush");

            card.Tag = new TaskCardVisual
            {
                DisplayIndex = displayIndex,
                TaskIndex = index,
                DropCueBorder = dropCueBorder
            };

            var cardTextContainer = new Grid();
            cardTextContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardTextContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var nameText = new TextBlock
            {
                Text = task.Name,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = GetReadableTextBrush(cardColor),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8),
                MaxHeight = 42
            };
            Grid.SetRow(nameText, 0);

            var descriptionText = new TextBlock
            {
                Text = task.Description ?? string.Empty,
                FontSize = 11,
                Foreground = GetReadableTextBrush(cardColor),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Visibility = string.IsNullOrWhiteSpace(task.Description) ? Visibility.Collapsed : Visibility.Visible,
                MaxHeight = 58
            };
            Grid.SetRow(descriptionText, 1);

            cardTextContainer.Children.Add(nameText);
            cardTextContainer.Children.Add(descriptionText);

            var button = new Button
            {
                Content = cardTextContainer,
                Tag = task,
                Style = (Style)Resources["TaskCardButtonStyle"],
                Background = new SolidColorBrush(cardColor),
                Foreground = GetReadableTextBrush(cardColor),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };

            var menuButton = new Button
            {
                Content = "⋮",
                Tag = index,
                Style = (Style)Resources["TaskMenuButtonStyle"],
                Background = new SolidColorBrush(cardColor),
                BorderBrush = new SolidColorBrush(cardColor),
                Foreground = GetReadableTextBrush(cardColor)
            };

            button.Click += TaskButton_Click;
            button.PreviewMouseLeftButtonDown += TaskCard_PreviewMouseLeftButtonDown;
            button.PreviewMouseMove += TaskCard_PreviewMouseMove;
            button.PreviewMouseLeftButtonUp += TaskCard_PreviewMouseLeftButtonUp;
            menuButton.Click += TaskMenuButton_Click;

            var runningOverlay = CreateRunningOverlay();
            card.Children.Add(runningOverlay.OverlayBorder);
            _taskRunningOverlays[GetTaskStateKey(task)] = runningOverlay;
            ApplyTaskCardRunningVisual(task);

            var inputPorts = GetInputPortNames(task);
            if (inputPorts.Count > 0)
            {
                var leftPorts = CreatePortStack(inputPorts, isInput: true);
                card.Children.Add(leftPorts);
            }

            var outputPorts = GetOutputPortNames(task);
            if (outputPorts.Count > 0)
            {
                var rightPorts = CreatePortStack(outputPorts, isInput: false);
                card.Children.Add(rightPorts);
            }

            card.Children.Add(button);
            card.Children.Add(dropCueBorder);
            card.Children.Add(menuButton);
            TaskPanel.Children.Add(card);
            _taskCardsByIndex[index] = card;
        }

        ScheduleConnectorRefresh();
    }

    private static List<string> GetInputPortNames(LauncherTask task)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in task.Inputs.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var name = key.Trim();
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        foreach (var key in task.InputBindings.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var name = key.Trim();
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<string> GetOutputPortNames(LauncherTask task)
    {
        return task.Outputs.Keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static StackPanel CreatePortStack(IReadOnlyList<string> portNames, bool isInput)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = isInput ? new Thickness(4, 0, 0, 0) : new Thickness(0, 0, 4, 0),
            IsHitTestVisible = false
        };

        foreach (var portName in portNames)
        {
            var dot = new Ellipse
            {
                ToolTip = portName
            };

            dot.SetResourceReference(FrameworkElement.MarginProperty, "PortDotMargin");
            dot.SetResourceReference(FrameworkElement.WidthProperty, "PortDotSize");
            dot.SetResourceReference(FrameworkElement.HeightProperty, "PortDotSize");
            dot.SetResourceReference(Shape.StrokeThicknessProperty, "PortDotStrokeThickness");
            dot.SetResourceReference(Shape.FillProperty, isInput ? "InputPortDotBrush" : "OutputPortDotBrush");
            dot.SetResourceReference(Shape.StrokeProperty, "PanelBackgroundBrush");

            stack.Children.Add(dot);
        }

        return stack;
    }

    private void ScheduleConnectorRefresh()
    {
        Dispatcher.BeginInvoke(RedrawDataflowConnectors, DispatcherPriority.Loaded);
    }

    private void RedrawDataflowConnectors()
    {
        TaskConnectorOverlay.Children.Clear();
        _connectorVisuals.Clear();
        _connectorByPath.Clear();
        _pinnedConnector = null;

        if (string.IsNullOrWhiteSpace(_selectedGroupName) || _visibleTaskIndexes.Count == 0)
        {
            TaskConnectorOverlay.Width = 0;
            TaskConnectorOverlay.Height = 0;
            return;
        }

        if (TaskPanel.ActualWidth <= 0 || TaskPanel.ActualHeight <= 0)
        {
            TaskConnectorOverlay.Width = 0;
            TaskConnectorOverlay.Height = 0;
            return;
        }

        TaskConnectorOverlay.Width = TaskPanel.ActualWidth;
        TaskConnectorOverlay.Height = TaskPanel.ActualHeight;

        var selectedGroupTasks = _visibleTaskIndexes
            .Select(index => new { Index = index, Task = _currentConfiguration.Tasks[index] })
            .ToList();

        var taskByName = selectedGroupTasks
            .Where(item => !string.IsNullOrWhiteSpace(item.Task.Name))
            .GroupBy(item => item.Task.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var target in selectedGroupTasks)
        {
            if (!_taskCardsByIndex.TryGetValue(target.Index, out var targetCard))
            {
                continue;
            }

            var bindingEntries = target.Task.InputBindings
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => new
                {
                    InputName = pair.Key.Trim(),
                    Reference = pair.Value?.Trim() ?? string.Empty
                })
                .ToList();

            var targetInputNames = GetInputPortNames(target.Task);

            for (var bindingIndex = 0; bindingIndex < bindingEntries.Count; bindingIndex++)
            {
                var binding = bindingEntries[bindingIndex];
                var targetPoint = GetCardPortPoint(targetCard, isOutput: false, targetInputNames, binding.InputName);

                if (!TryParseBindingTaskOutputReference(binding.Reference, out var sourceTaskName, out var sourceOutputName) ||
                    !taskByName.TryGetValue(sourceTaskName, out var sourceTaskInfo) ||
                    !_taskCardsByIndex.TryGetValue(sourceTaskInfo.Index, out var sourceCard))
                {
                    var fallbackStart = new Point(Math.Max(0, targetPoint.X - 56), targetPoint.Y);
                    var status = string.IsNullOrWhiteSpace(binding.Reference)
                        ? "Binding reference is empty."
                        : "Binding could not be matched to a task output in this group.";

                    AddConnector(
                        fallbackStart,
                        targetPoint,
                        resolved: false,
                        sourceTaskName: null,
                        sourceOutputName: null,
                        targetTaskName: target.Task.Name,
                        targetInputName: binding.InputName,
                        bindingReference: binding.Reference,
                        statusDetail: status);
                    continue;
                }

                var sourceOutputNames = GetOutputPortNames(sourceTaskInfo.Task);
                var hasOutput = sourceOutputNames.Contains(sourceOutputName, StringComparer.OrdinalIgnoreCase);
                var sourcePoint = GetCardPortPoint(sourceCard, isOutput: true, sourceOutputNames, sourceOutputName);

                var detail = hasOutput
                    ? "Binding resolved."
                    : $"Source task '{sourceTaskName}' does not declare output '{sourceOutputName}'.";

                AddConnector(
                    sourcePoint,
                    targetPoint,
                    hasOutput,
                    sourceTaskName,
                    sourceOutputName,
                    target.Task.Name,
                    binding.InputName,
                    binding.Reference,
                    detail);
            }
        }
    }

    private static bool TryParseBindingTaskOutputReference(string reference, out string taskName, out string outputName)
    {
        taskName = string.Empty;
        outputName = string.Empty;

        if (string.IsNullOrWhiteSpace(reference) ||
            reference.StartsWith("port:", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedReference = reference.StartsWith("output:", StringComparison.OrdinalIgnoreCase)
            ? reference[7..].Trim()
            : reference;

        var dotIndex = normalizedReference.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= normalizedReference.Length - 1)
        {
            return false;
        }

        taskName = normalizedReference[..dotIndex].Trim();
        outputName = normalizedReference[(dotIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(taskName) && !string.IsNullOrWhiteSpace(outputName);
    }

    private Point GetCardPortPoint(Grid card, bool isOutput, IReadOnlyList<string> portNames, string portName)
    {
        var cardOrigin = card.TranslatePoint(new Point(0, 0), TaskConnectorOverlay);
        var x = isOutput ? cardOrigin.X + card.ActualWidth : cardOrigin.X;

        if (portNames.Count == 0)
        {
            return new Point(x, cardOrigin.Y + (card.ActualHeight / 2));
        }

        var portIndex = -1;
        for (var i = 0; i < portNames.Count; i++)
        {
            if (string.Equals(portNames[i], portName, StringComparison.OrdinalIgnoreCase))
            {
                portIndex = i;
                break;
            }
        }

        if (portIndex < 0)
        {
            portIndex = 0;
        }

        var spacing = card.ActualHeight / (portNames.Count + 1);
        var y = cardOrigin.Y + (spacing * (portIndex + 1));
        return new Point(x, y);
    }

    private void AddConnector(
        Point start,
        Point end,
        bool resolved,
        string? sourceTaskName,
        string? sourceOutputName,
        string targetTaskName,
        string targetInputName,
        string bindingReference,
        string statusDetail)
    {
        var stroke = resolved ? GetResolvedConnectorBrush() : GetUnresolvedConnectorBrush();
        var controlOffset = Math.Max(24, Math.Abs(end.X - start.X) * 0.35);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + controlOffset, start.Y),
            new Point(end.X - controlOffset, end.Y),
            end,
            isStroked: true));

        var path = new System.Windows.Shapes.Path
        {
            Data = new PathGeometry([figure]),
            Stroke = stroke,
            StrokeThickness = resolved ? 2.2 : 2,
            Opacity = 0.9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        if (!resolved)
        {
            path.StrokeDashArray = [4, 4];
        }

        var connector = new ConnectorVisual
        {
            Path = path,
            SourceTaskName = sourceTaskName,
            SourceOutputName = sourceOutputName,
            TargetTaskName = string.IsNullOrWhiteSpace(targetTaskName) ? "(Unnamed Task)" : targetTaskName.Trim(),
            TargetInputName = targetInputName,
            BindingReference = bindingReference,
            Resolved = resolved,
            StatusDetail = statusDetail
        };

        path.ToolTip = BuildConnectorToolTip(connector);
        path.Cursor = Cursors.Hand;
        path.MouseEnter += Connector_MouseEnter;
        path.MouseLeave += Connector_MouseLeave;
        path.MouseLeftButtonUp += Connector_MouseLeftButtonUp;

        TaskConnectorOverlay.Children.Add(path);
        _connectorVisuals.Add(connector);
        _connectorByPath[path] = connector;
    }

    private static string BuildConnectorToolTip(ConnectorVisual connector)
    {
        var source = connector.SourceTaskName is null
            ? "(unresolved source)"
            : $"{connector.SourceTaskName}.{connector.SourceOutputName}";

        var status = connector.Resolved ? "Resolved" : "Unresolved";

        var builder = new StringBuilder();
        builder.AppendLine($"{status} binding");
        builder.AppendLine($"From: {source}");
        builder.AppendLine($"To: {connector.TargetTaskName}.{connector.TargetInputName}");
        builder.AppendLine($"Reference: {connector.BindingReference}");
        builder.Append($"Detail: {connector.StatusDetail}");
        return builder.ToString();
    }

    private void Connector_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Path path || !_connectorByPath.TryGetValue(path, out var connector))
        {
            return;
        }

        ApplyConnectorHighlight(connector);
    }

    private void Connector_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pinnedConnector is not null)
        {
            ApplyConnectorHighlight(_pinnedConnector);
            return;
        }

        ApplyConnectorDefaultStyles();
    }

    private void Connector_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Path path || !_connectorByPath.TryGetValue(path, out var connector))
        {
            return;
        }

        _pinnedConnector = ReferenceEquals(_pinnedConnector, connector) ? null : connector;

        if (_pinnedConnector is null)
        {
            ApplyConnectorDefaultStyles();
        }
        else
        {
            ApplyConnectorHighlight(_pinnedConnector);
        }

        e.Handled = true;
    }

    private void ApplyConnectorDefaultStyles()
    {
        var resolvedStroke = GetResolvedConnectorBrush();
        var unresolvedStroke = GetUnresolvedConnectorBrush();

        foreach (var connector in _connectorVisuals)
        {
            connector.Path.Stroke = connector.Resolved ? resolvedStroke : unresolvedStroke;
            connector.Path.StrokeThickness = connector.Resolved ? 2.2 : 2;
            connector.Path.Opacity = 0.9;
        }
    }

    private void ApplyConnectorHighlight(ConnectorVisual focal)
    {
        var related = GetUpstreamAndDownstreamConnectors(focal);
        var resolvedStroke = GetResolvedConnectorBrush();
        var unresolvedStroke = GetUnresolvedConnectorBrush();
        var focusedStroke = GetFocusedConnectorBrush();

        foreach (var connector in _connectorVisuals)
        {
            if (ReferenceEquals(connector, focal))
            {
                connector.Path.Stroke = focusedStroke;
                connector.Path.StrokeThickness = 3.6;
                connector.Path.Opacity = 1;
                continue;
            }

            if (related.Contains(connector))
            {
                connector.Path.Stroke = connector.Resolved ? resolvedStroke : unresolvedStroke;
                connector.Path.StrokeThickness = connector.Resolved ? 2.8 : 2.6;
                connector.Path.Opacity = 1;
                continue;
            }

            connector.Path.Stroke = connector.Resolved ? resolvedStroke : unresolvedStroke;
            connector.Path.StrokeThickness = connector.Resolved ? 1.8 : 1.7;
            connector.Path.Opacity = 0.22;
        }
    }

    private HashSet<ConnectorVisual> GetUpstreamAndDownstreamConnectors(ConnectorVisual focal)
    {
        var result = new HashSet<ConnectorVisual>();

        var upstreamFrontier = new Queue<string>();
        var upstreamVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(focal.SourceTaskName))
        {
            upstreamFrontier.Enqueue(focal.SourceTaskName);
            upstreamVisited.Add(focal.SourceTaskName);
        }

        while (upstreamFrontier.Count > 0)
        {
            var taskName = upstreamFrontier.Dequeue();

            foreach (var connector in _connectorVisuals.Where(item => item.Resolved && string.Equals(item.TargetTaskName, taskName, StringComparison.OrdinalIgnoreCase)))
            {
                if (ReferenceEquals(connector, focal))
                {
                    continue;
                }

                result.Add(connector);

                if (!string.IsNullOrWhiteSpace(connector.SourceTaskName) && upstreamVisited.Add(connector.SourceTaskName))
                {
                    upstreamFrontier.Enqueue(connector.SourceTaskName);
                }
            }
        }

        var downstreamFrontier = new Queue<string>();
        var downstreamVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            focal.TargetTaskName
        };

        downstreamFrontier.Enqueue(focal.TargetTaskName);

        while (downstreamFrontier.Count > 0)
        {
            var taskName = downstreamFrontier.Dequeue();

            foreach (var connector in _connectorVisuals.Where(item => item.Resolved && !string.IsNullOrWhiteSpace(item.SourceTaskName) && string.Equals(item.SourceTaskName, taskName, StringComparison.OrdinalIgnoreCase)))
            {
                if (ReferenceEquals(connector, focal))
                {
                    continue;
                }

                result.Add(connector);

                if (downstreamVisited.Add(connector.TargetTaskName))
                {
                    downstreamFrontier.Enqueue(connector.TargetTaskName);
                }
            }
        }

        return result;
    }

    private IReadOnlyList<string> GetGroupNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in _currentConfiguration.Tasks)
        {
            var groupName = NormalizeGroupName(task.Group);
            if (!seen.Add(groupName))
            {
                continue;
            }

            names.Add(groupName);
        }

        return names;
    }

    private IReadOnlyList<GroupSidebarItem> GetGroupSidebarItems(IReadOnlyList<string> groupNames)
    {
        var items = new List<GroupSidebarItem>();

        foreach (var groupName in groupNames)
        {
            var taskPreviews = _currentConfiguration.Tasks
                .Where(task => string.Equals(NormalizeGroupName(task.Group), groupName, StringComparison.OrdinalIgnoreCase))
                .Select(task => new TaskSidebarPreview
                {
                    Name = task.Name,
                    State = GetTaskRunState(task)
                })
                .ToList();

            var isExpanded = _groupExpandedStates.TryGetValue(groupName, out var expanded)
                ? expanded
                : string.Equals(groupName, _selectedGroupName, StringComparison.OrdinalIgnoreCase);

            items.Add(new GroupSidebarItem
            {
                Name = groupName,
                Tasks = taskPreviews,
                IsExpanded = isExpanded
            });
        }

        return items;

    }

    private static string NormalizeGroupName(string? groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? "General" : groupName.Trim();
    }

    private TaskRunState GetTaskRunState(LauncherTask task)
    {
        var key = GetTaskStateKey(task);
        return _taskRunStates.TryGetValue(key, out var state) ? state : TaskRunState.NeverRun;
    }

    private void SetTaskRunState(LauncherTask task, TaskRunState state)
    {
        _taskRunStates[GetTaskStateKey(task)] = state;
        ApplyTaskCardRunningVisual(task);
        UpdateSidebarStatusAnimation();
        RebuildGroupNavigator(GetGroupSidebarItems(GetGroupNames()));
    }

    private void ApplyTaskCardRunningVisual(LauncherTask task)
    {
        var stateKey = GetTaskStateKey(task);
        if (!_taskRunningOverlays.TryGetValue(stateKey, out var overlay))
        {
            return;
        }

        var state = GetTaskRunState(task);
        if (state == TaskRunState.Running)
        {
            overlay.OverlayBorder.Visibility = Visibility.Visible;
            overlay.PulseStoryboard.Begin(overlay.OverlayBorder, isControllable: true);
            return;
        }

        overlay.PulseStoryboard.Stop(overlay.OverlayBorder);
        overlay.OverlayBorder.Visibility = Visibility.Collapsed;
        overlay.OverlayBorder.Opacity = 0.45;
    }

    private static TaskRunningOverlayVisual CreateRunningOverlay()
    {
        var ring = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(245, 40, 140, 255)),
            Background = new SolidColorBrush(Color.FromArgb(78, 40, 140, 255)),
            Opacity = 0.45,
            Margin = new Thickness(0)
        };
        Panel.SetZIndex(ring, 12);

        var pulseAnimation = new DoubleAnimation
        {
            From = 0.45,
            To = 0.98,
            Duration = TimeSpan.FromMilliseconds(520),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(pulseAnimation);
        Storyboard.SetTarget(pulseAnimation, ring);
        Storyboard.SetTargetProperty(pulseAnimation, new PropertyPath(UIElement.OpacityProperty));

        return new TaskRunningOverlayVisual
        {
            OverlayBorder = ring,
            PulseStoryboard = storyboard
        };
    }

    private void UpdateSidebarStatusAnimation()
    {
        var hasRunningTasks = _taskRunStates.Values.Any(state => state == TaskRunState.Running);

        if (hasRunningTasks)
        {
            if (!_sidebarStatusTimer.IsEnabled)
            {
                _sidebarStatusTimer.Start();
            }

            return;
        }

        _sidebarStatusTimer.Stop();
        _sidebarSpinnerFrameIndex = 0;
    }

    private static string GetTaskStateKey(LauncherTask task)
    {
        return string.IsNullOrWhiteSpace(task.Name) ? "Unnamed Task" : task.Name.Trim();
    }

    private Brush GetStateBrush(TaskRunState state)
    {
        return state switch
        {
            TaskRunState.Running => new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            TaskRunState.Succeeded => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            TaskRunState.Failed => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            _ => (Brush)Resources["SecondaryTextBrush"]
        };
    }

    private string GetStateIcon(TaskRunState state)
    {
        return state switch
        {
            TaskRunState.Running => SidebarRunningFrames[_sidebarSpinnerFrameIndex % SidebarRunningFrames.Length],
            TaskRunState.Succeeded => "✓",
            TaskRunState.Failed => "✖",
            _ => "·"
        };
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new TaskFormWindow { Owner = this };

        if (window.ShowDialog() != true || window.Task is null)
        {
            return;
        }

        var newTask = window.Task;
        var existingIndex = _currentConfiguration.Tasks.FindIndex(task =>
            string.Equals(task.Name.Trim(), newTask.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            var overwriteResult = MessageBox.Show(
                $"A task named '{newTask.Name}' already exists. Overwrite it?",
                "Duplicate Task Name",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (overwriteResult != MessageBoxResult.OK)
            {
                return;
            }

            _currentConfiguration.Tasks[existingIndex] = newTask;
        }
        else
        {
            _currentConfiguration.Tasks.Add(newTask);
        }

        _selectedGroupName = NormalizeGroupName(newTask.Group);

        SaveTasksAndRefresh(TaskEditorTitle);
        RefreshHistoryView();
    }

    private void TaskCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: LauncherTask })
        {
            return;
        }

        _dragStartPoint = e.GetPosition(TaskPanel);
        _isDraggingTaskCard = true;
    }

    private void TaskCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingTaskCard = false;
    }

    private void TaskCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTaskCard || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (sender is not Button button || button.Tag is not LauncherTask)
        {
            return;
        }

        var currentPoint = e.GetPosition(TaskPanel);
        var horizontalDistance = Math.Abs(currentPoint.X - _dragStartPoint.X);
        var verticalDistance = Math.Abs(currentPoint.Y - _dragStartPoint.Y);

        if (horizontalDistance < SystemParameters.MinimumHorizontalDragDistance && verticalDistance < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (button.Parent is not Grid taskCard || taskCard.Tag is not TaskCardVisual sourceCardVisual)
        {
            return;
        }

        var sourceIndex = sourceCardVisual.TaskIndex;

        _isDraggingTaskCard = false;
        var dragData = new DataObject(typeof(int), sourceIndex);
        DragDrop.DoDragDrop(taskCard, dragData, DragDropEffects.Move);
    }

    private void TaskPanel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int)))
        {
            e.Effects = DragDropEffects.None;
            ClearDropCue();
            e.Handled = true;
            return;
        }

        var targetDisplayIndex = GetDropTargetDisplayIndex(e.GetPosition(TaskPanel));
        ApplyDropCue(targetDisplayIndex);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TaskPanel_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropCue();
    }

    private void TaskPanel_Drop(object sender, DragEventArgs e)
    {
        ClearDropCue();

        if (!e.Data.GetDataPresent(typeof(int)))
        {
            return;
        }

        var sourceGlobalIndex = (int)e.Data.GetData(typeof(int))!;
        var sourceDisplayIndex = _visibleTaskIndexes.IndexOf(sourceGlobalIndex);
        var targetDisplayIndex = GetDropTargetDisplayIndex(e.GetPosition(TaskPanel));

        if (sourceDisplayIndex < 0 || sourceDisplayIndex >= _visibleTaskIndexes.Count)
        {
            return;
        }

        if (targetDisplayIndex < 0 || targetDisplayIndex > _visibleTaskIndexes.Count)
        {
            return;
        }

        if (sourceDisplayIndex == targetDisplayIndex || sourceDisplayIndex == targetDisplayIndex - 1)
        {
            return;
        }

        var groupTasks = _visibleTaskIndexes.Select(index => _currentConfiguration.Tasks[index]).ToList();
        var movedTask = groupTasks[sourceDisplayIndex];
        groupTasks.RemoveAt(sourceDisplayIndex);

        if (sourceDisplayIndex < targetDisplayIndex)
        {
            targetDisplayIndex--;
        }

        targetDisplayIndex = Math.Clamp(targetDisplayIndex, 0, groupTasks.Count);
        groupTasks.Insert(targetDisplayIndex, movedTask);

        for (var i = 0; i < _visibleTaskIndexes.Count; i++)
        {
            _currentConfiguration.Tasks[_visibleTaskIndexes[i]] = groupTasks[i];
        }

        SaveTasksAndRefresh("Task Reorder");
    }

    private int GetDropTargetDisplayIndex(Point dropPoint)
    {
        for (var panelIndex = 0; panelIndex < TaskPanel.Children.Count; panelIndex++)
        {
            if (TaskPanel.Children[panelIndex] is not Grid card || card.Tag is not TaskCardVisual cardVisual)
            {
                continue;
            }

            var displayIndex = cardVisual.DisplayIndex;

            var topLeft = card.TransformToAncestor(TaskPanel).Transform(new Point(0, 0));
            var cardCenterX = topLeft.X + card.ActualWidth / 2;
            var cardCenterY = topLeft.Y + card.ActualHeight / 2;

            if (dropPoint.Y < cardCenterY || (Math.Abs(dropPoint.Y - cardCenterY) < card.ActualHeight * 0.45 && dropPoint.X < cardCenterX))
            {
                return displayIndex;
            }
        }

        return _visibleTaskIndexes.Count;
    }

    private void ApplyDropCue(int targetDisplayIndex)
    {
        ClearDropCue();

        if (_visibleTaskIndexes.Count == 0)
        {
            return;
        }

        var cueDisplayIndex = targetDisplayIndex >= _visibleTaskIndexes.Count
            ? _visibleTaskIndexes.Count - 1
            : targetDisplayIndex;

        for (var panelIndex = 0; panelIndex < TaskPanel.Children.Count; panelIndex++)
        {
            if (TaskPanel.Children[panelIndex] is not Grid card || card.Tag is not TaskCardVisual cardVisual)
            {
                continue;
            }

            if (cardVisual.DisplayIndex != cueDisplayIndex)
            {
                continue;
            }

            cardVisual.DropCueBorder.BorderThickness = new Thickness(2);
            _activeDropCueBorder = cardVisual.DropCueBorder;
            break;
        }
    }

    private void ClearDropCue()
    {
        if (_activeDropCueBorder is null)
        {
            return;
        }

        _activeDropCueBorder.BorderThickness = new Thickness(0);
        _activeDropCueBorder = null;
    }

    private void TaskMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int taskIndex })
        {
            return;
        }

        if (!IsValidTaskIndex(taskIndex))
        {
            MessageBox.Show(TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu();

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditTask(taskIndex);

        var replayEntry = GetLastSnapshotEntry(_currentConfiguration.Tasks[taskIndex]);
        var replayItem = new MenuItem
        {
            Header = "Replay Last Snapshot",
            IsEnabled = replayEntry is not null
        };
        replayItem.Click += async (_, _) =>
        {
            if (replayEntry is null)
            {
                return;
            }

            var replayTask = ToTask(replayEntry.Snapshot);
            SelectGroup(NormalizeGroupName(replayTask.Group));

            await ExecuteTaskAsync(replayTask, null, isReplay: true, replayFromEntryId: replayEntry.Id);
        };

        var runChainItem = new MenuItem { Header = "Run Chain (Same Group)" };
        runChainItem.Click += async (_, _) =>
        {
            if (!IsValidTaskIndex(taskIndex))
            {
                MessageBox.Show(TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RunTaskChainFromTaskAsync(taskIndex);
        };

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteTask(taskIndex);

        menu.Items.Add(editItem);
        menu.Items.Add(replayItem);
        menu.Items.Add(runChainItem);
        menu.Items.Add(deleteItem);

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void EditTask(int taskIndex)
    {
        if (!TryGetTask(taskIndex, out var selectedTask))
        {
            MessageBox.Show(TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var taskJson = TaskConfiguration.SerializeTask(selectedTask);
        var editorWindow = new TaskEditorWindow(taskJson) { Owner = this };

        if (editorWindow.ShowDialog() != true)
        {
            return;
        }

        if (!TaskConfiguration.TryDeserializeTask(editorWindow.TaskJsonText, out var updatedTask, out var parseError))
        {
            MessageBox.Show($"Could not save task because the JSON is invalid.\n\n{parseError}", "Task Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _currentConfiguration.Tasks[taskIndex] = updatedTask;

        SaveTasksAndRefresh(TaskEditorTitle);
    }

    private void DeleteTask(int taskIndex)
    {
        if (!IsValidTaskIndex(taskIndex))
        {
            MessageBox.Show(TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentConfiguration.Tasks.RemoveAt(taskIndex);

        SaveTasksAndRefresh(TaskEditorTitle);
    }

    private bool IsValidTaskIndex(int taskIndex)
    {
        return taskIndex >= 0 && taskIndex < _currentConfiguration.Tasks.Count;
    }

    private bool TryGetTask(int taskIndex, out LauncherTask task)
    {
        task = new LauncherTask();

        if (!IsValidTaskIndex(taskIndex))
        {
            return false;
        }

        task = _currentConfiguration.Tasks[taskIndex];
        return true;
    }

    private void SaveTasksAndRefresh(string errorTitle)
    {
        try
        {
            TaskConfiguration.Save(_configPath, _currentConfiguration);
            LoadTaskButtons();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save tasks.json.\n{ex.Message}", errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static Brush GetReadableTextBrush(Color backgroundColor)
    {
        var luminance = (0.2126 * backgroundColor.R + 0.7152 * backgroundColor.G + 0.0722 * backgroundColor.B) / 255.0;

        if (luminance >= 0.62)
        {
            return new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x35));
        }

        return Brushes.White;
    }

    private async void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherTask task })
        {
            return;
        }

        await ExecuteTaskAsync(task, (Button)sender, isReplay: false);
    }

    private async Task RunTaskChainFromTaskAsync(int startTaskIndex)
    {
        if (!TryGetTask(startTaskIndex, out var startTask))
        {
            MessageBox.Show(TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var groupName = NormalizeGroupName(startTask.Group);
        SelectGroup(groupName);

        var sameGroupTasks = _currentConfiguration.Tasks
            .Select((task, index) => new { Task = task, Index = index })
            .Where(item => string.Equals(NormalizeGroupName(item.Task.Group), groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var startPosition = sameGroupTasks.FindIndex(item => item.Index == startTaskIndex);
        if (startPosition < 0)
        {
            return;
        }

        var context = new DataflowContext();
        var chainLength = sameGroupTasks.Count - startPosition;

        for (var offset = 0; offset < chainLength; offset++)
        {
            var taskInfo = sameGroupTasks[startPosition + offset];
            var chainLabel = $"{offset + 1}/{chainLength}";
            var result = await ExecuteTaskAsync(taskInfo.Task, null, isReplay: false, dataflowContext: context, chainStepLabel: chainLabel);

            if (!result.Succeeded)
            {
                MessageBox.Show(
                    $"Chain stopped at '{taskInfo.Task.Name}' (step {chainLabel}) due to failure.",
                    "Chain Execution",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
    }

    private async Task<TaskExecutionResult> ExecuteTaskAsync(
        LauncherTask task,
        Button? sourceButton,
        bool isReplay,
        string? replayFromEntryId = null,
        DataflowContext? dataflowContext = null,
        string? chainStepLabel = null)
    {
        var tabName = isReplay ? $"{task.Name} (replay)" : task.Name;
        var tabContext = CreateOutputTab(tabName);
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        SetTaskRunState(task, TaskRunState.Running);
        var resolved = DataflowPortResolver.Resolve(task, dataflowContext);
        var effectiveTask = resolved.ResolvedTask;
        var historyEntry = StartHistoryEntry(effectiveTask, isReplay, replayFromEntryId);

        if (!string.IsNullOrWhiteSpace(chainStepLabel))
        {
            AppendLog(outputTextBox, $"[{task.Name}] Chain step {chainStepLabel}");
        }

        AppendDataflowTrace(outputTextBox, task.Name, resolved);

        if (string.IsNullOrWhiteSpace(effectiveTask.CommandLine))
        {
            AppendLog(outputTextBox, $"[{task.Name}] Command line is empty.");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, false, null);
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }

        var runner = TaskProcessStartInfoFactory.NormalizeRunner(effectiveTask.Runner);
        var waitForExit = effectiveTask.WaitForExit;

        if (sourceButton is not null)
        {
            sourceButton.IsEnabled = false;
        }

        var modePrefix = isReplay ? "[replay] " : string.Empty;
        var executionMode = waitForExit ? "wait" : "fire-and-forget";
        AppendLog(outputTextBox, $"[{task.Name}] {modePrefix}Running ({runner}, {executionMode}): {effectiveTask.CommandLine}");

        try
        {
            if (!TaskProcessStartInfoFactory.TryBuild(effectiveTask, out var processStartInfo, out var configurationError))
            {
                AppendLog(outputTextBox, $"[{task.Name}] Invalid task configuration. {configurationError}");
                SetTabStopped(tabContext, false);
                SetTaskRunState(task, TaskRunState.Failed);
                CompleteHistoryEntry(historyEntry, false, null);
                return new TaskExecutionResult { Succeeded = false, ExitCode = null };
            }

            if (!waitForExit)
            {
                // Detached launches should not keep stdout/stderr pipes open.
                processStartInfo.RedirectStandardOutput = false;
                processStartInfo.RedirectStandardError = false;
            }

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            if (!waitForExit)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Process started (PID {process.Id}). Not waiting for exit.");
                SetTabStopped(tabContext, true);
                SetTaskRunState(task, TaskRunState.Succeeded);
                CompleteHistoryEntry(historyEntry, true, null);

                if (resolved.ResolvedOutputs.Count > 0)
                {
                    AppendPublishedOutputs(outputTextBox, task.Name, resolved.ResolvedOutputs);
                    dataflowContext?.RegisterTaskOutputs(task.Name, resolved.ResolvedOutputs);
                }

                return new TaskExecutionResult
                {
                    Succeeded = true,
                    ExitCode = null,
                    PublishedOutputs = new Dictionary<string, string>(resolved.ResolvedOutputs, StringComparer.OrdinalIgnoreCase)
                };
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            AppendProcessOutput(outputTextBox, task.Name, process.ExitCode, standardOutput, standardError);
            SetTabStopped(tabContext, process.ExitCode == 0);
            SetTaskRunState(task, process.ExitCode == 0 ? TaskRunState.Succeeded : TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, process.ExitCode == 0, process.ExitCode);

            if (process.ExitCode == 0 && resolved.ResolvedOutputs.Count > 0)
            {
                AppendPublishedOutputs(outputTextBox, task.Name, resolved.ResolvedOutputs);
                dataflowContext?.RegisterTaskOutputs(task.Name, resolved.ResolvedOutputs);
            }

            return new TaskExecutionResult
            {
                Succeeded = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                PublishedOutputs = new Dictionary<string, string>(resolved.ResolvedOutputs, StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            AppendLog(outputTextBox, $"[{task.Name}] Failed to execute task. {ex.Message}");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, false, null);
            MessageBox.Show($"Failed to run task '{task.Name}'.\n{ex.Message}",
                "Task Execution Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }
        finally
        {
            if (sourceButton is not null)
            {
                sourceButton.IsEnabled = true;
            }
        }
    }

    private TaskRunHistoryEntry StartHistoryEntry(LauncherTask task, bool isReplay, string? replayFromEntryId)
    {
        var entry = new TaskRunHistoryEntry
        {
            Snapshot = CreateSnapshot(task),
            IsReplay = isReplay,
            ReplayFromRunId = replayFromEntryId,
            Outcome = TaskRunOutcome.Running,
            StartedAtUtc = DateTime.UtcNow
        };

        _runHistory.Add(entry);
        TrimRunHistory();
        TaskRunHistoryStore.Save(_runHistory);
        RefreshHistoryView();
        return entry;
    }

    private void CompleteHistoryEntry(TaskRunHistoryEntry entry, bool succeeded, int? exitCode)
    {
        entry.CompletedAtUtc = DateTime.UtcNow;
        entry.ExitCode = exitCode;
        entry.Outcome = succeeded ? TaskRunOutcome.Succeeded : TaskRunOutcome.Failed;
        TaskRunHistoryStore.Save(_runHistory);
        RefreshHistoryView();
        GroupMetaText.Text = string.IsNullOrWhiteSpace(_selectedGroupName)
            ? GroupMetaText.Text
            : $"Snapshots in this group: {_runHistory.Count(item => string.Equals(item.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase))}";
    }

    private void TrimRunHistory()
    {
        if (_runHistory.Count <= MaxRunHistoryEntries)
        {
            return;
        }

        _runHistory = _runHistory
            .OrderByDescending(entry => entry.StartedAtUtc)
            .Take(MaxRunHistoryEntries)
            .OrderBy(entry => entry.StartedAtUtc)
            .ToList();
    }

    private static TaskRunSnapshot CreateSnapshot(LauncherTask task)
    {
        return new TaskRunSnapshot
        {
            Name = task.Name,
            Group = NormalizeGroupName(task.Group),
            Description = task.Description,
            CommandLine = task.CommandLine,
            Runner = task.Runner,
            WorkingDirectory = task.WorkingDirectory,
            WaitForExit = task.WaitForExit,
            Env = new Dictionary<string, string>(task.Env, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static LauncherTask ToTask(TaskRunSnapshot snapshot)
    {
        return new LauncherTask
        {
            Name = snapshot.Name,
            Group = NormalizeGroupName(snapshot.Group),
            Description = snapshot.Description,
            CommandLine = snapshot.CommandLine,
            Runner = snapshot.Runner,
            WorkingDirectory = snapshot.WorkingDirectory,
            WaitForExit = snapshot.WaitForExit,
            Env = new Dictionary<string, string>(snapshot.Env, StringComparer.OrdinalIgnoreCase)
        };
    }

    private TaskRunHistoryEntry? GetLastSnapshotEntry(LauncherTask task)
    {
        var taskName = task.Name.Trim();
        var groupName = NormalizeGroupName(task.Group);

        return _runHistory
            .Where(entry => string.Equals(entry.Snapshot.Name, taskName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Snapshot.Group, groupName, StringComparison.OrdinalIgnoreCase)
                && entry.Outcome != TaskRunOutcome.Running)
            .OrderByDescending(entry => entry.StartedAtUtc)
            .FirstOrDefault();
    }

    private TaskTabContext CreateOutputTab(string taskName)
    {
        var currentOutputTabControl = GetOrCreateSelectedGroupOutputTabControl();
        var tabHeader = new DockPanel();

        var titleText = new TextBlock
        {
            Text = $"{taskName} {DateTime.Now:HH:mm:ss}",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        DockPanel.SetDock(titleText, Dock.Left);

        var statusText = new TextBlock
        {
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Text = string.Empty
        };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        DockPanel.SetDock(statusText, Dock.Right);

        var closeButton = new Button
        {
            Content = "x",
            FontWeight = FontWeights.Bold,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = "Close tab"
        };
        closeButton.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        DockPanel.SetDock(closeButton, Dock.Right);

        tabHeader.Children.Add(titleText);
        tabHeader.Children.Add(statusText);
        tabHeader.Children.Add(closeButton);

        var outputTextBox = CreateLogTextBox();

        var tabItem = new TabItem
        {
            Header = tabHeader,
            Content = outputTextBox
        };

        var spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        var tabContext = new TaskTabContext
        {
            HostTabControl = currentOutputTabControl,
            TabItem = tabItem,
            OutputTextBox = outputTextBox,
            StatusText = statusText,
            SpinnerTimer = spinnerTimer
        };

        tabItem.Tag = tabContext;

        spinnerTimer.Tick += (_, _) =>
        {
            tabContext.StatusText.Text = RunningIndicatorFrames[tabContext.SpinnerFrameIndex % RunningIndicatorFrames.Length];
            tabContext.SpinnerFrameIndex++;
        };

        tabHeader.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2)
            {
                CloseTab(tabItem);
                args.Handled = true;
            }
        };

        closeButton.Click += (_, _) => CloseTab(tabItem);

        currentOutputTabControl.Items.Add(tabItem);
        currentOutputTabControl.SelectedItem = tabItem;
        outputTextBox.Focus();

        return tabContext;
    }

    private TabControl GetOrCreateSelectedGroupOutputTabControl()
    {
        var groupName = _selectedGroupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            groupName = "General";
            _selectedGroupName = groupName;
        }

        if (_groupOutputTabs.TryGetValue(groupName, out var existingTabControl))
        {
            return existingTabControl;
        }

        var tabControl = new TabControl
        {
            Style = (Style)Resources["OutputTabControlStyle"],
            ItemContainerStyle = (Style)Resources["OutputTabItemStyle"],
            Visibility = Visibility.Collapsed
        };

        OutputTabHost.Children.Add(tabControl);
        _groupOutputTabs[groupName] = tabControl;
        return tabControl;
    }

    private void ShowSelectedGroupOutputTabControl()
    {
        foreach (var tabControl in _groupOutputTabs.Values)
        {
            tabControl.Visibility = Visibility.Collapsed;
        }

        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return;
        }

        var selectedControl = GetOrCreateSelectedGroupOutputTabControl();
        selectedControl.Visibility = Visibility.Visible;
    }

    private static void SetTabRunning(TaskTabContext tabContext)
    {
        tabContext.SpinnerFrameIndex = 0;
        tabContext.StatusText.Text = RunningIndicatorFrames[0];
        tabContext.SpinnerTimer.Start();
    }

    private static void SetTabStopped(TaskTabContext tabContext, bool succeeded)
    {
        tabContext.SpinnerTimer.Stop();
        tabContext.StatusText.Text = succeeded ? "●" : "✖";
        tabContext.StatusText.Foreground = succeeded
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    }

    private TextBox CreateLogTextBox()
    {
        var textBox = new TextBox
        {
            Margin = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            AcceptsTab = true
        };
        textBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        textBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        textBox.SetResourceReference(Control.BorderBrushProperty, "PanelBorderBrush");

        var contextMenu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (_, _) => textBox.Copy();

        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) => textBox.SelectAll();

        contextMenu.Items.Add(copyItem);
        contextMenu.Items.Add(selectAllItem);

        textBox.ContextMenu = contextMenu;

        return textBox;
    }

    private void CloseTab(TabItem tabItem)
    {
        if (tabItem.Tag is not TaskTabContext tabContext)
        {
            return;
        }

        var outputTabControl = tabContext.HostTabControl;

        if (outputTabControl.Items.Contains(tabItem))
        {
            // Stop any tab spinner to avoid timers running after tab closure.
            tabContext.SpinnerTimer.Stop();
        }

        var selectedIndex = outputTabControl.Items.IndexOf(tabItem);
        outputTabControl.Items.Remove(tabItem);

        if (outputTabControl.Items.Count == 0)
        {
            return;
        }

        var nextIndex = Math.Min(selectedIndex, outputTabControl.Items.Count - 1);
        outputTabControl.SelectedIndex = nextIndex;
    }

    private void AppendProcessOutput(TextBox outputTextBox, string taskName, int exitCode, string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{taskName}] Exit code: {exitCode}");

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.AppendLine("[stdout]");
            builder.AppendLine(standardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            builder.AppendLine("[stderr]");
            builder.AppendLine(standardError.TrimEnd());
        }

        if (string.IsNullOrWhiteSpace(standardOutput) && string.IsNullOrWhiteSpace(standardError))
        {
            builder.AppendLine("No output was produced.");
        }

        AppendLog(outputTextBox, builder.ToString().TrimEnd());
    }

    private void AppendDataflowTrace(TextBox outputTextBox, string taskName, DataflowResolutionResult resolved)
    {
        if (resolved.TraceLines.Count == 0)
        {
            AppendLog(outputTextBox, $"[{taskName}] Dataflow: no inputs, bindings, outputs, or placeholders to resolve.");
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[{taskName}] Dataflow resolution trace:");

        foreach (var line in resolved.TraceLines)
        {
            builder.AppendLine($"  - {line}");
        }

        AppendLog(outputTextBox, builder.ToString().TrimEnd());
    }

    private void AppendPublishedOutputs(TextBox outputTextBox, string taskName, IReadOnlyDictionary<string, string> outputs)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[{taskName}] Published outputs:");

        foreach (var pair in outputs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  - {pair.Key} = {pair.Value}");
        }

        AppendLog(outputTextBox, builder.ToString().TrimEnd());
    }

    private void AppendLog(TextBox outputTextBox, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        outputTextBox.AppendText($"[{timestamp}] {message}{Environment.NewLine}{Environment.NewLine}");
        outputTextBox.ScrollToEnd();
    }
}