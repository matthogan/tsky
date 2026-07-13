using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using RichTextBox = System.Windows.Controls.RichTextBox;

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
    private const double DefaultSidebarWidth = 220;
    private const double MinSidebarWidth = 180;
    private const double MaxSidebarWidth = 520;
    private const double DefaultHistoryListPaneRatio = 0.4;
    private const double MinHistoryListPaneRatio = 0.2;
    private const double MaxHistoryListPaneRatio = 0.8;
    private const string TaskEditorTitle = "Task Editor";
    private const string TaskUnavailableMessage = "The selected task is no longer available.";
    private const int MaxRunHistoryEntries = 300;
    private const bool EnableLayoutDebugLogging = true;
    private const double GraphNodeWidth = 190;
    private const double GraphNodeHeight = 96;
    private const double GraphLayerSpacing = 68;
    private const double GraphRowSpacing = 32;
    private const double GraphSurfacePadding = 24;

    private readonly string _currentDirectoryConfigPath;
    private readonly string _userProfileConfigPath;
    private TaskConfiguration _currentConfiguration = new();
    private UiState _uiState = new();
    private Point _dragStartPoint;
    private bool _isDraggingTaskCard;
    private Border? _activeDropCueBorder;
    private string? _selectedGroupName;
    private bool _preferGroupDeleteContext;
    private int? _selectedTaskIndex;
    private readonly List<int> _visibleTaskIndexes = [];
    private readonly Dictionary<string, OutputGroupPanelContext> _groupOutputPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupExpandedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskRunState> _taskRunStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _taskExecutionCancellation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupHistoryVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupOutputVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupSidebarVisibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _groupOutputHeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _favoriteGroupOrder = [];
    private readonly List<string> _nonFavoriteGroupsOrder = [];
    private readonly Dictionary<string, List<TextBlock>> _sidebarTaskStateGlyphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Grid> _taskCardsByIndex = [];
    private readonly Dictionary<int, GraphNodeVisual> _graphNodesByIndex = [];
    private readonly Dictionary<string, TaskRunningOverlayVisual> _taskRunningOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConnectorVisual> _connectorVisuals = [];
    private readonly Dictionary<System.Windows.Shapes.Path, ConnectorVisual> _connectorByPath = [];
    private ConnectorVisual? _pinnedConnector;
    private List<TaskRunHistoryEntry> _runHistory = [];
    private readonly RunHistoryService _runHistoryService;
    private readonly TaskCatalogService _taskCatalogService = new();
    private readonly ImportExportService _importExportService = new();
    private readonly DataflowGraphService _dataflowGraphService = new();
    private bool _isReplayingHistoryBatch;
    private bool _isRunningRitual;
    private bool _isHistoryDetailsVisible = true;
    private double _historyListPaneRatio = DefaultHistoryListPaneRatio;
    private int? _portEditorTaskIndex;
    private readonly HashSet<int> _invalidPortBindingTaskIndexes = [];
    private readonly DispatcherTimer _sidebarStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _graphRenderDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private TaskSurfaceViewMode _taskSurfaceViewMode = TaskSurfaceViewMode.Task;
    private int _sidebarSpinnerFrameIndex;
    private bool _isHandlingBottomPanePortTabSelection;
    private bool _isSyncingBottomPaneToSelection;
    private bool _isGraphPanning;
    private Point _graphPanStartPoint;
    private double _graphPanStartHorizontalOffset;
    private double _graphPanStartVerticalOffset;
    private int? _graphPointerDownTaskIndex;
    private Point _graphPointerDownPoint;
    private bool _graphPointerWasDrag;
    private bool _graphPointerActive;
    private static readonly Brush RibbonHistoryActiveBrush = new SolidColorBrush(Color.FromRgb(0x27, 0xF6, 0xC0));
    private static readonly string[] RunningIndicatorFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] SidebarRunningFrames = ["◐", "◓", "◑", "◒"];
    private const double GraphDragThreshold = 6;

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

    private enum TaskSurfaceViewMode
    {
        Task,
        Graph
    }

    private sealed class TaskTabContext
    {
        public required OutputGroupPanelContext HostOutputPanel { get; init; }

        public required Border RunItemContainer { get; init; }

        public required Expander RunItemExpander { get; init; }

        public required RichTextBox OutputTextBox { get; init; }

        public required TextBlock StatusText { get; init; }

        public required TextBlock TitleText { get; init; }

        public required DispatcherTimer SpinnerTimer { get; init; }

        public int SpinnerFrameIndex { get; set; }
    }

    private sealed class OutputGroupPanelContext
    {
        public required Grid RootGrid { get; init; }

        public required StackPanel RunListPanel { get; init; }

        public required TextBlock EmptyListText { get; init; }

        public List<TaskTabContext> Runs { get; } = [];
    }

    private sealed class TaskCardVisual
    {
        public required int DisplayIndex { get; init; }

        public required int TaskIndex { get; init; }

        public required Border DropCueBorder { get; init; }
        public required Border SelectionBorder { get; init; }
        public required Border InvalidBindingBorder { get; init; }
        public required Button TaskButton { get; init; }
    }

    private sealed class GraphNodeVisual
    {
        public required int TaskIndex { get; init; }

        public required Border SelectionBorder { get; init; }

        public required Border NodeBorder { get; init; }

        public required SolidColorBrush RunningBorderBrush { get; init; }

        public required TextBlock StateText { get; init; }

        public required Button NodeButton { get; init; }

        public bool IsRunningBorderPulseActive { get; set; }
    }

    private sealed class TaskCardTitleTooltipContext
    {
        public required TextBlock NameText { get; init; }

        public required string FullText { get; init; }

        public required Color CardColor { get; init; }

        public required Brush ForegroundBrush { get; init; }

        public required FontFamily FontFamily { get; init; }
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
        public required System.Windows.Shapes.Path PrimaryPath { get; init; }

        public System.Windows.Shapes.Path? CardOverlayPath { get; init; }

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

    private sealed class PortEditorRow
    {
        public required TextBox NameTextBox { get; init; }

        public TextBox? DefaultValueTextBox { get; init; }

        public required TextBox ValueTextBox { get; init; }
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
                    TaskRunOutcome.Started => "BG",
                    TaskRunOutcome.Succeeded => "OK",
                    TaskRunOutcome.IgnoredNonZeroExit => "IGN",
                    TaskRunOutcome.FailedNonZeroExit => "ERR",
                    TaskRunOutcome.LaunchFailed => "LAUNCH",
                    TaskRunOutcome.TimedOut => "TIME",
                    TaskRunOutcome.Cancelled => "CXL",
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
        Color.FromRgb(0x56, 0xCC, 0xF2),
        Color.FromRgb(0x6F, 0xB8, 0xFF),
        Color.FromRgb(0x7C, 0xF9, 0xE3),
        Color.FromRgb(0x8A, 0xD8, 0xFF),
        Color.FromRgb(0xA6, 0xE8, 0xFF),
        Color.FromRgb(0xB6, 0xD6, 0xFF),
        Color.FromRgb(0x97, 0xF0, 0xFF),
        Color.FromRgb(0xB8, 0xE6, 0xFF)
    ];

    public MainWindow()
    {
        InitializeComponent();

        _currentDirectoryConfigPath = TaskConfiguration.GetCurrentDirectoryConfigPath();
        _userProfileConfigPath = TaskConfiguration.GetUserProfileConfigPath();
        _sidebarStatusTimer.Tick += SidebarStatusTimer_Tick;
        _graphRenderDebounceTimer.Tick += GraphRenderDebounceTimer_Tick;
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
        _runHistoryService = new RunHistoryService(_runHistory, MaxRunHistoryEntries);
        TaskScrollViewer.ScrollChanged += TaskScrollViewer_ScrollChanged;
        TaskScrollViewer.SizeChanged += (_, _) =>
        {
            SyncTaskSurfaceToViewport();
            UpdateMainScrollBarVisibility("sizeChanged");
            if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
            {
                RequestGraphRender("sizeChanged");
            }
            else
            {
                ScheduleConnectorRefresh();
            }
        };
        TaskWorkspaceSurface.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        TaskPanel.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        TaskGraphCanvas.PreviewMouseLeftButtonDown += TaskGraphCanvas_PreviewMouseLeftButtonDown;
        TaskGraphCanvas.PreviewMouseMove += TaskGraphCanvas_PreviewMouseMove;
        TaskGraphCanvas.PreviewMouseLeftButtonUp += TaskGraphCanvas_PreviewMouseLeftButtonUp;
        TaskGraphCanvas.LostMouseCapture += TaskGraphCanvas_LostMouseCapture;
        PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
        SizeChanged += (_, _) => ScheduleConnectorRefresh();
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        _uiState = UiStateStore.Load();
        LoadGroupOrderingFromUiState();
        ApplyPersistedUiState();
        ApplyHistoryDetailsVisibility();

        TaskConfiguration.EnsureValidFileOrReset(_userProfileConfigPath);
        LoadConfigurationWithValidation(_userProfileConfigPath, "Read user profile tasks.json");

        LoadTaskButtons();
        UpdateTaskSurfaceToggleUi();
        SyncTaskSurfaceToViewport();
        DebugTraceLog.Write($"[Startup] MainWindow initialized. LogPath='{DebugTraceLog.CurrentLogPath}'");
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
            TaskWorkspaceSurface.MinWidth = 0;
            TaskPanel.Width = double.NaN;
            TaskPanel.MaxWidth = double.PositiveInfinity;
            TaskGraphCanvas.Width = double.NaN;
            TaskGraphEdgeOverlay.Width = double.NaN;
            return;
        }

        var stableWidth = Math.Max(0, scrollHostWidth);

        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            TaskWorkspaceSurface.Width = double.NaN;
            TaskWorkspaceSurface.MinWidth = stableWidth;
            TaskPanel.Width = double.NaN;
            TaskPanel.MaxWidth = double.PositiveInfinity;
            return;
        }

        TaskWorkspaceSurface.MinWidth = 0;
        TaskWorkspaceSurface.Width = stableWidth;
        TaskPanel.Width = double.NaN;
        TaskPanel.MaxWidth = stableWidth;
    }

    private void UpdateMainScrollBarVisibility(string reason)
    {
        var verticalVisibility = ScrollBarVisibility.Hidden;
        var horizontalVisibility = ScrollBarVisibility.Hidden;

        if (TaskScrollViewer.VerticalScrollBarVisibility != verticalVisibility)
        {
            TaskScrollViewer.VerticalScrollBarVisibility = verticalVisibility;
        }

        if (TaskScrollViewer.HorizontalScrollBarVisibility != horizontalVisibility)
        {
            TaskScrollViewer.HorizontalScrollBarVisibility = horizontalVisibility;
        }

        if (EnableLayoutDebugLogging)
        {
            var mode = _taskSurfaceViewMode == TaskSurfaceViewMode.Task ? "Task" : "Graph";
            Debug.WriteLine($"[MainViewLayout:{reason}] mode={mode} viewportW={TaskScrollViewer.ViewportWidth:F1} viewportH={TaskScrollViewer.ViewportHeight:F1} extentW={TaskScrollViewer.ExtentWidth:F1} extentH={TaskScrollViewer.ExtentHeight:F1} panelW={TaskPanel.ActualWidth:F1} panelH={TaskPanel.ActualHeight:F1}");
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
        return new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xFF));
    }

    private static Brush GetUnresolvedConnectorBrush()
    {
        return new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x95));
    }

    private static Brush GetFocusedConnectorBrush()
    {
        return new SolidColorBrush(Color.FromRgb(0xD7, 0xFF, 0x00));
    }

    private void ApplyTitleBarTheme(bool useDarkTheme)
    {
        WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var runningTaskNames = _currentConfiguration.Tasks
            .Where(task => GetTaskRunState(task) == TaskRunState.Running)
            .Select(task => task.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (runningTaskNames.Count > 0)
        {
            var previewNames = runningTaskNames.Take(5).ToList();
            var remainingCount = runningTaskNames.Count - previewNames.Count;
            var nameList = string.Join(Environment.NewLine, previewNames.Select(name => $"- {name}"));
            if (remainingCount > 0)
            {
                nameList += Environment.NewLine + $"- ...and {remainingCount} more";
            }

            var message = $"{runningTaskNames.Count} task(s) are still running:" + Environment.NewLine + Environment.NewLine
                + nameList + Environment.NewLine + Environment.NewLine
                + "Close the app anyway? Running tasks will not be terminated by tsky.";

            var result = AppMessageBox.Show(this, 
                message,
                "Tasks Still Running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            foreach (var runningTask in _currentConfiguration.Tasks.Where(task => GetTaskRunState(task) == TaskRunState.Running))
            {
                TryCancelTaskExecution(runningTask, out _);
            }
        }

        App.ThemeChanged -= OnThemeChanged;
        _sidebarStatusTimer.Stop();
        _graphRenderDebounceTimer.Stop();
        PersistUiState();
    }

    private void SidebarStatusTimer_Tick(object? sender, EventArgs e)
    {
        _sidebarSpinnerFrameIndex++;
        UpdateSidebarRunningGlyphs();
    }

    private void UpdateSidebarRunningGlyphs()
    {
        if (_sidebarTaskStateGlyphs.Count == 0)
        {
            return;
        }

        var runningGlyph = SidebarRunningFrames[_sidebarSpinnerFrameIndex % SidebarRunningFrames.Length];

        foreach (var pair in _sidebarTaskStateGlyphs)
        {
            var state = _taskRunStates.TryGetValue(pair.Key, out var trackedState)
                ? trackedState
                : TaskRunState.NeverRun;

            if (state != TaskRunState.Running)
            {
                continue;
            }

            foreach (var glyph in pair.Value)
            {
                glyph.Text = runningGlyph;
            }
        }
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

        var persistedSidebarWidth = _uiState.SidebarWidth;
        if (double.IsNaN(persistedSidebarWidth) || persistedSidebarWidth < MinSidebarWidth)
        {
            persistedSidebarWidth = DefaultSidebarWidth;
        }

        persistedSidebarWidth = Math.Min(MaxSidebarWidth, Math.Max(MinSidebarWidth, persistedSidebarWidth));
        SidebarColumnDefinition.Width = new GridLength(persistedSidebarWidth);

        _historyListPaneRatio = ClampHistoryListPaneRatio(_uiState.HistoryListPaneRatio);

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
        _uiState.SidebarWidth = Math.Min(MaxSidebarWidth, Math.Max(MinSidebarWidth, SidebarColumnDefinition.ActualWidth));

        if (_isHistoryDetailsVisible)
        {
            var listHeight = HistoryListRow?.ActualHeight ?? 0;
            var detailsHeight = HistoryDetailsRow?.ActualHeight ?? 0;
            var total = listHeight + detailsHeight;

            if (total > 0)
            {
                _historyListPaneRatio = ClampHistoryListPaneRatio(listHeight / total);
            }
        }

        _uiState.FavoriteGroups = _favoriteGroupOrder
            .Where(groupName => _favoriteGroups.Contains(groupName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _uiState.NonFavoriteGroupsOrder = _nonFavoriteGroupsOrder
            .Where(groupName => !_favoriteGroups.Contains(groupName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _uiState.HistoryListPaneRatio = _historyListPaneRatio;
        UiStateStore.Save(_uiState);
    }

    private void LoadGroupOrderingFromUiState()
    {
        _favoriteGroups.Clear();
        _favoriteGroupOrder.Clear();
        _nonFavoriteGroupsOrder.Clear();

        foreach (var rawGroupName in _uiState.FavoriteGroups ?? [])
        {
            var normalized = NormalizeGroupName(rawGroupName);
            if (_favoriteGroups.Add(normalized))
            {
                _favoriteGroupOrder.Add(normalized);
            }
        }

        foreach (var rawGroupName in _uiState.NonFavoriteGroupsOrder ?? [])
        {
            var normalized = NormalizeGroupName(rawGroupName);
            if (_favoriteGroups.Contains(normalized)
                || _nonFavoriteGroupsOrder.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _nonFavoriteGroupsOrder.Add(normalized);
        }
    }

    private static double ClampHistoryListPaneRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return DefaultHistoryListPaneRatio;
        }

        return Math.Min(MaxHistoryListPaneRatio, Math.Max(MinHistoryListPaneRatio, ratio));
    }

    private static bool IsPointOnAnyScreen(double x, double y)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return x >= screenLeft && x <= screenRight && y >= screenTop && y <= screenBottom;
    }

    private void LoadTaskButtons()
    {
        var currentDirectoryConfig = File.Exists(_currentDirectoryConfigPath)
            ? LoadConfigurationWithValidation(_currentDirectoryConfigPath, "Read workspace tasks.json")
            : new TaskConfiguration();

        var userProfileConfig = File.Exists(_userProfileConfigPath)
            ? LoadConfigurationWithValidation(_userProfileConfigPath, "Read user profile tasks.json")
            : new TaskConfiguration();

        _currentConfiguration = TaskConfiguration.MergeWithOverlay(currentDirectoryConfig, userProfileConfig);

        TaskPanel.AllowDrop = true;
        TaskPanel.DragOver -= TaskPanel_DragOver;
        TaskPanel.Drop -= TaskPanel_Drop;
        TaskPanel.DragLeave -= TaskPanel_DragLeave;
        TaskPanel.DragOver += TaskPanel_DragOver;
        TaskPanel.Drop += TaskPanel_Drop;
        TaskPanel.DragLeave += TaskPanel_DragLeave;

        var groupNames = GetGroupNames();
        GroupsHeaderCountText.Text = $"({groupNames.Count})";
        GroupsSummaryText.Text = $"{groupNames.Count} groups created";
        if (string.IsNullOrWhiteSpace(_selectedGroupName) || !groupNames.Contains(_selectedGroupName, StringComparer.OrdinalIgnoreCase))
        {
            _selectedGroupName = groupNames.FirstOrDefault();
        }

        RebuildGroupsMenu(groupNames);
        RebuildRitualsMenu();
        RebuildSidebarRitualsSection();
        RebuildGroupNavigator(GetGroupSidebarItems(groupNames));
        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            RequestGraphRender("loadTaskButtons");
        }
        else
        {
            RenderSelectedGroupTasks();
        }
        ShowSelectedGroupOutputTabControl();
        RefreshHistoryView();
        ApplyGroupViewState();
        UpdateTaskSurfaceToggleUi();
    }

    private void RebuildGroupNavigator(IReadOnlyList<GroupSidebarItem> groups)
    {
        GroupListPanel.Children.Clear();
        _sidebarTaskStateGlyphs.Clear();

        var searchTerm = GroupSearchTextBox?.Text?.Trim();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            groups = groups
                .Where(group =>
                    group.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || group.Tasks.Any(task => task.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        foreach (var group in groups)
        {
            var groupName = group.Name;
            var isFavorited = _favoriteGroups.Contains(groupName);

            var headerButton = new Button
            {
                Style = (Style)Resources["SidebarTreeGroupHeaderButtonStyle"],
                Tag = groupName,
                Background = string.Equals(groupName, _selectedGroupName, StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A))
                    : Brushes.Transparent
            };

            headerButton.Click += GroupHeaderButton_Click;

            var headerLayout = new DockPanel();

            var chevronText = new TextBlock
            {
                Text = group.IsExpanded ? "▼" : "▶",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)),
                FontSize = 10,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(chevronText, Dock.Left);

            var folderIcon = new TextBlock
            {
                Text = "📁",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA6, 0xB0)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(folderIcon, Dock.Left);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x38)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 22
            };
            DockPanel.SetDock(badge, Dock.Right);
            badge.Child = new TextBlock
            {
                Text = group.TaskCount.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA7, 0x4D)),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var favoriteGlyph = new Border
            {
                Tag = groupName,
                Background = Brushes.Transparent,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = isFavorited ? "Unfavorite group" : "Favorite group",
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "❤",
                    Foreground = isFavorited
                        ? new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))
                        : new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(favoriteGlyph, Dock.Right);
            favoriteGlyph.PreviewMouseLeftButtonDown += FavoriteGroupGlyph_PreviewMouseLeftButtonDown;

            var groupNameText = new TextBlock
            {
                Text = groupName,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)),
                Margin = new Thickness(0, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            headerLayout.Children.Add(chevronText);
            headerLayout.Children.Add(folderIcon);
            headerLayout.Children.Add(favoriteGlyph);
            headerLayout.Children.Add(badge);
            headerLayout.Children.Add(groupNameText);
            headerButton.Content = headerLayout;

            var previewPanel = new StackPanel();
            foreach (var preview in group.Tasks)
            {
                var row = new DockPanel
                {
                    Margin = new Thickness(34, 2, 8, 0),
                    Height = 28
                };

                var stateText = new TextBlock
                {
                    Text = GetStateIcon(preview.State),
                    Width = 16,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = GetStateBrush(preview.State)
                };
                RegisterSidebarTaskStateGlyph(GetTaskIdentityKey(groupName, preview.Name), stateText);
                DockPanel.SetDock(stateText, Dock.Left);

                var taskText = new TextBlock
                {
                    Text = preview.Name,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                    FontSize = 11,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                row.Children.Add(stateText);
                row.Children.Add(taskText);

                var taskRowButton = new Button
                {
                    Tag = new SidebarTaskSelectionTarget(groupName, preview.Name),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Cursor = Cursors.Hand,
                    Content = row
                };
                taskRowButton.Click += SidebarTaskRowButton_Click;

                previewPanel.Children.Add(taskRowButton);
            }

            var expander = new Expander
            {
                Style = (Style)Resources["SidebarTreeExpanderStyle"],
                Header = headerButton,
                Content = previewPanel,
                IsExpanded = group.IsExpanded,
                Tag = groupName
            };

            expander.Expanded += (_, _) => _groupExpandedStates[groupName] = true;
            expander.Collapsed += (_, _) => _groupExpandedStates[groupName] = false;
            GroupListPanel.Children.Add(expander);
        }

        if (GroupListPanel.Children.Count == 0)
        {
            GroupListPanel.Children.Add(new TextBlock
            {
                Text = "No groups match your search.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)),
                Margin = new Thickness(6, 8, 6, 0),
                FontSize = 11
            });
        }
    }

    private void GroupSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RebuildGroupNavigator(GetGroupSidebarItems(GetGroupNames()));
    }

    private void SidebarGroupsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (GroupsTreeScrollViewer is not null)
        {
            GroupsTreeScrollViewer.Visibility = Visibility.Visible;
        }

        if (GroupsSectionChevronText is not null)
        {
            GroupsSectionChevronText.Text = "▼";
        }
    }

    private void SidebarGroupsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (GroupsTreeScrollViewer is not null)
        {
            GroupsTreeScrollViewer.Visibility = Visibility.Collapsed;
        }

        if (GroupsSectionChevronText is not null)
        {
            GroupsSectionChevronText.Text = "▶";
        }
    }

    private void SidebarRitualsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (RitualsSectionChevronText is not null)
        {
            RitualsSectionChevronText.Text = "▼";
        }
    }

    private void SidebarRitualsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (RitualsSectionChevronText is not null)
        {
            RitualsSectionChevronText.Text = "▶";
        }
    }

    private void SidebarTaskRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SidebarTaskSelectionTarget target })
        {
            return;
        }

        _preferGroupDeleteContext = false;
        var targetGroup = NormalizeGroupName(target.GroupName);
        var targetTaskName = TaskIdentity.NormalizeTaskName(target.TaskName);
        var targetTaskKey = TaskIdentity.BuildKey(targetGroup, targetTaskName);

        if (!string.Equals(NormalizeGroupName(_selectedGroupName), targetGroup, StringComparison.OrdinalIgnoreCase))
        {
            SelectGroup(targetGroup);
        }

        var selectedIndex = _currentConfiguration.Tasks.FindIndex(task =>
            string.Equals(TaskIdentity.BuildKey(task), targetTaskKey, StringComparison.OrdinalIgnoreCase));

        if (selectedIndex < 0)
        {
            e.Handled = true;
            return;
        }

        _selectedTaskIndex = selectedIndex;
        ApplyTaskCardSelectionVisuals();
        FocusSelectedTaskCard();
        e.Handled = true;
    }

    private void RegisterSidebarTaskStateGlyph(string taskIdentityKey, TextBlock glyph)
    {
        if (!_sidebarTaskStateGlyphs.TryGetValue(taskIdentityKey, out var glyphs))
        {
            glyphs = [];
            _sidebarTaskStateGlyphs[taskIdentityKey] = glyphs;
        }

        glyphs.Add(glyph);
    }

    private sealed record SidebarTaskSelectionTarget(string GroupName, string TaskName);

    private void GroupExpander_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Expander { Tag: string groupName })
        {
            return;
        }

        _preferGroupDeleteContext = true;
        FocusSidebarPaneForKeyboardCommands();

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

        _preferGroupDeleteContext = true;
        FocusSidebarPaneForKeyboardCommands();
        SelectGroup(selectedGroup);
    }

    private void FocusSidebarPaneForKeyboardCommands()
    {
        if (GroupsTreeScrollViewer is null)
        {
            return;
        }

        GroupsTreeScrollViewer.Focusable = true;
        GroupsTreeScrollViewer.Focus();
        Keyboard.Focus(GroupsTreeScrollViewer);
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

    private void RebuildSidebarRitualsSection()
    {
        SidebarRitualsListPanel.Children.Clear();

        var rituals = _currentConfiguration.Rituals
            .OrderBy(ritual => ritual.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SidebarRitualsCountText.Text = $"({rituals.Count})";

        if (rituals.Count == 0)
        {
            SidebarRitualsListPanel.Children.Add(new TextBlock
            {
                Text = "No rituals defined",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)),
                FontSize = 11,
                Margin = new Thickness(2, 2, 2, 2)
            });

            return;
        }

        foreach (var ritual in rituals)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(0, 1, 0, 3),
                LastChildFill = true,
                Height = 28
            };

            var runButton = new Button
            {
                Content = "▶",
                Tag = ritual,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x38)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0xB5, 0x6A)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Run ritual"
            };
            runButton.Click += SidebarRunRitualButton_Click;
            DockPanel.SetDock(runButton, Dock.Left);

            var editButton = new Button
            {
                Content = "✎",
                Tag = ritual,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x38)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Edit ritual"
            };
            editButton.Click += SidebarEditRitualButton_Click;
            DockPanel.SetDock(editButton, Dock.Left);

            var ritualNameText = new TextBlock
            {
                Text = ritual.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xC8)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = ritual.Description
            };

            row.Children.Add(runButton);
            row.Children.Add(editButton);
            row.Children.Add(ritualNameText);
            SidebarRitualsListPanel.Children.Add(row);
        }
    }

    private async void SidebarRunRitualButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RitualDefinition ritual })
        {
            return;
        }

        await ExecuteRitualAsync(ritual);
    }

    private void SidebarEditRitualButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RitualDefinition ritual })
        {
            return;
        }

        EditRitualMenuEntry_Click(new MenuItem { Tag = ritual }, new RoutedEventArgs());
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
            AppMessageBox.Show(this, "Select a group first.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
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
            AppMessageBox.Show(this, $"No tasks are available in group '{groupName}'.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
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
            AppMessageBox.Show(this, $"Group '{groupName}' has no named tasks to include in a ritual.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        if (!TryPersistConfiguration("Ritual Chains", "Failed to save ritual."))
        {
            return;
        }

        var actionText = wasUpdate ? "updated" : "created";
        AppMessageBox.Show(this, $"Ritual '{ritualName}' {actionText} from group '{groupName}'.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
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
            AppMessageBox.Show(this, "Ritual no longer exists. Reload and try again.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(updated.Name, ritual.Name, StringComparison.OrdinalIgnoreCase) &&
            _currentConfiguration.Rituals.Any(existing => string.Equals(existing.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
        {
            AppMessageBox.Show(this, $"A ritual named '{updated.Name}' already exists.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentConfiguration.Rituals[index] = updated;

        if (!TryPersistConfiguration("Ritual Chains", "Failed to save ritual edits."))
        {
            return;
        }
    }

    private async Task ExecuteRitualAsync(RitualDefinition ritual)
    {
        if (_isRunningRitual)
        {
            AppMessageBox.Show(this, "A ritual is already running.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ritual.Steps.Count == 0)
        {
            AppMessageBox.Show(this, $"Ritual '{ritual.Name}' has no steps.", "Ritual Chains", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isRunningRitual = true;
        var tabContext = CreateOutputTab($"Ritual: {ritual.Name}");
        SetTabRunning(tabContext);
        var dataflowContext = new DataflowContext();

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

                if (!IsTaskEnabled(stepTask))
                {
                    AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: skipped disabled task '{stepTask.Name}'.");
                    continue;
                }

                var effectiveTask = CreateRitualStepExecutionTask(stepTask, step);
                AppendLog(tabContext.OutputTextBox, $"[Ritual] Step {label}: running '{effectiveTask.Name}' (waitForExit={effectiveTask.WaitForExit}).");
                SelectGroup(NormalizeGroupName(effectiveTask.Group));
                var result = await ExecuteTaskAsync(
                    effectiveTask,
                    null,
                    isReplay: false,
                    dataflowContext: dataflowContext,
                    chainStepLabel: $"ritual {label}");

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
            WaitForExit = step.WaitForExit ?? true,
            RequiresElevation = sourceTask.RequiresElevation,
            IgnoreExitCode = sourceTask.IgnoreExitCode
        };
    }

    private void SelectGroup(string groupName)
    {
        DebugTraceLog.Write($"[Group] SelectGroup requested='{groupName}', previous='{_selectedGroupName ?? "(none)"}'");
        _selectedGroupName = groupName;
        var groupNames = GetGroupNames();
        RebuildGroupsMenu(groupNames);
        RebuildGroupNavigator(GetGroupSidebarItems(groupNames));
        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            RequestGraphRender("selectGroup");
        }
        else
        {
            RenderSelectedGroupTasks();
        }
        ShowSelectedGroupOutputTabControl();
        RefreshHistoryView();
        ApplyGroupViewState();
        UpdateTaskSurfaceToggleUi();
        DebugTraceLog.Write($"[Group] SelectGroup applied='{_selectedGroupName ?? "(none)"}', visibleTasks={_visibleTaskIndexes.Count}");
    }

    private void RibbonTaskSurfaceToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return;
        }

        _taskSurfaceViewMode = _taskSurfaceViewMode == TaskSurfaceViewMode.Task
            ? TaskSurfaceViewMode.Graph
            : TaskSurfaceViewMode.Task;

        UpdateTaskSurfaceToggleUi();
        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            RequestGraphRender("toggleToGraph");
        }
        else
        {
            RenderSelectedGroupTasks();
        }
        RecalibrateTaskScrollViewport();
    }

    private void RequestGraphRender(string reason)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            return;
        }

        DebugTraceLog.Write($"[GraphRender] Queued render ({reason})");

        _graphRenderDebounceTimer.Stop();
        _graphRenderDebounceTimer.Start();
    }

    private void GraphRenderDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _graphRenderDebounceTimer.Stop();
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            return;
        }

        DebugTraceLog.Write("[GraphRender] Debounced render executing");
        RenderSelectedGroupTasks();
    }

    private void UpdateTaskSurfaceToggleUi()
    {
        if (RibbonTaskSurfaceToggleButton is null || RibbonTaskSurfaceToggleLabel is null)
        {
            return;
        }

        var hasGroupSelection = !string.IsNullOrWhiteSpace(_selectedGroupName);
        RibbonTaskSurfaceToggleButton.IsEnabled = hasGroupSelection;

        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Task)
        {
            RibbonTaskSurfaceToggleLabel.Text = "Graph";
            RibbonTaskSurfaceToggleButton.ToolTip = "Switch to Graph view";
            return;
        }

        RibbonTaskSurfaceToggleLabel.Text = "Task";
        RibbonTaskSurfaceToggleButton.ToolTip = "Switch to Task view";
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
        if (ViewHistoryMenuItem.IsChecked)
        {
            ViewOutputMenuItem.IsChecked = true;
            _groupOutputVisibility[groupKey] = true;
        }

        _groupHistoryVisibility[groupKey] = ViewHistoryMenuItem.IsChecked;
        ApplyGroupViewState();
    }

    private void ViewOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var groupKey = NormalizeGroupName(_selectedGroupName);
        _groupOutputVisibility[groupKey] = ViewOutputMenuItem.IsChecked;

        if (!ViewOutputMenuItem.IsChecked)
        {
            _groupHistoryVisibility[groupKey] = false;
            ViewHistoryMenuItem.IsChecked = false;
        }

        ApplyGroupViewState();
    }

    private void RibbonHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewHistoryMenuItem.IsEnabled)
        {
            return;
        }

        ViewHistoryMenuItem.IsChecked = true;
        ViewHistoryMenuItem_Click(ViewHistoryMenuItem, new RoutedEventArgs());
    }

    private void ToggleHistoryCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ViewHistoryMenuItem.IsEnabled)
        {
            return;
        }

        ViewHistoryMenuItem.IsChecked = true;
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
            historyVisible = false;
            _groupHistoryVisibility[groupKey] = false;
        }

        if (!_groupOutputVisibility.TryGetValue(groupKey, out var outputVisible))
        {
            outputVisible = true;
            _groupOutputVisibility[groupKey] = true;
        }

        if (!outputVisible && historyVisible)
        {
            historyVisible = false;
            _groupHistoryVisibility[groupKey] = false;
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
            var targetWidth = _uiState.SidebarWidth;
            if (double.IsNaN(targetWidth) || targetWidth < MinSidebarWidth)
            {
                targetWidth = DefaultSidebarWidth;
            }

            targetWidth = Math.Min(MaxSidebarWidth, Math.Max(MinSidebarWidth, targetWidth));
            AnimateColumnWidth(SidebarColumnDefinition, targetWidth);
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
        if (BottomPaneTabControl is null)
        {
            return;
        }

        if (isVisible)
        {
            BottomPaneTabControl.SelectedItem = HistoryPaneTabItem;
            return;
        }

        BottomPaneTabControl.SelectedItem = OutputPaneTabItem;
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
            : (_uiState.OutputHeight >= MinOutputHeight ? _uiState.OutputHeight : DefaultOutputHeight);

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

            if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
            {
                RequestGraphRender("recalibrate");
            }
            else
            {
                ScheduleConnectorRefresh();
            }
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
        UpdateRibbonHistoryButtonState(historyVisible);
        UpdateRibbonRitualsButtonState();
        UpdateTaskSurfaceToggleUi();
    }

    private void UpdateRibbonHistoryButtonState(bool historyVisible)
    {
        if (RibbonHistoryButton is null)
        {
            return;
        }

        RibbonHistoryButton.IsEnabled = ViewHistoryMenuItem.IsEnabled;
        if (HasHistoryForSelectedGroup())
        {
            RibbonHistoryButton.Foreground = RibbonHistoryActiveBrush;
            return;
        }

        // Let the button style control foreground so it matches peer commands (for example, Edit).
        RibbonHistoryButton.ClearValue(Control.ForegroundProperty);
    }

    private bool HasHistoryForSelectedGroup()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return false;
        }

        return _runHistory.Any(entry => string.Equals(entry.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase));
    }

    private void BottomPaneTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl || string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return;
        }

        var groupKey = NormalizeGroupName(_selectedGroupName);
        var isHistorySelected = ReferenceEquals(BottomPaneTabControl.SelectedItem, HistoryPaneTabItem);

        _groupHistoryVisibility[groupKey] = isHistorySelected;
        ViewHistoryMenuItem.IsChecked = isHistorySelected;
        UpdateRibbonHistoryButtonState(isHistorySelected);

        if (_isHandlingBottomPanePortTabSelection)
        {
            return;
        }

        if (!_selectedTaskIndex.HasValue || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
        {
            return;
        }

        try
        {
            _isHandlingBottomPanePortTabSelection = true;

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, InputPortsPaneTabItem))
            {
                OpenInputPortsEditor(_selectedTaskIndex.Value);
            }
            else if (ReferenceEquals(BottomPaneTabControl.SelectedItem, OutputPortsPaneTabItem))
            {
                OpenOutputPortsEditor(_selectedTaskIndex.Value);
            }
        }
        finally
        {
            _isHandlingBottomPanePortTabSelection = false;
        }
    }

    private void UpdateRibbonRitualsButtonState()
    {
        if (RibbonRitualsButton is null)
        {
            return;
        }

        var hasCurrentGroupRitual = TryGetCurrentGroupRitual(out _);

        if (hasCurrentGroupRitual)
        {
            RibbonRitualsButton.Foreground = RibbonHistoryActiveBrush;
            return;
        }

        // Let style control off-state foreground so it matches peer commands.
        RibbonRitualsButton.ClearValue(Control.ForegroundProperty);
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
        UpdateHistoryPreview();
        UpdateHistoryActionState();
    }

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHistoryPreview();
        UpdateHistoryActionState();
    }

    private void ToggleHistoryDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        _isHistoryDetailsVisible = !_isHistoryDetailsVisible;
        ApplyHistoryDetailsVisibility();
    }

    private void ApplyHistoryDetailsVisibility()
    {
        if (HistoryDetailsBorder is null || HistoryDetailsRow is null || HistoryDetailsGapRow is null || HistoryListRow is null || ToggleHistoryDetailsButton is null)
        {
            return;
        }

        if (_isHistoryDetailsVisible)
        {
            HistoryDetailsBorder.Visibility = Visibility.Visible;
            var detailsRatio = 1 - _historyListPaneRatio;
            HistoryListRow.Height = new GridLength(_historyListPaneRatio, GridUnitType.Star);
            HistoryDetailsGapRow.Height = new GridLength(6, GridUnitType.Pixel);
            HistoryDetailsRow.Height = new GridLength(detailsRatio, GridUnitType.Star);
            ToggleHistoryDetailsButton.Content = "Hide Details";
            return;
        }

        HistoryDetailsBorder.Visibility = Visibility.Collapsed;
        HistoryListRow.Height = new GridLength(1, GridUnitType.Star);
        HistoryDetailsGapRow.Height = new GridLength(0, GridUnitType.Pixel);
        HistoryDetailsRow.Height = new GridLength(0, GridUnitType.Pixel);
        ToggleHistoryDetailsButton.Content = "Show Details";
    }

    private void UpdateHistoryPreview()
    {
        if (HistoryDetailsTextBox is null)
        {
            return;
        }

        var selectedItems = HistoryListBox.SelectedItems.OfType<HistoryListItem>().ToList();
        if (selectedItems.Count == 0)
        {
            HistoryDetailsTextBox.Text = "Select a history item to view details.";
            return;
        }

        var firstEntry = selectedItems[0].Entry;
        var builder = new StringBuilder();

        if (selectedItems.Count > 1)
        {
            builder.AppendLine($"{selectedItems.Count} items selected. Showing details for the first selected item.");
            builder.AppendLine();
        }

        var startedLocal = firstEntry.StartedAtUtc.ToLocalTime();
        builder.AppendLine($"Run: {firstEntry.Snapshot.Name}");
        builder.AppendLine($"Group: {firstEntry.Snapshot.Group}");
        builder.AppendLine($"Status: {firstEntry.Outcome}");
        builder.AppendLine($"Started: {startedLocal:yyyy-MM-dd HH:mm:ss}");

        if (firstEntry.CompletedAtUtc.HasValue)
        {
            var completedLocal = firstEntry.CompletedAtUtc.Value.ToLocalTime();
            builder.AppendLine($"Completed: {completedLocal:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Duration: {(completedLocal - startedLocal).TotalSeconds:F1}s");
        }

        if (firstEntry.ExitCode.HasValue)
        {
            builder.AppendLine($"Exit code: {firstEntry.ExitCode.Value}");
        }

        if (firstEntry.IsReplay)
        {
            builder.AppendLine($"Replay: yes (from {firstEntry.ReplayFromRunId ?? "unknown"})");
        }

        builder.AppendLine();
        builder.AppendLine("Task Snapshot");
        builder.AppendLine($"Description: {firstEntry.Snapshot.Description ?? "(none)"}");
        builder.AppendLine($"Runner: {firstEntry.Snapshot.Runner}");
        builder.AppendLine($"Command: {firstEntry.Snapshot.CommandLine}");
        builder.AppendLine($"Working directory: {firstEntry.Snapshot.WorkingDirectory}");
        builder.AppendLine($"Wait for exit: {firstEntry.Snapshot.WaitForExit}");
        builder.AppendLine($"Requires elevation: {firstEntry.Snapshot.RequiresElevation}");
        builder.AppendLine($"Ignore exit code: {firstEntry.Snapshot.IgnoreExitCode}");

        if (firstEntry.Snapshot.Env.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Environment");
            foreach (var env in firstEntry.Snapshot.Env.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"{env.Key}={env.Value}");
            }
        }

        HistoryDetailsTextBox.Text = builder.ToString().TrimEnd();
        HistoryDetailsTextBox.ScrollToHome();
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
            var skippedDisabledCount = 0;

            foreach (var entry in selectedEntries)
            {
                var replayTask = RunHistoryService.ToTask(entry.Snapshot);

                if (IsTaskDisabledByIdentity(replayTask.Group, replayTask.Name))
                {
                    skippedDisabledCount++;
                    continue;
                }

                SelectGroup(NormalizeGroupName(replayTask.Group));

                await ExecuteTaskAsync(replayTask, null, isReplay: true, replayFromEntryId: entry.Id);
            }

            if (skippedDisabledCount > 0)
            {
                AppMessageBox.Show(this, $"Skipped {skippedDisabledCount} disabled task replay(s).", "Replay Selected", MessageBoxButton.OK, MessageBoxImage.Information);
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
        TaskGraphCanvas.Children.Clear();
        TaskGraphEdgeOverlay.Children.Clear();
        _taskCardsByIndex.Clear();
        _graphNodesByIndex.Clear();
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
            ApplyTaskSurfaceModeVisibility();
            return;
        }

        GroupTitleText.Text = _selectedGroupName;
        var snapshotCount = _runHistory.Count(entry => string.Equals(entry.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase));
        GroupMetaText.Text = $"Snapshots in this group: {snapshotCount}";

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

        if (_visibleTaskIndexes.Count == 0)
        {
            _selectedTaskIndex = null;
        }
        else if (_selectedTaskIndex is null || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
        {
            _selectedTaskIndex = _visibleTaskIndexes[0];
        }

        RecomputeInvalidPortBindingIndicators();

        ApplyTaskSurfaceModeVisibility();

        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            RenderGraphView();
            ApplyTaskCardSelectionVisuals();
            return;
        }

        TaskPanel.AllowDrop = !IsCurrentGroupDataflowLocked();

        for (var displayIndex = 0; displayIndex < _visibleTaskIndexes.Count; displayIndex++)
        {
            var index = _visibleTaskIndexes[displayIndex];
            var task = _currentConfiguration.Tasks[index];
            var cardColor = PastelPalette[displayIndex % PastelPalette.Length];
            var card = new Grid
            {
                Width = 195,
                Height = 195,
                Margin = new Thickness(0, 0, 21, 21)
            };
            var dropCueBorder = new Border
            {
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Opacity = 0.45,
                IsHitTestVisible = false
            };
            dropCueBorder.SetResourceReference(Border.BorderBrushProperty, "PrimaryTextBrush");

            var selectionBorder = new Border
            {
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false
            };
            selectionBorder.SetResourceReference(Border.BorderBrushProperty, "ConnectorFocusedBrush");

            var invalidBindingBorder = new Border
            {
                BorderThickness = _invalidPortBindingTaskIndexes.Contains(index) ? new Thickness(2) : new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x55))
            };

            var foregroundBrush = GetReadableTextBrush(cardColor);
            var cardFontFamily = TryFindResource("CardUiFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
            var cardTextContainer = new Grid();
            cardTextContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardTextContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardTextContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var overlayColor = foregroundBrush is SolidColorBrush solidForeground
                ? Color.FromArgb(28, solidForeground.Color.R, solidForeground.Color.G, solidForeground.Color.B)
                : Color.FromArgb(28, 0, 0, 0);

            var nameText = new TextBlock
            {
                Text = task.Name,
                FontWeight = FontWeights.Bold,
                FontFamily = cardFontFamily,
                FontSize = 16,
                Foreground = foregroundBrush,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 22,
                VerticalAlignment = VerticalAlignment.Center,
                MaxHeight = 44
            };

            var titleBar = new Border
            {
                Background = new SolidColorBrush(overlayColor),
                Padding = new Thickness(10, 8, 42, 8),
                Child = nameText
            };
            titleBar.Tag = new TaskCardTitleTooltipContext
            {
                NameText = nameText,
                FullText = task.Name,
                CardColor = cardColor,
                ForegroundBrush = foregroundBrush,
                FontFamily = cardFontFamily
            };
            titleBar.ToolTip = string.Empty;
            titleBar.ToolTipOpening += TaskCardTitleBar_ToolTipOpening;
            Grid.SetRow(titleBar, 0);

            var adminGlyph = new TextBlock
            {
                Text = "\uE72E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = foregroundBrush,
                Opacity = 0.92,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Requires administrator privileges",
                Visibility = task.RequiresElevation ? Visibility.Visible : Visibility.Collapsed,
                Margin = new Thickness(0)
            };

            var importGlyph = new TextBlock
            {
                Text = "I",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = foregroundBrush,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0),
                Visibility = string.IsNullOrWhiteSpace(task.ImportedFromFile) ? Visibility.Collapsed : Visibility.Visible,
                ToolTip = BuildImportedTaskTooltip(task)
            };

            var bottomGlyphs = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var disabledText = new TextBlock
            {
                Text = "DISABLED",
                FontFamily = cardFontFamily,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = foregroundBrush,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Visibility = IsTaskEnabled(task) ? Visibility.Collapsed : Visibility.Visible,
                ToolTip = "This task is disabled and will not run"
            };
            if (disabledText.Visibility == Visibility.Visible)
            {
                bottomGlyphs.Children.Add(disabledText);
            }

            var orderText = new TextBlock
            {
                Text = $"#{displayIndex + 1}",
                FontFamily = cardFontFamily,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = foregroundBrush,
                Opacity = 0.88,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Task order in current group"
            };
            bottomGlyphs.Children.Add(orderText);

            if (importGlyph.Visibility == Visibility.Visible)
            {
                importGlyph.Margin = new Thickness(0, 0, task.RequiresElevation ? 8 : 0, 0);
                bottomGlyphs.Children.Add(importGlyph);
            }

            if (adminGlyph.Visibility == Visibility.Visible)
            {
                bottomGlyphs.Children.Add(adminGlyph);
            }

            var adminBar = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 6, 8, 6),
                Child = bottomGlyphs
            };
            Grid.SetRow(adminBar, 2);

            var descriptionText = new TextBlock
            {
                Text = task.Description ?? string.Empty,
                FontFamily = cardFontFamily,
                FontSize = 12.5,
                Foreground = foregroundBrush,
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Visibility = string.IsNullOrWhiteSpace(task.Description) ? Visibility.Collapsed : Visibility.Visible,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxHeight = 78
            };

            var descriptionContainer = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Child = descriptionText
            };
            Grid.SetRow(descriptionContainer, 1);

            cardTextContainer.Children.Add(titleBar);
            cardTextContainer.Children.Add(descriptionContainer);
            cardTextContainer.Children.Add(adminBar);

            var button = new Button
            {
                Content = cardTextContainer,
                Tag = task,
                Style = (Style)Resources["TaskCardButtonStyle"],
                Background = new SolidColorBrush(cardColor),
                Foreground = foregroundBrush,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Opacity = IsTaskEnabled(task) ? 1.0 : 0.66
            };

            card.Tag = new TaskCardVisual
            {
                DisplayIndex = displayIndex,
                TaskIndex = index,
                DropCueBorder = dropCueBorder,
                SelectionBorder = selectionBorder,
                InvalidBindingBorder = invalidBindingBorder,
                TaskButton = button
            };

            var menuButton = new Button
            {
                Content = "⋮",
                Tag = index,
                Style = (Style)Resources["TaskMenuButtonStyle"],
                Background = new SolidColorBrush(cardColor),
                BorderBrush = new SolidColorBrush(cardColor),
                Foreground = foregroundBrush
            };

            var openInputPortsButton = CreatePortEditorGlyphButton(isInput: true, taskIndex: index);
            var openOutputPortsButton = CreatePortEditorGlyphButton(isInput: false, taskIndex: index);

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
            StackPanel? leftPorts = null;
            if (inputPorts.Count > 0)
            {
                leftPorts = CreatePortStack(inputPorts, isInput: true);
            }

            var outputPorts = GetOutputPortNames(task);
            StackPanel? rightPorts = null;
            if (outputPorts.Count > 0)
            {
                rightPorts = CreatePortStack(outputPorts, isInput: false);
            }

            card.Children.Add(button);
            if (leftPorts is not null)
            {
                card.Children.Add(leftPorts);
            }

            if (rightPorts is not null)
            {
                card.Children.Add(rightPorts);
            }

            card.Children.Add(invalidBindingBorder);
            card.Children.Add(selectionBorder);
            card.Children.Add(dropCueBorder);
            card.Children.Add(openInputPortsButton);
            card.Children.Add(openOutputPortsButton);
            card.Children.Add(menuButton);
            TaskPanel.Children.Add(card);
            _taskCardsByIndex[index] = card;
        }

        ScheduleConnectorRefresh();
        ApplyTaskCardSelectionVisuals();
    }

    private void ApplyTaskSurfaceModeVisibility()
    {
        var isTaskMode = _taskSurfaceViewMode == TaskSurfaceViewMode.Task;

        if (isTaskMode)
        {
            StopGraphPanning();
        }

        TaskPanel.Visibility = isTaskMode ? Visibility.Visible : Visibility.Collapsed;
        TaskConnectorOverlay.Visibility = isTaskMode ? Visibility.Visible : Visibility.Collapsed;
        TaskGraphCanvas.Visibility = isTaskMode ? Visibility.Collapsed : Visibility.Visible;
        TaskGraphEdgeOverlay.Visibility = isTaskMode ? Visibility.Collapsed : Visibility.Visible;
        TaskGraphCanvas.Cursor = isTaskMode ? Cursors.Arrow : (_isGraphPanning ? Cursors.SizeAll : Cursors.Hand);

        UpdateMainScrollBarVisibility("surfaceMode");
    }

    private void RenderGraphView()
    {
        TaskGraphCanvas.Children.Clear();
        TaskGraphEdgeOverlay.Children.Clear();
        _graphNodesByIndex.Clear();

        DebugTraceLog.Write($"[GraphRender] Group='{_selectedGroupName ?? "(none)"}', visibleTasks={_visibleTaskIndexes.Count}");

        if (_visibleTaskIndexes.Count == 0)
        {
            TaskGraphCanvas.Width = TaskWorkspaceSurface.ActualWidth;
            TaskGraphCanvas.Height = 0;
            TaskGraphEdgeOverlay.Width = TaskWorkspaceSurface.ActualWidth;
            TaskGraphEdgeOverlay.Height = 0;
            return;
        }

        var graphNodes = _dataflowGraphService.BuildTaskGraphNodes(_currentConfiguration.Tasks, _visibleTaskIndexes);
        var graphEdges = _dataflowGraphService.BuildTaskGraphEdges(_currentConfiguration.Tasks, _visibleTaskIndexes);

        var nodeSummary = string.Join(", ", graphNodes.Select(node => $"{node.TaskIndex}:{node.TaskName}"));
        DebugTraceLog.Write($"[GraphRender] Nodes=[{nodeSummary}], edges={graphEdges.Count}");

        var positions = ComputeGraphNodePositions(graphNodes, graphEdges, out var surfaceSize);

        TaskGraphCanvas.Width = surfaceSize.Width;
        TaskGraphCanvas.Height = surfaceSize.Height;
        TaskGraphEdgeOverlay.Width = surfaceSize.Width;
        TaskGraphEdgeOverlay.Height = surfaceSize.Height;

        foreach (var edge in graphEdges)
        {
            if (!positions.TryGetValue(edge.SourceTaskIndex, out var sourcePosition)
                || !positions.TryGetValue(edge.TargetTaskIndex, out var targetPosition)
                || edge.SourceTaskIndex == edge.TargetTaskIndex)
            {
                continue;
            }

            var sourceCenter = new Point(sourcePosition.X + GraphNodeWidth, sourcePosition.Y + (GraphNodeHeight / 2));
            var targetCenter = new Point(targetPosition.X, targetPosition.Y + (GraphNodeHeight / 2));
            AddGraphEdge(sourceCenter, targetCenter, edge.IsResolved, edge.Label);
        }

        foreach (var node in graphNodes)
        {
            if (!positions.TryGetValue(node.TaskIndex, out var position))
            {
                continue;
            }

            var nodeButton = CreateGraphNodeButton(node);
            Canvas.SetLeft(nodeButton, position.X);
            Canvas.SetTop(nodeButton, position.Y);
            TaskGraphCanvas.Children.Add(nodeButton);
        }
    }

    private Dictionary<int, Point> ComputeGraphNodePositions(
        IReadOnlyList<DataflowGraphService.TaskGraphNode> nodes,
        IReadOnlyList<DataflowGraphService.TaskGraphEdge> edges,
        out Size surfaceSize)
    {
        var positions = new Dictionary<int, Point>();
        var nodeOrder = nodes.Select((node, order) => new { node.TaskIndex, Order = order }).ToDictionary(item => item.TaskIndex, item => item.Order);

        var visibleWidth = Math.Max(420, TaskScrollViewer.ViewportWidth > 0 ? TaskScrollViewer.ViewportWidth : TaskWorkspaceSurface.ActualWidth);
        var hasEdges = edges.Count > 0;

        if (!hasEdges)
        {
            var columns = Math.Max(1, (int)Math.Floor((visibleWidth - (GraphSurfacePadding * 2) + GraphLayerSpacing) / (GraphNodeWidth + GraphLayerSpacing)));

            for (var i = 0; i < nodes.Count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var x = GraphSurfacePadding + (column * (GraphNodeWidth + GraphLayerSpacing));
                var y = GraphSurfacePadding + (row * (GraphNodeHeight + GraphRowSpacing));
                positions[nodes[i].TaskIndex] = new Point(x, y);
            }

            var rows = (int)Math.Ceiling(nodes.Count / (double)columns);
            var width = Math.Max(visibleWidth, (GraphSurfacePadding * 2) + (columns * GraphNodeWidth) + ((columns - 1) * GraphLayerSpacing));
            var height = (GraphSurfacePadding * 2) + (rows * GraphNodeHeight) + (Math.Max(0, rows - 1) * GraphRowSpacing);
            surfaceSize = new Size(width, height);
            return positions;
        }

        var adjacency = nodes.ToDictionary(node => node.TaskIndex, _ => new List<int>());
        var indegree = nodes.ToDictionary(node => node.TaskIndex, _ => 0);

        foreach (var edge in edges)
        {
            if (!adjacency.ContainsKey(edge.SourceTaskIndex) || !adjacency.ContainsKey(edge.TargetTaskIndex))
            {
                continue;
            }

            adjacency[edge.SourceTaskIndex].Add(edge.TargetTaskIndex);
            indegree[edge.TargetTaskIndex]++;
        }

        var roots = indegree
            .Where(pair => pair.Value == 0)
            .OrderBy(pair => nodeOrder[pair.Key])
            .Select(pair => pair.Key)
            .ToList();

        var queue = new Queue<int>(roots);
        var layerByTask = nodes.ToDictionary(node => node.TaskIndex, _ => 0);
        var visited = new HashSet<int>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited.Add(current);

            foreach (var downstream in adjacency[current])
            {
                layerByTask[downstream] = Math.Max(layerByTask[downstream], layerByTask[current] + 1);
                indegree[downstream]--;
                if (indegree[downstream] == 0)
                {
                    queue.Enqueue(downstream);
                }
            }
        }

        foreach (var node in nodes)
        {
            if (visited.Contains(node.TaskIndex))
            {
                continue;
            }

            layerByTask[node.TaskIndex] = 0;
        }

        var layers = layerByTask
            .GroupBy(pair => pair.Value)
            .OrderBy(group => group.Key)
            .Select(group => group
                .Select(pair => pair.Key)
                .OrderBy(taskIndex => nodeOrder[taskIndex])
                .ToList())
            .ToList();

        var maxLayerCount = layers.Count == 0 ? 1 : layers.Count;
        var maxRows = layers.Count == 0 ? 1 : layers.Max(layer => layer.Count);

        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layer = layers[layerIndex];
            for (var rowIndex = 0; rowIndex < layer.Count; rowIndex++)
            {
                var x = GraphSurfacePadding + (layerIndex * (GraphNodeWidth + GraphLayerSpacing));
                var y = GraphSurfacePadding + (rowIndex * (GraphNodeHeight + GraphRowSpacing));
                positions[layer[rowIndex]] = new Point(x, y);
            }
        }

        var requiredWidth = (GraphSurfacePadding * 2) + (maxLayerCount * GraphNodeWidth) + (Math.Max(0, maxLayerCount - 1) * GraphLayerSpacing);
        var requiredHeight = (GraphSurfacePadding * 2) + (maxRows * GraphNodeHeight) + (Math.Max(0, maxRows - 1) * GraphRowSpacing);
        surfaceSize = new Size(Math.Max(visibleWidth, requiredWidth), requiredHeight);
        return positions;
    }

    private void AddGraphEdge(Point start, Point end, bool isResolved, string? label)
    {
        var edgeStroke = isResolved ? GetResolvedConnectorBrush() : GetUnresolvedConnectorBrush();
        var controlOffset = Math.Max(36, Math.Abs(end.X - start.X) * 0.35);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + controlOffset, start.Y),
            new Point(end.X - controlOffset, end.Y),
            end,
            true));

        var path = new System.Windows.Shapes.Path
        {
            Data = new PathGeometry([figure]),
            Stroke = edgeStroke,
            StrokeThickness = isResolved ? 3.2 : 2.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.9,
            ToolTip = string.IsNullOrWhiteSpace(label)
                ? (isResolved ? "Resolved dataflow edge" : "Unresolved dataflow edge")
                : (isResolved ? $"Resolved: {label}" : $"Unresolved: {label}")
        };

        if (!isResolved)
        {
            path.StrokeDashArray = [6, 4];
        }

        var direction = new Vector(end.X - start.X, end.Y - start.Y);
        if (direction.Length > 0.001)
        {
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            var arrowLength = 11;
            var arrowWidth = 5.5;

            var arrowTip = end;
            var arrowBase = arrowTip - (direction * arrowLength);
            var arrowLeft = arrowBase + (normal * arrowWidth);
            var arrowRight = arrowBase - (normal * arrowWidth);

            var arrow = new Polygon
            {
                Fill = edgeStroke,
                Opacity = 0.9,
                Points = new PointCollection([arrowTip, arrowLeft, arrowRight])
            };

            TaskGraphEdgeOverlay.Children.Add(path);
            TaskGraphEdgeOverlay.Children.Add(arrow);
            return;
        }

        TaskGraphEdgeOverlay.Children.Add(path);
    }

    private Button CreateGraphNodeButton(DataflowGraphService.TaskGraphNode graphNode)
    {
        var titleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(graphNode.TaskName) ? "(Unnamed Task)" : graphNode.TaskName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 38
        };

        var stateText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.Medium
        };

        var contentPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        contentPanel.Children.Add(titleText);
        contentPanel.Children.Add(stateText);

        var runningBorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xAE, 0xFF));

        var nodeBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = contentPanel
        };

        var selectionBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            BorderBrush = GetFocusedConnectorBrush(),
            Child = nodeBorder
        };

        var nodeButton = new Button
        {
            Width = GraphNodeWidth,
            Height = GraphNodeHeight,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ToolTip = string.IsNullOrWhiteSpace(graphNode.Description) ? graphNode.TaskName : graphNode.Description,
            Tag = graphNode.TaskIndex
        };

        DebugTraceLog.Write($"[GraphNode] Create button taskIndex={graphNode.TaskIndex}, taskName='{graphNode.TaskName}', group='{_selectedGroupName ?? "(none)"}'");

        nodeButton.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        nodeButton.Content = selectionBorder;

        nodeButton.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (nodeButton.Tag is not int nodeTaskIndex)
            {
                return;
            }

            _selectedTaskIndex = nodeTaskIndex;
            ApplyTaskCardSelectionVisuals();
            OpenTaskContextMenu(nodeButton, nodeTaskIndex);
            e.Handled = true;
        };

        var visual = new GraphNodeVisual
        {
            TaskIndex = graphNode.TaskIndex,
            SelectionBorder = selectionBorder,
            NodeBorder = nodeBorder,
            RunningBorderBrush = runningBorderBrush,
            StateText = stateText,
            NodeButton = nodeButton
        };

        _graphNodesByIndex[graphNode.TaskIndex] = visual;
        ApplyGraphNodeStateVisual(graphNode.TaskIndex);
        return nodeButton;
    }

    private void TaskGraphCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            return;
        }

        var canvasPoint = e.GetPosition(TaskGraphCanvas);
        if (TryResolveGraphNodeIndexAtPoint(canvasPoint, out var nodeTaskIndex))
        {
            _selectedTaskIndex = nodeTaskIndex;
            ApplyTaskCardSelectionVisuals();
            _preferGroupDeleteContext = false;
            _graphPointerDownTaskIndex = nodeTaskIndex;
            DebugTraceLog.Write($"[GraphInput] MouseDown on node index={nodeTaskIndex}, point=({canvasPoint.X:F1},{canvasPoint.Y:F1}), group='{_selectedGroupName ?? "(none)"}', sourceType='{e.OriginalSource?.GetType().Name ?? "(null)"}'");
        }
        else
        {
            _graphPointerDownTaskIndex = null;
            DebugTraceLog.Write($"[GraphInput] MouseDown on canvas/background point=({canvasPoint.X:F1},{canvasPoint.Y:F1}), group='{_selectedGroupName ?? "(none)"}', sourceType='{e.OriginalSource?.GetType().Name ?? "(null)"}'");
        }

        _graphPointerDownPoint = e.GetPosition(TaskScrollViewer);
        _graphPointerWasDrag = false;
        _graphPointerActive = true;
    }

    private void TaskGraphCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph || !_graphPointerActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(TaskScrollViewer);

        if (!_isGraphPanning)
        {
            var delta = current - _graphPointerDownPoint;
            if (Math.Abs(delta.X) < GraphDragThreshold && Math.Abs(delta.Y) < GraphDragThreshold)
            {
                return;
            }

            _graphPointerWasDrag = true;
            StartGraphPanning(current);
            DebugTraceLog.Write($"[GraphInput] DragStart fromPoint=({_graphPointerDownPoint.X:F1},{_graphPointerDownPoint.Y:F1}) current=({current.X:F1},{current.Y:F1}) downTaskIndex={_graphPointerDownTaskIndex?.ToString() ?? "(none)"}");
        }

        UpdateGraphPanning(current);
        e.Handled = true;
    }

    private void TaskGraphCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            ClearGraphPointerInteraction();
            return;
        }

        var canvasPoint = e.GetPosition(TaskGraphCanvas);
        var taskToRun = (!_graphPointerWasDrag && TryResolveGraphNodeIndexAtPoint(canvasPoint, out var upTaskIndex))
            ? upTaskIndex
            : (!_graphPointerWasDrag && _graphPointerDownTaskIndex.HasValue
                ? _graphPointerDownTaskIndex.Value
                : (int?)null);

        DebugTraceLog.Write($"[GraphInput] MouseUp wasDrag={_graphPointerWasDrag}, point=({canvasPoint.X:F1},{canvasPoint.Y:F1}), downTaskIndex={_graphPointerDownTaskIndex?.ToString() ?? "(none)"}, resolvedRunTarget={taskToRun?.ToString() ?? "(none)"}, group='{_selectedGroupName ?? "(none)"}'");

        StopGraphPanning();
        ClearGraphPointerInteraction();

        if (taskToRun.HasValue)
        {
            _ = RunTaskByIndexAsync(taskToRun.Value);
            e.Handled = true;
        }
    }

    private void TaskGraphCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        DebugTraceLog.Write("[GraphInput] LostMouseCapture");
        StopGraphPanning();
        ClearGraphPointerInteraction();
    }

    private void StartGraphPanning(Point startPoint)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            return;
        }

        if (!Mouse.Capture(TaskGraphCanvas, CaptureMode.SubTree))
        {
            _isGraphPanning = false;
            TaskGraphCanvas.Cursor = Cursors.Hand;
            DebugTraceLog.Write("[GraphPan] Capture failed");
            return;
        }

        _isGraphPanning = true;
        _graphPanStartPoint = startPoint;
        _graphPanStartHorizontalOffset = TaskScrollViewer.HorizontalOffset;
        _graphPanStartVerticalOffset = TaskScrollViewer.VerticalOffset;
        TaskGraphCanvas.Cursor = Cursors.SizeAll;
        DebugTraceLog.Write($"[GraphPan] Start point=({startPoint.X:F1},{startPoint.Y:F1}) offsets=({_graphPanStartHorizontalOffset:F1},{_graphPanStartVerticalOffset:F1})");
    }

    private void UpdateGraphPanning(Point current)
    {
        if (!_isGraphPanning)
        {
            return;
        }

        var deltaX = current.X - _graphPanStartPoint.X;
        var deltaY = current.Y - _graphPanStartPoint.Y;

        TaskScrollViewer.ScrollToHorizontalOffset(_graphPanStartHorizontalOffset - deltaX);
        TaskScrollViewer.ScrollToVerticalOffset(_graphPanStartVerticalOffset - deltaY);
        DebugTraceLog.Write($"[GraphPan] Move current=({current.X:F1},{current.Y:F1}) delta=({deltaX:F1},{deltaY:F1}) offsets=({TaskScrollViewer.HorizontalOffset:F1},{TaskScrollViewer.VerticalOffset:F1})");
    }

    private void StopGraphPanning()
    {
        if (!_isGraphPanning)
        {
            if (Mouse.Captured == TaskGraphCanvas)
            {
                Mouse.Capture(null);
            }

            TaskGraphCanvas.Cursor = _taskSurfaceViewMode == TaskSurfaceViewMode.Graph ? Cursors.Hand : Cursors.Arrow;
            DebugTraceLog.Write("[GraphPan] Stop ignored (not active)");
            return;
        }

        _isGraphPanning = false;
        if (TaskGraphCanvas.IsMouseCaptured)
        {
            TaskGraphCanvas.ReleaseMouseCapture();
        }
        else if (Mouse.Captured == TaskGraphCanvas)
        {
            Mouse.Capture(null);
        }

        TaskGraphCanvas.Cursor = _taskSurfaceViewMode == TaskSurfaceViewMode.Graph ? Cursors.Hand : Cursors.Arrow;
        DebugTraceLog.Write($"[GraphPan] Stop offsets=({TaskScrollViewer.HorizontalOffset:F1},{TaskScrollViewer.VerticalOffset:F1})");
    }

    private void ClearGraphPointerInteraction()
    {
        DebugTraceLog.Write($"[GraphInput] ClearPointerState downTaskIndex={_graphPointerDownTaskIndex?.ToString() ?? "(none)"}, wasDrag={_graphPointerWasDrag}, active={_graphPointerActive}");
        _graphPointerDownTaskIndex = null;
        _graphPointerWasDrag = false;
        _graphPointerActive = false;

        if (Mouse.Captured == TaskGraphCanvas)
        {
            Mouse.Capture(null);
        }

        TaskGraphCanvas.Cursor = _taskSurfaceViewMode == TaskSurfaceViewMode.Graph ? Cursors.Hand : Cursors.Arrow;
    }

    private bool TryResolveGraphNodeIndexAtPoint(Point pointOnCanvas, out int taskIndex)
    {
        for (var i = TaskGraphCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (TaskGraphCanvas.Children[i] is not Button button || button.Tag is not int candidateIndex)
            {
                continue;
            }

            var left = Canvas.GetLeft(button);
            var top = Canvas.GetTop(button);
            var width = button.ActualWidth > 0 ? button.ActualWidth : button.Width;
            var height = button.ActualHeight > 0 ? button.ActualHeight : button.Height;

            if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0)
            {
                continue;
            }

            var bounds = new Rect(left, top, width, height);
            if (!bounds.Contains(pointOnCanvas))
            {
                continue;
            }

            taskIndex = candidateIndex;
            return true;
        }

        taskIndex = -1;
        return false;
    }

    private void MainWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Graph)
        {
            return;
        }

        if (!_isGraphPanning && !_graphPointerActive)
        {
            return;
        }

        StopGraphPanning();
        ClearGraphPointerInteraction();
    }

    private static T? FindVisualAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ApplyGraphNodeStateVisual(int taskIndex)
    {
        if (!_graphNodesByIndex.TryGetValue(taskIndex, out var visual) || !TryGetTask(taskIndex, out var task))
        {
            return;
        }

        var state = GetTaskRunState(task);
        var stateLabel = state switch
        {
            TaskRunState.Running => "Running",
            TaskRunState.Succeeded => "Last run: Succeeded",
            TaskRunState.Failed => "Last run: Failed",
            _ => "Ready"
        };

        if (!IsTaskEnabled(task))
        {
            stateLabel = "Disabled";
        }

        var stateBrush = state switch
        {
            TaskRunState.Running => new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            TaskRunState.Succeeded => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            TaskRunState.Failed => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            _ => (Brush)Resources["SecondaryTextBrush"]
        };

        var background = IsTaskEnabled(task)
            ? new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x35))
            : new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x24));

        var isEnabled = IsTaskEnabled(task);
        var isRunning = isEnabled && state == TaskRunState.Running;
        var nodeBorderBrush = isRunning
            ? (Brush)visual.RunningBorderBrush
            : isEnabled
                ? new SolidColorBrush(Color.FromRgb(0x3D, 0x4D, 0x5D))
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x4D, 0x50));
        var nodeBorderThickness = isRunning ? new Thickness(2) : new Thickness(1);

        if (isRunning)
        {
            if (!visual.IsRunningBorderPulseActive)
            {
                visual.RunningBorderBrush.Opacity = 1;
                visual.RunningBorderBrush.BeginAnimation(
                    SolidColorBrush.OpacityProperty,
                    new DoubleAnimation
                    {
                        From = 0.72,
                        To = 1.0,
                        Duration = TimeSpan.FromMilliseconds(680),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    });
                visual.IsRunningBorderPulseActive = true;
            }
        }
        else if (visual.IsRunningBorderPulseActive)
        {
            visual.RunningBorderBrush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
            visual.RunningBorderBrush.Opacity = 1;
            visual.IsRunningBorderPulseActive = false;
        }

        visual.NodeBorder.Background = background;
        visual.NodeBorder.BorderBrush = nodeBorderBrush;
        visual.NodeBorder.BorderThickness = nodeBorderThickness;
        visual.NodeButton.Opacity = isEnabled ? 1 : 0.72;
        visual.StateText.Text = stateLabel;
        visual.StateText.Foreground = stateBrush;

        var isSelected = _selectedTaskIndex.HasValue && _selectedTaskIndex.Value == taskIndex;
        visual.SelectionBorder.BorderThickness = isSelected ? new Thickness(2) : new Thickness(0);
    }

    private async Task RunTaskByIndexAsync(int taskIndex)
    {
        StopGraphPanning();
        ClearGraphPointerInteraction();

        DebugTraceLog.Write($"[TaskRun] RunTaskByIndex requested index={taskIndex}, selectedGroup='{_selectedGroupName ?? "(none)"}'");

        if (!TryGetTask(taskIndex, out var selectedTask))
        {
            DebugTraceLog.Write($"[TaskRun] RunTaskByIndex failed: invalid index={taskIndex}");
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!IsTaskEnabled(selectedTask))
        {
            DebugTraceLog.Write($"[TaskRun] RunTaskByIndex blocked: disabled task index={taskIndex}, name='{selectedTask.Name}', group='{selectedTask.Group}'");
            AppMessageBox.Show(this, $"Task '{selectedTask.Name}' is disabled.", "Task Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _selectedTaskIndex = taskIndex;
        ApplyTaskCardSelectionVisuals();
        DebugTraceLog.Write($"[TaskRun] Executing task index={taskIndex}, name='{selectedTask.Name}', group='{selectedTask.Group}', key='{TaskIdentity.BuildKey(selectedTask)}'");
        await ExecuteTaskAsync(selectedTask, null, isReplay: false);
        DebugTraceLog.Write($"[TaskRun] ExecuteTaskAsync completed index={taskIndex}, name='{selectedTask.Name}', group='{selectedTask.Group}'");
    }

    private void OpenTaskContextMenu(Button anchorButton, int taskIndex)
    {
        var menu = CreateTaskContextMenu(taskIndex);
        anchorButton.ContextMenu = menu;
        menu.PlacementTarget = anchorButton;
        menu.IsOpen = true;
    }

    private ContextMenu CreateTaskContextMenu(int taskIndex)
    {
        var menu = new ContextMenu();

        var editItem = new MenuItem { Header = "Edit" };
        editItem.Click += (_, _) => EditTask(taskIndex);

        var toggleItem = new MenuItem
        {
            Header = IsTaskEnabled(_currentConfiguration.Tasks[taskIndex]) ? "Disable" : "Enable"
        };
        toggleItem.Click += (_, _) => ToggleTaskEnabled(taskIndex);

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

            var replayTask = RunHistoryService.ToTask(replayEntry.Snapshot);
            if (IsTaskDisabledByIdentity(replayTask.Group, replayTask.Name))
            {
                AppMessageBox.Show(this, $"Task '{replayTask.Name}' is disabled and cannot be replayed.", "Replay Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectGroup(NormalizeGroupName(replayTask.Group));

            await ExecuteTaskAsync(replayTask, null, isReplay: true, replayFromEntryId: replayEntry.Id);
        };

        var runChainItem = new MenuItem { Header = "Run Chain (Same Group)" };
        runChainItem.IsEnabled = _visibleTaskIndexes.Any(index => IsTaskEnabled(_currentConfiguration.Tasks[index]));
        runChainItem.Click += async (_, _) =>
        {
            if (!IsValidTaskIndex(taskIndex))
            {
                AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RunTaskChainFromTaskAsync(taskIndex);
        };

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteTask(taskIndex);

        menu.Items.Add(editItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(replayItem);
        menu.Items.Add(runChainItem);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private Button CreatePortEditorGlyphButton(bool isInput, int taskIndex)
    {
        var button = new Button
        {
            Content = "+",
            Width = 22,
            Height = 22,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(205, 12, 24, 36)),
            Foreground = isInput
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0xFF, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)),
            BorderBrush = isInput
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0xFF, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)),
            BorderThickness = new Thickness(1.3),
            Padding = new Thickness(0),
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = isInput ? new Thickness(8, 0, 0, 8) : new Thickness(0, 0, 8, 8),
            ToolTip = isInput ? "Add/Edit Input Ports" : "Add/Edit Output Ports"
        };

        button.Click += (_, _) =>
        {
            if (isInput)
            {
                OpenInputPortsEditor(taskIndex);
            }
            else
            {
                OpenOutputPortsEditor(taskIndex);
            }
        };

        return button;
    }

    private void TaskCardTitleBar_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not Border { Tag: TaskCardTitleTooltipContext context } titleBar)
        {
            return;
        }

        if (!ShouldShowTaskCardTitleTooltip(context.NameText, context.FullText))
        {
            titleBar.ToolTip = string.Empty;
            e.Handled = true;
            return;
        }

        titleBar.ToolTip = CreateTaskCardTitleToolTip(context);
    }

    private static bool ShouldShowTaskCardTitleTooltip(TextBlock nameText, string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText)
            || nameText.ActualWidth <= 0
            || nameText.ActualHeight <= 0)
        {
            return false;
        }

        var measuringBlock = new TextBlock
        {
            Text = fullText,
            FontFamily = nameText.FontFamily,
            FontStyle = nameText.FontStyle,
            FontWeight = nameText.FontWeight,
            FontStretch = nameText.FontStretch,
            FontSize = nameText.FontSize,
            LineStackingStrategy = nameText.LineStackingStrategy,
            LineHeight = nameText.LineHeight,
            TextWrapping = nameText.TextWrapping,
            TextTrimming = TextTrimming.None,
            Width = nameText.ActualWidth
        };

        measuringBlock.Measure(new Size(nameText.ActualWidth, double.PositiveInfinity));
        return measuringBlock.DesiredSize.Height > nameText.ActualHeight + 0.5;
    }

    private static ToolTip CreateTaskCardTitleToolTip(TaskCardTitleTooltipContext context)
    {
        var borderBrushColor = Color.FromArgb(220, context.CardColor.R, context.CardColor.G, context.CardColor.B);

        return new ToolTip
        {
            Background = new SolidColorBrush(context.CardColor),
            Foreground = context.ForegroundBrush,
            BorderBrush = new SolidColorBrush(borderBrushColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 380,
            Content = new TextBlock
            {
                Text = context.FullText,
                FontFamily = context.FontFamily,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private bool IsCurrentGroupDataflowLocked()
    {
        return _taskCatalogService.IsGroupDataflowLocked(_currentConfiguration.Tasks, _visibleTaskIndexes);
    }

    private void OpenInputPortsEditor(int taskIndex, bool selectTab = true)
    {
        if (!TryGetTask(taskIndex, out var task))
        {
            return;
        }

        _portEditorTaskIndex = taskIndex;
        InputPortsEditorTitleText.Text = $"Input Ports - {task.Name}";
        InputPortsRowsPanel.Children.Clear();

        var portNames = new HashSet<string>(task.Inputs.Keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var key in task.InputBindings.Keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()))
        {
            portNames.Add(key);
        }

        foreach (var portName in portNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var defaultValue = task.Inputs.TryGetValue(portName, out var value) ? value : string.Empty;
            var bindingValue = task.InputBindings.TryGetValue(portName, out var binding) ? binding : string.Empty;
            InputPortsRowsPanel.Children.Add(CreateInputPortRowControl(portName, defaultValue, bindingValue));
        }

        if (InputPortsRowsPanel.Children.Count == 0)
        {
            InputPortsRowsPanel.Children.Add(CreateInputPortRowControl(string.Empty, string.Empty, string.Empty));
        }

        if (selectTab)
        {
            BottomPaneTabControl.SelectedItem = InputPortsPaneTabItem;
        }
    }

    private void OpenOutputPortsEditor(int taskIndex, bool selectTab = true)
    {
        if (!TryGetTask(taskIndex, out var task))
        {
            return;
        }

        _portEditorTaskIndex = taskIndex;
        OutputPortsEditorTitleText.Text = $"Output Ports - {task.Name}";
        OutputPortsRowsPanel.Children.Clear();

        foreach (var pair in task.Outputs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            OutputPortsRowsPanel.Children.Add(CreateOutputPortRowControl(pair.Key, pair.Value));
        }

        if (OutputPortsRowsPanel.Children.Count == 0)
        {
            OutputPortsRowsPanel.Children.Add(CreateOutputPortRowControl(string.Empty, string.Empty));
        }

        if (selectTab)
        {
            BottomPaneTabControl.SelectedItem = OutputPortsPaneTabItem;
        }
    }

    private Grid CreateInputPortRowControl(string name, string defaultValue, string bindingValue)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6), Tag = new PortEditorRowMarker { IsInput = true } };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = CreatePortEditorTextBox(name, "Input name");
        var defaultBox = CreatePortEditorTextBox(defaultValue, "Default value");
        var bindingBox = CreatePortEditorTextBox(bindingValue, "Binding (optional)");

        var removeButton = CreateRemovePortRowButton(() => InputPortsRowsPanel.Children.Remove(row));

        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(defaultBox, 1);
        Grid.SetColumn(bindingBox, 2);
        Grid.SetColumn(removeButton, 3);

        row.Children.Add(nameBox);
        row.Children.Add(defaultBox);
        row.Children.Add(bindingBox);
        row.Children.Add(removeButton);
        return row;
    }

    private Grid CreateOutputPortRowControl(string name, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6), Tag = new PortEditorRowMarker { IsInput = false } };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = CreatePortEditorTextBox(name, "Output name");
        var valueBox = CreatePortEditorTextBox(value, "Output value template");
        var removeButton = CreateRemovePortRowButton(() => OutputPortsRowsPanel.Children.Remove(row));

        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(removeButton, 2);

        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(removeButton);
        return row;
    }

    private sealed class PortEditorRowMarker
    {
        public bool IsInput { get; init; }
    }

    private static TextBox CreatePortEditorTextBox(string text, string toolTip)
    {
        var textBox = new TextBox
        {
            Text = text,
            Margin = new Thickness(0, 0, 6, 0),
            Height = 34,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = toolTip,
            FontFamily = new FontFamily("Cascadia Mono")
        };

        textBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        textBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        textBox.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
        return textBox;
    }

    private static Button CreateRemovePortRowButton(Action onClick)
    {
        var button = new Button
        {
            Content = "-",
            Width = 28,
            Height = 30,
            Margin = new Thickness(0),
            Cursor = Cursors.Hand
        };

        button.SetResourceReference(FrameworkElement.StyleProperty, "HistoryActionButtonStyle");

        button.Click += (_, _) => onClick();
        return button;
    }

    private void AddInputPortRowButton_Click(object sender, RoutedEventArgs e)
    {
        InputPortsRowsPanel.Children.Add(CreateInputPortRowControl(string.Empty, string.Empty, string.Empty));
    }

    private void AddOutputPortRowButton_Click(object sender, RoutedEventArgs e)
    {
        OutputPortsRowsPanel.Children.Add(CreateOutputPortRowControl(string.Empty, string.Empty));
    }

    private void SaveInputPortsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_portEditorTaskIndex.HasValue || !TryGetTask(_portEditorTaskIndex.Value, out var task))
        {
            return;
        }

        if (!TryCollectInputPortRows(out var inputs, out var bindings, out var error))
        {
            LogPortEditorError(error);
            return;
        }

        if (!ValidateInputBindingsAgainstPreviousTasks(_portEditorTaskIndex.Value, bindings, out error))
        {
            LogPortEditorError(error);
            return;
        }

        task.Inputs = inputs;
        task.InputBindings = bindings;
        SaveTasksAndRefresh("Save Input Ports");
        OpenInputPortsEditor(_portEditorTaskIndex.Value);
    }

    private void SaveOutputPortsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_portEditorTaskIndex.HasValue || !TryGetTask(_portEditorTaskIndex.Value, out var task))
        {
            return;
        }

        if (!TryCollectOutputPortRows(out var outputs, out var error))
        {
            LogPortEditorError(error);
            return;
        }

        task.Outputs = outputs;
        SaveTasksAndRefresh("Save Output Ports");
        OpenOutputPortsEditor(_portEditorTaskIndex.Value);
    }

    private void LogPortEditorError(string message)
    {
        var tabContext = CreateOutputTab("Port Validation");
        var output = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        AppendLog(output, $"[Ports] Validation error: {message}");
        SetTabStopped(tabContext, false);
        BottomPaneTabControl.SelectedItem = OutputPaneTabItem;
    }

    private bool TryCollectInputPortRows(out Dictionary<string, string> inputs, out Dictionary<string, string> bindings, out string error)
    {
        inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        foreach (var child in InputPortsRowsPanel.Children)
        {
            if (child is not Grid row || row.Children.Count < 3)
            {
                continue;
            }

            var name = (row.Children[0] as TextBox)?.Text?.Trim() ?? string.Empty;
            var defaultValue = (row.Children[1] as TextBox)?.Text ?? string.Empty;
            var bindingValue = (row.Children[2] as TextBox)?.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (string.IsNullOrWhiteSpace(defaultValue) && string.IsNullOrWhiteSpace(bindingValue))
                {
                    continue;
                }

                error = "Input row has value/binding but no input name.";
                return false;
            }

            if (inputs.ContainsKey(name))
            {
                error = $"Duplicate input name '{name}'.";
                return false;
            }

            inputs[name] = defaultValue;

            if (!string.IsNullOrWhiteSpace(bindingValue))
            {
                bindings[name] = bindingValue;
            }
        }

        return true;
    }

    private bool TryCollectOutputPortRows(out Dictionary<string, string> outputs, out string error)
    {
        outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        foreach (var child in OutputPortsRowsPanel.Children)
        {
            if (child is not Grid row || row.Children.Count < 2)
            {
                continue;
            }

            var name = (row.Children[0] as TextBox)?.Text?.Trim() ?? string.Empty;
            var value = (row.Children[1] as TextBox)?.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                error = "Output row has value but no output name.";
                return false;
            }

            if (outputs.ContainsKey(name))
            {
                error = $"Duplicate output name '{name}'.";
                return false;
            }

            outputs[name] = value;
        }

        return true;
    }

    private bool ValidateInputBindingsAgainstPreviousTasks(int taskIndex, IReadOnlyDictionary<string, string> bindings, out string error)
    {
        return _dataflowGraphService.ValidateInputBindingsAgainstPreviousTasks(
            _currentConfiguration.Tasks,
            _visibleTaskIndexes,
            taskIndex,
            bindings,
            out error);
    }

    private void RecomputeInvalidPortBindingIndicators()
    {
        _invalidPortBindingTaskIndexes.Clear();
        foreach (var taskIndex in _dataflowGraphService.ComputeInvalidPortBindingTaskIndexes(_currentConfiguration.Tasks, _visibleTaskIndexes))
        {
            _invalidPortBindingTaskIndexes.Add(taskIndex);
        }
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

        Panel.SetZIndex(stack, 15);

        foreach (var portName in portNames)
        {
            var dot = new Ellipse
            {
                ToolTip = portName,
                Width = 12,
                Height = 12,
                StrokeThickness = 1.5,
                Margin = new Thickness(0, 2, 0, 2),
                Fill = new SolidColorBrush(isInput ? Color.FromRgb(0x7C, 0xFF, 0x00) : Color.FromRgb(0x00, 0xE5, 0xFF)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x12, 0x1E, 0x2A))
            };

            stack.Children.Add(dot);
        }

        return stack;
    }

    private void ScheduleConnectorRefresh()
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Task)
        {
            return;
        }

        Dispatcher.BeginInvoke(RedrawDataflowConnectors, DispatcherPriority.Loaded);
    }

    private void RedrawDataflowConnectors()
    {
        if (_taskSurfaceViewMode != TaskSurfaceViewMode.Task)
        {
            TaskConnectorOverlay.Children.Clear();
            return;
        }

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
        var cardCoverGeometry = BuildTaskCardCoverGeometry();

        var connectorItems = _dataflowGraphService.BuildConnectorComputations(_currentConfiguration.Tasks, _visibleTaskIndexes);

        foreach (var connectorItem in connectorItems)
        {
            if (!_taskCardsByIndex.TryGetValue(connectorItem.TargetTaskIndex, out var targetCard))
            {
                continue;
            }

            var targetTask = _currentConfiguration.Tasks[connectorItem.TargetTaskIndex];
            var targetInputNames = GetInputPortNames(targetTask);
            var targetPoint = GetCardPortPoint(targetCard, isOutput: false, targetInputNames, connectorItem.TargetInputName);

            if (!connectorItem.SourceTaskIndex.HasValue
                || string.IsNullOrWhiteSpace(connectorItem.SourceOutputName)
                || !_taskCardsByIndex.TryGetValue(connectorItem.SourceTaskIndex.Value, out var sourceCard))
            {
                var fallbackStart = new Point(Math.Max(0, targetPoint.X - 56), targetPoint.Y);
                AddConnector(
                    fallbackStart,
                    targetPoint,
                    resolved: false,
                    sourceTaskName: connectorItem.SourceTaskName,
                    sourceOutputName: connectorItem.SourceOutputName,
                    targetTaskName: connectorItem.TargetTaskName,
                    targetInputName: connectorItem.TargetInputName,
                    bindingReference: connectorItem.BindingReference,
                    statusDetail: connectorItem.StatusDetail,
                    cardCoverGeometry);

                continue;
            }

            var sourceTask = _currentConfiguration.Tasks[connectorItem.SourceTaskIndex.Value];
            var sourcePortsForAnchor = GetOutputPortNames(sourceTask);
            var sourcePoint = GetCardPortPoint(sourceCard, isOutput: true, sourcePortsForAnchor, connectorItem.SourceOutputName!);

            AddConnector(
                sourcePoint,
                targetPoint,
                connectorItem.Resolved,
                connectorItem.SourceTaskName,
                connectorItem.SourceOutputName,
                connectorItem.TargetTaskName,
                connectorItem.TargetInputName,
                connectorItem.BindingReference,
                connectorItem.StatusDetail,
                cardCoverGeometry);
        }
    }

    private Geometry? BuildTaskCardCoverGeometry()
    {
        var geometryGroup = new GeometryGroup();

        foreach (var card in _taskCardsByIndex.Values)
        {
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = card.TranslatePoint(new Point(0, 0), TaskConnectorOverlay);
            if (double.IsNaN(topLeft.X) || double.IsNaN(topLeft.Y)
                || double.IsInfinity(topLeft.X) || double.IsInfinity(topLeft.Y))
            {
                continue;
            }

            geometryGroup.Children.Add(new RectangleGeometry(new Rect(topLeft, new Size(card.ActualWidth, card.ActualHeight))));
        }

        if (geometryGroup.Children.Count == 0)
        {
            return null;
        }

        return geometryGroup;
    }

    private Point GetCardPortPoint(Grid card, bool isOutput, IReadOnlyList<string> portNames, string portName)
    {
        var portStack = FindPortStack(card, isOutput);
        if (portStack is not null)
        {
            var dotPortIndex = -1;
            for (var i = 0; i < portNames.Count; i++)
            {
                if (string.Equals(portNames[i], portName, StringComparison.OrdinalIgnoreCase))
                {
                    dotPortIndex = i;
                    break;
                }
            }

            if (dotPortIndex < 0)
            {
                dotPortIndex = 0;
            }

            if (dotPortIndex >= 0 && dotPortIndex < portStack.Children.Count && portStack.Children[dotPortIndex] is Ellipse dot)
            {
                var center = dot.TranslatePoint(new Point(dot.ActualWidth / 2, dot.ActualHeight / 2), TaskConnectorOverlay);
                if (!double.IsNaN(center.X) && !double.IsNaN(center.Y) && !double.IsInfinity(center.X) && !double.IsInfinity(center.Y))
                {
                    return center;
                }
            }
        }

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

    private static StackPanel? FindPortStack(Grid card, bool isOutput)
    {
        var alignment = isOutput ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        foreach (var child in card.Children)
        {
            if (child is not StackPanel stack)
            {
                continue;
            }

            if (stack.Orientation != Orientation.Vertical)
            {
                continue;
            }

            if (stack.HorizontalAlignment != alignment)
            {
                continue;
            }

            return stack;
        }

        return null;
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
        string statusDetail,
        Geometry? cardCoverGeometry)
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
            StrokeThickness = resolved ? 4.4 : 4,
            Opacity = 0.98,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        System.Windows.Shapes.Path? cardOverlayPath = null;

        if (cardCoverGeometry is not null)
        {
            var fullOverlayBounds = new RectangleGeometry(new Rect(0, 0, TaskConnectorOverlay.Width, TaskConnectorOverlay.Height));
            path.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, fullOverlayBounds, cardCoverGeometry);

            cardOverlayPath = new System.Windows.Shapes.Path
            {
                Data = ((PathGeometry)path.Data).Clone(),
                Stroke = stroke,
                StrokeThickness = path.StrokeThickness,
                Opacity = 0.24,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Clip = cardCoverGeometry
            };
        }

        if (!resolved)
        {
            path.StrokeDashArray = [6, 4];
            if (cardOverlayPath is not null)
            {
                cardOverlayPath.StrokeDashArray = [6, 4];
            }
        }

        var connector = new ConnectorVisual
        {
            PrimaryPath = path,
            CardOverlayPath = cardOverlayPath,
            SourceTaskName = sourceTaskName,
            SourceOutputName = sourceOutputName,
            TargetTaskName = string.IsNullOrWhiteSpace(targetTaskName) ? "(Unnamed Task)" : targetTaskName.Trim(),
            TargetInputName = targetInputName,
            BindingReference = bindingReference,
            Resolved = resolved,
            StatusDetail = statusDetail
        };

        path.ToolTip = _dataflowGraphService.BuildConnectorToolTipText(new DataflowGraphService.ConnectorTooltipItem
        {
            Resolved = connector.Resolved,
            SourceTaskName = connector.SourceTaskName,
            SourceOutputName = connector.SourceOutputName,
            TargetTaskName = connector.TargetTaskName,
            TargetInputName = connector.TargetInputName,
            BindingReference = connector.BindingReference,
            StatusDetail = connector.StatusDetail
        });
        ApplyConnectorInteractivity(path, connector);
        if (cardOverlayPath is not null)
        {
            cardOverlayPath.ToolTip = path.ToolTip;
            ApplyConnectorInteractivity(cardOverlayPath, connector);
        }

        TaskConnectorOverlay.Children.Add(path);
        if (cardOverlayPath is not null)
        {
            TaskConnectorOverlay.Children.Add(cardOverlayPath);
        }
        _connectorVisuals.Add(connector);
        _connectorByPath[path] = connector;
        if (cardOverlayPath is not null)
        {
            _connectorByPath[cardOverlayPath] = connector;
        }
    }

    private void ApplyConnectorInteractivity(System.Windows.Shapes.Path path, ConnectorVisual connector)
    {
        path.Cursor = Cursors.Hand;
        path.MouseEnter += Connector_MouseEnter;
        path.MouseLeave += Connector_MouseLeave;
        path.MouseLeftButtonUp += Connector_MouseLeftButtonUp;
        _connectorByPath[path] = connector;
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
            ApplyConnectorVisualStyle(
                connector,
                connector.Resolved ? resolvedStroke : unresolvedStroke,
                connector.Resolved ? 4.4 : 4,
                primaryOpacity: 0.98,
                cardOverlayOpacity: 0.24);
        }
    }

    private void ApplyConnectorHighlight(ConnectorVisual focal)
    {
        var focalIndex = _connectorVisuals.FindIndex(item => ReferenceEquals(item, focal));
        var relationItems = _connectorVisuals
            .Select(item => new DataflowGraphService.ConnectorRelationItem
            {
                Resolved = item.Resolved,
                SourceTaskName = item.SourceTaskName,
                TargetTaskName = item.TargetTaskName
            })
            .ToList();

        var relatedIndexes = _dataflowGraphService.ComputeUpstreamAndDownstreamConnectorIndexes(relationItems, focalIndex);
        var resolvedStroke = GetResolvedConnectorBrush();
        var unresolvedStroke = GetUnresolvedConnectorBrush();
        var focusedStroke = GetFocusedConnectorBrush();

        for (var i = 0; i < _connectorVisuals.Count; i++)
        {
            var connector = _connectorVisuals[i];

            if (ReferenceEquals(connector, focal))
            {
                ApplyConnectorVisualStyle(
                    connector,
                    focusedStroke,
                    6.2,
                    primaryOpacity: 1,
                    cardOverlayOpacity: 0.34);
                continue;
            }

            if (relatedIndexes.Contains(i))
            {
                ApplyConnectorVisualStyle(
                    connector,
                    connector.Resolved ? resolvedStroke : unresolvedStroke,
                    connector.Resolved ? 5 : 4.7,
                    primaryOpacity: 1,
                    cardOverlayOpacity: 0.28);
                continue;
            }

            ApplyConnectorVisualStyle(
                connector,
                connector.Resolved ? resolvedStroke : unresolvedStroke,
                connector.Resolved ? 3.2 : 3,
                primaryOpacity: 0.38,
                cardOverlayOpacity: 0.14);
        }
    }

    private static void ApplyConnectorVisualStyle(
        ConnectorVisual connector,
        Brush stroke,
        double strokeThickness,
        double primaryOpacity,
        double cardOverlayOpacity)
    {
        connector.PrimaryPath.Stroke = stroke;
        connector.PrimaryPath.StrokeThickness = strokeThickness;
        connector.PrimaryPath.Opacity = primaryOpacity;

        if (connector.CardOverlayPath is not null)
        {
            connector.CardOverlayPath.Stroke = stroke;
            connector.CardOverlayPath.StrokeThickness = strokeThickness;
            connector.CardOverlayPath.Opacity = cardOverlayOpacity;
        }
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
        var orderedGroupNames = GetOrderedGroupNamesForSidebar(groupNames);

        foreach (var groupName in orderedGroupNames)
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

    private IReadOnlyList<string> GetOrderedGroupNamesForSidebar(IReadOnlyList<string> groupNames)
    {
        var normalizedGroups = groupNames.Select(NormalizeGroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var available = new HashSet<string>(normalizedGroups, StringComparer.OrdinalIgnoreCase);

        _favoriteGroupOrder.RemoveAll(groupName => !available.Contains(groupName));
        _nonFavoriteGroupsOrder.RemoveAll(groupName => !available.Contains(groupName));
        _favoriteGroups.RemoveWhere(groupName => !available.Contains(groupName));

        foreach (var groupName in normalizedGroups)
        {
            if (_favoriteGroups.Contains(groupName) && !_favoriteGroupOrder.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                _favoriteGroupOrder.Add(groupName);
            }
        }

        var ordered = new List<string>();

        foreach (var groupName in _favoriteGroupOrder)
        {
            if (_favoriteGroups.Contains(groupName) && available.Contains(groupName))
            {
                ordered.Add(groupName);
            }
        }

        foreach (var groupName in _nonFavoriteGroupsOrder)
        {
            if (!_favoriteGroups.Contains(groupName) && available.Contains(groupName) && !ordered.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(groupName);
            }
        }

        foreach (var groupName in normalizedGroups)
        {
            if (!_favoriteGroups.Contains(groupName) && !ordered.Contains(groupName, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(groupName);
            }
        }

        return ordered;
    }

    private void FavoriteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string groupName })
        {
            return;
        }

        ToggleFavoriteGroup(groupName);
        e.Handled = true;
    }

    private void FavoriteGroupGlyph_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupName })
        {
            return;
        }

        ToggleFavoriteGroup(groupName);
        e.Handled = true;
    }

    private void ToggleFavoriteGroup(string groupName)
    {
        var normalizedGroup = NormalizeGroupName(groupName);

        if (_favoriteGroups.Contains(normalizedGroup))
        {
            _favoriteGroups.Remove(normalizedGroup);
            _favoriteGroupOrder.RemoveAll(name => string.Equals(name, normalizedGroup, StringComparison.OrdinalIgnoreCase));
            _nonFavoriteGroupsOrder.RemoveAll(name => string.Equals(name, normalizedGroup, StringComparison.OrdinalIgnoreCase));
            _nonFavoriteGroupsOrder.Insert(0, normalizedGroup);
        }
        else
        {
            _favoriteGroups.Add(normalizedGroup);
            _favoriteGroupOrder.RemoveAll(name => string.Equals(name, normalizedGroup, StringComparison.OrdinalIgnoreCase));
            _favoriteGroupOrder.Add(normalizedGroup);
            _nonFavoriteGroupsOrder.RemoveAll(name => string.Equals(name, normalizedGroup, StringComparison.OrdinalIgnoreCase));
        }

        RebuildGroupNavigator(GetGroupSidebarItems(GetGroupNames()));
    }

    private static string NormalizeGroupName(string? groupName)
    {
        return TaskIdentity.NormalizeGroupName(groupName);
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
        var graphTaskIndex = _currentConfiguration.Tasks.FindIndex(candidate =>
            string.Equals(GetTaskIdentityKey(candidate), GetTaskIdentityKey(task), StringComparison.OrdinalIgnoreCase));
        if (graphTaskIndex >= 0)
        {
            ApplyGraphNodeStateVisual(graphTaskIndex);
        }
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
            CornerRadius = new CornerRadius(0),
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
        return TaskIdentity.BuildKey(task);
    }

    private static string GetTaskIdentityKey(LauncherTask task)
    {
        return TaskIdentity.BuildKey(task);
    }

    private static string GetTaskIdentityKey(string? groupName, string? taskName)
    {
        return TaskIdentity.BuildKey(groupName, taskName);
    }

    private bool TryCancelTaskExecution(LauncherTask task, out string message)
    {
        var stateKey = GetTaskStateKey(task);
        if (!_taskExecutionCancellation.TryGetValue(stateKey, out var cancellationSource))
        {
            message = "Task was not cancellable at this stage.";
            return false;
        }

        if (cancellationSource.IsCancellationRequested)
        {
            message = "Cancellation is already in progress.";
            return true;
        }

        cancellationSource.Cancel();
        message = task.KillProcessTreeOnCancel
            ? "Cancellation requested. tsky will terminate the process tree."
            : "Cancellation requested. tsky will terminate only the main process.";
        return true;
    }

    private void UnregisterTaskCancellation(LauncherTask task)
    {
        var stateKey = GetTaskStateKey(task);
        if (_taskExecutionCancellation.Remove(stateKey, out var cancellationSource))
        {
            cancellationSource.Dispose();
        }
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
        var newTaskIdentityKey = GetTaskIdentityKey(newTask);
        var existingIndex = _currentConfiguration.Tasks.FindIndex(task =>
            string.Equals(GetTaskIdentityKey(task), newTaskIdentityKey, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            var overwriteResult = AppMessageBox.Show(this, 
                $"A task named '{newTask.Name}' already exists in group '{NormalizeGroupName(newTask.Group)}'. Overwrite it?",
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

    private void RibbonAddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        AddTaskButton_Click(sender, e);
    }

    private void RibbonDeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        TryDeleteFromCurrentContext();
    }

    private void RibbonEditTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex))
        {
            AppMessageBox.Show(this, "No tasks are available in the current group.", "Edit Task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetTask(taskIndex, out _))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EditTask(taskIndex);
    }

    private void RibbonToggleTaskEnabledButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex))
        {
            AppMessageBox.Show(this, "No tasks are available in the current group.", "Enable/Disable Task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ToggleTaskEnabled(taskIndex);
    }

    private async void RibbonRunChainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_visibleTaskIndexes.Count == 0)
        {
            return;
        }

        var firstEnabledTaskIndex = _visibleTaskIndexes.FirstOrDefault(index => IsTaskEnabled(_currentConfiguration.Tasks[index]));
        if (!_visibleTaskIndexes.Contains(firstEnabledTaskIndex))
        {
            return;
        }

        await RunTaskChainFromTaskAsync(firstEnabledTaskIndex);
    }

    private void RibbonPreviousGroupButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateGroup(step: -1);
    }

    private void RibbonNextGroupButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateGroup(step: 1);
    }

    private void RibbonCreateRitualButton_Click(object sender, RoutedEventArgs e)
    {
        DefineRitualFromCurrentGroupMenuItem_Click(sender, e);
    }

    private void RibbonManageRitualsButton_Click(object sender, RoutedEventArgs e)
    {
        AppMessageBox.Show(this, "Ritual management is currently available from the rituals menu entries. A dedicated manager panel can be added next.", "Rituals", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RibbonGroupsDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = CreateRibbonContextMenu();

        var prevItem = new MenuItem
        {
            Header = "Previous Group",
            Style = (Style)Resources["AppMenuItemStyle"],
            InputGestureText = "Ctrl+PgUp",
            IsEnabled = GetSortedGroupNames().Count > 0
        };
        prevItem.Click += (_, _) => NavigateGroup(step: -1);

        var nextItem = new MenuItem
        {
            Header = "Next Group",
            Style = (Style)Resources["AppMenuItemStyle"],
            InputGestureText = "Ctrl+PgDn",
            IsEnabled = GetSortedGroupNames().Count > 0
        };
        nextItem.Click += (_, _) => NavigateGroup(step: 1);

        menu.Items.Add(prevItem);
        menu.Items.Add(nextItem);
        menu.Items.Add(new Separator());

        var sortedGroups = GetSortedGroupNames();
        if (sortedGroups.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "(No groups)",
                IsEnabled = false,
                Style = (Style)Resources["AppMenuItemStyle"]
            });
        }
        else
        {
            foreach (var groupName in sortedGroups)
            {
                var item = new MenuItem
                {
                    Header = groupName,
                    IsCheckable = true,
                    IsChecked = string.Equals(groupName, _selectedGroupName, StringComparison.OrdinalIgnoreCase),
                    Style = (Style)Resources["AppMenuItemStyle"]
                };

                item.Click += (_, _) => SelectGroup(groupName);
                menu.Items.Add(item);
            }
        }

        OpenRibbonContextMenu(button, menu);
    }

    private void RibbonRitualsDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = CreateRibbonContextMenu();
        var hasCurrentGroupRitual = TryGetCurrentGroupRitual(out _);

        var defineItem = new MenuItem
        {
            Header = "Define From Current Group",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = HasSelectedGroupTasks()
        };
        defineItem.Click += DefineRitualFromCurrentGroupMenuItem_Click;
        menu.Items.Add(defineItem);

        var runItem = new MenuItem
        {
            Header = "Run Current Group Ritual",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = hasCurrentGroupRitual
        };
        runItem.Click += RibbonRunCurrentGroupRitualMenuItem_Click;
        menu.Items.Add(runItem);

        var editItem = new MenuItem
        {
            Header = "Edit Current Group Ritual",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = hasCurrentGroupRitual
        };
        editItem.Click += RibbonEditCurrentGroupRitualMenuItem_Click;
        menu.Items.Add(editItem);

        OpenRibbonContextMenu(button, menu);
    }

    private async void RibbonRunCurrentGroupRitualMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentGroupRitual(out var ritual))
        {
            AppMessageBox.Show(this, "No ritual is defined for the current group.", "Rituals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteRitualAsync(ritual);
    }

    private void RibbonEditCurrentGroupRitualMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCurrentGroupRitual(out var ritual))
        {
            AppMessageBox.Show(this, "No ritual is defined for the current group.", "Rituals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EditRitualMenuEntry_Click(new MenuItem { Tag = ritual }, new RoutedEventArgs());
    }

    private bool TryGetCurrentGroupRitual(out RitualDefinition ritual)
    {
        ritual = new RitualDefinition();

        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return false;
        }

        var groupName = NormalizeGroupName(_selectedGroupName);
        var defaultName = $"{groupName} Ritual";

        ritual = _currentConfiguration.Rituals.FirstOrDefault(r =>
                     string.Equals(r.Name, defaultName, StringComparison.OrdinalIgnoreCase))
                 ?? _currentConfiguration.Rituals.FirstOrDefault(r =>
                     r.Steps.Any(step => string.Equals(NormalizeGroupName(step.Group), groupName, StringComparison.OrdinalIgnoreCase)))
                 ?? new RitualDefinition();

        return !string.IsNullOrWhiteSpace(ritual.Name);
    }

    private ContextMenu CreateRibbonContextMenu()
    {
        return new ContextMenu
        {
            Style = (Style)Resources["RibbonContextMenuStyle"],
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
    }

    private static void OpenRibbonContextMenu(Button button, ContextMenu menu)
    {
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AppendValidationEvent(string action, string path, bool succeeded, string details, RichTextBox? outputTextBox = null)
    {
        if (outputTextBox is not null)
        {
            AppendLog(outputTextBox, $"[Validation] {action} {(succeeded ? "passed" : "failed")}: {path}");
            if (!string.IsNullOrWhiteSpace(details))
            {
                AppendLog(outputTextBox, $"[Validation] {details}");
            }

            return;
        }

        var tabContext = CreateOutputTab("JSON Validation");
        var validationOutput = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        AppendLog(validationOutput, $"[Validation] {action} {(succeeded ? "passed" : "failed")}: {path}");
        if (!string.IsNullOrWhiteSpace(details))
        {
            AppendLog(validationOutput, $"[Validation] {details}");
        }

        SetTabStopped(tabContext, succeeded);
    }

    private TaskConfiguration LoadConfigurationWithValidation(string path, string action, RichTextBox? outputTextBox = null)
    {
        try
        {
            var config = TaskConfiguration.Load(path);
            AppendValidationEvent(action, path, true, $"Loaded {config.Tasks.Count} task(s) and {config.Rituals.Count} ritual(s).", outputTextBox);
            AppendBusinessRuleValidationWarnings(path, config, outputTextBox);
            return config;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Validation] Failed to load configuration from '{path}' for action '{action}'");
            AppendValidationEvent(action, path, false, ex.Message, outputTextBox);
            throw;
        }
    }

    private TaskConfiguration SaveAndValidateConfiguration(string path, TaskConfiguration config, string action, RichTextBox? outputTextBox = null)
    {
        try
        {
            TaskConfiguration.Save(path, config);
            var validatedConfig = TaskConfiguration.Load(path);
            AppendValidationEvent(action, path, true, $"Validated {validatedConfig.Tasks.Count} task(s) and {validatedConfig.Rituals.Count} ritual(s).", outputTextBox);
            AppendBusinessRuleValidationWarnings(path, validatedConfig, outputTextBox);
            return validatedConfig;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Validation] Failed to save/validate configuration at '{path}' for action '{action}'");
            AppendValidationEvent(action, path, false, ex.Message, outputTextBox);
            throw;
        }
    }

    private void AppendBusinessRuleValidationWarnings(string path, TaskConfiguration config, RichTextBox? outputTextBox = null)
    {
        var warnings = GetBusinessRuleValidationWarnings(config);
        if (warnings.Count == 0)
        {
            return;
        }

        if (outputTextBox is not null)
        {
            AppendLog(outputTextBox, $"[Validation] Business-rules warnings: {warnings.Count} issue(s) in {path}");
            foreach (var warning in warnings)
            {
                AppendLog(outputTextBox, $"[Validation] {warning}");
            }

            return;
        }

        var tabContext = CreateOutputTab("JSON Validation");
        var validationOutput = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        AppendLog(validationOutput, $"[Validation] Business-rules warnings: {warnings.Count} issue(s) in {path}");
        foreach (var warning in warnings)
        {
            AppendLog(validationOutput, $"[Validation] {warning}");
        }

        SetTabStopped(tabContext, true);
    }

    private static List<string> GetBusinessRuleValidationWarnings(TaskConfiguration config)
    {
        var warnings = new List<string>();

        foreach (var task in config.Tasks)
        {
            var runner = TaskProcessStartInfoFactory.NormalizeRunner(task.Runner);
            if (!string.Equals(runner, "exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryExtractCommandToken(task.CommandLine, out var commandToken))
            {
                continue;
            }

            var extension = System.IO.Path.GetExtension(commandToken)?.ToLowerInvariant();
            if (extension is not ".cmd" and not ".bat")
            {
                continue;
            }

            var taskName = string.IsNullOrWhiteSpace(task.Name) ? "(unnamed)" : task.Name.Trim();
            var groupName = NormalizeGroupName(task.Group);
            warnings.Add(
                $"Task '{taskName}' (group '{groupName}') uses runner 'exe' with script '{commandToken}'. Potential issue: .cmd/.bat scripts usually require runner 'cmd'. Suggested fix: set runner to 'cmd' or run via 'cmd /c \"{task.CommandLine}\"'.");
        }

        return warnings;
    }

    private static bool TryExtractCommandToken(string? commandLine, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }

            token = trimmed[1..endQuote].Trim();
            return !string.IsNullOrWhiteSpace(token);
        }

        var firstSpace = trimmed.IndexOf(' ');
        token = firstSpace < 0 ? trimmed : trimmed[..firstSpace].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private void RibbonShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            OpenShareMenu(button);
            return;
        }

        ExportCurrentTaskToClipboard();
    }

    private void OpenShareMenu(Button anchorButton)
    {
        var menu = CreateRibbonContextMenu();

        var shareTaskItem = new MenuItem
        {
            Header = "Share Task",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = TryGetSelectedVisibleTaskIndex(out _)
        };
        shareTaskItem.Click += (_, _) => ExportCurrentTaskToClipboard();
        menu.Items.Add(shareTaskItem);

        var shareGroupItem = new MenuItem
        {
            Header = "Share Group",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = !string.IsNullOrWhiteSpace(_selectedGroupName) && _visibleTaskIndexes.Count > 0
        };
        shareGroupItem.Click += (_, _) => ExportCurrentGroupToClipboard();
        menu.Items.Add(shareGroupItem);

        OpenRibbonContextMenu(anchorButton, menu);
    }

    private void ExportCurrentTaskToClipboard()
    {
        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex) || !TryGetTask(taskIndex, out var task))
        {
            AppMessageBox.Show(this, "No task is selected to share.", "Share", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var payload = new TaskConfiguration
        {
            Tasks = [task],
            Rituals = []
        };

        ExportConfigurationToClipboardAndOutput("Share Task", payload, $"task '{task.Name}'");
    }

    private void ExportCurrentGroupToClipboard()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName) || _visibleTaskIndexes.Count == 0)
        {
            AppMessageBox.Show(this, "No group is selected to share.", "Share", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tasks = _visibleTaskIndexes
            .Select(index => _currentConfiguration.Tasks[index])
            .ToList();

        var payload = new TaskConfiguration
        {
            Tasks = tasks,
            Rituals = []
        };

        ExportConfigurationToClipboardAndOutput("Share Group", payload, $"group '{_selectedGroupName}' ({tasks.Count} task(s))");
    }

    private void ExportConfigurationToClipboardAndOutput(string tabName, TaskConfiguration payload, string summary)
    {
        var tabContext = CreateOutputTab(tabName);
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);

        try
        {
            var json = _importExportService.SerializeConfiguration(payload);

            Clipboard.SetText(json);

            AppendLog(outputTextBox, $"[Share] Exported {summary} to clipboard.");
            AppendLog(outputTextBox, "[Share] Paste the JSON into email, chat, docs, or any note-taking tool.");
            AppendLog(outputTextBox, "[Share] Exported JSON:");
            AppendLog(outputTextBox, json);
            SetTabStopped(tabContext, true);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Share] ExportConfigurationToClipboardAndOutput failed for {summary}");
            AppendLog(outputTextBox, $"[Share] Export failed: {ex.Message}");
            SetTabStopped(tabContext, false);
            AppMessageBox.Show(this, $"Failed to export share payload to clipboard.\n{ex.Message}", "Share", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RibbonImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            OpenImportMenu(button);
            return;
        }

        ImportFromFile();
    }

    private void OpenImportMenu(Button anchorButton)
    {
        var menu = CreateRibbonContextMenu();

        var importFromFileItem = new MenuItem
        {
            Header = "Import from File",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        importFromFileItem.Click += (_, _) => ImportFromFile();
        menu.Items.Add(importFromFileItem);

        var importFromClipboardItem = new MenuItem
        {
            Header = "Import from Clipboard",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        importFromClipboardItem.Click += (_, _) => ImportFromClipboard();
        menu.Items.Add(importFromClipboardItem);

        OpenRibbonContextMenu(anchorButton, menu);
    }

    private void ImportFromFile()
    {
        using var importScope = DebugTraceLog.BeginOperation("import", "file");
        var tabContext = CreateOutputTab("Import");
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);

        var openFileDialog = new OpenFileDialog
        {
            Title = "Import Tasks Configuration",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };

        if (openFileDialog.ShowDialog(this) != true)
        {
            AppendLog(outputTextBox, "[Import] Import canceled.");
            SetTabStopped(tabContext, true);
            return;
        }

        var importPath = openFileDialog.FileName;
        AppendLog(outputTextBox, $"[Import] Selected file: {importPath}");

        try
        {
            var importedConfig = LoadConfigurationWithValidation(importPath, "Read import file tasks.json", outputTextBox);
            AppendLog(outputTextBox, $"[Import] Loaded {importedConfig.Tasks.Count} task(s) and {importedConfig.Rituals.Count} ritual(s) from file.");
            ImportConfiguration(importedConfig, outputTextBox, tabContext, importPath);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Import] ImportFromFile failed for '{importPath}'");
            AppendLog(outputTextBox, $"[Import] Error: {ex.Message}");
            SetTabStopped(tabContext, false);
        }
    }

    private void ImportFromClipboard()
    {
        using var importScope = DebugTraceLog.BeginOperation("import", "clipboard");
        var tabContext = CreateOutputTab("Import (Clipboard)");
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);

        try
        {
            if (!Clipboard.ContainsText())
            {
                AppendLog(outputTextBox, "[Import] Clipboard does not contain text.");
                SetTabStopped(tabContext, false);
                return;
            }

            var clipboardText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                AppendLog(outputTextBox, "[Import] Clipboard text is empty.");
                SetTabStopped(tabContext, false);
                return;
            }

            var tempInputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tsky-import-clipboard-input-{Guid.NewGuid():N}.json");
            File.WriteAllText(tempInputPath, clipboardText);

            try
            {
                var importedConfig = LoadConfigurationWithValidation(tempInputPath, "Read clipboard tasks.json", outputTextBox);
                AppendLog(outputTextBox, $"[Import] Loaded {importedConfig.Tasks.Count} task(s) and {importedConfig.Rituals.Count} ritual(s) from clipboard.");
                ImportConfiguration(importedConfig, outputTextBox, tabContext, "clipboard");
            }
            finally
            {
                if (File.Exists(tempInputPath))
                {
                    File.Delete(tempInputPath);
                }
            }
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[Import] ImportFromClipboard failed");
            AppendLog(outputTextBox, $"[Import] Error: {ex.Message}");
            SetTabStopped(tabContext, false);
        }
    }

    private void ImportConfiguration(TaskConfiguration importedConfig, RichTextBox outputTextBox, TaskTabContext tabContext, string importSourceLabel)
    {
        try
        {
            var mergeResult = _importExportService.MergeAndPersistImportedConfiguration(
                _userProfileConfigPath,
                importedConfig,
                importSourceLabel);

            var validatedMergedConfig = mergeResult.ValidatedMergedConfiguration;
            var validatedWrittenProfile = mergeResult.ValidatedWrittenUserProfileConfiguration;

            AppendLog(outputTextBox, $"[Import] Merged configuration validated and written to user profile: {_userProfileConfigPath}");
            AppendLog(outputTextBox, $"[Import] Final merged result: {validatedWrittenProfile.Tasks.Count} task(s), {validatedWrittenProfile.Rituals.Count} ritual(s).");

            LoadTaskButtons();
            AppendLog(outputTextBox, "[Import] UI reloaded.");
            SetTabStopped(tabContext, true);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Import] Merge/persist failed from source '{importSourceLabel}'");
            AppendLog(outputTextBox, $"[Import] Error: {ex.Message}");
            SetTabStopped(tabContext, false);
        }
    }

    private void TaskCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCurrentGroupDataflowLocked())
        {
            _isDraggingTaskCard = false;
            return;
        }

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
        if (IsCurrentGroupDataflowLocked())
        {
            _isDraggingTaskCard = false;
            return;
        }

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
        if (IsCurrentGroupDataflowLocked())
        {
            e.Effects = DragDropEffects.None;
            ClearDropCue();
            e.Handled = true;
            return;
        }

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
        if (IsCurrentGroupDataflowLocked())
        {
            ClearDropCue();
            return;
        }

        ClearDropCue();

        if (!e.Data.GetDataPresent(typeof(int)))
        {
            return;
        }

        var sourceGlobalIndex = (int)e.Data.GetData(typeof(int))!;
        var sourceDisplayIndex = _visibleTaskIndexes.IndexOf(sourceGlobalIndex);
        var targetDisplayIndex = GetDropTargetDisplayIndex(e.GetPosition(TaskPanel));

        if (!_taskCatalogService.ReorderVisibleGroupTasks(_currentConfiguration, _visibleTaskIndexes, sourceDisplayIndex, targetDisplayIndex))
        {
            return;
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
            var cardRight = topLeft.X + card.ActualWidth;
            var cardBottom = topLeft.Y + card.ActualHeight;
            var cardCenterX = topLeft.X + (card.ActualWidth / 2);

            var isInsideCardBounds = dropPoint.X >= topLeft.X
                && dropPoint.X <= cardRight
                && dropPoint.Y >= topLeft.Y
                && dropPoint.Y <= cardBottom;

            if (isInsideCardBounds)
            {
                // In-card hit testing makes two-item swaps reliable: left half inserts before,
                // right half inserts after the hovered card.
                return dropPoint.X < cardCenterX
                    ? displayIndex
                    : displayIndex + 1;
            }

            if (dropPoint.Y < topLeft.Y)
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

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            TryDeleteFromCurrentContext();
            e.Handled = true;

            return;
        }

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex)
                && TryGetTask(selectedTaskIndex, out var selectedTask)
                && GetTaskRunState(selectedTask) == TaskRunState.Running
                && TryCancelTaskExecution(selectedTask, out var cancelMessage))
            {
                var outputPanel = GetOrCreateSelectedGroupOutputPanel();
                if (outputPanel.Runs.Count > 0)
                {
                    AppendLog(outputPanel.Runs[^1].OutputTextBox, $"[{selectedTask.Name}] {cancelMessage}");
                }

                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.R && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (TaskScrollViewer?.IsKeyboardFocusWithin != true)
            {
                return;
            }

            if (BottomPaneTabControl?.IsKeyboardFocusWithin == true)
            {
                return;
            }

            if (!TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex) || !TryGetTask(selectedTaskIndex, out var selectedTask))
            {
                return;
            }

            if (!IsTaskEnabled(selectedTask))
            {
                AppMessageBox.Show(this, $"Task '{selectedTask.Name}' is disabled.", "Task Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
                return;
            }

            await ExecuteTaskAsync(selectedTask, null, isReplay: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (TaskScrollViewer?.IsKeyboardFocusWithin != true)
            {
                return;
            }

            if (!TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex))
            {
                return;
            }

            await RunTaskByIndexAsync(selectedTaskIndex);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.E && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex))
            {
                EditTask(selectedTaskIndex);
                e.Handled = true;
            }

            return;
        }

        if (e.Key != Key.Tab || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        if (BottomPaneTabControl?.IsKeyboardFocusWithin == true)
        {
            return;
        }

        if (_visibleTaskIndexes.Count == 0)
        {
            return;
        }

        SelectNextVisibleTask(step: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private bool IsSidebarPaneFocused()
    {
        return SidebarPaneBorder?.IsKeyboardFocusWithin == true
            || GroupsTreeScrollViewer?.IsKeyboardFocusWithin == true
            || GroupListPanel?.IsKeyboardFocusWithin == true;
    }

    private void TryDeleteFromCurrentContext()
    {
        if (IsSidebarPaneFocused() || _preferGroupDeleteContext)
        {
            TryDeleteSelectedGroup();
            return;
        }

        TryDeleteSelectedTask();
    }

    private void TryDeleteSelectedTask()
    {
        _preferGroupDeleteContext = false;

        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex))
        {
            AppMessageBox.Show(this, "No tasks are available in the current group.", "Delete Task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetTask(taskIndex, out var taskToDelete))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = AppMessageBox.Show(
            this,
            $"Delete task '{taskToDelete.Name}' from this group?",
            "Delete Task",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        DeleteTask(taskIndex);
    }

    private void SelectNextVisibleTask(int step)
    {
        if (_visibleTaskIndexes.Count == 0)
        {
            _selectedTaskIndex = null;
            return;
        }

        if (_selectedTaskIndex is null || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
        {
            _selectedTaskIndex = _visibleTaskIndexes[0];
        }
        else
        {
            var currentPosition = _visibleTaskIndexes.IndexOf(_selectedTaskIndex.Value);
            var nextPosition = (currentPosition + step + _visibleTaskIndexes.Count) % _visibleTaskIndexes.Count;
            _selectedTaskIndex = _visibleTaskIndexes[nextPosition];
        }

        ApplyTaskCardSelectionVisuals();
        FocusSelectedTaskCard();
    }

    private void FocusSelectedTaskCard()
    {
        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex))
        {
            return;
        }

        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Graph)
        {
            if (_graphNodesByIndex.TryGetValue(taskIndex, out var graphNode))
            {
                graphNode.NodeButton.Focus();
            }

            return;
        }

        if (_taskCardsByIndex.TryGetValue(taskIndex, out var card) && card.Tag is TaskCardVisual visual)
        {
            visual.TaskButton.Focus();
        }
    }

    private bool TryGetSelectedVisibleTaskIndex(out int taskIndex)
    {
        taskIndex = -1;

        if (_visibleTaskIndexes.Count == 0)
        {
            _selectedTaskIndex = null;
            return false;
        }

        if (_selectedTaskIndex is null || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
        {
            _selectedTaskIndex = _visibleTaskIndexes[0];
        }

        taskIndex = _selectedTaskIndex.Value;
        return true;
    }

    private void ApplyTaskCardSelectionVisuals()
    {
        foreach (var card in _taskCardsByIndex.Values)
        {
            if (card.Tag is not TaskCardVisual visual)
            {
                continue;
            }

            var isSelected = _selectedTaskIndex.HasValue && visual.TaskIndex == _selectedTaskIndex.Value;
            visual.SelectionBorder.BorderThickness = isSelected ? new Thickness(2) : new Thickness(0);
        }

        foreach (var taskIndex in _graphNodesByIndex.Keys)
        {
            ApplyGraphNodeStateVisual(taskIndex);
        }

        UpdateRibbonTaskActionState();
        SyncBottomPaneForSelectedTask();
    }

    private void SyncBottomPaneForSelectedTask()
    {
        if (_isSyncingBottomPaneToSelection || BottomPaneTabControl is null)
        {
            return;
        }

        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex) || !TryGetTask(taskIndex, out var task))
        {
            return;
        }

        try
        {
            _isSyncingBottomPaneToSelection = true;

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, InputPortsPaneTabItem))
            {
                OpenInputPortsEditor(taskIndex, selectTab: false);
                return;
            }

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, OutputPortsPaneTabItem))
            {
                OpenOutputPortsEditor(taskIndex, selectTab: false);
                return;
            }

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, HistoryPaneTabItem))
            {
                SyncHistoryTabForSelectedTask(task);
                return;
            }

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, OutputPaneTabItem))
            {
                SyncOutputTabForSelectedTask(task);
            }
        }
        finally
        {
            _isSyncingBottomPaneToSelection = false;
        }
    }

    private void SyncHistoryTabForSelectedTask(LauncherTask task)
    {
        var normalizedTaskName = task.Name.Trim();
        var targetItem = HistoryListBox.Items
            .OfType<HistoryListItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Entry.Snapshot.Name, normalizedTaskName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Entry.Snapshot.Group, NormalizeGroupName(task.Group), StringComparison.OrdinalIgnoreCase));

        HistoryListBox.SelectedItems.Clear();
        if (targetItem is null)
        {
            UpdateHistoryPreview();
            UpdateHistoryActionState();
            return;
        }

        HistoryListBox.SelectedItem = targetItem;
        HistoryListBox.ScrollIntoView(targetItem);
    }

    private void SyncOutputTabForSelectedTask(LauncherTask task)
    {
        var groupName = _selectedGroupName;
        if (string.IsNullOrWhiteSpace(groupName) || !_groupOutputPanels.TryGetValue(groupName, out var panel))
        {
            return;
        }

        var selectedRun = panel.Runs.FirstOrDefault(run =>
            run.TitleText.Text.StartsWith(task.Name.Trim() + " ", StringComparison.OrdinalIgnoreCase));

        if (selectedRun is null)
        {
            return;
        }

        selectedRun.RunItemExpander.IsExpanded = true;
    }

    private void UpdateRibbonTaskActionState()
    {
        if (RibbonToggleTaskEnabledButton is null || RibbonToggleTaskEnabledLabel is null || RibbonRunChainButton is null)
        {
            return;
        }

        var hasVisibleTasks = _visibleTaskIndexes.Count > 0;
        var hasEnabledTasksInGroup = _visibleTaskIndexes.Any(index => IsTaskEnabled(_currentConfiguration.Tasks[index]));

        RibbonRunChainButton.IsEnabled = hasEnabledTasksInGroup;
        RibbonToggleTaskEnabledButton.IsEnabled = hasVisibleTasks;

        if (!hasVisibleTasks || !TryGetSelectedVisibleTaskIndex(out var taskIndex) || !TryGetTask(taskIndex, out var task))
        {
            RibbonToggleTaskEnabledLabel.Text = "Enable";
            return;
        }

        RibbonToggleTaskEnabledLabel.Text = IsTaskEnabled(task) ? "Disable" : "Enable";
    }

    private void TaskMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int taskIndex })
        {
            return;
        }

        _selectedTaskIndex = taskIndex;
        ApplyTaskCardSelectionVisuals();

        if (!IsValidTaskIndex(taskIndex))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is not Button button)
        {
            return;
        }
        OpenTaskContextMenu(button, taskIndex);
    }

    private void EditTask(int taskIndex)
    {
        if (!TryGetTask(taskIndex, out var selectedTask))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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
            AppMessageBox.Show(this, $"Could not save task because the JSON is invalid.\n\n{parseError}", "Task Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _currentConfiguration.Tasks[taskIndex] = updatedTask;

        SaveTasksAndRefresh(TaskEditorTitle);
    }

    private void DeleteTask(int taskIndex)
    {
        if (!_taskCatalogService.DeleteTask(_currentConfiguration, taskIndex))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveTasksAndRefresh(TaskEditorTitle);
    }

    private void TryDeleteSelectedGroup()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            AppMessageBox.Show(this, "No group is selected.", "Delete Group", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedGroup = NormalizeGroupName(_selectedGroupName);
        var tasksInGroup = _currentConfiguration.Tasks
            .Where(task => string.Equals(NormalizeGroupName(task.Group), selectedGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tasksInGroup.Count == 0)
        {
            AppMessageBox.Show(this, $"Group '{selectedGroup}' has no tasks to delete.", "Delete Group", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ritualsWithGroupSteps = _currentConfiguration.Rituals
            .Where(ritual => ritual.Steps.Any(step =>
                !string.IsNullOrWhiteSpace(step.Group)
                && string.Equals(NormalizeGroupName(step.Group), selectedGroup, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var message = $"Delete group '{selectedGroup}' and {tasksInGroup.Count} task(s)?";
        if (ritualsWithGroupSteps.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine
                + $"This will also remove ritual steps that explicitly reference this group in {ritualsWithGroupSteps.Count} ritual(s).";
        }

        var result = AppMessageBox.Show(
            this,
            message,
            "Delete Group",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _currentConfiguration.Tasks = _currentConfiguration.Tasks
            .Where(task => !string.Equals(NormalizeGroupName(task.Group), selectedGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var ritual in _currentConfiguration.Rituals)
        {
            ritual.Steps = ritual.Steps
                .Where(step => string.IsNullOrWhiteSpace(step.Group)
                    || !string.Equals(NormalizeGroupName(step.Group), selectedGroup, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _currentConfiguration.Rituals = _currentConfiguration.Rituals
            .Where(ritual => ritual.Steps.Count > 0)
            .ToList();

        _groupExpandedStates.Remove(selectedGroup);
        _groupHistoryVisibility.Remove(selectedGroup);
        _groupOutputVisibility.Remove(selectedGroup);
        _groupSidebarVisibility.Remove(selectedGroup);
        _groupOutputHeights.Remove(selectedGroup);
        _favoriteGroups.Remove(selectedGroup);
        _favoriteGroupOrder.RemoveAll(groupName => string.Equals(groupName, selectedGroup, StringComparison.OrdinalIgnoreCase));
        _nonFavoriteGroupsOrder.RemoveAll(groupName => string.Equals(groupName, selectedGroup, StringComparison.OrdinalIgnoreCase));

        SaveTasksAndRefresh("Delete Group");
    }

    private bool IsValidTaskIndex(int taskIndex)
    {
        return _taskCatalogService.IsValidTaskIndex(_currentConfiguration, taskIndex);
    }

    private bool TryGetTask(int taskIndex, out LauncherTask task)
    {
        return _taskCatalogService.TryGetTask(_currentConfiguration, taskIndex, out task);
    }

    private void ToggleTaskEnabled(int taskIndex)
    {
        if (!_taskCatalogService.ToggleTaskEnabled(_currentConfiguration, taskIndex))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveTasksAndRefresh("Enable/Disable Task");
    }

    private static bool IsTaskEnabled(LauncherTask task)
    {
        return task.IsEnabled;
    }

    private bool IsTaskDisabledByIdentity(string? groupName, string? taskName)
    {
        return _taskCatalogService.IsTaskDisabledByIdentity(_currentConfiguration.Tasks, groupName, taskName);
    }

    private void SaveTasksAndRefresh(string errorTitle)
    {
        if (!TryPersistConfiguration(errorTitle, "Failed to save tasks.json."))
        {
            return;
        }
    }

    private bool TryPersistConfiguration(string errorTitle, string errorPrefix)
    {
        try
        {
            SaveAndValidateConfiguration(_userProfileConfigPath, _currentConfiguration, "Write user profile tasks.json");
            LoadTaskButtons();
            return true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[Persist] TryPersistConfiguration failed. Title='{errorTitle}'");
            AppMessageBox.Show(this, $"{errorPrefix}\n{ex.Message}", errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static Brush GetReadableTextBrush(Color backgroundColor)
    {
        return new SolidColorBrush(Color.FromRgb(0x12, 0x1E, 0x2A));
    }

    private static string BuildImportedTaskTooltip(LauncherTask task)
    {
        if (string.IsNullOrWhiteSpace(task.ImportedFromFile))
        {
            return "Imported task";
        }

        var importDateDisplay = task.ImportedAtUtc;
        if (!string.IsNullOrWhiteSpace(task.ImportedAtUtc)
            && DateTimeOffset.TryParse(task.ImportedAtUtc, out var importedAt))
        {
            importDateDisplay = importedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        }

        return string.IsNullOrWhiteSpace(importDateDisplay)
            ? $"Imported from: {task.ImportedFromFile}"
            : $"Imported from: {task.ImportedFromFile}{Environment.NewLine}Imported at: {importDateDisplay}";
    }

    private async void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherTask task } button)
        {
            return;
        }

        _preferGroupDeleteContext = false;

        if (button.Parent is Grid card && card.Tag is TaskCardVisual visual)
        {
            _selectedTaskIndex = visual.TaskIndex;
            ApplyTaskCardSelectionVisuals();
        }

        if (!IsTaskEnabled(task))
        {
            AppMessageBox.Show(this, $"Task '{task.Name}' is disabled.", "Task Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteTaskAsync(task, button, isReplay: false);
    }

    private async Task RunTaskChainFromTaskAsync(int startTaskIndex)
    {
        if (!TryGetTask(startTaskIndex, out var startTask))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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

            if (!IsTaskEnabled(taskInfo.Task))
            {
                continue;
            }

            var result = await ExecuteTaskAsync(taskInfo.Task, null, isReplay: false, dataflowContext: context, chainStepLabel: chainLabel);

            if (!result.Succeeded)
            {
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
        var operationDetail = isReplay ? $"{task.Name}-replay" : task.Name;
        using var taskRunScope = DebugTraceLog.BeginOperation("task", operationDetail);
        var tabName = isReplay ? $"{task.Name} (replay)" : task.Name;
        var tabContext = CreateOutputTab(tabName);
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        SetTaskRunState(task, TaskRunState.Running);
        var resolved = DataflowPortResolver.Resolve(task, dataflowContext);
        var effectiveTask = resolved.ResolvedTask;
        var publishedOutputs = new Dictionary<string, string>(resolved.ResolvedOutputs, StringComparer.OrdinalIgnoreCase);
        var historyEntry = StartHistoryEntry(effectiveTask, isReplay, replayFromEntryId);
        var capturedStandardOutput = string.Empty;
        var capturedStandardError = string.Empty;
        var taskStateKey = GetTaskStateKey(task);
        var taskCancellationSource = new CancellationTokenSource();
        _taskExecutionCancellation[taskStateKey] = taskCancellationSource;

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
            CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
            UnregisterTaskCancellation(task);
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }

        var runner = TaskProcessStartInfoFactory.NormalizeRunner(effectiveTask.Runner);
        var waitForExit = effectiveTask.WaitForExit;
        var launchAsAdministrator = TaskExecutionService.ShouldLaunchAsAdministrator(effectiveTask);
        var canCaptureOutput = waitForExit && !launchAsAdministrator;

        if (sourceButton is not null)
        {
            sourceButton.IsEnabled = false;
        }

        var modePrefix = isReplay ? "[replay] " : string.Empty;
        var executionMode = waitForExit ? "wait" : "fire-and-forget";
        AppendLog(outputTextBox, $"[{task.Name}] {modePrefix}Running ({runner}, {executionMode}): {effectiveTask.CommandLine}");
        if (waitForExit && effectiveTask.TimeoutSeconds > 0)
        {
            AppendLog(outputTextBox, $"[{task.Name}] Timeout: {effectiveTask.TimeoutSeconds}s | Kill tree on cancel: {effectiveTask.KillProcessTreeOnCancel} | Max output capture: {effectiveTask.MaxCapturedOutputChars} chars/stream.");
        }

        if (effectiveTask.RequiresElevation)
        {
            if (launchAsAdministrator)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Elevation required. Prompting for administrator consent (UAC).");
            }
            else
            {
                AppendLog(outputTextBox, $"[{task.Name}] Elevation required. Launcher is already running elevated.");
            }
        }

        try
        {
            var executionResult = await TaskExecutionService.ExecuteAsync(
                effectiveTask,
                onOutputLine: (line, isError) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendStreamOutputLine(outputTextBox, isError ? "stderr" : "stdout", line);
                    });
                },
                cancellationToken: taskCancellationSource.Token);

            if (!string.IsNullOrWhiteSpace(executionResult.ConfigurationError))
            {
                AppendLog(outputTextBox, $"[{task.Name}] Invalid task configuration. {executionResult.ConfigurationError}");
                SetTabStopped(tabContext, false);
                SetTaskRunState(task, TaskRunState.Failed);
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
                return new TaskExecutionResult { Succeeded = false, ExitCode = null };
            }

            if (!executionResult.Started)
            {
                if (executionResult.ElevationPromptCanceled)
                {
                    AppendLog(outputTextBox, $"[{task.Name}] Elevation was canceled by the user.");
                }
                else
                {
                    AppendLog(outputTextBox, $"[{task.Name}] Failed to execute task. {executionResult.FailureMessage}");
                }

                SetTabStopped(tabContext, false);
                SetTaskRunState(task, TaskRunState.Failed);
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);

                return new TaskExecutionResult { Succeeded = false, ExitCode = null };
            }

            if (!executionResult.WaitedForExit)
            {
                AppendRuntimeOutputCaptureWarningIfNeeded(outputTextBox, task.Name, publishedOutputs, reason: "task is configured to not wait for process exit");
                publishedOutputs = ResolveRuntimeOutputValues(publishedOutputs, capturedStandardOutput, capturedStandardError, exitCode: null);
                AppendLog(outputTextBox, $"[{task.Name}] Process started (PID {executionResult.ProcessId}). Not waiting for exit.");
                SetTabStopped(tabContext, true);
                SetTaskRunState(task, TaskRunState.Succeeded);
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.Started, null);

                if (publishedOutputs.Count > 0)
                {
                    AppendPublishedOutputs(outputTextBox, task.Name, publishedOutputs);
                    dataflowContext?.RegisterTaskOutputs(task.Name, publishedOutputs);
                }

                return new TaskExecutionResult
                {
                    Succeeded = true,
                    ExitCode = null,
                    PublishedOutputs = new Dictionary<string, string>(publishedOutputs, StringComparer.OrdinalIgnoreCase)
                };
            }

            if (executionResult.CanCaptureOutput)
            {
                capturedStandardOutput = executionResult.StandardOutput;
                capturedStandardError = executionResult.StandardError;
                AppendLog(outputTextBox, $"[{task.Name}] Process completed with exit code {executionResult.ExitCode}.");
            }
            else
            {
                if (launchAsAdministrator)
                {
                    AppendLog(outputTextBox, $"[{task.Name}] Elevated process completed with exit code {executionResult.ExitCode}. Output capture is disabled for elevated launches.");
                }
                else
                {
                    AppendLog(outputTextBox, $"[{task.Name}] Process completed with exit code {executionResult.ExitCode}.");
                }
            }

            var isSuccessfulExit = executionResult.Succeeded;
            if (executionResult.IgnoreExitCodeApplied)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Exit code {executionResult.ExitCode} ignored because ignoreExitCode=true.");
            }

            if (executionResult.Outcome == TaskExecutionOutcome.TimedOut)
            {
                AppendLog(outputTextBox, $"[{task.Name}] {executionResult.FailureMessage}");
            }
            else if (executionResult.Outcome == TaskExecutionOutcome.Cancelled)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Execution cancelled.");
            }
            else if (!isSuccessfulExit && !string.IsNullOrWhiteSpace(executionResult.FailureMessage))
            {
                AppendLog(outputTextBox, $"[{task.Name}] {executionResult.FailureMessage}");
            }

            if (executionResult.OutputTruncated)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Captured output was truncated to {executionResult.MaxCapturedOutputChars} characters per stream.");
            }

            SetTabStopped(tabContext, isSuccessfulExit);
            SetTaskRunState(task, isSuccessfulExit ? TaskRunState.Succeeded : TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, MapOutcome(executionResult.Outcome), executionResult.ExitCode);

            if (!executionResult.CanCaptureOutput)
            {
                var reason = executionResult.OutputCaptureDisabledReason ?? "process output capture was disabled";
                AppendRuntimeOutputCaptureWarningIfNeeded(outputTextBox, task.Name, publishedOutputs, reason);
            }

            publishedOutputs = ResolveRuntimeOutputValues(publishedOutputs, capturedStandardOutput, capturedStandardError, executionResult.ExitCode);

            if (isSuccessfulExit && publishedOutputs.Count > 0)
            {
                AppendPublishedOutputs(outputTextBox, task.Name, publishedOutputs);
                dataflowContext?.RegisterTaskOutputs(task.Name, publishedOutputs);
            }

            return new TaskExecutionResult
            {
                Succeeded = isSuccessfulExit,
                ExitCode = executionResult.ExitCode,
                PublishedOutputs = new Dictionary<string, string>(publishedOutputs, StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[TaskRun] ExecuteTaskAsync failed for task '{task.Name}'");
            AppendLog(outputTextBox, $"[{task.Name}] Failed to execute task. {ex.Message}");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }
        finally
        {
            UnregisterTaskCancellation(task);

            if (sourceButton is not null)
            {
                sourceButton.IsEnabled = true;
            }
        }
    }

    private static Dictionary<string, string> ResolveRuntimeOutputValues(
        IReadOnlyDictionary<string, string> outputValues,
        string standardOutput,
        string standardError,
        int? exitCode)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in outputValues)
        {
            var value = pair.Value ?? string.Empty;
            value = Regex.Replace(value, @"\$\{([^{}]+)\}", match =>
            {
                var token = match.Groups[1].Value.Trim();
                return token.ToLowerInvariant() switch
                {
                    "stdout" or "command:stdout" or "runtime:stdout" => standardOutput ?? string.Empty,
                    "stderr" or "command:stderr" or "runtime:stderr" => standardError ?? string.Empty,
                    "exitcode" or "command:exitcode" or "runtime:exitcode" => exitCode?.ToString() ?? string.Empty,
                    _ => match.Value
                };
            });

            resolved[pair.Key] = value;
        }

        return resolved;
    }

    private static readonly Regex RuntimeOutputTokenRegex = new(@"\$\{([^{}]+)\}", RegexOptions.Compiled);

    private void AppendRuntimeOutputCaptureWarningIfNeeded(
        RichTextBox outputTextBox,
        string taskName,
        IReadOnlyDictionary<string, string> outputValues,
        string reason)
    {
        var affectedOutputs = outputValues
            .Where(pair => ContainsRuntimeOutputToken(pair.Value))
            .Select(pair => pair.Key)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (affectedOutputs.Count == 0)
        {
            return;
        }

        AppendLog(
            outputTextBox,
            $"[{taskName}] Warning: runtime output tokens were evaluated while output streams were not capturable because {reason}. Affected outputs: {string.Join(", ", affectedOutputs)}.");
    }

    private static bool ContainsRuntimeOutputToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var matches = RuntimeOutputTokenRegex.Matches(value);
        foreach (Match match in matches)
        {
            var token = match.Groups[1].Value.Trim();
            if (token.Equals("stdout", StringComparison.OrdinalIgnoreCase)
                || token.Equals("stderr", StringComparison.OrdinalIgnoreCase)
                || token.Equals("exitcode", StringComparison.OrdinalIgnoreCase)
                || token.Equals("command:stdout", StringComparison.OrdinalIgnoreCase)
                || token.Equals("command:stderr", StringComparison.OrdinalIgnoreCase)
                || token.Equals("command:exitcode", StringComparison.OrdinalIgnoreCase)
                || token.Equals("runtime:stdout", StringComparison.OrdinalIgnoreCase)
                || token.Equals("runtime:stderr", StringComparison.OrdinalIgnoreCase)
                || token.Equals("runtime:exitcode", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private TaskRunHistoryEntry StartHistoryEntry(LauncherTask task, bool isReplay, string? replayFromEntryId)
    {
        var entry = _runHistoryService.StartEntry(task, isReplay, replayFromEntryId);
        RefreshHistoryView();
        return entry;
    }

    private void CompleteHistoryEntry(TaskRunHistoryEntry entry, TaskRunOutcome outcome, int? exitCode)
    {
        _runHistoryService.CompleteEntry(entry, outcome, exitCode);
        RefreshHistoryView();
        GroupMetaText.Text = string.IsNullOrWhiteSpace(_selectedGroupName)
            ? GroupMetaText.Text
            : $"Snapshots in this group: {_runHistory.Count(item => string.Equals(item.Snapshot.Group, _selectedGroupName, StringComparison.OrdinalIgnoreCase))}";
    }

    private static TaskRunOutcome MapOutcome(TaskExecutionOutcome outcome)
    {
        return outcome switch
        {
            TaskExecutionOutcome.Started => TaskRunOutcome.Started,
            TaskExecutionOutcome.Succeeded => TaskRunOutcome.Succeeded,
            TaskExecutionOutcome.IgnoredNonZeroExit => TaskRunOutcome.IgnoredNonZeroExit,
            TaskExecutionOutcome.FailedNonZeroExit => TaskRunOutcome.FailedNonZeroExit,
            TaskExecutionOutcome.TimedOut => TaskRunOutcome.TimedOut,
            TaskExecutionOutcome.Cancelled => TaskRunOutcome.Cancelled,
            _ => TaskRunOutcome.LaunchFailed
        };
    }

    private TaskRunHistoryEntry? GetLastSnapshotEntry(LauncherTask task)
    {
        return _runHistoryService.GetLastSnapshotEntry(task);
    }

    private TaskTabContext CreateOutputTab(string taskName)
    {
        var currentOutputPanel = GetOrCreateSelectedGroupOutputPanel();
        var runTitle = $"{taskName} {DateTime.Now:HH:mm:ss}";
        var accentColor = GetTaskRunAccentColor(taskName);
        var accentBrush = CreateRunGradientBrush(accentColor, runTitle);
        var titleBrush = GetReadableTextBrush(accentColor);

        var titleText = new TextBlock
        {
            Text = runTitle,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleText.SetValue(TextElement.ForegroundProperty, titleBrush);

        var statusText = new TextBlock
        {
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Text = string.Empty,
            MinWidth = 18,
            TextAlignment = TextAlignment.Center
        };
        statusText.SetValue(TextElement.ForegroundProperty, titleBrush);

        var closeButton = new Button
        {
            Content = "✕",
            FontWeight = FontWeights.Bold,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Close output item"
        };
        closeButton.SetValue(TextElement.ForegroundProperty, titleBrush);

        var runHeader = new Grid
        {
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        runHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        runHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        runHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        runHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var spacer = new Border { Background = Brushes.Transparent };

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(spacer, 1);
        Grid.SetColumn(statusText, 2);
        Grid.SetColumn(closeButton, 3);

        runHeader.Children.Add(titleText);
        runHeader.Children.Add(spacer);
        runHeader.Children.Add(statusText);
        runHeader.Children.Add(closeButton);

        var outputTextBox = CreateLogTextBox();

        var runItemExpander = new Expander
        {
            Header = runHeader,
            Content = outputTextBox,
            IsExpanded = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };

        var runItemContainer = new Border
        {
            Background = accentBrush,
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 0, 6),
            Child = runItemExpander,
            Opacity = 0.9
        };

        var spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        var tabContext = new TaskTabContext
        {
            HostOutputPanel = currentOutputPanel,
            RunItemContainer = runItemContainer,
            RunItemExpander = runItemExpander,
            OutputTextBox = outputTextBox,
            StatusText = statusText,
            TitleText = titleText,
            SpinnerTimer = spinnerTimer
        };

        spinnerTimer.Tick += (_, _) =>
        {
            tabContext.StatusText.Text = RunningIndicatorFrames[tabContext.SpinnerFrameIndex % RunningIndicatorFrames.Length];
            tabContext.SpinnerFrameIndex++;
        };

        runItemExpander.Expanded += (_, _) => ExpandOutputRun(tabContext);
        runItemExpander.Collapsed += (_, _) =>
        {
            if (!tabContext.RunItemExpander.IsExpanded)
            {
                tabContext.RunItemContainer.BorderThickness = new Thickness(1);
                tabContext.RunItemContainer.Opacity = 0.86;
            }
        };

        closeButton.Click += (_, args) =>
        {
            args.Handled = true;
            CloseOutputRun(tabContext);
        };

        currentOutputPanel.Runs.Insert(0, tabContext);
        currentOutputPanel.RunListPanel.Children.Insert(0, runItemContainer);
        UpdateOutputListPlaceholder(currentOutputPanel);

        tabContext.RunItemExpander.IsExpanded = true;
        outputTextBox.Focus();

        return tabContext;
    }

    private OutputGroupPanelContext GetOrCreateSelectedGroupOutputPanel()
    {
        var groupName = _selectedGroupName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            groupName = "General";
            _selectedGroupName = groupName;
        }

        if (_groupOutputPanels.TryGetValue(groupName, out var existingPanel))
        {
            return existingPanel;
        }

        var runListPanel = new StackPanel();

        var emptyListText = new TextBlock
        {
            Text = "No runs yet.",
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11,
            Visibility = Visibility.Visible
        };
        emptyListText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");

        var listStack = new StackPanel();
        listStack.Children.Add(runListPanel);
        listStack.Children.Add(emptyListText);

        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = listStack
        };

        var outputRoot = new Grid
        {
            Visibility = Visibility.Collapsed
        };

        var listPaneBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = listScroll
        };

        outputRoot.Children.Add(listPaneBorder);

        OutputTabHost.Children.Add(outputRoot);

        var panelContext = new OutputGroupPanelContext
        {
            RootGrid = outputRoot,
            RunListPanel = runListPanel,
            EmptyListText = emptyListText
        };

        _groupOutputPanels[groupName] = panelContext;
        return panelContext;
    }

    private void ShowSelectedGroupOutputTabControl()
    {
        foreach (var panel in _groupOutputPanels.Values)
        {
            panel.RootGrid.Visibility = Visibility.Collapsed;
        }

        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            return;
        }

        var selectedPanel = GetOrCreateSelectedGroupOutputPanel();
        selectedPanel.RootGrid.Visibility = Visibility.Visible;
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

    private void ExpandOutputRun(TaskTabContext runContext)
    {
        var panel = runContext.HostOutputPanel;

        foreach (var run in panel.Runs)
        {
            if (!ReferenceEquals(run, runContext))
            {
                run.RunItemExpander.IsExpanded = false;
                run.RunItemContainer.BorderThickness = new Thickness(1);
                run.RunItemContainer.Opacity = 0.86;
            }
        }

        runContext.RunItemContainer.BorderThickness = new Thickness(2);
        runContext.RunItemContainer.Opacity = 1.0;
    }

    private void UpdateOutputListPlaceholder(OutputGroupPanelContext panel)
    {
        panel.EmptyListText.Visibility = panel.Runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseOutputRun(TaskTabContext runContext)
    {
        var panel = runContext.HostOutputPanel;
        runContext.SpinnerTimer.Stop();

        panel.Runs.Remove(runContext);
        panel.RunListPanel.Children.Remove(runContext.RunItemContainer);

        if (panel.Runs.Count > 0 && !panel.Runs.Any(run => run.RunItemExpander.IsExpanded))
        {
            panel.Runs[0].RunItemExpander.IsExpanded = true;
        }

        UpdateOutputListPlaceholder(panel);
    }

    private Color GetTaskRunAccentColor(string taskName)
    {
        for (var displayIndex = 0; displayIndex < _visibleTaskIndexes.Count; displayIndex++)
        {
            var taskIndex = _visibleTaskIndexes[displayIndex];
            if (taskIndex < 0 || taskIndex >= _currentConfiguration.Tasks.Count)
            {
                continue;
            }

            var task = _currentConfiguration.Tasks[taskIndex];
            if (string.Equals(task.Name.Trim(), taskName, StringComparison.OrdinalIgnoreCase))
            {
                return PastelPalette[displayIndex % PastelPalette.Length];
            }
        }

        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(taskName);
        var index = Math.Abs(hash % PastelPalette.Length);
        return PastelPalette[index];
    }

    private static Brush CreateRunGradientBrush(Color leftColor, string seed)
    {
        var rightColor = BuildRandomizedGradientColor(leftColor, seed);
        var midColor = BlendColors(leftColor, rightColor, 0.45);
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            [
                new GradientStop(leftColor, 0.0),
                new GradientStop(midColor, 0.42),
                new GradientStop(rightColor, 1.0)
            ]
        };
    }

    private static Color BuildRandomizedGradientColor(Color baseColor, string seed)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(seed);
        var gradientTargets = new[]
        {
            Color.FromRgb(0x1D, 0x4E, 0x89),
            Color.FromRgb(0x3D, 0x34, 0x8A),
            Color.FromRgb(0x7A, 0x30, 0x74),
            Color.FromRgb(0x7C, 0x2D, 0x12),
            Color.FromRgb(0x0F, 0x76, 0x6E),
            Color.FromRgb(0x12, 0x5A, 0x4B),
            Color.FromRgb(0x5B, 0x21, 0xB6),
            Color.FromRgb(0x8B, 0x1E, 0x3F)
        };

        var target = gradientTargets[Math.Abs(hash % gradientTargets.Length)];
        return BlendColors(baseColor, target, 0.82);
    }

    private static Color BlendColors(Color left, Color right, double rightWeight)
    {
        var clampedRightWeight = Math.Max(0.0, Math.Min(1.0, rightWeight));
        var leftWeight = 1.0 - clampedRightWeight;

        var red = (byte)Math.Round((left.R * leftWeight) + (right.R * clampedRightWeight));
        var green = (byte)Math.Round((left.G * leftWeight) + (right.G * clampedRightWeight));
        var blue = (byte)Math.Round((left.B * leftWeight) + (right.B * clampedRightWeight));

        return Color.FromRgb(red, green, blue);
    }

    private RichTextBox CreateLogTextBox()
    {
        var outputFontFamily = TryFindResource("AppUiFontFamily") as FontFamily ?? new FontFamily("Segoe UI");

        var textBox = new RichTextBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            FontFamily = outputFontFamily,
            FontSize = 12,
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            FlowDirection = FlowDirection.LeftToRight,
            IsDocumentEnabled = false
        };
        textBox.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        textBox.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        textBox.SetResourceReference(Control.BorderBrushProperty, "PanelBorderBrush");
        textBox.Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Left
        };

        textBox.Loaded += (_, _) => UpdateOutputDocumentPageWidth(textBox);
        textBox.SizeChanged += (_, _) => UpdateOutputDocumentPageWidth(textBox);

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

    private static void UpdateOutputDocumentPageWidth(RichTextBox textBox)
    {
        if (textBox.Document is null)
        {
            return;
        }

        var availableWidth = Math.Max(120, textBox.ActualWidth - 24);
        textBox.Document.PageWidth = availableWidth;
        textBox.Document.ColumnGap = 0;
    }

    private void AppendProcessOutput(RichTextBox outputTextBox, string taskName, int exitCode, string standardOutput, string standardError)
    {
        AppendLog(outputTextBox, $"[{taskName}] Exit code: {exitCode}");

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            AppendLog(outputTextBox, "[stdout]");
            AppendAnsiText(outputTextBox, standardOutput.TrimEnd());
            AppendPlainText(outputTextBox, Environment.NewLine + Environment.NewLine);
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            AppendLog(outputTextBox, "[stderr]");
            AppendAnsiText(outputTextBox, standardError.TrimEnd());
            AppendPlainText(outputTextBox, Environment.NewLine + Environment.NewLine);
        }

        if (string.IsNullOrWhiteSpace(standardOutput) && string.IsNullOrWhiteSpace(standardError))
        {
            AppendLog(outputTextBox, "No output was produced.");
        }
    }

    private void AppendDataflowTrace(RichTextBox outputTextBox, string taskName, DataflowResolutionResult resolved)
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

    private void AppendPublishedOutputs(RichTextBox outputTextBox, string taskName, IReadOnlyDictionary<string, string> outputs)
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

    private void AppendLog(RichTextBox outputTextBox, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var paragraph = EnsureOutputParagraph(outputTextBox);
        AppendTimestampRun(paragraph, timestamp);
        paragraph.Inlines.Add(new Run(" "));
        AppendTaggedMessageRuns(paragraph, message);
        paragraph.Inlines.Add(new Run(Environment.NewLine + Environment.NewLine));
        outputTextBox.ScrollToEnd();
    }

    private void AppendStreamOutputLine(RichTextBox outputTextBox, string streamName, string line)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var paragraph = EnsureOutputParagraph(outputTextBox);
        AppendTimestampRun(paragraph, timestamp);
        paragraph.Inlines.Add(new Run(" "));

        var streamTagBrush = streamName.Equals("stderr", StringComparison.OrdinalIgnoreCase)
            ? StderrTagBrush
            : StdoutTagBrush;

        paragraph.Inlines.Add(new Run($"[{streamName}]")
        {
            Foreground = streamTagBrush,
            FontWeight = FontWeights.SemiBold
        });
        paragraph.Inlines.Add(new Run(" "));

        AppendAnsiText(outputTextBox, line);
        AppendPlainText(outputTextBox, Environment.NewLine + Environment.NewLine);
        outputTextBox.ScrollToEnd();
    }

    private static void AppendTimestampRun(Paragraph paragraph, string timestamp)
    {
        paragraph.Inlines.Add(new Run($"[{timestamp}]")
        {
            Foreground = TimestampBrush,
            FontWeight = FontWeights.SemiBold
        });
    }

    private static void AppendTaggedMessageRuns(Paragraph paragraph, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var index = 0;

        while (index < message.Length && message[index] == '[')
        {
            var closeBracket = message.IndexOf(']', index + 1);
            if (closeBracket < 0)
            {
                break;
            }

            var tag = message.Substring(index, closeBracket - index + 1);
            paragraph.Inlines.Add(new Run(tag)
            {
                Foreground = ResolveTagBrush(tag),
                FontWeight = FontWeights.SemiBold
            });

            index = closeBracket + 1;
            while (index < message.Length && message[index] == ' ')
            {
                paragraph.Inlines.Add(new Run(" "));
                index++;
            }
        }

        if (index < message.Length)
        {
            AppendOutcomeHighlightedText(paragraph, message[index..]);
        }
    }

    private static void AppendOutcomeHighlightedText(Paragraph paragraph, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var currentIndex = 0;
        foreach (Match match in OutcomeWordRegex.Matches(text))
        {
            if (match.Index > currentIndex)
            {
                paragraph.Inlines.Add(new Run(text[currentIndex..match.Index]));
            }

            var matchedText = match.Value;
            paragraph.Inlines.Add(new Run(matchedText)
            {
                Foreground = ResolveOutcomeWordBrush(matchedText),
                FontWeight = FontWeights.SemiBold
            });

            currentIndex = match.Index + match.Length;
        }

        if (currentIndex < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[currentIndex..]));
        }
    }

    private static Brush ResolveOutcomeWordBrush(string matchedText)
    {
        if (matchedText.Equals("passed", StringComparison.OrdinalIgnoreCase))
        {
            return PassedWordBrush;
        }

        if (matchedText.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return FailedWordBrush;
        }

        if (matchedText.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || matchedText.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return CancelledWordBrush;
        }

        return TimeoutWordBrush;
    }

    private static Brush ResolveTagBrush(string tag)
    {
        if (tag.Equals("[stdout]", StringComparison.OrdinalIgnoreCase))
        {
            return StdoutTagBrush;
        }

        if (tag.Equals("[stderr]", StringComparison.OrdinalIgnoreCase))
        {
            return StderrTagBrush;
        }

        if (tag.Equals("[Validation]", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationTagBrush;
        }

        return GenericTagBrush;
    }

    private static void AppendAnsiText(RichTextBox outputTextBox, string text)
    {
        var paragraph = EnsureOutputParagraph(outputTextBox);
        var style = new AnsiStyleState();
        var spanStart = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\u001b' || index + 1 >= text.Length || text[index + 1] != '[')
            {
                continue;
            }

            var commandEnd = index + 2;
            while (commandEnd < text.Length && text[commandEnd] != 'm')
            {
                commandEnd++;
            }

            if (commandEnd >= text.Length)
            {
                break;
            }

            var segmentLength = index - spanStart;
            if (segmentLength > 0)
            {
                AppendStyledRun(paragraph, text.Substring(spanStart, segmentLength), style);
            }

            var ansiPayload = text.Substring(index + 2, commandEnd - (index + 2));
            ApplyAnsiCodes(style, ansiPayload);

            index = commandEnd;
            spanStart = index + 1;
        }

        if (spanStart < text.Length)
        {
            AppendStyledRun(paragraph, text[spanStart..], style);
        }

        outputTextBox.ScrollToEnd();
    }

    private static void AppendPlainText(RichTextBox outputTextBox, string text)
    {
        var paragraph = EnsureOutputParagraph(outputTextBox);
        paragraph.Inlines.Add(new Run(text));
    }

    private static Paragraph EnsureOutputParagraph(RichTextBox outputTextBox)
    {
        outputTextBox.Document ??= new FlowDocument();
        if (outputTextBox.Document.Blocks.LastBlock is Paragraph paragraph)
        {
            return paragraph;
        }

        var newParagraph = new Paragraph
        {
            Margin = new Thickness(0)
        };
        outputTextBox.Document.Blocks.Add(newParagraph);
        return newParagraph;
    }

    private static void AppendStyledRun(Paragraph paragraph, string text, AnsiStyleState style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var run = new Run(text)
        {
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal
        };

        if (style.Foreground is not null)
        {
            run.Foreground = style.Foreground;
        }

        paragraph.Inlines.Add(run);
    }

    private static void ApplyAnsiCodes(AnsiStyleState style, string ansiPayload)
    {
        var codes = string.IsNullOrWhiteSpace(ansiPayload)
            ? [0]
            : ansiPayload.Split(';').Select(code => int.TryParse(code, out var parsed) ? parsed : -1);

        foreach (var code in codes)
        {
            switch (code)
            {
                case 0:
                    style.Foreground = null;
                    style.Bold = false;
                    break;
                case 1:
                    style.Bold = true;
                    break;
                case 22:
                    style.Bold = false;
                    break;
                case 39:
                    style.Foreground = null;
                    break;
                default:
                    if (TryMapAnsiForeground(code, out var brush))
                    {
                        style.Foreground = brush;
                    }
                    break;
            }
        }
    }

    private static bool TryMapAnsiForeground(int code, out Brush brush)
    {
        brush = Brushes.Transparent;

        brush = code switch
        {
            30 => Brushes.Black,
            31 => Brushes.IndianRed,
            32 => Brushes.ForestGreen,
            33 => Brushes.Goldenrod,
            34 => Brushes.SteelBlue,
            35 => Brushes.MediumVioletRed,
            36 => Brushes.Teal,
            37 => Brushes.LightGray,
            90 => Brushes.DimGray,
            91 => Brushes.OrangeRed,
            92 => Brushes.LimeGreen,
            93 => Brushes.Khaki,
            94 => Brushes.DeepSkyBlue,
            95 => Brushes.Orchid,
            96 => Brushes.DarkTurquoise,
            97 => Brushes.White,
            _ => Brushes.Transparent
        };

        return code is >= 30 and <= 37 or >= 90 and <= 97;
    }

    private sealed class AnsiStyleState
    {
        public Brush? Foreground { get; set; }

        public bool Bold { get; set; }
    }

    private static readonly Brush TimestampBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xB2));
    private static readonly Brush StdoutTagBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush StderrTagBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush ValidationTagBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
    private static readonly Brush GenericTagBrush = new SolidColorBrush(Color.FromRgb(0x26, 0xC6, 0xDA));
    private static readonly Brush PassedWordBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A));
    private static readonly Brush FailedWordBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
    private static readonly Brush TimeoutWordBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    private static readonly Brush CancelledWordBrush = new SolidColorBrush(Color.FromRgb(0xAB, 0x47, 0xBC));
    private static readonly Regex OutcomeWordRegex = new(@"\b(passed|failed|timeout|timed\s*out|cancelled|canceled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
