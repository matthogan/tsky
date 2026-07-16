using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace tsky.Tests;

public sealed class TaskExecutionServiceTests
{
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
}
