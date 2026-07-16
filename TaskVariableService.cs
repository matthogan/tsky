using System.Text.RegularExpressions;

namespace tsky;

public static class TaskVariableService
{
    private static readonly Regex VariablePattern = new("\\$\\{([^{}:$\\s]+)\\}", RegexOptions.Compiled);
    private static readonly Regex EnvironmentPlaceholderPattern = new("%([^%]+)%", RegexOptions.Compiled);

    public sealed class TaskVariableUsage
    {
        public required string Name { get; init; }

        public required bool IsDefined { get; init; }

        public required bool HasValue { get; init; }

        public string? Value { get; init; }

        public bool IsResolved => IsDefined && HasValue;
    }

    public static IReadOnlyList<TaskVariableUsage> GetGroupVariableUsages(
        LauncherTask task,
        IReadOnlyDictionary<string, string> groupVariables)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPlaceholderNames(task.Name, names);
        CollectPlaceholderNames(task.Description, names);
        CollectPlaceholderNames(task.Group, names);
        CollectPlaceholderNames(task.CommandLine, names);
        CollectPlaceholderNames(task.Runner, names);
        CollectPlaceholderNames(task.WorkingDirectory, names);
        CollectPlaceholderNames(task.ImportedFromFile, names);
        CollectPlaceholderNames(task.ImportedAtUtc, names);
        CollectPlaceholderNames(task.Env, names);
        CollectPlaceholderNames(task.Inputs, names);
        CollectPlaceholderNames(task.InputBindings, names);
        CollectPlaceholderNames(task.Outputs, names);

        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                if (!groupVariables.TryGetValue(name, out var value))
                {
                    return new TaskVariableUsage
                    {
                        Name = name,
                        IsDefined = false,
                        HasValue = false,
                        Value = null
                    };
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    return new TaskVariableUsage
                    {
                        Name = name,
                        IsDefined = true,
                        HasValue = false,
                        Value = value
                    };
                }

                return new TaskVariableUsage
                {
                    Name = name,
                    IsDefined = true,
                    HasValue = true,
                    Value = value
                };
            })
            .ToList();
    }

    public static bool UsesGroupVariablePlaceholders(LauncherTask task)
    {
        return GetGroupVariableUsages(task, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)).Count > 0;
    }

    public static LauncherTask ApplyGroupVariables(
        LauncherTask task,
        IReadOnlyDictionary<string, string> groupVariables,
        out List<string> unresolvedVariables)
    {
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var expandedTask = new LauncherTask
        {
            Name = Expand(task.Name, groupVariables, unresolved),
            Description = Expand(task.Description, groupVariables, unresolved),
            Group = Expand(task.Group, groupVariables, unresolved),
            CommandLine = Expand(task.CommandLine, groupVariables, unresolved),
            Runner = Expand(task.Runner, groupVariables, unresolved),
            WorkingDirectory = Expand(task.WorkingDirectory, groupVariables, unresolved),
            WaitForExit = task.WaitForExit,
            RequiresElevation = task.RequiresElevation,
            IgnoreExitCode = task.IgnoreExitCode,
            TimeoutSeconds = task.TimeoutSeconds,
            KillProcessTreeOnCancel = task.KillProcessTreeOnCancel,
            MaxCapturedOutputChars = task.MaxCapturedOutputChars,
            IsEnabled = task.IsEnabled,
            ImportedFromFile = Expand(task.ImportedFromFile, groupVariables, unresolved),
            ImportedAtUtc = Expand(task.ImportedAtUtc, groupVariables, unresolved),
            Env = ExpandDictionary(task.Env, groupVariables, unresolved),
            Inputs = ExpandDictionary(task.Inputs, groupVariables, unresolved),
            InputBindings = ExpandDictionary(task.InputBindings, groupVariables, unresolved),
            Outputs = ExpandDictionary(task.Outputs, groupVariables, unresolved)
        };

        unresolvedVariables = unresolved.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return expandedTask;
    }

    public static List<string> GetUnresolvedVariables(LauncherTask task, IReadOnlyDictionary<string, string> groupVariables)
    {
        _ = ApplyGroupVariables(task, groupVariables, out var unresolved);
        return unresolved;
    }

    private static Dictionary<string, string> ExpandDictionary(
        IReadOnlyDictionary<string, string> dictionary,
        IReadOnlyDictionary<string, string> groupVariables,
        HashSet<string> unresolved)
    {
        var expanded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in dictionary)
        {
            var key = Expand(pair.Key, groupVariables, unresolved, expandEnvironmentPlaceholders: false);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            expanded[key] = Expand(pair.Value, groupVariables, unresolved, expandEnvironmentPlaceholders: true);
        }

        return expanded;
    }

    private static string Expand(
        string? value,
        IReadOnlyDictionary<string, string> groupVariables,
        HashSet<string> unresolved,
        bool expandEnvironmentPlaceholders = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var expanded = VariablePattern.Replace(value, match =>
        {
            var variableName = match.Groups[1].Value;
            if (groupVariables.TryGetValue(variableName, out var variableValue))
            {
                if (string.IsNullOrWhiteSpace(variableValue))
                {
                    unresolved.Add(variableName);
                    return match.Value;
                }

                return ExpandEnvironmentPlaceholders(variableValue ?? string.Empty);
            }

            if (IsDeferredDataflowOrRuntimeToken(variableName))
            {
                return match.Value;
            }

            unresolved.Add(variableName);
            return match.Value;
        });

        return expandEnvironmentPlaceholders
            ? ExpandEnvironmentPlaceholders(expanded)
            : expanded;
    }

    private static string ExpandEnvironmentPlaceholders(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return EnvironmentPlaceholderPattern.Replace(value, match =>
        {
            var variableName = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return match.Value;
            }

            var resolved = Environment.GetEnvironmentVariable(variableName);
            return resolved ?? string.Empty;
        });
    }

    private static bool IsDeferredDataflowOrRuntimeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.StartsWith("input:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("port:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("output:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("command:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("runtime:", StringComparison.OrdinalIgnoreCase)
            || token.Equals("stdout", StringComparison.OrdinalIgnoreCase)
            || token.Equals("stderr", StringComparison.OrdinalIgnoreCase)
            || token.Equals("exitcode", StringComparison.OrdinalIgnoreCase)
            || token.Contains('.');
    }

    private static bool ContainsPlaceholder(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && VariablePattern.IsMatch(value);
    }

    private static bool ContainsPlaceholder(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            if (ContainsPlaceholder(pair.Key) || ContainsPlaceholder(pair.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectPlaceholderNames(string? value, ISet<string> names)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (Match match in VariablePattern.Matches(value))
        {
            var variableName = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(variableName))
            {
                names.Add(variableName);
            }
        }
    }

    private static void CollectPlaceholderNames(IReadOnlyDictionary<string, string> values, ISet<string> names)
    {
        foreach (var pair in values)
        {
            CollectPlaceholderNames(pair.Key, names);
            CollectPlaceholderNames(pair.Value, names);
        }
    }
}
