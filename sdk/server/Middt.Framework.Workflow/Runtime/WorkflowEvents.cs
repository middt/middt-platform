using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Model.Workflow;

namespace Middt.Framework.Workflow.Runtime;

public enum WorkflowEventType
{
    Started,
    StateChanged,
    Completed,
    Cancelled,
    Faulted,
}

/// <summary>Queued with the state change and delivered after commit; handlers may receive it again after failure or restart.</summary>
public sealed record WorkflowEvent(
    WorkflowEventType Type,
    Guid InstanceId,
    string Workflow,
    int WorkflowVersion,
    string? BusinessKey,
    string? FromState,
    string State,
    string? Transition,
    JsonObject Data,
    DateTime OccurredAt);

/// <summary>Observe workflow changes (notifications, integration events, projections). Register any number of handlers.</summary>
public interface IWorkflowEventHandler
{
    Task HandleAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken);
}

/// <summary>Notified when a runtime catalog (such as workflow definitions or AI agents) is updated on any node in the cluster.</summary>
public interface IWorkflowCatalogInvalidationHandler
{
    void OnCatalogInvalidated(string catalog);
}

/// <summary>
/// Optional database-agnostic cluster broadcast transport (e.g. RabbitMQ/Rebus or Redis Pub/Sub) used to propagate
/// job wakeups, real-time SSE events, and catalog invalidations across nodes when running on non-PostgreSQL databases
/// (SQL Server, MySQL, Oracle, SQLite).
/// </summary>
public interface IWorkflowClusterBus
{
    Task PublishAsync(string channel, string? payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pub/sub hub that broadcasts committed <see cref="WorkflowEvent"/> notifications to Server-Sent Events (SSE) subscribers
/// on this node and (via PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> in <see cref="WorkflowJobNotifier"/>) across all nodes sharing the database.
/// </summary>
public sealed class WorkflowEventStreamHub(
    IServiceScopeFactory? scopeFactory = null,
    WorkflowJobNotifier? jobNotifier = null) : IWorkflowEventHandler
{
    private readonly ConcurrentDictionary<Guid, Subscription> subscriptions = new();

    public async Task HandleAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);

        var model = new WorkflowEventStreamModel
        {
            Type = workflowEvent.Type.ToString(),
            InstanceId = workflowEvent.InstanceId,
            Workflow = workflowEvent.Workflow,
            WorkflowVersion = workflowEvent.WorkflowVersion,
            Key = workflowEvent.BusinessKey,
            FromState = workflowEvent.FromState,
            State = workflowEvent.State,
            Transition = workflowEvent.Transition,
            OccurredAt = workflowEvent.OccurredAt,
        };

        PublishLocal(model);

        if (jobNotifier is not null && scopeFactory is not null)
        {
            await jobNotifier.NotifyEventAsync(scopeFactory, model, cancellationToken);
        }
    }

    /// <summary>Broadcasts a workflow event stream model to SSE subscribers connected to this node.</summary>
    public void PublishLocal(WorkflowEventStreamModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (subscriptions.IsEmpty)
        {
            return;
        }

        foreach (var (_, sub) in subscriptions)
        {
            if (sub.Matches(model))
            {
                sub.Channel.Writer.TryWrite(model);
            }
        }
    }

    public async IAsyncEnumerable<WorkflowEventStreamModel> SubscribeAsync(
        string? workflow = null,
        Guid? instanceId = null,
        Func<string, bool>? canAccessWorkflow = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<WorkflowEventStreamModel>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var sub = new Subscription(workflow, instanceId, canAccessWorkflow, channel);
        subscriptions[id] = sub;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            subscriptions.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private sealed record Subscription(
        string? WorkflowFilter,
        Guid? InstanceIdFilter,
        Func<string, bool>? CanAccessWorkflow,
        Channel<WorkflowEventStreamModel> Channel)
    {
        public bool Matches(WorkflowEventStreamModel ev)
        {
            if (!string.IsNullOrWhiteSpace(WorkflowFilter) &&
                !string.Equals(WorkflowFilter, ev.Workflow, StringComparison.Ordinal))
            {
                return false;
            }

            if (InstanceIdFilter.HasValue && InstanceIdFilter.Value != ev.InstanceId)
            {
                return false;
            }

            if (CanAccessWorkflow is not null && !CanAccessWorkflow(ev.Workflow))
            {
                return false;
            }

            return true;
        }
    }
}

/// <summary>Wakes the job processor when new work is due immediately.</summary>
public sealed class WorkflowJobSignal
{
    private readonly System.Threading.Channels.Channel<bool> channel =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
        });

    public void Notify() => channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // polling interval elapsed
        }
    }
}
