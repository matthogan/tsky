namespace tsky;

public sealed class TaskCatalogService
{
    public bool IsValidTaskIndex(TaskConfiguration configuration, int taskIndex)
    {
        return taskIndex >= 0 && taskIndex < configuration.Tasks.Count;
    }

    public bool TryGetTask(TaskConfiguration configuration, int taskIndex, out LauncherTask task)
    {
        task = new LauncherTask();

        if (!IsValidTaskIndex(configuration, taskIndex))
        {
            return false;
        }

        task = configuration.Tasks[taskIndex];
        return true;
    }

    public bool IsTaskEnabled(LauncherTask task)
    {
        return task.IsEnabled;
    }

    public bool IsTaskDisabledByIdentity(IReadOnlyList<LauncherTask> tasks, string? groupName, string? taskName)
    {
        var normalizedGroup = TaskIdentity.NormalizeGroupName(groupName);
        var normalizedName = TaskIdentity.NormalizeTaskName(taskName);

        var match = tasks.FirstOrDefault(task =>
            string.Equals(TaskIdentity.NormalizeGroupName(task.Group), normalizedGroup, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TaskIdentity.NormalizeTaskName(task.Name), normalizedName, StringComparison.OrdinalIgnoreCase));

        return match is not null && !match.IsEnabled;
    }

    public bool IsGroupDataflowLocked(IReadOnlyList<LauncherTask> tasks, IReadOnlyList<int> visibleTaskIndexes)
    {
        return visibleTaskIndexes.Any(index =>
        {
            if (index < 0 || index >= tasks.Count)
            {
                return false;
            }

            var task = tasks[index];
            return task.InputBindings.Count > 0 || task.Outputs.Count > 0;
        });
    }

    public bool ToggleTaskEnabled(TaskConfiguration configuration, int taskIndex)
    {
        if (!TryGetTask(configuration, taskIndex, out var task))
        {
            return false;
        }

        task.IsEnabled = !task.IsEnabled;
        return true;
    }

    public bool DeleteTask(TaskConfiguration configuration, int taskIndex)
    {
        if (!IsValidTaskIndex(configuration, taskIndex))
        {
            return false;
        }

        configuration.Tasks.RemoveAt(taskIndex);
        return true;
    }

    public bool ReorderVisibleGroupTasks(TaskConfiguration configuration, IReadOnlyList<int> visibleTaskIndexes, int sourceDisplayIndex, int targetDisplayIndex)
    {
        if (sourceDisplayIndex < 0 || sourceDisplayIndex >= visibleTaskIndexes.Count)
        {
            return false;
        }

        if (targetDisplayIndex < 0 || targetDisplayIndex > visibleTaskIndexes.Count)
        {
            return false;
        }

        if (sourceDisplayIndex == targetDisplayIndex || sourceDisplayIndex == targetDisplayIndex - 1)
        {
            return false;
        }

        var groupTasks = visibleTaskIndexes
            .Where(index => index >= 0 && index < configuration.Tasks.Count)
            .Select(index => configuration.Tasks[index])
            .ToList();

        var movedTask = groupTasks[sourceDisplayIndex];
        groupTasks.RemoveAt(sourceDisplayIndex);

        if (sourceDisplayIndex < targetDisplayIndex)
        {
            targetDisplayIndex--;
        }

        targetDisplayIndex = Math.Clamp(targetDisplayIndex, 0, groupTasks.Count);
        groupTasks.Insert(targetDisplayIndex, movedTask);

        for (var i = 0; i < visibleTaskIndexes.Count; i++)
        {
            var index = visibleTaskIndexes[i];
            if (index < 0 || index >= configuration.Tasks.Count)
            {
                continue;
            }

            configuration.Tasks[index] = groupTasks[i];
        }

        return true;
    }
}
