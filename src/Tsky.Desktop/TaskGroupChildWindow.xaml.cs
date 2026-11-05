using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace tsky;

internal partial class TaskGroupChildWindow : Window
{
    private const double GraphNodeWidth = 190;
    private const double GraphNodeHeight = 96;
    private const double GraphLayerSpacing = 68;
    private const double GraphRowSpacing = 32;
    private const double GraphSurfacePadding = 24;

    private static readonly Color[] CardPalette =
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

    private readonly TaskGroupSurfaceSession _session;
    private readonly TaskGroupChildWindowActions _actions;
    private readonly DataflowGraphService _graphService = new();
    private readonly Dictionary<int, Grid> _taskCardsByIndex = [];
    private TaskGroupSurfaceSnapshot _snapshot = TaskGroupSurfaceSnapshot.Empty;

    internal TaskGroupChildWindow(TaskGroupSurfaceSession session, TaskGroupChildWindowActions actions)
    {
        InitializeComponent();
        _session = session;
        _actions = actions;

        ChildSurfaceScrollViewer.SizeChanged += (_, _) => SyncTaskSurfaceToViewport();
        ChildTaskPanel.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        ChildSurfaceRoot.SizeChanged += (_, _) => ScheduleConnectorRefresh();
        SizeChanged += (_, _) => ScheduleConnectorRefresh();

        _session.StateChanged += Session_StateChanged;
        Closed += TaskGroupChildWindow_Closed;
    }

    internal void Refresh(TaskGroupSurfaceSnapshot snapshot)
    {
        _snapshot = snapshot;
        RenderSnapshot();
    }

    private void RenderSnapshot()
    {
        var groupName = _snapshot.GroupName;
        ChildGroupTitleText.Text = string.IsNullOrWhiteSpace(groupName) ? "No Group" : groupName;
        ChildGroupMetaText.Text = _snapshot.GroupMetaText;

        UpdateToggleViewModeLabel();

        ChildTaskPanel.Children.Clear();
        ChildTaskConnectorOverlay.Children.Clear();
        ChildGraphNodeCanvas.Children.Clear();
        ChildGraphEdgeCanvas.Children.Clear();
        _taskCardsByIndex.Clear();
        SyncTaskSurfaceToViewport();

        ChildRunChainButton.IsEnabled = _snapshot.Tasks.Any(item => item.IsEnabled);

        var hasValidGroup = _snapshot.HasValidGroup;
        var hasTasks = _snapshot.Tasks.Count > 0;

        if (!hasValidGroup)
        {
            ChildTaskPanel.Visibility = Visibility.Collapsed;
            ChildTaskConnectorOverlay.Visibility = Visibility.Collapsed;
            ChildGraphNodeCanvas.Visibility = Visibility.Collapsed;
            ChildGraphEdgeCanvas.Visibility = Visibility.Collapsed;
            ChildEmptyStateText.Text = "No group is currently selected.";
            ChildEmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        if (_snapshot.IsGraphMode)
        {
            ChildTaskPanel.Visibility = Visibility.Collapsed;
            ChildTaskConnectorOverlay.Visibility = Visibility.Collapsed;
            ChildGraphNodeCanvas.Visibility = Visibility.Visible;
            ChildGraphEdgeCanvas.Visibility = Visibility.Visible;

            if (!hasTasks)
            {
                ChildEmptyStateText.Text = _snapshot.EmptyStateText;
                ChildEmptyStateText.Visibility = Visibility.Visible;
                return;
            }

            ChildEmptyStateText.Visibility = Visibility.Collapsed;
            RenderGraphSurface();
            SyncTaskSurfaceToViewport();
            return;
        }

        ChildGraphNodeCanvas.Visibility = Visibility.Collapsed;
        ChildGraphEdgeCanvas.Visibility = Visibility.Collapsed;
        ChildTaskPanel.Visibility = Visibility.Visible;
        ChildTaskConnectorOverlay.Visibility = Visibility.Visible;

        if (!hasTasks)
        {
            ChildEmptyStateText.Text = _snapshot.EmptyStateText;
            ChildEmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        ChildEmptyStateText.Visibility = Visibility.Collapsed;
        RenderTaskCards();
        SyncTaskSurfaceToViewport();
    }

    private void RenderTaskCards()
    {
        var cardFontFamily = TryFindResource("CardUiFontFamily") as FontFamily ?? new FontFamily("Segoe UI");

        for (var i = 0; i < _snapshot.Tasks.Count; i++)
        {
            var item = _snapshot.Tasks[i];
            if (!TryGetSnapshotTask(item.TaskIndex, out var task))
            {
                continue;
            }

            var cardColor = CardPalette[i % CardPalette.Length];
            var cardBackgroundBrush = new SolidColorBrush(cardColor);
            var textBrush = GetReadableTextBrush(cardColor);

            var card = new Grid
            {
                Width = 195,
                Height = 195,
                Margin = new Thickness(0, 0, 18, 18)
            };

            var selectionBorder = new Border
            {
                BorderThickness = item.IsSelected ? new Thickness(3) : new Thickness(0),
                BorderBrush = item.IsSelected
                    ? BuildTaskSelectionBorderBrush(cardBackgroundBrush)
                    : Brushes.Transparent,
                CornerRadius = new CornerRadius(0),
                IsHitTestVisible = false
            };

            var contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var overlayColor = textBrush is SolidColorBrush solidForeground
                ? Color.FromArgb(28, solidForeground.Color.R, solidForeground.Color.G, solidForeground.Color.B)
                : Color.FromArgb(28, 0, 0, 0);

            var titleText = new TextBlock
            {
                Text = item.Name,
                FontWeight = FontWeights.Bold,
                FontFamily = cardFontFamily,
                FontSize = 16,
                Foreground = textBrush,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 22,
                MaxHeight = 44
            };

            var titleBar = new Border
            {
                Background = new SolidColorBrush(overlayColor),
                Padding = new Thickness(10, 8, 10, 8),
                Child = titleText
            };
            Grid.SetRow(titleBar, 0);

            var inputPorts = item.InputPortNames;
            var outputPorts = item.OutputPortNames;

            var descriptionText = new TextBlock
            {
                Text = item.Description,
                FontFamily = cardFontFamily,
                FontSize = 12.5,
                Foreground = textBrush,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 80,
                Opacity = string.IsNullOrWhiteSpace(item.Description) ? 0.65 : 0.9
            };

            var descriptionContainer = new Border
            {
                Padding = new Thickness(
                    12 + (inputPorts.Count > 0 ? 8 : 0),
                    10,
                    12 + (outputPorts.Count > 0 ? 8 : 0),
                    10),
                Child = descriptionText
            };
            Grid.SetRow(descriptionContainer, 1);

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
                Foreground = textBrush,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Visibility = item.IsEnabled ? Visibility.Collapsed : Visibility.Visible,
                ToolTip = "This task is disabled and will not run"
            };
            if (disabledText.Visibility == Visibility.Visible)
            {
                bottomGlyphs.Children.Add(disabledText);
            }

            var orderText = new TextBlock
            {
                Text = $"#{item.DisplayOrder}",
                FontFamily = cardFontFamily,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                Opacity = 0.88,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Task order in current group"
            };
            bottomGlyphs.Children.Add(orderText);

            if (item.HasVariableUsage)
            {
                bottomGlyphs.Children.Add(CreateTaskCardFooterGlyph(
                    text: "𝑥",
                    fontFamily: new FontFamily("Cambria Math"),
                    foregroundBrush: textBrush,
                    toolTip: item.VariableUsageToolTip));
            }

            if (item.HasResultEvaluatorScript)
            {
                bottomGlyphs.Children.Add(CreateTaskCardFooterGlyph(
                    text: "ƒ",
                    fontFamily: cardFontFamily,
                    foregroundBrush: textBrush,
                    toolTip: "Task has a result evaluator script"));
            }

            if (item.HasImportedSource)
            {
                bottomGlyphs.Children.Add(CreateTaskCardFooterGlyph(
                    text: "I",
                    fontFamily: cardFontFamily,
                    foregroundBrush: textBrush,
                    toolTip: item.ImportedSourceToolTip));
            }

            if (item.RequiresElevation)
            {
                bottomGlyphs.Children.Add(CreateTaskCardFooterGlyph(
                    text: "\uE72E",
                    fontFamily: new FontFamily("Segoe MDL2 Assets"),
                    foregroundBrush: textBrush,
                    toolTip: "Requires administrator privileges"));
            }

            var footerBar = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 6, 8, 6),
                Child = bottomGlyphs
            };
            Grid.SetRow(footerBar, 2);

            contentGrid.Children.Add(titleBar);
            contentGrid.Children.Add(descriptionContainer);
            contentGrid.Children.Add(footerBar);

            var button = new Button
            {
                Style = (Style)Resources["ChildTaskCardButtonStyle"],
                Background = cardBackgroundBrush,
                Content = contentGrid,
                Opacity = item.IsEnabled ? 1.0 : 0.66,
                ContextMenu = CreateTaskContextMenu(item)
            };

            button.Click += async (_, _) => await ExecuteTaskActionAsync(item.TaskIndex, runTask: true);
            button.ContextMenuOpening += (_, _) => _actions.SelectTask(item.TaskIndex);

            card.Children.Add(button);

            if (inputPorts.Count > 0)
            {
                card.Children.Add(CreatePortStack(inputPorts, isInput: true, task.Inputs, task.InputBindings));
            }

            if (outputPorts.Count > 0)
            {
                card.Children.Add(CreatePortStack(outputPorts, isInput: false));
            }

            card.Children.Add(selectionBorder);
            ChildTaskPanel.Children.Add(card);
            _taskCardsByIndex[item.TaskIndex] = card;
        }

        ScheduleConnectorRefresh();
    }

    private bool TryGetSnapshotTask(int taskIndex, out LauncherTask task)
    {
        if (taskIndex >= 0 && taskIndex < _snapshot.AllTasks.Count)
        {
            task = _snapshot.AllTasks[taskIndex];
            return true;
        }

        task = default!;
        return false;
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

    private static Brush GetReadableTextBrush(Color backgroundColor)
    {
        return new SolidColorBrush(Color.FromRgb(0x12, 0x1E, 0x2A));
    }

    private static TextBlock CreateTaskCardFooterGlyph(string text, FontFamily fontFamily, Brush foregroundBrush, string toolTip)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = foregroundBrush,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = toolTip
        };
    }

    private static StackPanel CreatePortStack(
        IReadOnlyList<string> portNames,
        bool isInput,
        IReadOnlyDictionary<string, string>? inputDefaults = null,
        IReadOnlyDictionary<string, string>? inputBindings = null)
    {
        var fillBrush = isInput
            ? ResolveThemeBrush("InputPortDotBrush", Color.FromRgb(0x45, 0x5A, 0x64))
            : ResolveThemeBrush("OutputPortDotBrush", Color.FromRgb(0x00, 0x96, 0x88));
        var strokeBrush = ResolveThemeBrush("PanelBorderBrush", Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = isInput ? new Thickness(4, 0, 0, 0) : new Thickness(0, 0, 4, 0),
            IsHitTestVisible = true
        };

        Panel.SetZIndex(stack, 15);

        foreach (var portName in portNames)
        {
            var toolTipText = isInput
                ? BuildInputPortIndicatorToolTip(portName, inputDefaults, inputBindings)
                : portName;

            var dot = new Ellipse
            {
                ToolTip = toolTipText,
                Width = 12,
                Height = 12,
                StrokeThickness = 1.5,
                Margin = new Thickness(0, 2, 0, 2),
                Fill = fillBrush,
                Stroke = strokeBrush
            };

            stack.Children.Add(dot);
        }

        return stack;
    }

    private static string BuildInputPortIndicatorToolTip(
        string portName,
        IReadOnlyDictionary<string, string>? inputDefaults,
        IReadOnlyDictionary<string, string>? inputBindings)
    {
        var normalizedPortName = string.IsNullOrWhiteSpace(portName)
            ? "(Unnamed Input)"
            : portName.Trim();

        var rawDefaultValue = string.Empty;
        var hasDefault = inputDefaults is not null && inputDefaults.TryGetValue(normalizedPortName, out rawDefaultValue);
        var defaultValue = hasDefault ? rawDefaultValue ?? string.Empty : string.Empty;
        var defaultDisplay = string.IsNullOrEmpty(defaultValue) ? "(blank)" : defaultValue;

        var rawBindingValue = string.Empty;
        var hasBinding = inputBindings is not null && inputBindings.TryGetValue(normalizedPortName, out rawBindingValue);
        var bindingValue = hasBinding ? (rawBindingValue ?? string.Empty).Trim() : string.Empty;
        var bindingDisplay = string.IsNullOrWhiteSpace(bindingValue) ? "(none)" : bindingValue;

        return string.Join(Environment.NewLine,
            $"Input: {normalizedPortName}",
            $"Default: {defaultDisplay}",
            $"Binding: {bindingDisplay}");
    }

    private void ScheduleConnectorRefresh()
    {
        if (_snapshot.IsGraphMode)
        {
            return;
        }

        Dispatcher.BeginInvoke(RedrawDataflowConnectors, DispatcherPriority.Loaded);
    }

    private void RedrawDataflowConnectors()
    {
        if (_snapshot.IsGraphMode)
        {
            ChildTaskConnectorOverlay.Children.Clear();
            return;
        }

        ChildTaskConnectorOverlay.Children.Clear();

        if (_snapshot.VisibleTaskIndexes.Count == 0 || _taskCardsByIndex.Count == 0)
        {
            ChildTaskConnectorOverlay.Width = 0;
            ChildTaskConnectorOverlay.Height = 0;
            return;
        }

        if (ChildTaskPanel.ActualWidth <= 0 || ChildTaskPanel.ActualHeight <= 0)
        {
            ChildTaskConnectorOverlay.Width = 0;
            ChildTaskConnectorOverlay.Height = 0;
            return;
        }

        ChildTaskConnectorOverlay.Width = ChildTaskPanel.ActualWidth;
        ChildTaskConnectorOverlay.Height = ChildTaskPanel.ActualHeight;
        var cardCoverGeometry = BuildTaskCardCoverGeometry();

        var connectorItems = _graphService.BuildConnectorComputations(_snapshot.AllTasks, _snapshot.VisibleTaskIndexes);

        foreach (var connectorItem in connectorItems)
        {
            if (!_taskCardsByIndex.TryGetValue(connectorItem.TargetTaskIndex, out var targetCard)
                || !TryGetSnapshotTask(connectorItem.TargetTaskIndex, out var targetTask))
            {
                continue;
            }

            var targetInputNames = GetInputPortNames(targetTask);
            var targetPoint = GetCardPortPoint(targetCard, isOutput: false, targetInputNames, connectorItem.TargetInputName);

            if (!connectorItem.SourceTaskIndex.HasValue
                || string.IsNullOrWhiteSpace(connectorItem.SourceOutputName)
                || !_taskCardsByIndex.TryGetValue(connectorItem.SourceTaskIndex.Value, out var sourceCard)
                || !TryGetSnapshotTask(connectorItem.SourceTaskIndex.Value, out var sourceTask))
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

            var sourcePoint = GetCardPortPoint(sourceCard, isOutput: true, GetOutputPortNames(sourceTask), connectorItem.SourceOutputName!);

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

            var topLeft = card.TranslatePoint(new Point(0, 0), ChildTaskConnectorOverlay);
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
                var center = dot.TranslatePoint(new Point(dot.ActualWidth / 2, dot.ActualHeight / 2), ChildTaskConnectorOverlay);
                if (!double.IsNaN(center.X) && !double.IsNaN(center.Y) && !double.IsInfinity(center.X) && !double.IsInfinity(center.Y))
                {
                    return center;
                }
            }
        }

        var cardOrigin = card.TranslatePoint(new Point(0, 0), ChildTaskConnectorOverlay);
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
            StrokeEndLineCap = PenLineCap.Round,
            ToolTip = _graphService.BuildConnectorToolTipText(new DataflowGraphService.ConnectorTooltipItem
            {
                Resolved = resolved,
                SourceTaskName = sourceTaskName,
                SourceOutputName = sourceOutputName,
                TargetTaskName = string.IsNullOrWhiteSpace(targetTaskName) ? "(Unnamed Task)" : targetTaskName.Trim(),
                TargetInputName = targetInputName,
                BindingReference = bindingReference,
                StatusDetail = statusDetail
            })
        };

        System.Windows.Shapes.Path? cardOverlayPath = null;

        if (cardCoverGeometry is not null)
        {
            var fullOverlayBounds = new RectangleGeometry(new Rect(0, 0, ChildTaskConnectorOverlay.Width, ChildTaskConnectorOverlay.Height));
            path.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, fullOverlayBounds, cardCoverGeometry);

            cardOverlayPath = new System.Windows.Shapes.Path
            {
                Data = ((PathGeometry)path.Data).Clone(),
                Stroke = stroke,
                StrokeThickness = path.StrokeThickness,
                Opacity = 0.20,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Clip = cardCoverGeometry,
                ToolTip = path.ToolTip
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

        ChildTaskConnectorOverlay.Children.Add(path);
        if (cardOverlayPath is not null)
        {
            ChildTaskConnectorOverlay.Children.Add(cardOverlayPath);
        }
    }

    private static Brush GetResolvedConnectorBrush()
    {
        return ResolveThemeBrush("ConnectorResolvedBrush", Color.FromRgb(0x0C, 0x8A, 0x83));
    }

    private static Brush GetUnresolvedConnectorBrush()
    {
        return ResolveThemeBrush("ConnectorUnresolvedBrush", Color.FromRgb(0xC6, 0x28, 0x28));
    }

    private void RenderGraphSurface()
    {
        var graphNodes = _graphService.BuildTaskGraphNodes(_snapshot.AllTasks, _snapshot.VisibleTaskIndexes);
        var graphEdges = _graphService.BuildTaskGraphEdges(_snapshot.AllTasks, _snapshot.VisibleTaskIndexes);

        if (graphNodes.Count == 0)
        {
            ChildGraphNodeCanvas.Width = ChildSurfaceRoot.ActualWidth;
            ChildGraphNodeCanvas.Height = 0;
            ChildGraphEdgeCanvas.Width = ChildSurfaceRoot.ActualWidth;
            ChildGraphEdgeCanvas.Height = 0;
            return;
        }

        var positions = ComputeGraphNodePositions(graphNodes, graphEdges, out var surfaceSize);
        var snapshotItems = _snapshot.Tasks.ToDictionary(item => item.TaskIndex, item => item);

        ChildGraphNodeCanvas.Width = surfaceSize.Width;
        ChildGraphNodeCanvas.Height = surfaceSize.Height;
        ChildGraphEdgeCanvas.Width = surfaceSize.Width;
        ChildGraphEdgeCanvas.Height = surfaceSize.Height;

        foreach (var edge in graphEdges)
        {
            if (!positions.TryGetValue(edge.SourceTaskIndex, out var source)
                || !positions.TryGetValue(edge.TargetTaskIndex, out var target)
                || edge.SourceTaskIndex == edge.TargetTaskIndex)
            {
                continue;
            }

            var sourceCenter = new Point(source.X + GraphNodeWidth, source.Y + (GraphNodeHeight / 2));
            var targetCenter = new Point(target.X, target.Y + (GraphNodeHeight / 2));
            AddGraphEdge(sourceCenter, targetCenter, edge.IsResolved, edge.Label);
        }

        foreach (var node in graphNodes)
        {
            if (!positions.TryGetValue(node.TaskIndex, out var position))
            {
                continue;
            }

            if (!snapshotItems.TryGetValue(node.TaskIndex, out var taskItem))
            {
                continue;
            }

            var nodeButton = CreateGraphNodeButton(node, taskItem);
            Canvas.SetLeft(nodeButton, position.X);
            Canvas.SetTop(nodeButton, position.Y);
            ChildGraphNodeCanvas.Children.Add(nodeButton);
        }
    }

    private Dictionary<int, Point> ComputeGraphNodePositions(
        IReadOnlyList<DataflowGraphService.TaskGraphNode> nodes,
        IReadOnlyList<DataflowGraphService.TaskGraphEdge> edges,
        out Size surfaceSize)
    {
        var positions = new Dictionary<int, Point>();
        var nodeOrder = nodes
            .Select((node, order) => new { node.TaskIndex, Order = order })
            .ToDictionary(item => item.TaskIndex, item => item.Order);

        var visibleWidth = Math.Max(420, ChildSurfaceScrollViewer.ViewportWidth > 0 ? ChildSurfaceScrollViewer.ViewportWidth : ChildSurfaceRoot.ActualWidth);
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
            if (!visited.Contains(node.TaskIndex))
            {
                layerByTask[node.TaskIndex] = 0;
            }
        }

        var layers = layerByTask
            .GroupBy(pair => pair.Value)
            .OrderBy(group => group.Key)
            .Select(group => group
                .Select(pair => pair.Key)
                .OrderBy(taskIndex => nodeOrder[taskIndex])
                .ToList())
            .ToList();

        var maxRows = 1;
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layerTasks = layers[layerIndex];
            maxRows = Math.Max(maxRows, layerTasks.Count);

            for (var rowIndex = 0; rowIndex < layerTasks.Count; rowIndex++)
            {
                var taskIndex = layerTasks[rowIndex];
                var x = GraphSurfacePadding + (layerIndex * (GraphNodeWidth + GraphLayerSpacing));
                var y = GraphSurfacePadding + (rowIndex * (GraphNodeHeight + GraphRowSpacing));
                positions[taskIndex] = new Point(x, y);
            }
        }

        var maxLayerCount = Math.Max(1, layers.Count);
        var requiredWidth = (GraphSurfacePadding * 2) + (maxLayerCount * GraphNodeWidth) + (Math.Max(0, maxLayerCount - 1) * GraphLayerSpacing);
        var requiredHeight = (GraphSurfacePadding * 2) + (maxRows * GraphNodeHeight) + (Math.Max(0, maxRows - 1) * GraphRowSpacing);

        surfaceSize = new Size(Math.Max(visibleWidth, requiredWidth), requiredHeight);
        return positions;
    }

    private void AddGraphEdge(Point sourceCenter, Point targetCenter, bool isResolved, string? label)
    {
        var edgeStroke = isResolved ? GetResolvedConnectorBrush() : GetUnresolvedConnectorBrush();
        var controlOffset = Math.Max(36, Math.Abs(targetCenter.X - sourceCenter.X) * 0.35);

        var figure = new PathFigure { StartPoint = sourceCenter, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(
            new Point(sourceCenter.X + controlOffset, sourceCenter.Y),
            new Point(targetCenter.X - controlOffset, targetCenter.Y),
            targetCenter,
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

        var direction = new Vector(targetCenter.X - sourceCenter.X, targetCenter.Y - sourceCenter.Y);
        if (direction.Length > 0.001)
        {
            direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            const double arrowLength = 11;
            const double arrowWidth = 5.5;

            var arrowTip = targetCenter;
            var arrowBase = arrowTip - (direction * arrowLength);
            var arrowLeft = arrowBase + (normal * arrowWidth);
            var arrowRight = arrowBase - (normal * arrowWidth);

            var arrow = new Polygon
            {
                Fill = edgeStroke,
                Opacity = 0.9,
                Points = new PointCollection([arrowTip, arrowLeft, arrowRight])
            };

            ChildGraphEdgeCanvas.Children.Add(path);
            ChildGraphEdgeCanvas.Children.Add(arrow);
            return;
        }

        ChildGraphEdgeCanvas.Children.Add(path);
    }

    private Button CreateGraphNodeButton(DataflowGraphService.TaskGraphNode node, TaskGroupSurfaceTaskItem item)
    {
        var stateBrush = ResolveTaskStateBrush(item.State);
        var backgroundBrush = item.IsEnabled
            ? ResolveThemeBrush("GraphNodeBackgroundBrush", Color.FromRgb(0x1F, 0x2A, 0x35))
            : ResolveThemeBrush("GraphNodeDisabledBackgroundBrush", Color.FromRgb(0x20, 0x22, 0x24));
        var borderBrush = item.State == TaskGroupSurfaceTaskState.Running
            ? new SolidColorBrush(Color.FromRgb(0x40, 0xAE, 0xFF))
            : item.IsEnabled
                ? ResolveThemeBrush("GraphNodeBorderBrush", Color.FromRgb(0x3D, 0x4D, 0x5D))
                : ResolveThemeBrush("GraphNodeDisabledBorderBrush", Color.FromRgb(0x4A, 0x4D, 0x50));

        var titleText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(node.TaskName) ? "(Unnamed Task)" : node.TaskName,
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
            FontWeight = FontWeights.Medium,
            Text = item.StateLabel,
            Foreground = stateBrush
        };

        var contentPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        contentPanel.Children.Add(titleText);
        contentPanel.Children.Add(stateText);

        var nodeBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = item.State == TaskGroupSurfaceTaskState.Running ? new Thickness(2) : new Thickness(1),
            BorderBrush = borderBrush,
            Background = backgroundBrush,
            Padding = new Thickness(12, 10, 12, 10),
            Child = contentPanel
        };

        var selectionBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = item.IsSelected ? new Thickness(3) : new Thickness(0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9C)),
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
            ToolTip = string.IsNullOrWhiteSpace(node.Description) ? node.TaskName : node.Description,
            Tag = node.TaskIndex,
            Content = selectionBorder,
            ContextMenu = CreateTaskContextMenu(item),
            Opacity = item.IsEnabled ? 1.0 : 0.72
        };

        nodeButton.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        nodeButton.Click += async (_, _) => await ExecuteTaskActionAsync(item.TaskIndex, runTask: true);
        nodeButton.ContextMenuOpening += (_, _) => _actions.SelectTask(item.TaskIndex);

        return nodeButton;
    }

    private ContextMenu CreateTaskContextMenu(TaskGroupSurfaceTaskItem item)
    {
        var menu = new ContextMenu();

        var runItem = new MenuItem
        {
            Header = "Run",
            IsEnabled = item.IsEnabled
        };
        runItem.Click += async (_, _) => await ExecuteTaskActionAsync(item.TaskIndex, runTask: true);

        var runChainItem = new MenuItem
        {
            Header = "Run Chain (Same Group)",
            IsEnabled = item.IsEnabled
        };
        runChainItem.Click += async (_, _) => await ExecuteChainFromTaskActionAsync(item.TaskIndex);

        var editItem = new MenuItem
        {
            Header = "Edit"
        };
        editItem.Click += (_, _) =>
        {
            _actions.SelectTask(item.TaskIndex);
            _actions.EditTask(item.TaskIndex);
        };

        var toggleEnabledItem = new MenuItem
        {
            Header = item.IsEnabled ? "Disable" : "Enable"
        };
        toggleEnabledItem.Click += (_, _) =>
        {
            _actions.SelectTask(item.TaskIndex);
            _actions.ToggleTaskEnabled(item.TaskIndex);
        };

        var deleteItem = new MenuItem
        {
            Header = "Delete"
        };
        deleteItem.Click += (_, _) =>
        {
            _actions.SelectTask(item.TaskIndex);
            _actions.DeleteTask(item.TaskIndex);
        };

        menu.Items.Add(runItem);
        menu.Items.Add(runChainItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(editItem);
        menu.Items.Add(toggleEnabledItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        return menu;
    }

    private Brush ResolveTaskStateBrush(TaskGroupSurfaceTaskState state)
    {
        return state switch
        {
            TaskGroupSurfaceTaskState.Running => new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            TaskGroupSurfaceTaskState.Succeeded => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            TaskGroupSurfaceTaskState.Failed => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            TaskGroupSurfaceTaskState.Disabled => ResolveThemeBrush("SecondaryTextBrush", Color.FromRgb(0x57, 0x60, 0x6A)),
            _ => ResolveThemeBrush("SecondaryTextBrush", Color.FromRgb(0x57, 0x60, 0x6A))
        };
    }

    private void UpdateToggleViewModeLabel()
    {
        ChildToggleViewModeGlyphText.Text = _snapshot.IsGraphMode
            ? "\uE8A5"
            : "\uE81E";
        ChildToggleViewModeButton.ToolTip = _snapshot.IsGraphMode
            ? "Switch to Task view"
            : "Switch to Graph view";
    }

    private void SyncTaskSurfaceToViewport()
    {
        var scrollHostWidth = ChildSurfaceScrollViewer.ActualWidth;
        if (double.IsNaN(scrollHostWidth) || double.IsInfinity(scrollHostWidth) || scrollHostWidth <= 0)
        {
            ChildSurfaceRoot.Width = double.NaN;
            ChildSurfaceRoot.MinWidth = 0;
            ChildTaskPanel.Width = double.NaN;
            ChildTaskPanel.MaxWidth = double.PositiveInfinity;
            return;
        }

        var stableWidth = Math.Max(0, scrollHostWidth);

        if (_snapshot.IsGraphMode)
        {
            ChildSurfaceRoot.Width = double.NaN;
            ChildSurfaceRoot.MinWidth = stableWidth;
            ChildTaskPanel.Width = double.NaN;
            ChildTaskPanel.MaxWidth = double.PositiveInfinity;
            ChildSurfaceScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            return;
        }

        ChildSurfaceRoot.MinWidth = 0;
        ChildSurfaceRoot.Width = stableWidth;
        ChildTaskPanel.Width = double.NaN;
        ChildTaskPanel.MaxWidth = stableWidth;
        ChildSurfaceScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
    }

    private static Brush BuildTaskSelectionBorderBrush(Brush? taskCardBackground)
    {
        if (taskCardBackground is not SolidColorBrush solidCardBrush)
        {
            return new SolidColorBrush(Color.FromRgb(0x00, 0x5A, 0x9C));
        }

        var contrastingColor = BuildContrastingSelectionColor(solidCardBrush.Color);
        return new SolidColorBrush(contrastingColor);
    }

    private static Color BuildContrastingSelectionColor(Color cardColor)
    {
        var (hue, _, _) = RgbToHsv(cardColor);
        var isWarmCardHue = hue < 96d || hue >= 330d;
        var hueCandidates = isWarmCardHue
            ? new[] { 208d, 198d, 220d, 188d }
            : new[] { 58d, 66d, 50d, 74d };

        var valueCandidates = new[] { 1.0d, 0.98d, 0.95d };
        const double saturation = 1.0d;
        const double minimumPreferredContrast = 1.75d;

        Color bestCandidate = HsvToRgb(hueCandidates[0], saturation, valueCandidates[0]);
        var bestContrast = GetContrastRatio(cardColor, bestCandidate);

        foreach (var candidateHue in hueCandidates)
        {
            foreach (var candidateValue in valueCandidates)
            {
                var candidate = HsvToRgb(candidateHue, saturation, candidateValue);
                var contrast = GetContrastRatio(cardColor, candidate);

                if (contrast > bestContrast)
                {
                    bestCandidate = candidate;
                    bestContrast = contrast;
                }

                if (contrast >= minimumPreferredContrast)
                {
                    return candidate;
                }
            }
        }

        return bestCandidate;
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta < 0.00001)
        {
            hue = 0;
        }
        else if (Math.Abs(max - r) < 0.00001)
        {
            hue = 60d * (((g - b) / delta) % 6d);
        }
        else if (Math.Abs(max - g) < 0.00001)
        {
            hue = 60d * (((b - r) / delta) + 2d);
        }
        else
        {
            hue = 60d * (((r - g) / delta) + 4d);
        }

        if (hue < 0)
        {
            hue += 360d;
        }

        var saturation = max <= 0 ? 0 : delta / max;
        var value = max;
        return (hue, saturation, value);
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = (hue % 360d + 360d) % 360d;
        saturation = Math.Max(0d, Math.Min(1d, saturation));
        value = Math.Max(0d, Math.Min(1d, value));

        var c = value * saturation;
        var x = c * (1d - Math.Abs(((hue / 60d) % 2d) - 1d));
        var m = value - c;

        (double rPrime, double gPrime, double bPrime) channels = hue switch
        {
            >= 0 and < 60 => (c, x, 0d),
            >= 60 and < 120 => (x, c, 0d),
            >= 120 and < 180 => (0d, c, x),
            >= 180 and < 240 => (0d, x, c),
            >= 240 and < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        var (rPrime, gPrime, bPrime) = channels;

        var r = (byte)Math.Round((rPrime + m) * 255d);
        var g = (byte)Math.Round((gPrime + m) * 255d);
        var b = (byte)Math.Round((bPrime + m) * 255d);
        return Color.FromRgb(r, g, b);
    }

    private static double GetContrastRatio(Color colorA, Color colorB)
    {
        var luminanceA = RelativeLuminance(colorA);
        var luminanceB = RelativeLuminance(colorB);
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var srgb = channel / 255d;
            return srgb <= 0.03928d
                ? srgb / 12.92d
                : Math.Pow((srgb + 0.055d) / 1.055d, 2.4d);
        }

        var r = Linearize(color.R);
        var g = Linearize(color.G);
        var b = Linearize(color.B);

        return (0.2126d * r) + (0.7152d * g) + (0.0722d * b);
    }

    private async Task ExecuteTaskActionAsync(int taskIndex, bool runTask)
    {
        _actions.SelectTask(taskIndex);
        if (!runTask)
        {
            return;
        }

        await _actions.RunTaskAsync(taskIndex);
    }

    private async Task ExecuteChainFromTaskActionAsync(int taskIndex)
    {
        _actions.SelectTask(taskIndex);
        await _actions.RunChainFromTaskAsync(taskIndex);
    }

    private void Session_StateChanged(object? sender, TaskGroupSurfaceSessionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateToggleViewModeLabel);
    }

    private static Brush ResolveThemeBrush(string key, Color fallbackColor)
    {
        if (Application.Current?.Resources[key] is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    private async void ChildRunChainButton_Click(object sender, RoutedEventArgs e)
    {
        await _actions.RunChainAsync();
    }

    private void ChildToggleViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _actions.ToggleViewMode();
    }

    private void ChildCloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TaskGroupChildWindow_Closed(object? sender, EventArgs e)
    {
        _session.StateChanged -= Session_StateChanged;
        Closed -= TaskGroupChildWindow_Closed;
    }
}
