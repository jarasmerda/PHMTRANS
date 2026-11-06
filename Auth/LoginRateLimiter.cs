using System.Collections.Concurrent;

namespace PHMTRANS.Auth;

public sealed class LoginRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    public bool TryAcquire(string key, int permitPerMinute, DateTimeOffset now)
    {
        Cleanup(now);

        var limit = Math.Max(1, permitPerMinute);
        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
                queue.Dequeue();

            if (queue.Count >= limit)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }

    // Jednou za minutu zahodí adresy bez pokusu v posledním okně, aby slovník nerostl donekonečna.
    private void Cleanup(DateTimeOffset now)
    {
        if (now < _nextCleanup)
            return;
        _nextCleanup = now + Window;

        foreach (var (key, queue) in _hits)
        {
            lock (queue)
            {
                if (queue.Count == 0 || now - queue.Last() >= Window)
                    _hits.TryRemove(new KeyValuePair<string, Queue<DateTimeOffset>>(key, queue));
            }
        }
    }
}
