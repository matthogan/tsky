using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace tsky;

public sealed class TaskConfiguration
{
    public const int CurrentSchemaVersion = 1;
    private static readonly string AppDataTskyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<LauncherTask> Tasks { get; set; } = [];

    public List<LauncherTaskGroup> Groups { get; set; } = [];

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
                || !TryValidateTasks(root, out error)
                || !TryValidateGroups(root, out error))
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

        Directory.CreateDirectory(AppDataTskyDirectory);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var token = BuildPathToken(path);
        var backupPath = Path.Combine(AppDataTskyDirectory, $"tasks.{token}.{timestamp}.bak");
        File.Copy(path, backupPath, overwrite: false);
    }

    private static string BuildPathToken(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
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
        var groupVariables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var groupTargeting = new Dictionary<string, LauncherTaskGroup>(StringComparer.OrdinalIgnoreCase);

        if (fileModel.Groups is { Count: > 0 })
        {
            foreach (var group in fileModel.Groups)
            {
                var groupName = string.IsNullOrWhiteSpace(group.Name) ? "General" : group.Name.Trim();
                groupVariables[groupName] = NormalizeGroupVariables(group.Variables);
                groupTargeting[groupName] = new LauncherTaskGroup
                {
                    Name = groupName,
                    ExecutionTargetMode = NormalizeTargetMode(group.ExecutionTargetMode),
                    TargetAgentId = string.IsNullOrWhiteSpace(group.TargetAgentId) ? null : group.TargetAgentId.Trim(),
                    TargetPoolId = string.IsNullOrWhiteSpace(group.TargetPoolId) ? null : group.TargetPoolId.Trim()
                };
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

        var normalizedGroups = BuildGroups(tasks, groupVariables, groupTargeting);
        return new TaskConfiguration
        {
            Tasks = tasks,
            Groups = normalizedGroups
        };
    }

    private static TaskConfigurationFileModel ToFileModel(TaskConfiguration config)
    {
        var groupVariables = config.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Name))
            .ToDictionary(
                group => group.Name.Trim(),
                group => NormalizeGroupVariables(group.Variables),
                StringComparer.OrdinalIgnoreCase);

        var groupTargeting = config.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Name))
            .ToDictionary(
                group => group.Name.Trim(),
                group => new LauncherTaskGroup
                {
                    Name = group.Name.Trim(),
                    ExecutionTargetMode = NormalizeTargetMode(group.ExecutionTargetMode),
                    TargetAgentId = string.IsNullOrWhiteSpace(group.TargetAgentId) ? null : group.TargetAgentId.Trim(),
                    TargetPoolId = string.IsNullOrWhiteSpace(group.TargetPoolId) ? null : group.TargetPoolId.Trim()
                },
                StringComparer.OrdinalIgnoreCase);

        var groups = BuildGroups(config.Tasks, groupVariables, groupTargeting);
        return new TaskConfigurationFileModel
        {
            SchemaVersion = CurrentSchemaVersion,
            Groups = groups,
            Tasks = null
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

            if (!TryValidateStringDictionary(group, "variables", out error))
            {
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
            || !TryValidateStringProperty(task, "executionTargetMode", out error, allowNull: true)
            || !TryValidateStringProperty(task, "targetAgentId", out error, allowNull: true)
            || !TryValidateStringProperty(task, "targetPoolId", out error, allowNull: true)
            || !TryValidateStringProperty(task, "resultEvaluatorScript", out error, allowNull: true)
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
            || !TryValidateNonNegativeIntegerProperty(task, "retries", out error)
            || !TryValidateStringDictionary(task, "env", out error)
            || !TryValidateStringDictionary(task, "inputs", out error)
            || !TryValidateStringDictionary(task, "inputBindings", out error)
            || !TryValidateStringDictionary(task, "outputs", out error))
        {
            return false;
        }

        if (!TryValidateCommandLineEnvironmentVariables(task, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateCommandLineEnvironmentVariables(JsonElement task, out string error)
    {
        error = string.Empty;

        if (!TryGetPropertyIgnoreCase(task, "commandLine", out var commandLineElement)
            || commandLineElement.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        var commandLine = commandLineElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return true;
        }

        var environmentOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetPropertyIgnoreCase(task, "env", out var envElement)
            && envElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in envElement.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(pair.Name))
                {
                    environmentOverrides[pair.Name.Trim()] = pair.Value.GetString() ?? string.Empty;
                }
            }
        }

        var missingVariables = TaskProcessStartInfoFactory.GetMissingEnvironmentPlaceholders(commandLine, environmentOverrides.Keys);
        if (missingVariables.Count == 0)
        {
            return true;
        }

        error = $"commandLine references undefined environment variable(s): {string.Join(", ", missingVariables.Select(name => "%" + name + "%"))}.";
        return false;
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

    private static bool TryValidateNonNegativeIntegerProperty(JsonElement obj, string propertyName, out string error)
    {
        if (!TryValidateIntegerProperty(obj, propertyName, out error))
        {
            return false;
        }

        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
        {
            return true;
        }

        if (!value.TryGetInt32(out var integerValue) || integerValue < 0)
        {
            error = $"{propertyName} must be greater than or equal to 0.";
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

    private static List<LauncherTaskGroup> BuildGroups(
        IEnumerable<LauncherTask> tasks,
        IReadOnlyDictionary<string, Dictionary<string, string>>? groupVariables = null,
        IReadOnlyDictionary<string, LauncherTaskGroup>? groupTargeting = null)
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
                    ExecutionTargetMode = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var groupDefaults)
                        ? NormalizeTargetMode(groupDefaults.ExecutionTargetMode)
                        : "local",
                    TargetAgentId = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var targetAgentDefaults)
                        ? targetAgentDefaults.TargetAgentId
                        : null,
                    TargetPoolId = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var targetPoolDefaults)
                        ? targetPoolDefaults.TargetPoolId
                        : null,
                    Tasks = [],
                    Variables = groupVariables is not null && groupVariables.TryGetValue(groupName, out var variables)
                        ? NormalizeGroupVariables(variables)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };

                lookup[groupName] = group;
                grouped.Add(group);
            }

            group.Tasks.Add(task);
        }

        if (groupVariables is not null)
        {
            foreach (var pair in groupVariables)
            {
                var groupName = string.IsNullOrWhiteSpace(pair.Key) ? "General" : pair.Key.Trim();
                if (lookup.ContainsKey(groupName))
                {
                    continue;
                }

                var group = new LauncherTaskGroup
                {
                    Name = groupName,
                    ExecutionTargetMode = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var groupDefaults)
                        ? NormalizeTargetMode(groupDefaults.ExecutionTargetMode)
                        : "local",
                    TargetAgentId = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var targetAgentDefaults)
                        ? targetAgentDefaults.TargetAgentId
                        : null,
                    TargetPoolId = groupTargeting is not null && groupTargeting.TryGetValue(groupName, out var targetPoolDefaults)
                        ? targetPoolDefaults.TargetPoolId
                        : null,
                    Tasks = [],
                    Variables = NormalizeGroupVariables(pair.Value)
                };

                lookup[groupName] = group;
                grouped.Add(group);
            }
        }

        return grouped;
    }

    private static Dictionary<string, string> NormalizeGroupVariables(IReadOnlyDictionary<string, string>? variables)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (variables is null)
        {
            return normalized;
        }

        foreach (var pair in variables)
        {
            var key = pair.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key] = pair.Value ?? string.Empty;
        }

        return normalized;
    }

    private static void NormalizeTask(LauncherTask task, string fallbackGroupName)
    {
        task.Name = TaskIdentity.NormalizeTaskName(task.Name);
        task.Group = string.IsNullOrWhiteSpace(task.Group) ? fallbackGroupName : TaskIdentity.NormalizeGroupName(task.Group);
        task.ExecutionTargetMode = NormalizeTargetMode(task.ExecutionTargetMode);
        task.TargetAgentId = string.IsNullOrWhiteSpace(task.TargetAgentId) ? null : task.TargetAgentId.Trim();
        task.TargetPoolId = string.IsNullOrWhiteSpace(task.TargetPoolId) ? null : task.TargetPoolId.Trim();
        task.Retries = Math.Max(0, task.Retries);
        task.Env ??= [];
        task.Inputs ??= [];
        task.InputBindings ??= [];
        task.Outputs ??= [];
    }

    private static string NormalizeTargetMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "local";
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "local" or "agent" or "pool" or "auto"
            ? normalized
            : "local";
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
        var mergedGroupVariables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        AddGroupVariables(baseConfig.Groups);
        AddGroupVariables(overlayConfig.Groups);

        var mergedGroupTargeting = new Dictionary<string, LauncherTaskGroup>(StringComparer.OrdinalIgnoreCase);
        AddGroupTargeting(baseConfig.Groups);
        AddGroupTargeting(overlayConfig.Groups);

        return new TaskConfiguration
        {
            Tasks = mergedTasks,
            Groups = BuildGroups(mergedTasks, mergedGroupVariables, mergedGroupTargeting)
        };

        void AddGroupVariables(IReadOnlyList<LauncherTaskGroup> groups)
        {
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Name))
                {
                    continue;
                }

                mergedGroupVariables[group.Name.Trim()] = NormalizeGroupVariables(group.Variables);
            }
        }

        void AddGroupTargeting(IReadOnlyList<LauncherTaskGroup> groups)
        {
            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Name))
                {
                    continue;
                }

                mergedGroupTargeting[group.Name.Trim()] = new LauncherTaskGroup
                {
                    Name = group.Name.Trim(),
                    ExecutionTargetMode = NormalizeTargetMode(group.ExecutionTargetMode),
                    TargetAgentId = string.IsNullOrWhiteSpace(group.TargetAgentId) ? null : group.TargetAgentId.Trim(),
                    TargetPoolId = string.IsNullOrWhiteSpace(group.TargetPoolId) ? null : group.TargetPoolId.Trim()
                };
            }
        }
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

    private static string BuildTaskIdentityKey(string? groupName, string? taskName)
    {
        return TaskIdentity.BuildKey(groupName, taskName);
    }

}

public sealed class LauncherTaskGroup
{
    public string Name { get; set; } = "General";

    public string? ExecutionTargetMode { get; set; }

    public string? TargetAgentId { get; set; }

    public string? TargetPoolId { get; set; }

    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<LauncherTask> Tasks { get; set; } = [];
}

public sealed class TaskConfigurationFileModel
{
    public int? SchemaVersion { get; set; }

    public List<LauncherTask>? Tasks { get; set; }

    public List<LauncherTaskGroup>? Groups { get; set; }
}

public sealed class LauncherTask
{
    public string Name { get; set; } = "Unnamed Task";

    public string? Description { get; set; }

    public string? Group { get; set; }

    public string ExecutionTargetMode { get; set; } = "local";

    public string? TargetAgentId { get; set; }

    public string? TargetPoolId { get; set; }

    public string? ResultEvaluatorScript { get; set; }

    public string CommandLine { get; set; } = string.Empty;

    public string Runner { get; set; } = "exe";

    public Dictionary<string, string> Env { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public bool WaitForExit { get; set; } = true;

    public bool RequiresElevation { get; set; }

    public bool IgnoreExitCode { get; set; }

    public int TimeoutSeconds { get; set; } = 600;

    public int Retries { get; set; }

    public bool KillProcessTreeOnCancel { get; set; } = true;

    public int MaxCapturedOutputChars { get; set; } = 65536;

    public bool IsEnabled { get; set; } = true;

    public string? ImportedFromFile { get; set; }

    public string? ImportedAtUtc { get; set; }

    public Dictionary<string, string> Inputs { get; set; } = [];

    public Dictionary<string, string> InputBindings { get; set; } = [];

    public Dictionary<string, string> Outputs { get; set; } = [];
}
