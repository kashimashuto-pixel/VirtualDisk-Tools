namespace Qcow2Explorer.Core;

public interface IDiskImageReader : IBlockReader, IDisposable
{
    string Path { get; }
    string FormatName { get; }

    IReadOnlyList<KeyValuePair<string, string>> GetHeaderRows();
    IReadOnlyList<string> GetWarnings();
    string DescribeOffset(long offset);
}
