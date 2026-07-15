using System.IO;
using System.Text.Json;

namespace tsky.Tests;

public sealed class TaskConfigurationSchemaTests
{
    [Fact]
    public void Save_WritesCurrentSchemaVersion()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-schema-save-{Guid.NewGuid():N}.json");

        try
        {
            TaskConfiguration.Save(tempPath, new TaskConfiguration
            {
                Tasks =
                [
                    new LauncherTask
                    {
                        Name = "Example",
                        Group = "General",
                        Runner = "cmd",
                        CommandLine = "echo hi"
                    }
                ]
            });

            var json = File.ReadAllText(tempPath);
            using var document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion));
            Assert.Equal(TaskConfiguration.CurrentSchemaVersion, schemaVersion.GetInt32());
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Load_AllowsLegacyFileWithoutSchemaVersion()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-schema-legacy-{Guid.NewGuid():N}.json");

        try
        {
            var json = """
            {
              "groups": [
                {
                  "name": "General",
                  "tasks": [
                    {
                      "name": "Legacy Task",
                      "commandLine": "echo legacy",
                      "runner": "cmd"
                    }
                  ]
                }
              ]
            }
            """;

            File.WriteAllText(tempPath, json);

            var config = TaskConfiguration.Load(tempPath);

            Assert.Single(config.Tasks);
            Assert.Equal("Legacy Task", config.Tasks[0].Name);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Load_RejectsUnsupportedSchemaVersion()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-schema-version-{Guid.NewGuid():N}.json");

        try
        {
            var json = """
            {
              "schemaVersion": 999,
              "groups": []
            }
            """;

            File.WriteAllText(tempPath, json);

            var error = Assert.Throws<InvalidDataException>(() => TaskConfiguration.Load(tempPath));
            Assert.Contains("schemaVersion", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void TryValidateSchema_FailsForInvalidDictionaryValueType()
    {
        var json = """
        {
          "schemaVersion": 1,
          "groups": [
            {
              "name": "General",
              "tasks": [
                {
                  "name": "Bad Env",
                  "commandLine": "echo bad",
                  "runner": "cmd",
                  "env": {
                    "A": 123
                  }
                }
              ]
            }
          ]
        }
        """;

        var valid = TaskConfiguration.TryValidateSchema(json, out var error);

        Assert.False(valid);
        Assert.Contains("env", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_DoesNotPersistUnknownLegacyProperty_InTasksJson()
    {
      var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-schema-legacy-save-{Guid.NewGuid():N}.json");

        try
        {
            var json = """
            {
              "schemaVersion": 1,
              "groups": [
                {
                  "name": "General",
                  "tasks": [
                    {
                      "name": "Example",
                      "commandLine": "echo hi",
                      "runner": "cmd"
                    }
                  ]
                }
              ],
              "legacyItems": [
                {
                  "name": "Legacy Item",
                  "steps": [
                    {
                      "taskName": "Example"
                    }
                  ]
                }
              ]
            }
            """;

            File.WriteAllText(tempPath, json);
            var loaded = TaskConfiguration.Load(tempPath);
            TaskConfiguration.Save(tempPath, loaded);

            var persisted = File.ReadAllText(tempPath);
            using var document = JsonDocument.Parse(persisted);

            Assert.True(document.RootElement.TryGetProperty("groups", out _));
            Assert.False(document.RootElement.TryGetProperty("legacyItems", out _));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
