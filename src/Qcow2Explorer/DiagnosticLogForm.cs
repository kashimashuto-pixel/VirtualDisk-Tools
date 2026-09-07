using Qcow2Explorer.Core;

namespace Qcow2Explorer;

public sealed class DiagnosticLogForm : Form
{
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

        BeginInvoke(() =>
        {
            _text.AppendText(entry + Environment.NewLine);
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        });
    }
}