using System.Collections.Concurrent;
using Qcow2Explorer.Core;

namespace Qcow2Explorer;

public sealed class DiagnosticLogForm : Form
{
    private const int MaximumEntriesPerUpdate = 400;
    private readonly ConcurrentQueue<string> _pendingEntries = new();
    private int _updateScheduled;

    private readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9)
    };

    public DiagnosticLogForm()
    {
        Text = "Virtual Disk Explorer - Diagnostic Log";
        Width = 920;
        Height = 420;
        Controls.Add(_text);
        _text.Lines = DiagnosticLog.Snapshot().ToArray();
        DiagnosticLog.EntryAdded += OnEntryAdded;
        FormClosed += (_, _) => DiagnosticLog.EntryAdded -= OnEntryAdded;
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Hide();
    }

    private void OnEntryAdded(object? sender, string entry)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _pendingEntries.Enqueue(entry);
        if (Interlocked.Exchange(ref _updateScheduled, 1) != 0)
        {
            return;
        }

        BeginInvoke(DrainPendingEntries);
    }

    private void DrainPendingEntries()
    {
        var processed = 0;
        while (processed < MaximumEntriesPerUpdate
            && _pendingEntries.TryDequeue(out var pendingEntry))
        {
            _text.AppendText(pendingEntry + Environment.NewLine);
            processed++;
        }

        _text.SelectionStart = _text.TextLength;
        _text.ScrollToCaret();
        Volatile.Write(ref _updateScheduled, 0);

        if (!_pendingEntries.IsEmpty
            && !IsDisposed
            && IsHandleCreated
            && Interlocked.Exchange(ref _updateScheduled, 1) == 0)
        {
            BeginInvoke(DrainPendingEntries);
        }
    }
}
