using System.Text.RegularExpressions;

namespace tsky;

internal sealed class DataflowContext
{
    private readonly Dictionary<string, string> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _taskOutputs = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetPort(string name, out string value)
    {
        return _ports.TryGetValue(name, out value!);
    }

    public bool TryGetTaskOutput(string taskName, string outputName, out string value)
    {
        value = string.Empty;

        if (!_taskOutputs.TryGetValue(taskName, out var outputs))
        {
            return false;
        }

        return outputs.TryGetValue(outputName, out value!);
    }

    public void RegisterTaskOutputs(string taskName, IReadOnlyDictionary<string, string> outputs)
    {
        if (string.IsNullOrWhiteSpace(taskName) || outputs.Count == 0)
        {
            return;
        }

        var storedOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in outputs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var outputName = pair.Key.Trim();
            var outputValue = pair.Value ?? string.Empty;
            storedOutputs[outputName] = outputValue;
            _ports[outputName] = outputValue;
        }

        if (storedOutputs.Count > 0)
        {
            _taskOutputs[taskName.Trim()] = storedOutputs;
        }
    }
}

internal sealed class DataflowResolutionResult
{
    public required LauncherTask ResolvedTask { get; init; }

    public Dictionary<string, string> ResolvedInputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ResolvedOutputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> TraceLines { get; init; } = [];
}

internal static class DataflowPortResolver
{
    private static readonly Regex PlaceholderRegex = new(@"\$\{([^{}]+)\}", RegexOptions.Compiled);

    public static DataflowResolutionResult Resolve(LauncherTask task, DataflowContext? context)
    {
        var trace = new List<string>();
        var resolvedInputs = ResolveInputs(task, context, trace);

        var resolvedTask = new LauncherTask
        {
            Name = task.Name,
            Description = task.Description,
            Group = task.Group,
            Runner = task.Runner,
            WaitForExit = task.WaitForExit,
            RequiresElevation = task.RequiresElevation,
            CommandLine = ResolveValue(task.CommandLine, resolvedInputs, context, trace, "commandLine"),
            WorkingDirectory = ResolveValue(task.WorkingDirectory, resolvedInputs, context, trace, "workingDirectory"),
            Env = ResolveEnvironment(task.Env, resolvedInputs, context, trace),
            Inputs = new Dictionary<string, string>(resolvedInputs, StringComparer.OrdinalIgnoreCase),
            InputBindings = new Dictionary<string, string>(task.InputBindings, StringComparer.OrdinalIgnoreCase),
            Outputs = new Dictionary<string, string>(task.Outputs, StringComparer.OrdinalIgnoreCase)
        };

        var resolvedOutputs = ResolveOutputs(task, resolvedInputs, context, trace);

        return new DataflowResolutionResult
        {
            ResolvedTask = resolvedTask,
            ResolvedInputs = resolvedInputs,
            ResolvedOutputs = resolvedOutputs,
            TraceLines = trace
        };
    }

    private static Dictionary<string, string> ResolveInputs(LauncherTask task, DataflowContext? context, List<string> trace)
    {
        var resolvedInputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in task.Inputs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var inputName = pair.Key.Trim();
            var resolvedValue = ResolveValue(pair.Value ?? string.Empty, resolvedInputs, context, trace, $"input default '{inputName}'");
            resolvedInputs[inputName] = resolvedValue;
            trace.Add($"Input '{inputName}' default -> '{resolvedValue}'");
        }

        foreach (var binding in task.InputBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Key))
            {
                continue;
            }

            var inputName = binding.Key.Trim();
            var reference = binding.Value?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(reference))
            {
                trace.Add($"Input '{inputName}' binding skipped (empty reference).");
                continue;
            }

            if (TryResolveReference(reference, resolvedInputs, context, out var resolvedValue, out var source))
            {
                resolvedInputs[inputName] = resolvedValue;
                trace.Add($"Input '{inputName}' <- {source} = '{resolvedValue}'");
            }
            else
            {
                trace.Add($"Input '{inputName}' binding '{reference}' unresolved.");
            }
        }

        return resolvedInputs;
    }

    private static Dictionary<string, string> ResolveEnvironment(
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> resolvedInputs,
        DataflowContext? context,
        List<string> trace)
    {
        var resolvedEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in env)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var key = pair.Key.Trim();
            var resolvedValue = ResolveValue(pair.Value ?? string.Empty, resolvedInputs, context, trace, $"env '{key}'");
            resolvedEnv[key] = resolvedValue;
        }

        return resolvedEnv;
    }

    private static Dictionary<string, string> ResolveOutputs(
        LauncherTask task,
        IReadOnlyDictionary<string, string> resolvedInputs,
        DataflowContext? context,
        List<string> trace)
    {
        var resolvedOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in task.Outputs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var outputName = pair.Key.Trim();
            var resolvedValue = ResolveValue(pair.Value ?? string.Empty, resolvedInputs, context, trace, $"output '{outputName}'");
            resolvedOutputs[outputName] = resolvedValue;
            trace.Add($"Output '{outputName}' -> '{resolvedValue}'");
        }

        return resolvedOutputs;
    }

    private static string ResolveValue(
        string value,
        IReadOnlyDictionary<string, string> resolvedInputs,
        DataflowContext? context,
        List<string> trace,
        string location)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var didReplace = false;
        var replaced = PlaceholderRegex.Replace(value, match =>
        {
            var token = match.Groups[1].Value.Trim();

            if (TryResolveToken(token, resolvedInputs, context, out var tokenValue, out var source))
            {
                didReplace = true;
                trace.Add($"Resolved ${token} in {location} from {source} -> '{tokenValue}'");
                return tokenValue;
            }

            trace.Add($"Unresolved ${token} in {location}; replaced with empty string.");
            didReplace = true;
            return string.Empty;
        });

        if (!didReplace)
        {
            return value;
        }

        return replaced;
    }

    private static bool TryResolveToken(
        string token,
        IReadOnlyDictionary<string, string> resolvedInputs,
        DataflowContext? context,
        out string value,
        out string source)
    {
        value = string.Empty;
        source = string.Empty;

        if (token.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
        {
            var inputName = token[6..].Trim();
            if (resolvedInputs.TryGetValue(inputName, out value!))
            {
                source = $"input:{inputName}";
                return true;
            }

            return false;
        }

        if (token.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
        {
            var portName = token[5..].Trim();
            if (context is not null && context.TryGetPort(portName, out value!))
            {
                source = $"port:{portName}";
                return true;
            }

            return false;
        }

        if (token.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
        {
            var outputRef = token[7..].Trim();
            if (TryResolveTaskOutputReference(outputRef, context, out value!, out var taskName, out var outputName))
            {
                source = $"output:{taskName}.{outputName}";
                return true;
            }

            return false;
        }

        if (TryResolveTaskOutputReference(token, context, out value!, out var plainTaskName, out var plainOutputName))
        {
            source = $"output:{plainTaskName}.{plainOutputName}";
            return true;
        }

        if (resolvedInputs.TryGetValue(token, out value!))
        {
            source = $"input:{token}";
            return true;
        }

        if (context is not null && context.TryGetPort(token, out value!))
        {
            source = $"port:{token}";
            return true;
        }

        return false;
    }

    private static bool TryResolveReference(
        string reference,
        IReadOnlyDictionary<string, string> resolvedInputs,
        DataflowContext? context,
        out string value,
        out string source)
    {
        value = string.Empty;
        source = string.Empty;

        if (reference.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
        {
            var inputName = reference[6..].Trim();
            if (resolvedInputs.TryGetValue(inputName, out value!))
            {
                source = $"input:{inputName}";
                return true;
            }

            return false;
        }

        if (reference.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
        {
            var portName = reference[5..].Trim();
            if (context is not null && context.TryGetPort(portName, out value!))
            {
                source = $"port:{portName}";
                return true;
            }

            return false;
        }

        if (reference.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
        {
            var outputRef = reference[7..].Trim();
            if (TryResolveTaskOutputReference(outputRef, context, out value!, out var taskName, out var outputName))
            {
                source = $"output:{taskName}.{outputName}";
                return true;
            }

            return false;
        }

        if (TryResolveTaskOutputReference(reference, context, out value!, out var taskFromPlainRef, out var outputFromPlainRef))
        {
            source = $"output:{taskFromPlainRef}.{outputFromPlainRef}";
            return true;
        }

        if (context is not null && context.TryGetPort(reference, out value!))
        {
            source = $"port:{reference}";
            return true;
        }

        return false;
    }

    private static bool TryResolveTaskOutputReference(string reference, DataflowContext? context, out string value, out string taskName, out string outputName)
    {
        value = string.Empty;
        taskName = string.Empty;
        outputName = string.Empty;

        if (context is null || string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var dotIndex = reference.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= reference.Length - 1)
        {
            return false;
        }

        taskName = reference[..dotIndex].Trim();
        outputName = reference[(dotIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(taskName) || string.IsNullOrWhiteSpace(outputName))
        {
            return false;
        }

        return context.TryGetTaskOutput(taskName, outputName, out value);
    }
}
