namespace tsky;

public static class TaskIdentity
{
    public static string NormalizeGroupName(string? groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? "General" : groupName.Trim();
    }

    public static string NormalizeTaskName(string? taskName)
    {
        return string.IsNullOrWhiteSpace(taskName) ? "Unnamed Task" : taskName.Trim();
    }

    public static string BuildKey(string? groupName, string? taskName)
    {
        return $"{NormalizeGroupName(groupName)}::{NormalizeTaskName(taskName)}";
    }

    public static string BuildKey(LauncherTask task)
    {
        return BuildKey(task.Group, task.Name);
    }
}
