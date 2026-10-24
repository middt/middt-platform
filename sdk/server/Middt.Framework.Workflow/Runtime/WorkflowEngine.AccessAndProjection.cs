using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Schema;

namespace Middt.Framework.Workflow.Runtime;

/// <summary>Authorization, transition availability, and response projection for workflow instances.</summary>
public sealed partial class WorkflowEngine
{
    private TransitionDefinition FindManualTransition(WorkflowDefinition definition, WorkflowInstance instance, string key, WorkflowUser user, JsonObject? input = null)
    {
        if (instance.IsTerminal)
        {
            throw new WorkflowConflictException($"The instance is {instance.Status}.");
        }

        var transition = AvailableIn(definition, instance.State).FirstOrDefault(t => t.Key == key && t.Trigger == TriggerType.Manual)
            ?? throw new WorkflowConflictException($"Transition '{key}' is not available in state '{instance.State}'.");

        var data = JsonPath.ParseObject(instance.Data);
        if (!CanExecute(transition, definition, instance, data, user))
        {
            throw new WorkflowForbiddenException($"You are not allowed to execute '{key}'.");
        }

        if (!IsRuleSatisfied(transition, definition, instance, data, user))
        {
            throw new WorkflowConflictException($"Transition '{key}' is not available for the current data.");
        }

        if (instance.WaitingForInstanceId is not null && !IsCancellation(definition, transition))
        {
            throw new WorkflowConflictException("The instance is waiting for a subflow to finish.");
        }

        ValidateInput(transition, input);

        var body = Endpoints.WorkflowHttpInput.ExtractBody(input, transition.Input?.Schema) ?? input ?? [];
        if (!IsRuleSatisfied(transition, definition, instance, data, user, body)
            || !IsInputRuleSatisfied(transition, definition, instance, data, user, body))
        {
            throw new WorkflowConflictException($"Transition '{key}' is not available for the current data.");
        }

        return transition;
    }

    /// <summary>Availability condition of a manual transition (<c>rule</c>); evaluation errors make it unavailable.</summary>
    private bool IsRuleSatisfied(TransitionDefinition transition, WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user, JsonObject? input = null)
    {
        if (transition.Trigger != TriggerType.Manual || string.IsNullOrWhiteSpace(transition.Rule))
        {
            return true;
        }

        try
        {
            return expressions.EvaluateCondition(transition.Rule, RuleScope(definition, instance, data, user, input));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRuleFailed(logger, ex, transition.Key, instance.Id);
            return false;
        }
    }

    /// <summary>Input condition of a manual transition (<c>input.rule</c>), evaluated when input is submitted.</summary>
    private bool IsInputRuleSatisfied(TransitionDefinition transition, WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user, JsonObject input)
    {
        if (string.IsNullOrWhiteSpace(transition.Input?.Rule) || transition.Input.Format == InputFormat.JsonPatch)
        {
            return true;
        }

        try
        {
            var scope = RuleScope(definition, instance, data, user, input);
            AttachEnumFromLabels(transition.Input.Schema, scope);
            return expressions.EvaluateCondition(transition.Input.Rule, scope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRuleFailed(logger, ex, transition.Key, instance.Id);
            return false;
        }
    }

    private ExpressionScope RuleScope(WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user, JsonObject? input = null, JsonObject? agents = null, JsonArray? agentRuns = null, JsonArray? toolCalls = null) => new()
    {
        Data = data,
        Input = input,
        User = user,
        Now = time.GetUtcNow(),
        Instance = new JsonObject
        {
            ["id"] = instance.Id,
            ["key"] = instance.BusinessKey,
            ["state"] = instance.State,
            ["workflow"] = definition.Key,
            ["version"] = definition.Version,
            ["agents"] = agents?.DeepClone() ?? new JsonObject(),
            ["agentRuns"] = agentRuns?.DeepClone() ?? new JsonArray(),
            ["toolCalls"] = toolCalls?.DeepClone() ?? new JsonArray(),
        },
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Availability rule of transition {Transition} failed for instance {InstanceId}")]
    private static partial void LogRuleFailed(ILogger logger, Exception exception, string transition, Guid instanceId);

    private static bool IsCancellation(WorkflowDefinition definition, TransitionDefinition transition) =>
        definition.FindState(transition.Target) is { IsFinal: true };

    private bool CanExecute(TransitionDefinition transition, WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user)
    {
        if (user.IsSystem)
        {
            return true;
        }

        if (transition.DenyRoles.Count > 0 && transition.DenyRoles.Any(r => MatchesRoleEntry(r, definition, instance, data, user)))
        {
            return false;
        }

        return transition.Roles.Count == 0 || transition.Roles.Any(r => MatchesRoleEntry(r, definition, instance, data, user));
    }

    private static void EnsureCanPatchInstanceData(WorkflowDefinition definition, WorkflowInstance instance, JsonObject patch, WorkflowUser user)
    {
        if (user.IsSystem)
        {
            return;
        }

        if (definition.DataSchema is { } dataSchema)
        {
            EnsureCanWriteDataProperties(patch, dataSchema, user);
        }

        var isOwnerOrStarter = !string.IsNullOrEmpty(instance.StartedBy) &&
                               string.Equals(instance.StartedBy, user.AuditName, StringComparison.Ordinal);
        var isSupervisor = user.IsInRole("ui-admin")
                           || user.IsInRole("manager")
                           || (definition.InstanceAccess?.SupervisorRoles.Any(user.IsInRole) == true);
        if (!isOwnerOrStarter && !isSupervisor && definition.InstanceAccess is { IsOwnerOnly: true })
        {
            throw new WorkflowForbiddenException("You are not allowed to modify instance data on retry.");
        }
    }

    private static void EnsureCanWriteDataProperties(JsonObject patch, JsonObject schema, WorkflowUser user)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (propName, propVal) in patch)
        {
            if (properties[propName] is not JsonObject propSchema)
            {
                continue;
            }

            if (propSchema[JsonSchemaValidator.RolesKeyword] is JsonArray rolesArr && rolesArr.Count > 0)
            {
                var allowed = rolesArr
                    .Select(r => r is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : null)
                    .OfType<string>()
                    .Any(user.IsInRole);
                if (!allowed)
                {
                    throw new WorkflowForbiddenException($"You are not allowed to modify field '{propName}'.");
                }
            }

            if (propVal is JsonObject childPatch)
            {
                EnsureCanWriteDataProperties(childPatch, propSchema, user);
            }
        }
    }

    private bool MatchesRoleEntry(string roleEntry, WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user)
    {
        if (roleEntry is "$starter" or "$owner")
        {
            return !string.IsNullOrEmpty(instance.StartedBy) &&
                string.Equals(instance.StartedBy, user.AuditName, StringComparison.Ordinal);
        }

        if (roleEntry == "$previousUser")
        {
            var prev = instance.LastExecutedBy ?? instance.StartedBy;
            return !string.IsNullOrEmpty(prev) &&
                string.Equals(prev, user.AuditName, StringComparison.Ordinal);
        }

        if (roleEntry.StartsWith("$user:", StringComparison.Ordinal))
        {
            var resolved = ResolveRoleExpression(roleEntry["$user:".Length..].Trim(), definition, instance, data, user);
            return !string.IsNullOrEmpty(resolved) &&
                string.Equals(resolved, user.AuditName, StringComparison.Ordinal);
        }

        if (roleEntry.StartsWith("$role:", StringComparison.Ordinal))
        {
            var resolved = ResolveRoleExpression(roleEntry["$role:".Length..].Trim(), definition, instance, data, user);
            return !string.IsNullOrEmpty(resolved) && user.IsInRole(resolved);
        }

        return user.IsInRole(roleEntry);
    }

    private string? ResolveRoleExpression(string exprOrTemplate, WorkflowDefinition definition, WorkflowInstance instance, JsonObject data, WorkflowUser user)
    {
        try
        {
            var scope = RuleScope(definition, instance, data, user);
            return exprOrTemplate.Contains("{{", StringComparison.Ordinal)
                ? expressions.RenderText(exprOrTemplate, scope).Trim()
                : expressions.Evaluate(exprOrTemplate, scope)?.ToString()?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRuleFailed(logger, ex, exprOrTemplate, instance.Id);
            return null;
        }
    }

    /// <summary>Definition roles plus, in <c>owner</c> mode, ownership of the instance (others get 404: no existence disclosure).</summary>
    private static void EnsureCanAccess(WorkflowDefinition definition, WorkflowInstance instance, WorkflowUser user)
    {
        EnsureCanRead(definition, user);
        if (!CanSeeAllInstances(definition, user) && !string.Equals(instance.StartedBy, user.AuditName, StringComparison.Ordinal))
        {
            throw new WorkflowNotFoundException($"Instance '{instance.Id}' does not exist.");
        }
    }

    private static bool CanSeeAllInstances(WorkflowDefinition definition, WorkflowUser user) =>
        user.IsSystem
        || definition.InstanceAccess is not { IsOwnerOnly: true } access
        || access.SupervisorRoles.Any(user.IsInRole);

    private static void EnsureCanRead(WorkflowDefinition definition, WorkflowUser user)
    {
        if (definition.Roles.Count > 0 && !definition.Roles.Any(user.IsInRole))
        {
            throw new WorkflowForbiddenException($"You are not allowed to access workflow '{definition.Key}'.");
        }
    }

    private static void EnsureActive(WorkflowInstance instance)
    {
        if (instance.Status != WorkflowInstanceStatus.Active)
        {
            throw new WorkflowConflictException($"The instance is {instance.Status}.");
        }
    }

    /// <summary>Loads the instance and applies the read checks (roles, owner mode, expected workflow → 403/404).</summary>
    private async Task<WorkflowInstance> FindAccessibleAsync(Guid id, string? workflow, WorkflowUser user, CancellationToken cancellationToken)
    {
        var instance = await FindAsync(id, cancellationToken);
        EnsureCanAccess(registry.Get(instance.WorkflowKey, instance.WorkflowVersion), instance, user);
        if (workflow is not null && !string.Equals(instance.WorkflowKey, workflow, StringComparison.Ordinal))
        {
            throw new WorkflowNotFoundException($"Instance '{id}' does not belong to workflow '{workflow}'.");
        }

        return instance;
    }

    /// <summary>
    /// Response of a command, built from the committed in-memory run. Scheduled jobs are only read from the database when
    /// the instance stayed in the state it was claimed in (their jobs were not created by this run).
    /// </summary>
    private async Task<WorkflowInstanceModel> ToModelAsync(Run run, CancellationToken cancellationToken)
    {
        var instance = run.Instance;
        List<ScheduledTransitionModel> scheduled;
        if (!run.ClearAllJobs && instance.State == run.InitialState && !run.LeftStates.Contains(instance.State))
        {
            scheduled = await db.Jobs.AsNoTracking()
                .Where(j => j.InstanceId == instance.Id && j.Kind == WorkflowJobKind.Scheduled)
                .OrderBy(j => j.DueAt)
                .Select(j => new ScheduledTransitionModel { Key = j.TransitionKey!, DueAt = j.DueAt })
                .ToListAsync(cancellationToken);
        }
        else
        {
            scheduled = run.ClearAllJobs
                ? []
                : run.NewJobs
                    .Where(j => j.Kind == WorkflowJobKind.Scheduled && j.State == instance.State)
                    .OrderBy(j => j.DueAt)
                    .Select(j => new ScheduledTransitionModel { Key = j.TransitionKey!, DueAt = j.DueAt })
                    .ToList();
        }

        var activeSubFlow = instance.WaitingForInstanceId is { } waitingId
            ? run.ActiveSubFlowModel ?? await LoadActiveSubFlowAsync(waitingId, run.User, cancellationToken)
            : null;
        return ToModel(instance, run.User, scheduled, activeSubFlow, run.AgentRuns);
    }

    private async Task<WorkflowInstance> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
        ?? throw new WorkflowNotFoundException($"Instance '{id}' does not exist.");

    private async Task<WorkflowInstanceModel> ToModelAsync(WorkflowInstance instance, WorkflowUser user, CancellationToken cancellationToken)
    {
        var scheduled = await db.Jobs.AsNoTracking()
            .Where(j => j.InstanceId == instance.Id && j.Kind == WorkflowJobKind.Scheduled)
            .OrderBy(j => j.DueAt)
            .Select(j => new ScheduledTransitionModel { Key = j.TransitionKey!, DueAt = j.DueAt })
            .ToListAsync(cancellationToken);

        var activeSubFlow = instance.WaitingForInstanceId is { } waitingId
            ? await LoadActiveSubFlowAsync(waitingId, user, cancellationToken)
            : null;

        var definition = registry.Get(instance.WorkflowKey, instance.WorkflowVersion);
        var agentRuns = HasAgentOrMcpTasks(definition)
            ? await LoadAgentRunsAsync(instance.Id, cancellationToken)
            : null;
        return ToModel(instance, user, scheduled, activeSubFlow, agentRuns);
    }

    private async Task<WorkflowInstanceModel?> LoadActiveSubFlowAsync(Guid childId, WorkflowUser user, CancellationToken cancellationToken)
    {
        var child = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == childId, cancellationToken);
        return child is null ? null : await ToModelAsync(child, user, cancellationToken);
    }

    private WorkflowInstanceModel ToModel(
        WorkflowInstance instance,
        WorkflowUser user,
        List<ScheduledTransitionModel> scheduled,
        WorkflowInstanceModel? activeSubFlow = null,
        List<WorkflowAgentRunModel>? agentRuns = null)
    {
        var definition = registry.Get(instance.WorkflowKey, instance.WorkflowVersion);
        var state = definition.FindState(instance.State);

        var (agentsObj, agentRunsArr, toolCallsArr) = BuildAgentsScope(agentRuns);
        var data = JsonPath.ParseObject(instance.Data);
        var scope = RuleScope(definition, instance, data, user, null, agentsObj, agentRunsArr, toolCallsArr);
        var available = instance.Status == WorkflowInstanceStatus.Active
            ? AvailableIn(definition, instance.State)
                .Where(t => t.Trigger == TriggerType.Manual && CanExecute(t, definition, instance, data, user))
                .Where(t => instance.WaitingForInstanceId is null || IsCancellation(definition, t))
                .Where(t => IsRuleSatisfied(t, definition, instance, data, user))
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
                .ToList()
            : [];

        var projectedData = JsonSchemaValidator.ProjectData(data, definition.DataSchema, user);

        string? pendingTransitionKey = null;
        JsonObject? pendingInputModel = null;
        JsonObject? pendingInputSchema = null;
        if (instance.Status == WorkflowInstanceStatus.Faulted && !string.IsNullOrEmpty(instance.PendingTransition))
        {
            pendingTransitionKey = instance.PendingTransition;
            var pendingTransition = pendingTransitionKey == BackgroundTransition
                ? null
                : ResolveTransition(definition, instance, pendingTransitionKey);
            var canRetryPending = pendingTransitionKey == BackgroundTransition
                || (pendingTransition is not null && CanExecute(pendingTransition, definition, instance, data, user));
            if (canRetryPending)
            {
                if (pendingTransition?.Input?.Schema is { } trSchema)
                {
                    pendingInputSchema = ProjectInputSchema(trSchema, scope);
                    var rawPending = string.IsNullOrWhiteSpace(instance.PendingInput) ? null : JsonPath.ParseObject(instance.PendingInput);
                    var extracted = Endpoints.WorkflowHttpInput.ExtractBody(rawPending, trSchema);
                    if (extracted is { Count: > 0 })
                    {
                        pendingInputModel = JsonSchemaValidator.ProjectData((JsonObject)extracted.DeepClone(), trSchema, user);
                    }
                    else if (pendingTransition.Input.Into is { Length: > 0 } into && JsonPath.Get(projectedData, into) is JsonObject intoObj)
                    {
                        pendingInputModel = (JsonObject)intoObj.DeepClone();
                    }
                    else
                    {
                        pendingInputModel = (JsonObject)projectedData.DeepClone();
                    }
                }
                else
                {
                    pendingInputSchema = ProjectInputSchema(definition.DataSchema, scope);
                    if (pendingTransitionKey != BackgroundTransition
                        && !string.IsNullOrWhiteSpace(instance.PendingInput)
                        && JsonPath.ParseObject(instance.PendingInput) is { Count: > 0 } rawPending)
                    {
                        var extracted = Endpoints.WorkflowHttpInput.ExtractBody(rawPending, null) ?? rawPending;
                        var merged = (JsonObject)projectedData.DeepClone();
                        JsonPath.Merge(merged, JsonSchemaValidator.ProjectData(extracted, definition.DataSchema, user));
                        pendingInputModel = merged;
                    }
                    else
                    {
                        pendingInputModel = (JsonObject)projectedData.DeepClone();
                    }
                }

                pendingInputSchema = EnsureEditableProperties(pendingInputSchema, pendingInputModel);
            }
        }

        return new WorkflowInstanceModel
        {
            Id = instance.Id,
            Key = instance.BusinessKey,
            Workflow = instance.WorkflowKey,
            WorkflowVersion = instance.WorkflowVersion,
            State = instance.State,
            StateTitle = state?.Title ?? (string.IsNullOrEmpty(instance.State) ? "Başlangıç" : null),
            StateType = state?.Type.ToString() ?? string.Empty,
            StateSubType = state?.SubType.ToString() ?? string.Empty,
            View = state?.Type == StateType.SubFlow
                ? activeSubFlow?.View
                : (string.IsNullOrWhiteSpace(state?.View?.Component) ? null : state.View.Component),
            Layout = state?.Type == StateType.SubFlow
                ? activeSubFlow?.Layout
                : (string.IsNullOrWhiteSpace(state?.View?.Layout) ? null : state.View.Layout),
            Status = instance.Status,
            Data = projectedData,
            DataVersion = instance.DataVersion,
            ParentInstanceId = instance.ParentInstanceId,
            WaitingForInstanceId = instance.WaitingForInstanceId,
            ActiveSubFlow = activeSubFlow,
            Error = instance.Error is null ? null : options.Value.ExposeErrorDetails ? instance.Error : HiddenErrorText,
            PendingTransition = pendingTransitionKey,
            PendingInput = pendingInputModel,
            PendingInputSchema = pendingInputSchema,
            StartedBy = instance.StartedBy,
            CreatedAt = instance.CreatedAt,
            UpdatedAt = instance.UpdatedAt,
            CompletedAt = instance.CompletedAt,
            AvailableTransitions = available,
            ScheduledTransitions = scheduled,
            Agents = agentsObj,
            AgentRuns = agentRuns ?? [],
            ToolCalls = agentRuns is { Count: > 0 } ? [.. agentRuns.SelectMany(r => r.ToolCalls)] : [],
        };
    }

    private static JsonObject? EnsureEditableProperties(JsonObject? schema, JsonObject? sample)
    {
        if (schema?["properties"] is JsonObject { Count: > 0 })
        {
            return schema;
        }

        if (sample is not { Count: > 0 })
        {
            return schema;
        }

        var props = new JsonObject();
        foreach (var (key, node) in sample)
        {
            if (key.StartsWith('$') || node is JsonObject or JsonArray)
            {
                continue;
            }

            var propType = node is JsonValue val
                ? val.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.Number => "number",
                    System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
                    _ => "string",
                }
                : "string";
            props[key] = new JsonObject
            {
                ["type"] = propType,
                ["title"] = key,
            };
        }

        if (props.Count == 0)
        {
            return schema;
        }

        var result = schema is not null ? (JsonObject)schema.DeepClone() : new JsonObject { ["type"] = "object" };
        result["type"] ??= "object";
        result["properties"] = props;
        return result;
    }

    private static bool HasAgentOrMcpTasks(WorkflowDefinition definition)
    {
        static bool IsAgentOrMcp(TaskDefinition t) =>
            string.Equals(t.Task, "agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "aiAgent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "ai-agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "mcp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "mcp-call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "mcp-tool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "mcp-resource", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Task, "mcp-prompt", StringComparison.OrdinalIgnoreCase);

        if (definition.Start.OnExecute.Any(IsAgentOrMcp)
            || definition.SharedTransitions.Any(tr => tr.OnExecute.Any(IsAgentOrMcp)))
        {
            return true;
        }

        foreach (var state in definition.States)
        {
            if (state.OnEntry.Any(IsAgentOrMcp)
                || state.OnExit.Any(IsAgentOrMcp)
                || state.Transitions.Any(tr => tr.OnExecute.Any(IsAgentOrMcp)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<List<WorkflowAgentRunModel>> LoadAgentRunsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var runRows = await db.AgentRuns.AsNoTracking()
            .Where(r => r.InstanceId == instanceId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        if (runRows.Count == 0)
        {
            return [];
        }

        var toolCallRows = await db.AgentToolCalls.AsNoTracking()
            .Where(t => t.InstanceId == instanceId)
            .OrderBy(t => t.Order)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var callsByRun = toolCallRows
            .GroupBy(t => t.RunId)
            .ToDictionary(g => g.Key, g => g.Select(t => new WorkflowAgentToolCallModel
            {
                Id = t.Id,
                RunId = t.RunId,
                AgentName = t.AgentName,
                TaskKey = t.TaskKey,
                CallId = t.CallId,
                Name = t.ToolName,
                Arguments = TryParseJsonNode(t.Arguments),
                Result = TryParseJsonNode(t.Result),
                Order = t.Order,
                CreatedAt = t.CreatedAt,
            }).ToList());

        var models = new List<WorkflowAgentRunModel>(runRows.Count);
        foreach (var row in runRows)
        {
            var allowed = new List<WorkflowTransitionOptionModel>();
            if (!string.IsNullOrWhiteSpace(row.AllowedTransitions) && TryParseJsonNode(row.AllowedTransitions) is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject obj && obj["key"]?.GetValue<string>() is { Length: > 0 } k)
                    {
                        allowed.Add(new WorkflowTransitionOptionModel
                        {
                            Key = k,
                            Title = obj["title"]?.GetValue<string>(),
                            Description = obj["description"]?.GetValue<string>(),
                            Target = obj["target"]?.GetValue<string>(),
                        });
                    }
                    else if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    {
                        allowed.Add(new WorkflowTransitionOptionModel { Key = s });
                    }
                }
            }

            models.Add(new WorkflowAgentRunModel
            {
                Id = row.Id,
                State = row.State,
                TransitionKey = row.TransitionKey,
                TaskKey = row.TaskKey,
                AgentName = row.AgentName,
                Mode = row.Mode,
                SelectedTransition = row.SelectedTransition,
                AllowedTransitions = allowed,
                ResponseId = row.ResponseId,
                FinishReason = row.FinishReason,
                InputTokens = row.InputTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                ToolCallCount = row.ToolCallCount,
                Output = TryParseJsonNode(row.Output),
                CreatedAt = row.CreatedAt,
                ToolCalls = callsByRun.TryGetValue(row.Id, out var calls) ? calls : [],
            });
        }

        return models;
    }

    private static JsonNode? TryParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return JsonValue.Create(json);
        }
    }

    internal static (JsonObject Agents, JsonArray AgentRuns, JsonArray ToolCalls) BuildAgentsScope(IReadOnlyList<WorkflowAgentRunModel>? runs)
    {
        var agentsObj = new JsonObject();
        var runsArray = new JsonArray();
        var allToolCallsArray = new JsonArray();

        if (runs is null || runs.Count == 0)
        {
            return (agentsObj, runsArray, allToolCallsArray);
        }

        foreach (var run in runs)
        {
            var entry = new JsonObject();
            if (run.Output is JsonObject outputObj)
            {
                foreach (var (k, v) in outputObj)
                {
                    entry[k] = v?.DeepClone();
                }

                entry["output"] = outputObj.DeepClone();
            }
            else if (run.Output is not null)
            {
                entry["output"] = run.Output.DeepClone();
            }

            var runToolCalls = new JsonArray();
            foreach (var tc in run.ToolCalls)
            {
                var tcNode = new JsonObject
                {
                    ["id"] = tc.Id.ToString(),
                    ["runId"] = tc.RunId.ToString(),
                    ["agent"] = tc.AgentName,
                    ["taskKey"] = tc.TaskKey,
                    ["callId"] = tc.CallId,
                    ["name"] = tc.Name,
                    ["arguments"] = tc.Arguments?.DeepClone(),
                    ["result"] = tc.Result?.DeepClone(),
                    ["order"] = tc.Order,
                    ["createdAt"] = tc.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                };
                runToolCalls.Add(tcNode);
                allToolCallsArray.Add(tcNode.DeepClone());
            }

            var allowedArr = new JsonArray([.. run.AllowedTransitions.Select(t => (JsonNode?)new JsonObject
            {
                ["key"] = t.Key,
                ["title"] = t.Title,
                ["description"] = t.Description,
                ["target"] = t.Target,
            })]);
            var evaluatedAt = run.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

            entry["id"] = run.Id.ToString();
            entry["agent"] = run.AgentName;
            entry["taskKey"] = run.TaskKey;
            entry["state"] = run.State;
            entry["transitionKey"] = run.TransitionKey;
            entry["mode"] = run.Mode;
            entry["selectedTransition"] = run.SelectedTransition;
            entry["nextTransition"] = run.SelectedTransition;
            entry["allowedTransitions"] = allowedArr;
            entry["responseId"] = run.ResponseId;
            entry["finishReason"] = run.FinishReason;
            entry["inputTokens"] = run.InputTokens;
            entry["outputTokens"] = run.OutputTokens;
            entry["totalTokens"] = run.TotalTokens;
            entry["toolCallCount"] = run.ToolCallCount;
            entry["evaluatedAt"] = evaluatedAt;
            entry["createdAt"] = evaluatedAt;
            entry["toolCalls"] = runToolCalls;

            runsArray.Add(entry.DeepClone());

            if (!string.IsNullOrWhiteSpace(run.AgentName))
            {
                agentsObj[run.AgentName] = entry.DeepClone();
                var camelAgent = ToCamelIdentifier(run.AgentName);
                if (!string.IsNullOrWhiteSpace(camelAgent))
                {
                    agentsObj[camelAgent] = entry.DeepClone();
                }
            }

            if (!string.IsNullOrWhiteSpace(run.TaskKey))
            {
                agentsObj[run.TaskKey] = entry.DeepClone();
                var camelTask = ToCamelIdentifier(run.TaskKey);
                if (!string.IsNullOrWhiteSpace(camelTask))
                {
                    agentsObj[camelTask] = entry.DeepClone();
                }
            }
        }

        return (agentsObj, runsArray, allToolCallsArray);
    }

    private static string ToCamelIdentifier(string value)
    {
        var parts = value.Split(['-', '_', '.', ' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var first = parts[0].Length > 1
            ? char.ToLowerInvariant(parts[0][0]) + parts[0][1..]
            : parts[0].ToLowerInvariant();
        if (parts.Length == 1)
        {
            return first;
        }

        return first + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : string.Empty)));
    }

    private JsonObject? ProjectInputSchema(JsonObject? schema, ExpressionScope scope)
    {
        if (schema is null)
        {
            return null;
        }

        var clone = (JsonObject)schema.DeepClone();
        if (clone["properties"] is not JsonObject properties)
        {
            return clone;
        }

        foreach (var (_, propNode) in properties)
        {
            if (propNode is not JsonObject prop)
            {
                continue;
            }

            if (prop["default"] is JsonValue defVal
                && defVal.TryGetValue<string>(out var defStr)
                && ExpressionEvaluator.IsTemplate(defStr))
            {
                try
                {
                    prop["default"] = expressions.Render(defVal, scope);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // keep static template when evaluation fails
                }
            }

            if (prop["x-enumFrom"] is not { } enumFromNode)
            {
                continue;
            }

            prop.Remove("x-enumFrom");
            var values = ResolveEnumFrom(enumFromNode, scope);
            if (values.Count > 0)
            {
                prop["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
                prop["default"] ??= JsonValue.Create(values[^1]);
            }
        }

        return clone;
    }

    internal void AttachEnumFromLabels(JsonObject? schema, ExpressionScope scope)
    {
        if (schema?["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (_, propNode) in properties)
        {
            if (propNode is JsonObject prop && prop["x-enumFrom"] is { } enumFromNode)
            {
                _ = ResolveEnumFrom(enumFromNode, scope);
            }
        }
    }

    private List<string> ResolveEnumFrom(JsonNode enumFromNode, ExpressionScope scope)
    {
        if (enumFromNode is JsonObject specObj
            && specObj["source"] is JsonValue srcVal
            && srcVal.TryGetValue<string>(out var sourcePath)
            && !string.IsNullOrWhiteSpace(sourcePath))
        {
            if (JsonPath.Get(scope.Data, sourcePath) is not JsonArray objArr || objArr.Count == 0)
            {
                return [];
            }

            var format = specObj["format"] is JsonValue fmtVal && fmtVal.TryGetValue<string>(out var fmt) ? fmt : null;
            var entries = new List<(JsonObject? Item, string Label)>(objArr.Count);
            for (var i = 0; i < objArr.Count; i++)
            {
                var current = objArr[i];
                if (!string.IsNullOrWhiteSpace(format))
                {
                    var itemObj = current is JsonObject jo
                        ? (JsonObject)jo.DeepClone()
                        : new JsonObject { ["value"] = current?.DeepClone() };
                    itemObj["rowNumber"] ??= i + 1;
                    var itemScope = new ExpressionScope
                    {
                        Data = scope.Data,
                        Instance = scope.Instance,
                        User = scope.User,
                        Now = scope.Now,
                        Item = itemObj,
                        Index = i,
                    };
                    var rendered = expressions.RenderText(format, itemScope);
                    if (!string.IsNullOrWhiteSpace(rendered))
                    {
                        entries.Add((current as JsonObject, rendered));
                    }
                }
                else
                {
                    var text = current is JsonValue v && v.TryGetValue<string>(out var s) ? s : current?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        entries.Add((current as JsonObject, text));
                    }
                }
            }

            return FinalizeEnumEntries(entries);
        }

        if (enumFromNode is JsonValue pathVal
            && pathVal.TryGetValue<string>(out var enumFrom)
            && !string.IsNullOrWhiteSpace(enumFrom))
        {
            var lastDot = enumFrom.LastIndexOf('.');
            var arrayPath = lastDot > 0 ? enumFrom[..lastDot] : enumFrom;
            var fieldName = lastDot > 0 ? enumFrom[(lastDot + 1)..] : null;
            if (JsonPath.Get(scope.Data, arrayPath) is not JsonArray arr || arr.Count == 0)
            {
                return [];
            }

            var entries = new List<(JsonObject? Item, string Label)>(arr.Count);
            foreach (var item in arr)
            {
                var raw = fieldName is not null && item is JsonObject itemObj ? itemObj[fieldName] : item;
                var text = raw is JsonValue val && val.TryGetValue<string>(out var s) ? s : raw?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    entries.Add((item as JsonObject, text));
                }
            }

            return FinalizeEnumEntries(entries);
        }

        return [];
    }

    private static List<string> FinalizeEnumEntries(List<(JsonObject? Item, string Label)> entries)
    {
        var hasDuplicates = entries.Select(e => e.Label).Distinct(StringComparer.Ordinal).Count() != entries.Count;
        var result = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (item, rawLabel) = entries[i];
            var label = hasDuplicates ? $"{i + 1} - {rawLabel}" : rawLabel;
            if (item is not null)
            {
                JsonDynamic.RegisterFormattedLabel(item, label);
            }

            result.Add(label);
        }

        return result;
    }
}
