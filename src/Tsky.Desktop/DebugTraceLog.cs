using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace tsky;

public static class DebugTraceLog
{
    private const int MaxRetainedLogFiles = 5;
    private static readonly object SyncRoot = new();
    private static readonly AsyncLocal<string?> CorrelationToken = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "logs");
    private static int _operationSequence;

    private static readonly string LogFilePath;

    static DebugTraceLog()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(
                LogDirectory,
                $"debug-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}.log");
            PruneOldLogs();
        }
        catch
        {
            LogFilePath = Path.Combine(
                Path.GetTempPath(),
                $"tsky-debug-{DateTime.Now:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}.log");
        }
    }

    public static string CurrentLogPath => LogFilePath;

    public static string? CurrentOperationToken => CorrelationToken.Value;

    public static void Write(string area, string message)
    {
        try
        {
            var tokenSegment = string.IsNullOrWhiteSpace(CorrelationToken.Value)
                ? string.Empty
                : $" [Op:{CorrelationToken.Value}]";
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [T{Environment.CurrentManagedThreadId}]{tokenSegment} [{area}] {message}{Environment.NewLine}";

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort logger: never throw into app flow.
        }
    }

    public static void Write(string message,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "")
    {
        Write(BuildCallerArea(callerFilePath, callerMemberName), message);
    }

    public static void Write(Exception exception, string? message = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerMemberName] string callerMemberName = "")
    {
        var area = BuildCallerArea(callerFilePath, callerMemberName);
        var prefix = string.IsNullOrWhiteSpace(message) ? "Exception" : message;
        Write(area, $"{prefix}: {exception}");
    }

    public static void Write(Type sourceType, string message)
    {
        var area = sourceType?.Name ?? "Unknown";
        Write(area, message);
    }

    public static void Write<TSource>(string message)
    {
        Write(typeof(TSource), message);
    }

    public static IDisposable BeginOperation(string category, string? detail = null)
    {
        var token = BuildOperationToken(category, detail);
        var previousToken = CorrelationToken.Value;
        CorrelationToken.Value = token;
        Write("Operation", $"Begin {token}");
        return new OperationScope(previousToken, token);
    }

    private static void PruneOldLogs()
    {
        try
        {
            var logFiles = new DirectoryInfo(LogDirectory)
                .GetFiles("debug-*.log")
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var obsoleteLog in logFiles.Skip(MaxRetainedLogFiles))
            {
                try
                {
                    obsoleteLog.Delete();
                }
                catch
                {
                    // Ignore retention delete failures.
                }
            }
        }
        catch
        {
            // Ignore retention discovery failures.
        }
    }

    private static string BuildCallerArea(string callerFilePath, string callerMemberName)
    {
        var fileName = Path.GetFileNameWithoutExtension(callerFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Unknown";
        }

        return string.IsNullOrWhiteSpace(callerMemberName)
            ? fileName
            : $"{fileName}.{callerMemberName}";
    }

    private static string BuildOperationToken(string category, string? detail)
    {
        var normalizedCategory = NormalizeTokenPart(category, 12, "op");
        var normalizedDetail = NormalizeTokenPart(detail, 20, string.Empty);
        var stamp = DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture);
        var sequence = Interlocked.Increment(ref _operationSequence).ToString("x4", CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(normalizedDetail)
            ? $"{normalizedCategory}-{stamp}-{sequence}"
            : $"{normalizedCategory}-{normalizedDetail}-{stamp}-{sequence}";
    }

    private static string NormalizeTokenPart(string? input, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                if (builder.Length >= maxLength)
                {
                    break;
                }
            }
            else if (char.IsWhiteSpace(ch) && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
                if (builder.Length >= maxLength)
                {
                    break;
                }
            }
        }

        if (builder.Length == 0)
        {
            return fallback;
        }

        var normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly string? _previousToken;
        private readonly string _token;
        private bool _disposed;

        public OperationScope(string? previousToken, string token)
        {
            _previousToken = previousToken;
            _token = token;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Write("Operation", $"End {_token}");
            CorrelationToken.Value = _previousToken;
        }
    }
}
