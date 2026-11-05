using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace tsky.Tests;

[Collection("TaskExecutionServiceSeamTests")]
public sealed class TaskExecutionServiceTests
{
    private sealed class TaskExecutionServiceSeamScope : IDisposable
    {
        private readonly Func<Process, bool>? _startOverride;
        private readonly Func<Process, CancellationToken, Task>? _waitOverride;
        private readonly Func<bool>? _elevatedOverride;

        public TaskExecutionServiceSeamScope()
        {
            _startOverride = TaskExecutionService.StartProcessOverride;
            _waitOverride = TaskExecutionService.WaitForExitAsyncOverride;
            _elevatedOverride = TaskExecutionService.IsCurrentProcessElevatedOverride;
        }

        public void Dispose()
        {
            TaskExecutionService.StartProcessOverride = _startOverride;
            TaskExecutionService.WaitForExitAsyncOverride = _waitOverride;
            TaskExecutionService.IsCurrentProcessElevatedOverride = _elevatedOverride;
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidRunner_ReturnsConfigurationError()
    {
        var task = new LauncherTask
        {
            Name = "Invalid runner",
            Group = "Tests",
            Runner = "bash",
            WaitForExit = true,
            CommandLine = "echo hi"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("Runner must be one of: exe, cmd, powershell, python.", result.ConfigurationError);
    }

    [Fact]
    public async Task ExecuteAsync_ElevationPromptCanceled_ReturnsLaunchFailedWithElevationFlag()
    {
        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.IsCurrentProcessElevatedOverride = static () => false;
        TaskExecutionService.StartProcessOverride = _ => throw new Win32Exception(1223, "The operation was canceled by the user.");

        var task = new LauncherTask
        {
            Name = "Elevation prompt cancelled",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            RequiresElevation = true,
            CommandLine = "echo hi"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.True(result.ElevationPromptCanceled);
        Assert.True(result.LaunchedElevated);
        Assert.False(result.CanCaptureOutput);
        Assert.Equal("Elevation was canceled by the user.", result.FailureMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WaitReturnsAfterTimeoutSignal_ReturnsTimedOutWithoutThrowPath()
    {
        if (!CanRunCmd())
        {
            return;
        }

        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.WaitForExitAsyncOverride = static async (_, _) =>
        {
            await Task.Delay(1300, CancellationToken.None);
        };

        var task = new LauncherTask
        {
            Name = "Timeout interleaving",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            TimeoutSeconds = 1,
            CommandLine = "echo done"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.TimedOut, result.Outcome);
        Assert.True(result.ProcessId.HasValue);
        Assert.Contains("timed out", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WaitReturnsAfterCancellationSignal_ReturnsCancelledWithoutThrowPath()
    {
        if (!CanRunCmd())
        {
            return;
        }

        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.WaitForExitAsyncOverride = static async (_, _) =>
        {
            await Task.Delay(500, CancellationToken.None);
        };

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(120));

        var task = new LauncherTask
        {
            Name = "Cancellation interleaving",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            TimeoutSeconds = 30,
            CommandLine = "echo done"
        };

        var result = await TaskExecutionService.ExecuteAsync(task, cancellationToken: cancellation.Token);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Cancelled, result.Outcome);
        Assert.True(result.ProcessId.HasValue);
        Assert.Contains("cancel", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StartThrowsUnexpectedException_ReturnsLaunchFailedWithMessage()
    {
        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.StartProcessOverride = _ => throw new InvalidOperationException("synthetic-start-failure");

        var task = new LauncherTask
        {
            Name = "Unexpected start failure",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo hi"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("synthetic-start-failure", result.FailureMessage);
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForgetSlowStart_DoesNotBlockCallerThread()
    {
        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.StartProcessOverride = process =>
        {
            Thread.Sleep(500);
            process.Start();
            return true;
        };

        var task = new LauncherTask
        {
            Name = "Slow fire-and-forget start",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = false,
            CommandLine = "echo hi"
        };

        var stopwatch = Stopwatch.StartNew();
        var pending = TaskExecutionService.ExecuteAsync(task);
        var synchronousElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        Assert.True(
            synchronousElapsedMilliseconds < 250,
            $"ExecuteAsync should return control quickly for fire-and-forget launches. Elapsed: {synchronousElapsedMilliseconds} ms.");

        var result = await pending;
        Assert.True(result.Started);
        Assert.False(result.WaitedForExit);
        Assert.Equal(TaskExecutionOutcome.Started, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_MissingEnvironmentPlaceholderInCommandLine_ReturnsConfigurationError()
    {
        var variableName = "TSKY_MISSING_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var task = new LauncherTask
        {
            Name = "Missing placeholder",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = $"echo %{variableName}%"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.Contains(variableName, result.ConfigurationError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_EnvironmentVariableNameEmpty_ReturnsConfigurationError()
    {
        var task = new LauncherTask
        {
            Name = "Invalid env",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo hi",
            Env = new Dictionary<string, string>
            {
                ["   "] = "value"
            }
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("Environment variable names cannot be empty.", result.ConfigurationError);
    }

    [Fact]
    public async Task ExecuteAsync_WorkingDirectoryMissing_ReturnsConfigurationError()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "tsky-tests", Guid.NewGuid().ToString("N"));
        var task = new LauncherTask
        {
            Name = "Missing working dir",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = "echo hi",
            WorkingDirectory = missingDirectory
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.Contains("Working directory does not exist", result.ConfigurationError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingDirectory, result.ConfigurationError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_CommandLinePlaceholderProvidedByTaskEnv_IsExpanded()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var variableName = "TSKY_ENV_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var variableValue = "expanded-value";
        var task = new LauncherTask
        {
            Name = "Placeholder expansion",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            CommandLine = $"echo %{variableName}%",
            Env = new Dictionary<string, string>
            {
                [variableName] = variableValue
            }
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.True(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Succeeded, result.Outcome);
        Assert.Contains(variableValue, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_LaunchFailureForMissingExecutable_ReturnsLaunchFailed()
    {
        var missingExecutablePath = Path.Combine(Path.GetTempPath(), "tsky-tests", Guid.NewGuid().ToString("N"), "missing.exe");
        var task = new LauncherTask
        {
            Name = "Missing exe",
            Group = "Tests",
            Runner = "exe",
            WaitForExit = true,
            CommandLine = $"\"{missingExecutablePath}\" --arg"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.LaunchFailed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    [Fact]
    public async Task ExecuteAsync_CancellationTokenCancelled_ReturnsCancelled()
    {
        if (!CanRunCmd())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));

        var task = new LauncherTask
        {
            Name = "Cancelable cmd",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            TimeoutSeconds = 30,
            KillProcessTreeOnCancel = true,
            CommandLine = "ping 127.0.0.1 -n 20 >nul"
        };

        var result = await TaskExecutionService.ExecuteAsync(task, cancellationToken: cancellation.Token);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancel", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_OutputExceedsBudget_IsTruncatedAndFlagged()
    {
        if (!CanRunCmd())
        {
            return;
        }

        var largePayload = new string('a', 5000);
        var maxCaptured = 256;
        var effectiveCaptureLimit = 1024;
        var task = new LauncherTask
        {
            Name = "Large output",
            Group = "Tests",
            Runner = "cmd",
            WaitForExit = true,
            MaxCapturedOutputChars = maxCaptured,
            CommandLine = $"echo {largePayload}"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.True(result.Succeeded);
        Assert.True(result.OutputTruncated);
        Assert.Equal(effectiveCaptureLimit, result.MaxCapturedOutputChars);
        Assert.True(result.StandardOutput.Length <= effectiveCaptureLimit);
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellErrorRecordOnStderr_FailsEvenWithZeroExitCode()
    {
        if (!CanRunPowerShell())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "PowerShell stderr record",
            Group = "Tests",
            Runner = "powershell",
            WaitForExit = true,
            CommandLine = "$ErrorActionPreference='Continue'; Write-Error 'boom'; Write-Output 'done'"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.FailedNonZeroExit, result.Outcome);
        Assert.Contains("PowerShell produced stderr output", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Write-Error", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellSuccessWithNoErrorRecord_Succeeds()
    {
        if (!CanRunPowerShell())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "PowerShell success",
            Group = "Tests",
            Runner = "powershell",
            WaitForExit = true,
            CommandLine = "Write-Output 'ok'"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellPlainStderr_FailsEvenWithZeroExitCode()
    {
        if (!CanRunPowerShell())
        {
            return;
        }

        var task = new LauncherTask
        {
            Name = "PowerShell plain stderr",
            Group = "Tests",
            Runner = "powershell",
            WaitForExit = true,
            CommandLine = "[Console]::Error.WriteLine('plain stderr'); Write-Output 'done'"
        };

        var result = await TaskExecutionService.ExecuteAsync(task);

        Assert.True(result.Started);
        Assert.True(result.WaitedForExit);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal(TaskExecutionOutcome.FailedNonZeroExit, result.Outcome);
        Assert.Contains("plain stderr", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainsPowerShellErrorRecord_CategoryInfoPattern_ReturnsTrue()
    {
        var stderr = "CategoryInfo : NotSpecified: (:) [], RuntimeException";

        var result = InvokeContainsPowerShellErrorRecord(stderr);

        Assert.True(result);
    }

    [Fact]
    public void ContainsPowerShellErrorRecord_CmdletWithLineContext_ReturnsTrue()
    {
        var stderr = "Write-Error : boom" + Environment.NewLine + "Line |" + Environment.NewLine + "| some pipeline context";

        var result = InvokeContainsPowerShellErrorRecord(stderr);

        Assert.True(result);
    }

    [Fact]
    public void ContainsPowerShellErrorRecord_GenericStderr_ReturnsFalse()
    {
        var stderr = "plain stderr output";

        var result = InvokeContainsPowerShellErrorRecord(stderr);

        Assert.False(result);
    }

    [Fact]
    public void ContainsPowerShellErrorRecord_AtLinePattern_ReturnsTrue()
    {
        var stderr = "At line:1 char:1";

        var result = InvokeContainsPowerShellErrorRecord(stderr);

        Assert.True(result);
    }

    [Fact]
    public void AppendCapped_EmptyValue_DoesNotMutateBuffer()
    {
        var builder = new StringBuilder("seed");
        var wasTruncated = false;

        InvokeAppendCapped(builder, string.Empty, 10, ref wasTruncated);

        Assert.Equal("seed", builder.ToString());
        Assert.False(wasTruncated);
    }

    [Fact]
    public void AppendCapped_NoRemainingBudget_SetsTruncated()
    {
        var builder = new StringBuilder("12345");
        var wasTruncated = false;

        InvokeAppendCapped(builder, "abc", 5, ref wasTruncated);

        Assert.Equal("12345", builder.ToString());
        Assert.True(wasTruncated);
    }

    [Fact]
    public void AppendCapped_ValueFits_AppendsWithoutTruncation()
    {
        var builder = new StringBuilder("12");
        var wasTruncated = false;

        InvokeAppendCapped(builder, "abc", 10, ref wasTruncated);

        Assert.Equal("12abc", builder.ToString());
        Assert.False(wasTruncated);
    }

    [Fact]
    public void IsCurrentProcessElevated_OverrideBranch_IsHonored()
    {
        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.IsCurrentProcessElevatedOverride = static () => true;

        var elevated = InvokeIsCurrentProcessElevated();

        Assert.True(elevated);
    }

    [Fact]
    public void ShouldLaunchAsAdministrator_UsesElevationProbe()
    {
        using var seamScope = new TaskExecutionServiceSeamScope();
        TaskExecutionService.IsCurrentProcessElevatedOverride = static () => false;

        var task = new LauncherTask
        {
            RequiresElevation = true
        };

        var shouldLaunch = TaskExecutionService.ShouldLaunchAsAdministrator(task);

        Assert.True(shouldLaunch);
    }

    private static bool CanRunCmd()
    {
        var candidate = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return File.Exists(candidate);
    }

    private static bool CanRunPowerShell()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var exe = string.Empty;
        foreach (var pathEntry in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(pathEntry))
            {
                continue;
            }

            try
            {
                var trimmed = pathEntry.Trim();
                var pwshCandidate = Path.Combine(trimmed, "pwsh.exe");
                if (File.Exists(pwshCandidate))
                {
                    exe = pwshCandidate;
                    break;
                }

                var windowsPowerShellCandidate = Path.Combine(trimmed, "powershell.exe");
                if (File.Exists(windowsPowerShellCandidate))
                {
                    exe = windowsPowerShellCandidate;
                    break;
                }
            }
            catch
            {
                // Ignore malformed path entries.
            }
        }

        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "exit 0" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(3000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool InvokeContainsPowerShellErrorRecord(string standardError)
    {
        var method = typeof(TaskExecutionService).GetMethod("ContainsPowerShellErrorRecord", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ContainsPowerShellErrorRecord method was not found.");

        var value = method.Invoke(null, [standardError]);
        return value is bool typed && typed;
    }

    private static void InvokeAppendCapped(StringBuilder builder, string value, int maxChars, ref bool wasTruncated)
    {
        var method = typeof(TaskExecutionService).GetMethod("AppendCapped", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppendCapped method was not found.");

        var args = new object[]
        {
            builder,
            value,
            maxChars,
            wasTruncated
        };

        method.Invoke(null, args);
        wasTruncated = args[3] is bool typed && typed;
    }

    private static bool InvokeIsCurrentProcessElevated()
    {
        var method = typeof(TaskExecutionService).GetMethod("IsCurrentProcessElevated", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsCurrentProcessElevated method was not found.");

        var value = method.Invoke(null, []);
        return value is bool typed && typed;
    }
}
