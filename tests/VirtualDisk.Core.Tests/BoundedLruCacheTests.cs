using Qcow2Explorer.Core;

internal static class BoundedLruCacheTests
{
    public static void Run()
    {
        TestLeastRecentlyUsedEviction();
        TestOversizedItemBypass();
        TestConcurrentAccess();
    }

    private static void TestLeastRecentlyUsedEviction()
    {
        var cache = new BoundedLruCache<string, byte[]>(10, value => value.LongLength);
        _ = cache.AddOrGetExisting("first", new byte[6]);
        _ = cache.AddOrGetExisting("second", new byte[4]);
        Assert(cache.TryGetValue("first", out _), "LRU hit promotes entry");
        _ = cache.AddOrGetExisting("third", new byte[4]);

        Assert(cache.TryGetValue("first", out var first) && first.Length == 6, "recent LRU entry retained");
        Assert(!cache.TryGetValue("second", out _), "least-recent LRU entry evicted");
        Assert(cache.TryGetValue("third", out var third) && third.Length == 4, "new LRU entry retained");
        Assert(cache.Count == 2 && cache.TotalSize == 10, "LRU cache size accounting");
    }

    private static void TestOversizedItemBypass()
    {
        var cache = new BoundedLruCache<string, byte[]>(4, value => value.LongLength);
        var oversized = new byte[5];
        Assert(ReferenceEquals(cache.AddOrGetExisting("large", oversized), oversized), "oversized cache value returned");
        Assert(cache.Count == 0 && cache.TotalSize == 0, "oversized cache value bypassed");
    }

    private static void TestConcurrentAccess()
    {
        var cache = new BoundedLruCache<int, byte[]>(64, value => value.LongLength);
        Parallel.For(0, 10_000, index =>
        {
            var key = index % 128;
            var value = cache.AddOrGetExisting(key, [checked((byte)key)]);
            Assert(value[0] == checked((byte)key), "concurrent LRU value integrity");
            _ = cache.TryGetValue(key, out _);
        });

        Assert(cache.Count <= 64, "concurrent LRU entry bound");
        Assert(cache.TotalSize <= 64, "concurrent LRU byte bound");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
