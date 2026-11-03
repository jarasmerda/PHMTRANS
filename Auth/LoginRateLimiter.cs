using System.Collections.Concurrent;

namespace PHMTRANS.Auth;

public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    public bool TryAcquire(string key, int permitPerMinute, DateTimeOffset now)
    {
        var limit = Math.Max(1, permitPerMinute);
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var window = TimeSpan.FromMinutes(1);
            while (queue.Count > 0 && now - queue.Peek() >= window)
                queue.Dequeue();

            if (queue.Count >= limit)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }
}
