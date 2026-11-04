using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Persistence;
using Npgsql;

namespace Middt.Framework.Workflow.Runtime;

/// <summary>
/// Wakes the job processors of every node when jobs are written and broadcasts real-time SSE events and catalog invalidations
/// across nodes. On PostgreSQL a <c>NOTIFY</c> reaches the <see cref="WorkflowJobListener"/> of all processes sharing the
/// database; elsewhere only the local <see cref="WorkflowJobSignal"/> is set and other nodes fall back to polling.
/// </summary>
public sealed partial class WorkflowJobNotifier(
    WorkflowJobSignal signal,
    IOptions<MiddtWorkflowOptions> options,
    ILogger<WorkflowJobNotifier> logger,
    IWorkflowClusterBus? clusterBus = null)
{
    /// <summary>Unique identifier of this runtime node/pod so self-originated notifications are not processed twice.</summary>
    public string NodeId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The <c>LISTEN</c>/<c>NOTIFY</c> channel for background jobs; one per workflow schema.</summary>
    public string Channel { get; } = ChannelFor(options.Value.Schema);

    /// <summary>The <c>LISTEN</c>/<c>NOTIFY</c> channel for cross-pod real-time SSE workflow events.</summary>
    public string EventChannel { get; } = EventChannelFor(options.Value.Schema);

    /// <summary>The <c>LISTEN</c>/<c>NOTIFY</c> channel for cross-pod catalog cache invalidations (workflows, agents).</summary>
    public string CatalogChannel { get; } = CatalogChannelFor(options.Value.Schema);

    internal static string ChannelFor(string? schema) =>
        "middt_wf_jobs_" + NormalizeSchema(schema);

    internal static string EventChannelFor(string? schema) =>
        "middt_wf_events_" + NormalizeSchema(schema);

    internal static string CatalogChannelFor(string? schema) =>
        "middt_wf_catalog_" + NormalizeSchema(schema);

    private static string NormalizeSchema(string? schema) =>
        Regex.Replace(string.IsNullOrEmpty(schema) ? "public" : schema, "[^A-Za-z0-9_]", "_").ToLowerInvariant();

    /// <summary>Wakes the local processor and, on PostgreSQL or via <see cref="IWorkflowClusterBus"/>, every other node. Call after the jobs are committed.</summary>
    public async Task NotifyAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        signal.Notify();
        if (clusterBus is not null)
        {
            try
            {
                await clusterBus.PublishAsync(Channel, NodeId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotifyFailed(logger, ex);
            }
        }

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, '')", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Other nodes still poll; a lost wake-up only costs latency.
            LogNotifyFailed(logger, ex);
        }
    }

    /// <summary>Broadcasts a committed <see cref="WorkflowEventStreamModel"/> to SSE subscribers on other nodes via PostgreSQL <c>NOTIFY</c> and/or <see cref="IWorkflowClusterBus"/>.</summary>
    public async Task NotifyEventAsync(IServiceScopeFactory scopeFactory, WorkflowEventStreamModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(model);

        var payload = JsonSerializer.Serialize(new ClusterEventEnvelope(NodeId, model), WorkflowJson.Options);
        if (clusterBus is not null)
        {
            try
            {
                await clusterBus.PublishAsync(EventChannel, payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotifyFailed(logger, ex);
            }
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<WorkflowDbContext>();
            if (db is null || db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            await db.Database.ExecuteSqlAsync($"SELECT pg_notify({EventChannel}, {payload})", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(logger, ex);
        }
    }

    /// <summary>Broadcasts a catalog invalidation signal (<c>"workflows"</c>, <c>"agents"</c>, etc.) to all other nodes via PostgreSQL <c>NOTIFY</c> and/or <see cref="IWorkflowClusterBus"/>.</summary>
    public async Task NotifyCatalogAsync(WorkflowDbContext db, string catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);

        var payload = JsonSerializer.Serialize(new ClusterCatalogEnvelope(NodeId, catalog), WorkflowJson.Options);
        if (clusterBus is not null)
        {
            try
            {
                await clusterBus.PublishAsync(CatalogChannel, payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotifyFailed(logger, ex);
            }
        }

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_notify({CatalogChannel}, {payload})", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(logger, ex);
        }
    }

    internal sealed record ClusterEventEnvelope(string NodeId, WorkflowEventStreamModel? Event);

    internal sealed record ClusterCatalogEnvelope(string NodeId, string Catalog);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow NOTIFY failed; other nodes fall back to polling")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception);
}

/// <summary>
/// PostgreSQL only: <c>LISTEN</c>s on <see cref="WorkflowJobNotifier.Channel"/>, <see cref="WorkflowJobNotifier.EventChannel"/>,
/// and <see cref="WorkflowJobNotifier.CatalogChannel"/> over a dedicated connection so background jobs, SSE events, and runtime
/// catalog changes propagate immediately across all nodes sharing the database.
/// </summary>
public sealed partial class WorkflowJobListener(
    IServiceScopeFactory scopeFactory,
    WorkflowJobNotifier notifier,
    WorkflowJobSignal signal,
    ILogger<WorkflowJobListener> logger,
    WorkflowEventStreamHub? eventHub = null,
    WorkflowRegistry? registry = null,
    IEnumerable<IWorkflowCatalogInvalidationHandler>? catalogHandlers = null) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    /// <summary>Set once the first <c>LISTEN</c> succeeded (tests wait for it).</summary>
    public Task Listening => listening.Task;

    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IWorkflowCatalogInvalidationHandler[] handlers = catalogHandlers?.ToArray() ?? [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? connectionString;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            {
                return;
            }

            connectionString = db.Database.GetConnectionString();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) => HandleNotification(e.Channel, e.Payload);
                await using (var listen = new NpgsqlCommand(
                    $"LISTEN \"{notifier.Channel}\"; LISTEN \"{notifier.EventChannel}\"; LISTEN \"{notifier.CatalogChannel}\"; LISTEN \"middt_ui_layouts\";",
                    connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                listening.TrySetResult();
                signal.Notify(); // jobs written while we were not listening
                InvalidateAllCatalogs();
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogListenFailed(logger, ex);
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Routes an incoming message from an external <see cref="IWorkflowClusterBus"/> into the local listener pipeline.</summary>
    public void ReceiveClusterMessage(string channel, string? payload) =>
        HandleNotification(channel, payload);

    internal void HandleNotification(string channel, string? payload)
    {
        if (string.Equals(channel, notifier.Channel, StringComparison.Ordinal))
        {
            if (!string.Equals(payload, notifier.NodeId, StringComparison.Ordinal))
            {
                signal.Notify();
            }

            return;
        }

        if (string.Equals(channel, "middt_ui_layouts", StringComparison.Ordinal))
        {
            var uiCatalog = string.IsNullOrWhiteSpace(payload) ? "ui" : $"ui:{payload}";
            foreach (var handler in handlers)
            {
                handler.OnCatalogInvalidated(uiCatalog);
            }

            return;
        }

        if (string.Equals(channel, notifier.EventChannel, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(payload) || eventHub is null)
            {
                return;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<WorkflowJobNotifier.ClusterEventEnvelope>(payload, WorkflowJson.Options);
                if (envelope?.Event is not null && !string.Equals(envelope.NodeId, notifier.NodeId, StringComparison.Ordinal))
                {
                    eventHub.PublishLocal(envelope.Event);
                }
            }
            catch
            {
                // Ignore malformed notification payloads.
            }

            return;
        }

        if (string.Equals(channel, notifier.CatalogChannel, StringComparison.Ordinal))
        {
            var catalog = "*";
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<WorkflowJobNotifier.ClusterCatalogEnvelope>(payload, WorkflowJson.Options);
                    if (envelope is not null)
                    {
                        if (string.Equals(envelope.NodeId, notifier.NodeId, StringComparison.Ordinal))
                        {
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(envelope.Catalog))
                        {
                            catalog = envelope.Catalog;
                        }
                    }
                }
                catch
                {
                    // Fallback to full invalidation.
                }
            }

            if (string.Equals(catalog, "*", StringComparison.Ordinal) || string.Equals(catalog, "workflows", StringComparison.OrdinalIgnoreCase))
            {
                registry?.InvalidateCache();
            }

            foreach (var handler in handlers)
            {
                handler.OnCatalogInvalidated(catalog);
            }
        }
    }

    private void InvalidateAllCatalogs()
    {
        registry?.InvalidateCache();
        foreach (var handler in handlers)
        {
            handler.OnCatalogInvalidated("*");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow LISTEN connection failed; reconnecting")]
    private static partial void LogListenFailed(ILogger logger, Exception exception);
}
