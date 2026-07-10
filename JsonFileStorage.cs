using System.IO;
using System.Text.Json;

namespace tsky;

internal static class JsonFileStorage
{
    public static T LoadOrDefault<T>(string path, JsonSerializerOptions options) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, options) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions options)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var json = JsonSerializer.Serialize(value, options);
        File.WriteAllText(path, json);
    }
}
