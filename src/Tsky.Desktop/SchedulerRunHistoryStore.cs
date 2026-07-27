using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsky;

public sealed class SchedulerRunHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ScheduledItemId { get; set; } = string.Empty;

    public string ScheduledItemDisplayName { get; set; } = "Scheduled Item";

    public ScheduledTargetKind TargetKind { get; set; } = ScheduledTargetKind.TaskSnapshot;

    public DateTime OccurrenceUtc { get; set; } = DateTime.UtcNow;

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public TaskRunOutcome Outcome { get; set; } = TaskRunOutcome.Running;

    public int? ExitCode { get; set; }

    public string? Message { get; set; }

    public int SnapshotTaskCount { get; set; }
}

public static class SchedulerRunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "scheduler-run-history.json");

    public static List<SchedulerRunHistoryEntry> Load()
    {
        return JsonFileStorage.LoadOrDefault<List<SchedulerRunHistoryEntry>>(HistoryFilePath, JsonOptions);
    }

    public static void Save(List<SchedulerRunHistoryEntry> entries)
    {
        JsonFileStorage.Save(HistoryFilePath, entries, JsonOptions);
    }
}

public sealed class SchedulerRunHistoryService
{
    private readonly List<SchedulerRunHistoryEntry> _entries;
    private readonly int _maxEntries;
    private readonly Action<List<SchedulerRunHistoryEntry>> _saveAction;

    public SchedulerRunHistoryService(
        List<SchedulerRunHistoryEntry> entries,
        int maxEntries,
        Action<List<SchedulerRunHistoryEntry>>? saveAction = null)
    {
        _entries = entries;
        _maxEntries = Math.Max(1, maxEntries);
        _saveAction = saveAction ?? SchedulerRunHistoryStore.Save;
    }

    public IReadOnlyList<SchedulerRunHistoryEntry> Entries => _entries;

    public SchedulerRunHistoryEntry StartEntry(ScheduledItem item, DateTime occurrenceUtc)
    {
        var taskCount = item.TargetKind == ScheduledTargetKind.GroupChainSnapshot
            ? item.ChainSnapshot.Count
            : 1;

        var entry = new SchedulerRunHistoryEntry
        {
            ScheduledItemId = item.Id,
            ScheduledItemDisplayName = item.DisplayName,
            TargetKind = item.TargetKind,
            OccurrenceUtc = occurrenceUtc,
            StartedAtUtc = DateTime.UtcNow,
            Outcome = TaskRunOutcome.Running,
            SnapshotTaskCount = taskCount
        };

        _entries.Add(entry);
        Save();
        return entry;
    }

    public void CompleteEntry(SchedulerRunHistoryEntry entry, TaskRunOutcome outcome, int? exitCode, string? message)
    {
        entry.CompletedAtUtc = DateTime.UtcNow;
        entry.Outcome = outcome;
        entry.ExitCode = exitCode;
        entry.Message = message;
        Save();
    }

    private void Save()
    {
        TrimToRetentionLimit();
        _saveAction(_entries);
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
}
