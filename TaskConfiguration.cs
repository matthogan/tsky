using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public static TaskConfiguration Load(string path)
    {
        return JsonFileStorage.LoadOrDefault<TaskConfiguration>(path, JsonOptions);
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
            var config = JsonSerializer.Deserialize<TaskConfiguration>(rawText, JsonOptions);
            return config is not null && config.Tasks is not null;
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
        JsonFileStorage.Save(path, config, JsonOptions);
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
}

public sealed class LauncherTask
{
    public string Name { get; set; } = "Unnamed Task";

    public string? Description { get; set; }

    public string CommandLine { get; set; } = string.Empty;

    public string Runner { get; set; } = "exe";

    public Dictionary<string, string> Env { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;
}
