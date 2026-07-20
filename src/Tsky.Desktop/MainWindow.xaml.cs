using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;
using Microsoft.Win32;
using Tsky.Agent.V1;
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
    private string? _copiedTaskJson;
    private CopiedGroupPayload? _copiedGroupPayload;
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
    private readonly Dictionary<string, Button> _sidebarAgentButtonsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Grid> _taskCardsByIndex = [];
    private readonly Dictionary<string, Grid> _agentCardsById = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly AgentRegistryService _agentRegistryService = new();
    private readonly AgentGatewaySettingsService _agentGatewaySettingsService = new();
    private AgentRegistry _agentRegistry = new();
    private AgentGatewaySettings _agentGatewaySettings = new();
    private readonly AgentGatewayHost _agentGatewayHost;
    private readonly RemoteControlPlaneService _remoteControlPlaneService;
    private readonly Dictionary<string, RemoteAttemptHandle> _taskRemoteAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentTransientExecutionState> _agentTransientExecutionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<string>> _runningAttemptIdsByAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteAttemptTracking> _remoteAttemptTracking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AttemptBindingRecord> _attemptBindingByAttemptId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AttemptBindingRecord> _activeAttemptBindingByTaskStateKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingReconciliationAttempt> _pendingReconciliationByAttemptId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RichTextBox, OutputBudgetState> _outputBudgetByTextBox = new();
    private readonly DispatcherTimer _agentStatusRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isAgentViewSelected;
    private string? _selectedAgentId;
    private string? _selectedDispatchAgentId;
    private bool _isReplayingHistoryBatch;
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
    private string? _variablesEditorGroupName;
    private bool _isGraphPanning;
    private Point _graphPanStartPoint;
    private double _graphPanStartHorizontalOffset;
    private double _graphPanStartVerticalOffset;
    private int? _graphPointerDownTaskIndex;
    private Point _graphPointerDownPoint;
    private bool _graphPointerWasDrag;
    private bool _graphPointerActive;
    private CancellationTokenSource? _gatewayValidationCts;
    private bool _isGatewayToggleInProgress;
    private static readonly Brush RibbonHistoryActiveBrush = new SolidColorBrush(Color.FromRgb(0x27, 0xF6, 0xC0));
    private static readonly Brush RibbonListenerOnBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x43));
    private static readonly Brush RibbonListenerOnBorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0xA3, 0x63));
    private static readonly Brush RibbonListenerOffBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x68, 0x2A, 0x2A));
    private static readonly Brush RibbonListenerOffBorderBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x3A, 0x3A));
    private static readonly Brush RibbonListenerPendingBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x00));
    private static readonly Brush RibbonListenerPendingBorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x8F, 0x00));
    private static readonly string[] RunningIndicatorFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] SidebarRunningFrames = ["◐", "◓", "◑", "◒"];
    private const double GraphDragThreshold = 6;
    private const string AgentDispatchTelemetryCategory = "AgentDispatch";
    private const string AgentDispatchValidationEvent = "Validation";
    private const string AgentDispatchRequestEvent = "Request";
    private const string AgentDispatchResponseEvent = "Response";
    private const string AgentDispatchStateEvent = "State";
    private const string AgentDispatchBindingEvent = "Binding";
    private const string AgentDispatchActionEvent = "Action";
    private const int AgentDispatchPageJumpSize = 5;
    private const int MaxUiLogMessageChars = 12000;
    private const int MaxUiTraceLineChars = 900;
    private const int MaxUiPublishedValueChars = 1500;
    private const int MaxUiStreamLineChars = 6000;
    private const int MaxUiOutputCharsPerRun = 300000;
    private static readonly Brush AgentsDispatchRowBorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x3E));
    private static readonly Brush AgentsDispatchRowSelectedBorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0xF6, 0xC0));
    private static readonly Brush AgentsDispatchRowFocusedBorderBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0xC2, 0xFF));
    private static readonly Brush AgentsDispatchRowBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x29));
    private static readonly Brush AgentsDispatchRowSelectedBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x2B));
    private static readonly Brush AgentsDispatchRowFocusedBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2D, 0x33));
    private static readonly Brush AgentsDispatchPlayEnabledBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x19, 0xA8, 0x50));
    private static readonly Brush AgentsDispatchPlayEnabledBorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0xD7, 0x7A));
    private static readonly Brush AgentsDispatchPlayDisabledBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x3C));
    private static readonly Brush AgentsDispatchPlayDisabledBorderBrush = new SolidColorBrush(Color.FromRgb(0x49, 0x4F, 0x56));
    private static readonly Brush AgentsDispatchRunningBorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0xD7, 0x7A));

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

    private enum MainSurfaceKind
    {
        Tasks,
        Agents
    }

    private enum AgentTransientExecutionState
    {
        Idle,
        Dispatching,
        Running,
        Completed,
        Failed
    }

    private sealed class TaskTabContext
    {
        public required OutputGroupPanelContext HostOutputPanel { get; init; }

        public required string BaseTaskName { get; init; }

        public required DateTime StartedAtLocal { get; init; }

        public required Border RunItemContainer { get; init; }

        public required Expander RunItemExpander { get; init; }

        public required RichTextBox OutputTextBox { get; init; }

        public required TextBlock StatusText { get; init; }

        public required TextBlock TitleText { get; init; }

        public required DispatcherTimer SpinnerTimer { get; init; }

        public int SpinnerFrameIndex { get; set; }
    }

    private sealed class OutputBudgetState
    {
        public int TotalCharsAppended { get; set; }

        public bool LimitReachedLogged { get; set; }
    }

    private sealed class OutputGroupPanelContext
    {
        public required Grid RootGrid { get; init; }

        public required ScrollViewer RunListScrollViewer { get; init; }

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

    private sealed class CopiedGroupPayload
    {
        public required string SourceGroupName { get; init; }

        public required Dictionary<string, string> Variables { get; init; }

        public required List<LauncherTask> Tasks { get; init; }
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

    private sealed class RemoteAttemptHandle
    {
        public required string AgentId { get; init; }

        public required string RunId { get; init; }

        public required string AttemptId { get; init; }
    }

    private sealed class RemoteAttemptTracking
    {
        public required string AgentId { get; init; }

        public required string AttemptId { get; init; }

        public required string RunId { get; init; }

        public required string TaskName { get; init; }

        public required string TaskStateKey { get; init; }

        public required RichTextBox OutputTextBox { get; init; }

        public bool EndMarkerWritten { get; set; }

        public StringBuilder StandardOutputBuffer { get; } = new();

        public StringBuilder StandardErrorBuffer { get; } = new();

        public object SyncRoot { get; } = new();

        public TaskCompletionSource<RemoteAttemptResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CompletionSignaled { get; set; }
    }

    private sealed class RemoteAttemptResult
    {
        public bool Succeeded { get; init; }

        public int? ExitCode { get; init; }

        public TaskRunOutcome Outcome { get; init; }

        public string StandardOutput { get; init; } = string.Empty;

        public string StandardError { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public Dictionary<string, string> ProducedOutputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AttemptBindingRecord
    {
        public required string TaskStateKey { get; init; }

        public required string TaskName { get; init; }

        public required string AgentId { get; init; }

        public required string RunId { get; init; }

        public required string AttemptId { get; init; }

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    private sealed class PendingReconciliationAttempt
    {
        public required string AgentId { get; init; }

        public required string AttemptId { get; init; }

        public required string RunId { get; init; }

        public required string TaskName { get; init; }

        public required string TaskStateKey { get; init; }

        public required RichTextBox OutputTextBox { get; init; }

        public DateTime DisconnectedAtUtc { get; init; } = DateTime.UtcNow;
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

    private sealed class AgentCardVisual
    {
        public required string AgentId { get; init; }

        public required Border SelectionBorder { get; init; }
    }

    private sealed class AgentDispatchCommand
    {
        public required string AgentId { get; init; }
        public bool RunChain { get; init; }
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
        Color.FromRgb(0xB8, 0xE6, 0xFF),
        Color.FromRgb(0xFF, 0xE5, 0x8A),
        Color.FromRgb(0xFF, 0xD1, 0x77),
        Color.FromRgb(0xFF, 0xB8, 0x66),
        Color.FromRgb(0xFF, 0xA1, 0x54)
    ];

    public MainWindow()
    {
        InitializeComponent();

        _agentGatewaySettings = _agentGatewaySettingsService.Load();
        _agentGatewayHost = new AgentGatewayHost(
            _agentRegistryService,
            _agentGatewaySettings,
            runtimeEvent => Dispatcher.Invoke(() => HandleAgentRuntimeEvent(runtimeEvent)));
        _remoteControlPlaneService = new RemoteControlPlaneService(_agentGatewayHost, _agentRegistryService);

        _currentDirectoryConfigPath = TaskConfiguration.GetCurrentDirectoryConfigPath();
        _userProfileConfigPath = TaskConfiguration.GetUserProfileConfigPath();
        _sidebarStatusTimer.Tick += SidebarStatusTimer_Tick;
        _graphRenderDebounceTimer.Tick += GraphRenderDebounceTimer_Tick;
        _agentStatusRefreshTimer.Tick += AgentStatusRefreshTimer_Tick;
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
        TaskScrollViewer.MouseRightButtonUp += TaskScrollViewer_MouseRightButtonUp;
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
        TaskWorkspaceSurface.MouseRightButtonUp += TaskWorkspaceSurface_MouseRightButtonUp;
        PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
        SizeChanged += (_, _) => ScheduleConnectorRefresh();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(LogButtonAction), true);
        AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(LogMenuAction), true);
        CommandManager.AddPreviewExecutedHandler(this, CommandManager_PreviewExecuted);

        _uiState = UiStateStore.Load();
        LoadGroupOrderingFromUiState();
        ApplyPersistedUiState();
        ApplyHistoryDetailsVisibility();

        TaskConfiguration.EnsureValidFileOrReset(_userProfileConfigPath);
        LoadConfigurationWithValidation(_userProfileConfigPath, "Read user profile tasks.json");
        _agentRegistry = _agentRegistryService.Load();

        UpdateGatewayListenerToggleUi();
        _agentStatusRefreshTimer.Start();

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
        _agentStatusRefreshTimer.Stop();
        CommandManager.RemovePreviewExecutedHandler(this, CommandManager_PreviewExecuted);
        _ = _agentGatewayHost.StopAsync();
        PersistUiState();
    }

    private void LogButtonAction(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(source.Name) ? "(unnamed)" : source.Name;
        var text = ExtractActionText(source);
        DebugTraceLog.Write("UIAction", $"Button click: name={name}, text={text}");
    }

    private void LogMenuAction(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem menuItem)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(menuItem.Name) ? "(unnamed)" : menuItem.Name;
        var header = menuItem.Header?.ToString() ?? string.Empty;
        DebugTraceLog.Write("UIAction", $"Menu click: name={name}, header={header}");
    }

    private void CommandManager_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var commandName = e.Command switch
        {
            RoutedUICommand ui when !string.IsNullOrWhiteSpace(ui.Name) => ui.Name,
            RoutedCommand routed when !string.IsNullOrWhiteSpace(routed.Name) => routed.Name,
            _ => e.Command?.GetType().Name ?? "(unknown)"
        };

        DebugTraceLog.Write("UIAction", $"Command executed: {commandName}");
    }

    private static string ExtractActionText(FrameworkElement element)
    {
        return element switch
        {
            Button button when button.Content is string text => text,
            Button button when button.Content is TextBlock textBlock => textBlock.Text,
            MenuItem menuItem => menuItem.Header?.ToString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private void AgentStatusRefreshTimer_Tick(object? sender, EventArgs e)
    {
        HandleAgentRuntimeEvent();
    }

    private void HandleAgentRuntimeEvent(AgentGatewayRuntimeEvent? runtimeEvent = null)
    {
        if (runtimeEvent is not null)
        {
            HandleRemoteRuntimeFrame(runtimeEvent);
        }

        _agentGatewayHost.MarkKnownAgentsOfflineIfStale();

        var previous = _agentRegistry;

        var latest = _agentRegistryService.Load();

        RequestReconciliationForReconnectedAgents(previous, latest);

        ProcessRemoteAttemptCompletions(previous, latest);

        var rowsChanged = HaveAgentRowsChanged(previous, latest);

        // Always update the cached snapshot so non-visual consumers (e.g., dispatch row labels)
        // see the newest registry data even if we skip a rerender on this tick.
        _agentRegistry = latest;

        if (!rowsChanged)
        {
            return;
        }
        AgentsHeaderCountText.Text = $"({_agentRegistry.Agents.Count})";

        if (_isAgentViewSelected)
        {
            RenderAgentsView();
            UpdateSelectedAgentRuntimePanel();
            UpdateRibbonTaskActionState();
        }

        RebuildAgentsSidebarList();

        if (!_isAgentViewSelected && ReferenceEquals(BottomPaneTabControl?.SelectedItem, AgentsDispatchPaneTabItem))
        {
            RefreshAgentsDispatchPane();
        }
    }

    private void HandleRemoteRuntimeFrame(AgentGatewayRuntimeEvent runtimeEvent)
    {
        if (runtimeEvent.RunAccepted is not null)
        {
            HandleRemoteRunAccepted(runtimeEvent.AgentId, runtimeEvent.RunAccepted);
        }

        if (runtimeEvent.RunRejected is not null)
        {
            HandleRemoteRunRejected(runtimeEvent.AgentId, runtimeEvent.RunRejected);
        }

        if (runtimeEvent.RunOutput is not null)
        {
            AppendRemoteRunOutput(runtimeEvent.AgentId, runtimeEvent.RunOutput);
        }

        if (runtimeEvent.RunEvent is not null)
        {
            AppendRemoteRunEvent(runtimeEvent.AgentId, runtimeEvent.RunEvent);
        }

        if (runtimeEvent.RunCompleted is not null)
        {
            HandleRemoteRunCompleted(runtimeEvent.AgentId, runtimeEvent.RunCompleted);
        }

        if (runtimeEvent.ReconciliationResponse is not null)
        {
            HandleReconciliationResponse(runtimeEvent.AgentId, runtimeEvent.ReconciliationResponse);
        }

        if (runtimeEvent.ArtifactUploadCompleted is not null)
        {
            HandleRemoteArtifactUploadCompleted(runtimeEvent.AgentId, runtimeEvent.ArtifactUploadCompleted);
        }

        if (runtimeEvent.ArtifactUploadFailed is not null)
        {
            HandleRemoteArtifactUploadFailed(runtimeEvent.AgentId, runtimeEvent.ArtifactUploadFailed);
        }
    }

    private static void HandleRemoteArtifactUploadCompleted(string agentId, ArtifactUploadCompleted completed)
    {
        DebugTraceLog.Write(
            AgentDispatchTelemetryCategory,
            $"[ProtocolWarning] Received unsupported ArtifactUploadCompleted frame from agent='{NormalizeMarkerToken(agentId)}' artifact='{NormalizeMarkerToken(completed.ArtifactId)}' grant='{NormalizeMarkerToken(completed.GrantId)}' size={completed.SizeBytes}.");
    }

    private static void HandleRemoteArtifactUploadFailed(string agentId, ArtifactUploadFailed failed)
    {
        var errorCode = failed.Error?.Code;
        var errorMessage = failed.Error?.Message;
        DebugTraceLog.Write(
            AgentDispatchTelemetryCategory,
            $"[ProtocolWarning] Received unsupported ArtifactUploadFailed frame from agent='{NormalizeMarkerToken(agentId)}' artifact='{NormalizeMarkerToken(failed.ArtifactId)}' grant='{NormalizeMarkerToken(failed.GrantId)}' code='{NormalizeMarkerToken(errorCode)}' message='{NormalizeMarkerToken(errorMessage)}'.");
    }

    private void HandleRemoteRunAccepted(string agentId, RunAccepted runAccepted)
    {
        if (string.IsNullOrWhiteSpace(runAccepted.AttemptId)
            || !_remoteAttemptTracking.TryGetValue(runAccepted.AttemptId, out var tracking))
        {
            return;
        }

        AppendLog(
            tracking.OutputTextBox,
            $"EVENT task={NormalizeMarkerToken(tracking.TaskName)} agent={NormalizeMarkerToken(agentId)} run={NormalizeMarkerToken(runAccepted.RunId)} attempt={NormalizeMarkerToken(runAccepted.AttemptId)} type=RunAccepted");
    }

    private void HandleRemoteRunRejected(string agentId, RunRejected runRejected)
    {
        if (string.IsNullOrWhiteSpace(runRejected.AttemptId)
            || !_remoteAttemptTracking.TryGetValue(runRejected.AttemptId, out var tracking))
        {
            return;
        }

        var reason = runRejected.Error?.Message;
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = runRejected.Error?.Code;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "agent rejected offer";
        }

        AppendLog(
            tracking.OutputTextBox,
            AgentDispatchLifecycleMarkers.Error(tracking.TaskName, agentId, reason, tracking.AttemptId));
        WriteRemoteEndMarker(tracking, "Failed", reason);
        SetRemoteTabStopped(tracking.OutputTextBox, succeeded: false);
        TrySignalRemoteAttemptCompletion(
            tracking,
            new RemoteAttemptResult
            {
                Succeeded = false,
                ExitCode = null,
                Outcome = TaskRunOutcome.LaunchFailed,
                StandardOutput = GetBufferedRemoteStandardOutput(tracking),
                StandardError = GetBufferedRemoteStandardError(tracking),
                Message = reason
            });

        _pendingReconciliationByAttemptId.Remove(runRejected.AttemptId);
        _remoteAttemptTracking.Remove(runRejected.AttemptId);
        _taskRemoteAttempts.Remove(tracking.TaskStateKey);
        ClearAttemptBinding(tracking.TaskStateKey, runRejected.AttemptId);
        RemoveRunningAttempt(agentId, runRejected.AttemptId);

        var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
            string.Equals(GetTaskStateKey(task), tracking.TaskStateKey, StringComparison.OrdinalIgnoreCase));
        if (matchedTask is not null)
        {
            SetTaskRunState(matchedTask, TaskRunState.Failed);
        }

        SetAgentTransientExecutionState(agentId, AgentTransientExecutionState.Failed, "run_rejected_frame");
    }

    private void HandleReconciliationResponse(string agentId, ReconciliationResponse response)
    {
        var pendingForAgent = _pendingReconciliationByAttemptId.Values
            .Where(item => string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pendingForAgent.Count == 0)
        {
            return;
        }

        var reconciledByAttemptId = response.Attempts
            .Where(attempt => !string.IsNullOrWhiteSpace(attempt.AttemptId))
            .ToDictionary(attempt => attempt.AttemptId, StringComparer.OrdinalIgnoreCase);

        foreach (var pending in pendingForAgent)
        {
            var lifecycleContext = new AgentDispatchLifecycleContext(
                pending.TaskName,
                agentId,
                pending.RunId,
                pending.AttemptId);

            if (!reconciledByAttemptId.TryGetValue(pending.AttemptId, out var reconciledAttempt))
            {
                AppendLog(
                    pending.OutputTextBox,
                    AgentDispatchLifecycleMarkers.ReconcileUnrecovered(lifecycleContext, response.ReconciliationId));

                var unrecoveredTracking = _remoteAttemptTracking.TryGetValue(pending.AttemptId, out var unresolvedTracking)
                    ? unresolvedTracking
                    : null;

                _pendingReconciliationByAttemptId.Remove(pending.AttemptId);
                _remoteAttemptTracking.Remove(pending.AttemptId);
                _taskRemoteAttempts.Remove(pending.TaskStateKey);
                ClearAttemptBinding(pending.TaskStateKey, pending.AttemptId);
                SetRemoteTabStopped(pending.OutputTextBox, succeeded: false);

                var missingTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
                    string.Equals(GetTaskStateKey(task), pending.TaskStateKey, StringComparison.OrdinalIgnoreCase));
                if (missingTask is not null)
                {
                    SetTaskRunState(missingTask, TaskRunState.Failed);
                }

                SetAgentTransientExecutionState(agentId, AgentTransientExecutionState.Failed, "reconciliation_unrecovered");
                if (unrecoveredTracking is not null)
                {
                    TrySignalRemoteAttemptCompletion(
                        unrecoveredTracking,
                        new RemoteAttemptResult
                        {
                            Succeeded = false,
                            ExitCode = null,
                            Outcome = TaskRunOutcome.LaunchFailed,
                            StandardOutput = GetBufferedRemoteStandardOutput(unrecoveredTracking),
                            StandardError = GetBufferedRemoteStandardError(unrecoveredTracking),
                            Message = "attempt not found during reconciliation"
                        });
                }
                continue;
            }

            if (reconciledAttempt.State is RunAttemptState.Succeeded
                or RunAttemptState.Failed
                or RunAttemptState.Cancelled
                or RunAttemptState.Interrupted
                or RunAttemptState.TimedOut)
            {
                var terminalLabel = reconciledAttempt.State switch
                {
                    RunAttemptState.Succeeded => "Succeeded",
                    RunAttemptState.Cancelled => "Cancelled",
                    _ => "Failed"
                };

                AppendLog(
                    pending.OutputTextBox,
                    AgentDispatchLifecycleMarkers.ReconcileRecovered(lifecycleContext, terminalLabel, response.ReconciliationId));

                if (_remoteAttemptTracking.TryGetValue(pending.AttemptId, out var tracking) && !tracking.EndMarkerWritten)
                {
                    WriteRemoteEndMarker(tracking, terminalLabel, "recovered_by_reconciliation");
                }

                var recoveredTracking = _remoteAttemptTracking.TryGetValue(pending.AttemptId, out var completionTracking)
                    ? completionTracking
                    : null;

                _pendingReconciliationByAttemptId.Remove(pending.AttemptId);
                _remoteAttemptTracking.Remove(pending.AttemptId);
                _taskRemoteAttempts.Remove(pending.TaskStateKey);
                ClearAttemptBinding(pending.TaskStateKey, pending.AttemptId);
                SetRemoteTabStopped(pending.OutputTextBox, reconciledAttempt.State == RunAttemptState.Succeeded);

                var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
                    string.Equals(GetTaskStateKey(task), pending.TaskStateKey, StringComparison.OrdinalIgnoreCase));
                if (matchedTask is not null)
                {
                    SetTaskRunState(
                        matchedTask,
                        reconciledAttempt.State == RunAttemptState.Succeeded ? TaskRunState.Succeeded : TaskRunState.Failed);
                }

                SetAgentTransientExecutionState(
                    agentId,
                    reconciledAttempt.State == RunAttemptState.Succeeded ? AgentTransientExecutionState.Completed : AgentTransientExecutionState.Failed,
                    "reconciliation_recovered_terminal");
                if (recoveredTracking is not null)
                {
                    TrySignalRemoteAttemptCompletion(
                        recoveredTracking,
                        new RemoteAttemptResult
                        {
                            Succeeded = reconciledAttempt.State == RunAttemptState.Succeeded,
                            ExitCode = null,
                            Outcome = MapRemoteOutcome(reconciledAttempt.State),
                            StandardOutput = GetBufferedRemoteStandardOutput(recoveredTracking),
                            StandardError = GetBufferedRemoteStandardError(recoveredTracking),
                            Message = "terminal state recovered by reconciliation"
                        });
                }

                continue;
            }

            var inFlightState = reconciledAttempt.State.ToString();
            AppendLog(
                pending.OutputTextBox,
                AgentDispatchLifecycleMarkers.ReconcileUnrecoveredInFlight(
                    lifecycleContext,
                    inFlightState,
                    reconciledAttempt.ProcessIsKnownRunning,
                    response.ReconciliationId));
        }
    }

    private void RequestReconciliationForReconnectedAgents(AgentRegistry previous, AgentRegistry latest)
    {
        if (_pendingReconciliationByAttemptId.Count == 0)
        {
            return;
        }

        var reconnectedAgentIds = latest.Agents
            .Where(latestAgent =>
            {
                var previousAgent = previous.Agents.FirstOrDefault(candidate =>
                    string.Equals(candidate.AgentId, latestAgent.AgentId, StringComparison.OrdinalIgnoreCase));
                return previousAgent is not null
                    && !IsConnectedState(previousAgent.State)
                    && IsConnectedState(latestAgent.State);
            })
            .Select(agent => agent.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reconnectedAgentIds.Count == 0)
        {
            return;
        }

        var pendingForReconnectedAgents = _pendingReconciliationByAttemptId.Values
            .Where(item => reconnectedAgentIds.Contains(item.AgentId))
            .ToList();
        if (pendingForReconnectedAgents.Count == 0)
        {
            return;
        }

        var probeCount = _remoteControlPlaneService.RequestReconciliationAll();
        foreach (var pending in pendingForReconnectedAgents)
        {
            var lifecycleContext = new AgentDispatchLifecycleContext(
                pending.TaskName,
                pending.AgentId,
                pending.RunId,
                pending.AttemptId);
            AppendLog(
                pending.OutputTextBox,
                AgentDispatchLifecycleMarkers.ReconcileRequestedOnReconnect(lifecycleContext, probeCount));
        }
    }

    private void AppendRemoteRunOutput(string agentId, Tsky.Agent.V1.RunOutput runOutput)
    {
        if (string.IsNullOrWhiteSpace(runOutput.AttemptId)
            || !_remoteAttemptTracking.TryGetValue(runOutput.AttemptId, out var tracking))
        {
            return;
        }

        var streamName = runOutput.Stream switch
        {
            OutputStream.Stderr => "stderr",
            OutputStream.System => "system",
            _ => "stdout"
        };

        var payload = runOutput.Data?.ToStringUtf8() ?? string.Empty;
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        lock (tracking.SyncRoot)
        {
            if (runOutput.Stream == OutputStream.Stderr)
            {
                tracking.StandardErrorBuffer.Append(payload);
            }
            else if (runOutput.Stream == OutputStream.Stdout)
            {
                tracking.StandardOutputBuffer.Append(payload);
            }
        }

        var normalizedPayload = payload.Replace("\r\n", "\n");
        normalizedPayload = normalizedPayload.Replace('\r', '\n');
        var lines = normalizedPayload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            AppendStreamOutputLine(tracking.OutputTextBox, streamName, line);
        }
    }

    private void AppendRemoteRunEvent(string agentId, Tsky.Agent.V1.RunEvent runEvent)
    {
        if (string.IsNullOrWhiteSpace(runEvent.AttemptId)
            || !_remoteAttemptTracking.TryGetValue(runEvent.AttemptId, out var tracking))
        {
            return;
        }

        var eventType = runEvent.Type.ToString();
        var attemptState = runEvent.AttemptState.ToString();
        var stepToken = string.IsNullOrWhiteSpace(runEvent.StepId)
            ? "n/a"
            : NormalizeMarkerToken(runEvent.StepId);

        AppendLog(
            tracking.OutputTextBox,
            $"EVENT task={NormalizeMarkerToken(tracking.TaskName)} agent={NormalizeMarkerToken(agentId)} run={NormalizeMarkerToken(runEvent.RunId)} attempt={NormalizeMarkerToken(runEvent.AttemptId)} seq={runEvent.EventSequence} type={eventType} step={stepToken} state={attemptState}");
    }

    private void HandleRemoteRunCompleted(string agentId, Tsky.Agent.V1.RunCompleted runCompleted)
    {
        if (string.IsNullOrWhiteSpace(runCompleted.AttemptId)
            || !_remoteAttemptTracking.TryGetValue(runCompleted.AttemptId, out var tracking))
        {
            return;
        }

        var terminalState = runCompleted.TerminalState;
        var terminalLabel = terminalState switch
        {
            RunAttemptState.Succeeded => "Succeeded",
            RunAttemptState.Cancelled => "Cancelled",
            RunAttemptState.TimedOut => "Failed",
            RunAttemptState.Interrupted => "Failed",
            RunAttemptState.Failed => "Failed",
            _ => "Failed"
        };

        var reason = runCompleted.Error is not null && !string.IsNullOrWhiteSpace(runCompleted.Error.Message)
            ? runCompleted.Error.Message
            : "remote completion frame received";

        WriteRemoteEndMarker(tracking, terminalLabel, reason);
        SetRemoteTabStopped(tracking.OutputTextBox, terminalState == RunAttemptState.Succeeded);
        TrySignalRemoteAttemptCompletion(
            tracking,
            new RemoteAttemptResult
            {
                Succeeded = terminalState == RunAttemptState.Succeeded,
                ExitCode = runCompleted.HasExitCode ? runCompleted.ExitCode : null,
                Outcome = MapRemoteOutcome(terminalState),
                StandardOutput = GetBufferedRemoteStandardOutput(tracking),
                StandardError = GetBufferedRemoteStandardError(tracking),
                Message = reason,
                ProducedOutputs = ExtractProducedPortOutputs(runCompleted)
            });

        _pendingReconciliationByAttemptId.Remove(runCompleted.AttemptId);
        _remoteAttemptTracking.Remove(runCompleted.AttemptId);
        _taskRemoteAttempts.Remove(tracking.TaskStateKey);
        ClearAttemptBinding(tracking.TaskStateKey, runCompleted.AttemptId);
        RemoveRunningAttempt(agentId, runCompleted.AttemptId);

        var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
            string.Equals(GetTaskStateKey(task), tracking.TaskStateKey, StringComparison.OrdinalIgnoreCase));
        if (matchedTask is not null)
        {
            var succeeded = terminalState == RunAttemptState.Succeeded;
            SetTaskRunState(matchedTask, succeeded ? TaskRunState.Succeeded : TaskRunState.Failed);
        }

        var transientState = terminalState == RunAttemptState.Succeeded
            ? AgentTransientExecutionState.Completed
            : AgentTransientExecutionState.Failed;
        SetAgentTransientExecutionState(agentId, transientState, "run_completed_frame");
    }

    private void RemoveRunningAttempt(string agentId, string attemptId)
    {
        if (!_runningAttemptIdsByAgent.TryGetValue(agentId, out var queue) || queue.Count == 0)
        {
            return;
        }

        var retained = queue.Where(id => !string.Equals(id, attemptId, StringComparison.Ordinal)).ToArray();
        if (retained.Length == 0)
        {
            _runningAttemptIdsByAgent.Remove(agentId);
            return;
        }

        _runningAttemptIdsByAgent[agentId] = new Queue<string>(retained);
    }

    private static TaskRunOutcome MapRemoteOutcome(RunAttemptState terminalState)
    {
        return terminalState switch
        {
            RunAttemptState.Succeeded => TaskRunOutcome.Succeeded,
            RunAttemptState.Cancelled => TaskRunOutcome.Cancelled,
            RunAttemptState.TimedOut => TaskRunOutcome.TimedOut,
            RunAttemptState.Interrupted => TaskRunOutcome.FailedNonZeroExit,
            RunAttemptState.Failed => TaskRunOutcome.FailedNonZeroExit,
            _ => TaskRunOutcome.LaunchFailed
        };
    }

    private static Dictionary<string, string> ExtractProducedPortOutputs(RunCompleted runCompleted)
    {
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var produced in runCompleted.ProducedPortValues)
        {
            if (string.IsNullOrWhiteSpace(produced.PortId) || produced.Value is null)
            {
                continue;
            }

            outputs[produced.PortId] = ConvertPortValueToString(produced.Value);
        }

        return outputs;
    }

    private static string ConvertPortValueToString(PortValue value)
    {
        return value.ValueCase switch
        {
            PortValue.ValueOneofCase.StringValue => value.StringValue ?? string.Empty,
            PortValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            PortValue.ValueOneofCase.IntegerValue => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
            PortValue.ValueOneofCase.FloatingValue => value.FloatingValue.ToString(CultureInfo.InvariantCulture),
            PortValue.ValueOneofCase.DecimalValue => value.DecimalValue ?? string.Empty,
            _ => value.ToString()
        };
    }

    private static string GetBufferedRemoteStandardOutput(RemoteAttemptTracking tracking)
    {
        lock (tracking.SyncRoot)
        {
            return tracking.StandardOutputBuffer.ToString();
        }
    }

    private static string GetBufferedRemoteStandardError(RemoteAttemptTracking tracking)
    {
        lock (tracking.SyncRoot)
        {
            return tracking.StandardErrorBuffer.ToString();
        }
    }

    private static void TrySignalRemoteAttemptCompletion(RemoteAttemptTracking tracking, RemoteAttemptResult completion)
    {
        lock (tracking.SyncRoot)
        {
            if (tracking.CompletionSignaled)
            {
                return;
            }

            tracking.CompletionSignaled = true;
        }

        tracking.CompletionSource.TrySetResult(completion);
    }

    private void RegisterAttemptBinding(AttemptBindingRecord binding)
    {
        _attemptBindingByAttemptId[binding.AttemptId] = binding;
        _activeAttemptBindingByTaskStateKey[binding.TaskStateKey] = binding;
        LogAgentDispatchTelemetry(
            AgentDispatchBindingEvent,
            $"bind task={NormalizeMarkerToken(binding.TaskName)} taskKey={NormalizeMarkerToken(binding.TaskStateKey)} agent={NormalizeMarkerToken(binding.AgentId)} run={NormalizeMarkerToken(binding.RunId)} attempt={NormalizeMarkerToken(binding.AttemptId)}");
    }

    private bool TryGetActiveAttemptBindingForTask(string taskStateKey, out AttemptBindingRecord binding)
    {
        return _activeAttemptBindingByTaskStateKey.TryGetValue(taskStateKey, out binding!);
    }

    private void ClearAttemptBinding(string taskStateKey, string attemptId)
    {
        _attemptBindingByAttemptId.Remove(attemptId);

        if (_activeAttemptBindingByTaskStateKey.TryGetValue(taskStateKey, out var existing)
            && string.Equals(existing.AttemptId, attemptId, StringComparison.OrdinalIgnoreCase))
        {
            _activeAttemptBindingByTaskStateKey.Remove(taskStateKey);
            LogAgentDispatchTelemetry(
                AgentDispatchBindingEvent,
                $"unbind taskKey={NormalizeMarkerToken(taskStateKey)} attempt={NormalizeMarkerToken(attemptId)}");
        }
    }

    private void ProcessRemoteAttemptCompletions(AgentRegistry previous, AgentRegistry latest)
    {
        var activeAgentIds = _runningAttemptIdsByAgent.Keys.ToList();
        foreach (var agentId in activeAgentIds)
        {
            var previousAgent = previous.Agents.FirstOrDefault(agent =>
                string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            var latestAgent = latest.Agents.FirstOrDefault(agent =>
                string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

            if (latestAgent is null)
            {
                TransitionPendingAttemptsToTerminal(agentId, "Failed", "agent record removed during remote execution");
                continue;
            }

            var successfulDelta = Math.Max(0, latestAgent.SuccessfulRuns - (previousAgent?.SuccessfulRuns ?? 0));
            var failedDelta = Math.Max(0, latestAgent.FailedRuns - (previousAgent?.FailedRuns ?? 0));

            for (var i = 0; i < successfulDelta; i++)
            {
                CompleteNextRunningAttempt(agentId, succeeded: true, reason: "remote completion reported");
            }

            for (var i = 0; i < failedDelta; i++)
            {
                CompleteNextRunningAttempt(agentId, succeeded: false, reason: "remote completion reported");
            }

            if (!IsConnectedState(latestAgent.State) && _runningAttemptIdsByAgent.TryGetValue(agentId, out var pendingQueue) && pendingQueue.Count > 0)
            {
                TransitionPendingAttemptsOnDisconnect(agentId, "agent disconnected while run was active");
            }
        }
    }

    private void TransitionPendingAttemptsOnDisconnect(string agentId, string reason)
    {
        if (!_runningAttemptIdsByAgent.TryGetValue(agentId, out var pendingQueue) || pendingQueue.Count == 0)
        {
            return;
        }

        var reconciliationProbeCount = _remoteControlPlaneService.RequestReconciliationAll();

        while (pendingQueue.Count > 0)
        {
            var attemptId = pendingQueue.Dequeue();
            if (!_remoteAttemptTracking.TryGetValue(attemptId, out var tracking))
            {
                continue;
            }

            AppendLog(
                tracking.OutputTextBox,
                AgentDispatchLifecycleMarkers.ReconcileRequested(
                    new AgentDispatchLifecycleContext(tracking.TaskName, agentId, tracking.RunId, attemptId),
                    reconciliationProbeCount));
            AppendLog(
                tracking.OutputTextBox,
                AgentDispatchLifecycleMarkers.ReconcileAwaitingReconnect(
                    new AgentDispatchLifecycleContext(tracking.TaskName, agentId, tracking.RunId, attemptId)));

            _pendingReconciliationByAttemptId[attemptId] = new PendingReconciliationAttempt
            {
                AgentId = tracking.AgentId,
                AttemptId = tracking.AttemptId,
                RunId = tracking.RunId,
                TaskName = tracking.TaskName,
                TaskStateKey = tracking.TaskStateKey,
                OutputTextBox = tracking.OutputTextBox
            };

            TrySignalRemoteAttemptCompletion(
                tracking,
                new RemoteAttemptResult
                {
                    Succeeded = false,
                    ExitCode = null,
                    Outcome = TaskRunOutcome.LaunchFailed,
                    StandardOutput = GetBufferedRemoteStandardOutput(tracking),
                    StandardError = GetBufferedRemoteStandardError(tracking),
                    Message = reason
                });

            WriteRemoteEndMarker(tracking, "Unknown", "completion_not_inferred_after_disconnect");

            var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
                string.Equals(GetTaskStateKey(task), tracking.TaskStateKey, StringComparison.OrdinalIgnoreCase));
            if (matchedTask is not null)
            {
                // Keep UI conservative after disconnect: treat unresolved runs as failed/unknown.
                SetTaskRunState(matchedTask, TaskRunState.Failed);
            }
        }

        _runningAttemptIdsByAgent.Remove(agentId);
        SetAgentTransientExecutionState(agentId, AgentTransientExecutionState.Failed, reason + ":unknown_terminal_state");
    }

    private void CompleteNextRunningAttempt(string agentId, bool succeeded, string reason)
    {
        if (!_runningAttemptIdsByAgent.TryGetValue(agentId, out var pendingQueue) || pendingQueue.Count == 0)
        {
            return;
        }

        var attemptId = pendingQueue.Dequeue();
        if (pendingQueue.Count == 0)
        {
            _runningAttemptIdsByAgent.Remove(agentId);
        }

        if (!_remoteAttemptTracking.TryGetValue(attemptId, out var tracking))
        {
            SetAgentTransientExecutionState(
                agentId,
                succeeded ? AgentTransientExecutionState.Completed : AgentTransientExecutionState.Failed,
                $"completion_without_tracking:{reason}");
            return;
        }

        WriteRemoteEndMarker(tracking, succeeded ? "Succeeded" : "Failed", reason);
        SetRemoteTabStopped(tracking.OutputTextBox, succeeded);
        TrySignalRemoteAttemptCompletion(
            tracking,
            new RemoteAttemptResult
            {
                Succeeded = succeeded,
                ExitCode = null,
                Outcome = succeeded ? TaskRunOutcome.Succeeded : TaskRunOutcome.FailedNonZeroExit,
                StandardOutput = GetBufferedRemoteStandardOutput(tracking),
                StandardError = GetBufferedRemoteStandardError(tracking),
                Message = reason
            });

        var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
            string.Equals(GetTaskStateKey(task), tracking.TaskStateKey, StringComparison.OrdinalIgnoreCase));
        if (matchedTask is not null)
        {
            SetTaskRunState(matchedTask, succeeded ? TaskRunState.Succeeded : TaskRunState.Failed);
        }

        _remoteAttemptTracking.Remove(attemptId);
        _taskRemoteAttempts.Remove(tracking.TaskStateKey);
        ClearAttemptBinding(tracking.TaskStateKey, attemptId);
        SetAgentTransientExecutionState(
            agentId,
            succeeded ? AgentTransientExecutionState.Completed : AgentTransientExecutionState.Failed,
            reason);
    }

    private void TransitionPendingAttemptsToTerminal(string agentId, string endState, string reason)
    {
        if (!_runningAttemptIdsByAgent.TryGetValue(agentId, out var pendingQueue) || pendingQueue.Count == 0)
        {
            return;
        }

        while (pendingQueue.Count > 0)
        {
            var attemptId = pendingQueue.Dequeue();
            if (!_remoteAttemptTracking.TryGetValue(attemptId, out var tracking))
            {
                continue;
            }

            WriteRemoteEndMarker(tracking, endState, reason);
            SetRemoteTabStopped(tracking.OutputTextBox, string.Equals(endState, "Succeeded", StringComparison.OrdinalIgnoreCase));
            var isSuccessfulTerminal = string.Equals(endState, "Succeeded", StringComparison.OrdinalIgnoreCase);
            TrySignalRemoteAttemptCompletion(
                tracking,
                new RemoteAttemptResult
                {
                    Succeeded = isSuccessfulTerminal,
                    ExitCode = null,
                    Outcome = isSuccessfulTerminal ? TaskRunOutcome.Succeeded : TaskRunOutcome.FailedNonZeroExit,
                    StandardOutput = GetBufferedRemoteStandardOutput(tracking),
                    StandardError = GetBufferedRemoteStandardError(tracking),
                    Message = reason
                });
            _remoteAttemptTracking.Remove(attemptId);
            _taskRemoteAttempts.Remove(tracking.TaskStateKey);
            ClearAttemptBinding(tracking.TaskStateKey, attemptId);

            var matchedTask = _currentConfiguration.Tasks.FirstOrDefault(task =>
                string.Equals(GetTaskStateKey(task), tracking.TaskStateKey, StringComparison.OrdinalIgnoreCase));
            if (matchedTask is not null)
            {
                SetTaskRunState(matchedTask, TaskRunState.Failed);
            }
        }

        _runningAttemptIdsByAgent.Remove(agentId);
        SetAgentTransientExecutionState(agentId, AgentTransientExecutionState.Failed, reason);
    }

    private void WriteRemoteEndMarker(RemoteAttemptTracking tracking, string terminalState, string reason)
    {
        if (tracking.EndMarkerWritten)
        {
            return;
        }

        tracking.EndMarkerWritten = true;
        AppendLog(
            tracking.OutputTextBox,
            AgentDispatchLifecycleMarkers.End(
                new AgentDispatchLifecycleContext(tracking.TaskName, tracking.AgentId, tracking.RunId, tracking.AttemptId),
                terminalState,
                reason));
    }

    private void SetRemoteTabStopped(RichTextBox outputTextBox, bool succeeded)
    {
        if (!TryGetOutputTabContext(outputTextBox, out var tabContext))
        {
            return;
        }

        SetTabStopped(tabContext, succeeded);
    }

    private void SetAgentTransientExecutionState(string agentId, AgentTransientExecutionState state, string reason = "unspecified")
    {
        var previousState = _agentTransientExecutionStates.TryGetValue(agentId, out var existing)
            ? existing
            : AgentTransientExecutionState.Idle;

        var activeRunningAttempts = _runningAttemptIdsByAgent.TryGetValue(agentId, out var pendingQueue)
            ? pendingQueue.Count
            : 0;

        var coercedState = FromStateKind(
            AgentTransientStatePolicy.CoerceForConcurrency(
                ToStateKind(previousState),
                ToStateKind(state),
                activeRunningAttempts));

        if (coercedState != state)
        {
            LogAgentDispatchTelemetry(
                AgentDispatchStateEvent,
                $"agent={NormalizeMarkerToken(agentId)} from={previousState} requested={state} coerced={coercedState} activeRunningAttempts={activeRunningAttempts} reason={NormalizeMarkerToken(reason)} guard=concurrency");
        }

        state = coercedState;

        if (previousState == state)
        {
            return;
        }

        _agentTransientExecutionStates[agentId] = state;
        LogAgentDispatchTelemetry(
            AgentDispatchStateEvent,
            $"agent={NormalizeMarkerToken(agentId)} from={previousState} to={state} reason={NormalizeMarkerToken(reason)}");

        if (!_isAgentViewSelected && ReferenceEquals(BottomPaneTabControl?.SelectedItem, AgentsDispatchPaneTabItem))
        {
            RefreshAgentsDispatchPane();
        }
    }

    private static AgentTransientStateKind ToStateKind(AgentTransientExecutionState state)
    {
        return state switch
        {
            AgentTransientExecutionState.Idle => AgentTransientStateKind.Idle,
            AgentTransientExecutionState.Dispatching => AgentTransientStateKind.Dispatching,
            AgentTransientExecutionState.Running => AgentTransientStateKind.Running,
            AgentTransientExecutionState.Completed => AgentTransientStateKind.Completed,
            AgentTransientExecutionState.Failed => AgentTransientStateKind.Failed,
            _ => AgentTransientStateKind.Idle
        };
    }

    private static AgentTransientExecutionState FromStateKind(AgentTransientStateKind state)
    {
        return state switch
        {
            AgentTransientStateKind.Idle => AgentTransientExecutionState.Idle,
            AgentTransientStateKind.Dispatching => AgentTransientExecutionState.Dispatching,
            AgentTransientStateKind.Running => AgentTransientExecutionState.Running,
            AgentTransientStateKind.Completed => AgentTransientExecutionState.Completed,
            AgentTransientStateKind.Failed => AgentTransientExecutionState.Failed,
            _ => AgentTransientExecutionState.Idle
        };
    }

    private static bool HaveAgentRowsChanged(AgentRegistry current, AgentRegistry next)
    {
        if (current.Agents.Count != next.Agents.Count)
        {
            return true;
        }

        var orderedCurrent = current.Agents.OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase).ToList();
        var orderedNext = next.Agents.OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < orderedCurrent.Count; index++)
        {
            var left = orderedCurrent[index];
            var right = orderedNext[index];
            if (!string.Equals(left.AgentId, right.AgentId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
                || !string.Equals(left.Hostname, right.Hostname, StringComparison.Ordinal)
                || left.State != right.State
                || left.ActiveRuns != right.ActiveRuns
                || left.SuccessfulRuns != right.SuccessfulRuns
                || left.FailedRuns != right.FailedRuns
                || left.MaximumConcurrentRuns != right.MaximumConcurrentRuns
                || !string.Equals(left.LastSeenSummary, right.LastSeenSummary, StringComparison.Ordinal)
                || !string.Equals(left.ImplementationVersion, right.ImplementationVersion, StringComparison.Ordinal)
                || !string.Equals(left.Hostname, right.Hostname, StringComparison.Ordinal)
                || left.LastSeenAtUtc != right.LastSeenAtUtc)
            {
                return true;
            }
        }

        return false;
    }

    private void TryStartAgentGateway()
    {
        _ = StartAgentGatewayAsync().GetAwaiter().GetResult();
    }

    private void TryStopAgentGateway(bool requestDrain)
    {
        _ = StopAgentGatewayAsync(requestDrain).GetAwaiter().GetResult();
    }

    private async Task<bool> StartAgentGatewayAsync()
    {
        try
        {
            _agentGatewayHost.ApplySettings(_agentGatewaySettings);
            await _agentGatewayHost.StartAsync();
            return true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[AgentGateway] Failed to start local agent gateway");
            AppMessageBox.Show(this,
                "The local agent gateway could not start. Remote agent enrolment and live status are unavailable.\n\n"
                + ex.Message,
                "Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            UpdateGatewayListenerToggleUi();
        }
    }

    private async Task<bool> StopAgentGatewayAsync(bool requestDrain)
    {
        try
        {
            if (requestDrain && HasConnectedAgents())
            {
                await Task.Run(() => _agentGatewayHost.RequestDrainingFromAll(true));
            }

            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _agentGatewayHost.StopAsync(shutdownCts.Token);
            return true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[AgentGateway] Failed to stop local agent gateway");
            AppMessageBox.Show(
                this,
                "The local agent gateway could not be stopped.\n\n" + ex.Message,
                "Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            UpdateGatewayListenerToggleUi();
        }
    }

    private async void RibbonGatewayListenerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isGatewayToggleInProgress)
        {
            return;
        }

        _isGatewayToggleInProgress = true;
        UpdateGatewayListenerToggleUi();

        try
        {
            if (_agentGatewayHost.IsRunning)
            {
                await StopAgentGatewayAsync(requestDrain: true);
            }
            else
            {
                await StartAgentGatewayAsync();
            }
        }
        finally
        {
            _isGatewayToggleInProgress = false;
            UpdateGatewayListenerToggleUi();
        }
    }

    private void UpdateGatewayListenerToggleUi()
    {
        if (RibbonGatewayListenerToggleButton is null || RibbonGatewayListenerGlyphText is null || RibbonGatewayListenerLabel is null)
        {
            return;
        }

        RibbonGatewayListenerLabel.Text = "Listener";
        RibbonGatewayListenerToggleButton.IsEnabled = !_isGatewayToggleInProgress;

        if (_isGatewayToggleInProgress)
        {
            RibbonGatewayListenerGlyphText.Text = "⌛";
            RibbonGatewayListenerToggleButton.Background = RibbonListenerPendingBackgroundBrush;
            RibbonGatewayListenerToggleButton.BorderBrush = RibbonListenerPendingBorderBrush;
            RibbonGatewayListenerToggleButton.ToolTip = "Listener operation in progress";
            return;
        }

        if (_agentGatewayHost.IsRunning)
        {
            RibbonGatewayListenerGlyphText.Text = "👂";
            RibbonGatewayListenerToggleButton.Background = RibbonListenerOnBackgroundBrush;
            RibbonGatewayListenerToggleButton.BorderBrush = RibbonListenerOnBorderBrush;
            RibbonGatewayListenerToggleButton.ToolTip = "Stop local agent listener";
            return;
        }

        RibbonGatewayListenerGlyphText.Text = "🔇";
        RibbonGatewayListenerToggleButton.Background = RibbonListenerOffBackgroundBrush;
        RibbonGatewayListenerToggleButton.BorderBrush = RibbonListenerOffBorderBrush;
        RibbonGatewayListenerToggleButton.ToolTip = "Start local agent listener";
    }

    private bool HasConnectedAgents()
    {
        var latest = _agentRegistryService.Load();
        return latest.Agents.Any(agent =>
            !string.IsNullOrWhiteSpace(agent.SessionId)
            || IsConnectedState(agent.State));
    }

    private void OpenGatewaySettings()
    {
        if (!_isAgentViewSelected)
        {
            SelectAgentsView();
        }

        ApplyBottomPaneTabsForViewMode();
        BottomPaneTabControl.SelectedItem = GatewaySettingsPaneTabItem;
        UpdateGatewaySettingsPanel();
    }

    private async void ApplyGatewaySettingsFromTabButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadGatewaySettingsDraftFromTab();

        if (AreGatewaySettingsEquivalent(_agentGatewaySettings, settings))
        {
            UpdateGatewaySettingsApplyButtonState();
            return;
        }

        _gatewayValidationCts?.Cancel();
        _gatewayValidationCts?.Dispose();
        _gatewayValidationCts = new CancellationTokenSource();
        var validationToken = _gatewayValidationCts.Token;

        ApplyGatewaySettingsFromTabButton.IsEnabled = false;
        GatewaySettingsValidationText.Text = "Validating settings...";

        bool isValid;
        string validationError;
        try
        {
            (isValid, validationError) = await ValidateGatewaySettingsAsync(settings, validationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (validationToken.IsCancellationRequested)
        {
            return;
        }

        if (!isValid)
        {
            GatewaySettingsValidationText.Text = validationError;
            ApplyGatewaySettingsFromTabButton.IsEnabled = true;
            return;
        }

        GatewaySettingsValidationText.Text = string.Empty;

        _agentGatewaySettings = settings;
        _agentGatewaySettingsService.Save(_agentGatewaySettings);
        UpdateGatewaySettingsPanel();

        try
        {
            var stopped = await StopAgentGatewayAsync(requestDrain: false);
            if (!stopped)
            {
                return;
            }

            var started = await StartAgentGatewayAsync();
            if (!started)
            {
                return;
            }

            AppMessageBox.Show(
                this,
                "Gateway listener settings saved and applied.",
                "Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[AgentGateway] Failed to apply listener settings");
            AppMessageBox.Show(
                this,
                "Failed to apply listener settings.\n\n" + ex.Message,
                "Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            UpdateGatewayListenerToggleUi();
        }
    }

    private static bool AreGatewaySettingsEquivalent(AgentGatewaySettings left, AgentGatewaySettings right)
    {
        var leftHost = string.IsNullOrWhiteSpace(left.BindHost) ? "localhost" : left.BindHost.Trim();
        var rightHost = string.IsNullOrWhiteSpace(right.BindHost) ? "localhost" : right.BindHost.Trim();

        var leftPort = left.Port <= 0 ? AgentGatewayHost.DefaultPort : left.Port;
        var rightPort = right.Port <= 0 ? AgentGatewayHost.DefaultPort : right.Port;

        var leftCertPath = string.IsNullOrWhiteSpace(left.CertificatePath) ? null : left.CertificatePath.Trim();
        var rightCertPath = string.IsNullOrWhiteSpace(right.CertificatePath) ? null : right.CertificatePath.Trim();

        var leftCertPassword = string.IsNullOrWhiteSpace(left.CertificatePassword) ? null : left.CertificatePassword;
        var rightCertPassword = string.IsNullOrWhiteSpace(right.CertificatePassword) ? null : right.CertificatePassword;

        return left.EnableTlsListener == right.EnableTlsListener
            && string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase)
            && leftPort == rightPort
            && string.Equals(leftCertPath, rightCertPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftCertPassword, rightCertPassword, StringComparison.Ordinal)
            && left.IsDevLoopbackFallbackEnabled == right.IsDevLoopbackFallbackEnabled;
    }

    private void GatewayBrowseCertButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PFX Certificate",
            Filter = "PFX certificate (*.pfx)|*.pfx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var existingPath = GatewayCertPathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            try
            {
                var fullPath = System.IO.Path.GetFullPath(existingPath);
                var directory = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                }

                var fileName = System.IO.Path.GetFileName(fullPath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    dialog.FileName = fileName;
                }
            }
            catch
            {
                // Keep default file dialog location if current value is not a valid path.
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            GatewayCertPathTextBox.Text = dialog.FileName;
            UpdateGatewaySettingsApplyButtonState();
        }
    }

    private void GatewaySettingsTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateGatewaySettingsApplyButtonState();
    }

    private void GatewaySettingsPasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateGatewaySettingsApplyButtonState();
    }

    private void GatewaySettingsToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdateGatewaySettingsApplyButtonState();
    }

    private void RequestAgentReconciliation()
    {
        var count = _remoteControlPlaneService.RequestReconciliationAll();
        AppMessageBox.Show(
            this,
            count > 0
                ? $"Requested reconciliation from {count} active agent session(s)."
                : "No active agent sessions are connected.",
            "Reconciliation",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

        _uiState.SelectedGroupName = string.IsNullOrWhiteSpace(_selectedGroupName)
            ? null
            : NormalizeGroupName(_selectedGroupName);

        _uiState.HistoryListPaneRatio = _historyListPaneRatio;

        // Preserve dialog-specific persisted dimensions written during this app session.
        var currentPersistedState = UiStateStore.Load();
        _uiState.AddTaskDialogWidth = currentPersistedState.AddTaskDialogWidth;
        _uiState.AddTaskDialogHeight = currentPersistedState.AddTaskDialogHeight;

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
        AgentsHeaderCountText.Text = $"({_agentRegistry.Agents.Count})";
        UpdateAgentsSidebarVisual();
        RebuildAgentsSidebarList();
        if (groupNames.Count == 0)
        {
            _selectedGroupName = null;
        }
        else if (string.IsNullOrWhiteSpace(_selectedGroupName) || !groupNames.Contains(_selectedGroupName, StringComparer.OrdinalIgnoreCase))
        {
            var persistedSelection = NormalizeGroupName(_uiState.SelectedGroupName);
            _selectedGroupName = groupNames.Contains(persistedSelection, StringComparer.OrdinalIgnoreCase)
                ? groupNames.First(name => string.Equals(name, persistedSelection, StringComparison.OrdinalIgnoreCase))
                : groupNames[0];
        }

        RebuildGroupsMenu(groupNames);
        RebuildGroupNavigator(GetGroupSidebarItems(groupNames));
        if (_isAgentViewSelected)
        {
            RenderAgentsView();
            ApplyGroupViewState();
            UpdateRibbonTaskActionState();
            return;
        }

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
            headerButton.PreviewMouseRightButtonUp += GroupHeaderButton_PreviewMouseRightButtonUp;

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

    private void SidebarAgentsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (AgentsSectionChevronText is not null)
        {
            AgentsSectionChevronText.Text = "▼";
        }

        RebuildAgentsSidebarList();
    }

    private void SidebarAgentsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (AgentsSectionChevronText is not null)
        {
            AgentsSectionChevronText.Text = "▶";
        }
    }

    private void AgentsSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        SelectAgentsView();
    }

    private void SidebarAgentRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string agentId })
        {
            return;
        }

        _selectedAgentId = agentId;
        SelectAgentsView();
    }

    private void AgentsSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RebuildAgentsSidebarList();
    }

    private void AgentsSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox searchTextBox)
        {
            return;
        }

        if (!string.IsNullOrEmpty(searchTextBox.Text))
        {
            searchTextBox.Clear();
        }

        e.Handled = true;
    }

    private void RebuildAgentsSidebarList()
    {
        if (SidebarAgentsListPanel is null)
        {
            return;
        }

        SidebarAgentsListPanel.Children.Clear();
        _sidebarAgentButtonsById.Clear();

        var searchTerm = AgentsSearchTextBox?.Text?.Trim();

        var orderedAgents = _agentRegistry.Agents
            .OrderBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            orderedAgents = orderedAgents
                .Where(agent =>
                    agent.EffectiveDisplayName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || agent.AgentId.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(agent.Hostname)
                        && agent.Hostname.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (orderedAgents.Count == 0)
        {
            SidebarAgentsListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(searchTerm)
                    ? "No agents configured."
                    : "No agents match your search.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81)),
                Margin = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var agent in orderedAgents)
        {
            var connected = IsConnectedState(agent.State);
            var row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(0)
            };

            var statusGlyph = new TextBlock
            {
                Text = connected ? "●" : "○",
                Foreground = connected
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0xD7, 0x7A))
                    : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                Width = 14,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0)
            };
            DockPanel.SetDock(statusGlyph, Dock.Left);

            var nameText = new TextBlock
            {
                Text = agent.EffectiveDisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            row.Children.Add(statusGlyph);
            row.Children.Add(nameText);

            var button = new Button
            {
                Tag = agent.AgentId,
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                ToolTip = $"Open Agents view and select {agent.EffectiveDisplayName}"
            };
            button.Click += SidebarAgentRowButton_Click;

            SidebarAgentsListPanel.Children.Add(button);
            _sidebarAgentButtonsById[agent.AgentId] = button;
        }

        UpdateAgentSidebarSelectionVisuals();
    }

    private void UpdateAgentSidebarSelectionVisuals()
    {
        foreach (var button in _sidebarAgentButtonsById.Values)
        {
            if (button.Tag is not string agentId)
            {
                continue;
            }

            button.Background = string.Equals(agentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A))
                : Brushes.Transparent;
        }
    }

    private void SelectAgentsView()
    {
        if (SidebarAgentsExpander is not null && !SidebarAgentsExpander.IsExpanded)
        {
            SidebarAgentsExpander.IsExpanded = true;
        }

        _isAgentViewSelected = true;
        _preferGroupDeleteContext = false;
        StopGraphPanning();
        ClearGraphPointerInteraction();
        RenderAgentsView();
        ApplyGroupViewState();
        ApplyBottomPaneTabsForViewMode();
        UpdateSelectedAgentRuntimePanel();
        UpdateAgentSidebarSelectionVisuals();
        UpdateAgentsSidebarVisual();
        UpdateRibbonTaskActionState();
    }

    private void SelectTasksView()
    {
        if (!_isAgentViewSelected)
        {
            return;
        }

        _isAgentViewSelected = false;
        ApplyBottomPaneTabsForViewMode();
        UpdateAgentsSidebarVisual();
    }

    private void UpdateAgentsSidebarVisual()
    {
        if (AgentsSidebarButton is null)
        {
            return;
        }

        AgentsSidebarButton.Background = _isAgentViewSelected
            ? new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1A))
            : Brushes.Transparent;
    }

    private void RenderAgentsView()
    {
        TaskPanel.Children.Clear();
        TaskGraphCanvas.Children.Clear();
        TaskGraphEdgeOverlay.Children.Clear();
        TaskConnectorOverlay.Children.Clear();
        _taskCardsByIndex.Clear();
        _agentCardsById.Clear();
        _graphNodesByIndex.Clear();
        _taskRunningOverlays.Clear();
        _visibleTaskIndexes.Clear();
        _selectedTaskIndex = null;
        TaskPanel.AllowDrop = false;
        SyncTaskSurfaceToViewport();
        ApplyTaskSurfaceModeVisibility();

        GroupTitleText.Text = "Agents";
        GroupMetaText.Text = $"{_agentRegistry.Agents.Count} registered definition(s)";
        EmptyStateText.Text = "No agents are configured. Use Add to create a pending enrolment.";
        EmptyStateText.Visibility = _agentRegistry.Agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_agentRegistry.Agents.Count == 0)
        {
            _selectedAgentId = null;
            UpdateAgentSidebarSelectionVisuals();
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedAgentId)
            || !_agentRegistry.Agents.Any(agent => string.Equals(agent.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedAgentId = _agentRegistry.Agents[0].AgentId;
        }

        var orderedAgents = _agentRegistry.Agents
            .OrderBy(agent => agent.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < orderedAgents.Count; index++)
        {
            TaskPanel.Children.Add(CreateAgentCard(orderedAgents[index], PastelPalette[index % PastelPalette.Length]));
        }

        ApplyAgentCardSelectionVisuals();
        UpdateSelectedAgentRuntimePanel();
        UpdateAgentSidebarSelectionVisuals();
    }

    private Grid CreateAgentCard(AgentDefinition agent, Color cardColor)
    {
        var foregroundBrush = GetReadableTextBrush(cardColor);
        var cardFontFamily = TryFindResource("CardUiFontFamily") as FontFamily ?? new FontFamily("Segoe UI");
        var card = new Grid
        {
            Width = 195,
            Height = 195,
            Margin = new Thickness(0, 0, 21, 21)
        };

        var selectionBorder = new Border
        {
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            IsHitTestVisible = false
        };
        selectionBorder.SetResourceReference(Border.BorderBrushProperty, "ConnectorFocusedBrush");

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = agent.EffectiveDisplayName,
            FontWeight = FontWeights.Bold,
            FontFamily = cardFontFamily,
            FontSize = 16,
            Foreground = foregroundBrush,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 44
        };

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 18, 30, 42)),
            Padding = new Thickness(10, 8, 42, 8),
            Child = title
        };
        Grid.SetRow(titleBar, 0);

        var status = new TextBlock
        {
            Text = BuildAgentStatusText(agent),
            FontFamily = cardFontFamily,
            FontSize = 12.5,
            Foreground = foregroundBrush,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.92,
            MaxHeight = 92
        };
        var statusContainer = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Child = status
        };
        Grid.SetRow(statusContainer, 1);

        var footer = new TextBlock
        {
            Text = BuildAgentFooterGlyphText(agent),
            FontFamily = cardFontFamily,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = foregroundBrush,
            Opacity = 0.86,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 42, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(footer, 2);

        body.Children.Add(titleBar);
        body.Children.Add(statusContainer);
        body.Children.Add(footer);

        var button = new Button
        {
            Content = body,
            Tag = agent.AgentId,
            Style = (Style)Resources["TaskCardButtonStyle"],
            Background = new SolidColorBrush(cardColor),
            Foreground = foregroundBrush,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        button.Click += AgentCardButton_Click;
        button.PreviewMouseRightButtonUp += AgentCardButton_PreviewMouseRightButtonUp;

        var menuButton = new Button
        {
            Content = "⋮",
            Tag = agent.AgentId,
            Style = (Style)Resources["TaskMenuButtonStyle"],
            Background = new SolidColorBrush(cardColor),
            BorderBrush = new SolidColorBrush(cardColor),
            Foreground = foregroundBrush
        };
        menuButton.Click += AgentMenuButton_Click;

        card.Tag = new AgentCardVisual
        {
            AgentId = agent.AgentId,
            SelectionBorder = selectionBorder
        };
        card.Children.Add(button);
        card.Children.Add(selectionBorder);
        card.Children.Add(menuButton);
        _agentCardsById[agent.AgentId] = card;
        return card;
    }

    private static string BuildAgentStatusText(AgentDefinition agent)
    {
        var state = agent.State.ToString().ToLowerInvariant();
        var pendingText = agent.State is AgentDefinitionState.Pending or AgentDefinitionState.Enrolling
            ? agent.EnrolmentTokenExpiresAtUtc is { } expiresAt
                ? $"Token expires {expiresAt.ToLocalTime():g}"
                : "No active enrolment token"
            : null;

        var causeDetail = string.IsNullOrWhiteSpace(agent.LastStateReasonCode)
            ? null
            : string.IsNullOrWhiteSpace(agent.LastStateReasonMessage)
                ? $"cause: {agent.LastStateReasonCode}"
                : $"cause: {agent.LastStateReasonCode} ({agent.LastStateReasonMessage})";
        var protocolDetail = string.IsNullOrWhiteSpace(agent.ProtocolMismatchDetail)
            ? null
            : $"protocol: {agent.ProtocolMismatchDetail}";

        var liveTarget = string.IsNullOrWhiteSpace(agent.Hostname)
            ? "host n/a"
            : agent.Hostname;
        var version = string.IsNullOrWhiteSpace(agent.ImplementationVersion)
            ? "version n/a"
            : $"v{agent.ImplementationVersion}";

        var lines = new List<string>
        {
            state,
            $"{liveTarget} · {version}",
            $"runs: {agent.ActiveRuns}/{agent.MaximumConcurrentRuns}"
        };

        if (!string.IsNullOrWhiteSpace(pendingText))
        {
            lines.Insert(1, pendingText);
        }

        if (!string.IsNullOrWhiteSpace(causeDetail))
        {
            lines.Add(causeDetail);
        }

        if (!string.IsNullOrWhiteSpace(protocolDetail))
        {
            lines.Add(protocolDetail);
        }

        return string.Join("\n", lines);
    }

    private static string BuildAgentFooterGlyphText(AgentDefinition agent)
    {
        var connected = IsConnectedState(agent.State);
        var sessionActive = connected || !string.IsNullOrWhiteSpace(agent.SessionId);
        var connectivityGlyph = connected ? "●" : "○";
        var connectivityLabel = connected ? "online" : "offline";
        var sessionGlyph = sessionActive ? "◉" : "◌";
        var sessionLabel = sessionActive ? "active" : "inactive";
        return $"{connectivityGlyph} {connectivityLabel}  {sessionGlyph} session {sessionLabel}";
    }

    private static bool IsConnectedState(AgentDefinitionState state)
    {
        return state is AgentDefinitionState.Online
            or AgentDefinitionState.Busy
            or AgentDefinitionState.Draining
            or AgentDefinitionState.Degraded
            or AgentDefinitionState.Incompatible;
    }

    private void AgentCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string agentId })
        {
            return;
        }

        _selectedAgentId = agentId;
        ApplyAgentCardSelectionVisuals();
        UpdateSelectedAgentRuntimePanel();
        UpdateAgentSidebarSelectionVisuals();
    }

    private void AgentCardButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string agentId } button)
        {
            return;
        }

        _selectedAgentId = agentId;
        ApplyAgentCardSelectionVisuals();
        UpdateSelectedAgentRuntimePanel();
        UpdateAgentSidebarSelectionVisuals();
        OpenContextMenuAtPointer(CreateAgentContextMenu(agentId), button);
        e.Handled = true;
    }

    private void AgentMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string agentId } button)
        {
            return;
        }

        _selectedAgentId = agentId;
        ApplyAgentCardSelectionVisuals();
        UpdateSelectedAgentRuntimePanel();
        UpdateAgentSidebarSelectionVisuals();
        OpenContextMenuAtButton(CreateAgentContextMenu(agentId), button);
    }

    private ContextMenu CreateAgentContextMenu(string agentId)
    {
        var menu = CreateRibbonContextMenu();
        var agent = FindAgent(agentId);

        var editItem = new MenuItem { Header = "Edit", Style = (Style)Resources["AppMenuItemStyle"], IsEnabled = agent is not null };
        editItem.Click += (_, _) => EditAgent(agentId);

        var newTokenItem = new MenuItem { Header = "Generate Enrolment Command", Style = (Style)Resources["AppMenuItemStyle"], IsEnabled = agent is not null };
        newTokenItem.Click += (_, _) => RegenerateAgentEnrolment(agentId);

        var disableItem = new MenuItem
        {
            Header = "Disable Agent",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = agent is not null && agent.State != AgentDefinitionState.Disabled
        };
        disableItem.Click += (_, _) => DisableAgent(agentId);

        var revokeItem = new MenuItem
        {
            Header = "Revoke Agent",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = agent is not null && agent.State != AgentDefinitionState.Revoked
        };
        revokeItem.Click += (_, _) => RevokeAgent(agentId);

        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)Resources["AppMenuItemStyle"], IsEnabled = agent is not null };
        deleteItem.Click += (_, _) => DeleteAgent(agentId);

        menu.Items.Add(editItem);
        menu.Items.Add(newTokenItem);
        menu.Items.Add(disableItem);
        menu.Items.Add(revokeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void ApplyAgentCardSelectionVisuals()
    {
        foreach (var card in _agentCardsById.Values)
        {
            if (card.Tag is not AgentCardVisual visual)
            {
                continue;
            }

            visual.SelectionBorder.BorderThickness =
                string.Equals(visual.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase)
                    ? new Thickness(2)
                    : new Thickness(0);
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

        // Always route through SelectGroup so clicking a task in the sidebar
        // reliably switches from Agents view back to the selected group's task view.
        SelectGroup(targetGroup);

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

    private void SelectGroup(string groupName)
    {
        SelectTasksView();
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
        ApplyBottomPaneTabsForViewMode();
    }

    private void ApplyBottomPaneTabsForViewMode()
    {
        if (BottomPaneTabControl is null)
        {
            return;
        }

        var inAgentView = _isAgentViewSelected;
        InputPortsPaneTabItem.Visibility = inAgentView ? Visibility.Collapsed : Visibility.Visible;
        OutputPortsPaneTabItem.Visibility = inAgentView ? Visibility.Collapsed : Visibility.Visible;
        VariablesPaneTabItem.Visibility = inAgentView ? Visibility.Collapsed : Visibility.Visible;
        AgentsDispatchPaneTabItem.Visibility = inAgentView ? Visibility.Collapsed : Visibility.Visible;
        AgentRuntimePaneTabItem.Visibility = inAgentView ? Visibility.Visible : Visibility.Collapsed;
        GatewaySettingsPaneTabItem.Visibility = inAgentView ? Visibility.Visible : Visibility.Collapsed;

        UpdateGatewaySettingsPanel();

        if (inAgentView)
        {
            if (!ReferenceEquals(BottomPaneTabControl.SelectedItem, AgentRuntimePaneTabItem)
                && !ReferenceEquals(BottomPaneTabControl.SelectedItem, GatewaySettingsPaneTabItem))
            {
                BottomPaneTabControl.SelectedItem = AgentRuntimePaneTabItem;
            }
            return;
        }

        if (ReferenceEquals(BottomPaneTabControl.SelectedItem, AgentRuntimePaneTabItem)
            || ReferenceEquals(BottomPaneTabControl.SelectedItem, GatewaySettingsPaneTabItem))
        {
            BottomPaneTabControl.SelectedItem = OutputPaneTabItem;
        }

        if (ReferenceEquals(BottomPaneTabControl.SelectedItem, AgentsDispatchPaneTabItem))
        {
            RefreshAgentsDispatchPane();
        }
    }

    private void UpdateSelectedAgentRuntimePanel()
    {
        if (!_isAgentViewSelected)
        {
            return;
        }

        var selectedAgent = _agentRegistry.Agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, _selectedAgentId, StringComparison.OrdinalIgnoreCase));

        if (selectedAgent is null)
        {
            AgentRuntimeTitleText.Text = "Select an agent";
            AgentRuntimeMetaText.Text = string.Empty;
            AgentRuntimeActiveRunsText.Text = "-";
            AgentRuntimeSessionText.Text = "-";
            AgentRuntimeConnectivityText.Text = "-";
            AgentRuntimeSuccessfulText.Text = "-";
            AgentRuntimeFailedText.Text = "-";
            return;
        }

        var connected = IsConnectedState(selectedAgent.State);
        var sessionActive = connected || !string.IsNullOrWhiteSpace(selectedAgent.SessionId);

        AgentRuntimeTitleText.Text = selectedAgent.EffectiveDisplayName;
        AgentRuntimeMetaText.Text = $"{selectedAgent.AgentId} · {(selectedAgent.ImplementationVersion is { Length: > 0 } ? "v" + selectedAgent.ImplementationVersion : "version n/a")}";
        AgentRuntimeActiveRunsText.Text = $"{selectedAgent.ActiveRuns}/{selectedAgent.MaximumConcurrentRuns}";
        AgentRuntimeSessionText.Text = sessionActive ? "Active" : "Inactive";
        AgentRuntimeConnectivityText.Text = connected ? "Online" : "Offline";
        AgentRuntimeSuccessfulText.Text = selectedAgent.SuccessfulRuns.ToString(CultureInfo.InvariantCulture);
        AgentRuntimeFailedText.Text = selectedAgent.FailedRuns.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateGatewaySettingsPanel()
    {
        if (GatewaySettingsSummaryText is null)
        {
            return;
        }

        GatewayEnableTlsCheckBox.IsChecked = _agentGatewaySettings.EnableTlsListener;
        GatewayHostTextBox.Text = string.IsNullOrWhiteSpace(_agentGatewaySettings.BindHost) ? "localhost" : _agentGatewaySettings.BindHost;
        GatewayPortTextBox.Text = (_agentGatewaySettings.Port <= 0 ? AgentGatewayHost.DefaultPort : _agentGatewaySettings.Port).ToString(CultureInfo.InvariantCulture);
        GatewayCertPathTextBox.Text = _agentGatewaySettings.CertificatePath ?? string.Empty;
        GatewayCertPasswordBox.Password = _agentGatewaySettings.CertificatePassword ?? string.Empty;
        GatewayDevFallbackCheckBox.IsChecked = _agentGatewaySettings.IsDevLoopbackFallbackEnabled;

        var scheme = _agentGatewaySettings.EnableTlsListener ? "https" : "http";
        var host = string.IsNullOrWhiteSpace(_agentGatewaySettings.BindHost) ? "localhost" : _agentGatewaySettings.BindHost;
        var port = _agentGatewaySettings.Port <= 0 ? AgentGatewayHost.DefaultPort : _agentGatewaySettings.Port;
        GatewaySettingsSummaryText.Text = $"Controller endpoint advertised to agents: {scheme}://{host}:{port}";

        if (_agentGatewaySettings.EnableTlsListener)
        {
            var certState = string.IsNullOrWhiteSpace(_agentGatewaySettings.CertificatePath)
                ? "Certificate path: not configured"
                : $"Certificate path: {_agentGatewaySettings.CertificatePath}";
            var fallback = _agentGatewaySettings.IsDevLoopbackFallbackEnabled
                ? "HTTP loopback fallback: enabled"
                : "HTTP loopback fallback: disabled";
            GatewaySettingsStatusText.Text = certState + "\n" + fallback;
            UpdateGatewaySettingsApplyButtonState();
            return;
        }

        GatewaySettingsStatusText.Text = "Traffic is not TLS-encrypted. Enable TLS listener and configure a PFX certificate for encrypted transport.";
        UpdateGatewaySettingsApplyButtonState();
    }

    private void UpdateGatewaySettingsApplyButtonState()
    {
        if (ApplyGatewaySettingsFromTabButton is null || GatewaySettingsValidationText is null)
        {
            return;
        }

        var pendingSettings = ReadGatewaySettingsDraftFromTab();
        var hasChanges = !AreGatewaySettingsEquivalent(_agentGatewaySettings, pendingSettings);

        // Keep typing/toggling responsive by validating only when Save and Apply is clicked.
        GatewaySettingsValidationText.Text = string.Empty;
        ApplyGatewaySettingsFromTabButton.IsEnabled = hasChanges;
    }

    private AgentGatewaySettings ReadGatewaySettingsDraftFromTab()
    {
        var host = string.IsNullOrWhiteSpace(GatewayHostTextBox.Text)
            ? "localhost"
            : GatewayHostTextBox.Text.Trim();

        var port = int.TryParse(GatewayPortTextBox.Text.Trim(), out var parsedPort)
            ? parsedPort
            : _agentGatewaySettings.Port;

        var certPath = string.IsNullOrWhiteSpace(GatewayCertPathTextBox.Text)
            ? null
            : GatewayCertPathTextBox.Text.Trim();

        var certPassword = string.IsNullOrWhiteSpace(GatewayCertPasswordBox.Password)
            ? null
            : GatewayCertPasswordBox.Password;

        return new AgentGatewaySettings
        {
            EnableTlsListener = GatewayEnableTlsCheckBox.IsChecked == true,
            BindHost = host,
            Port = port,
            CertificatePath = certPath,
            CertificatePassword = certPassword,
            IsDevLoopbackFallbackEnabled = GatewayDevFallbackCheckBox.IsChecked == true
        };
    }

    private static bool TryValidateGatewaySettings(AgentGatewaySettings settings, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (settings.Port <= 0 || settings.Port > 65535)
        {
            errorMessage = "Port must be between 1 and 65535.";
            return false;
        }

        var host = string.IsNullOrWhiteSpace(settings.BindHost) ? "localhost" : settings.BindHost.Trim();
        if (!TryValidateGatewayHost(host, out errorMessage))
        {
            return false;
        }

        if (!settings.EnableTlsListener)
        {
            return true;
        }

        var certPath = string.IsNullOrWhiteSpace(settings.CertificatePath)
            ? null
            : settings.CertificatePath.Trim();

        if (string.IsNullOrWhiteSpace(certPath))
        {
            errorMessage = "TLS is enabled. Select a PFX certificate file.";
            return false;
        }

        if (!File.Exists(certPath))
        {
            errorMessage = "PFX certificate file does not exist.";
            return false;
        }

        try
        {
            _ = new X509Certificate2(certPath, settings.CertificatePassword);
        }
        catch
        {
            errorMessage = "Unable to load the certificate with the provided password.";
            return false;
        }

        return true;
    }

    private static Task<(bool IsValid, string ErrorMessage)> ValidateGatewaySettingsAsync(AgentGatewaySettings settings, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isValid = TryValidateGatewaySettings(settings, out var errorMessage);
            cancellationToken.ThrowIfCancellationRequested();
            return (isValid, errorMessage);
        }, cancellationToken);
    }

    private static bool TryValidateGatewayHost(string host, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            errorMessage = "Host is required.";
            return false;
        }

        if (host is "localhost" or "*" or "+" or "0.0.0.0" or "::" or "[::]")
        {
            return true;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        try
        {
            var resolved = Dns.GetHostAddresses(host);
            if (resolved.Length > 0)
            {
                return true;
            }
        }
        catch
        {
            // handled below
        }

        errorMessage = "Host name could not be resolved.";
        return false;
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

        try
        {
            _isHandlingBottomPanePortTabSelection = true;

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, InputPortsPaneTabItem))
            {
                if (!_selectedTaskIndex.HasValue || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
                {
                    return;
                }

                OpenInputPortsEditor(_selectedTaskIndex.Value);
            }
            else if (ReferenceEquals(BottomPaneTabControl.SelectedItem, OutputPortsPaneTabItem))
            {
                if (!_selectedTaskIndex.HasValue || !_visibleTaskIndexes.Contains(_selectedTaskIndex.Value))
                {
                    return;
                }

                OpenOutputPortsEditor(_selectedTaskIndex.Value);
            }
            else if (ReferenceEquals(BottomPaneTabControl.SelectedItem, VariablesPaneTabItem))
            {
                OpenVariablesEditor(selectTab: false);
            }
            else if (ReferenceEquals(BottomPaneTabControl.SelectedItem, AgentsDispatchPaneTabItem))
            {
                RefreshAgentsDispatchPane();
            }
        }
        finally
        {
            _isHandlingBottomPanePortTabSelection = false;
        }
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

    private void ToggleHistoryGroupingButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void ApplyHistoryGroupingToggleUi()
    {
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
        var selectedItems = HistoryListBox.SelectedItems.OfType<HistoryListItem>().ToList();
        var hasSelection = selectedItems.Count > 0;
        var hasReplayableSelection = hasSelection;

        ReplaySelectedButton.IsEnabled = hasReplayableSelection && !_isReplayingHistoryBatch;
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
            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, VariablesPaneTabItem))
            {
                OpenVariablesEditor(selectTab: false);
            }
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

            var variableUsages = TaskVariableService.GetGroupVariableUsages(task, GetGroupVariablesForGroup(task.Group));
            if (variableUsages.Count > 0)
            {
                bottomGlyphs.Children.Add(new TextBlock
                {
                    Text = "𝑥",
                    FontFamily = new FontFamily("Cambria Math"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = foregroundBrush,
                    Opacity = 0.9,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    ToolTip = BuildTaskVariableUsageTooltip(variableUsages)
                });
            }

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
            button.PreviewMouseRightButtonUp += TaskCard_PreviewMouseRightButtonUp;
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

        if (ReferenceEquals(BottomPaneTabControl.SelectedItem, VariablesPaneTabItem))
        {
            OpenVariablesEditor(selectTab: false);
        }
    }

    private void ApplyTaskSurfaceModeVisibility()
    {
        var isTaskMode = _isAgentViewSelected || _taskSurfaceViewMode == TaskSurfaceViewMode.Task;

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
            OpenTaskRightClickContextMenu(nodeButton, nodeTaskIndex);
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

    private static T? FindVisualDescendant<T>(DependencyObject? start) where T : DependencyObject
    {
        if (start is null)
        {
            return null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(start); i++)
        {
            var child = VisualTreeHelper.GetChild(start, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
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
        OpenContextMenuAtButton(menu, anchorButton);
    }

    private void GroupHeaderButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button headerButton || headerButton.Tag is not string groupName)
        {
            return;
        }

        var normalizedGroupName = NormalizeGroupName(groupName);
        _selectedGroupName = normalizedGroupName;
        DebugTraceLog.Write($"[GroupContextMenu] Open request for group '{normalizedGroupName}' at pointer.");

        var menu = CreateGroupHeaderContextMenu(normalizedGroupName);
        OpenContextMenuAtPointer(menu, headerButton);
        e.Handled = true;
    }

    private ContextMenu CreateGroupHeaderContextMenu(string groupName)
    {
        var menu = CreateRibbonContextMenu();

        var copyItem = new MenuItem
        {
            Header = "Copy Group",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        copyItem.Click += (_, _) => CopyGroup(groupName);

        var pasteItem = new MenuItem
        {
            Header = "Paste Group",
            IsEnabled = _copiedGroupPayload is not null,
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        pasteItem.Click += (_, _) => PasteCopiedGroup();

        var deleteItem = new MenuItem
        {
            Header = "Delete Group",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        deleteItem.Click += (_, _) =>
        {
            _selectedGroupName = NormalizeGroupName(groupName);
            DebugTraceLog.Write($"[GroupContextMenu] Delete requested for group '{_selectedGroupName}'.");
            TryDeleteSelectedGroup();
        };

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void OpenTaskRightClickContextMenu(FrameworkElement anchorElement, int taskIndex)
    {
        var menu = CreateTaskRightClickContextMenu(taskIndex);
        OpenContextMenuAtPointer(menu, anchorElement);
    }

    private ContextMenu CreateTaskContextMenu(int taskIndex)
    {
        var menu = CreateRibbonContextMenu();

        var editItem = new MenuItem { Header = "Edit", Style = (Style)Resources["AppMenuItemStyle"] };
        editItem.Click += (_, _) => EditTask(taskIndex);

        var toggleItem = new MenuItem
        {
            Header = IsTaskEnabled(_currentConfiguration.Tasks[taskIndex]) ? "Disable" : "Enable",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        toggleItem.Click += (_, _) => ToggleTaskEnabled(taskIndex);

        var copyItem = new MenuItem
        {
            Header = "Copy",
            InputGestureText = "Ctrl+C",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        copyItem.Click += (_, _) => CopyTask(taskIndex);

        var pasteItem = new MenuItem
        {
            Header = "Paste",
            InputGestureText = "Ctrl+V",
            IsEnabled = CanPasteTaskIntoCurrentGroup(),
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        pasteItem.Click += (_, _) => PasteTaskIntoSelectedGroup();

        var replayEntry = GetLastSnapshotEntry(_currentConfiguration.Tasks[taskIndex]);
        var replayItem = new MenuItem
        {
            Header = "Replay Last Snapshot",
            IsEnabled = replayEntry is not null,
            Style = (Style)Resources["AppMenuItemStyle"]
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

        var runChainItem = new MenuItem
        {
            Header = "Run Chain (Same Group)",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        runChainItem.IsEnabled = _visibleTaskIndexes.Any(index => IsTaskEnabled(_currentConfiguration.Tasks[index]));
        runChainItem.Click += async (_, _) =>
        {
            if (!IsValidTaskIndex(taskIndex))
            {
                AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _ = await RunTaskChainFromTaskAsync(taskIndex);
        };

        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)Resources["AppMenuItemStyle"] };
        deleteItem.Click += (_, _) => DeleteTask(taskIndex);

        menu.Items.Add(editItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(replayItem);
        menu.Items.Add(runChainItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        return menu;
    }

    private ContextMenu CreateMainViewContextMenu()
    {
        if (_isAgentViewSelected)
        {
            var agentMenu = CreateRibbonContextMenu();
            var selectedAgent = string.IsNullOrWhiteSpace(_selectedAgentId) ? null : FindAgent(_selectedAgentId);
            var addItem = new MenuItem { Header = "Add Agent", Style = (Style)Resources["AppMenuItemStyle"] };
            addItem.Click += (_, _) => AddAgent();

            var editItem = new MenuItem
            {
                Header = "Edit Agent",
                IsEnabled = !string.IsNullOrWhiteSpace(_selectedAgentId),
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            editItem.Click += (_, _) => TryEditSelectedAgent();

            var deleteItem = new MenuItem
            {
                Header = "Delete Agent",
                IsEnabled = !string.IsNullOrWhiteSpace(_selectedAgentId),
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            deleteItem.Click += (_, _) => TryDeleteSelectedAgent();

            var disableItem = new MenuItem
            {
                Header = "Disable Agent",
                IsEnabled = selectedAgent is not null && selectedAgent.State != AgentDefinitionState.Disabled,
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            disableItem.Click += (_, _) => TryDisableSelectedAgent();

            var revokeItem = new MenuItem
            {
                Header = "Revoke Agent",
                IsEnabled = selectedAgent is not null && selectedAgent.State != AgentDefinitionState.Revoked,
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            revokeItem.Click += (_, _) => TryRevokeSelectedAgent();

            var reconcileItem = new MenuItem
            {
                Header = "Request Reconciliation",
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            reconcileItem.Click += (_, _) => RequestAgentReconciliation();

            var gatewaySettingsItem = new MenuItem
            {
                Header = "Gateway Listener Settings",
                Style = (Style)Resources["AppMenuItemStyle"]
            };
            gatewaySettingsItem.Click += (_, _) => OpenGatewaySettings();

            agentMenu.Items.Add(addItem);
            agentMenu.Items.Add(editItem);
            agentMenu.Items.Add(disableItem);
            agentMenu.Items.Add(revokeItem);
            agentMenu.Items.Add(reconcileItem);
            agentMenu.Items.Add(gatewaySettingsItem);
            agentMenu.Items.Add(new Separator());
            agentMenu.Items.Add(deleteItem);
            return agentMenu;
        }

        var menu = CreateRibbonContextMenu();
        var hasSelectedTask = TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex);

        var copyItem = new MenuItem
        {
            Header = "Copy",
            InputGestureText = "Ctrl+C",
            IsEnabled = hasSelectedTask,
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        copyItem.Click += (_, _) =>
        {
            if (hasSelectedTask)
            {
                CopyTask(selectedTaskIndex);
            }
        };

        var pasteItem = new MenuItem
        {
            Header = "Paste",
            InputGestureText = "Ctrl+V",
            IsEnabled = CanPasteTaskIntoCurrentGroup(),
            Style = (Style)Resources["AppMenuItemStyle"]
        };

        menu.Items.Add(copyItem);
        pasteItem.Click += (_, _) => PasteTaskIntoSelectedGroup();
        menu.Items.Add(pasteItem);
        return menu;
    }

    private ContextMenu CreateTaskRightClickContextMenu(int taskIndex)
    {
        var menu = CreateRibbonContextMenu();

        var copyItem = new MenuItem
        {
            Header = "Copy",
            InputGestureText = "Ctrl+C",
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        copyItem.Click += (_, _) => CopyTask(taskIndex);

        var pasteItem = new MenuItem
        {
            Header = "Paste",
            InputGestureText = "Ctrl+V",
            IsEnabled = CanPasteTaskIntoCurrentGroup(),
            Style = (Style)Resources["AppMenuItemStyle"]
        };
        pasteItem.Click += (_, _) => PasteTaskIntoSelectedGroup();

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        return menu;
    }

    private void OpenContextMenuAtButton(ContextMenu menu, FrameworkElement anchorElement)
    {
        try
        {
            anchorElement.ContextMenu = menu;
            menu.PlacementTarget = anchorElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[ContextMenu] Failed to open context menu at button anchor");
            AppMessageBox.Show(this, $"Could not open the menu.\n{ex.Message}", "Menu Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenContextMenuAtPointer(ContextMenu menu, FrameworkElement anchorElement)
    {
        try
        {
            anchorElement.ContextMenu = menu;
            menu.PlacementTarget = anchorElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[ContextMenu] Failed to open context menu at mouse pointer");
            AppMessageBox.Show(this, $"Could not open the menu.\n{ex.Message}", "Menu Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TaskWorkspaceSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        if (_taskSurfaceViewMode == TaskSurfaceViewMode.Task && _visibleTaskIndexes.Count > 0)
        {
            _preferGroupDeleteContext = false;
        }

        var menu = CreateMainViewContextMenu();
        OpenContextMenuAtPointer(menu, anchor);
        e.Handled = true;
    }

    private void TaskScrollViewer_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = CreateMainViewContextMenu();
        OpenContextMenuAtPointer(menu, anchor);
        e.Handled = true;
    }

    private void TaskCard_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button
            || button.Parent is not Grid card
            || card.Tag is not TaskCardVisual visual)
        {
            return;
        }

        _selectedTaskIndex = visual.TaskIndex;
        ApplyTaskCardSelectionVisuals();

        if (!IsValidTaskIndex(visual.TaskIndex))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }

        OpenTaskRightClickContextMenu(button, visual.TaskIndex);
        e.Handled = true;
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

    private void OpenVariablesEditor(bool selectTab = true)
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            VariablesEditorTitleText.Text = "Variables - No Group";
            VariablesRowsPanel.Children.Clear();
            VariablesRowsPanel.Children.Add(CreateVariableRowControl(string.Empty, string.Empty));
            _variablesEditorGroupName = null;

            if (selectTab)
            {
                BottomPaneTabControl.SelectedItem = VariablesPaneTabItem;
            }

            return;
        }

        var groupName = NormalizeGroupName(_selectedGroupName);
        _variablesEditorGroupName = groupName;
        VariablesEditorTitleText.Text = $"Variables - {groupName}";
        VariablesRowsPanel.Children.Clear();

        var variables = GetGroupVariablesForGroup(groupName);
        foreach (var pair in variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            VariablesRowsPanel.Children.Add(CreateVariableRowControl(pair.Key, pair.Value));
        }

        if (VariablesRowsPanel.Children.Count == 0)
        {
            VariablesRowsPanel.Children.Add(CreateVariableRowControl(string.Empty, string.Empty));
        }

        if (selectTab)
        {
            BottomPaneTabControl.SelectedItem = VariablesPaneTabItem;
        }
    }

    private Grid CreateVariableRowControl(string name, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = CreatePortEditorTextBox(name, "Variable name");
        var valueBox = CreatePortEditorTextBox(value, "Variable value");
        var removeButton = CreateRemovePortRowButton(() => VariablesRowsPanel.Children.Remove(row));

        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(removeButton, 2);

        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(removeButton);
        return row;
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

    private void AddVariableRowButton_Click(object sender, RoutedEventArgs e)
    {
        VariablesRowsPanel.Children.Add(CreateVariableRowControl(string.Empty, string.Empty));
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

    private void SaveVariablesButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_variablesEditorGroupName))
        {
            LogPortEditorError("Select a group before saving variables.");
            return;
        }

        if (!TryCollectVariableRows(out var variables, out var error))
        {
            LogPortEditorError(error);
            return;
        }

        var groupName = NormalizeGroupName(_variablesEditorGroupName);
        var groupDefinition = GetOrCreateGroupDefinition(groupName);
        groupDefinition.Variables = variables;
        SaveTasksAndRefresh("Save Variables");
        OpenVariablesEditor();
    }

    private bool TryCollectVariableRows(out Dictionary<string, string> variables, out string error)
    {
        variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        foreach (var child in VariablesRowsPanel.Children)
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

                error = "Variable row has value but no variable name.";
                return false;
            }

            if (variables.ContainsKey(name))
            {
                error = $"Duplicate variable name '{name}'.";
                return false;
            }

            variables[name] = value;
        }

        return true;
    }

    private LauncherTaskGroup GetOrCreateGroupDefinition(string groupName)
    {
        var normalizedGroup = NormalizeGroupName(groupName);
        var existing = _currentConfiguration.Groups.FirstOrDefault(group =>
            string.Equals(NormalizeGroupName(group.Name), normalizedGroup, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return existing;
        }

        var created = new LauncherTaskGroup
        {
            Name = normalizedGroup,
            Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Tasks = []
        };

        _currentConfiguration.Groups.Add(created);
        return created;
    }

    private Dictionary<string, string> GetGroupVariablesForGroup(string? groupName)
    {
        var normalizedGroup = NormalizeGroupName(groupName);
        var variables = _currentConfiguration.Groups.FirstOrDefault(group =>
            string.Equals(NormalizeGroupName(group.Name), normalizedGroup, StringComparison.OrdinalIgnoreCase))?.Variables;

        if (variables is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
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

    private ExecutionTargetSelection ResolveExecutionTargetSelection(LauncherTask task)
    {
        var mode = (task.ExecutionTargetMode ?? "local").Trim().ToLowerInvariant();
        var selection = new ExecutionTargetSelection();

        if (mode == "agent")
        {
            selection.Mode = ExecutionTargetMode.SpecificAgent;
            selection.AgentId = task.TargetAgentId;
            return selection;
        }

        if (mode == "pool")
        {
            selection.Mode = ExecutionTargetMode.AgentPool;
            selection.PoolId = task.TargetPoolId;
            return selection;
        }

        if (mode == "auto")
        {
            selection.Mode = ExecutionTargetMode.Automatic;
            return selection;
        }

        var normalizedGroup = NormalizeGroupName(task.Group);
        var group = _currentConfiguration.Groups.FirstOrDefault(candidate =>
            string.Equals(NormalizeGroupName(candidate.Name), normalizedGroup, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            var groupMode = (group.ExecutionTargetMode ?? "local").Trim().ToLowerInvariant();
            if (groupMode == "agent")
            {
                selection.Mode = ExecutionTargetMode.SpecificAgent;
                selection.AgentId = group.TargetAgentId;
                return selection;
            }

            if (groupMode == "pool")
            {
                selection.Mode = ExecutionTargetMode.AgentPool;
                selection.PoolId = group.TargetPoolId;
                return selection;
            }

            if (groupMode == "auto")
            {
                selection.Mode = ExecutionTargetMode.Automatic;
                return selection;
            }
        }

        return selection;
    }

    private bool TryCancelTaskExecution(LauncherTask task, out string message)
    {
        var stateKey = GetTaskStateKey(task);

        if (_taskRemoteAttempts.TryGetValue(stateKey, out var remoteAttempt))
        {
            var cancelled = _remoteControlPlaneService.TryCancel(
                remoteAttempt.AgentId,
                remoteAttempt.RunId,
                remoteAttempt.AttemptId,
                out message);
            if (cancelled)
            {
                _taskRemoteAttempts.Remove(stateKey);
            }

            return cancelled;
        }

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
        if (_isAgentViewSelected)
        {
            AddAgent();
            return;
        }

        var groupNames = GetGroupNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defaultGroupName = string.IsNullOrWhiteSpace(_selectedGroupName)
            ? "General"
            : NormalizeGroupName(_selectedGroupName);

        var window = new TaskFormWindow(groupNames, defaultGroupName) { Owner = this };

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

    private void AddAgent()
    {
        var draft = _agentRegistryService.CreateDraft();
        var window = new AgentDefinitionWindow(draft, isNew: true) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var result = _agentRegistryService.AddOrUpdate(_agentRegistry, window.Agent, window.RegenerateEnrolmentToken);
        _selectedAgentId = result.Agent.AgentId;
        _agentRegistry = _agentRegistryService.Load();
        RenderAgentsView();
        AgentsHeaderCountText.Text = $"({_agentRegistry.Agents.Count})";
        if (result.EnrolmentSecret is not null)
        {
            ShowEnrolmentCommand(result.Agent, result.EnrolmentSecret);
        }
    }

    private void TryEditSelectedAgent()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            AppMessageBox.Show(this, "No agent is selected.", "Edit Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EditAgent(_selectedAgentId);
    }

    private void EditAgent(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
        {
            AppMessageBox.Show(this, "The selected agent is no longer available.", "Edit Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new AgentDefinitionWindow(agent, isNew: false) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var result = _agentRegistryService.AddOrUpdate(_agentRegistry, window.Agent, window.RegenerateEnrolmentToken);
        _selectedAgentId = result.Agent.AgentId;
        _agentRegistry = _agentRegistryService.Load();
        RenderAgentsView();
        if (result.EnrolmentSecret is not null)
        {
            ShowEnrolmentCommand(result.Agent, result.EnrolmentSecret);
        }
    }

    private void TryDeleteSelectedAgent()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            AppMessageBox.Show(this, "No agent is selected.", "Delete Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeleteAgent(_selectedAgentId);
    }

    private void DeleteAgent(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
        {
            AppMessageBox.Show(this, "The selected agent is no longer available.", "Delete Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = AppMessageBox.Show(
            this,
            $"Delete agent '{agent.EffectiveDisplayName}'?",
            "Delete Agent",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _agentRegistryService.Delete(_agentRegistry, agentId);
        _agentRegistry = _agentRegistryService.Load();
        _selectedAgentId = _agentRegistry.Agents.FirstOrDefault()?.AgentId;
        AgentsHeaderCountText.Text = $"({_agentRegistry.Agents.Count})";
        RenderAgentsView();
    }

    private void TryDisableSelectedAgent()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            AppMessageBox.Show(this, "No agent is selected.", "Disable Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DisableAgent(_selectedAgentId);
    }

    private void DisableAgent(string agentId)
    {
        SetAgentAdministrativeState(
            agentId,
            AgentDefinitionState.Disabled,
            "Disable Agent",
            "Disable");
    }

    private void TryRevokeSelectedAgent()
    {
        if (string.IsNullOrWhiteSpace(_selectedAgentId))
        {
            AppMessageBox.Show(this, "No agent is selected.", "Revoke Agent", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RevokeAgent(_selectedAgentId);
    }

    private void RevokeAgent(string agentId)
    {
        SetAgentAdministrativeState(
            agentId,
            AgentDefinitionState.Revoked,
            "Revoke Agent",
            "Revoke");
    }

    private void SetAgentAdministrativeState(string agentId, AgentDefinitionState targetState, string title, string actionVerb)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
        {
            AppMessageBox.Show(this, "The selected agent is no longer available.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (agent.State == targetState)
        {
            return;
        }

        var result = AppMessageBox.Show(
            this,
            $"{actionVerb} agent '{agent.EffectiveDisplayName}'?",
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        agent.State = targetState;
        agent.ActiveRuns = 0;
        agent.SessionId = null;
        agent.IsDraining = false;
        agent.LastStateReasonCode = targetState == AgentDefinitionState.Revoked ? "revoked" : "disabled";
        agent.LastStateReasonMessage = targetState == AgentDefinitionState.Revoked
            ? "Agent trust was revoked by an administrator."
            : "Agent was disabled by an administrator.";
        agent.LastSeenSummary = targetState == AgentDefinitionState.Revoked ? "Revoked" : "Disabled";
        if (targetState == AgentDefinitionState.Revoked)
        {
            agent.RevokedAtUtc = DateTime.UtcNow;
        }

        _agentRegistryService.Save(_agentRegistry);
        _agentRegistry = _agentRegistryService.Load();
        RenderAgentsView();
    }

    private void RegenerateAgentEnrolment(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
        {
            AppMessageBox.Show(this, "The selected agent is no longer available.", "Agent Enrolment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var secret = _agentRegistryService.GenerateEnrolment(agent, TimeSpan.FromHours(2));
        agent.State = AgentDefinitionState.Pending;
        _agentRegistryService.Save(_agentRegistry);
        _agentRegistry = _agentRegistryService.Load();
        RenderAgentsView();
        ShowEnrolmentCommand(agent, secret);
    }

    private void ShowEnrolmentCommand(AgentDefinition agent, AgentEnrolmentSecret secret)
    {
        var command = AgentRegistryService.BuildEnrolmentCommand(agent, secret);
        var window = new AgentEnrolmentCommandWindow(command, secret.ExpiresAtUtc) { Owner = this };
        window.ShowDialog();
    }

    private AgentDefinition? FindAgent(string agentId)
    {
        return _agentRegistry.Agents.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    private void RibbonDeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAgentViewSelected)
        {
            TryDeleteSelectedAgent();
            return;
        }

        TryDeleteFromCurrentContext();
    }

    private void RibbonEditTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAgentViewSelected)
        {
            TryEditSelectedAgent();
            return;
        }

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

        _ = await RunTaskChainFromTaskAsync(firstEnabledTaskIndex);
    }

    private void RibbonPreviousGroupButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateGroup(step: -1);
    }

    private void RibbonNextGroupButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateGroup(step: 1);
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

    private ContextMenu CreateRibbonContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)Resources["RibbonContextMenuStyle"],
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        menu.Resources[typeof(MenuItem)] = (Style)Resources["AppMenuItemStyle"];
        menu.Resources[typeof(Separator)] = (Style)Resources["AppMenuSeparatorStyle"];
        return menu;
    }

    private void OpenRibbonContextMenu(Button button, ContextMenu menu)
    {
        try
        {
            button.ContextMenu = menu;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[ContextMenu] Failed to open ribbon context menu");
            AppMessageBox.Show(this, $"Could not open the menu.\n{ex.Message}", "Menu Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            AppendValidationEvent(action, path, true, $"Loaded {config.Tasks.Count} task(s).", outputTextBox);
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
            AppendValidationEvent(action, path, true, $"Validated {validatedConfig.Tasks.Count} task(s).", outputTextBox);
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
        var groupVariables = config.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Name))
            .ToDictionary(
                group => NormalizeGroupName(group.Name),
                group => (IReadOnlyDictionary<string, string>)(group.Variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

        foreach (var task in config.Tasks)
        {
            var taskName = string.IsNullOrWhiteSpace(task.Name) ? "(unnamed)" : task.Name.Trim();
            var groupName = NormalizeGroupName(task.Group);
            var variables = groupVariables.TryGetValue(groupName, out var groupValues)
                ? groupValues
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedVariables = TaskVariableService.GetUnresolvedVariables(task, variables);
            if (unresolvedVariables.Count > 0)
            {
                warnings.Add(
                    $"Task '{taskName}' (group '{groupName}') contains unresolved variables: {string.Join(", ", unresolvedVariables.Select(name => "${" + name + "}"))}. Define them in group variables.");
            }

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

    private static string NormalizeMarkerToken(string? value)
    {
        return AgentDispatchLifecycleMarkers.NormalizeToken(value);
    }

    private static string BuildRemoteCorrelationId(string? taskName, string? agentId, string? attemptId)
    {
        return AgentDispatchLifecycleMarkers.BuildCorrelationId(taskName, agentId, attemptId);
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

        var exportBackupItem = new MenuItem
        {
            Header = "Export Backup (Tasks)",
            Style = (Style)Resources["AppMenuItemStyle"],
            IsEnabled = _currentConfiguration.Tasks.Count > 0
        };
        exportBackupItem.Click += (_, _) => ExportConfigurationToClipboardAndOutput(
            "Share Backup",
            new TaskConfiguration { Tasks = _currentConfiguration.Tasks.Select(TaskIdentityClone).ToList() },
            $"backup ({_currentConfiguration.Tasks.Count} task(s))");
        menu.Items.Add(exportBackupItem);

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
            Tasks = [task]
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
            Tasks = tasks
        };

        ExportConfigurationToClipboardAndOutput("Share Group", payload, $"group '{_selectedGroupName}' ({tasks.Count} task(s))");
    }

    private void ExportCombinedBackupToFile()
    {
        if (_currentConfiguration.Tasks.Count == 0)
        {
            AppMessageBox.Show(this, "There are no tasks to export.", "Export Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Export Backup (Tasks)",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"tsky-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        var tasks = _currentConfiguration.Tasks.Select(TaskIdentityClone).ToList();

        var tabContext = CreateOutputTab("Export Backup");
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);

        try
        {
            var payload = new TaskConfiguration
            {
                Tasks = tasks
            };

            var json = _importExportService.SerializeConfiguration(payload);
            File.WriteAllText(saveDialog.FileName, json);

            AppendLog(outputTextBox, $"[Share] Exported combined backup to '{saveDialog.FileName}'.");
            AppendLog(outputTextBox, $"[Share] Included {tasks.Count} task(s).");
            SetTabStopped(tabContext, true);
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, "[Share] ExportCombinedBackupToFile failed");
            AppendLog(outputTextBox, $"[Share] Export failed: {ex.Message}");
            SetTabStopped(tabContext, false);
            AppMessageBox.Show(this, $"Failed to export backup.{Environment.NewLine}{ex.Message}", "Export Backup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            Title = "Import Configuration",
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
            var importedConfig = LoadConfigurationWithValidation(importPath, "Read import file JSON", outputTextBox);
            AppendLog(outputTextBox, $"[Import] Loaded {importedConfig.Tasks.Count} task(s) from file.");
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
                var importedConfig = LoadConfigurationWithValidation(tempInputPath, "Read clipboard JSON", outputTextBox);
                AppendLog(outputTextBox, $"[Import] Loaded {importedConfig.Tasks.Count} task(s) from clipboard.");
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
            var importedTasksOnly = new TaskConfiguration
            {
                Tasks = importedConfig.Tasks.Select(TaskIdentityClone).ToList()
            };

            var mergeResult = _importExportService.MergeAndPersistImportedConfiguration(
                _userProfileConfigPath,
                importedTasksOnly,
                importSourceLabel);

            var validatedWrittenProfile = mergeResult.ValidatedWrittenUserProfileConfiguration;

            AppendLog(outputTextBox, $"[Import] Merged configuration validated and written to user profile: {_userProfileConfigPath}");
            AppendLog(outputTextBox, $"[Import] Final merged tasks result: {validatedWrittenProfile.Tasks.Count} task(s).");

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

    private static LauncherTask TaskIdentityClone(LauncherTask task)
    {
        return new LauncherTask
        {
            Name = task.Name,
            Description = task.Description,
            Group = task.Group,
            CommandLine = task.CommandLine,
            Runner = task.Runner,
            Env = new Dictionary<string, string>(task.Env, StringComparer.OrdinalIgnoreCase),
            WorkingDirectory = task.WorkingDirectory,
            WaitForExit = task.WaitForExit,
            RequiresElevation = task.RequiresElevation,
            IgnoreExitCode = task.IgnoreExitCode,
            TimeoutSeconds = task.TimeoutSeconds,
            KillProcessTreeOnCancel = task.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = task.MaxCapturedOutputChars,
            IsEnabled = task.IsEnabled,
            ImportedFromFile = task.ImportedFromFile,
            ImportedAtUtc = task.ImportedAtUtc,
            Inputs = new Dictionary<string, string>(task.Inputs, StringComparer.OrdinalIgnoreCase),
            InputBindings = new Dictionary<string, string>(task.InputBindings, StringComparer.OrdinalIgnoreCase),
            Outputs = new Dictionary<string, string>(task.Outputs, StringComparer.OrdinalIgnoreCase)
        };
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
        if (TryHandleOutputPaneNavigationKey(e))
        {
            return;
        }

        if (_isAgentViewSelected)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                TryDeleteSelectedAgent();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.E && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                TryEditSelectedAgent();
                e.Handled = true;
                return;
            }

            if (e.Key is Key.C or Key.V or Key.R)
            {
                return;
            }
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex))
            {
                CopyTask(selectedTaskIndex);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Keyboard.FocusedElement is TextBoxBase or TextBox or RichTextBox or ComboBox)
            {
                return;
            }

            if (PasteTaskIntoSelectedGroup())
            {
                e.Handled = true;
            }

            return;
        }

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

    private bool TryHandleOutputPaneNavigationKey(KeyEventArgs e)
    {
        var focusedRun = FindFocusedOutputTabContext();
        if (focusedRun is null)
        {
            return false;
        }

        return TryHandleOutputNavigationKey(focusedRun.HostOutputPanel, e);
    }

    private static bool TryHandleOutputNavigationKey(OutputGroupPanelContext panel, KeyEventArgs e)
    {
        var scrollViewer = panel.RunListScrollViewer;

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Up:
                    scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - 24));
                    e.Handled = true;
                    return true;
                case Key.Down:
                    scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + 24));
                    e.Handled = true;
                    return true;
                case Key.PageUp:
                    scrollViewer.PageUp();
                    e.Handled = true;
                    return true;
                case Key.PageDown:
                    scrollViewer.PageDown();
                    e.Handled = true;
                    return true;
            }
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.End)
            {
                scrollViewer.ScrollToBottom();
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.Home)
            {
                scrollViewer.ScrollToTop();
                e.Handled = true;
                return true;
            }
        }

        return false;
    }

    private TaskTabContext? FindFocusedOutputTabContext()
    {
        foreach (var panel in _groupOutputPanels.Values)
        {
            foreach (var run in panel.Runs)
            {
                if (run.OutputTextBox.IsKeyboardFocusWithin)
                {
                    return run;
                }
            }
        }

        return null;
    }

    private bool TryGetOutputTabContext(RichTextBox outputTextBox, out TaskTabContext tabContext)
    {
        foreach (var panel in _groupOutputPanels.Values)
        {
            foreach (var run in panel.Runs)
            {
                if (ReferenceEquals(run.OutputTextBox, outputTextBox))
                {
                    tabContext = run;
                    return true;
                }
            }
        }

        tabContext = null!;
        return false;
    }

    private bool IsSidebarPaneFocused()
    {
        return SidebarPaneBorder?.IsKeyboardFocusWithin == true
            || GroupsTreeScrollViewer?.IsKeyboardFocusWithin == true
            || GroupListPanel?.IsKeyboardFocusWithin == true;
    }

    private void TryDeleteFromCurrentContext()
    {
        if (_isAgentViewSelected)
        {
            TryDeleteSelectedAgent();
            return;
        }

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

    private void CopyTask(int taskIndex)
    {
        if (!TryGetTask(taskIndex, out var taskToCopy))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _copiedTaskJson = TaskConfiguration.SerializeTask(taskToCopy);
    }

    private bool CanPasteTaskIntoCurrentGroup()
    {
        return !string.IsNullOrWhiteSpace(_copiedTaskJson)
            && !string.IsNullOrWhiteSpace(_selectedGroupName);
    }

    private void CopyGroup(string sourceGroupName)
    {
        var normalizedSourceGroup = NormalizeGroupName(sourceGroupName);
        var sourceTasks = _currentConfiguration.Tasks
            .Where(task => string.Equals(NormalizeGroupName(task.Group), normalizedSourceGroup, StringComparison.OrdinalIgnoreCase))
            .Select(CloneTask)
            .ToList();
        var variables = CloneStringDictionary(GetGroupVariablesForGroup(normalizedSourceGroup));

        _copiedGroupPayload = new CopiedGroupPayload
        {
            SourceGroupName = normalizedSourceGroup,
            Variables = variables,
            Tasks = sourceTasks
        };

        DebugTraceLog.Write($"[GroupCopy] Copied group '{normalizedSourceGroup}' with {sourceTasks.Count} task(s) and {variables.Count} variable(s).");
    }

    private bool PasteCopiedGroup()
    {
        if (_copiedGroupPayload is null)
        {
            DebugTraceLog.Write("[GroupPaste] Paste blocked because no copied group payload exists.");
            AppMessageBox.Show(this, "Copy a group first, then paste it.", "Paste Group", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var existingGroupNames = GetGroupNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestedGroupName = BuildSuggestedGroupCopyName(_copiedGroupPayload.SourceGroupName, existingGroupNames);
        DebugTraceLog.Write($"[GroupPaste] Prompting for target name. Source='{_copiedGroupPayload.SourceGroupName}', suggested='{suggestedGroupName}', existingGroups={existingGroupNames.Count}.");
        var prompt = new GroupNamePromptWindow(existingGroupNames, suggestedGroupName)
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true)
        {
            DebugTraceLog.Write($"[GroupPaste] User cancelled paste for source '{_copiedGroupPayload.SourceGroupName}'.");
            return false;
        }

        var newGroupName = NormalizeGroupName(prompt.GroupName);

        if (existingGroupNames.Contains(newGroupName))
        {
            DebugTraceLog.Write($"[GroupPaste] Validation failed after prompt. Target group '{newGroupName}' already exists.");
            AppMessageBox.Show(this, $"A group named '{newGroupName}' already exists.", "Paste Group", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var groupDefinition = GetOrCreateGroupDefinition(newGroupName);
        groupDefinition.Variables = CloneStringDictionary(_copiedGroupPayload.Variables);
        var addedTaskCount = 0;

        foreach (var sourceTask in _copiedGroupPayload.Tasks)
        {
            var clonedTask = CloneTask(sourceTask);
            clonedTask.Group = newGroupName;
            _currentConfiguration.Tasks.Add(clonedTask);
            addedTaskCount++;
        }

        _selectedGroupName = newGroupName;
        DebugTraceLog.Write($"[GroupPaste] Creating group '{newGroupName}' from '{_copiedGroupPayload.SourceGroupName}' with {addedTaskCount} task(s) and {groupDefinition.Variables.Count} variable(s).");
        SaveTasksAndRefresh("Paste Group");
        DebugTraceLog.Write($"[GroupPaste] Persisted pasted group '{newGroupName}'.");
        return true;
    }

    private static string BuildSuggestedGroupCopyName(string sourceGroupName, ISet<string> existingGroupNames)
    {
        var baseName = $"{NormalizeGroupName(sourceGroupName)} Copy";
        var candidate = baseName;
        var suffix = 2;

        while (existingGroupNames.Contains(candidate))
        {
            candidate = $"{baseName} ({suffix})";
            suffix++;
        }

        return candidate;
    }

    private static Dictionary<string, string> CloneStringDictionary(IReadOnlyDictionary<string, string>? source)
    {
        var clone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return clone;
        }

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            clone[pair.Key] = pair.Value ?? string.Empty;
        }

        return clone;
    }

    private static LauncherTask CloneTask(LauncherTask source)
    {
        return new LauncherTask
        {
            Name = source.Name,
            Description = source.Description,
            Group = NormalizeGroupName(source.Group),
            CommandLine = source.CommandLine,
            Runner = source.Runner,
            Env = CloneStringDictionary(source.Env),
            WorkingDirectory = source.WorkingDirectory,
            WaitForExit = source.WaitForExit,
            RequiresElevation = source.RequiresElevation,
            IgnoreExitCode = source.IgnoreExitCode,
            TimeoutSeconds = source.TimeoutSeconds,
            KillProcessTreeOnCancel = source.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = source.MaxCapturedOutputChars,
            IsEnabled = source.IsEnabled,
            ImportedFromFile = source.ImportedFromFile,
            ImportedAtUtc = source.ImportedAtUtc,
            Inputs = CloneStringDictionary(source.Inputs),
            InputBindings = CloneStringDictionary(source.InputBindings),
            Outputs = CloneStringDictionary(source.Outputs)
        };
    }

    private bool PasteTaskIntoSelectedGroup()
    {
        if (string.IsNullOrWhiteSpace(_selectedGroupName))
        {
            AppMessageBox.Show(this, "Select a group before pasting a task.", "Paste Task", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (string.IsNullOrWhiteSpace(_copiedTaskJson))
        {
            AppMessageBox.Show(this, "Copy a task first, then paste it into the target group.", "Paste Task", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!TaskConfiguration.TryDeserializeTask(_copiedTaskJson, out var pastedTask, out var parseError))
        {
            AppMessageBox.Show(this, $"Could not paste task because the copied task data is invalid.\n\n{parseError}", "Paste Task", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var targetGroup = NormalizeGroupName(_selectedGroupName);
        pastedTask.Group = targetGroup;

        var pastedTaskIdentityKey = GetTaskIdentityKey(pastedTask);
        var existingIndex = _currentConfiguration.Tasks.FindIndex(task =>
            string.Equals(GetTaskIdentityKey(task), pastedTaskIdentityKey, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            var overwriteResult = AppMessageBox.Show(this,
                $"A task named '{pastedTask.Name}' already exists in group '{targetGroup}'. Overwrite it?",
                "Duplicate Task Name",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (overwriteResult == MessageBoxResult.OK)
            {
                _currentConfiguration.Tasks[existingIndex] = pastedTask;
                _selectedTaskIndex = existingIndex;
            }
            else
            {
                pastedTask.Name = BuildUniqueTaskCopyName(targetGroup, pastedTask.Name);
                _currentConfiguration.Tasks.Add(pastedTask);
                _selectedTaskIndex = _currentConfiguration.Tasks.Count - 1;
            }
        }
        else
        {
            _currentConfiguration.Tasks.Add(pastedTask);
            _selectedTaskIndex = _currentConfiguration.Tasks.Count - 1;
        }

        _selectedGroupName = targetGroup;
        SaveTasksAndRefresh("Paste Task");
        return true;
    }

    private string BuildUniqueTaskCopyName(string groupName, string originalName)
    {
        var baseName = string.IsNullOrWhiteSpace(originalName)
            ? "Task"
            : originalName.Trim();

        var candidate = $"{baseName} (2)";
        var suffix = 2;

        while (_currentConfiguration.Tasks.Any(task =>
                   string.Equals(NormalizeGroupName(task.Group), groupName, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(task.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            candidate = $"{baseName} ({suffix})";
        }

        return candidate;
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
                return;
            }

            if (ReferenceEquals(BottomPaneTabControl.SelectedItem, AgentsDispatchPaneTabItem))
            {
                RefreshAgentsDispatchPane();
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

    private void RefreshAgentsDispatchPane()
    {
        if (AgentsDispatchRowsPanel is null || AgentsDispatchValidationText is null || AgentsDispatchTitleText is null)
        {
            return;
        }

        AgentsDispatchRowsPanel.Children.Clear();

        var selectedTaskLabel = "No task selected";
        if (TryGetSelectedVisibleTaskIndex(out var selectedTaskIndex)
            && TryGetTask(selectedTaskIndex, out var selectedTask))
        {
            selectedTaskLabel = $"Selected task: {selectedTask.Name} ({NormalizeGroupName(selectedTask.Group)})";
        }

        AgentsDispatchTitleText.Text = $"Run Selected Task On Agent  |  {selectedTaskLabel}";

        var orderedAgents = GetOrderedAgentsForDispatch();

        if (orderedAgents.Count == 0)
        {
            _selectedDispatchAgentId = null;
            SetAgentsDispatchValidationMessage(
                "No agents are available yet. Enroll an agent and ensure it is Online to run the selected task from this tab.",
                isError: false);
            return;
        }

        AgentsDispatchRowsPanel.Children.Add(CreateAgentDispatchHeaderRow());

        if (string.IsNullOrWhiteSpace(_selectedDispatchAgentId)
            || orderedAgents.All(agent => !string.Equals(agent.AgentId, _selectedDispatchAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedDispatchAgentId = orderedAgents
                .FirstOrDefault(IsAgentEligibleForDispatch)?.AgentId
                ?? orderedAgents[0].AgentId;
        }

        foreach (var agent in orderedAgents)
        {
            AgentsDispatchRowsPanel.Children.Add(CreateAgentDispatchRow(agent));
        }

        if (!TryValidateSelectedTaskForAgentDispatch(out _, out var taskValidationMessage, out _))
        {
            SetAgentsDispatchValidationMessage(taskValidationMessage, isError: true);
            return;
        }

        SetAgentsDispatchValidationMessage("Select an Online agent and choose Play to dispatch the selected task.", isError: false);
    }

    private Border CreateAgentDispatchHeaderRow()
    {
        var headerGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6)
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var agentHeader = new TextBlock
        {
            Text = "Agent",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(agentHeader, 0);

        var stateHeader = new TextBlock
        {
            Text = "State",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(stateHeader, 1);

        headerGrid.Children.Add(agentHeader);
        headerGrid.Children.Add(stateHeader);

        return new Border
        {
            Padding = new Thickness(8, 2, 8, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x42, 0x47)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid
        };
    }

    private Border CreateAgentDispatchRow(AgentDefinition agent)
    {
        var transientState = GetAgentTransientExecutionState(agent.AgentId);
        var canDispatchToAgent = IsAgentEligibleForDispatch(agent);
        var isRowLocked = transientState is AgentTransientExecutionState.Dispatching or AgentTransientExecutionState.Running;
        var activeAttemptCount = _runningAttemptIdsByAgent.TryGetValue(agent.AgentId, out var queue)
            ? queue.Count
            : 0;
        var isActivelyRunning = activeAttemptCount > 0 || transientState is AgentTransientExecutionState.Dispatching or AgentTransientExecutionState.Running;
        var isSelected = string.Equals(_selectedDispatchAgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase);
        var agentDisplayName = GetAgentDisplayName(agent);
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = agentDisplayName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            // Use an explicit bright foreground here to avoid theme/resource contrast issues.
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetName(title, $"Agent {agentDisplayName}");
        Grid.SetColumn(title, 0);

        var state = new TextBlock
        {
            Text = GetAgentDispatchStateSummary(agent, transientState, activeAttemptCount),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetName(state, $"Agent state {GetAgentDispatchStateSummary(agent, transientState, activeAttemptCount)}");
        Grid.SetColumn(state, 1);

        var canPlay = canDispatchToAgent && !isRowLocked;
        var playButton = new Button
        {
            Content = "\uE768",
            Style = (Style)Resources["HistoryActionButtonStyle"],
            Margin = new Thickness(0),
            MinWidth = 36,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            IsEnabled = canPlay,
            Background = canPlay ? AgentsDispatchPlayEnabledBackgroundBrush : AgentsDispatchPlayDisabledBackgroundBrush,
            BorderBrush = canPlay ? AgentsDispatchPlayEnabledBorderBrush : AgentsDispatchPlayDisabledBorderBrush,
            Foreground = Brushes.White,
            Opacity = canPlay ? 1.0 : 0.65,
            Tag = new AgentDispatchCommand { AgentId = agent.AgentId, RunChain = false }
        };
        playButton.ToolTip = $"Run selected task on {agentDisplayName}";
        AutomationProperties.SetName(playButton, $"Play task on agent {agentDisplayName}");
        AutomationProperties.SetHelpText(playButton, canPlay
            ? "Dispatches the currently selected task to this agent."
            : "Unavailable because the agent is not currently dispatchable.");
        playButton.Click += AgentDispatchPlayButton_Click;
        Grid.SetColumn(playButton, 2);

        var canRunChain = canDispatchToAgent && !isRowLocked;
        var chainButton = new Button
        {
            Content = "Chain",
            Style = (Style)Resources["HistoryActionButtonStyle"],
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 52,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            IsEnabled = canRunChain,
            Background = canRunChain ? AgentsDispatchPlayEnabledBackgroundBrush : AgentsDispatchPlayDisabledBackgroundBrush,
            BorderBrush = canRunChain ? AgentsDispatchPlayEnabledBorderBrush : AgentsDispatchPlayDisabledBorderBrush,
            Foreground = Brushes.White,
            Opacity = canRunChain ? 1.0 : 0.65,
            Tag = new AgentDispatchCommand { AgentId = agent.AgentId, RunChain = true }
        };
        chainButton.ToolTip = $"Run task chain from selected task on {agentDisplayName}";
        AutomationProperties.SetName(chainButton, $"Run chain on agent {agentDisplayName}");
        AutomationProperties.SetHelpText(chainButton, canRunChain
            ? "Runs the selected group chain step-by-step on this agent with remote output propagation."
            : "Unavailable because the agent is not currently dispatchable.");
        chainButton.Click += AgentDispatchPlayButton_Click;
        Grid.SetColumn(chainButton, 2);

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        actionPanel.Children.Add(playButton);
        actionPanel.Children.Add(chainButton);
        Grid.SetColumn(actionPanel, 2);

        row.Children.Add(title);
        row.Children.Add(state);
        row.Children.Add(actionPanel);

        var container = new Border
        {
            Padding = new Thickness(8, 8, 8, 8),
            CornerRadius = new CornerRadius(0),
            BorderBrush = isSelected
                ? AgentsDispatchRowSelectedBorderBrush
                : isActivelyRunning
                    ? AgentsDispatchRunningBorderBrush
                    : AgentsDispatchRowBorderBrush,
            BorderThickness = isSelected ? new Thickness(2) : new Thickness(1),
            Background = isSelected
                ? AgentsDispatchRowSelectedBackgroundBrush
                : AgentsDispatchRowBackgroundBrush,
            Tag = agent.AgentId,
            Focusable = true,
            Child = row
        };

        container.MouseLeftButtonUp += AgentDispatchRow_MouseLeftButtonUp;
        container.KeyDown += AgentDispatchRow_KeyDown;
        container.GotKeyboardFocus += AgentDispatchRow_GotKeyboardFocus;
        container.LostKeyboardFocus += AgentDispatchRow_LostKeyboardFocus;
        AutomationProperties.SetName(container, $"Agent list item {agentDisplayName}");
        AutomationProperties.SetHelpText(container, "Selects this agent row for expanded agent actions and details.");

        ApplyDispatchRowVisual(container);

        return container;
    }

    private void AgentDispatchRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string agentId } || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        _selectedDispatchAgentId = agentId;

        if (!_isAgentViewSelected && ReferenceEquals(BottomPaneTabControl?.SelectedItem, AgentsDispatchPaneTabItem))
        {
            RefreshAgentsDispatchPane();
            FocusSelectedDispatchRow();
        }
    }

    private async void AgentDispatchRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { Tag: string agentId } || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _selectedDispatchAgentId = agentId;
            await DispatchSelectedTaskToAgentAsync(agentId);
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            MoveDispatchSelection(-1);
            return;
        }

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            MoveDispatchSelection(1);
            return;
        }

        if (e.Key == Key.Home)
        {
            e.Handled = true;
            SelectDispatchRowByIndex(0);
            return;
        }

        if (e.Key == Key.End)
        {
            e.Handled = true;
            var orderedAgents = GetOrderedAgentsForDispatch();
            if (orderedAgents.Count > 0)
            {
                SelectDispatchRowByIndex(orderedAgents.Count - 1);
            }
            return;
        }

        if (e.Key == Key.PageUp)
        {
            e.Handled = true;
            MoveDispatchSelection(-AgentDispatchPageJumpSize);
            return;
        }

        if (e.Key == Key.PageDown)
        {
            e.Handled = true;
            MoveDispatchSelection(AgentDispatchPageJumpSize);
        }
    }

    private void AgentDispatchRow_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyDispatchRowVisual(border);
        }
    }

    private void AgentDispatchRow_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyDispatchRowVisual(border);
        }
    }

    private void MoveDispatchSelection(int offset)
    {
        var orderedAgents = GetOrderedAgentsForDispatch();
        if (orderedAgents.Count == 0)
        {
            return;
        }

        var currentIndex = orderedAgents.FindIndex(agent =>
            string.Equals(agent.AgentId, _selectedDispatchAgentId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = Math.Clamp(currentIndex + offset, 0, orderedAgents.Count - 1);
        if (nextIndex == currentIndex)
        {
            return;
        }

        SelectDispatchRowByIndex(nextIndex);
    }

    private void SelectDispatchRowByIndex(int index)
    {
        var orderedAgents = GetOrderedAgentsForDispatch();
        if (orderedAgents.Count == 0)
        {
            return;
        }

        var boundedIndex = Math.Clamp(index, 0, orderedAgents.Count - 1);
        _selectedDispatchAgentId = orderedAgents[boundedIndex].AgentId;
        RefreshAgentsDispatchPane();
        FocusSelectedDispatchRow();
    }

    private void FocusSelectedDispatchRow()
    {
        if (AgentsDispatchRowsPanel is null || string.IsNullOrWhiteSpace(_selectedDispatchAgentId))
        {
            return;
        }

        var selectedRow = AgentsDispatchRowsPanel.Children
            .OfType<Border>()
            .FirstOrDefault(border => border.Tag is string rowAgentId
                && string.Equals(rowAgentId, _selectedDispatchAgentId, StringComparison.OrdinalIgnoreCase));

        selectedRow?.Focus();
    }

    private void ApplyDispatchRowVisual(Border row)
    {
        if (row.Tag is not string rowAgentId || string.IsNullOrWhiteSpace(rowAgentId))
        {
            return;
        }

        var isSelected = string.Equals(_selectedDispatchAgentId, rowAgentId, StringComparison.OrdinalIgnoreCase);
        var isFocused = row.IsKeyboardFocused || row.IsKeyboardFocusWithin;

        if (isSelected)
        {
            row.BorderBrush = isFocused ? AgentsDispatchRowFocusedBorderBrush : AgentsDispatchRowSelectedBorderBrush;
            row.BorderThickness = new Thickness(2);
            row.Background = isFocused ? AgentsDispatchRowFocusedBackgroundBrush : AgentsDispatchRowSelectedBackgroundBrush;
            return;
        }

        row.BorderBrush = isFocused ? AgentsDispatchRowFocusedBorderBrush : AgentsDispatchRowBorderBrush;
        row.BorderThickness = new Thickness(1);
        row.Background = isFocused ? AgentsDispatchRowFocusedBackgroundBrush : AgentsDispatchRowBackgroundBrush;
    }

    private List<AgentDefinition> GetOrderedAgentsForDispatch()
    {
        return _agentRegistry.Agents
            .OrderBy(agent => GetAgentDisplayName(agent), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void AgentDispatchPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AgentDispatchCommand command })
        {
            return;
        }

        _selectedDispatchAgentId = command.AgentId;
        if (command.RunChain)
        {
            await DispatchSelectedTaskChainToAgentAsync(command.AgentId);
            return;
        }

        await DispatchSelectedTaskToAgentAsync(command.AgentId);
    }

    private async Task DispatchSelectedTaskToAgentAsync(string agentId)
    {
        if (!TryValidateSelectedTaskForAgentDispatch(out var task, out var taskValidationMessage, out var taskValidationReason))
        {
            LogAgentDispatchValidationFailure(
                taskName: null,
                agentId: agentId,
                reason: taskValidationReason,
                message: taskValidationMessage);
            SetAgentsDispatchValidationMessage(taskValidationMessage, isError: true);
            return;
        }

        await RunSelectedTaskOnAgent(task, agentId);
    }

    private async Task DispatchSelectedTaskChainToAgentAsync(string agentId)
    {
        if (!TryValidateSelectedTaskForAgentDispatch(out var task, out var taskValidationMessage, out var taskValidationReason))
        {
            LogAgentDispatchValidationFailure(
                taskName: null,
                agentId: agentId,
                reason: taskValidationReason,
                message: taskValidationMessage);
            SetAgentsDispatchValidationMessage(taskValidationMessage, isError: true);
            return;
        }

        await RunSelectedTaskChainOnAgent(task, agentId);
    }

    private async Task<bool> RunSelectedTaskOnAgent(LauncherTask selectedTask, string agentId)
    {
        var taskIdentityKey = GetTaskIdentityKey(selectedTask);
        var taskIndex = _currentConfiguration.Tasks.FindIndex(candidate =>
            string.Equals(GetTaskIdentityKey(candidate), taskIdentityKey, StringComparison.OrdinalIgnoreCase));

        if (taskIndex < 0 || !TryGetTask(taskIndex, out var task))
        {
            LogAgentDispatchValidationFailure(
                taskName: selectedTask.Name,
                agentId: agentId,
                reason: "task_unavailable",
                message: "The selected task is no longer available.");
            SetAgentsDispatchValidationMessage("The selected task is no longer available.", isError: true);
            return false;
        }

        if (!IsTaskEnabled(task))
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agentId,
                reason: "task_disabled",
                message: $"Task '{task.Name}' is disabled.");
            SetAgentsDispatchValidationMessage($"Task '{task.Name}' is disabled.", isError: true);
            return false;
        }

        var agent = FindAgent(agentId);
        if (agent is null)
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agentId,
                reason: "agent_unavailable",
                message: "The selected agent is no longer available.");
            SetAgentsDispatchValidationMessage("The selected agent is no longer available.", isError: true);
            RefreshAgentsDispatchPane();
            return false;
        }

        if (!IsAgentEligibleForDispatch(agent))
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agent.AgentId,
                reason: "agent_not_eligible",
                message: $"Agent '{GetAgentDisplayName(agent)}' is not eligible for dispatch ({agent.State.ToString().ToLowerInvariant()}).");
            SetAgentsDispatchValidationMessage($"Agent '{GetAgentDisplayName(agent)}' is not eligible for dispatch ({agent.State.ToString().ToLowerInvariant()}).", isError: true);
            RefreshAgentsDispatchPane();
            return false;
        }

        _selectedTaskIndex = taskIndex;
        ApplyTaskCardSelectionVisuals();

        var dispatchSelection = new ExecutionTargetSelection
        {
            Mode = ExecutionTargetMode.SpecificAgent,
            AgentId = agent.AgentId
        };

        var taskStateKey = GetTaskStateKey(task);
        if (TryGetActiveAttemptBindingForTask(taskStateKey, out var activeBinding))
        {
            var existingAgent = FindAgent(activeBinding.AgentId) is { } boundAgent
                ? GetAgentDisplayName(boundAgent)
                : activeBinding.AgentId;
            var retargetMessage =
                $"Task '{task.Name}' is already bound to agent '{existingAgent}' for active attempt '{activeBinding.AttemptId}'. " +
                "Wait for completion/cancellation before dispatching to another agent.";
            LogAgentDispatchTelemetry(AgentDispatchBindingEvent, retargetMessage);
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agent.AgentId,
                reason: "active_binding_immutable",
                message: retargetMessage);
            SetAgentsDispatchValidationMessage(retargetMessage, isError: true);
            return false;
        }

        LogAgentDispatchTelemetry(
            AgentDispatchActionEvent,
            $"action=run_selected_task task={NormalizeMarkerToken(task.Name)} agent={NormalizeMarkerToken(agent.AgentId)}");

        SetAgentTransientExecutionState(agent.AgentId, AgentTransientExecutionState.Dispatching, "play_clicked");

        var result = await ExecuteTaskAsync(task, null, isReplay: false, forcedExecutionTargetSelection: dispatchSelection);
        if (result.Succeeded)
        {
            SetAgentsDispatchValidationMessage($"Dispatch requested: '{task.Name}' -> {GetAgentDisplayName(agent)}.", isError: false);
            return true;
        }

        SetAgentTransientExecutionState(agent.AgentId, AgentTransientExecutionState.Failed, "dispatch_call_failed");
        SetAgentsDispatchValidationMessage($"Dispatch failed for '{task.Name}' on {GetAgentDisplayName(agent)}. Check Task Output for details.", isError: true);
        return false;
    }

    private async Task<bool> RunSelectedTaskChainOnAgent(LauncherTask selectedTask, string agentId)
    {
        var taskIdentityKey = GetTaskIdentityKey(selectedTask);
        var taskIndex = _currentConfiguration.Tasks.FindIndex(candidate =>
            string.Equals(GetTaskIdentityKey(candidate), taskIdentityKey, StringComparison.OrdinalIgnoreCase));

        if (taskIndex < 0 || !TryGetTask(taskIndex, out var task))
        {
            LogAgentDispatchValidationFailure(
                taskName: selectedTask.Name,
                agentId: agentId,
                reason: "task_unavailable",
                message: "The selected task is no longer available.");
            SetAgentsDispatchValidationMessage("The selected task is no longer available.", isError: true);
            return false;
        }

        if (!IsTaskEnabled(task))
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agentId,
                reason: "task_disabled",
                message: $"Task '{task.Name}' is disabled.");
            SetAgentsDispatchValidationMessage($"Task '{task.Name}' is disabled.", isError: true);
            return false;
        }

        var agent = FindAgent(agentId);
        if (agent is null)
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agentId,
                reason: "agent_unavailable",
                message: "The selected agent is no longer available.");
            SetAgentsDispatchValidationMessage("The selected agent is no longer available.", isError: true);
            RefreshAgentsDispatchPane();
            return false;
        }

        if (!IsAgentEligibleForDispatch(agent))
        {
            LogAgentDispatchValidationFailure(
                taskName: task.Name,
                agentId: agent.AgentId,
                reason: "agent_not_eligible",
                message: $"Agent '{GetAgentDisplayName(agent)}' is not eligible for dispatch ({agent.State.ToString().ToLowerInvariant()}).");
            SetAgentsDispatchValidationMessage($"Agent '{GetAgentDisplayName(agent)}' is not eligible for dispatch ({agent.State.ToString().ToLowerInvariant()}).", isError: true);
            RefreshAgentsDispatchPane();
            return false;
        }

        _selectedTaskIndex = taskIndex;
        ApplyTaskCardSelectionVisuals();

        var dispatchSelection = new ExecutionTargetSelection
        {
            Mode = ExecutionTargetMode.SpecificAgent,
            AgentId = agent.AgentId
        };

        LogAgentDispatchTelemetry(
            AgentDispatchActionEvent,
            $"action=run_chain task={NormalizeMarkerToken(task.Name)} agent={NormalizeMarkerToken(agent.AgentId)}");

        SetAgentTransientExecutionState(agent.AgentId, AgentTransientExecutionState.Dispatching, "chain_play_clicked");

        var succeeded = await RunTaskChainFromTaskAsync(taskIndex, dispatchSelection);
        if (succeeded)
        {
            SetAgentsDispatchValidationMessage($"Chain started from '{task.Name}' on {GetAgentDisplayName(agent)}.", isError: false);
            return true;
        }

        SetAgentTransientExecutionState(agent.AgentId, AgentTransientExecutionState.Failed, "chain_dispatch_failed");
        SetAgentsDispatchValidationMessage($"Chain failed from '{task.Name}' on {GetAgentDisplayName(agent)}. Check Task Output for details.", isError: true);
        return false;
    }

    private static string GetAgentDisplayName(AgentDefinition agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.DisplayName))
        {
            return agent.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(agent.Hostname))
        {
            return agent.Hostname.Trim();
        }

        if (!string.IsNullOrWhiteSpace(agent.AgentId))
        {
            return agent.AgentId.Trim();
        }

        return "Unnamed Agent";
    }

    private bool TryValidateSelectedTaskForAgentDispatch(out LauncherTask task, out string message, out string reason)
    {
        task = null!;
        message = string.Empty;
        reason = string.Empty;

        if (!TryGetSelectedVisibleTaskIndex(out var taskIndex) || !TryGetTask(taskIndex, out task))
        {
            message = "Select a task in the current group before choosing an agent.";
            reason = "no_selected_task";
            return false;
        }

        if (!IsTaskEnabled(task))
        {
            message = $"Task '{task.Name}' is disabled.";
            reason = "task_disabled";
            return false;
        }

        return true;
    }

    private void LogAgentDispatchValidationFailure(string? taskName, string? agentId, string reason, string message)
    {
        LogAgentDispatchTelemetry(
            AgentDispatchValidationEvent,
            $"task={NormalizeMarkerToken(taskName)} agent={NormalizeMarkerToken(agentId)} reason={NormalizeMarkerToken(reason)} message={NormalizeMarkerToken(message)}");
    }

    private void LogDispatchRequest(
        LauncherTask task,
        ExecutionTargetSelection executionTargetSelection,
        string requestedAgentToken,
        IReadOnlyDictionary<string, string> resolvedInputs,
        IReadOnlyDictionary<string, string> declaredOutputs)
    {
        var corr = BuildRemoteCorrelationId(task.Name, requestedAgentToken, "dispatch-request");
        LogAgentDispatchTelemetry(
            AgentDispatchRequestEvent,
            $"task={NormalizeMarkerToken(task.Name)} mode={executionTargetSelection.Mode} requestedAgent={NormalizeMarkerToken(requestedAgentToken)} pool={NormalizeMarkerToken(executionTargetSelection.PoolId)} runner={NormalizeMarkerToken(task.Runner)} waitForExit={task.WaitForExit} timeoutSec={task.TimeoutSeconds} inputs={resolvedInputs.Count} outputs={declaredOutputs.Count} corr={corr}");
    }

    private void LogDispatchResponse(LauncherTask task, string requestedAgentToken, RemoteDispatchReceipt receipt)
    {
        var resolvedAgentToken = string.IsNullOrWhiteSpace(receipt.AgentId)
            ? requestedAgentToken
            : receipt.AgentId;
        var attemptToken = string.IsNullOrWhiteSpace(receipt.AttemptId)
            ? "dispatch-failed"
            : receipt.AttemptId;
        var corr = BuildRemoteCorrelationId(task.Name, resolvedAgentToken, attemptToken);

        LogAgentDispatchTelemetry(
            AgentDispatchResponseEvent,
            $"task={NormalizeMarkerToken(task.Name)} dispatched={receipt.Dispatched} agent={NormalizeMarkerToken(receipt.AgentId)} run={NormalizeMarkerToken(receipt.RunId)} attempt={NormalizeMarkerToken(receipt.AttemptId)} corr={corr} message={NormalizeMarkerToken(receipt.Message)}");
    }

    private static void LogAgentDispatchTelemetry(string eventName, string message)
    {
        DebugTraceLog.Write($"{AgentDispatchTelemetryCategory}.{eventName}", message);
    }

    private static bool IsAgentEligibleForDispatch(AgentDefinition agent)
    {
        return agent.State == AgentDefinitionState.Online
            && !agent.IsDraining;
    }

    private AgentTransientExecutionState GetAgentTransientExecutionState(string agentId)
    {
        return _agentTransientExecutionStates.TryGetValue(agentId, out var state)
            ? state
            : AgentTransientExecutionState.Idle;
    }

    private static string GetAgentDispatchStateSummary(AgentDefinition agent, AgentTransientExecutionState transientState, int activeAttemptCount)
    {
        var stateLabel = agent.State.ToString();
        if (agent.IsDraining && agent.State != AgentDefinitionState.Draining)
        {
            stateLabel += " (Draining)";
        }

        if (activeAttemptCount > 0 || transientState is AgentTransientExecutionState.Dispatching or AgentTransientExecutionState.Running)
        {
            var countLabel = activeAttemptCount <= 0 ? "1" : activeAttemptCount.ToString(CultureInfo.InvariantCulture);
            stateLabel += $" · Running ({countLabel})";
        }

        return stateLabel;
    }

    private void SetAgentsDispatchValidationMessage(string message, bool isError)
    {
        if (AgentsDispatchValidationText is null)
        {
            return;
        }

        AgentsDispatchValidationText.Text = message;
        AgentsDispatchValidationText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xE1, 0x6A, 0x6A))
            : (Brush)Resources["SecondaryTextBrush"];
    }

    private void UpdateRibbonTaskActionState()
    {
        if (RibbonToggleTaskEnabledButton is null || RibbonToggleTaskEnabledLabel is null || RibbonRunChainButton is null)
        {
            return;
        }

        if (_isAgentViewSelected)
        {
            RibbonAddActionLabel.Text = "Add Agent";
            RibbonEditActionLabel.Text = "Edit Agent";
            RibbonDeleteActionLabel.Text = "Delete";
            RibbonRunChainButton.IsEnabled = false;
            RibbonToggleTaskEnabledButton.IsEnabled = false;
            RibbonToggleTaskEnabledLabel.Text = "Enable";
            RibbonTaskSurfaceToggleButton.IsEnabled = false;
            return;
        }

        RibbonAddActionLabel.Text = "Add";
        RibbonEditActionLabel.Text = "Edit";
        RibbonDeleteActionLabel.Text = "Delete";
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

        var message = $"Delete group '{selectedGroup}' and {tasksInGroup.Count} task(s)?";

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

    private bool TryPersistConfiguration(
        string errorTitle,
        string errorPrefix)
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

    private static string BuildTaskVariableUsageTooltip(IReadOnlyList<TaskVariableService.TaskVariableUsage> usages)
    {
        if (usages.Count == 0)
        {
            return "No variables in use.";
        }

        var lines = new List<string>
        {
            "Variables in use:"
        };

        foreach (var usage in usages)
        {
            var valueDisplay = usage.IsResolved
                ? usage.Value
                : usage.IsDefined
                    ? "<empty>"
                    : "<unresolved>";
            lines.Add($"- ${{{usage.Name}}} = {valueDisplay}");
        }

        return string.Join(Environment.NewLine, lines);
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

    private async Task<bool> RunTaskChainFromTaskAsync(int startTaskIndex, ExecutionTargetSelection? forcedExecutionTargetSelection = null)
    {
        if (!TryGetTask(startTaskIndex, out var startTask))
        {
            AppMessageBox.Show(this, TaskUnavailableMessage, TaskEditorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
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
            return false;
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

            var result = await ExecuteTaskAsync(
                taskInfo.Task,
                null,
                isReplay: false,
                dataflowContext: context,
                chainStepLabel: chainLabel,
                forcedExecutionTargetSelection: forcedExecutionTargetSelection);

            if (!result.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<TaskExecutionResult> ExecuteTaskAsync(
        LauncherTask task,
        Button? sourceButton,
        bool isReplay,
        string? replayFromEntryId = null,
        DataflowContext? dataflowContext = null,
        string? chainStepLabel = null,
        CancellationToken externalCancellationToken = default,
        ExecutionTargetSelection? forcedExecutionTargetSelection = null)
    {
        var operationDetail = isReplay ? $"{task.Name}-replay" : task.Name;
        using var taskRunScope = DebugTraceLog.BeginOperation("task", operationDetail);
        var tabName = isReplay ? $"{task.Name} (replay)" : task.Name;
        var tabContext = CreateOutputTab(tabName);
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);
        SetTaskRunState(task, TaskRunState.Running);
        var groupVariables = GetGroupVariablesForGroup(task.Group);
        var groupExpandedTask = TaskVariableService.ApplyGroupVariables(task, groupVariables, out var unresolvedVariables);
        var resolved = DataflowPortResolver.Resolve(groupExpandedTask, dataflowContext);
        var effectiveTask = resolved.ResolvedTask;
        var publishedOutputs = new Dictionary<string, string>(resolved.ResolvedOutputs, StringComparer.OrdinalIgnoreCase);
        var historyEntry = StartHistoryEntry(
            effectiveTask,
            isReplay,
            replayFromEntryId);
        var capturedStandardOutput = string.Empty;
        var capturedStandardError = string.Empty;
        var taskStateKey = GetTaskStateKey(task);
        var taskCancellationSource = new CancellationTokenSource();
        var linkedCancellationSource = externalCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(taskCancellationSource.Token, externalCancellationToken)
            : null;
        var effectiveCancellationToken = linkedCancellationSource?.Token ?? taskCancellationSource.Token;
        _taskExecutionCancellation[taskStateKey] = taskCancellationSource;

        if (!string.IsNullOrWhiteSpace(chainStepLabel))
        {
            AppendLog(outputTextBox, $"[{task.Name}] Chain step {chainStepLabel}");
        }

        AppendDataflowTrace(outputTextBox, task.Name, resolved);

        if (resolved.UnresolvedPlaceholders.Count > 0)
        {
            AppendLog(
                outputTextBox,
                $"[{task.Name}] Execution blocked: unresolved dataflow placeholders detected: {string.Join(", ", resolved.UnresolvedPlaceholders)}.");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
            UnregisterTaskCancellation(task);
            linkedCancellationSource?.Dispose();
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }

        if (unresolvedVariables.Count > 0)
        {
            AppendLog(outputTextBox, $"[{task.Name}] Unresolved variables in group '{NormalizeGroupName(task.Group)}': {string.Join(", ", unresolvedVariables.Select(name => "${" + name + "}"))}.");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
            UnregisterTaskCancellation(task);
            linkedCancellationSource?.Dispose();
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }

        if (string.IsNullOrWhiteSpace(effectiveTask.CommandLine))
        {
            AppendLog(outputTextBox, $"[{task.Name}] Command line is empty.");
            SetTabStopped(tabContext, false);
            SetTaskRunState(task, TaskRunState.Failed);
            CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
            UnregisterTaskCancellation(task);
            linkedCancellationSource?.Dispose();
            return new TaskExecutionResult { Succeeded = false, ExitCode = null };
        }

        var executionTargetSelection = forcedExecutionTargetSelection ?? ResolveExecutionTargetSelection(effectiveTask);
        if (executionTargetSelection.Mode != ExecutionTargetMode.Local)
        {
            var requestedAgentToken = string.IsNullOrWhiteSpace(executionTargetSelection.AgentId)
                ? "(auto)"
                : executionTargetSelection.AgentId;

            if (TryGetActiveAttemptBindingForTask(taskStateKey, out var existingBinding))
            {
                var existingAgentToken = string.IsNullOrWhiteSpace(existingBinding.AgentId)
                    ? "(unknown)"
                    : existingBinding.AgentId;
                var immutableMessage =
                    $"Task '{task.Name}' already has an active binding: agent={existingAgentToken}, attempt={existingBinding.AttemptId}. Retargeting is blocked until completion.";
                AppendLog(outputTextBox, $"[{task.Name}] {immutableMessage}");
                AppendLog(
                    outputTextBox,
                    AgentDispatchLifecycleMarkers.Error(task.Name, requestedAgentToken, "active_binding_immutable", "retarget-blocked"));
                SetTabStopped(tabContext, false);
                SetTaskRunState(task, TaskRunState.Failed);
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
                return new TaskExecutionResult { Succeeded = false, ExitCode = null };
            }

            LogDispatchRequest(
                effectiveTask,
                executionTargetSelection,
                requestedAgentToken,
                resolved.ResolvedInputs,
                publishedOutputs);

            var receipt = await DispatchRemoteOfferWithRetryAsync(
                effectiveTask,
                executionTargetSelection,
                resolved.ResolvedInputs,
                publishedOutputs,
                effectiveCancellationToken);

            LogDispatchResponse(effectiveTask, requestedAgentToken, receipt);

            if (!receipt.Dispatched || string.IsNullOrWhiteSpace(receipt.AgentId) || string.IsNullOrWhiteSpace(receipt.RunId) || string.IsNullOrWhiteSpace(receipt.AttemptId))
            {
                AppendLog(outputTextBox, $"[{task.Name}] Remote dispatch failed. {receipt.Message}");
                AppendLog(
                    outputTextBox,
                    AgentDispatchLifecycleMarkers.Error(task.Name, requestedAgentToken, receipt.Message, "dispatch-failed"));
                SetTabStopped(tabContext, false);
                SetTaskRunState(task, TaskRunState.Failed);
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.LaunchFailed, null);
                if (!string.IsNullOrWhiteSpace(executionTargetSelection.AgentId))
                {
                    SetAgentTransientExecutionState(executionTargetSelection.AgentId, AgentTransientExecutionState.Failed, "dispatch_rejected");
                }
                UnregisterTaskCancellation(task);
                linkedCancellationSource?.Dispose();
                return new TaskExecutionResult { Succeeded = false, ExitCode = null };
            }

            _taskRemoteAttempts[taskStateKey] = new RemoteAttemptHandle
            {
                AgentId = receipt.AgentId,
                RunId = receipt.RunId,
                AttemptId = receipt.AttemptId
            };

            SetOutputTabAgentTitle(tabContext, ResolveAgentTitleName(receipt.AgentId));

            var remoteTracking = new RemoteAttemptTracking
            {
                AgentId = receipt.AgentId,
                AttemptId = receipt.AttemptId,
                RunId = receipt.RunId,
                TaskName = task.Name,
                TaskStateKey = taskStateKey,
                OutputTextBox = outputTextBox,
                EndMarkerWritten = false
            };
            _remoteAttemptTracking[receipt.AttemptId] = remoteTracking;

            RegisterAttemptBinding(new AttemptBindingRecord
            {
                TaskStateKey = taskStateKey,
                TaskName = task.Name,
                AgentId = receipt.AgentId,
                RunId = receipt.RunId,
                AttemptId = receipt.AttemptId
            });

            if (!_runningAttemptIdsByAgent.TryGetValue(receipt.AgentId, out var runningAttempts))
            {
                runningAttempts = new Queue<string>();
                _runningAttemptIdsByAgent[receipt.AgentId] = runningAttempts;
            }

            runningAttempts.Enqueue(receipt.AttemptId);

            AppendLog(outputTextBox, $"[{task.Name}] Offer sent (pending agent acceptance). {receipt.Message}");
            AppendLog(outputTextBox, $"[{task.Name}] run_id={receipt.RunId}, attempt_id={receipt.AttemptId}");
            AppendLog(
                outputTextBox,
                AgentDispatchLifecycleMarkers.Start(new AgentDispatchLifecycleContext(task.Name, receipt.AgentId, receipt.RunId, receipt.AttemptId)));
            SetTaskRunState(task, TaskRunState.Running);
            SetAgentTransientExecutionState(receipt.AgentId, AgentTransientExecutionState.Running, "dispatch_accepted");

            var shouldWaitForRemoteCompletion = dataflowContext is not null;
            if (!shouldWaitForRemoteCompletion)
            {
                CompleteHistoryEntry(historyEntry, TaskRunOutcome.Started, null);

                return new TaskExecutionResult
                {
                    Succeeded = true,
                    ExitCode = null,
                    PublishedOutputs = new Dictionary<string, string>(publishedOutputs, StringComparer.OrdinalIgnoreCase)
                };
            }

            if (!effectiveTask.WaitForExit)
            {
                AppendLog(outputTextBox, $"[{task.Name}] Chain execution requires terminal completion in remote mode; waiting for completion despite waitForExit=false.");
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? remoteCompletionWaitCts = null;
            var waitToken = effectiveCancellationToken;
            if (effectiveTask.TimeoutSeconds > 0)
            {
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTask.TimeoutSeconds));
                remoteCompletionWaitCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCancellationToken, timeoutCts.Token);
                waitToken = remoteCompletionWaitCts.Token;
            }

            RemoteAttemptResult remoteCompletion;
            try
            {
                remoteCompletion = await remoteTracking.CompletionSource.Task.WaitAsync(waitToken);
            }
            catch (OperationCanceledException)
            {
                var timedOut = timeoutCts?.IsCancellationRequested == true && !effectiveCancellationToken.IsCancellationRequested;
                var cancelReason = timedOut ? "timeout" : "cancellation";
                AppendLog(outputTextBox, $"[{task.Name}] Remote chain step ended early due to {cancelReason}. Sending cancel to agent.");

                if (_remoteControlPlaneService.TryCancel(receipt.AgentId, receipt.RunId, receipt.AttemptId, out var cancelMessage))
                {
                    AppendLog(outputTextBox, $"[{task.Name}] {cancelMessage}");
                }
                else
                {
                    AppendLog(outputTextBox, $"[{task.Name}] Failed to send remote cancellation: {cancelMessage}");
                }

                SetTaskRunState(task, TaskRunState.Failed);
                SetTabStopped(tabContext, false);
                CompleteHistoryEntry(historyEntry, timedOut ? TaskRunOutcome.TimedOut : TaskRunOutcome.Cancelled, null);

                return new TaskExecutionResult
                {
                    Succeeded = false,
                    ExitCode = null,
                    PublishedOutputs = new Dictionary<string, string>(publishedOutputs, StringComparer.OrdinalIgnoreCase)
                };
            }
            finally
            {
                remoteCompletionWaitCts?.Dispose();
                timeoutCts?.Dispose();
            }

            foreach (var produced in remoteCompletion.ProducedOutputs)
            {
                publishedOutputs[produced.Key] = produced.Value;
            }

            publishedOutputs = ResolveRuntimeOutputValues(
                publishedOutputs,
                remoteCompletion.StandardOutput,
                remoteCompletion.StandardError,
                remoteCompletion.ExitCode);

            if (remoteCompletion.Succeeded && publishedOutputs.Count > 0)
            {
                AppendPublishedOutputs(outputTextBox, task.Name, publishedOutputs);
                dataflowContext?.RegisterTaskOutputs(task.Name, publishedOutputs);
            }

            if (!remoteCompletion.Succeeded && !string.IsNullOrWhiteSpace(remoteCompletion.Message))
            {
                AppendLog(outputTextBox, $"[{task.Name}] Remote step failed: {remoteCompletion.Message}");
            }

            CompleteHistoryEntry(historyEntry, remoteCompletion.Outcome, remoteCompletion.ExitCode);

            return new TaskExecutionResult
            {
                Succeeded = remoteCompletion.Succeeded,
                ExitCode = remoteCompletion.ExitCode,
                PublishedOutputs = new Dictionary<string, string>(publishedOutputs, StringComparer.OrdinalIgnoreCase)
            };
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
        AppendLog(outputTextBox, $"[{task.Name}] {modePrefix}Running ({runner}, {executionMode}): {PreviewForUiLog(effectiveTask.CommandLine, MaxUiPublishedValueChars)}");
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
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        AppendStreamOutputLine(outputTextBox, isError ? "stderr" : "stdout", line);
                    }));
                },
                cancellationToken: effectiveCancellationToken);

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
            linkedCancellationSource?.Dispose();

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

    private TaskRunHistoryEntry StartHistoryEntry(
        LauncherTask task,
        bool isReplay,
        string? replayFromEntryId)
    {
        var entry = _runHistoryService.StartEntry(
            task,
            isReplay,
            replayFromEntryId);
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
        var startedAtLocal = DateTime.Now;
        var runTitle = BuildOutputTabTitle(taskName, null, startedAtLocal);
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
            BaseTaskName = taskName,
            StartedAtLocal = startedAtLocal,
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
        _outputBudgetByTextBox[outputTextBox] = new OutputBudgetState();
        UpdateOutputListPlaceholder(currentOutputPanel);

        tabContext.RunItemExpander.IsExpanded = true;
        outputTextBox.Focus();

        return tabContext;
    }

    private static string BuildOutputTabTitle(string taskName, string? agentName, DateTime startedAtLocal)
    {
        var normalizedTaskName = string.IsNullOrWhiteSpace(taskName) ? "Task" : taskName.Trim();
        var normalizedAgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim();
        var agentSuffix = normalizedAgentName is null ? string.Empty : $" [{normalizedAgentName}]";
        return $"{normalizedTaskName}{agentSuffix} {startedAtLocal:HH:mm:ss}";
    }

    private void SetOutputTabAgentTitle(TaskTabContext tabContext, string? agentName)
    {
        tabContext.TitleText.Text = BuildOutputTabTitle(tabContext.BaseTaskName, agentName, tabContext.StartedAtLocal);
    }

    private string ResolveAgentTitleName(string agentId)
    {
        var agent = FindAgent(agentId);
        if (agent is null)
        {
            return agentId;
        }

        return string.IsNullOrWhiteSpace(agent.EffectiveDisplayName)
            ? agentId
            : agent.EffectiveDisplayName;
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
            RunListScrollViewer = listScroll,
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
        _outputBudgetByTextBox.Remove(runContext.OutputTextBox);

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
        textBox.PreviewKeyDown += OutputTextBox_PreviewKeyDown;

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

    private void OutputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not RichTextBox outputTextBox)
        {
            return;
        }

        if (!TryGetOutputTabContext(outputTextBox, out var tabContext))
        {
            return;
        }

        TryHandleOutputNavigationKey(tabContext.HostOutputPanel, e);
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
            builder.AppendLine($"  - {PreviewForUiLog(line, MaxUiTraceLineChars)}");
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
            builder.AppendLine($"  - {pair.Key} = {PreviewForUiLog(pair.Value, MaxUiPublishedValueChars)}");
        }

        AppendLog(outputTextBox, builder.ToString().TrimEnd());
    }

    private void AppendLog(RichTextBox outputTextBox, string message)
    {
        var boundedMessage = PreviewForUiLog(message, MaxUiLogMessageChars);
        if (!TryConsumeOutputBudget(outputTextBox, boundedMessage.Length, out var budgetState))
        {
            if (!budgetState.LimitReachedLogged)
            {
                budgetState.LimitReachedLogged = true;
                AppendLogBypassBudget(outputTextBox, "[system] Output limit reached for this run; additional log lines are suppressed to keep the UI responsive.");
            }

            return;
        }

        var shouldAutoScroll = ShouldAutoScrollOutput(outputTextBox);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var paragraph = EnsureOutputParagraph(outputTextBox);
        AppendTimestampRun(paragraph, timestamp);
        paragraph.Inlines.Add(new Run(" "));
        AppendTaggedMessageRuns(paragraph, boundedMessage);
        paragraph.Inlines.Add(new Run(Environment.NewLine + Environment.NewLine));
        if (shouldAutoScroll)
        {
            outputTextBox.ScrollToEnd();
        }
    }

    private void AppendStreamOutputLine(RichTextBox outputTextBox, string streamName, string line)
    {
        var boundedLine = PreviewForUiLog(line, MaxUiStreamLineChars);
        if (!TryConsumeOutputBudget(outputTextBox, boundedLine.Length, out var budgetState))
        {
            if (!budgetState.LimitReachedLogged)
            {
                budgetState.LimitReachedLogged = true;
                AppendLogBypassBudget(outputTextBox, "[system] Output limit reached for this run; additional stream output is suppressed to keep the UI responsive.");
            }

            return;
        }

        var shouldAutoScroll = ShouldAutoScrollOutput(outputTextBox);
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

        AppendAnsiText(outputTextBox, boundedLine);
        AppendPlainText(outputTextBox, Environment.NewLine + Environment.NewLine);
        if (shouldAutoScroll)
        {
            outputTextBox.ScrollToEnd();
        }
    }

    private bool TryConsumeOutputBudget(RichTextBox outputTextBox, int incomingChars, out OutputBudgetState state)
    {
        if (!_outputBudgetByTextBox.TryGetValue(outputTextBox, out state!))
        {
            state = new OutputBudgetState();
            _outputBudgetByTextBox[outputTextBox] = state;
        }

        if (state.TotalCharsAppended >= MaxUiOutputCharsPerRun)
        {
            return false;
        }

        state.TotalCharsAppended += Math.Max(0, incomingChars);
        return true;
    }

    private void AppendLogBypassBudget(RichTextBox outputTextBox, string message)
    {
        var shouldAutoScroll = ShouldAutoScrollOutput(outputTextBox);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var paragraph = EnsureOutputParagraph(outputTextBox);
        AppendTimestampRun(paragraph, timestamp);
        paragraph.Inlines.Add(new Run(" "));
        AppendTaggedMessageRuns(paragraph, PreviewForUiLog(message, MaxUiLogMessageChars));
        paragraph.Inlines.Add(new Run(Environment.NewLine + Environment.NewLine));
        if (shouldAutoScroll)
        {
            outputTextBox.ScrollToEnd();
        }
    }

    private static string PreviewForUiLog(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars < 32)
        {
            return value ?? string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        var omitted = value.Length - maxChars;
        return value[..maxChars] + $" ... [truncated {omitted} chars]";
    }

    private async Task<RemoteDispatchReceipt> DispatchRemoteOfferWithRetryAsync(
        LauncherTask task,
        ExecutionTargetSelection selection,
        IReadOnlyDictionary<string, string> resolvedInputs,
        IReadOnlyDictionary<string, string> publishedOutputs,
        CancellationToken cancellationToken)
    {
        var receipt = await Task.Run(
            () => _remoteControlPlaneService.TryDispatchTask(task, selection, resolvedInputs, publishedOutputs),
            cancellationToken);

        if (receipt.Dispatched || !IsDispatchTimeoutReceipt(receipt) || cancellationToken.IsCancellationRequested)
        {
            return receipt;
        }

        // Transient write stalls can clear once the stream resumes; retry once before failing the chain step.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        return await Task.Run(
            () => _remoteControlPlaneService.TryDispatchTask(task, selection, resolvedInputs, publishedOutputs),
            cancellationToken);
    }

    private static bool IsDispatchTimeoutReceipt(RemoteDispatchReceipt receipt)
    {
        if (receipt.Dispatched)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(receipt.Message)
            && receipt.Message.Contains("Timed out sending frame to agent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAutoScrollOutput(RichTextBox outputTextBox)
    {
        if (outputTextBox.IsKeyboardFocusWithin && !outputTextBox.Selection.IsEmpty)
        {
            return false;
        }

        var scrollViewer = FindVisualDescendant<ScrollViewer>(outputTextBox);
        if (scrollViewer is null)
        {
            return true;
        }

        if (scrollViewer.ScrollableHeight <= 0)
        {
            return true;
        }

        const double autoScrollThreshold = 6;
        return scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - autoScrollThreshold;
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
