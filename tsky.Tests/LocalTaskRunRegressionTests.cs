using System;
using System.IO;
using System.Threading.Tasks;

namespace tsky.Tests;

public sealed class LocalTaskRunRegressionTests
{
    [Fact]
    public async Task ExecuteAsync_LocalCmdSuccess_CapturesOutputAndSucceeds()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "Local cmd success",
            Group = "Regression",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo local-ok"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.True(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("local-ok", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LocalCmdNonZeroExit_WithoutIgnore_Fails()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "Local cmd fail",
            Group = "Regression",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "exit /b 5",
            IgnoreExitCode = false
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.FailedNonZeroExit, result.Outcome);
        Assert.Equal(5, result.ExitCode);
        Assert.False(result.IgnoreExitCodeApplied);
    }

    [Fact]
    public async Task ExecuteAsync_LocalCmdNonZeroExit_WithIgnore_SucceedsAsIgnored()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "Local cmd ignored fail",
            Group = "Regression",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "exit /b 7",
            IgnoreExitCode = true
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.True(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.IgnoredNonZeroExit, result.Outcome);
        Assert.Equal(7, result.ExitCode);
        Assert.True(result.IgnoreExitCodeApplied);
    }

    [Fact]
    public async Task ExecuteAsync_LocalCmdFireAndForget_ReturnsStartedWithoutWait()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "Local cmd async",
            Group = "Regression",
            Runner = "cmd",
            WaitForExit = false,
            CommandLine = "echo async-local"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.False(result.WaitedForExit);
        Assert.True(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Started, result.Outcome);
        Assert.False(result.CanCaptureOutput);
        Assert.Contains("not wait for process exit", result.OutputCaptureDisabledReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LocalCmdTimeout_ReturnsTimedOut()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "Local cmd timeout",
            Group = "Regression",
            Runner = "cmd",
            WaitForExit = true,
            TimeoutSeconds = 1,
            KillProcessTreeOnCancel = true,
            CommandLine = "ping 127.0.0.1 -n 6 >nul"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.TimedOut, result.Outcome);
        Assert.Contains("timed out", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRunCmd()
    {
        var candidate = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return File.Exists(candidate);
    }
}
