using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Qcow2Explorer.Core;

public static class DiagnosticLog
{
    private const int MaximumEntries = 20_000;
    private static readonly object Sync = new();
    private static readonly ConcurrentQueue<string> Entries = new();
    private static StreamWriter? _writer;

    public static event EventHandler<string>? EntryAdded;
    public static string? LogPath { get; private set; }

    public static IReadOnlyList<string> Snapshot() => Entries.ToArray();

    public static void Initialize()
    {
        Exception? failure = null;
        lock (Sync)
        {
            if (_writer is not null)
            {
                return;
            }

            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VirtualDiskExplorer",
                    "Logs");
                Directory.CreateDirectory(directory);
                LogPath = Path.Combine(directory, $"diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
                _writer = new StreamWriter(LogPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (IsRecoverableLoggingFailure(ex))
            {
                _writer = null;
                LogPath = null;
                failure = ex;
            }
        }

        if (failure is not null)
        {
            TraceSafely($"Diagnostic file logging could not start: {failure}");
        }

        Write(failure is null
            ? $"Diagnostic logging started: path={LogPath}"
            : $"Diagnostic logging started without a file: error={failure.Message}");
    }

    public static void Write(string message)
    {
        var entry = $"{DateTime.UtcNow:O} {message}";
        Entries.Enqueue(entry);
        while (Entries.Count > MaximumEntries && Entries.TryDequeue(out _))
        {
        }

        Exception? writeFailure = null;
        lock (Sync)
        {
            try
            {
                _writer?.WriteLine(entry);
            }
            catch (Exception ex) when (IsRecoverableLoggingFailure(ex))
            {
                writeFailure = ex;
                TryDisposeWriter();
            }
        }

        if (writeFailure is not null)
        {
            TraceSafely($"Diagnostic file logging stopped after a write failure: {writeFailure}");
        }

        TraceSafely(entry);
        var subscribers = EntryAdded;
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<string> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(null, entry);
            }
            catch (Exception ex)
            {
                TraceSafely($"Diagnostic log subscriber failed: {ex}");
            }
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            TryDisposeWriter();
        }
    }

    private static void TryDisposeWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (IsRecoverableLoggingFailure(ex))
        {
            TraceSafely($"Diagnostic log close failed: {ex}");
        }
        finally
        {
            _writer = null;
        }
    }

    private static bool IsRecoverableLoggingFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    private static void TraceSafely(string message)
    {
        try
        {
            Trace.WriteLine(message);
        }
        catch
        {
            // Diagnostic output must never stop the operation being diagnosed.
        }
    }
}
