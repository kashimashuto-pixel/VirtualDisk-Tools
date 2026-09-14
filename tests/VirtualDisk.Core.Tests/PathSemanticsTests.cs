using Qcow2Explorer.Core;

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

        if (OperatingSystem.IsWindows())
        {
            Assert(rawLower == rawUpper, "Windows raw cache paths ignore filename case");
            Assert(indexLower == indexUpper, "Windows index cache paths ignore filename case");
        }
        else
        {
            Assert(rawLower != rawUpper, "case-sensitive raw cache paths remain distinct");
            Assert(indexLower != indexUpper, "case-sensitive index cache paths remain distinct");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
