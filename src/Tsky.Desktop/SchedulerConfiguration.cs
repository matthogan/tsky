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
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int PollIntervalSeconds { get; set; } = SchedulerTimingService.DefaultPollIntervalSeconds;

    public string DesktopInstanceId { get; set; } = "desktop_" + Guid.NewGuid().ToString("N");


    public Dictionary<string, ulong> AgentOccurrenceCursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ScheduledItem> Items { get; set; } = [];

    public List<DesktopScheduleTombstone> DeletedSchedules { get; set; } = [];
}

public sealed class DesktopScheduleTombstone
{
    public string ScheduleId { get; set; } = string.Empty;
    public ulong Revision { get; set; }
    public string AgentId { get; set; } = string.Empty;
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

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ulong Revision { get; set; } = 1;

    public string ExecutionTargetMode { get; set; } = "local";

    public string? TargetAgentId { get; set; }

    public string? TargetPoolId { get; set; }

    public string? PublishedAgentId { get; set; }

    public string? PendingPreviousAgentId { get; set; }

    public ulong AcceptedRevision { get; set; }

    public string? RemoteDeploymentState { get; set; }

    public string? RemoteDeploymentMessage { get; set; }

    public ulong LastAcknowledgedOccurrenceSequence { get; set; }

    [JsonIgnore]
    public bool IsAgentOwned => !string.Equals(ExecutionTargetMode, "local", StringComparison.OrdinalIgnoreCase);

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
		configuration.DesktopInstanceId = string.IsNullOrWhiteSpace(configuration.DesktopInstanceId)
			? "desktop_" + Guid.NewGuid().ToString("N")
			: configuration.DesktopInstanceId.Trim();
        configuration.Items ??= [];
        configuration.DeletedSchedules ??= [];
		configuration.AgentOccurrenceCursors ??= new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
		configuration.AgentOccurrenceCursors = new Dictionary<string, ulong>(configuration.AgentOccurrenceCursors, StringComparer.OrdinalIgnoreCase);
		configuration.DeletedSchedules = configuration.DeletedSchedules
			.Where(item => !string.IsNullOrWhiteSpace(item.ScheduleId) && item.Revision > 0 && !string.IsNullOrWhiteSpace(item.AgentId))
			.GroupBy(item => item.ScheduleId, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.OrderByDescending(item => item.Revision).First())
			.ToList();

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
			item.Revision = Math.Max(1UL, item.Revision);
			item.ExecutionTargetMode = SchedulerSnapshotFactory.NormalizeTargetMode(item.ExecutionTargetMode);
			item.TargetAgentId = string.IsNullOrWhiteSpace(item.TargetAgentId) ? null : item.TargetAgentId.Trim();
			item.TargetPoolId = string.IsNullOrWhiteSpace(item.TargetPoolId) ? null : item.TargetPoolId.Trim();
			item.PublishedAgentId = string.IsNullOrWhiteSpace(item.PublishedAgentId) ? null : item.PublishedAgentId.Trim();
			item.PendingPreviousAgentId = string.IsNullOrWhiteSpace(item.PendingPreviousAgentId) ? null : item.PendingPreviousAgentId.Trim();
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

			if (!item.IsAgentOwned)
			{
				item.NextRunAtUtc = SchedulerTimingService.TryResolveNextRunUtc(item, DateTime.UtcNow);
			}
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
			ExecutionTargetMode = NormalizeTargetMode(sourceTask.ExecutionTargetMode),
			TargetAgentId = sourceTask.TargetAgentId,
			TargetPoolId = sourceTask.TargetPoolId,
            TaskSnapshot = snapshot
        };
    }

    public static ScheduledItem CreateGroupChainItem(string groupName, IReadOnlyList<LauncherTask> sourceTasks, DateTime triggerAtUtc)
    {
        var normalizedGroup = TaskIdentity.NormalizeGroupName(groupName);
        var snapshots = sourceTasks
            .Select(CloneAsLocalSnapshot)
            .ToList();
		var targetModes = sourceTasks.Select(task => NormalizeTargetMode(task.ExecutionTargetMode)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var targetMode = targetModes.Count == 1 ? targetModes[0] : "local";
		var targetAgentIds = sourceTasks.Select(task => task.TargetAgentId?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var targetPoolIds = sourceTasks.Select(task => task.TargetPoolId?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ScheduledItem
        {
            DisplayName = normalizedGroup,
            GroupName = normalizedGroup,
            TargetKind = ScheduledTargetKind.GroupChainSnapshot,
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
			ExecutionTargetMode = targetMode,
			TargetAgentId = targetAgentIds.Count == 1 ? targetAgentIds[0] : null,
			TargetPoolId = targetPoolIds.Count == 1 ? targetPoolIds[0] : null,
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

	internal static string NormalizeTargetMode(string? value)
	{
		return (value ?? "local").Trim().ToLowerInvariant() switch
		{
			"agent" => "agent",
			"pool" => "pool",
			"auto" => "auto",
			_ => "local"
		};
	}
}
