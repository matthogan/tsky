using System.Diagnostics;

namespace tsky.Tests;

public sealed class TaskProcessStartInfoFactoryTests
{
    [Theory]
    [InlineData("python", "python")]
    [InlineData("Python", "python")]
    [InlineData("py", "python")]
    [InlineData("PY", "python")]
    public void NormalizeRunner_PythonAliases_AreNormalized(string input, string expected)
    {
        var normalized = TaskProcessStartInfoFactory.NormalizeRunner(input);

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryBuild_PythonRunner_UsesPythonExecutableAndPreservesArguments()
    {
        const string commandLine = "scripts/demo.py --name \"Jane Doe\" --mode fast";

        var task = new LauncherTask
        {
            Name = "Run Script",
            Group = "Tests",
            Runner = "python",
            CommandLine = commandLine
        };

        var success = TaskProcessStartInfoFactory.TryBuild(task, out ProcessStartInfo startInfo, out var error);

        Assert.True(success, error);
        Assert.True(string.Equals(startInfo.FileName, "python.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(startInfo.FileName, "py.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(commandLine, startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void TryBuild_WorkingDirectoryQuotedAbsolutePath_UsesUnquotedPath()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "tsky-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var task = new LauncherTask
            {
                Name = "Quoted working directory",
                Group = "Tests",
                Runner = "cmd",
                CommandLine = "echo hi",
                WorkingDirectory = $"\"{workingDirectory}\""
            };

            var success = TaskProcessStartInfoFactory.TryBuild(task, out ProcessStartInfo startInfo, out var error);

            Assert.True(success, error);
            Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryBuild_WorkingDirectoryQuotedMissingPath_ReturnsDirectPathError()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "tsky-tests", Guid.NewGuid().ToString("N"));
        var task = new LauncherTask
        {
            Name = "Missing quoted working directory",
            Group = "Tests",
            Runner = "cmd",
            CommandLine = "echo hi",
            WorkingDirectory = $"\"{missingDirectory}\""
        };

        var success = TaskProcessStartInfoFactory.TryBuild(task, out _, out var error);

        Assert.False(success);
        Assert.Equal($"Working directory does not exist: {missingDirectory}", error);
    }
}
