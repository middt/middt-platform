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
    public async Task<(List<WorkflowInboxItemModel> Items, int TotalCount)> ListInboxAsync(
        string? workflow,
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

        var inboxItems = new List<WorkflowInboxItemModel>();
        foreach (var row in activeRows)
        {
            var def = registry.Get(row.WorkflowKey, row.WorkflowVersion);
            if (!CanSeeAllInstances(def, user) && !string.Equals(row.StartedBy, user.AuditName, StringComparison.Ordinal))
            {
                continue;
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
            ValidateInput(definition.Start, startInput);
            ApplySimulatedInput(definition.Start, workingData, startInput);
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

                ApplySimulatedInput(transition, workingData, stepInput);
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

        var historyRows = await db.History.Where(h => instanceIds.Contains(h.InstanceId)).ToListAsync(cancellationToken);
        var incidentRows = await db.Incidents.Where(inc => instanceIds.Contains(inc.InstanceId)).ToListAsync(cancellationToken);

        if (!request.DryRun)
        {
            var agentRuns = await db.AgentRuns.Where(r => instanceIds.Contains(r.InstanceId)).ToListAsync(cancellationToken);
            var toolCalls = await db.AgentToolCalls.Where(tc => instanceIds.Contains(tc.InstanceId)).ToListAsync(cancellationToken);
            var sessions = await db.AgentSessions.Where(s => instanceIds.Contains(s.InstanceId)).ToListAsync(cancellationToken);

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

    private static void ApplySimulatedInput(TransitionDefinition transition, JsonObject workingData, JsonObject? envelopeInput)
    {
        var body = WorkflowHttpInput.ExtractBody(envelopeInput, transition.Input?.Schema);
        if (body is null || body.Count == 0)
        {
            return;
        }

        var clean = transition.Input?.Persistable(body) ?? body;
        var into = transition.Input?.Into;
        if (!string.IsNullOrWhiteSpace(into))
        {
            var target = JsonPath.Get(workingData, into) as JsonObject ?? [];
            JsonPath.Merge(target, clean);
            JsonPath.Set(workingData, into, target);
        }
        else
        {
            JsonPath.Merge(workingData, clean);
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
