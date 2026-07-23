using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsky;

public enum TaskRunOutcome
{
    Running,
    Started,
    Succeeded,
    FailedNonZeroExit,
    IgnoredNonZeroExit,
    LaunchFailed,
    TimedOut,
    Cancelled
}

public sealed class TaskRunSnapshot
{
    public string Name { get; set; } = "Unnamed Task";

    public string Group { get; set; } = "General";

    public string? Description { get; set; }

    public string CommandLine { get; set; } = string.Empty;

    public string? ResultEvaluatorScript { get; set; }

    public string Runner { get; set; } = "exe";

    public Dictionary<string, string> Env { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool WaitForExit { get; set; } = true;

    public bool RequiresElevation { get; set; }

    public bool IgnoreExitCode { get; set; }

    public int TimeoutSeconds { get; set; } = 600;

    public bool KillProcessTreeOnCancel { get; set; } = true;

    public int MaxCapturedOutputChars { get; set; } = 65536;
}

public sealed class TaskRunHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public int? ExitCode { get; set; }

    public bool IsReplay { get; set; }

    public string? ReplayFromRunId { get; set; }

    public TaskRunOutcome Outcome { get; set; } = TaskRunOutcome.Running;

    public TaskRunSnapshot Snapshot { get; set; } = new();
}

public static class TaskRunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "run-history.json");

    public static List<TaskRunHistoryEntry> Load()
    {
        return JsonFileStorage.LoadOrDefault<List<TaskRunHistoryEntry>>(HistoryFilePath, JsonOptions);
    }

    public static void Save(List<TaskRunHistoryEntry> entries)
    {
        JsonFileStorage.Save(HistoryFilePath, entries, JsonOptions);
    }
}
