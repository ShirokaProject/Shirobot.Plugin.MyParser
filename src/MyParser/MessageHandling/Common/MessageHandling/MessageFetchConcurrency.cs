namespace Shirobot.Plugin.MyParser.MessageHandling;

internal static class MessageFetchConcurrency
{
    public const int DefaultImageConcurrency = 6;

    public static async Task<IReadOnlyList<TResult>> SelectParallelOrderedAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxConcurrency,
        Func<TSource, Task<TResult>> selector)
    {
        var items = source.ToArray();
        if (items.Length == 0)
        {
            return Array.Empty<TResult>();
        }

        maxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        var results = new TResult[items.Length];
        using var throttler = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(async (item, index) =>
        {
            await throttler.WaitAsync();
            try
            {
                results[index] = await selector(item);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }
}
