using System.Linq;

namespace tsky;

public sealed class RunHistoryService
{
    private readonly List<TaskRunHistoryEntry> _entries;
    private readonly int _maxEntries;
    private readonly Action<List<TaskRunHistoryEntry>> _saveAction;

    public RunHistoryService(
        List<TaskRunHistoryEntry> entries,
        int maxEntries,
        Action<List<TaskRunHistoryEntry>>? saveAction = null)
    {
        _entries = entries;
        _maxEntries = Math.Max(1, maxEntries);
        _saveAction = saveAction ?? TaskRunHistoryStore.Save;
    }

    public IReadOnlyList<TaskRunHistoryEntry> Entries => _entries;

    public TaskRunHistoryEntry StartEntry(
        LauncherTask task,
        bool isReplay,
        string? replayFromRunId)
    {
        var entry = new TaskRunHistoryEntry
        {
            Snapshot = CreateSnapshot(task),
            IsReplay = isReplay,
            ReplayFromRunId = replayFromRunId,
            Outcome = TaskRunOutcome.Running,
            StartedAtUtc = DateTime.UtcNow
        };

        _entries.Add(entry);
        Save();
        return entry;
    }

    public void CompleteEntry(TaskRunHistoryEntry entry, TaskRunOutcome outcome, int? exitCode)
    {
        entry.CompletedAtUtc = DateTime.UtcNow;
        entry.ExitCode = exitCode;
        entry.Outcome = outcome;
        Save();
    }

    public TaskRunHistoryEntry? GetLastSnapshotEntry(LauncherTask task)
    {
        var taskName = task.Name.Trim();
        var groupName = NormalizeGroupName(task.Group);

        return _entries
            .Where(entry => string.Equals(entry.Snapshot.Name, taskName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Snapshot.Group, groupName, StringComparison.OrdinalIgnoreCase)
                && entry.Outcome != TaskRunOutcome.Running)
            .OrderByDescending(entry => entry.StartedAtUtc)
            .FirstOrDefault();
    }

    public static LauncherTask ToTask(TaskRunSnapshot snapshot)
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
            RequiresElevation = snapshot.RequiresElevation,
            IgnoreExitCode = snapshot.IgnoreExitCode,
            TimeoutSeconds = snapshot.TimeoutSeconds,
            KillProcessTreeOnCancel = snapshot.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = snapshot.MaxCapturedOutputChars,
            Env = new Dictionary<string, string>(snapshot.Env, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void TrimToRetentionLimit()
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        var trimmed = _entries
            .OrderByDescending(entry => entry.StartedAtUtc)
            .Take(_maxEntries)
            .OrderBy(entry => entry.StartedAtUtc)
            .ToList();

        _entries.Clear();
        _entries.AddRange(trimmed);
    }

    private void Save()
    {
        TrimToRetentionLimit();
        _saveAction(_entries);
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
            RequiresElevation = task.RequiresElevation,
            IgnoreExitCode = task.IgnoreExitCode,
            TimeoutSeconds = task.TimeoutSeconds,
            KillProcessTreeOnCancel = task.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = task.MaxCapturedOutputChars,
            Env = new Dictionary<string, string>(task.Env, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string NormalizeGroupName(string? groupName)
    {
        return TaskIdentity.NormalizeGroupName(groupName);
    }
}
