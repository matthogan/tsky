using System.Text.Json;
using Jint;
using Jint.Native;

namespace tsky;

public sealed record TaskResultEvaluationInput(
    int? ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Cancelled,
    long DurationMs,
    string WorkingDirectory)
{
    public string CombinedOutput => $"{Stdout}\n{Stderr}";
}

public sealed record TaskResultEvaluationResult(
    string Outcome,
    string? Message,
    string? ReasonCode);

public static class TaskResultEvaluationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static bool TryEvaluate(
        string script,
        TaskResultEvaluationInput input,
        CancellationToken cancellationToken,
        out TaskResultEvaluationResult result,
        out string error)
    {
        result = new TaskResultEvaluationResult("failure", null, null);
        error = string.Empty;

        try
        {
            var engine = new Engine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromSeconds(2));
                options.MaxStatements(20_000);
                options.LimitMemory(8_000_000);
                options.CancellationToken(cancellationToken);
            });

            var inputJson = JsonSerializer.Serialize(input, JsonOptions);
            engine.SetValue("__taskJson", inputJson);
            engine.Execute("const task = JSON.parse(__taskJson);");
            engine.Execute(script);

            JsValue jsResult;
            try
            {
                jsResult = engine.Evaluate("evaluate(task)");
            }
            catch (Exception ex)
            {
                if (IsMissingEvaluateFunctionError(ex.Message))
                {
                    error = "The evaluator script must define function evaluate(task).";
                }
                else
                {
                    error = ex.Message;
                }

                return false;
            }

            var resultJson = engine
                .SetValue("__result", jsResult)
                .Evaluate("JSON.stringify(__result)")
                .AsString();

            var parsed = JsonSerializer.Deserialize<TaskResultEvaluationResult>(resultJson, JsonOptions);
            if (parsed is null)
            {
                error = "The evaluator script returned no result.";
                return false;
            }

            if (!IsSupportedOutcome(parsed.Outcome))
            {
                error = $"The evaluator returned unsupported outcome '{parsed.Outcome}'. Use 'success' or 'failure'.";
                return false;
            }

            result = parsed with { Outcome = NormalizeOutcome(parsed.Outcome) };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsMissingEvaluateFunctionError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("evaluate", StringComparison.OrdinalIgnoreCase)
               && (message.Contains("not", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("invoke", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("function", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedOutcome(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("success", StringComparison.OrdinalIgnoreCase)
            || value.Equals("failure", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOutcome(string value)
    {
        return value.Equals("success", StringComparison.OrdinalIgnoreCase)
            ? "success"
            : "failure";
    }
}
