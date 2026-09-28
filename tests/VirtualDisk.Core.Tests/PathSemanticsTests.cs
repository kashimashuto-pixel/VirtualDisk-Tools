using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

internal static class PathSemanticsTests
{
    public static void Run(string directory)
    {
        var lowerPath = Path.Combine(directory, "case-sensitive-source.lzo");
        var upperPath = Path.Combine(directory, "CASE-SENSITIVE-SOURCE.LZO");
        var rawLower = LzopRawCacheManager.GetCacheDirectory(directory, lowerPath);
        var rawUpper = LzopRawCacheManager.GetCacheDirectory(directory, upperPath);
        var indexLower = LzopIndexCacheManager.GetCachePath(lowerPath);
        var indexUpper = LzopIndexCacheManager.GetCachePath(upperPath);
        using var contentStore = new PendingEditContentStore(directory);
        var differentlyCasedStoreChild = Path.Combine(contentStore.RootPath.ToUpperInvariant(), "ITEM.BIN");

        if (OperatingSystem.IsWindows())
        {
            Assert(rawLower == rawUpper, "Windows raw cache paths ignore filename case");
            Assert(indexLower == indexUpper, "Windows index cache paths ignore filename case");
            Assert(contentStore.OwnsPath(differentlyCasedStoreChild), "Windows owned paths ignore filename case");
        }
        else
        {
            Assert(rawLower != rawUpper, "case-sensitive raw cache paths remain distinct");
            Assert(indexLower != indexUpper, "case-sensitive index cache paths remain distinct");
            Assert(!contentStore.OwnsPath(differentlyCasedStoreChild), "case-sensitive owned paths remain distinct");
        }

        Assert(
            !contentStore.OwnsPath(contentStore.RootPath + "-sibling/item.bin"),
            "owned path check rejects prefix siblings");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
