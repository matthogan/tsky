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
}
