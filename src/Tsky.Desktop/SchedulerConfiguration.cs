using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsky;

public enum ScheduledTargetKind
{
    TaskSnapshot,
    GroupChainSnapshot
}

public sealed class SchedulerConfiguration
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int PollIntervalSeconds { get; set; } = SchedulerTimingService.DefaultPollIntervalSeconds;

    public List<ScheduledItem> Items { get; set; } = [];
}

public sealed class ScheduledItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Scheduled Item";

    public string GroupName { get; set; } = "General";

    public ScheduledTargetKind TargetKind { get; set; } = ScheduledTargetKind.TaskSnapshot;

    public DateTime TriggerAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsRecurring { get; set; }

    public string? RecurrenceExpression { get; set; }

    public int TriggerWindowSeconds { get; set; } = 60;

    public bool IsEnabled { get; set; } = true;

    public bool IsRunning { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastTriggeredAtUtc { get; set; }

    public DateTime? LastCompletedAtUtc { get; set; }

    public string? LastOccurrenceToken { get; set; }

    public DateTime? NextRunAtUtc { get; set; }

    public string? LastRunOutcome { get; set; }

    public string? LastRunMessage { get; set; }

    public string? SourceTaskName { get; set; }

    public LauncherTask? TaskSnapshot { get; set; }

    public List<LauncherTask> ChainSnapshot { get; set; } = [];
}

public static class SchedulerConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SchedulerFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "scheduler.json");

    public static SchedulerConfiguration Load()
    {
        var loaded = JsonFileStorage.LoadOrDefault<SchedulerConfiguration>(SchedulerFilePath, JsonOptions);
        return Normalize(loaded);
    }

    public static void Save(SchedulerConfiguration configuration)
    {
        JsonFileStorage.Save(SchedulerFilePath, Normalize(configuration), JsonOptions);
    }

    private static SchedulerConfiguration Normalize(SchedulerConfiguration configuration)
    {
        configuration ??= new SchedulerConfiguration();
        configuration.SchemaVersion = SchedulerConfiguration.CurrentSchemaVersion;
        configuration.PollIntervalSeconds = SchedulerTimingService.NormalizePollIntervalSeconds(configuration.PollIntervalSeconds);
        configuration.Items ??= [];

        foreach (var item in configuration.Items)
        {
            item.Id = string.IsNullOrWhiteSpace(item.Id)
                ? Guid.NewGuid().ToString("N")
                : item.Id.Trim();
            item.DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                ? "Scheduled Item"
                : item.DisplayName.Trim();
            item.GroupName = TaskIdentity.NormalizeGroupName(item.GroupName);
            item.TriggerWindowSeconds = SchedulerTimingService.NormalizeTriggerWindowSeconds(item.TriggerWindowSeconds);
            item.ChainSnapshot ??= [];

            if (item.TaskSnapshot is not null)
            {
                item.TaskSnapshot = SchedulerSnapshotFactory.CloneAsLocalSnapshot(item.TaskSnapshot);
            }

            if (item.ChainSnapshot.Count > 0)
            {
                item.ChainSnapshot = item.ChainSnapshot
                    .Select(SchedulerSnapshotFactory.CloneAsLocalSnapshot)
                    .ToList();
            }

            if (!item.IsRecurring)
            {
                item.RecurrenceExpression = null;
            }

            item.NextRunAtUtc = SchedulerTimingService.TryResolveNextRunUtc(item, DateTime.UtcNow);
        }

        return configuration;
    }
}

public static class SchedulerSnapshotFactory
{
    public static ScheduledItem CreateTaskItem(LauncherTask sourceTask, DateTime triggerAtUtc)
    {
        var snapshot = CloneAsLocalSnapshot(sourceTask);

        return new ScheduledItem
        {
            DisplayName = snapshot.Name,
            GroupName = TaskIdentity.NormalizeGroupName(snapshot.Group),
            TargetKind = ScheduledTargetKind.TaskSnapshot,
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            SourceTaskName = snapshot.Name,
            TaskSnapshot = snapshot
        };
    }

    public static ScheduledItem CreateGroupChainItem(string groupName, IReadOnlyList<LauncherTask> sourceTasks, DateTime triggerAtUtc)
    {
        var normalizedGroup = TaskIdentity.NormalizeGroupName(groupName);
        var snapshots = sourceTasks
            .Select(CloneAsLocalSnapshot)
            .ToList();

        return new ScheduledItem
        {
            DisplayName = normalizedGroup,
            GroupName = normalizedGroup,
            TargetKind = ScheduledTargetKind.GroupChainSnapshot,
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            ChainSnapshot = snapshots
        };
    }

    public static LauncherTask CloneAsLocalSnapshot(LauncherTask source)
    {
        return new LauncherTask
        {
            Name = source.Name,
            Description = source.Description,
            Group = TaskIdentity.NormalizeGroupName(source.Group),
            ExecutionTargetMode = "local",
            TargetAgentId = null,
            TargetPoolId = null,
            ResultEvaluatorScript = source.ResultEvaluatorScript,
            CommandLine = source.CommandLine,
            Runner = source.Runner,
            Env = CloneStringDictionary(source.Env),
            WorkingDirectory = source.WorkingDirectory,
            WaitForExit = source.WaitForExit,
            RequiresElevation = source.RequiresElevation,
            IgnoreExitCode = source.IgnoreExitCode,
            TimeoutSeconds = source.TimeoutSeconds,
            Retries = source.Retries,
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
}
