namespace tsky.Tests;

public sealed class TaskVariableServiceTests
{
    [Fact]
    public void ApplyGroupVariables_SubstitutesAcrossTaskFields_AndTracksUnresolved()
    {
        var task = new LauncherTask
        {
            Name = "Build ${projectName}",
            Description = "${projectName} on ${environment}",
            Group = "${groupName}",
            Runner = "${runner}",
            CommandLine = "dotnet build ${projectName}.sln",
            WorkingDirectory = "C:\\src\\${projectName}",
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PROJECT"] = "${projectName}",
                ["UNKNOWN"] = "${missingValue}"
            },
            Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input-${environment}"] = "${projectName}"
            },
            InputBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["upstream"] = "${missingBinding}"
            },
            Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bin"] = "${projectName}/bin"
            }
        };

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectName"] = "tsky",
            ["environment"] = "dev",
            ["groupName"] = "Build",
            ["runner"] = "cmd"
        };

        var expanded = TaskVariableService.ApplyGroupVariables(task, variables, out var unresolved);

        Assert.Equal("Build tsky", expanded.Name);
        Assert.Equal("tsky on dev", expanded.Description);
        Assert.Equal("Build", expanded.Group);
        Assert.Equal("cmd", expanded.Runner);
        Assert.Equal("dotnet build tsky.sln", expanded.CommandLine);
        Assert.Equal("C:\\src\\tsky", expanded.WorkingDirectory);
        Assert.Equal("tsky", expanded.Env["PROJECT"]);
        Assert.Equal("${missingValue}", expanded.Env["UNKNOWN"]);
        Assert.Equal("tsky", expanded.Inputs["input-dev"]);
        Assert.Equal("${missingBinding}", expanded.InputBindings["upstream"]);
        Assert.Equal("tsky/bin", expanded.Outputs["bin"]);
        Assert.Contains("missingValue", unresolved);
        Assert.Contains("missingBinding", unresolved);
        Assert.Equal(2, unresolved.Count);
    }

    [Fact]
    public void ApplyGroupVariables_EmptyVariableValue_IsReportedAsUnresolved()
    {
        var task = new LauncherTask
        {
            Name = "Context to Tomcat",
            Group = "PPI",
            Runner = "cmd",
            CommandLine = "ctx2tom.bat ${contextFile}"
        };

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contextFile"] = string.Empty
        };

        var expanded = TaskVariableService.ApplyGroupVariables(task, variables, out var unresolved);

        Assert.Equal("ctx2tom.bat ${contextFile}", expanded.CommandLine);
        Assert.Contains("contextFile", unresolved);
    }

    [Fact]
    public void GetGroupVariableUsages_DeduplicatesAndReportsResolutionStatus()
    {
        var task = new LauncherTask
        {
            Name = "Deploy ${projectName}",
            CommandLine = "deploy.bat ${projectName} ${missingVar} ${emptyVar}",
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["P"] = "${projectName}"
            }
        };

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectName"] = "tsky",
            ["emptyVar"] = ""
        };

        var usages = TaskVariableService.GetGroupVariableUsages(task, variables);

        Assert.Equal(3, usages.Count);

        var project = Assert.Single(usages, usage => usage.Name == "projectName");
        Assert.True(project.IsResolved);
        Assert.Equal("tsky", project.Value);

        var empty = Assert.Single(usages, usage => usage.Name == "emptyVar");
        Assert.True(empty.IsDefined);
        Assert.False(empty.HasValue);

        var missing = Assert.Single(usages, usage => usage.Name == "missingVar");
        Assert.False(missing.IsDefined);
        Assert.False(missing.HasValue);
    }

    [Fact]
    public void ApplyGroupVariables_ExpandsWindowsEnvironmentVariablesInVariableAndInputValues()
    {
        var variableName = $"TSKY_VAR_{Guid.NewGuid():N}";
        const string variableValue = "ENV_OK";
        var previousValue = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, variableValue);

            var task = new LauncherTask
            {
                Name = "Build",
                Group = "General",
                Runner = "cmd",
                CommandLine = "echo ${valueFromGroupVar}",
                Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["inputPath"] = "%" + variableName + "%\\input"
                }
            };

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["valueFromGroupVar"] = "%" + variableName + "%"
            };

            var expanded = TaskVariableService.ApplyGroupVariables(task, variables, out var unresolved);

            Assert.Equal($"echo {variableValue}", expanded.CommandLine);
            Assert.Equal($"{variableValue}\\input", expanded.Inputs["inputPath"]);
            Assert.Empty(unresolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    [Fact]
    public void GetGroupVariableUsages_InputPlaceholderInCommandLine_IsNotReportedAsVariable()
    {
        var task = new LauncherTask
        {
            Name = "Extract Prices",
            Group = "JSON Dataflow Example",
            Runner = "powershell",
            CommandLine = "'${Phone.Data}' | ConvertFrom-Json",
            Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Phone.Data"] = "[]"
            },
            InputBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Phone.Data"] = "port:Phone.Data"
            }
        };

        var usages = TaskVariableService.GetGroupVariableUsages(
            task,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain(usages, usage => usage.Name.Equals("Phone.Data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetGroupVariableUsages_InputPlaceholderInCommandLine_WithMatchingGroupVariable_IsReportedAsVariable()
    {
        var task = new LauncherTask
        {
            Name = "Extract Prices",
            Group = "JSON Dataflow Example",
            Runner = "powershell",
            CommandLine = "'${Phone.Data}' | ConvertFrom-Json",
            Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Phone.Data"] = "[]"
            }
        };

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Phone.Data"] = "override"
        };

        var usages = TaskVariableService.GetGroupVariableUsages(task, variables);

        var usage = Assert.Single(usages, item => item.Name.Equals("Phone.Data", StringComparison.OrdinalIgnoreCase));
        Assert.True(usage.IsDefined);
        Assert.True(usage.HasValue);
        Assert.Equal("override", usage.Value);
    }
}
