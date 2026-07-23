namespace tsky.Tests;

public sealed class TaskResultEvaluationServiceTests
{
    [Fact]
    public void TryEvaluate_ValidScript_ReturnsSuccessOutcome()
    {
        const string script = """
            function evaluate(task) {
                if (task.exitCode === 0 && task.stderr.length === 0) {
                    return { outcome: 'success', reasonCode: 'OK', message: 'all good' };
                }

                return { outcome: 'failure', reasonCode: 'BAD' };
            }
            """;

        var input = new TaskResultEvaluationInput(
            ExitCode: 0,
            Stdout: "done",
            Stderr: string.Empty,
            TimedOut: false,
            Cancelled: false,
            DurationMs: 42,
            WorkingDirectory: "C:/tmp");

        var ok = TaskResultEvaluationService.TryEvaluate(
            script,
            input,
            CancellationToken.None,
            out var result,
            out var error);

        Assert.True(ok);
        Assert.True(string.IsNullOrWhiteSpace(error));
        Assert.Equal("success", result.Outcome);
        Assert.Equal("OK", result.ReasonCode);
        Assert.Equal("all good", result.Message);
    }

    [Fact]
    public void TryEvaluate_MissingEvaluateFunction_Fails()
    {
        const string script = "const x = 1;";

        var input = new TaskResultEvaluationInput(
            ExitCode: 0,
            Stdout: string.Empty,
            Stderr: string.Empty,
            TimedOut: false,
            Cancelled: false,
            DurationMs: 0,
            WorkingDirectory: string.Empty);

        var ok = TaskResultEvaluationService.TryEvaluate(
            script,
            input,
            CancellationToken.None,
            out var _,
            out var error);

        Assert.False(ok);
        Assert.Contains("function evaluate(task)", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryEvaluate_InvalidOutcome_Fails()
    {
        const string script = """
            function evaluate(task) {
                return { outcome: 'maybe' };
            }
            """;

        var input = new TaskResultEvaluationInput(
            ExitCode: 0,
            Stdout: string.Empty,
            Stderr: string.Empty,
            TimedOut: false,
            Cancelled: false,
            DurationMs: 0,
            WorkingDirectory: string.Empty);

        var ok = TaskResultEvaluationService.TryEvaluate(
            script,
            input,
            CancellationToken.None,
            out var _,
            out var error);

        Assert.False(ok);
        Assert.Contains("unsupported outcome", error, StringComparison.OrdinalIgnoreCase);
    }
}
