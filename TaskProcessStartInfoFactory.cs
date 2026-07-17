using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace tsky;

internal static class TaskProcessStartInfoFactory
{
    private static readonly Regex EnvironmentPlaceholderRegex = new("%([^%]+)%", RegexOptions.Compiled);

    public static bool TryBuild(LauncherTask task, out ProcessStartInfo processStartInfo, out string error)
    {
        processStartInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        error = string.Empty;
        var runner = NormalizeRunner(task.Runner);
        if (!ApplyTaskEnvironmentVariables(task, processStartInfo, out error))
        {
            return false;
        }

        var commandLine = task.CommandLine ?? string.Empty;
        if (!TryExpandEnvironmentPlaceholders(commandLine, processStartInfo.Environment, out var expandedCommandLine, out var missingCommandLineVariables))
        {
            error = $"commandLine references undefined environment variable(s): {string.Join(", ", missingCommandLineVariables.Select(name => "%" + name + "%"))}.";
            return false;
        }

        switch (runner)
        {
            case "exe":
                if (!TrySplitExecutableCommand(expandedCommandLine, out var fileName, out var arguments))
                {
                    error = "For runner 'exe', commandLine must include an executable path or command.";
                    return false;
                }

                processStartInfo.FileName = fileName;
                processStartInfo.Arguments = arguments;

                if (Path.IsPathRooted(fileName))
                {
                    var directory = Path.GetDirectoryName(fileName);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    {
                        processStartInfo.WorkingDirectory = directory;
                    }
                }
                break;

            case "cmd":
                processStartInfo.FileName = "cmd.exe";
                processStartInfo.Arguments = $"/c \"{expandedCommandLine}\"";
                break;

            case "powershell":
                processStartInfo.FileName = ResolvePowerShellExecutable();
                processStartInfo.ArgumentList.Add("-ExecutionPolicy");
                processStartInfo.ArgumentList.Add("Bypass");
                processStartInfo.ArgumentList.Add("-Command");
                processStartInfo.ArgumentList.Add(expandedCommandLine);
                break;

            case "python":
                processStartInfo.FileName = ResolvePythonExecutable();
                processStartInfo.Arguments = expandedCommandLine;
                break;

            default:
                error = "Runner must be one of: exe, cmd, powershell, python.";
                return false;
        }

        if (!ApplyTaskWorkingDirectory(task, processStartInfo, out error))
        {
            return false;
        }

        return true;
    }

    public static string NormalizeRunner(string? runner)
    {
        var normalized = string.IsNullOrWhiteSpace(runner) ? "exe" : runner.Trim().ToLowerInvariant();

        // Support py as a friendly alias for the python runner.
        return normalized == "py" ? "python" : normalized;
    }

    private static string ResolvePowerShellExecutable()
    {
        // Prefer pwsh to match modern PowerShell CLI behavior, fall back to Windows PowerShell.
        if (IsExecutableOnPath("pwsh.exe"))
        {
            return "pwsh.exe";
        }

        return "powershell.exe";
    }

    private static string ResolvePythonExecutable()
    {
        // Prefer python.exe for parity with existing shells, then fall back to the Windows launcher.
        if (IsExecutableOnPath("python.exe"))
        {
            return "python.exe";
        }

        if (IsExecutableOnPath("py.exe"))
        {
            return "py.exe";
        }

        // Fall back to python.exe so startup errors remain clear in task output.
        return "python.exe";
    }

    private static bool IsExecutableOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(pathEntry))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(pathEntry.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore malformed path entries and continue checking other PATH segments.
            }
        }

        return false;
    }

    private static bool ApplyTaskEnvironmentVariables(LauncherTask task, ProcessStartInfo processStartInfo, out string error)
    {
        error = string.Empty;

        if (task.Env.Count == 0)
        {
            return true;
        }

        foreach (var pair in task.Env)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                error = "Environment variable names cannot be empty.";
                return false;
            }

            var variableName = pair.Key.Trim();
            var rawValue = pair.Value ?? string.Empty;
            var expandedValue = ExpandEnvironmentPlaceholders(rawValue, processStartInfo.Environment);
            processStartInfo.Environment[variableName] = expandedValue;
        }

        return true;
    }

    private static bool ApplyTaskWorkingDirectory(LauncherTask task, ProcessStartInfo processStartInfo, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(task.WorkingDirectory))
        {
            return true;
        }

        var expandedDirectory = ExpandEnvironmentPlaceholders(task.WorkingDirectory.Trim(), processStartInfo.Environment);
        var resolvedDirectory = Path.IsPathRooted(expandedDirectory)
            ? expandedDirectory
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, expandedDirectory));

        if (!Directory.Exists(resolvedDirectory))
        {
            error = $"Working directory does not exist: {resolvedDirectory}";
            return false;
        }

        processStartInfo.WorkingDirectory = resolvedDirectory;
        return true;
    }

    private static string ExpandEnvironmentPlaceholders(string value, IDictionary<string, string?> environment)
    {
        return EnvironmentPlaceholderRegex.Replace(value, match =>
        {
            var variableName = match.Groups[1].Value;

            if (environment.TryGetValue(variableName, out var existingValue) && existingValue is not null)
            {
                return existingValue;
            }

            var machineValue = Environment.GetEnvironmentVariable(variableName);
            return machineValue ?? string.Empty;
        });
    }

    internal static IReadOnlyList<string> GetMissingEnvironmentPlaceholders(string value, IEnumerable<string>? overrideVariableNames = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var overrideNameSet = overrideVariableNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(overrideVariableNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (Match match in EnvironmentPlaceholderRegex.Matches(value))
        {
            var variableName = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(variableName))
            {
                continue;
            }

            if (overrideNameSet.Contains(variableName))
            {
                continue;
            }

            if (Environment.GetEnvironmentVariable(variableName) is not null)
            {
                continue;
            }

            if (!missing.Contains(variableName, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(variableName);
            }
        }

        return missing;
    }

    private static bool TryExpandEnvironmentPlaceholders(
        string value,
        IDictionary<string, string?> environment,
        out string expanded,
        out IReadOnlyList<string> missingVariables)
    {
        missingVariables = GetMissingEnvironmentPlaceholders(value, environment.Keys);
        if (missingVariables.Count > 0)
        {
            expanded = string.Empty;
            return false;
        }

        expanded = ExpandEnvironmentPlaceholders(value, environment);
        return true;
    }

    private static bool TrySplitExecutableCommand(string commandLine, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var trimmed = commandLine.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex <= 1)
            {
                return false;
            }

            fileName = trimmed[1..closingQuoteIndex];
            arguments = trimmed[(closingQuoteIndex + 1)..].TrimStart();
            return true;
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        if (firstSpaceIndex < 0)
        {
            fileName = trimmed;
            arguments = string.Empty;
            return true;
        }

        fileName = trimmed[..firstSpaceIndex];
        arguments = trimmed[(firstSpaceIndex + 1)..].TrimStart();
        return true;
    }
}
