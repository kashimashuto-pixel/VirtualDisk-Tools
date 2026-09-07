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
        lock (Sync)
        {
            if (_writer is not null)
            {
                return;
            }

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VirtualDiskExplorer",
                "Logs");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(LogPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        }

        Write($"Diagnostic logging started: path={LogPath}");
    }

    public static void Write(string message)
    {
        var entry = $"{DateTime.UtcNow:O} {message}";
        Entries.Enqueue(entry);
        while (Entries.Count > MaximumEntries && Entries.TryDequeue(out _))
        {
        }

        lock (Sync)
        {
            _writer?.WriteLine(entry);
        }

        Trace.WriteLine(entry);
        try
        {
            EntryAdded?.Invoke(null, entry);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Diagnostic log subscriber failed: {ex}");
        }
    }

    public static void Dispose()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}