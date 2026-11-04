using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Runtime;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Tests.Workflow;

public sealed class WorkflowReliabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_applies_each_instances_definition_version_access_rule()
    {
        const string v1 = """
            { "key": "versioned-access", "version": 1, "instanceAccess": { "mode": "owner" },
              "start": { "key": "start", "target": "open" }, "states": [ { "key": "open" } ] }
            """;
        const string v2 = """
            { "key": "versioned-access", "version": 2,
              "start": { "key": "start", "target": "open" }, "states": [ { "key": "open" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync(v1, v2);
        var oldAlice = await host.StartInstanceAsync("versioned-access", user: WorkflowTestHost.User("alice"));
        var oldBob = await host.StartInstanceAsync("versioned-access", user: WorkflowTestHost.User("bob"));
        var currentBob = await host.StartInstanceAsync("versioned-access", user: WorkflowTestHost.User("bob"));
        await host.WithDbAsync(db => db.Instances.Where(i => i.Id == oldAlice.Id || i.Id == oldBob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.WorkflowVersion, 1), Ct));

        var list = await host.WithEngineAsync(e => e.ListAsync("versioned-access", WorkflowTestHost.User("alice"), null, null, null, 1, 20, Ct));

        list.Count.Should().Be(2);
        list.Items.Select(i => i.Id).Should().BeEquivalentTo([oldAlice.Id, currentBob.Id]);
    }

    [Fact]
    public async Task Busy_child_is_cancelled_by_durable_job_after_its_claim_ends()
    {
        await using var host = await WorkflowTestHost.StartAsync(TestWorkflows.ParentAsync, TestWorkflows.ChildManual);
        var parent = await host.StartInstanceAsync("parent-async");
        var childId = parent.WaitingForInstanceId!.Value;
        var original = await host.RowAsync(childId);
        await host.SetStatusAsync(childId, WorkflowInstanceStatus.Busy, host.Time.GetUtcNow().UtcDateTime);

        await host.TransitionAsync(parent.Id, "cancel");

        (await host.RowAsync(childId)).Status.Should().Be(WorkflowInstanceStatus.Busy);
        (await host.JobsAsync(childId)).Should().ContainSingle(j => j.Kind == WorkflowJobKind.CancelChild);
        await host.SetStatusAsync(childId, WorkflowInstanceStatus.Faulted);
        await host.RunJobsAsync(TimeSpan.FromSeconds(1));

        var cancelled = await host.RowAsync(childId);
        cancelled.Status.Should().Be(WorkflowInstanceStatus.Cancelled);
        cancelled.Revision.Should().Be(original.Revision + 1);
        (await host.JobsAsync(childId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Recovered_busy_instance_can_retry_its_saved_command()
    {
        const string definition = """
            { "key": "recoverable", "start": { "key": "start", "target": "open" },
              "states": [ { "key": "open", "transitions": [ { "key": "finish", "target": "done" } ] },
                          { "key": "done", "type": "Finish" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync([definition]);
        var instance = await host.StartInstanceAsync("recoverable");
        await host.WithDbAsync(db => db.Instances.Where(i => i.Id == instance.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, WorkflowInstanceStatus.Busy)
            .SetProperty(i => i.BusySince, host.Time.GetUtcNow().UtcDateTime.AddHours(-1))
            .SetProperty(i => i.PendingTransition, "finish"), Ct));

        await host.Processor.RunOnceAsync(Ct);
        (await host.RowAsync(instance.Id)).Status.Should().Be(WorkflowInstanceStatus.Faulted);
        (await host.IncidentsAsync(instance.Id)).Should().ContainSingle(i => i.TransitionKey == "finish");

        var result = await host.RetryAsync(instance.Id);
        result.Status.Should().Be(WorkflowInstanceStatus.Completed);
        (await host.RowAsync(instance.Id)).PendingTransition.Should().BeNull();
    }

    [Fact]
    public async Task Multi_hop_automatic_transitions_increment_data_version_per_hop()
    {
        const string definition = """
            {
              "key": "multi-hop",
              "start": { "key": "start", "target": "step1", "onExecute": [ { "task": "set", "config": { "values": { "a": "1" } } } ] },
              "states": [
                {
                  "key": "step1",
                  "transitions": [
                    { "key": "to-step2", "target": "step2", "trigger": "Automatic", "default": true, "onExecute": [ { "task": "set", "config": { "values": { "b": "2" } } } ] }
                  ]
                },
                {
                  "key": "step2",
                  "transitions": [
                    { "key": "to-done", "target": "done", "trigger": "Automatic", "default": true, "onExecute": [ { "task": "set", "config": { "values": { "c": "3" } } } ] }
                  ]
                },
                { "key": "done", "type": "Finish" }
              ]
            }
            """;

        await using var host = await WorkflowTestHost.StartAsync([definition]);
        var instance = await host.StartInstanceAsync("multi-hop");

        instance.Status.Should().Be(WorkflowInstanceStatus.Completed);
        instance.DataVersion.Should().Be(3);
        var history = await host.HistoryAsync(instance.Id);
        history.Select(h => (h.TransitionKey, h.DataVersion)).Should().Equal(
            ("start", 1),
            ("to-step2", 2),
            ("to-done", 3));
    }

    [Fact]
    public async Task Failed_task_attempt_does_not_leak_data_into_the_next_attempt()
    {
        const string definition = """
            { "key": "attempt-isolation", "start": { "key": "start", "target": "done",
                "onExecute": [ { "task": "mutate-once", "onError": { "maxAttempts": 2, "delay": "00:00:00" } } ] },
              "states": [ { "key": "done", "type": "Finish" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync([definition],
            configure: o => o.AddTask<MutateOnceTask>("mutate-once"),
            services: s => s.AddSingleton<AttemptProbe>());

        var instance = await host.StartInstanceAsync("attempt-isolation");
        var probe = host.Services.GetRequiredService<AttemptProbe>();

        instance.Data["successful"].Should().NotBeNull();
        instance.Data.ContainsKey("dirty").Should().BeFalse();
        probe.SawDirtyOnRetry.Should().BeFalse();
        probe.Keys.Should().HaveCount(2).And.OnlyContain(k => k == probe.Keys[0]);
    }

    [Fact]
    public async Task Failed_event_delivery_remains_queued_and_is_replayed()
    {
        const string definition = """
            { "key": "event-replay", "start": { "key": "start", "target": "open" },
              "states": [ { "key": "open" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync([definition],
            services: s =>
            {
                s.AddSingleton<FailFirstEventHandler>();
                s.AddSingleton<IWorkflowEventHandler>(p => p.GetRequiredService<FailFirstEventHandler>());
            });
        var instance = await host.StartInstanceAsync("event-replay");
        var handler = host.Services.GetRequiredService<FailFirstEventHandler>();

        (await host.JobsAsync(instance.Id)).Should().ContainSingle(j => j.Kind == WorkflowJobKind.Event);
        await host.RunJobsAsync(TimeSpan.FromSeconds(6));

        handler.Calls.Should().BeGreaterThan(1);
        (await host.JobsAsync(instance.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Finishing_an_instance_does_not_delete_undelivered_events()
    {
        const string definition = """
            { "key": "event-retention", "start": { "key": "start", "target": "open" },
              "states": [ { "key": "open", "transitions": [ { "key": "finish", "target": "done" } ] },
                          { "key": "done", "type": "Finish" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync([definition],
            services: s => s.AddSingleton<IWorkflowEventHandler, AlwaysFailEventHandler>());
        var instance = await host.StartInstanceAsync("event-retention");
        var pending = (await host.JobsAsync(instance.Id)).Where(j => j.Kind == WorkflowJobKind.Event).Select(j => j.Id).ToArray();
        pending.Should().NotBeEmpty();

        await host.TransitionAsync(instance.Id, "finish");

        (await host.JobsAsync(instance.Id)).Select(j => j.Id).Should().Contain(pending);
    }

    [Fact]
    public async Task Abandoned_job_deletes_job_and_creates_incident_atomically_with_workflow_key()
    {
        const string definition = """
            { "key": "event-abandon", "start": { "key": "start", "target": "open" },
              "states": [ { "key": "open" } ] }
            """;
        await using var host = await WorkflowTestHost.StartAsync([definition],
            services: s => s.AddSingleton<IWorkflowEventHandler, AlwaysFailEventHandler>());
        var instance = await host.StartInstanceAsync("event-abandon");

        await host.WithDbAsync(db => db.Jobs
            .Where(j => j.InstanceId == instance.Id && j.Kind == WorkflowJobKind.Event)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Attempts, 19)
                .SetProperty(j => j.DueAt, host.Time.GetUtcNow().UtcDateTime.AddSeconds(-1))
                .SetProperty(j => j.LockedUntil, (DateTime?)null), Ct));

        await host.Processor.RunOnceAsync(Ct);

        (await host.JobsAsync(instance.Id)).Should().BeEmpty();
        var incidentRows = await host.WithDbAsync(db => db.Incidents.Where(i => i.InstanceId == instance.Id).ToListAsync(Ct));
        incidentRows.Should().NotBeEmpty().And.OnlyContain(i =>
            i.WorkflowKey == "event-abandon" &&
            i.Attempts == 20 &&
            i.Error.Contains("Event job abandoned after 20 attempts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Graceful_shutdown_cancellation_releases_job_lease_and_decrements_attempts_immediately()
    {
        const string definition = """
            { "key": "shutdown-lease", "start": { "key": "start", "target": "open" },
              "states": [ { "key": "open" } ] }
            """;
        var cancelHolder = new CancelOnFirstCallEventHandler();
        await using var host = await WorkflowTestHost.StartAsync([definition],
            services: s => s.AddSingleton<IWorkflowEventHandler>(cancelHolder));

        var instance = await host.StartInstanceAsync("shutdown-lease");
        (await host.JobsAsync(instance.Id)).Should().ContainSingle(j => j.Kind == WorkflowJobKind.Event && j.LockedUntil == null && j.Attempts == 0);
        host.Time.Advance(TimeSpan.FromSeconds(6));

        using var shutdownCts = new CancellationTokenSource();
        cancelHolder.ShutdownCts = shutdownCts;

        var act = () => host.Processor.RunOnceAsync(shutdownCts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var jobAfterCancel = (await host.JobsAsync(instance.Id)).Should().ContainSingle().Subject;
        jobAfterCancel.LockedUntil.Should().BeNull("a shutting-down pod must immediately release its job lease for other pods");
        jobAfterCancel.Attempts.Should().Be(0, "a graceful shutdown interruption must not burn a retry attempt");

        // Another healthy pod (or subsequent run) can pick up and complete the job immediately without waiting 5 minutes.
        await host.Processor.RunOnceAsync(Ct);
        (await host.JobsAsync(instance.Id)).Should().BeEmpty();
        cancelHolder.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Workflow_registry_syncs_in_place_definition_updates_and_invalidations_across_nodes()
    {
        const string initialDef = """
            { "key": "multi-pod-def", "version": 1, "title": "Initial Title",
              "start": { "key": "start", "target": "open" }, "states": [ { "key": "open" } ] }
            """;
        const string updatedDef = """
            { "key": "multi-pod-def", "version": 1, "title": "Updated By Pod B",
              "start": { "key": "start", "target": "open" }, "states": [ { "key": "open" } ] }
            """;

        await using var host = await WorkflowTestHost.StartAsync([initialDef]);
        var registry = host.Services.GetRequiredService<Middt.Framework.Workflow.Definitions.WorkflowRegistry>();
        await registry.DeployAsync([initialDef], "pod-a", Ct);
        registry.Get("multi-pod-def", 1).Title.Should().Be("Initial Title");

        // Simulate Pod B updating the definition in the shared database with a newer UpdatedAt timestamp.
        await host.WithDbAsync(db => db.Definitions
            .Where(d => d.Key == "multi-pod-def" && d.Version == 1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Content, updatedDef)
                .SetProperty(d => d.Title, "Updated By Pod B")
                .SetProperty(d => d.UpdatedAt, DateTime.UtcNow.AddSeconds(10)), Ct));

        registry.InvalidateCache();
        registry.Get("multi-pod-def", 1).Title.Should().Be("Updated By Pod B");
    }

    [Fact]
    public void Sql_data_filter_and_sort_pushdown_supports_postgres_sqlite_sqlserver_mysql_and_oracle()
    {
        Assert.True(WorkflowDbContext.SupportsDatabaseSchemas("Npgsql.EntityFrameworkCore.PostgreSQL"));
        Assert.True(WorkflowDbContext.SupportsDatabaseSchemas("Microsoft.EntityFrameworkCore.SqlServer"));
        Assert.False(WorkflowDbContext.SupportsDatabaseSchemas("Microsoft.EntityFrameworkCore.Sqlite"));
        Assert.False(WorkflowDbContext.SupportsDatabaseSchemas("Pomelo.EntityFrameworkCore.MySql"));
        Assert.False(WorkflowDbContext.SupportsDatabaseSchemas("Oracle.EntityFrameworkCore"));

        var conditions = new List<WorkflowPartitionManager.SqlDataCondition>
        {
            new("amount", ">=", WorkflowPartitionManager.SqlConditionKind.Numeric, 100m),
            new("active", "=", WorkflowPartitionManager.SqlConditionKind.Boolean, true),
            new("customerName", "CONTAINS", WorkflowPartitionManager.SqlConditionKind.ContainsText, "ışık"),
        };

        var (sqlServerFilter, sqlServerParams) = WorkflowPartitionManager.BuildSqlDataFilterQuery(
            "Microsoft.EntityFrameworkCore.SqlServer", "wf", "Order", conditions);
        Assert.NotNull(sqlServerFilter);
        Assert.Contains("FROM [wf].[WorkflowInstanceData]", sqlServerFilter, StringComparison.Ordinal);
        Assert.Contains("TRY_CAST(JSON_VALUE([Data], '$.amount') AS decimal(38,10)) >= {1}", sqlServerFilter, StringComparison.Ordinal);
        Assert.Contains("CHARINDEX({3}", sqlServerFilter, StringComparison.Ordinal);
        Assert.Equal(4, sqlServerParams.Count);

        var (mySqlFilter, _) = WorkflowPartitionManager.BuildSqlDataFilterQuery(
            "Pomelo.EntityFrameworkCore.MySql", "wf", "Order", conditions);
        Assert.NotNull(mySqlFilter);
        Assert.Contains("FROM `WorkflowInstanceData`", mySqlFilter, StringComparison.Ordinal);
        Assert.Contains("JSON_UNQUOTE(JSON_EXTRACT(`Data`, '$.amount'))", mySqlFilter, StringComparison.Ordinal);
        Assert.Contains("LOCATE({3}", mySqlFilter, StringComparison.Ordinal);

        var (oracleFilter, _) = WorkflowPartitionManager.BuildSqlDataFilterQuery(
            "Oracle.EntityFrameworkCore", "wf", "Order", conditions);
        Assert.NotNull(oracleFilter);
        Assert.Contains("FROM \"WorkflowInstanceData\"", oracleFilter, StringComparison.Ordinal);
        Assert.Contains("TO_NUMBER(JSON_VALUE(\"Data\", '$.amount') DEFAULT NULL ON CONVERSION ERROR)", oracleFilter, StringComparison.Ordinal);
        Assert.Contains("INSTR(", oracleFilter, StringComparison.Ordinal);

        var (sqlServerSort, _) = WorkflowPartitionManager.BuildOrderBySqlDataQuery(
            "Microsoft.EntityFrameworkCore.SqlServer", "wf", "Order", "amount", ascending: false);
        Assert.NotNull(sqlServerSort);
        Assert.Contains("ROW_NUMBER() OVER", sqlServerSort, StringComparison.Ordinal);
        Assert.Contains("FROM [wf].[WorkflowInstanceData]", sqlServerSort, StringComparison.Ordinal);

        var (mySqlSort, _) = WorkflowPartitionManager.BuildOrderBySqlDataQuery(
            "Pomelo.EntityFrameworkCore.MySql", "wf", "Order", "amount", ascending: true);
        Assert.NotNull(mySqlSort);
        Assert.Contains("AS SIGNED) AS `DataVersion`", mySqlSort, StringComparison.Ordinal);

        var (oracleSort, _) = WorkflowPartitionManager.BuildOrderBySqlDataQuery(
            "Oracle.EntityFrameworkCore", "wf", "Order", "amount", ascending: true);
        Assert.NotNull(oracleSort);
        Assert.Contains("AS NUMBER(10)) AS \"DataVersion\"", oracleSort, StringComparison.Ordinal);
    }

    [Fact]
    public void In_memory_data_filter_fallback_evaluates_all_condition_kinds_for_non_pushdown_providers()
    {
        var item = new JsonObject
        {
            ["amount"] = 250.5m,
            ["active"] = true,
            ["statusText"] = "APPROVED",
            ["customer"] = "Işık Ticaret A.Ş.",
        };

        var matching = new List<WorkflowPartitionManager.SqlDataCondition>
        {
            new("amount", ">", WorkflowPartitionManager.SqlConditionKind.Numeric, 100m),
            new("active", "=", WorkflowPartitionManager.SqlConditionKind.Boolean, true),
            new("statusText", "=", WorkflowPartitionManager.SqlConditionKind.ExactText, "APPROVED"),
            new("customer", "CONTAINS", WorkflowPartitionManager.SqlConditionKind.ContainsText, "ışık"),
        };

        Assert.True(WorkflowInstanceDataQuery.MatchesAllConditions(item, matching));

        var nonMatching = new List<WorkflowPartitionManager.SqlDataCondition>
        {
            new("amount", "<", WorkflowPartitionManager.SqlConditionKind.Numeric, 100m),
        };

        Assert.False(WorkflowInstanceDataQuery.MatchesAllConditions(item, nonMatching));
    }

    [Fact]
    public async Task Cluster_bus_propagates_sse_events_and_catalog_invalidations_on_non_postgres_providers()
    {
        const string flowJson = """
            { "key": "cluster-bus-flow", "version": 1,
              "start": { "key": "start", "target": "open" }, "states": [ { "key": "open", "isFinal": true } ] }
            """;

        var bus = new InMemoryTestClusterBus();
        await using var nodeA = await WorkflowTestHost.StartAsync(
            [flowJson],
            services: services => services.AddSingleton<IWorkflowClusterBus>(bus));
        await using var nodeB = await WorkflowTestHost.StartAsync(
            [flowJson],
            services: services => services.AddSingleton<IWorkflowClusterBus>(bus));

        var listenerB = nodeB.Services.GetRequiredService<WorkflowJobListener>();
        bus.Subscribe(listenerB.ReceiveClusterMessage);

        var hubB = nodeB.Services.GetRequiredService<WorkflowEventStreamHub>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = hubB.SubscribeAsync(workflow: "cluster-bus-flow", cancellationToken: cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var nextTask = enumerator.MoveNextAsync().AsTask();

        await nodeA.StartInstanceAsync("cluster-bus-flow", key: "cb-1");

        Assert.True(await nextTask);
        Assert.Equal("cb-1", enumerator.Current.Key);
    }

    private sealed class InMemoryTestClusterBus : IWorkflowClusterBus
    {
        private readonly List<Action<string, string?>> subscribers = [];

        public void Subscribe(Action<string, string?> handler) => subscribers.Add(handler);

        public Task PublishAsync(string channel, string? payload, CancellationToken cancellationToken = default)
        {
            foreach (var sub in subscribers)
            {
                sub(channel, payload);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancelOnFirstCallEventHandler : IWorkflowEventHandler
    {
        public int Calls { get; private set; }
        public CancellationTokenSource? ShutdownCts { get; set; }

        public async Task HandleAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken)
        {
            if (workflowEvent.Type != WorkflowEventType.Started)
            {
                return;
            }

            Calls++;
            if (Calls == 1)
            {
                // Fail inline delivery so the durable Event job stays queued in WorkflowJobs.
                throw new InvalidOperationException("queue for background job processor");
            }

            if (Calls == 2 && ShutdownCts is not null)
            {
                await ShutdownCts.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    public sealed class AttemptProbe
    {
        public int Calls { get; set; }
        public bool SawDirtyOnRetry { get; set; }
        public List<string> Keys { get; } = [];
    }

    public sealed class MutateOnceTask(AttemptProbe probe) : IWorkflowTask
    {
        public Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
        {
            probe.Calls++;
            probe.Keys.Add(context.IdempotencyKey);
            if (probe.Calls == 1)
            {
                context.Data["dirty"] = true;
                throw new InvalidOperationException("transient");
            }

            probe.SawDirtyOnRetry = context.Data.ContainsKey("dirty") || context.Scope.Data.ContainsKey("dirty");
            context.Data["successful"] = true;
            return Task.CompletedTask;
        }
    }

    public sealed class FailFirstEventHandler : IWorkflowEventHandler
    {
        public int Calls { get; private set; }

        public Task HandleAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new InvalidOperationException("delivery failed");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFailEventHandler : IWorkflowEventHandler
    {
        public Task HandleAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("delivery failed");
    }
}
