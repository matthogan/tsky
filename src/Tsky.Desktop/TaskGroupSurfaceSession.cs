using System.Collections.Generic;

namespace tsky;

internal sealed class TaskGroupSurfaceSession
{
    public string? GroupName { get; private set; }

    public bool IsGraphMode { get; private set; }

    public int? SelectedTaskIndex { get; private set; }

    public long VersionStamp { get; private set; }

    public event EventHandler<TaskGroupSurfaceSessionChangedEventArgs>? StateChanged;

    public bool Update(string? groupName, bool isGraphMode, int? selectedTaskIndex, string origin)
    {
        var normalizedGroupName = NormalizeGroupName(groupName);
        var normalizedSelectedTaskIndex = selectedTaskIndex is >= 0 ? selectedTaskIndex : null;

        var changed = !string.Equals(GroupName, normalizedGroupName, StringComparison.OrdinalIgnoreCase)
            || IsGraphMode != isGraphMode
            || SelectedTaskIndex != normalizedSelectedTaskIndex;

        if (!changed)
        {
            return false;
        }

        GroupName = normalizedGroupName;
        IsGraphMode = isGraphMode;
        SelectedTaskIndex = normalizedSelectedTaskIndex;
        VersionStamp++;

        StateChanged?.Invoke(this, new TaskGroupSurfaceSessionChangedEventArgs(origin, VersionStamp));
        return true;
    }

    private static string? NormalizeGroupName(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return null;
        }

        return groupName.Trim();
    }
}

internal sealed class TaskGroupSurfaceSessionChangedEventArgs : EventArgs
{
    public TaskGroupSurfaceSessionChangedEventArgs(string origin, long versionStamp)
    {
        Origin = string.IsNullOrWhiteSpace(origin) ? "System" : origin.Trim();
        VersionStamp = versionStamp;
    }

    public string Origin { get; }

    public long VersionStamp { get; }
}

internal enum TaskGroupSurfaceTaskState
{
    Ready,
    Running,
    Succeeded,
    Failed,
    Disabled
}

internal sealed record TaskGroupSurfaceTaskItem(
    int TaskIndex,
    int DisplayOrder,
    string Name,
    string Description,
    IReadOnlyList<string> InputPortNames,
    IReadOnlyList<string> OutputPortNames,
    bool IsEnabled,
    bool IsSelected,
    TaskGroupSurfaceTaskState State,
    string StateLabel,
    bool HasVariableUsage,
    string VariableUsageToolTip,
    bool HasResultEvaluatorScript,
    bool HasImportedSource,
    string ImportedSourceToolTip,
    bool RequiresElevation);

internal sealed class TaskGroupSurfaceSnapshot
{
    public static readonly TaskGroupSurfaceSnapshot Empty = new(
        groupName: null,
        groupMetaText: string.Empty,
        emptyStateText: "No group selected.",
        isGraphMode: false,
        selectedTaskIndex: null,
        allTasks: [],
        visibleTaskIndexes: [],
        tasks: []);

    public TaskGroupSurfaceSnapshot(
        string? groupName,
        string groupMetaText,
        string emptyStateText,
        bool isGraphMode,
        int? selectedTaskIndex,
        IReadOnlyList<LauncherTask> allTasks,
        IReadOnlyList<int> visibleTaskIndexes,
        IReadOnlyList<TaskGroupSurfaceTaskItem> tasks)
    {
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        GroupMetaText = groupMetaText ?? string.Empty;
        EmptyStateText = emptyStateText ?? string.Empty;
        IsGraphMode = isGraphMode;
        SelectedTaskIndex = selectedTaskIndex;
        AllTasks = allTasks;
        VisibleTaskIndexes = visibleTaskIndexes;
        Tasks = tasks;
    }

    public string? GroupName { get; }

    public string GroupMetaText { get; }

    public string EmptyStateText { get; }

    public bool IsGraphMode { get; }

    public int? SelectedTaskIndex { get; }

    public IReadOnlyList<LauncherTask> AllTasks { get; }

    public IReadOnlyList<int> VisibleTaskIndexes { get; }

    public IReadOnlyList<TaskGroupSurfaceTaskItem> Tasks { get; }

    public bool HasValidGroup => !string.IsNullOrWhiteSpace(GroupName);
}

internal sealed class TaskGroupChildWindowActions
{
    public required Func<Task> RunChainAsync { get; init; }

    public required Action ToggleViewMode { get; init; }

    public required Action<int> SelectTask { get; init; }

    public required Func<int, Task> RunTaskAsync { get; init; }

    public required Func<int, Task> RunChainFromTaskAsync { get; init; }

    public required Action<int> EditTask { get; init; }

    public required Action<int> DeleteTask { get; init; }

    public required Action<int> ToggleTaskEnabled { get; init; }
}
