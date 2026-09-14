namespace Qcow2Explorer.Core;

public sealed record DiskImageProgress(string Message, long Completed = 0, long Total = 0)
{
    public int? Percentage => Total > 0
        ? (int)Math.Clamp((double)Completed / Total * 100, 0, 100)
        : null;
}
