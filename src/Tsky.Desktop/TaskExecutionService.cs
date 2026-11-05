using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace tsky;

public sealed class TaskExecutionRunResult
{
    public bool Started { get; init; }

    public bool WaitedForExit { get; init; }

    public bool Succeeded { get; init; }

    public TaskExecutionOutcome Outcome { get; init; } = TaskExecutionOutcome.LaunchFailed;

    public int? ExitCode { get; init; }

    public int? ProcessId { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool CanCaptureOutput { get; init; }

    public bool LaunchedElevated { get; init; }

    public bool WaitForExit { get; init; }

    public bool IgnoreExitCodeApplied { get; init; }

    public string? OutputCaptureDisabledReason { get; init; }

    public string? ConfigurationError { get; init; }

    public string? FailureMessage { get; init; }

    public bool ElevationPromptCanceled { get; init; }

    public bool OutputTruncated { get; init; }

    public int MaxCapturedOutputChars { get; init; }
}

public enum TaskExecutionOutcome
{
    Started,
    Succeeded,
    FailedNonZeroExit,
    IgnoredNonZeroExit,
    LaunchFailed,
    TimedOut,
    Cancelled
}

public static class TaskExecutionService
{
    internal static Func<Process, bool>? StartProcessOverride { get; set; }

    internal static Func<Process, CancellationToken, Task>? WaitForExitAsyncOverride { get; set; }

    internal static Func<bool>? IsCurrentProcessElevatedOverride { get; set; }

    public static bool ShouldLaunchAsAdministrator(LauncherTask task)
    {
        return task.RequiresElevation && !IsCurrentProcessElevated();
    }

    public static async Task<TaskExecutionRunResult> ExecuteAsync(
        LauncherTask task,
        Action<string, bool>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var launchAsAdministrator = ShouldLaunchAsAdministrator(task);
        var waitForExit = task.WaitForExit;
        var canCaptureOutput = waitForExit && !launchAsAdministrator;
        var timeoutSeconds = Math.Max(0, task.TimeoutSeconds);
        var maxCapturedOutputChars = Math.Max(1024, task.MaxCapturedOutputChars);

        if (!TaskProcessStartInfoFactory.TryBuild(task, out var processStartInfo, out var configurationError))
        {
            return new TaskExecutionRunResult
            {
                Started = false,
                Succeeded = false,
                Outcome = TaskExecutionOutcome.LaunchFailed,
                WaitForExit = waitForExit,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                ConfigurationError = configurationError
            };
        }

        if (!canCaptureOutput)
        {
            processStartInfo.RedirectStandardOutput = false;
            processStartInfo.RedirectStandardError = false;
        }

        if (launchAsAdministrator)
        {
            processStartInfo.UseShellExecute = true;
            processStartInfo.Verb = "runas";
            processStartInfo.CreateNoWindow = false;
        }

        try
        {
            using var process = new Process { StartInfo = processStartInfo };
            await StartProcessAsync(process);

            if (!waitForExit)
            {
                return new TaskExecutionRunResult
                {
                    Started = true,
                    WaitedForExit = false,
                    Succeeded = true,
                    Outcome = TaskExecutionOutcome.Started,
                    ProcessId = process.Id,
                    WaitForExit = false,
                    CanCaptureOutput = false,
                    LaunchedElevated = launchAsAdministrator,
                    MaxCapturedOutputChars = maxCapturedOutputChars,
                    OutputCaptureDisabledReason = "task is configured to not wait for process exit"
                };
            }

            var capturedStandardOutput = string.Empty;
            var capturedStandardError = string.Empty;
            var outputTruncated = false;

            using var timeoutCts = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : null;
            using var linkedCts = timeoutCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var killRegistration = linkedCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(task.KillProcessTreeOnCancel);
                    }
                }
                catch (Exception ex)
                {
                    DebugTraceLog.Write(ex, $"[TaskExecutionService] Best-effort kill failed for task '{task.Name}'");
                    // Best-effort termination; completion path will return cancellation/timeout state.
                }
            });

            if (canCaptureOutput)
            {
                var standardOutputBuilder = new StringBuilder();
                var standardErrorBuilder = new StringBuilder();
                var standardOutputClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var standardErrorClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data is null)
                    {
                        standardOutputClosed.TrySetResult(true);
                        return;
                    }

                    AppendCapped(standardOutputBuilder, args.Data + Environment.NewLine, maxCapturedOutputChars, ref outputTruncated);
                    onOutputLine?.Invoke(args.Data, false);
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is null)
                    {
                        standardErrorClosed.TrySetResult(true);
                        return;
                    }

                    AppendCapped(standardErrorBuilder, args.Data + Environment.NewLine, maxCapturedOutputChars, ref outputTruncated);
                    onOutputLine?.Invoke(args.Data, true);
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await WaitForExitAsync(process, linkedCts.Token);
                await Task.WhenAll(standardOutputClosed.Task, standardErrorClosed.Task);

                capturedStandardOutput = standardOutputBuilder.ToString().TrimEnd();
                capturedStandardError = standardErrorBuilder.ToString().TrimEnd();
            }
            else
            {
                await WaitForExitAsync(process, linkedCts.Token);
            }

            if (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                return new TaskExecutionRunResult
                {
                    Started = true,
                    WaitedForExit = true,
                    Succeeded = false,
                    Outcome = TaskExecutionOutcome.TimedOut,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    ProcessId = process.Id,
                    StandardOutput = capturedStandardOutput,
                    StandardError = capturedStandardError,
                    OutputTruncated = outputTruncated,
                    MaxCapturedOutputChars = maxCapturedOutputChars,
                    WaitForExit = true,
                    CanCaptureOutput = canCaptureOutput,
                    LaunchedElevated = launchAsAdministrator,
                    FailureMessage = timeoutSeconds > 0
                        ? $"Process timed out after {timeoutSeconds} seconds."
                        : "Process timed out."
                };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new TaskExecutionRunResult
                {
                    Started = true,
                    WaitedForExit = true,
                    Succeeded = false,
                    Outcome = TaskExecutionOutcome.Cancelled,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    ProcessId = process.Id,
                    StandardOutput = capturedStandardOutput,
                    StandardError = capturedStandardError,
                    OutputTruncated = outputTruncated,
                    MaxCapturedOutputChars = maxCapturedOutputChars,
                    WaitForExit = true,
                    CanCaptureOutput = canCaptureOutput,
                    LaunchedElevated = launchAsAdministrator,
                    FailureMessage = "Process execution was cancelled."
                };
            }

            var isPowerShellRunner = string.Equals(
                TaskProcessStartInfoFactory.NormalizeRunner(task.Runner),
                "powershell",
                StringComparison.OrdinalIgnoreCase);

            var hasPowerShellErrorRecord = process.ExitCode == 0
                && isPowerShellRunner
                && ContainsPowerShellErrorRecord(capturedStandardError);

            var hasPowerShellStderrOutput = process.ExitCode == 0
                && isPowerShellRunner
                && !string.IsNullOrWhiteSpace(capturedStandardError);

            var hasPowerShellFailureSignal = hasPowerShellErrorRecord || hasPowerShellStderrOutput;

            var isSuccessfulExit = process.ExitCode == 0 && !hasPowerShellFailureSignal
                || process.ExitCode != 0 && task.IgnoreExitCode;
            var outcome = process.ExitCode == 0
                ? hasPowerShellFailureSignal
                    ? TaskExecutionOutcome.FailedNonZeroExit
                    : TaskExecutionOutcome.Succeeded
                : task.IgnoreExitCode
                    ? TaskExecutionOutcome.IgnoredNonZeroExit
                    : TaskExecutionOutcome.FailedNonZeroExit;
            var failureMessage = hasPowerShellFailureSignal
                ? "PowerShell produced stderr output even though the process exited with code 0."
                : null;

            return new TaskExecutionRunResult
            {
                Started = true,
                WaitedForExit = true,
                Succeeded = isSuccessfulExit,
                Outcome = outcome,
                ExitCode = process.ExitCode,
                ProcessId = process.Id,
                StandardOutput = capturedStandardOutput,
                StandardError = capturedStandardError,
                OutputTruncated = outputTruncated,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                WaitForExit = true,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                FailureMessage = failureMessage,
                IgnoreExitCodeApplied = process.ExitCode != 0 && task.IgnoreExitCode,
                OutputCaptureDisabledReason = canCaptureOutput
                    ? null
                    : launchAsAdministrator
                        ? "process ran elevated (stream redirection unavailable)"
                        : "process output capture was disabled"
            };
        }
        catch (Win32Exception ex) when (launchAsAdministrator && ex.NativeErrorCode == 1223)
        {
            DebugTraceLog.Write(ex, $"[TaskExecutionService] Elevation prompt canceled for task '{task.Name}'");
            return new TaskExecutionRunResult
            {
                Started = false,
                Succeeded = false,
                Outcome = TaskExecutionOutcome.LaunchFailed,
                WaitForExit = waitForExit,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                ElevationPromptCanceled = true,
                FailureMessage = "Elevation was canceled by the user."
            };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            DebugTraceLog.Write(ex, $"[TaskExecutionService] Cancellation observed for task '{task.Name}'");
            return new TaskExecutionRunResult
            {
                Started = true,
                WaitedForExit = true,
                Succeeded = false,
                Outcome = TaskExecutionOutcome.Cancelled,
                WaitForExit = waitForExit,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                FailureMessage = "Process execution was cancelled."
            };
        }
        catch (OperationCanceledException ex)
        {
            DebugTraceLog.Write(ex, $"[TaskExecutionService] Timeout observed for task '{task.Name}'");
            return new TaskExecutionRunResult
            {
                Started = true,
                WaitedForExit = true,
                Succeeded = false,
                Outcome = TaskExecutionOutcome.TimedOut,
                WaitForExit = waitForExit,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                FailureMessage = timeoutSeconds > 0
                    ? $"Process timed out after {timeoutSeconds} seconds."
                    : "Process timed out."
            };
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[TaskExecutionService] Launch/execute failed for task '{task.Name}'");
            return new TaskExecutionRunResult
            {
                Started = false,
                Succeeded = false,
                Outcome = TaskExecutionOutcome.LaunchFailed,
                WaitForExit = waitForExit,
                CanCaptureOutput = canCaptureOutput,
                LaunchedElevated = launchAsAdministrator,
                MaxCapturedOutputChars = maxCapturedOutputChars,
                FailureMessage = ex.Message
            };
        }
    }

    private static void AppendCapped(StringBuilder builder, string value, int maxChars, ref bool wasTruncated)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var remaining = maxChars - builder.Length;
        if (remaining <= 0)
        {
            wasTruncated = true;
            return;
        }

        if (value.Length <= remaining)
        {
            builder.Append(value);
            return;
        }

        builder.Append(value.AsSpan(0, remaining));
        wasTruncated = true;
    }

    private static bool IsCurrentProcessElevated()
    {
        if (IsCurrentProcessElevatedOverride is not null)
        {
            return IsCurrentProcessElevatedOverride();
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity is null)
        {
            return false;
        }

        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StartProcess(Process process)
    {
        if (StartProcessOverride is not null)
        {
            StartProcessOverride(process);
            return;
        }

        process.Start();
    }

    private static Task StartProcessAsync(Process process)
    {
        // Process.Start can block for GUI-heavy launches (for example IDE bootstrap).
        // Run startup on a worker thread so task launch does not stall the UI thread.
        return Task.Run(() => StartProcess(process));
    }

    private static Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        if (WaitForExitAsyncOverride is not null)
        {
            return WaitForExitAsyncOverride(process, cancellationToken);
        }

        return process.WaitForExitAsync(cancellationToken);
    }

    private static bool ContainsPowerShellErrorRecord(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        if (Regex.IsMatch(standardError, @"(?im)^\s*(CategoryInfo|FullyQualifiedErrorId)\s*:")
            || Regex.IsMatch(standardError, @"(?im)^\s*At\s+line:\d+"))
        {
            return true;
        }

        var hasCmdletErrorPrefix = Regex.IsMatch(standardError, @"(?im)^\s*[A-Za-z][A-Za-z0-9-]*\s*:\s*(?:$|.+)");
        var hasPowerShellLineContext = Regex.IsMatch(standardError, @"(?im)^\s*Line\s*\|\s*$")
            || Regex.IsMatch(standardError, @"(?im)^\s*\|\s+.+$");

        return hasCmdletErrorPrefix && hasPowerShellLineContext;
    }
}
