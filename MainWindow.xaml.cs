using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly string _configPath;
    private TaskConfiguration _currentConfiguration = new();
    private UiState _uiState = new();
    private Point _dragStartPoint;
    private bool _isDraggingTaskCard;
    private Border? _activeDropCueBorder;
    private static readonly string[] RunningIndicatorFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private sealed class TaskTabContext
    {
        public required TabItem TabItem { get; init; }

        public required TextBox OutputTextBox { get; init; }

        public required TextBlock StatusText { get; init; }

        public required DispatcherTimer SpinnerTimer { get; init; }

        public int SpinnerFrameIndex { get; set; }
    }

    private sealed class TaskCardVisual
    {
        public required int TaskIndex { get; init; }

        public required Border DropCueBorder { get; init; }
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
        SourceInitialized += MainWindow_SourceInitialized;
        App.ThemeChanged += OnThemeChanged;
        Closing += MainWindow_Closing;

        _uiState = UiStateStore.Load();
        ApplyPersistedUiState();

        TaskConfiguration.EnsureValidFileOrReset(_configPath);

        LoadTaskButtons();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyTitleBarTheme(App.IsDarkThemeEnabled());
    }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => ApplyTitleBarTheme(useDarkTheme));
    }

    private void ApplyTitleBarTheme(bool useDarkTheme)
    {
        WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        PersistUiState();
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
        var currentDirectoryPath = Path.Combine(Environment.CurrentDirectory, "tasks.json");
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        var appDirectoryPath = Path.Combine(AppContext.BaseDirectory, "tasks.json");
        if (File.Exists(appDirectoryPath))
        {
            return appDirectoryPath;
        }

        return currentDirectoryPath;
    }

    private void LoadTaskButtons()
    {
        _currentConfiguration = TaskConfiguration.Load(_configPath);
        TaskPanel.Children.Clear();

        TaskPanel.AllowDrop = true;
        TaskPanel.DragOver -= TaskPanel_DragOver;
        TaskPanel.Drop -= TaskPanel_Drop;
        TaskPanel.DragLeave -= TaskPanel_DragLeave;
        TaskPanel.DragOver += TaskPanel_DragOver;
        TaskPanel.Drop += TaskPanel_Drop;
        TaskPanel.DragLeave += TaskPanel_DragLeave;

        AddCreateTaskCard();
        EmptyStateText.Text = "No tasks are configured. Add entries to tasks.json to show buttons.";
        EmptyStateText.Visibility = _currentConfiguration.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var index = 0; index < _currentConfiguration.Tasks.Count; index++)
        {
            var task = _currentConfiguration.Tasks[index];
            var cardColor = PastelPalette[index % PastelPalette.Length];
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
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.9,
                Visibility = string.IsNullOrWhiteSpace(task.Description) ? Visibility.Collapsed : Visibility.Visible,
                MaxHeight = 60
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

            card.Children.Add(button);
            card.Children.Add(dropCueBorder);
            card.Children.Add(menuButton);
            TaskPanel.Children.Add(card);
        }
    }

    private void AddCreateTaskCard()
    {
        var card = new Grid
        {
            Width = 130,
            Height = 130,
            Margin = new Thickness(0, 0, 14, 14)
        };

        var plusText = new TextBlock
        {
            Text = "+",
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 4)
        };

        var content = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(plusText);

        var button = new Button
        {
            Content = content,
            Style = (Style)Resources["TaskCardButtonStyle"],
            Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xF5, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x3A, 0x56)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        button.Click += AddTaskButton_Click;

        card.Children.Add(button);
        TaskPanel.Children.Add(card);
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

        SaveTasksAndRefresh(TaskEditorTitle);
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

        var targetIndex = GetDropTargetTaskIndex(e.GetPosition(TaskPanel));
        ApplyDropCue(targetIndex);

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

        var sourceIndex = (int)e.Data.GetData(typeof(int))!;
        var targetIndex = GetDropTargetTaskIndex(e.GetPosition(TaskPanel));

        if (targetIndex < 0 || sourceIndex < 0 || sourceIndex >= _currentConfiguration.Tasks.Count)
        {
            return;
        }

        if (targetIndex > _currentConfiguration.Tasks.Count)
        {
            targetIndex = _currentConfiguration.Tasks.Count;
        }

        if (sourceIndex == targetIndex || sourceIndex == targetIndex - 1)
        {
            return;
        }

        var movedTask = _currentConfiguration.Tasks[sourceIndex];
        _currentConfiguration.Tasks.RemoveAt(sourceIndex);

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        if (targetIndex < 0)
        {
            targetIndex = 0;
        }

        if (targetIndex > _currentConfiguration.Tasks.Count)
        {
            targetIndex = _currentConfiguration.Tasks.Count;
        }

        _currentConfiguration.Tasks.Insert(targetIndex, movedTask);

        SaveTasksAndRefresh("Task Reorder");
    }

    private int GetDropTargetTaskIndex(Point dropPoint)
    {
        for (var panelIndex = 1; panelIndex < TaskPanel.Children.Count; panelIndex++)
        {
            if (TaskPanel.Children[panelIndex] is not Grid card || card.Tag is not TaskCardVisual cardVisual)
            {
                continue;
            }

            var taskIndex = cardVisual.TaskIndex;

            var topLeft = card.TransformToAncestor(TaskPanel).Transform(new Point(0, 0));
            var cardCenterX = topLeft.X + card.ActualWidth / 2;
            var cardCenterY = topLeft.Y + card.ActualHeight / 2;

            if (dropPoint.Y < cardCenterY || (Math.Abs(dropPoint.Y - cardCenterY) < card.ActualHeight * 0.45 && dropPoint.X < cardCenterX))
            {
                return taskIndex;
            }
        }

        return _currentConfiguration.Tasks.Count;
    }

    private void ApplyDropCue(int targetIndex)
    {
        ClearDropCue();

        if (_currentConfiguration.Tasks.Count == 0)
        {
            return;
        }

        var cueTaskIndex = targetIndex >= _currentConfiguration.Tasks.Count
            ? _currentConfiguration.Tasks.Count - 1
            : targetIndex;

        for (var panelIndex = 1; panelIndex < TaskPanel.Children.Count; panelIndex++)
        {
            if (TaskPanel.Children[panelIndex] is not Grid card || card.Tag is not TaskCardVisual cardVisual)
            {
                continue;
            }

            if (cardVisual.TaskIndex != cueTaskIndex)
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

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteTask(taskIndex);

        menu.Items.Add(editItem);
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

        var tabContext = CreateOutputTab(task.Name);
        var outputTextBox = tabContext.OutputTextBox;
        SetTabRunning(tabContext);

        if (string.IsNullOrWhiteSpace(task.CommandLine))
        {
            AppendLog(outputTextBox, $"[{task.Name}] Command line is empty.");
            SetTabStopped(tabContext, false);
            return;
        }

        var runner = TaskProcessStartInfoFactory.NormalizeRunner(task.Runner);

        var button = (Button)sender;
        button.IsEnabled = false;
        AppendLog(outputTextBox, $"[{task.Name}] Running ({runner}): {task.CommandLine}");

        try
        {
            if (!TaskProcessStartInfoFactory.TryBuild(task, out var processStartInfo, out var configurationError))
            {
                AppendLog(outputTextBox, $"[{task.Name}] Invalid task configuration. {configurationError}");
                SetTabStopped(tabContext, false);
                return;
            }

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            AppendProcessOutput(outputTextBox, task.Name, process.ExitCode, standardOutput, standardError);
            SetTabStopped(tabContext, process.ExitCode == 0);
        }
        catch (Exception ex)
        {
            AppendLog(outputTextBox, $"[{task.Name}] Failed to execute task. {ex.Message}");
            SetTabStopped(tabContext, false);
            MessageBox.Show($"Failed to run task '{task.Name}'.\n{ex.Message}",
                "Task Execution Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private TaskTabContext CreateOutputTab(string taskName)
    {
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

        OutputTabControl.Items.Add(tabItem);
        OutputTabControl.SelectedItem = tabItem;
        outputTextBox.Focus();

        return tabContext;
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
        if (OutputTabControl.Items.Contains(tabItem))
        {
            // Stop any tab spinner to avoid timers running after tab closure.
            if (tabItem.Tag is TaskTabContext tabContext)
            {
                tabContext.SpinnerTimer.Stop();
            }
        }

        var selectedIndex = OutputTabControl.Items.IndexOf(tabItem);
        OutputTabControl.Items.Remove(tabItem);

        if (OutputTabControl.Items.Count == 0)
        {
            return;
        }

        var nextIndex = Math.Min(selectedIndex, OutputTabControl.Items.Count - 1);
        OutputTabControl.SelectedIndex = nextIndex;
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

    private void AppendLog(TextBox outputTextBox, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        outputTextBox.AppendText($"[{timestamp}] {message}{Environment.NewLine}{Environment.NewLine}");
        outputTextBox.ScrollToEnd();
    }
}