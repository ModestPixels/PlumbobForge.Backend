using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlumbobForge.Backend.Services;

public class NotificationService
{
    private readonly ConcurrentDictionary<Guid, Func<string, Task>> _subscribers = new();

    public Guid Subscribe(Func<string, Task> handler)
    {
        var id = Guid.NewGuid();
        _subscribers.TryAdd(id, handler);
        return id;
    }

    public void Unsubscribe(Guid id)
    {
        _subscribers.TryRemove(id, out _);
    }

    public async Task BroadcastAsync(string eventName, object data)
    {
        var json = JsonSerializer.Serialize(new { eventName, data });
        var payload = $"data: {json}\n\n";

        var subscribers = _subscribers.Values.ToList();
        if (subscribers.Count == 0) return;

        var tasks = subscribers.Select(async subscriber =>
        {
            try
            {
                await subscriber(payload);
            }
            catch
            {
                // Ignore disconnected subscribers
            }
        });

        await Task.WhenAll(tasks);
    }
}
