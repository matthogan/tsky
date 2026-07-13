using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace tsky;

public sealed class TaskConfiguration
{
    public const int CurrentSchemaVersion = 1;

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
        if (!File.Exists(path))
        {
            return new TaskConfiguration();
        }

        var rawText = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new TaskConfiguration();
        }

        if (!TryValidateSchema(rawText, out var schemaError))
        {
            throw new InvalidDataException($"Configuration schema validation failed: {schemaError}");
        }

        var fileModel = JsonSerializer.Deserialize<TaskConfigurationFileModel>(rawText, JsonOptions)
            ?? new TaskConfigurationFileModel();
        return FromFileModel(fileModel);
    }

    public static TaskConfiguration LoadMerged(string currentDirectoryPath, string userProfilePath)
    {
        var currentDirectoryConfig = LoadIfExists(currentDirectoryPath);
        var userProfileConfig = LoadIfExists(userProfilePath);
        return MergeConfigurations(currentDirectoryConfig, userProfileConfig);
    }

    public static TaskConfiguration MergeWithOverlay(TaskConfiguration baseConfig, TaskConfiguration overlayConfig)
    {
        return MergeConfigurations(baseConfig, overlayConfig);
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
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[TaskConfiguration] EnsureValidFileOrReset could not read '{path}'");
            // If the file cannot be read, leave it untouched and let normal load behavior handle fallback.
            return;
        }

        if (string.IsNullOrWhiteSpace(rawText) || !TryValidateSchema(rawText, out _))
        {
            BackupExistingFile(path);
            Save(path, new TaskConfiguration());
        }
    }

    public static bool TryValidateSchema(string rawText, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = "Configuration JSON is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawText);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Root JSON value must be an object.";
                return false;
            }

            var root = document.RootElement;

            if (!TryValidateSchemaVersion(root, out error)
                || !TryValidateArrayProperty(root, "tasks", out error)
                || !TryValidateArrayProperty(root, "groups", out error)
                || !TryValidateArrayProperty(root, "rituals", out error)
                || !TryValidateTasks(root, out error)
                || !TryValidateGroups(root, out error)
                || !TryValidateRituals(root, out error))
            {
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            DebugTraceLog.Write(ex, "[TaskConfiguration] TryValidateSchema failed");
            error = ex.Message;
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
            DebugTraceLog.Write(ex, "[TaskConfiguration] TryDeserializeTask failed");
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
            SchemaVersion = CurrentSchemaVersion,
            Groups = groups,
            Tasks = null,
            Rituals = config.Rituals
        };
    }

    private static bool TryValidateSchemaVersion(JsonElement root, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "schemaVersion", out var schemaVersionElement))
        {
            // Missing schemaVersion is supported for legacy files.
            return true;
        }

        if (schemaVersionElement.ValueKind != JsonValueKind.Number || !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            error = "schemaVersion must be an integer.";
            return false;
        }

        if (schemaVersion < 1)
        {
            error = "schemaVersion must be greater than or equal to 1.";
            return false;
        }

        if (schemaVersion > CurrentSchemaVersion)
        {
            error = $"schemaVersion {schemaVersion} is not supported by this build (max supported: {CurrentSchemaVersion}).";
            return false;
        }

        return true;
    }

    private static bool TryValidateArrayProperty(JsonElement root, string propertyName, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = $"{propertyName} must be an array when provided.";
            return false;
        }

        return true;
    }

    private static bool TryValidateTasks(JsonElement root, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "tasks", out var tasksElement)
            || tasksElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        foreach (var task in tasksElement.EnumerateArray())
        {
            if (!TryValidateTaskObject(task, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateGroups(JsonElement root, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "groups", out var groupsElement)
            || groupsElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        foreach (var group in groupsElement.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                error = "Each item in groups must be an object.";
                return false;
            }

            if (TryGetPropertyIgnoreCase(group, "name", out var nameElement)
                && nameElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                error = "groups[].name must be a string when provided.";
                return false;
            }

            if (TryGetPropertyIgnoreCase(group, "tasks", out var groupTasks)
                && groupTasks.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                error = "groups[].tasks must be an array when provided.";
                return false;
            }

            if (group.TryGetProperty("tasks", out var tasksElement) && tasksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var task in tasksElement.EnumerateArray())
                {
                    if (!TryValidateTaskObject(task, out error))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool TryValidateTaskObject(JsonElement task, out string error)
    {
        error = string.Empty;

        if (task.ValueKind != JsonValueKind.Object)
        {
            error = "Each task must be an object.";
            return false;
        }

        if (!TryValidateStringProperty(task, "name", out error)
            || !TryValidateStringProperty(task, "description", out error, allowNull: true)
            || !TryValidateStringProperty(task, "group", out error, allowNull: true)
            || !TryValidateStringProperty(task, "commandLine", out error)
            || !TryValidateStringProperty(task, "runner", out error)
            || !TryValidateStringProperty(task, "workingDirectory", out error)
            || !TryValidateStringProperty(task, "importedFromFile", out error, allowNull: true)
            || !TryValidateStringProperty(task, "importedAtUtc", out error, allowNull: true)
            || !TryValidateBooleanProperty(task, "waitForExit", out error)
            || !TryValidateBooleanProperty(task, "requiresElevation", out error)
            || !TryValidateBooleanProperty(task, "ignoreExitCode", out error)
            || !TryValidateBooleanProperty(task, "isEnabled", out error)
            || !TryValidateBooleanProperty(task, "killProcessTreeOnCancel", out error)
            || !TryValidateIntegerProperty(task, "timeoutSeconds", out error)
            || !TryValidateIntegerProperty(task, "maxCapturedOutputChars", out error)
            || !TryValidateStringDictionary(task, "env", out error)
            || !TryValidateStringDictionary(task, "inputs", out error)
            || !TryValidateStringDictionary(task, "inputBindings", out error)
            || !TryValidateStringDictionary(task, "outputs", out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateRituals(JsonElement root, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(root, "rituals", out var ritualsElement)
            || ritualsElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        foreach (var ritual in ritualsElement.EnumerateArray())
        {
            if (ritual.ValueKind != JsonValueKind.Object)
            {
                error = "Each item in rituals must be an object.";
                return false;
            }

            if (!TryValidateStringProperty(ritual, "name", out error)
                || !TryValidateStringProperty(ritual, "description", out error, allowNull: true))
            {
                return false;
            }

            if (TryGetPropertyIgnoreCase(ritual, "steps", out var stepsElement)
                && stepsElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                error = "rituals[].steps must be an array when provided.";
                return false;
            }

            if (stepsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in stepsElement.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object)
                    {
                        error = "Each ritual step must be an object.";
                        return false;
                    }

                    if (!TryValidateStringProperty(step, "taskName", out error)
                        || !TryValidateStringProperty(step, "group", out error, allowNull: true)
                        || !TryValidateIntegerProperty(step, "delayMs", out error)
                        || !TryValidateBooleanProperty(step, "continueOnError", out error)
                        || !TryValidateBooleanProperty(step, "waitForExit", out error, allowNull: true))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool TryValidateStringProperty(JsonElement obj, string propertyName, out string error, bool allowNull = false)
    {
        error = string.Empty;
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (allowNull)
            {
                return true;
            }

            error = $"{propertyName} cannot be null.";
            return false;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"{propertyName} must be a string.";
            return false;
        }

        return true;
    }

    private static bool TryValidateBooleanProperty(JsonElement obj, string propertyName, out string error, bool allowNull = false)
    {
        error = string.Empty;
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (allowNull)
            {
                return true;
            }

            error = $"{propertyName} cannot be null.";
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"{propertyName} must be a boolean.";
            return false;
        }

        return true;
    }

    private static bool TryValidateIntegerProperty(JsonElement obj, string propertyName, out string error)
    {
        error = string.Empty;
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))
        {
            error = $"{propertyName} must be an integer.";
            return false;
        }

        return true;
    }

    private static bool TryValidateStringDictionary(JsonElement obj, string propertyName, out string error)
    {
        error = string.Empty;
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"{propertyName} must be an object with string values.";
            return false;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"{propertyName}.{property.Name} must be a string value.";
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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
        if (baseTasks.Count > 0 && overlayTasks.Count > 0)
        {
            var baseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseTask in baseTasks)
            {
                baseKeys.Add(BuildTaskIdentityKey(baseTask.Group, baseTask.Name));
            }

            var overlayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var overlayTask in overlayTasks)
            {
                overlayKeys.Add(BuildTaskIdentityKey(overlayTask.Group, overlayTask.Name));
            }

            // If overlay covers all base task identities, preserve overlay ordering as the source of truth.
            if (baseKeys.All(overlayKeys.Contains))
            {
                return BuildUniqueTasksByOrder(overlayTasks);
            }
        }

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

    private static List<LauncherTask> BuildUniqueTasksByOrder(IReadOnlyList<LauncherTask> source)
    {
        var ordered = new List<LauncherTask>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < source.Count; i++)
        {
            var task = source[i];
            var key = BuildTaskIdentityKey(task.Group, task.Name);

            if (indexByKey.TryGetValue(key, out var existingIndex))
            {
                ordered[existingIndex] = task;
                continue;
            }

            indexByKey[key] = ordered.Count;
            ordered.Add(task);
        }

        return ordered;
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
    public int? SchemaVersion { get; set; }

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

    public bool IgnoreExitCode { get; set; }

    public int TimeoutSeconds { get; set; } = 600;

    public bool KillProcessTreeOnCancel { get; set; } = true;

    public int MaxCapturedOutputChars { get; set; } = 65536;

    public bool IsEnabled { get; set; } = true;

    public string? ImportedFromFile { get; set; }

    public string? ImportedAtUtc { get; set; }

    public Dictionary<string, string> Inputs { get; set; } = [];

    public Dictionary<string, string> InputBindings { get; set; } = [];

    public Dictionary<string, string> Outputs { get; set; } = [];
}
