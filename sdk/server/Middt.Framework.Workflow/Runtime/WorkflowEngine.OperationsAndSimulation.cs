using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Endpoints;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Schema;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Workflow.Runtime;

/// <summary>Operations center, unified human task inbox, dry-run process simulator, and data retention purge.</summary>
public sealed partial class WorkflowEngine
{
    private const int MaxSimulationAutoSteps = 25;

    /// <summary>Returns an operational overview of faulted instances, busy instances, pending jobs, and unresolved incidents.</summary>
    public async Task<WorkflowOperationsSummaryModel> GetOperationsSummaryAsync(
        string? workflow,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        var accessibleWorkflows = registry.Latest
            .Where(d => d.Roles.Count == 0 || d.Roles.Any(user.IsInRole) || user.IsInRole("ui-admin"))
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(workflow))
        {
            var targetDef = registry.GetLatest(workflow);
            if (!accessibleWorkflows.Contains(targetDef.Key))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            accessibleWorkflows = [targetDef.Key];
        }

        var faultedRows = await db.Instances.AsNoTracking()
            .Where(i => accessibleWorkflows.Contains(i.WorkflowKey) && i.Status == WorkflowInstanceStatus.Faulted)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var accessibleFaulted = new List<WorkflowInstanceModel>();
        foreach (var row in faultedRows)
        {
            var def = registry.Get(row.WorkflowKey, row.WorkflowVersion);
            if (CanSeeAllInstances(def, user) || string.Equals(row.StartedBy, user.AuditName, StringComparison.Ordinal))
            {
                accessibleFaulted.Add(await ToModelAsync(row, user, cancellationToken));
            }
        }

        var busyCount = await db.Instances.AsNoTracking()
            .CountAsync(i => accessibleWorkflows.Contains(i.WorkflowKey) && i.Status == WorkflowInstanceStatus.Busy, cancellationToken);

        var jobsRows = await db.Jobs.AsNoTracking()
            .Where(j => accessibleWorkflows.Contains(j.WorkflowKey))
            .OrderBy(j => j.DueAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var pendingJobs = jobsRows.Select(j => new WorkflowPendingJobModel
        {
            Id = j.Id,
            Workflow = j.WorkflowKey,
            InstanceId = j.InstanceId,
            Kind = j.Kind.ToString(),
            State = j.State ?? string.Empty,
            TransitionKey = j.TransitionKey,
            DueAt = j.DueAt,
            Attempts = j.Attempts,
            LastError = options.Value.ExposeErrorDetails ? j.LastError : null,
        }).ToList();

        var incidentsRows = await db.Incidents.AsNoTracking()
            .Where(inc => accessibleWorkflows.Contains(inc.WorkflowKey) && inc.ResolvedAt == null)
            .OrderByDescending(inc => inc.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var recentIncidents = incidentsRows.Select(inc => new WorkflowIncidentModel
        {
            Id = inc.Id,
            State = inc.State,
            TransitionKey = inc.TransitionKey,
            TaskKey = inc.TaskKey,
            Error = options.Value.ExposeErrorDetails ? inc.Error : "An internal workflow step failed.",
            Attempts = inc.Attempts,
            CreatedAt = inc.CreatedAt,
            ResolvedAt = inc.ResolvedAt,
        }).ToList();

        return new WorkflowOperationsSummaryModel
        {
            FaultedCount = accessibleFaulted.Count,
            BusyCount = busyCount,
            PendingJobCount = pendingJobs.Count,
            UnresolvedIncidentCount = recentIncidents.Count,
            FaultedInstances = accessibleFaulted,
            PendingJobs = pendingJobs,
            RecentIncidents = recentIncidents,
        };
    }

    /// <summary>Retries multiple faulted workflow instances in a batch, enforcing per-instance authorization.</summary>
    public async Task<WorkflowBatchOperationResultModel> RetryBatchAsync(
        WorkflowBatchRetryRequest request,
        WorkflowUser user,
        HttpContext? http = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new WorkflowBatchOperationResultModel();

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Workflow) || item.InstanceId == Guid.Empty)
            {
                result.Failed.Add(new WorkflowBatchRetryFailureModel
                {
                    InstanceId = item.InstanceId,
                    Workflow = item.Workflow ?? string.Empty,
                    Error = "Workflow key and instanceId are required.",
                });
                continue;
            }

            try
            {
                var enrichedInput = item.Input is { Count: > 0 }
                    ? WorkflowHttpInput.FromHttpContext(http, item.Input)
                    : null;
                await RetryAsync(item.Workflow, item.InstanceId, enrichedInput, user, cancellationToken);
                result.SucceededIds.Add(item.InstanceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed.Add(new WorkflowBatchRetryFailureModel
                {
                    InstanceId = item.InstanceId,
                    Workflow = item.Workflow,
                    Error = ex.Message,
                });
            }
        }

        return result;
    }

    /// <summary>Lists actionable human task instances across all accessible workflows for <paramref name="user"/>.</summary>
    public Task<(List<WorkflowInboxItemModel> Items, int TotalCount)> ListInboxAsync(
        string? workflow,
        WorkflowUser user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        ListInboxAsync(workflow, null, user, page, pageSize, cancellationToken);

    /// <summary>Lists actionable human task instances across all accessible workflows for <paramref name="user"/> with optional assignment filter.</summary>
    public async Task<(List<WorkflowInboxItemModel> Items, int TotalCount)> ListInboxAsync(
        string? workflow,
        string? assignment,
        WorkflowUser user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var accessibleDefs = registry.Latest
            .Where(d => d.Roles.Count == 0 || d.Roles.Any(user.IsInRole) || user.IsInRole("ui-admin"))
            .ToDictionary(d => d.Key, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(workflow))
        {
            var targetDef = registry.GetLatest(workflow);
            if (!accessibleDefs.ContainsKey(targetDef.Key))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            accessibleDefs = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal)
            {
                [targetDef.Key] = targetDef,
            };
        }

        var workflowKeys = accessibleDefs.Keys.ToHashSet(StringComparer.Ordinal);
        var activeRows = await db.Instances.AsNoTracking()
            .Where(i => workflowKeys.Contains(i.WorkflowKey) && i.Status == WorkflowInstanceStatus.Active)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        var normalizedAssignment = assignment?.Trim();
        var inboxItems = new List<WorkflowInboxItemModel>();
        foreach (var row in activeRows)
        {
            var def = registry.Get(row.WorkflowKey, row.WorkflowVersion);
            if (!CanSeeAllInstances(def, user)
                && !string.Equals(row.StartedBy, user.AuditName, StringComparison.Ordinal)
                && !IsAssignedToUser(row, user))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedAssignment) && !string.Equals(normalizedAssignment, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(normalizedAssignment, "mine", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedAssignment, "$mine", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedAssignment, "assignedToMe", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsAssignedToUser(row, user))
                    {
                        continue;
                    }
                }
                else if (string.Equals(normalizedAssignment, "unassigned", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(normalizedAssignment, "$unassigned", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(row.AssignedTo))
                    {
                        continue;
                    }
                }
                else if (string.Equals(normalizedAssignment, "claimed", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(normalizedAssignment, "assigned", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(row.AssignedTo))
                    {
                        continue;
                    }
                }
            }

            var model = await ToModelAsync(row, user, cancellationToken);
            if (model.AvailableTransitions.Count == 0)
            {
                continue;
            }

            DateTime? dueAt = model.ScheduledTransitions.Count > 0
                ? model.ScheduledTransitions.Min(s => s.DueAt)
                : null;

            inboxItems.Add(new WorkflowInboxItemModel
            {
                InstanceId = model.Id,
                Key = model.Key,
                Workflow = model.Workflow,
                WorkflowVersion = model.WorkflowVersion,
                WorkflowTitle = def.Title,
                WorkflowIcon = def.Icon,
                State = model.State,
                StateTitle = model.StateTitle,
                StartedBy = model.StartedBy,
                AssignedTo = model.AssignedTo,
                AssignedAt = model.AssignedAt,
                CreatedAt = model.CreatedAt,
                UpdatedAt = model.UpdatedAt,
                DueAt = dueAt,
                AvailableTransitions = model.AvailableTransitions,
            });
        }

        var ordered = inboxItems
            .OrderBy(i => i.DueAt.HasValue ? 0 : 1)
            .ThenBy(i => i.DueAt)
            .ThenByDescending(i => i.UpdatedAt)
            .ToList();

        var totalCount = ordered.Count;
        var paged = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return (paged, totalCount);
    }

    /// <summary>Claims an active workflow instance in its current state for <paramref name="user"/> so other non-supervisor users cannot execute manual transitions until it is unclaimed or leaves the state.</summary>
    public async Task<WorkflowInstanceModel> ClaimAsync(
        string? workflow,
        Guid instanceId,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await FindAccessibleAsync(instanceId, workflow, user, cancellationToken);
        EnsureActive(snapshot);

        var isCurrentlyAssignee = false;
        if (!string.IsNullOrEmpty(snapshot.AssignedTo))
        {
            isCurrentlyAssignee = IsAssignedToUser(snapshot, user);
            if (!isCurrentlyAssignee)
            {
                throw new WorkflowConflictException($"Bu görev zaten '{snapshot.AssignedTo}' tarafından üstlenilmiş.");
            }

            if (!IsPoolAssignment(snapshot.AssignedTo))
            {
                return await ToModelAsync(snapshot, user, cancellationToken);
            }
        }

        var definition = registry.Get(snapshot.WorkflowKey, snapshot.WorkflowVersion);
        var data = JsonPath.ParseObject(snapshot.Data);
        var canAct = isCurrentlyAssignee
            || IsSupervisor(definition, user)
            || AvailableIn(definition, snapshot.State).Any(t => t.Trigger == TriggerType.Manual && CanExecuteIgnoringAssignee(t, definition, snapshot, data, user));
        if (!canAct)
        {
            throw new WorkflowForbiddenException("Bu adımdaki görevi üstlenme yetkiniz bulunmuyor.");
        }

        var now = Now;
        var assignee = user.AuditName;
        var updated = await db.Instances
            .Where(i => i.Id == instanceId && i.WorkflowKey == snapshot.WorkflowKey && i.Status == WorkflowInstanceStatus.Active && i.Revision == snapshot.Revision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.AssignedTo, assignee)
                .SetProperty(i => i.AssignedAt, now)
                .SetProperty(i => i.UpdatedAt, now)
                .SetProperty(i => i.Revision, i => i.Revision + 1), cancellationToken);

        if (updated == 0)
        {
            throw new WorkflowConflictException("Kayıt başka bir kullanıcı tarafından güncellendi veya üstlenildi; lütfen sayfayı yenileyin.");
        }

        snapshot.AssignedTo = assignee;
        snapshot.AssignedAt = now;
        snapshot.UpdatedAt = now;
        snapshot.Revision++;

        await PublishDirectEventAsync(snapshot, snapshot.State, "$claim", cancellationToken);
        return await ToModelAsync(snapshot, user, cancellationToken);
    }

    /// <summary>Releases (<c>unclaims</c>) an active workflow instance so any authorized user in the current state can claim or execute it.</summary>
    public async Task<WorkflowInstanceModel> UnclaimAsync(
        string? workflow,
        Guid instanceId,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await FindAccessibleAsync(instanceId, workflow, user, cancellationToken);
        EnsureActive(snapshot);

        if (string.IsNullOrEmpty(snapshot.AssignedTo))
        {
            return await ToModelAsync(snapshot, user, cancellationToken);
        }

        var definition = registry.Get(snapshot.WorkflowKey, snapshot.WorkflowVersion);
        if (!IsAssignedToUser(snapshot, user) && !IsSupervisor(definition, user))
        {
            throw new WorkflowForbiddenException("Yalnızca görevi üstlenen kullanıcı veya bir yönetici görevi bırakabilir.");
        }

        var now = Now;
        var updated = await db.Instances
            .Where(i => i.Id == instanceId && i.WorkflowKey == snapshot.WorkflowKey && i.Status == WorkflowInstanceStatus.Active && i.Revision == snapshot.Revision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.AssignedTo, (string?)null)
                .SetProperty(i => i.AssignedAt, (DateTime?)null)
                .SetProperty(i => i.UpdatedAt, now)
                .SetProperty(i => i.Revision, i => i.Revision + 1), cancellationToken);

        if (updated == 0)
        {
            throw new WorkflowConflictException("Kayıt eşzamanlı olarak güncellendi; lütfen sayfayı yenileyin.");
        }

        snapshot.AssignedTo = null;
        snapshot.AssignedAt = null;
        snapshot.UpdatedAt = now;
        snapshot.Revision++;

        await PublishDirectEventAsync(snapshot, snapshot.State, "$unclaim", cancellationToken);
        return await ToModelAsync(snapshot, user, cancellationToken);
    }

    /// <summary>Assigns or delegates (<c>vekalet / devretme</c>) an active workflow instance to one or more target users or roles.</summary>
    public async Task<WorkflowInstanceModel> AssignAsync(
        string? workflow,
        Guid instanceId,
        WorkflowAssignRequest request,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetAssignee = NormalizeAssigneeSpec(request);
        if (string.IsNullOrWhiteSpace(targetAssignee))
        {
            throw new WorkflowValidationException(["Atanacak kullanıcı veya rol ('assignedTo', 'users', 'roles') boş olamaz."]);
        }

        var snapshot = await FindAccessibleAsync(instanceId, workflow, user, cancellationToken);
        EnsureActive(snapshot);

        var definition = registry.Get(snapshot.WorkflowKey, snapshot.WorkflowVersion);
        var data = JsonPath.ParseObject(snapshot.Data);
        var isSupervisor = IsSupervisor(definition, user);
        if (string.IsNullOrEmpty(snapshot.AssignedTo))
        {
            var canAct = isSupervisor
                || AvailableIn(definition, snapshot.State).Any(t => t.Trigger == TriggerType.Manual && CanExecuteIgnoringAssignee(t, definition, snapshot, data, user));
            if (!canAct)
            {
                throw new WorkflowForbiddenException("Bu adımdaki görevi atama yetkiniz bulunmuyor.");
            }
        }
        else if (!IsAssignedToUser(snapshot, user) && !isSupervisor)
        {
            throw new WorkflowForbiddenException("Yalnızca görevi üstlenen kullanıcı veya bir yönetici görevi başkasına devredebilir.");
        }

        var now = Now;
        var updated = await db.Instances
            .Where(i => i.Id == instanceId && i.WorkflowKey == snapshot.WorkflowKey && i.Status == WorkflowInstanceStatus.Active && i.Revision == snapshot.Revision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.AssignedTo, targetAssignee)
                .SetProperty(i => i.AssignedAt, now)
                .SetProperty(i => i.UpdatedAt, now)
                .SetProperty(i => i.Revision, i => i.Revision + 1), cancellationToken);

        if (updated == 0)
        {
            throw new WorkflowConflictException("Kayıt eşzamanlı olarak güncellendi; lütfen sayfayı yenileyin.");
        }

        snapshot.AssignedTo = targetAssignee;
        snapshot.AssignedAt = now;
        snapshot.UpdatedAt = now;
        snapshot.Revision++;

        await PublishDirectEventAsync(snapshot, snapshot.State, "$assign", cancellationToken);
        return await ToModelAsync(snapshot, user, cancellationToken);
    }

    /// <summary>Migrates active (and optionally faulted) workflow instances to a target workflow definition version with optional state mapping and dry-run validation.</summary>
    public async Task<WorkflowInstanceMigrationResultModel> MigrateInstancesAsync(
        string workflow,
        WorkflowInstanceMigrationRequest request,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!user.IsSystem && !user.IsInRole("ui-admin") && !user.IsInRole("idp-admin"))
        {
            throw new WorkflowForbiddenException("Migrating workflow instances requires the 'ui-admin' or 'idp-admin' role.");
        }

        var targetDef = request.ToVersion is { } tv ? registry.Get(workflow, tv) : registry.GetLatest(workflow);
        var query = db.Instances.Where(i => i.WorkflowKey == targetDef.Key);

        if (request.IncludeFaulted)
        {
            query = query.Where(i => i.Status == WorkflowInstanceStatus.Active || i.Status == WorkflowInstanceStatus.Faulted);
        }
        else
        {
            query = query.Where(i => i.Status == WorkflowInstanceStatus.Active);
        }

        if (request.FromVersion is { } fv)
        {
            query = query.Where(i => i.WorkflowVersion == fv);
        }
        else
        {
            query = query.Where(i => i.WorkflowVersion != targetDef.Version);
        }

        if (request.InstanceIds is { Count: > 0 })
        {
            var idSet = request.InstanceIds.ToHashSet();
            query = query.Where(i => idSet.Contains(i.Id));
        }

        var candidates = await query
            .OrderByDescending(i => i.UpdatedAt)
            .ToListAsync(cancellationToken);

        var result = new WorkflowInstanceMigrationResultModel
        {
            Workflow = targetDef.Key,
            ToVersion = targetDef.Version,
            DryRun = request.DryRun,
            CandidateCount = candidates.Count,
        };

        if (candidates.Count == 0)
        {
            return result;
        }

        var plans = new List<(WorkflowInstance Instance, int FromVersion, string FromState, string ToState)>(candidates.Count);
        foreach (var instance in candidates)
        {
            var fromState = instance.State;
            var toState = fromState;
            if (request.StateMap is not null
                && request.StateMap.TryGetValue(fromState, out var mapped)
                && !string.IsNullOrWhiteSpace(mapped))
            {
                toState = mapped.Trim();
            }

            if (targetDef.FindState(toState) is null)
            {
                var err = $"Hedef sürüm v{targetDef.Version} içinde '{toState}' durumu bulunamadı (kaynak durum: '{fromState}'). Lütfen stateMap eşlemesi tanımlayın.";
                result.FailedCount++;
                result.Errors.Add($"[{instance.BusinessKey ?? instance.Id.ToString()}] {err}");
                result.Items.Add(new WorkflowInstanceMigrationItemModel
                {
                    InstanceId = instance.Id,
                    Key = instance.BusinessKey,
                    FromVersion = instance.WorkflowVersion,
                    ToVersion = targetDef.Version,
                    FromState = fromState,
                    ToState = toState,
                    Migrated = false,
                    Error = err,
                });
            }
            else
            {
                plans.Add((instance, instance.WorkflowVersion, fromState, toState));
                result.Items.Add(new WorkflowInstanceMigrationItemModel
                {
                    InstanceId = instance.Id,
                    Key = instance.BusinessKey,
                    FromVersion = instance.WorkflowVersion,
                    ToVersion = targetDef.Version,
                    FromState = fromState,
                    ToState = toState,
                    Migrated = true,
                });
            }
        }

        if (result.FailedCount > 0)
        {
            foreach (var item in result.Items)
            {
                if (item.Error is null)
                {
                    item.Migrated = false;
                }
            }

            return result;
        }

        result.MigratedCount = plans.Count;
        if (request.DryRun)
        {
            return result;
        }

        var now = Now;
        foreach (var (instance, fromVersion, fromState, toState) in plans)
        {
            instance.WorkflowVersion = targetDef.Version;
            instance.State = toState;
            instance.UpdatedAt = now;
            instance.Revision++;

            var targetStateDef = targetDef.FindState(toState);
            var pendingScheduled = await db.Jobs
                .Where(j => j.InstanceId == instance.Id && j.WorkflowKey == instance.WorkflowKey && j.Kind == WorkflowJobKind.Scheduled && j.State == fromState)
                .ToListAsync(cancellationToken);
            if (pendingScheduled.Count > 0)
            {
                var validScheduledKeys = targetStateDef?.Transitions
                    .Where(t => t.Trigger == TriggerType.Scheduled)
                    .Select(t => t.Key)
                    .ToHashSet(StringComparer.Ordinal) ?? [];
                foreach (var job in pendingScheduled)
                {
                    if (job.TransitionKey is not null && !validScheduledKeys.Contains(job.TransitionKey))
                    {
                        db.Jobs.Remove(job);
                    }
                    else
                    {
                        job.State = toState;
                    }
                }
            }

            if (targetDef.History != HistoryMode.None)
            {
                db.History.Add(new WorkflowHistory
                {
                    InstanceId = instance.Id,
                    WorkflowKey = instance.WorkflowKey,
                    TransitionKey = "$migrate",
                    FromState = fromState,
                    ToState = toState,
                    Trigger = "Migration",
                    ExecutedBy = user.AuditName,
                    Input = new JsonObject
                    {
                        ["fromVersion"] = fromVersion,
                        ["toVersion"] = targetDef.Version,
                        ["fromState"] = fromState,
                        ["toState"] = toState,
                    }.ToJsonString(),
                    DataVersion = instance.DataVersion,
                    Data = targetDef.History == HistoryMode.Full ? instance.Data : null,
                    Patch = targetDef.History == HistoryMode.Delta ? "[]" : null,
                    StartedAt = now,
                    FinishedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var (instance, _, fromState, _) in plans)
        {
            await PublishDirectEventAsync(instance, fromState, "$migrate", cancellationToken);
        }

        return result;
    }

    private async Task PublishDirectEventAsync(WorkflowInstance instance, string? fromState, string transition, CancellationToken cancellationToken)
    {
        if (EventHandlers.Length == 0)
        {
            return;
        }

        var ev = new WorkflowEvent(
            WorkflowEventType.StateChanged,
            instance.Id,
            instance.WorkflowKey,
            instance.WorkflowVersion,
            instance.BusinessKey,
            fromState,
            instance.State,
            transition,
            [],
            Now);

        foreach (var handler in EventHandlers)
        {
            try
            {
                await handler.HandleAsync(ev, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogHandlerFailed(logger, ex, handler.GetType().Name, ev.Type, instance.Id);
            }
        }
    }

    /// <summary>Executes a dry-run simulation of a workflow definition in memory without writing to the database.</summary>
    public async Task<WorkflowSimulationResultModel> SimulateAsync(
        string workflow,
        WorkflowSimulationRequest request,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkflowDefinition definition;
        if (request.Definition is { } draftJson)
        {
            var validation = registry.ValidateJson(draftJson);
            if (!validation.IsValid)
            {
                throw new WorkflowValidationException(validation.Errors);
            }

            definition = System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(draftJson, WorkflowJson.Options)!;
        }
        else
        {
            definition = request.Version is { } v ? registry.Get(workflow, v) : registry.GetLatest(workflow);
        }

        EnsureCanRead(definition, user);

        var visitedStates = new List<string>();
        var executedTransitions = new List<string>();
        var workingData = new JsonObject();
        var simInstance = new WorkflowInstance
        {
            Id = Guid.CreateVersion7(),
            WorkflowKey = definition.Key,
            WorkflowVersion = definition.Version,
            BusinessKey = request.Key,
            Status = WorkflowInstanceStatus.Active,
            StartedBy = user.AuditName,
            LastExecutedBy = user.AuditName,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        try
        {
            var startInput = WorkflowHttpInput.EnsureEnvelope(request.Data);
            ValidateInput(definition.Start, startInput, data: null, user: user);
            ApplySimulatedInput(definition.Start, definition, simInstance, workingData, startInput, user);
            await ExecuteSimulatedTasksAsync(definition.Start.OnExecute, definition, simInstance, workingData, startInput, user, request.TaskMocks, cancellationToken);
            executedTransitions.Add(definition.Start.Key);

            await EnterSimulatedStateAsync(
                definition,
                simInstance,
                definition.Start.Target,
                workingData,
                startInput,
                user,
                visitedStates,
                executedTransitions,
                request.TaskMocks,
                cancellationToken);

            foreach (var step in request.Steps)
            {
                if (simInstance.IsTerminal)
                {
                    throw new WorkflowConflictException($"Simulation reached terminal state '{simInstance.State}' ({simInstance.Status}) before step '{step.Transition}'.");
                }

                var stepInput = WorkflowHttpInput.EnsureEnvelope(step.Input);
                simInstance.Data = workingData.ToJsonString();
                var transition = FindManualTransition(definition, simInstance, step.Transition, user, stepInput);
                var currentStateDef = definition.FindState(simInstance.State)
                    ?? throw new WorkflowConflictException($"State '{simInstance.State}' does not exist.");

                ApplySimulatedInput(transition, definition, simInstance, workingData, stepInput, user);
                await ExecuteSimulatedTasksAsync(transition.OnExecute, definition, simInstance, workingData, stepInput, user, request.TaskMocks, cancellationToken);
                await ExecuteSimulatedTasksAsync(currentStateDef.OnExit, definition, simInstance, workingData, stepInput, user, request.TaskMocks, cancellationToken);
                executedTransitions.Add(transition.Key);

                var targetStateKey = transition.Target == TransitionDefinition.Self ? simInstance.State : transition.Target;
                await EnterSimulatedStateAsync(
                    definition,
                    simInstance,
                    targetStateKey,
                    workingData,
                    stepInput,
                    user,
                    visitedStates,
                    executedTransitions,
                    request.TaskMocks,
                    cancellationToken);
            }

            simInstance.Data = workingData.ToJsonString();
            var projectedData = JsonSchemaValidator.ProjectData(workingData, definition.DataSchema, user);
            var scope = RuleScope(definition, simInstance, workingData, user);
            var available = simInstance.IsTerminal
                ? []
                : AvailableIn(definition, simInstance.State)
                    .Where(t => t.Trigger == TriggerType.Manual && CanExecute(t, definition, simInstance, workingData, user))
                    .Where(t => IsRuleSatisfied(t, definition, simInstance, workingData, user))
                    .Select(t => new AvailableTransitionModel
                    {
                        Key = t.Key,
                        Title = t.Title,
                        Description = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description,
                        Target = t.Target,
                        Style = string.IsNullOrWhiteSpace(t.Style) ? null : t.Style,
                        Icon = string.IsNullOrWhiteSpace(t.Icon) ? null : t.Icon,
                        Toolbar = t.Toolbar,
                        InputSchema = ProjectInputSchema(t.Input?.Schema, scope),
                        View = string.IsNullOrWhiteSpace(t.View?.Component) ? null : t.View.Component,
                        Inline = t.View?.Inline == true,
                        InputInto = t.Input?.Into,
                        InputFormat = t.Input?.Format == InputFormat.JsonPatch ? "jsonPatch" : null,
                    })
                    .ToList();

            return new WorkflowSimulationResultModel
            {
                Success = true,
                FinalState = simInstance.State,
                FinalStatus = simInstance.Status,
                VisitedStates = visitedStates,
                ExecutedTransitions = executedTransitions,
                FinalData = projectedData,
                AvailableTransitions = available,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WorkflowSimulationResultModel
            {
                Success = false,
                FinalState = simInstance.State,
                FinalStatus = WorkflowInstanceStatus.Faulted,
                VisitedStates = visitedStates,
                ExecutedTransitions = executedTransitions,
                FinalData = (JsonObject)workingData.DeepClone(),
                Error = ex.Message,
            };
        }
    }

    /// <summary>Purges terminal (<c>Completed</c> / <c>Cancelled</c>) workflow instances older than <see cref="WorkflowPurgeRequest.RetentionDays"/>.</summary>
    public async Task<WorkflowPurgeResultModel> PurgeAsync(
        WorkflowPurgeRequest request,
        WorkflowUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(user.Id, WorkflowUser.System.Id, StringComparison.Ordinal) && !user.IsInRole("ui-admin"))
        {
            throw new WorkflowForbiddenException("Purging workflow instances requires the 'ui-admin' role.");
        }

        var retentionDays = Math.Max(0, request.RetentionDays);
        var cutoffUtc = Now.AddDays(-retentionDays);

        var query = db.Instances.Where(i =>
            (i.Status == WorkflowInstanceStatus.Completed || i.Status == WorkflowInstanceStatus.Cancelled)
            && (i.CompletedAt ?? i.UpdatedAt) <= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(request.Workflow))
        {
            query = query.Where(i => i.WorkflowKey == request.Workflow);
        }

        var targetInstances = await query.ToListAsync(cancellationToken);
        var instanceIds = targetInstances.Select(i => i.Id).ToHashSet();

        if (instanceIds.Count == 0)
        {
            return new WorkflowPurgeResultModel
            {
                DryRun = request.DryRun,
                CutoffUtc = cutoffUtc,
                PurgedInstancesCount = 0,
                PurgedHistoryCount = 0,
                PurgedIncidentsCount = 0,
            };
        }

        var workflowKeys = targetInstances.Select(i => i.WorkflowKey).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var historyRows = await db.History.Where(h => workflowKeys.Contains(h.WorkflowKey) && instanceIds.Contains(h.InstanceId)).ToListAsync(cancellationToken);
        var incidentRows = await db.Incidents.Where(inc => workflowKeys.Contains(inc.WorkflowKey) && instanceIds.Contains(inc.InstanceId)).ToListAsync(cancellationToken);

        if (!request.DryRun)
        {
            var jobRows = await db.Jobs.Where(j => workflowKeys.Contains(j.WorkflowKey) && instanceIds.Contains(j.InstanceId)).ToListAsync(cancellationToken);
            var agentRuns = await db.AgentRuns.Where(r => workflowKeys.Contains(r.WorkflowKey) && instanceIds.Contains(r.InstanceId)).ToListAsync(cancellationToken);
            var toolCalls = await db.AgentToolCalls.Where(tc => workflowKeys.Contains(tc.WorkflowKey) && instanceIds.Contains(tc.InstanceId)).ToListAsync(cancellationToken);
            var sessions = await db.AgentSessions.Where(s => workflowKeys.Contains(s.WorkflowKey) && instanceIds.Contains(s.InstanceId)).ToListAsync(cancellationToken);

            db.Jobs.RemoveRange(jobRows);
            db.AgentToolCalls.RemoveRange(toolCalls);
            db.AgentRuns.RemoveRange(agentRuns);
            db.AgentSessions.RemoveRange(sessions);
            db.History.RemoveRange(historyRows);
            db.Incidents.RemoveRange(incidentRows);
            db.Instances.RemoveRange(targetInstances);
            await db.SaveChangesAsync(cancellationToken);
        }

        return new WorkflowPurgeResultModel
        {
            DryRun = request.DryRun,
            CutoffUtc = cutoffUtc,
            PurgedInstancesCount = targetInstances.Count,
            PurgedHistoryCount = historyRows.Count,
            PurgedIncidentsCount = incidentRows.Count,
        };
    }

    private void ApplySimulatedInput(
        TransitionDefinition transition,
        WorkflowDefinition definition,
        WorkflowInstance simInstance,
        JsonObject workingData,
        JsonObject? envelopeInput,
        WorkflowUser user)
    {
        if (transition.Input is not { } inputDef)
        {
            return;
        }

        var body = WorkflowHttpInput.ExtractBody(envelopeInput, inputDef.Schema);
        if (inputDef.Schema is not null && body is { Count: > 0 } && inputDef.Persistable(body) is { } clean)
        {
            JsonPath.Merge(workingData, clean, inputDef.Into);
        }

        var scopeInput = envelopeInput is not null ? (JsonObject)envelopeInput.DeepClone() : [];
        scopeInput["removed"] = new JsonArray();
        AttachEnumFromLabels(inputDef.Schema, RuleScope(definition, simInstance, workingData, user, scopeInput));
        foreach (var (path, expression) in inputDef.EffectiveSet)
        {
            var value = expressions.EvaluateToNode(expression, RuleScope(definition, simInstance, workingData, user, scopeInput));
            if (value is JsonArray updatedArr && JsonDynamic.GetRemovedItems(updatedArr) is { Count: > 0 } removedByExpr)
            {
                scopeInput["removed"] = removedByExpr.DeepClone();
            }

            JsonPath.Set(workingData, path, value);
        }
    }

    private async Task EnterSimulatedStateAsync(
        WorkflowDefinition definition,
        WorkflowInstance simInstance,
        string targetStateKey,
        JsonObject workingData,
        JsonObject? currentInput,
        WorkflowUser user,
        List<string> visitedStates,
        List<string> executedTransitions,
        Dictionary<string, JsonObject>? taskMocks,
        CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < MaxSimulationAutoSteps; depth++)
        {
            var state = definition.FindState(targetStateKey)
                ?? throw new WorkflowConflictException($"State '{targetStateKey}' does not exist.");
            simInstance.State = state.Key;
            visitedStates.Add(state.Key);

            await ExecuteSimulatedTasksAsync(state.OnEntry, definition, simInstance, workingData, currentInput, user, taskMocks, cancellationToken);

            if (state.IsFinal)
            {
                simInstance.Status = state.SubType == StateSubType.Cancelled
                    ? WorkflowInstanceStatus.Cancelled
                    : WorkflowInstanceStatus.Completed;
                simInstance.CompletedAt = Now;
                return;
            }

            var autoTransitions = state.Transitions.Where(t => t.Trigger == TriggerType.Automatic).ToList();
            if (autoTransitions.Count == 0)
            {
                return;
            }

            TransitionDefinition? matchedAuto = null;
            var scope = RuleScope(definition, simInstance, workingData, user, currentInput);
            foreach (var candidate in autoTransitions.Where(t => !t.Default && !string.IsNullOrWhiteSpace(t.Rule)))
            {
                if (expressions.EvaluateCondition(candidate.Rule!, scope))
                {
                    matchedAuto = candidate;
                    break;
                }
            }

            matchedAuto ??= autoTransitions.FirstOrDefault(t => t.Default);
            if (matchedAuto is null)
            {
                return;
            }

            await ExecuteSimulatedTasksAsync(matchedAuto.OnExecute, definition, simInstance, workingData, null, user, taskMocks, cancellationToken);
            await ExecuteSimulatedTasksAsync(state.OnExit, definition, simInstance, workingData, null, user, taskMocks, cancellationToken);
            executedTransitions.Add(matchedAuto.Key);
            targetStateKey = matchedAuto.Target == TransitionDefinition.Self ? state.Key : matchedAuto.Target;
            currentInput = null;
        }

        throw new WorkflowConflictException($"Simulation exceeded maximum automatic transition depth ({MaxSimulationAutoSteps}).");
    }

    private async Task ExecuteSimulatedTasksAsync(
        List<TaskDefinition> taskDefs,
        WorkflowDefinition definition,
        WorkflowInstance simInstance,
        JsonObject workingData,
        JsonObject? currentInput,
        WorkflowUser user,
        Dictionary<string, JsonObject>? taskMocks,
        CancellationToken cancellationToken)
    {
        foreach (var taskDef in taskDefs)
        {
            var scope = RuleScope(definition, simInstance, workingData, user, currentInput);
            if (!string.IsNullOrWhiteSpace(taskDef.When) && !expressions.EvaluateCondition(taskDef.When, scope))
            {
                continue;
            }

            if (taskMocks is not null
                && ((taskDef.Key is not null && taskMocks.TryGetValue(taskDef.Key, out var mockData))
                    || taskMocks.TryGetValue(taskDef.Task, out mockData)))
            {
                var into = taskDef.Config["into"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(into))
                {
                    JsonPath.Set(workingData, into, mockData.DeepClone());
                }
                else
                {
                    JsonPath.Merge(workingData, mockData);
                }

                continue;
            }

            // Safe in-memory tasks (set, setKey, log) run directly; external I/O tasks are skipped in dry-run unless mocked.
            if (taskDef.Task is "set" or "setKey" or "set-key" or "log")
            {
                var taskImpl = tasks.Resolve(services, taskDef.Task);
                var ctx = new WorkflowTaskContext
                {
                    Task = taskDef,
                    Data = workingData,
                    Scope = scope,
                    Expressions = expressions,
                    InstanceId = simInstance.Id,
                    IdempotencyKey = $"sim:{simInstance.Id:N}:{taskDef.DisplayKey}",
                    Workflow = definition.Key,
                    State = simInstance.State,
                    Services = services,
                    Logger = logger,
                    BusinessKey = simInstance.BusinessKey,
                    SetBusinessKey = (k, _) =>
                    {
                        simInstance.BusinessKey = k;
                        return Task.CompletedTask;
                    },
                };
                await taskImpl.ExecuteAsync(ctx, cancellationToken);
            }
        }
    }
}
