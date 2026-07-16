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
    public void TryValidateSchema_FailsForMissingCommandLineEnvironmentVariable()
    {
        var variableName = $"TSKY_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, null);

        var json = """
        {
          "schemaVersion": 1,
          "groups": [
            {
              "name": "General",
              "tasks": [
                {
                  "name": "Missing Env Var",
                  "commandLine": "echo %__VAR__%",
                  "runner": "cmd"
                }
              ]
            }
          ]
        }
        """.Replace("__VAR__", variableName, StringComparison.Ordinal);

        var valid = TaskConfiguration.TryValidateSchema(json, out var error);

        Assert.False(valid);
        Assert.Contains(variableName, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateSchema_AllowsCommandLineEnvironmentVariableWhenProvidedInTaskEnv()
    {
        var variableName = $"TSKY_LOCAL_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, null);

        var json = """
        {
          "schemaVersion": 1,
          "groups": [
            {
              "name": "General",
              "tasks": [
                {
                  "name": "Task Env Var",
                  "commandLine": "echo %__VAR__%",
                  "runner": "cmd",
                  "env": {
                    "__VAR__": "task-value"
                  }
                }
              ]
            }
          ]
        }
        """.Replace("__VAR__", variableName, StringComparison.Ordinal);

        var valid = TaskConfiguration.TryValidateSchema(json, out var error);

        Assert.True(valid, error);
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

        [Fact]
        public void PasteIntoGroup_CopyPayload_RewritesGroupAndPersistsToTargetGroup()
        {
          var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-paste-group-{Guid.NewGuid():N}.json");

          try
          {
            var sourceTask = new LauncherTask
            {
              Name = "Build Service",
              Group = "Source Group",
              Runner = "cmd",
              CommandLine = "echo build",
              Description = "original task description",
              WorkingDirectory = "C:\\repo"
            };

            var copiedJson = TaskConfiguration.SerializeTask(sourceTask);
            var canDeserialize = TaskConfiguration.TryDeserializeTask(copiedJson, out var pastedTask, out var parseError);

            Assert.True(canDeserialize, parseError);

            pastedTask.Group = "Target Group";

            var config = new TaskConfiguration
            {
              Tasks = [pastedTask]
            };

            TaskConfiguration.Save(tempPath, config);
            var reloaded = TaskConfiguration.Load(tempPath);

            var persistedTask = Assert.Single(reloaded.Tasks);
            Assert.Equal("Build Service", persistedTask.Name);
            Assert.Equal("Target Group", persistedTask.Group);
            Assert.Equal("cmd", persistedTask.Runner);
            Assert.Equal("echo build", persistedTask.CommandLine);
            Assert.Equal("original task description", persistedTask.Description);
            Assert.Equal("C:\\repo", persistedTask.WorkingDirectory);
            Assert.False(string.Equals("Source Group", persistedTask.Group, StringComparison.OrdinalIgnoreCase));
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
        public void SaveAndLoad_PersistsGroupVariablesAlongsideTasks()
        {
          var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-group-vars-{Guid.NewGuid():N}.json");

          try
          {
            var config = new TaskConfiguration
            {
              Tasks =
              [
                new LauncherTask
                {
                  Name = "Build",
                  Group = "BuildGroup",
                  Runner = "cmd",
                  CommandLine = "echo ${projectName}"
                }
              ],
              Groups =
              [
                new LauncherTaskGroup
                {
                  Name = "BuildGroup",
                  Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                  {
                    ["projectName"] = "tsky",
                    ["configuration"] = "Debug"
                  }
                }
              ]
            };

            TaskConfiguration.Save(tempPath, config);
            var loaded = TaskConfiguration.Load(tempPath);

            var loadedGroup = Assert.Single(loaded.Groups);
            Assert.Equal("BuildGroup", loadedGroup.Name);
            Assert.Equal("tsky", loadedGroup.Variables["projectName"]);
            Assert.Equal("Debug", loadedGroup.Variables["configuration"]);
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
        public void TryValidateSchema_FailsForInvalidGroupVariableValueType()
        {
          var json = """
          {
            "schemaVersion": 1,
            "groups": [
            {
              "name": "BuildGroup",
              "variables": {
              "projectName": 123
              },
              "tasks": [
              {
                "name": "Build",
                "commandLine": "echo hi",
                "runner": "cmd"
              }
              ]
            }
            ]
          }
          """;

          var valid = TaskConfiguration.TryValidateSchema(json, out var error);

          Assert.False(valid);
          Assert.Contains("variables", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SaveAndLoad_PreservesVariableOnlyGroups()
        {
          var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-group-vars-only-{Guid.NewGuid():N}.json");

          try
          {
            var config = new TaskConfiguration
            {
              Tasks = [],
              Groups =
              [
                new LauncherTaskGroup
                {
                  Name = "TemplateGroup",
                  Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                  {
                    ["projectName"] = "starter"
                  },
                  Tasks = []
                }
              ]
            };

            TaskConfiguration.Save(tempPath, config);
            var reloaded = TaskConfiguration.Load(tempPath);

            var group = Assert.Single(reloaded.Groups);
            Assert.Equal("TemplateGroup", group.Name);
            Assert.Equal("starter", group.Variables["projectName"]);
            Assert.Empty(reloaded.Tasks);
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
