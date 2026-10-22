using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Runtime;
using Middt.Framework.Workflow.Schema;

namespace Middt.Framework.Workflow.Tasks;

/// <summary>
/// <c>loadWorkflow</c>: loads another workflow instance's JSON <c>Data</c>, metadata, optional AI agent outputs
/// (<c>includeAgents: true</c>), and optional transition history (<c>includeHistory: true</c>) by business <c>key</c>,
/// instance <c>id</c>, or instance data condition (<c>where</c>) into the current workflow's data:
/// <code>
/// {
///   "workflow": "kyc-verification",
///   "where": "target.taxNumber == data.taxNumber",
///   "select": ["companyName", "riskScore", "approvedLimit"],
///   "includeAgents": true,
///   "into": "kyc"
/// }
/// </code>
/// </summary>
public sealed class LoadWorkflowTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["workflow"]?.GetValue<string>() is not { Length: > 0 })
        {
            yield return "loadWorkflow: 'workflow' is required.";
        }

        if (!CrossWorkflowTaskHelpers.HasTargetSelector(task.Config))
        {
            yield return "loadWorkflow: one of 'key', 'id', or 'where' is required.";
        }

        var hasInto = task.Config["into"]?.GetValue<string>() is { Length: > 0 };
        var hasSelectObject = task.Config["select"] is JsonObject { Count: > 0 };
        if (!hasInto && !hasSelectObject)
        {
            yield return "loadWorkflow: 'into' is required (unless 'select' is a mapping object).";
        }

        foreach (var error in CrossWorkflowTaskHelpers.ValidateTargetSelector(task.Config, expressions, "loadWorkflow"))
        {
            yield return error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<WorkflowDbContext>();
        var workflow = context.Config["workflow"]!.GetValue<string>();
        var into = context.Config["into"]?.GetValue<string>();
        var key = context.RenderText("key")?.Trim();
        var idText = context.RenderText("id")?.Trim();
        var stateFilter = context.RenderText("state")?.Trim();
        var statusFilter = CrossWorkflowTaskHelpers.ParseStatusFilter(context.RenderText("status"));
        var failIfNotFound = context.Config["failIfNotFound"]?.GetValue<bool>() == true;

        var instance = await CrossWorkflowTaskHelpers.FindSingleInstanceAsync(
            db,
            context,
            workflow,
            idText,
            key,
            stateFilter,
            statusFilter,
            cancellationToken);

        if (instance is null)
        {
            if (failIfNotFound)
            {
                throw new WorkflowTaskException(
                    $"loadWorkflow: no matching instance found in workflow '{workflow}'.",
                    statusCode: 404);
            }

            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["id"] = idText,
                    ["key"] = key,
                    ["workflow"] = workflow,
                    ["found"] = false,
                });
            }

            return;
        }

        var loaded = CrossWorkflowTaskHelpers.BuildInstancePayload(instance);

        if (context.Config["includeAgents"]?.GetValue<bool>() == true)
        {
            await CrossWorkflowTaskHelpers.AttachAgentOutputsAsync(db, instance.Id, instance.WorkflowKey, loaded, cancellationToken);
        }

        if (context.Config["includeHistory"]?.GetValue<bool>() == true)
        {
            await CrossWorkflowTaskHelpers.AttachHistorySummaryAsync(db, instance.Id, instance.WorkflowKey, loaded, cancellationToken);
        }

        if (context.Config["select"] is { } selectNode)
        {
            var projected = CrossWorkflowTaskHelpers.ApplySelectProjection(loaded, selectNode, context, includeMetadata: !string.IsNullOrEmpty(into));
            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, projected);
            }
            else
            {
                foreach (var (targetPath, val) in projected)
                {
                    JsonPath.Set(context.Data, targetPath, val?.DeepClone());
                }
            }

            return;
        }

        JsonPath.Set(context.Data, into!, loaded);
    }
}

/// <summary>
/// <c>queryWorkflow</c>: queries multiple instances of a workflow using a unified <c>where</c> condition (automatically
/// pushing indexable predicates to PostgreSQL partition SQL and evaluating the full expression on <c>WorkflowInstanceData</c>),
/// sorting, projection (<c>select</c>), and aggregations (<c>aggregate</c>: <c>count</c>, <c>sum</c>, <c>avg</c>, <c>min</c>, <c>max</c>).
/// <code>
/// {
///   "workflow": "loan-application",
///   "state": "approved",
///   "where": "target.customerNumber == data.customerNumber &amp;&amp; target.requestedAmount >= 10000",
///   "orderBy": "requestedAmount",
///   "descending": true,
///   "aggregate": {
///     "totalExposure": { "op": "sum", "field": "requestedAmount" },
///     "maxLimit": { "op": "max", "field": "approvedLimit" },
///     "loanCount": { "op": "count" }
///   },
///   "into": "customerLoanHistory"
/// }
/// </code>
/// </summary>
public sealed class QueryWorkflowTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["workflow"]?.GetValue<string>() is not { Length: > 0 })
        {
            yield return "queryWorkflow: 'workflow' is required.";
        }

        if (task.Config["into"]?.GetValue<string>() is not { Length: > 0 })
        {
            yield return "queryWorkflow: 'into' is required.";
        }

        foreach (var error in CrossWorkflowTaskHelpers.ValidateTargetSelector(task.Config, expressions, "queryWorkflow"))
        {
            yield return error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var db = context.Services.GetRequiredService<WorkflowDbContext>();
        var workflow = context.Config["workflow"]!.GetValue<string>();
        var into = context.Config["into"]!.GetValue<string>();
        var stateFilter = context.RenderText("state")?.Trim();
        var statusFilter = CrossWorkflowTaskHelpers.ParseStatusFilter(context.RenderText("status"));
        var keyFilter = context.RenderText("key")?.Trim();
        var excludeSelf = context.Config["excludeSelf"]?.GetValue<bool>() == true
                          || context.Config["excludeCurrent"]?.GetValue<bool>() == true;
        var take = Math.Clamp(context.Config["take"]?.GetValue<int>() ?? 50, 1, 500);
        var orderBy = context.Config["orderBy"]?.GetValue<string>()?.Trim();
        var descending = context.Config["descending"]?.GetValue<bool>() ?? true;
        var firstOnly = context.Config["first"]?.GetValue<bool>() == true;
        var includeAgents = context.Config["includeAgents"]?.GetValue<bool>() == true;

        var matchedInstances = await CrossWorkflowTaskHelpers.FindMatchingInstancesAsync(
            db,
            context,
            workflow,
            idText: null,
            key: keyFilter,
            stateFilter: stateFilter,
            statusFilter: statusFilter,
            excludeSelf: excludeSelf,
            maxScan: Math.Max(take * 4, 200),
            cancellationToken: cancellationToken);

        var matchedPayloads = new List<JsonObject>(matchedInstances.Count);
        foreach (var (instance, payload) in matchedInstances)
        {
            if (includeAgents)
            {
                await CrossWorkflowTaskHelpers.AttachAgentOutputsAsync(db, instance.Id, instance.WorkflowKey, payload, cancellationToken);
            }

            matchedPayloads.Add(payload);
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            matchedPayloads = CrossWorkflowTaskHelpers.SortPayloads(matchedPayloads, orderBy, descending);
        }

        var totalMatched = matchedPayloads.Count;
        var aggregates = CrossWorkflowTaskHelpers.ComputeAggregates(matchedPayloads, context.Config["aggregate"] as JsonObject);

        var limited = matchedPayloads.Take(take).ToList();
        var selectNode = context.Config["select"];
        var finalItems = new JsonArray();
        foreach (var item in limited)
        {
            var projected = selectNode is not null
                ? CrossWorkflowTaskHelpers.ApplySelectProjection(item, selectNode, context, includeMetadata: true)
                : item;
            finalItems.Add(projected);
        }

        if (firstOnly)
        {
            if (finalItems.Count > 0 && finalItems[0] is JsonObject firstObj)
            {
                JsonPath.Set(context.Data, into, firstObj.DeepClone());
            }
            else
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["workflow"] = workflow,
                    ["found"] = false,
                });
            }

            return;
        }

        var resultObj = new JsonObject
        {
            ["workflow"] = workflow,
            ["count"] = totalMatched,
            ["exists"] = totalMatched > 0,
            ["items"] = finalItems,
            ["first"] = finalItems.Count > 0 ? finalItems[0]?.DeepClone() : null,
        };

        if (aggregates.Count > 0)
        {
            resultObj["aggregates"] = aggregates;
            foreach (var (aggKey, aggVal) in aggregates)
            {
                if (!resultObj.ContainsKey(aggKey))
                {
                    resultObj[aggKey] = aggVal?.DeepClone();
                }
            }
        }

        JsonPath.Set(context.Data, into, resultObj);
    }
}

/// <summary>
/// <c>updateWorkflow</c>: updates or patches another workflow's instance(s) matched by <c>id</c>, <c>key</c>, or
/// instance data condition (<c>where</c>, with <c>all: true</c> to update all matching instances) using <c>values</c>
/// (expressions with <c>target</c> and <c>data</c>), <c>merge</c> (template object), or <c>patch</c> (RFC 6902 JSON Patch).
/// <code>
/// {
///   "workflow": "customer-onboarding",
///   "where": "target.customerNumber == data.customerNumber",
///   "values": {
///     "usedCreditLimit": "(target.usedCreditLimit ?? 0) + data.requestedAmount",
///     "lastLoanId": "instance.id"
///   },
///   "into": "updatedCustomer"
/// }
/// </code>
/// </summary>
public sealed class UpdateWorkflowTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["workflow"]?.GetValue<string>() is not { Length: > 0 })
        {
            yield return "updateWorkflow: 'workflow' is required.";
        }

        if (!CrossWorkflowTaskHelpers.HasTargetSelector(task.Config))
        {
            yield return "updateWorkflow: one of 'key', 'id', or 'where' is required.";
        }

        var hasValues = task.Config["values"] is JsonObject { Count: > 0 };
        var hasMerge = task.Config["merge"] is JsonObject { Count: > 0 };
        var hasPatch = task.Config["patch"] is JsonArray { Count: > 0 };
        if (!hasValues && !hasMerge && !hasPatch)
        {
            yield return "updateWorkflow: at least one of 'values', 'merge', or 'patch' is required.";
        }

        if (task.Config["values"] is JsonObject values)
        {
            foreach (var (path, exprNode) in values)
            {
                if (exprNode?.GetValue<string>() is not { } exprText || expressions.Validate(exprText) is { } err)
                {
                    yield return $"updateWorkflow: value of '{path}' is not a valid expression.";
                }
            }
        }

        foreach (var error in CrossWorkflowTaskHelpers.ValidateTargetSelector(task.Config, expressions, "updateWorkflow")
                     .Concat(expressions.ValidateTemplate(task.Config["merge"]))
                     .Concat(expressions.ValidateTemplate(task.Config["patch"])))
        {
            yield return error.StartsWith("updateWorkflow:", StringComparison.Ordinal) ? error : "updateWorkflow: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var workflow = context.Config["workflow"]!.GetValue<string>();
        var into = context.Config["into"]?.GetValue<string>();
        var key = context.RenderText("key")?.Trim();
        var idText = context.RenderText("id")?.Trim();
        var stateFilter = context.RenderText("state")?.Trim();
        var statusFilter = CrossWorkflowTaskHelpers.ParseStatusFilter(context.RenderText("status"));
        var failIfNotFound = context.Config["failIfNotFound"]?.GetValue<bool>() ?? true;
        var validateSchema = context.Config["validateSchema"]?.GetValue<bool>() == true;
        var updateAll = context.Config["all"]?.GetValue<bool>() == true;
        var targetInto = context.Config["targetInto"]?.GetValue<string>();

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var time = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var registry = scope.ServiceProvider.GetService<WorkflowRegistry>();

        var matched = await CrossWorkflowTaskHelpers.FindMatchingInstancesAsync(
            db,
            context,
            workflow,
            idText,
            key,
            stateFilter,
            statusFilter,
            excludeSelf: false,
            maxScan: updateAll ? 200 : 50,
            cancellationToken: cancellationToken);

        if (matched.Count == 0)
        {
            if (failIfNotFound)
            {
                throw new WorkflowTaskException(
                    $"updateWorkflow: target instance not found in workflow '{workflow}'.",
                    statusCode: 404);
            }

            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["id"] = idText,
                    ["key"] = key,
                    ["workflow"] = workflow,
                    ["found"] = false,
                    ["updatedCount"] = 0,
                });
            }

            return;
        }

        var targetsToUpdate = updateAll ? matched : [matched[0]];
        var updatedPayloads = new JsonArray();
        var now = time.GetUtcNow().UtcDateTime;

        foreach (var (targetSummary, _) in targetsToUpdate)
        {
            var instance = await db.Instances.FirstAsync(
                i => i.Id == targetSummary.Id && i.WorkflowKey == targetSummary.WorkflowKey,
                cancellationToken);

            var targetData = JsonPath.ParseObject(instance.Data);
            var targetScopePayload = CrossWorkflowTaskHelpers.BuildInstancePayload(instance);
            var evalScope = CrossWorkflowTaskHelpers.CreateTargetScope(context.Scope, targetScopePayload);

            if (context.Config["merge"] is not null && context.Expressions.Render(context.Config["merge"], evalScope) is JsonObject mergedObj)
            {
                JsonPath.Merge(targetData, mergedObj, targetInto);
            }

            if (context.Config["values"] is JsonObject values)
            {
                foreach (var (path, exprNode) in values)
                {
                    if (exprNode?.GetValue<string>() is { } exprText)
                    {
                        var computed = context.Expressions.EvaluateToNode(exprText, evalScope);
                        JsonPath.Set(targetData, path, computed);
                        JsonPath.Set(targetScopePayload, path, computed?.DeepClone());
                    }
                }
            }

            if (context.Config["patch"] is not null && context.Expressions.Render(context.Config["patch"], evalScope) is JsonArray renderedPatch)
            {
                JsonNode patchTarget = !string.IsNullOrEmpty(targetInto)
                    ? (JsonPath.Get(targetData, targetInto) as JsonObject ?? new JsonObject())
                    : targetData;

                try
                {
                    JsonPatch.ApplyStrict(patchTarget, renderedPatch, out _);
                }
                catch (JsonPatchException ex)
                {
                    throw new WorkflowTaskException($"updateWorkflow: JSON Patch failed on '{workflow}': {ex.Message}", ex, statusCode: ex.IsTestFailure ? 409 : 400);
                }

                if (!string.IsNullOrEmpty(targetInto))
                {
                    JsonPath.Set(targetData, targetInto, patchTarget);
                }
            }

            if (validateSchema && registry is not null)
            {
                var definition = registry.Get(instance.WorkflowKey, instance.WorkflowVersion);
                if (definition.DataSchema is { } schema)
                {
                    var errors = JsonSchemaValidator.Validate(targetData, schema);
                    if (errors.Count > 0)
                    {
                        throw new WorkflowTaskException($"updateWorkflow: schema validation failed on '{workflow}': {string.Join("; ", errors)}", statusCode: 400);
                    }
                }
            }

            var newJson = targetData.ToJsonString();
            if (newJson != instance.Data)
            {
                instance.UpdatedAt = now;
                instance.Data = newJson;
                instance.DataVersion++;
                instance.Revision++;
            }

            updatedPayloads.Add(CrossWorkflowTaskHelpers.BuildInstancePayload(instance));
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(into))
        {
            if (updateAll)
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["workflow"] = workflow,
                    ["found"] = true,
                    ["updatedCount"] = updatedPayloads.Count,
                    ["items"] = updatedPayloads,
                    ["first"] = updatedPayloads.Count > 0 ? updatedPayloads[0]?.DeepClone() : null,
                });
            }
            else if (updatedPayloads.Count > 0 && updatedPayloads[0] is JsonObject singlePayload)
            {
                JsonPath.Set(context.Data, into, singlePayload.DeepClone());
            }
        }
    }
}

/// <summary>
/// <c>triggerWorkflow</c>: executes a transition (<c>transition</c>) or delivers an event message (<c>event</c>) on another
/// workflow instance (identified by <c>workflow</c> + <c>id</c>, <c>key</c>, or instance data condition <c>where</c>) with
/// rendered <c>input</c>/<c>data</c>, and writes the target workflow's resulting state and data into <c>into</c>.
/// <code>
/// {
///   "workflow": "order-fulfillment",
///   "where": "target.orderCode == data.orderCode",
///   "event": "PaymentCaptured",
///   "input": { "paymentReference": "{{ data.paymentId }}", "paidAmount": "{{ data.amount }}" },
///   "into": "orderResult"
/// }
/// </code>
/// </summary>
public sealed class TriggerWorkflowTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["workflow"]?.GetValue<string>() is not { Length: > 0 })
        {
            yield return "triggerWorkflow: 'workflow' is required.";
        }

        if (!CrossWorkflowTaskHelpers.HasTargetSelector(task.Config))
        {
            yield return "triggerWorkflow: one of 'key', 'id', or 'where' is required.";
        }

        var hasTransition = task.Config["transition"]?.GetValue<string>() is { Length: > 0 };
        var hasEvent = task.Config["event"]?.GetValue<string>() is { Length: > 0 };
        if (hasTransition == hasEvent)
        {
            yield return "triggerWorkflow: specify exactly one of 'transition' or 'event'.";
        }

        if (task.Config["bufferFor"]?.GetValue<string>() is { Length: > 0 } bufferFor
            && !bufferFor.Contains("{{", StringComparison.Ordinal)
            && !Durations.TryParse(bufferFor, out _))
        {
            yield return "triggerWorkflow: 'bufferFor' is not a valid duration.";
        }

        foreach (var error in CrossWorkflowTaskHelpers.ValidateTargetSelector(task.Config, expressions, "triggerWorkflow")
                     .Concat(expressions.ValidateTemplate(task.Config["transition"]))
                     .Concat(expressions.ValidateTemplate(task.Config["event"]))
                     .Concat(expressions.ValidateTemplate(task.Config["input"]))
                     .Concat(expressions.ValidateTemplate(task.Config["data"])))
        {
            yield return error.StartsWith("triggerWorkflow:", StringComparison.Ordinal) ? error : "triggerWorkflow: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var workflow = context.Config["workflow"]!.GetValue<string>();
        var into = context.Config["into"]?.GetValue<string>();
        var key = context.RenderText("key")?.Trim();
        var idText = context.RenderText("id")?.Trim();
        var stateFilter = context.RenderText("state")?.Trim();
        var statusFilter = CrossWorkflowTaskHelpers.ParseStatusFilter(context.RenderText("status"));
        var transitionKey = context.RenderText("transition")?.Trim();
        var eventName = context.RenderText("event")?.Trim();
        var runAsync = context.Config["runAsync"]?.GetValue<bool>() == true;
        var triggerAll = context.Config["all"]?.GetValue<bool>() == true;
        var failIfNotFound = context.Config["failIfNotFound"]?.GetValue<bool>() ?? true;
        var payload = (context.Render("input") ?? context.Render("data")) as JsonObject;

        await using var scope = context.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var actor = context.Scope.User.IsSystem
            ? context.Scope.User
            : new WorkflowUser
            {
                Id = context.Scope.User.Id,
                Name = context.Scope.User.AuditName,
                Roles = context.Scope.User.Roles,
                IsSystem = true,
            };

        var matched = await CrossWorkflowTaskHelpers.FindMatchingInstancesAsync(
            db,
            context,
            workflow,
            idText,
            key,
            stateFilter,
            statusFilter,
            excludeSelf: false,
            maxScan: triggerAll ? 200 : 50,
            cancellationToken: cancellationToken);

        if (matched.Count == 0)
        {
            if (failIfNotFound)
            {
                throw new WorkflowTaskException(
                    $"triggerWorkflow: target instance not found in workflow '{workflow}'.",
                    statusCode: 404);
            }

            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["workflow"] = workflow,
                    ["found"] = false,
                });
            }

            return;
        }

        var targets = triggerAll ? matched : [matched[0]];
        var resultsArray = new JsonArray();

        foreach (var (targetInstance, _) in targets)
        {
            WorkflowInstanceModel? resultModel;
            string outcome;

            if (!string.IsNullOrEmpty(eventName))
            {
                var bufferForText = context.RenderText("bufferFor")?.Trim();
                var bufferFor = !string.IsNullOrEmpty(bufferForText) && Durations.TryParse(bufferForText, out var parsedBuffer)
                    ? parsedBuffer
                    : TimeSpan.Zero;

                var msgResult = await engine.DeliverMessageAsync(
                    new WorkflowMessage
                    {
                        Name = eventName,
                        Workflow = workflow,
                        InstanceId = targetInstance.Id,
                        Data = payload?.DeepClone() as JsonObject,
                        BufferFor = bufferFor,
                    },
                    actor,
                    cancellationToken);

                outcome = msgResult.Outcome.ToString();
                resultModel = msgResult.Instance ?? await engine.GetAsync(workflow, msgResult.InstanceId, actor, cancellationToken);
            }
            else
            {
                resultModel = await engine.ExecuteTransitionAsync(
                    workflow,
                    targetInstance.Id,
                    transitionKey!,
                    payload?.DeepClone() as JsonObject,
                    actor,
                    runAsync,
                    cancellationToken);
                outcome = runAsync ? "Queued" : "Executed";
            }

            if (resultModel is not null)
            {
                var output = (JsonObject)resultModel.Data.DeepClone();
                output["id"] = resultModel.Id.ToString("D");
                output["key"] = resultModel.Key;
                output["workflow"] = resultModel.Workflow;
                output["version"] = resultModel.WorkflowVersion;
                output["state"] = resultModel.State;
                output["status"] = resultModel.Status.ToString();
                output["outcome"] = outcome;
                output["found"] = true;
                resultsArray.Add(output);
            }
        }

        if (!string.IsNullOrEmpty(into))
        {
            if (triggerAll)
            {
                JsonPath.Set(context.Data, into, new JsonObject
                {
                    ["workflow"] = workflow,
                    ["found"] = true,
                    ["triggeredCount"] = resultsArray.Count,
                    ["items"] = resultsArray,
                    ["first"] = resultsArray.Count > 0 ? resultsArray[0]?.DeepClone() : null,
                });
            }
            else if (resultsArray.Count > 0 && resultsArray[0] is JsonObject singleResult)
            {
                JsonPath.Set(context.Data, into, singleResult.DeepClone());
            }
        }
    }
}

internal static class CrossWorkflowTaskHelpers
{
    // Matches simple predicates in a `where` expression such as:
    //   target.customerNumber == data.customerNumber
    //   item.creditScore >= 700
    //   target.segment == "Corporate"
    private static readonly Regex SimpleComparisonRegex = new(
        @"^(?:target|item)\.(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*(?<op>==|>=|<=|>|<)\s*(?<rhs>.+)$",
        RegexOptions.Compiled);

    public static bool HasTargetSelector(JsonObject config) =>
        config["key"]?.GetValue<string>() is { Length: > 0 }
        || config["id"]?.GetValue<string>() is { Length: > 0 }
        || config["where"] is JsonValue { } wv && wv.TryGetValue<string>(out var ws) && !string.IsNullOrWhiteSpace(ws)
        || config["where"] is JsonObject { Count: > 0 }
        || config["filter"] is JsonObject { Count: > 0 };

    public static IEnumerable<string> ValidateTargetSelector(JsonObject config, WorkflowExpressionEngine expressions, string taskName)
    {
        foreach (var error in expressions.ValidateTemplate(config["key"])
                     .Concat(expressions.ValidateTemplate(config["id"]))
                     .Concat(expressions.ValidateTemplate(config["state"]))
                     .Concat(expressions.ValidateTemplate(config["status"])))
        {
            yield return $"{taskName}: {error}";
        }

        if (config["where"] is JsonValue whereVal && whereVal.TryGetValue<string>(out var whereExpr) && !string.IsNullOrWhiteSpace(whereExpr))
        {
            if (expressions.Validate(whereExpr) is { } whereErr)
            {
                yield return $"{taskName}: where: {whereErr}";
            }
        }
        else if (config["where"] is JsonObject whereObj)
        {
            foreach (var error in expressions.ValidateTemplate(whereObj))
            {
                yield return $"{taskName}: where: {error}";
            }
        }

        if (config["filter"] is JsonObject filterObj)
        {
            foreach (var error in expressions.ValidateTemplate(filterObj))
            {
                yield return $"{taskName}: filter: {error}";
            }
        }
    }

    public static WorkflowInstanceStatus? ParseStatusFilter(string? statusText) =>
        !string.IsNullOrWhiteSpace(statusText) && Enum.TryParse<WorkflowInstanceStatus>(statusText.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : null;

    public static async Task<WorkflowInstance?> FindSingleInstanceAsync(
        WorkflowDbContext db,
        WorkflowTaskContext context,
        string workflow,
        string? idText,
        string? key,
        string? stateFilter,
        WorkflowInstanceStatus? statusFilter,
        CancellationToken cancellationToken)
    {
        var matches = await FindMatchingInstancesAsync(
            db,
            context,
            workflow,
            idText,
            key,
            stateFilter,
            statusFilter,
            excludeSelf: false,
            maxScan: 50,
            cancellationToken: cancellationToken);

        return matches.Count > 0 ? matches[0].Instance : null;
    }

    public static async Task<List<(WorkflowInstance Instance, JsonObject Payload)>> FindMatchingInstancesAsync(
        WorkflowDbContext db,
        WorkflowTaskContext context,
        string workflow,
        string? idText,
        string? key,
        string? stateFilter,
        WorkflowInstanceStatus? statusFilter,
        bool excludeSelf,
        int maxScan,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idText, out var id))
        {
            var byId = await db.Instances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id && i.WorkflowKey == workflow, cancellationToken);
            return byId is not null ? [(byId, BuildInstancePayload(byId))] : [];
        }

        if (!string.IsNullOrEmpty(key) && context.Config["where"] is null && context.Config["filter"] is null)
        {
            var byKey = await db.Instances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.WorkflowKey == workflow && i.BusinessKey == key, cancellationToken);
            return byKey is not null ? [(byKey, BuildInstancePayload(byKey))] : [];
        }

        var (filterDict, whereExpr) = ExtractUnifiedWhereFilters(context);

        var registry = context.Services.GetService<WorkflowRegistry>();
        var workflowOptions = context.Services.GetService<IOptions<MiddtWorkflowOptions>>()?.Value;
        JsonObject? dataSchema = null;
        if (registry is not null)
        {
            try
            {
                dataSchema = registry.GetLatest(workflow).DataSchema;
            }
            catch (WorkflowNotFoundException)
            {
                // Allow querying instances even if definition is not loaded in memory.
            }
        }

        IQueryable<WorkflowInstance> query = db.Instances.AsNoTracking().Where(i => i.WorkflowKey == workflow);

        if (excludeSelf && context.InstanceId != Guid.Empty)
        {
            var selfId = context.InstanceId;
            query = query.Where(i => i.Id != selfId);
        }

        if (!string.IsNullOrEmpty(key))
        {
            query = query.Where(i => i.BusinessKey == key);
        }

        if (!string.IsNullOrEmpty(stateFilter))
        {
            query = query.Where(i => i.State == stateFilter);
        }

        if (statusFilter is { } s)
        {
            query = query.Where(i => i.Status == s);
        }

        var (sqlConditions, remainingFilters, createdFromUtc, createdToUtc) =
            WorkflowPartitionManager.ClassifyDataFilters(filterDict, dataSchema);

        if (sqlConditions.Count > 0 || createdFromUtc is not null || createdToUtc is not null)
        {
            query = WorkflowPartitionManager.ApplySqlDataFilters(
                query,
                db,
                workflowOptions?.Schema,
                workflow,
                sqlConditions,
                createdFromUtc,
                createdToUtc);
        }

        var candidates = await query
            .OrderByDescending(i => i.CreatedAt)
            .Take(maxScan)
            .ToListAsync(cancellationToken);

        var results = new List<(WorkflowInstance Instance, JsonObject Payload)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var payload = BuildInstancePayload(candidate);
            if (remainingFilters.Count > 0 && !WorkflowPartitionManager.MatchesDataFilters(payload, remainingFilters))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(whereExpr))
            {
                var itemScope = CreateTargetScope(context.Scope, payload);
                if (!context.Expressions.EvaluateCondition(whereExpr, itemScope))
                {
                    continue;
                }
            }

            results.Add((candidate, payload));
        }

        return results;
    }

    /// <summary>
    /// Unifies <c>where</c> (expression string or filter object) and optional legacy <c>filter</c> into a single
    /// pipeline: extracts SQL-pushable equality/range predicates from <c>where</c> expressions AND evaluates the full
    /// <c>where</c> expression against <c>WorkflowInstanceData</c>.
    /// </summary>
    public static (Dictionary<string, string?> FilterDict, string? WhereExpression) ExtractUnifiedWhereFilters(WorkflowTaskContext context)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? whereExpr = null;

        if (context.Config["filter"] is JsonObject legacyFilter)
        {
            PopulateFilterDictionary(dict, legacyFilter, context);
        }

        if (context.Config["where"] is JsonObject whereObj)
        {
            PopulateFilterDictionary(dict, whereObj, context);
        }
        else if (context.Config["where"] is JsonValue whereVal
                 && whereVal.TryGetValue<string>(out var rawWhere)
                 && !string.IsNullOrWhiteSpace(rawWhere))
        {
            whereExpr = rawWhere.Trim();
            TryExtractSqlFiltersFromExpression(whereExpr, context, dict);
        }

        return (dict, whereExpr);
    }

    private static void PopulateFilterDictionary(Dictionary<string, string?> dict, JsonObject rawFilter, WorkflowTaskContext context)
    {
        var rendered = context.Expressions.Render(rawFilter, context.Scope) as JsonObject ?? rawFilter;
        foreach (var (k, v) in rendered)
        {
            if (v is null)
            {
                continue;
            }

            dict[k] = v switch
            {
                JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>(),
                JsonValue jv when jv.GetValueKind() == JsonValueKind.True => "true",
                JsonValue jv when jv.GetValueKind() == JsonValueKind.False => "false",
                _ => v.ToJsonString(),
            };
        }
    }

    private static void TryExtractSqlFiltersFromExpression(
        string expression,
        WorkflowTaskContext context,
        Dictionary<string, string?> filterDict)
    {
        // Only extract when the expression is a conjunction (&&) without top-level OR (||) so SQL pushdown never over-filters.
        if (expression.Contains("||", StringComparison.Ordinal))
        {
            return;
        }

        var parts = expression.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var match = SimpleComparisonRegex.Match(part);
            if (!match.Success)
            {
                continue;
            }

            var prop = match.Groups["prop"].Value;
            var op = match.Groups["op"].Value;
            var rhs = match.Groups["rhs"].Value.Trim();

            // Skip if the right-hand side also references target.* or item.*
            if (rhs.Contains("target.", StringComparison.Ordinal) || rhs.Contains("item.", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var evaluatedNode = context.Expressions.EvaluateToNode(rhs, context.Scope);
                if (evaluatedNode is not JsonValue val)
                {
                    continue;
                }

                var strVal = val.GetValueKind() switch
                {
                    JsonValueKind.String => val.GetValue<string>(),
                    JsonValueKind.Number => val.ToJsonString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };

                if (string.IsNullOrEmpty(strVal))
                {
                    continue;
                }

                var filterKey = op switch
                {
                    "==" => prop,
                    ">=" => $"{prop}_gte",
                    "<=" => $"{prop}_lte",
                    ">" => $"{prop}_gt",
                    "<" => $"{prop}_lt",
                    _ => null,
                };

                if (filterKey is not null)
                {
                    filterDict.TryAdd(filterKey, strVal);
                }
            }
            catch
            {
                // If RHS cannot be pre-evaluated, in-memory `where` evaluation will handle it.
            }
        }
    }

    public static JsonObject BuildInstancePayload(WorkflowInstance instance)
    {
        var loaded = JsonNode.Parse(string.IsNullOrWhiteSpace(instance.Data) ? "{}" : instance.Data) as JsonObject ?? [];
        loaded["id"] = instance.Id.ToString("D");
        loaded["key"] = instance.BusinessKey;
        loaded["workflow"] = instance.WorkflowKey;
        loaded["version"] = instance.WorkflowVersion;
        loaded["state"] = instance.State;
        loaded["status"] = instance.Status.ToString();
        loaded["dataVersion"] = instance.DataVersion;
        loaded["createdAt"] = instance.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        loaded["updatedAt"] = instance.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        if (instance.CompletedAt is { } completedAt)
        {
            loaded["completedAt"] = completedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        loaded["found"] = true;
        return loaded;
    }

    public static async Task AttachAgentOutputsAsync(
        WorkflowDbContext db,
        Guid instanceId,
        string workflowKey,
        JsonObject targetPayload,
        CancellationToken cancellationToken)
    {
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.InstanceId == instanceId && r.WorkflowKey == workflowKey)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var agentsMap = new JsonObject();
        var runsArray = new JsonArray();

        foreach (var run in runs)
        {
            JsonNode? parsedOutput = null;
            if (!string.IsNullOrWhiteSpace(run.Output))
            {
                try
                {
                    parsedOutput = JsonNode.Parse(run.Output);
                }
                catch (JsonException)
                {
                    parsedOutput = JsonValue.Create(run.Output);
                }
            }

            if (!string.IsNullOrWhiteSpace(run.AgentName))
            {
                agentsMap[run.AgentName] = parsedOutput?.DeepClone();
            }

            if (!string.IsNullOrWhiteSpace(run.TaskKey) && !agentsMap.ContainsKey(run.TaskKey))
            {
                agentsMap[run.TaskKey] = parsedOutput?.DeepClone();
            }

            runsArray.Add(new JsonObject
            {
                ["id"] = run.Id.ToString("D"),
                ["agentName"] = run.AgentName,
                ["taskKey"] = run.TaskKey,
                ["state"] = run.State,
                ["transitionKey"] = run.TransitionKey,
                ["selectedTransition"] = run.SelectedTransition,
                ["toolCallCount"] = run.ToolCallCount,
                ["output"] = parsedOutput?.DeepClone(),
                ["createdAt"] = run.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        targetPayload["agents"] = agentsMap;
        targetPayload["agentRuns"] = runsArray;
    }

    public static async Task AttachHistorySummaryAsync(
        WorkflowDbContext db,
        Guid instanceId,
        string workflowKey,
        JsonObject targetPayload,
        CancellationToken cancellationToken)
    {
        var rows = await db.History.AsNoTracking()
            .Where(h => h.InstanceId == instanceId && h.WorkflowKey == workflowKey)
            .OrderBy(h => h.Id)
            .Select(h => new
            {
                h.TransitionKey,
                h.FromState,
                h.ToState,
                h.Trigger,
                h.ExecutedBy,
                h.DataVersion,
                h.StartedAt,
                h.FinishedAt,
            })
            .ToListAsync(cancellationToken);

        var historyArray = new JsonArray();
        foreach (var row in rows)
        {
            historyArray.Add(new JsonObject
            {
                ["transitionKey"] = row.TransitionKey,
                ["fromState"] = row.FromState,
                ["toState"] = row.ToState,
                ["trigger"] = row.Trigger,
                ["executedBy"] = row.ExecutedBy,
                ["dataVersion"] = row.DataVersion,
                ["startedAt"] = row.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["finishedAt"] = row.FinishedAt.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        targetPayload["history"] = historyArray;
    }

    public static JsonObject ApplySelectProjection(
        JsonObject source,
        JsonNode selectNode,
        WorkflowTaskContext context,
        bool includeMetadata)
    {
        var projected = new JsonObject();

        if (selectNode is JsonArray fieldList)
        {
            if (includeMetadata)
            {
                projected["id"] = source["id"]?.DeepClone();
                projected["key"] = source["key"]?.DeepClone();
                projected["workflow"] = source["workflow"]?.DeepClone();
                projected["state"] = source["state"]?.DeepClone();
                projected["status"] = source["status"]?.DeepClone();
                projected["found"] = source["found"]?.DeepClone() ?? true;
            }

            foreach (var item in fieldList)
            {
                if (item?.GetValue<string>() is { Length: > 0 } fieldPath)
                {
                    var val = JsonPath.Get(source, fieldPath);
                    JsonPath.Set(projected, fieldPath, val?.DeepClone());
                }
            }

            return projected;
        }

        if (selectNode is JsonObject mapObj)
        {
            if (includeMetadata)
            {
                projected["id"] = source["id"]?.DeepClone();
                projected["key"] = source["key"]?.DeepClone();
                projected["workflow"] = source["workflow"]?.DeepClone();
                projected["state"] = source["state"]?.DeepClone();
                projected["status"] = source["status"]?.DeepClone();
                projected["found"] = source["found"]?.DeepClone() ?? true;
            }

            var targetScope = CreateTargetScope(context.Scope, source);
            foreach (var (targetKey, sourceExprNode) in mapObj)
            {
                if (sourceExprNode?.GetValue<string>() is not { Length: > 0 } sourceExpr)
                {
                    continue;
                }

                if (JsonPath.Get(source, sourceExpr) is { } directNode)
                {
                    projected[targetKey] = directNode.DeepClone();
                }
                else
                {
                    projected[targetKey] = context.Expressions.EvaluateToNode(sourceExpr, targetScope);
                }
            }
        }

        return projected;
    }

    public static ExpressionScope CreateTargetScope(ExpressionScope current, JsonObject targetPayload) =>
        new()
        {
            Data = current.Data,
            Input = current.Input,
            Args = current.Args,
            Result = current.Result,
            Child = current.Child,
            User = current.User,
            Instance = current.Instance,
            Item = targetPayload,
            Target = targetPayload,
            Index = current.Index,
            Now = current.Now,
        };

    public static List<JsonObject> SortPayloads(List<JsonObject> items, string orderBy, bool descending)
    {
        int CompareNodes(JsonObject a, JsonObject b)
        {
            var nodeA = JsonPath.Get(a, orderBy);
            var nodeB = JsonPath.Get(b, orderBy);
            if (nodeA is null && nodeB is null)
            {
                return 0;
            }

            if (nodeA is null)
            {
                return -1;
            }

            if (nodeB is null)
            {
                return 1;
            }

            if (WorkflowPartitionManager.TryGetNodeDecimal(nodeA, out var decA)
                && WorkflowPartitionManager.TryGetNodeDecimal(nodeB, out var decB))
            {
                return decA.CompareTo(decB);
            }

            var strA = nodeA is JsonValue va && va.GetValueKind() == JsonValueKind.String ? va.GetValue<string>() : nodeA.ToString();
            var strB = nodeB is JsonValue vb && vb.GetValueKind() == JsonValueKind.String ? vb.GetValue<string>() : nodeB.ToString();
            return string.Compare(strA, strB, StringComparison.OrdinalIgnoreCase);
        }

        items.Sort((a, b) => descending ? CompareNodes(b, a) : CompareNodes(a, b));
        return items;
    }

    public static JsonObject ComputeAggregates(List<JsonObject> matchedPayloads, JsonObject? aggregateConfig)
    {
        var result = new JsonObject();
        if (aggregateConfig is null || aggregateConfig.Count == 0)
        {
            return result;
        }

        foreach (var (aggName, specNode) in aggregateConfig)
        {
            string op = "count";
            string? field = null;

            if (specNode is JsonValue specVal && specVal.GetValueKind() == JsonValueKind.String)
            {
                var raw = specVal.GetValue<string>().Trim();
                var colonIdx = raw.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                {
                    op = raw[..colonIdx].Trim().ToLowerInvariant();
                    field = raw[(colonIdx + 1)..].Trim();
                }
                else
                {
                    op = raw.ToLowerInvariant();
                }
            }
            else if (specNode is JsonObject specObj)
            {
                op = (specObj["op"]?.GetValue<string>() ?? specObj["function"]?.GetValue<string>() ?? "count").Trim().ToLowerInvariant();
                field = specObj["field"]?.GetValue<string>()?.Trim();
            }

            if (op == "count")
            {
                if (string.IsNullOrEmpty(field))
                {
                    result[aggName] = matchedPayloads.Count;
                }
                else
                {
                    result[aggName] = matchedPayloads.Count(p => JsonPath.Get(p, field) is not null);
                }

                continue;
            }

            if (string.IsNullOrEmpty(field))
            {
                result[aggName] = 0;
                continue;
            }

            var numbers = new List<decimal>(matchedPayloads.Count);
            foreach (var item in matchedPayloads)
            {
                if (WorkflowPartitionManager.TryGetNodeDecimal(JsonPath.Get(item, field), out var num))
                {
                    numbers.Add(num);
                }
            }

            result[aggName] = op switch
            {
                "sum" => numbers.Count > 0 ? JsonValue.Create(numbers.Sum()) : JsonValue.Create(0m),
                "avg" or "average" => numbers.Count > 0 ? JsonValue.Create(Math.Round(numbers.Average(), 2)) : JsonValue.Create(0m),
                "min" => numbers.Count > 0 ? JsonValue.Create(numbers.Min()) : null,
                "max" => numbers.Count > 0 ? JsonValue.Create(numbers.Max()) : null,
                _ => null,
            };
        }

        return result;
    }
}
