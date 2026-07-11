using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace tsky;

public sealed class TaskConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<LauncherTask> Tasks { get; set; } = [];

    public List<LauncherTaskGroup> Groups { get; set; } = [];

    public List<RitualDefinition> Rituals { get; set; } = [];

    public static string GetCurrentDirectoryConfigPath()
    {
        return Path.Combine(Environment.CurrentDirectory, "tasks.json");
    }

    public static string GetUserProfileConfigPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tasks.json");
    }

    public static TaskConfiguration Load(string path)
    {
        var fileModel = JsonFileStorage.LoadOrDefault<TaskConfigurationFileModel>(path, JsonOptions);
        return FromFileModel(fileModel);
    }

    public static TaskConfiguration LoadMerged(string currentDirectoryPath, string userProfilePath)
    {
        var currentDirectoryConfig = LoadIfExists(currentDirectoryPath);
        var userProfileConfig = LoadIfExists(userProfilePath);
        return MergeConfigurations(currentDirectoryConfig, userProfileConfig);
    }

    public static void EnsureValidFileOrReset(string path)
    {
        if (!File.Exists(path))
        {
            Save(path, new TaskConfiguration());
            return;
        }

        string rawText;
        try
        {
            rawText = File.ReadAllText(path);
        }
        catch
        {
            // If the file cannot be read, leave it untouched and let normal load behavior handle fallback.
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText) || !IsValidConfigurationJson(rawText))
        {
            BackupExistingFile(path);
            Save(path, new TaskConfiguration());
        }
    }

    private static bool IsValidConfigurationJson(string rawText)
    {
        try
        {
            var config = JsonSerializer.Deserialize<TaskConfigurationFileModel>(rawText, JsonOptions);
            return config is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void BackupExistingFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{path}.{timestamp}.bak";
        File.Copy(path, backupPath, overwrite: false);
    }

    public static void Save(string path, TaskConfiguration config)
    {
        var fileModel = ToFileModel(config);
        JsonFileStorage.Save(path, fileModel, JsonOptions);
    }

    public static string SerializeTask(LauncherTask task)
    {
        return JsonSerializer.Serialize(task, JsonOptions);
    }

    public static bool TryDeserializeTask(string json, out LauncherTask task, out string error)
    {
        try
        {
            task = JsonSerializer.Deserialize<LauncherTask>(json, JsonOptions) ?? new LauncherTask();
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            task = new LauncherTask();
            error = ex.Message;
            return false;
        }
    }

    private static TaskConfiguration FromFileModel(TaskConfigurationFileModel fileModel)
    {
        var tasks = new List<LauncherTask>();

        if (fileModel.Groups is { Count: > 0 })
        {
            foreach (var group in fileModel.Groups)
            {
                var groupName = string.IsNullOrWhiteSpace(group.Name) ? "General" : group.Name.Trim();
                foreach (var task in group.Tasks ?? [])
                {
                    NormalizeTask(task, groupName);
                    tasks.Add(task);
                }
            }
        }
        else if (fileModel.Tasks is { Count: > 0 })
        {
            foreach (var task in fileModel.Tasks)
            {
                NormalizeTask(task, "General");
                tasks.Add(task);
            }
        }

        tasks = MergeTasks([], tasks);

        var rituals = NormalizeRituals(fileModel.Rituals);

        var normalizedGroups = BuildGroups(tasks);
        return new TaskConfiguration
        {
            Tasks = tasks,
            Groups = normalizedGroups,
            Rituals = rituals
        };
    }

    private static TaskConfigurationFileModel ToFileModel(TaskConfiguration config)
    {
        var groups = BuildGroups(config.Tasks);
        return new TaskConfigurationFileModel
        {
            Groups = groups,
            Tasks = null,
            Rituals = config.Rituals
        };
    }

    private static List<LauncherTaskGroup> BuildGroups(IEnumerable<LauncherTask> tasks)
    {
        var grouped = new List<LauncherTaskGroup>();
        var lookup = new Dictionary<string, LauncherTaskGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            var groupName = string.IsNullOrWhiteSpace(task.Group) ? "General" : task.Group.Trim();

            if (!lookup.TryGetValue(groupName, out var group))
            {
                group = new LauncherTaskGroup
                {
                    Name = groupName,
                    Tasks = []
                };

                lookup[groupName] = group;
                grouped.Add(group);
            }

            group.Tasks.Add(task);
        }

        return grouped;
    }

    private static void NormalizeTask(LauncherTask task, string fallbackGroupName)
    {
        task.Name = TaskIdentity.NormalizeTaskName(task.Name);
        task.Group = string.IsNullOrWhiteSpace(task.Group) ? fallbackGroupName : TaskIdentity.NormalizeGroupName(task.Group);
        task.Env ??= [];
        task.Inputs ??= [];
        task.InputBindings ??= [];
        task.Outputs ??= [];
    }

    private static TaskConfiguration LoadIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new TaskConfiguration();
        }

        try
        {
            return Load(path);
        }
        catch
        {
            return new TaskConfiguration();
        }
    }

    private static TaskConfiguration MergeConfigurations(TaskConfiguration baseConfig, TaskConfiguration overlayConfig)
    {
        var mergedTasks = MergeTasks(baseConfig.Tasks, overlayConfig.Tasks);
        var mergedRituals = MergeRituals(baseConfig.Rituals, overlayConfig.Rituals);

        return new TaskConfiguration
        {
            Tasks = mergedTasks,
            Groups = BuildGroups(mergedTasks),
            Rituals = mergedRituals
        };
    }

    private static List<LauncherTask> MergeTasks(IReadOnlyList<LauncherTask> baseTasks, IReadOnlyList<LauncherTask> overlayTasks)
    {
        var merged = new List<LauncherTask>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AddTasks(baseTasks);
        AddTasks(overlayTasks);

        return merged;

        void AddTasks(IReadOnlyList<LauncherTask> source)
        {
            for (var index = 0; index < source.Count; index++)
            {
                var task = source[index];
                var identityKey = BuildTaskIdentityKey(task.Group, task.Name);

                if (indexByKey.TryGetValue(identityKey, out var existingIndex))
                {
                    merged[existingIndex] = task;
                    continue;
                }

                indexByKey[identityKey] = merged.Count;
                merged.Add(task);
            }
        }
    }

    private static List<RitualDefinition> MergeRituals(IReadOnlyList<RitualDefinition> baseRituals, IReadOnlyList<RitualDefinition> overlayRituals)
    {
        var merged = new List<RitualDefinition>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AddRituals(baseRituals);
        AddRituals(overlayRituals);

        return merged;

        void AddRituals(IReadOnlyList<RitualDefinition> source)
        {
            for (var index = 0; index < source.Count; index++)
            {
                var ritual = source[index];
                if (string.IsNullOrWhiteSpace(ritual.Name))
                {
                    continue;
                }

                var ritualName = ritual.Name.Trim();
                if (indexByName.TryGetValue(ritualName, out var existingIndex))
                {
                    merged[existingIndex] = ritual;
                    continue;
                }

                indexByName[ritualName] = merged.Count;
                merged.Add(ritual);
            }
        }
    }

    private static string BuildTaskIdentityKey(string? groupName, string? taskName)
    {
        return TaskIdentity.BuildKey(groupName, taskName);
    }

    private static List<RitualDefinition> NormalizeRituals(List<RitualDefinition>? rituals)
    {
        if (rituals is null || rituals.Count == 0)
        {
            return [];
        }

        var normalized = new List<RitualDefinition>();

        foreach (var ritual in rituals)
        {
            if (string.IsNullOrWhiteSpace(ritual.Name))
            {
                continue;
            }

            ritual.Name = ritual.Name.Trim();
            ritual.Steps ??= [];

            ritual.Steps = ritual.Steps
                .Where(step => !string.IsNullOrWhiteSpace(step.TaskName))
                .Select(step =>
                {
                    step.TaskName = step.TaskName.Trim();
                    step.Group = string.IsNullOrWhiteSpace(step.Group) ? null : step.Group.Trim();
                    step.DelayMs = Math.Max(0, step.DelayMs);
                    return step;
                })
                .ToList();

            if (ritual.Steps.Count == 0)
            {
                continue;
            }

            normalized.Add(ritual);
        }

        return normalized;
    }
}

public sealed class LauncherTaskGroup
{
    public string Name { get; set; } = "General";

    public List<LauncherTask> Tasks { get; set; } = [];
}

public sealed class TaskConfigurationFileModel
{
    public List<LauncherTask>? Tasks { get; set; }

    public List<LauncherTaskGroup>? Groups { get; set; }

    public List<RitualDefinition>? Rituals { get; set; }
}

public sealed class RitualDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<RitualStepDefinition> Steps { get; set; } = [];
}

public sealed class RitualStepDefinition
{
    public string TaskName { get; set; } = string.Empty;

    public string? Group { get; set; }

    public int DelayMs { get; set; }

    public bool ContinueOnError { get; set; }

    public bool? WaitForExit { get; set; }
}

public sealed class LauncherTask
{
    public string Name { get; set; } = "Unnamed Task";

    public string? Description { get; set; }

    public string? Group { get; set; }

    public string CommandLine { get; set; } = string.Empty;

    public string Runner { get; set; } = "exe";

    public Dictionary<string, string> Env { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool WaitForExit { get; set; } = true;

    public bool RequiresElevation { get; set; }

    public Dictionary<string, string> Inputs { get; set; } = [];

    public Dictionary<string, string> InputBindings { get; set; } = [];

    public Dictionary<string, string> Outputs { get; set; } = [];
}
