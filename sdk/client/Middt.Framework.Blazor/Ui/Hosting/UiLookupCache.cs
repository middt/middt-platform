using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Middt.Framework.Client.Http;

namespace Middt.Framework.Blazor.Ui;

/// <summary>
/// Per-circuit cache of lookup lists (<c>getall</c>) so several selects of one page share a request.
/// The shared load runs with the cache's own token (cancelled when the circuit's scope is disposed), never with a caller's:
/// a caller that goes away only stops waiting (<see cref="Task.WaitAsync(CancellationToken)"/>) and cannot cancel the load
/// for the others. Failed or cancelled loads are not cached.
/// </summary>
public sealed class UiLookupCache(UiDataClient client, TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ItemEntry> items = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource disposed = new();

    public Task<List<JsonObject>> GetAllAsync(string entity, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (!entries.TryGetValue(entity, out var entry) || now - entry.LoadedAt >= Duration || entry.Items.IsFaulted || entry.Items.IsCanceled)
        {
            var load = LoadAsync(entity);
            entry = new Entry(now, load);
            entries[entity] = entry;

            // A failed or cancelled load is removed at once (only this entry, not one that already replaced it).
            var added = entry;
            _ = load.ContinueWith(
                _ => entries.TryRemove(new KeyValuePair<string, Entry>(entity, added)),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return entry.Items.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// One row of <paramref name="entity"/> by <paramref name="id"/> (<c>getbyid</c>, e.g. the text of a stored picker value);
    /// <c>null</c> when it does not exist. Shared by every editor of the circuit for <see cref="Duration"/>; failed or
    /// cancelled loads are not cached.
    /// </summary>
    public Task<JsonObject?> GetByIdAsync(string entity, string id, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{entity}\0{id}";
        var now = timeProvider.GetUtcNow();
        if (!items.TryGetValue(cacheKey, out var entry) || now - entry.LoadedAt >= Duration || entry.Item.IsFaulted || entry.Item.IsCanceled)
        {
            var load = LoadItemAsync(entity, id);
            entry = new ItemEntry(now, load);
            items[cacheKey] = entry;

            var added = entry;
            _ = load.ContinueWith(
                _ => items.TryRemove(new KeyValuePair<string, ItemEntry>(cacheKey, added)),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return entry.Item.WaitAsync(cancellationToken);
    }

    private async Task<List<JsonObject>> LoadAsync(string entity)
    {
        var response = await client.GetAllAsync(entity, disposed.Token).ConfigureAwait(false);
        return response.IsSuccess ? response.Data ?? [] : throw new InvalidOperationException(response.ErrorText);
    }

    private async Task<JsonObject?> LoadItemAsync(string entity, string id)
    {
        var response = await client.GetByIdAsync(entity, id, disposed.Token).ConfigureAwait(false);
        return response.IsSuccess ? response.Data : throw new InvalidOperationException(response.ErrorText);
    }

    public void Dispose()
    {
        disposed.Cancel();
        disposed.Dispose();
    }

    private sealed record Entry(DateTimeOffset LoadedAt, Task<List<JsonObject>> Items);

    private sealed record ItemEntry(DateTimeOffset LoadedAt, Task<JsonObject?> Item);
}
