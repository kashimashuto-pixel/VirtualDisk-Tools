using DiscUtils;
using DiscUtils.Streams;

namespace Qcow2Explorer.Core;

/// <summary>DiscUtils must use this adapter for every parent/extent existence check and open.</summary>
internal sealed class RestrictedImageFileSystem(ImageFileAccessPolicy policy, string imageDirectory) : DiscFileSystem
{
    private readonly List<SparseStream> _openedStreams = [];
    private int _openCount;
    internal string GetHostPath(string path) => Resolve(path);
    private string Resolve(string path)
    {
        return policy.ResolveReference(imageDirectory, path);
    }

    public override string FriendlyName => "Restricted local image files";
    public override bool CanWrite => false;
    public override Stream RawStream => throw new NotSupportedException();
    public override long Size => 0;
    public override long UsedSpace => 0;
    public override long AvailableSpace => 0;
    public override bool DirectoryExists(string path) => Directory.Exists(Resolve(path));
    public override bool FileExists(string path) => File.Exists(Resolve(path));
    public override SparseStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        if (mode != FileMode.Open || access != FileAccess.Read)
        {
            throw new UnauthorizedAccessException("イメージの関連ファイルは読み取り専用です。");
        }

        if (Interlocked.Increment(ref _openCount) > 4096)
        {
            throw new InvalidDataException("関連ファイルのオープン回数が上限を超えました。循環参照の可能性があります。");
        }
        var stream = SparseStream.FromStream(new FileStream(Resolve(path), mode, access, FileShare.Read), Ownership.Dispose);
        _openedStreams.Add(stream);
        return stream;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var stream in _openedStreams) stream.Dispose();
            _openedStreams.Clear();
        }
        base.Dispose(disposing);
    }

    public override FileAttributes GetAttributes(string path) => File.GetAttributes(Resolve(path));
    public override DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(Resolve(path));
    public override DateTime GetLastAccessTimeUtc(string path) => File.GetLastAccessTimeUtc(Resolve(path));
    public override DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(Resolve(path));
    public override long GetFileLength(string path) => new FileInfo(Resolve(path)).Length;
    // Disk readers don't require directory enumeration. Do not expose a host filesystem browser.
    public override IEnumerable<string> GetDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public override IEnumerable<string> GetFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public override IEnumerable<string> GetFileSystemEntries(string path) => throw new NotSupportedException();
    public override IEnumerable<string> GetFileSystemEntries(string path, string pattern) => throw new NotSupportedException();
    public override IEnumerable<string> GetFileSystemEntries(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public override void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public override void CreateDirectory(string path) => throw new NotSupportedException();
    public override void DeleteDirectory(string path) => throw new NotSupportedException();
    public override void DeleteFile(string path) => throw new NotSupportedException();
    public override void MoveDirectory(string source, string destination) => throw new NotSupportedException();
    public override void MoveFile(string source, string destination, bool overwrite) => throw new NotSupportedException();
    public override void SetAttributes(string path, FileAttributes value) => throw new NotSupportedException();
    public override void SetCreationTimeUtc(string path, DateTime value) => throw new NotSupportedException();
    public override void SetLastAccessTimeUtc(string path, DateTime value) => throw new NotSupportedException();
    public override void SetLastWriteTimeUtc(string path, DateTime value) => throw new NotSupportedException();
}
