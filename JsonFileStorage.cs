using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;

namespace tsky;

internal static class JsonFileStorage
{
    private const int LockAcquireTimeoutMs = 5000;
    private const int LockRetryDelayMs = 50;
    private const int MaxBackupFilesPerTarget = 5;
    private static readonly ConcurrentDictionary<string, FileStream> ActiveLockStreams = new(StringComparer.OrdinalIgnoreCase);

    public static T LoadOrDefault<T>(string path, JsonSerializerOptions options) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            using var _ = AcquireCrossInstanceLock(path);
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, options) ?? new T();
        }
        catch (Exception ex)
        {
            DebugTraceLog.Write(ex, $"[JsonFileStorage] LoadOrDefault failed for '{path}', returning default");
            return new T();
        }
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions options)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        using var _ = AcquireCrossInstanceLock(path);

        var json = JsonSerializer.Serialize(value, options);
        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directoryPath) ? Environment.CurrentDirectory : directoryPath,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            // Write to a temp file first so target content is never partially updated.
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            var backupPath = CreateTimestampedBackupPath(path);
            try
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                TrimBackupHistory(path);
            }
            catch (PlatformNotSupportedException)
            {
                DebugTraceLog.Write("[JsonFileStorage] File.Replace not supported; using backup/delete/move fallback");
                CreateBackupSnapshot(path);
                File.Delete(path);
                File.Move(tempPath, path);
            }
            catch (IOException)
            {
                // Fallback for file systems where File.Replace semantics are unavailable.
                DebugTraceLog.Write($"[JsonFileStorage] File.Replace IO fallback engaged for '{path}'");
                CreateBackupSnapshot(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    DebugTraceLog.Write(ex, $"[JsonFileStorage] Temp file cleanup failed for '{tempPath}'");
                    // Best-effort cleanup.
                }
            }
        }
    }

    public static void ReleaseAllLocks()
    {
        foreach (var pair in ActiveLockStreams)
        {
            try
            {
                pair.Value.Dispose();
            }
            catch (Exception ex)
            {
                DebugTraceLog.Write(ex, "[JsonFileStorage] ReleaseAllLocks dispose failed");
                // Best-effort lock cleanup at shutdown.
            }
        }

        ActiveLockStreams.Clear();
    }

    private static IDisposable AcquireCrossInstanceLock(string targetPath)
    {
        var lockPath = targetPath + ".lock";
        var lockDirectory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        var start = Environment.TickCount64;
        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var key = Guid.NewGuid().ToString("N");
                ActiveLockStreams[key] = stream;
                return new LockHandle(key, stream);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 - start >= LockAcquireTimeoutMs)
                {
                    DebugTraceLog.Write($"[JsonFileStorage] Lock acquire timed out (IOException) for '{targetPath}'");
                    throw;
                }

                Thread.Sleep(LockRetryDelayMs);
            }
            catch (UnauthorizedAccessException)
            {
                if (Environment.TickCount64 - start >= LockAcquireTimeoutMs)
                {
                    DebugTraceLog.Write($"[JsonFileStorage] Lock acquire timed out (UnauthorizedAccessException) for '{targetPath}'");
                    throw;
                }

                Thread.Sleep(LockRetryDelayMs);
            }
        }
    }

    private static string CreateTimestampedBackupPath(string targetPath)
    {
        return $"{targetPath}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
    }

    private static void CreateBackupSnapshot(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return;
        }

        var backupPath = CreateTimestampedBackupPath(targetPath);
        File.Copy(targetPath, backupPath, overwrite: false);
        TrimBackupHistory(targetPath);
    }

    private static void TrimBackupHistory(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var fileName = Path.GetFileName(targetPath);
        var backupPattern = $"{fileName}.*.bak";

        var backups = Directory.GetFiles(directory, backupPattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();

        foreach (var stale in backups.Skip(MaxBackupFilesPerTarget))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception ex)
            {
                DebugTraceLog.Write(ex, $"[JsonFileStorage] Stale backup delete failed for '{stale.FullName}'");
                // Best-effort cleanup; stale backups are non-fatal.
            }
        }
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly string _key;
        private FileStream? _stream;

        public LockHandle(string key, FileStream stream)
        {
            _key = key;
            _stream = stream;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            finally
            {
                ActiveLockStreams.TryRemove(_key, out _);
            }
        }
    }
}
