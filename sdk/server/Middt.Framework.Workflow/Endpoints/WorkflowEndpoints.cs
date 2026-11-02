using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;

namespace Middt.Framework.Workflow.Endpoints;

public static class WorkflowEndpoints
{
    /// <summary>
    /// <c>messageList</c> entry of a start whose business key already belonged to an instance (200 instead of 201): that
    /// instance is returned and nothing new was started. Clients show it instead of "started".
    /// </summary>
    public const string ExistingInstanceMessage = "Bu iş anahtarıyla açık bir akış zaten var; mevcut akış açıldı.";

    /// <summary>
    /// Maps the workflow REST API under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>GET /</c> workflows · <c>GET /{workflow}</c> states and transitions</item>
    /// <item><c>POST /{workflow}/instances</c> start · <c>GET /{workflow}/instances</c> list</item>
    /// <item><c>GET /{workflow}/instances/{id}</c> instance with available transitions</item>
    /// <item><c>POST /{workflow}/instances/{id}/transitions/{transition}[?async=true]</c> execute (200, or 202 when queued);
    /// the body is an object, or a JSON Patch array for <c>input.format: jsonPatch</c></item>
    /// <item><c>GET …/history</c> · <c>GET …/incidents</c> · <c>POST …/retry</c></item>
    /// <item><c>POST /{workflow}/instances/{id}/messages/{message}</c> · <c>POST /{workflow}/messages/{message}?key=</c>
    /// deliver a message to an event transition (200, or 202 when buffered with <c>?bufferFor=PT10M</c>)</item>
    /// </list>
    /// Responses use <see cref="BaseResponseDataModel{T}"/>; errors are ProblemDetails (400/403/404/409).
    /// </summary>
    public static RouteGroupBuilder MapMiddtWorkflows(this IEndpointRouteBuilder endpoints, string prefix = "api/workflows", bool requireAuthorization = true)
    {
        var group = endpoints.MapGroup(prefix)
            .WithTags("Workflows")
            .AddEndpointFilter(HandleWorkflowErrors);

        if (requireAuthorization)
        {
            group.RequireAuthorization();
        }

        group.MapGet("", (WorkflowRegistry registry, ClaimsPrincipal principal) =>
        {
            var user = WorkflowUser.From(principal);
            var items = registry.Latest
                .Where(d => d.Roles.Count == 0 || d.Roles.Any(user.IsInRole) || user.IsInRole("ui-admin"))
                .Select(d => new WorkflowSummaryModel { Key = d.Key, Version = d.Version, Title = d.Title, Icon = d.Icon })
                .OrderBy(d => d.Key, StringComparer.Ordinal)
                .ToList();
            return BaseResponseDataModel<List<WorkflowSummaryModel>>.Success(items, items.Count);
        }).WithName("ListWorkflows");

        group.MapGet("tasks", () =>
        {
            var items = WorkflowTaskCatalog.All.ToList();
            return BaseResponseDataModel<List<WorkflowTaskSpecModel>>.Success(items, items.Count);
        }).WithName("ListWorkflowTaskSpecs");

        group.MapPost("validate", (JsonNode? definition, WorkflowRegistry registry) =>
        {
            var result = registry.ValidateJson(definition);
            return BaseResponseDataModel<WorkflowValidationResultModel>.Success(result, 1);
        }).WithName("ValidateWorkflowDefinition");

        group.MapPost("definitions", async (JsonNode? body, WorkflowRegistry registry, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !user.IsInRole("ui-admin") && !user.IsInRole("idp-admin"))
            {
                throw new WorkflowForbiddenException("Deploying workflow definitions requires the 'ui-admin' or 'idp-admin' role.");
            }

            var rawDocs = body switch
            {
                JsonArray arr => arr.Where(n => n is not null).Select(n => n!.ToJsonString()).ToList(),
                JsonObject obj => [obj.ToJsonString()],
                _ => throw new WorkflowValidationException(["Request body must be a workflow definition JSON object or an array of workflow definition objects."]),
            };

            var deployed = await registry.DeployAsync(rawDocs, user.Id, ct);
            return BaseResponseDataModel<List<WorkflowSummaryModel>>.Success([.. deployed], deployed.Count);
        }).WithName("DeployWorkflowDefinitions");

        group.MapGet("{workflow}/definition", (string workflow, int? version, WorkflowRegistry registry, ClaimsPrincipal principal) =>
        {
            var definition = version is { } v ? registry.Get(workflow, v) : registry.GetLatest(workflow);
            var user = WorkflowUser.From(principal);
            if (definition.Roles.Count > 0 && !definition.Roles.Any(user.IsInRole) && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            return BaseResponseDataModel<JsonObject>.Success(registry.GetJson(workflow, version), 1);
        }).WithName("GetWorkflowDefinitionJson");

        group.MapPut("{workflow}/definition", async (string workflow, JsonObject definition, WorkflowRegistry registry, Persistence.WorkflowDbContext db, Microsoft.Extensions.Options.IOptions<MiddtWorkflowOptions> workflowOptions, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException("Saving workflow definitions requires the 'ui-admin' role.");
            }

            var key = definition["key"]?.GetValue<string>()?.Trim();
            if (!string.Equals(workflow, key, StringComparison.Ordinal))
            {
                throw new WorkflowValidationException(["The route workflow key and the definition 'key' differ."]);
            }

            var savedJson = await PersistAndCommitDefinitionAsync(definition, registry, db, workflowOptions.Value, user, ct);
            return BaseResponseDataModel<JsonObject>.Success(savedJson, 1);
        }).WithName("SaveWorkflowDefinitionJson");

        group.MapGet("{workflow}/versions", async (string workflow, WorkflowRegistry registry, Persistence.WorkflowDbContext db, Microsoft.Extensions.Options.IOptions<MiddtWorkflowOptions> workflowOptions, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var versions = registry.GetVersions(workflow);
            var latest = versions[^1];
            var user = WorkflowUser.From(principal);
            if (latest.Roles.Count > 0 && !latest.Roles.Any(user.IsInRole) && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            Dictionary<int, (string? UpdatedBy, DateTime UpdatedAt)> persistedMeta = [];
            if (await WorkflowStartup.TableExistsAsync(db, workflowOptions.Value.Schema, "WorkflowDefinitions", ct))
            {
                var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                    Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Definitions)
                        .Where(d => d.Key == workflow),
                    ct);
                foreach (var row in rows)
                {
                    persistedMeta[row.Version] = (row.UpdatedBy, row.UpdatedAt);
                }
            }

            var items = versions
                .OrderByDescending(v => v.Version)
                .Select(v =>
                {
                    persistedMeta.TryGetValue(v.Version, out var meta);
                    return new WorkflowVersionSummaryModel
                    {
                        Version = v.Version,
                        Title = v.Title,
                        StatesCount = v.States.Count,
                        TransitionsCount = v.States.Sum(s => s.Transitions.Count) + v.SharedTransitions.Count,
                        IsLatest = v.Version == latest.Version,
                        UpdatedBy = meta.UpdatedBy,
                        UpdatedAt = meta.UpdatedAt == default ? null : meta.UpdatedAt,
                    };
                })
                .ToList();

            return BaseResponseDataModel<List<WorkflowVersionSummaryModel>>.Success(items, items.Count);
        }).WithName("ListWorkflowVersions");

        group.MapGet("{workflow}/diff", (string workflow, int? fromVersion, int? toVersion, WorkflowRegistry registry, ClaimsPrincipal principal) =>
        {
            var versions = registry.GetVersions(workflow);
            var latest = versions[^1];
            var user = WorkflowUser.From(principal);
            if (latest.Roles.Count > 0 && !latest.Roles.Any(user.IsInRole) && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            var resolvedTo = toVersion ?? latest.Version;
            var resolvedFrom = fromVersion ?? (versions.Count > 1 && resolvedTo == latest.Version ? versions[^2].Version : resolvedTo);

            var fromDef = registry.Get(workflow, resolvedFrom);
            var toDef = registry.Get(workflow, resolvedTo);
            var fromJson = registry.GetJson(workflow, resolvedFrom);
            var toJson = registry.GetJson(workflow, resolvedTo);

            var fromStateKeys = fromDef.States.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            var toStateKeys = toDef.States.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

            var addedStates = toStateKeys.Except(fromStateKeys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var removedStates = fromStateKeys.Except(toStateKeys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

            var changedStates = new List<string>();
            foreach (var commonState in fromStateKeys.Intersect(toStateKeys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            {
                var s1 = fromJson["states"]?.AsArray().FirstOrDefault(n => string.Equals(n?["key"]?.GetValue<string>(), commonState, StringComparison.Ordinal));
                var s2 = toJson["states"]?.AsArray().FirstOrDefault(n => string.Equals(n?["key"]?.GetValue<string>(), commonState, StringComparison.Ordinal));
                if (!JsonNode.DeepEquals(s1, s2))
                {
                    changedStates.Add(commonState);
                }
            }

            var fromTransitions = fromDef.States.SelectMany(s => s.Transitions.Select(t => $"{s.Key}:{t.Key}"))
                .Concat(fromDef.SharedTransitions.Select(t => $"*:{t.Key}"))
                .ToHashSet(StringComparer.Ordinal);
            var toTransitions = toDef.States.SelectMany(s => s.Transitions.Select(t => $"{s.Key}:{t.Key}"))
                .Concat(toDef.SharedTransitions.Select(t => $"*:{t.Key}"))
                .ToHashSet(StringComparer.Ordinal);

            var diffModel = new WorkflowVersionDiffModel
            {
                Key = workflow,
                FromVersion = resolvedFrom,
                ToVersion = resolvedTo,
                AddedStates = addedStates,
                RemovedStates = removedStates,
                ChangedStates = changedStates,
                AddedTransitions = toTransitions.Except(fromTransitions, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                RemovedTransitions = fromTransitions.Except(toTransitions, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                Patch = JsonPatch.Diff(fromJson, toJson),
            };

            return BaseResponseDataModel<WorkflowVersionDiffModel>.Success(diffModel, 1);
        }).WithName("GetWorkflowVersionDiff");

        group.MapPost("{workflow}/versions/{version:int}/rollback", async (string workflow, int version, WorkflowRegistry registry, Persistence.WorkflowDbContext db, Microsoft.Extensions.Options.IOptions<MiddtWorkflowOptions> workflowOptions, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException("Rolling back workflow definitions requires the 'ui-admin' role.");
            }

            var targetJson = registry.GetJson(workflow, version);
            var savedJson = await PersistAndCommitDefinitionAsync(targetJson, registry, db, workflowOptions.Value, user, ct);
            return BaseResponseDataModel<JsonObject>.Success(savedJson, 1);
        }).WithName("RollbackWorkflowVersion");

        group.MapGet("operations", async (string? workflow, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var summary = await engine.GetOperationsSummaryAsync(workflow, WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<WorkflowOperationsSummaryModel>.Success(summary, 1);
        }).WithName("GetWorkflowOperationsSummary");

        group.MapPost("operations/retry-batch", async (WorkflowBatchRetryRequest request, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            var result = await engine.RetryBatchAsync(request, WorkflowUser.From(principal), http, ct);
            return BaseResponseDataModel<WorkflowBatchOperationResultModel>.Success(result, result.SucceededIds.Count);
        }).WithName("RetryWorkflowBatch");

        group.MapGet("inbox", async (string? workflow, int? page, int? pageSize, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var (items, totalCount) = await engine.ListInboxAsync(workflow, WorkflowUser.From(principal), page ?? 1, pageSize ?? 20, ct);
            return BaseResponseDataModel<List<WorkflowInboxItemModel>>.Success(items, totalCount);
        }).WithName("ListWorkflowInbox");

        group.MapPost("{workflow}/simulate", async (string workflow, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] WorkflowSimulationRequest? request, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var result = await engine.SimulateAsync(workflow, request ?? new WorkflowSimulationRequest(), WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<WorkflowSimulationResultModel>.Success(result, 1);
        }).WithName("SimulateWorkflow");

        group.MapGet("bundle", (WorkflowRegistry registry, ClaimsPrincipal principal) =>
        {
            var user = WorkflowUser.From(principal);
            var definitions = registry.Latest
                .Where(d => d.Roles.Count == 0 || d.Roles.Any(user.IsInRole) || user.IsInRole("ui-admin"))
                .OrderBy(d => d.Key, StringComparer.Ordinal)
                .ToList();

            var items = new List<WorkflowBundleItemModel>(definitions.Count);
            var bundleDigestBuilder = new StringBuilder();
            foreach (var def in definitions)
            {
                var raw = registry.GetJson(def.Key, def.Version);
                var canonical = raw.ToJsonString(WorkflowJson.Options);
                var itemChecksum = ComputeSha256Hex(canonical);
                bundleDigestBuilder.Append(def.Key).Append(':').Append(def.Version).Append(':').Append(itemChecksum).Append('\n');
                items.Add(new WorkflowBundleItemModel
                {
                    Key = def.Key,
                    Version = def.Version,
                    Title = def.Title,
                    Checksum = itemChecksum,
                    Definition = raw,
                });
            }

            var bundle = new WorkflowBundleModel
            {
                ExportedAt = DateTime.UtcNow,
                Checksum = ComputeSha256Hex(bundleDigestBuilder.ToString()),
                Items = items,
            };

            return BaseResponseDataModel<WorkflowBundleModel>.Success(bundle, items.Count);
        }).WithName("ExportWorkflowBundle");

        group.MapPost("bundle/import", async (WorkflowBundleImportRequest request, WorkflowRegistry registry, Persistence.WorkflowDbContext db, Microsoft.Extensions.Options.IOptions<MiddtWorkflowOptions> workflowOptions, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !request.DryRun && !user.IsInRole("ui-admin"))
            {
                throw new WorkflowForbiddenException("Importing workflow bundles requires the 'ui-admin' role.");
            }

            var result = new WorkflowBundleImportResultModel { DryRun = request.DryRun };
            if (request.Items.Count == 0)
            {
                result.Errors.Add("Bundle 'items' must contain at least one workflow definition.");
                return BaseResponseDataModel<WorkflowBundleImportResultModel>.Success(result, 0);
            }

            var digestBuilder = new StringBuilder();
            foreach (var item in request.Items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                var canonical = item.Definition.ToJsonString(WorkflowJson.Options);
                var computedItemChecksum = ComputeSha256Hex(canonical);
                if (!string.IsNullOrWhiteSpace(item.Checksum) && !string.Equals(item.Checksum, computedItemChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Workflow '{item.Key}' checksum mismatch (expected '{item.Checksum}', actual '{computedItemChecksum}').");
                }

                var version = item.Definition["version"]?.GetValue<int>() ?? (item.Version > 0 ? item.Version : 1);
                digestBuilder.Append(item.Key).Append(':').Append(version).Append(':').Append(computedItemChecksum).Append('\n');

                var validation = registry.ValidateJson(item.Definition);
                if (!validation.IsValid)
                {
                    foreach (var err in validation.Errors)
                    {
                        result.Errors.Add($"{item.Key}: {err}");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedChecksum))
            {
                var actualBundleChecksum = ComputeSha256Hex(digestBuilder.ToString());
                if (!string.Equals(request.ExpectedChecksum, actualBundleChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Bundle checksum mismatch (expected '{request.ExpectedChecksum}', actual '{actualBundleChecksum}').");
                }
            }

            if (!result.IsValid || request.DryRun)
            {
                if (result.IsValid)
                {
                    result.ImportedWorkflows.AddRange(request.Items.Select(i => i.Key));
                }

                return BaseResponseDataModel<WorkflowBundleImportResultModel>.Success(result, result.ImportedWorkflows.Count);
            }

            foreach (var item in request.Items)
            {
                var saved = await PersistAndCommitDefinitionAsync(item.Definition, registry, db, workflowOptions.Value, user, ct);
                result.ImportedWorkflows.Add(saved["key"]?.GetValue<string>() ?? item.Key);
            }

            return BaseResponseDataModel<WorkflowBundleImportResultModel>.Success(result, result.ImportedWorkflows.Count);
        }).WithName("ImportWorkflowBundle");

        group.MapPost("maintenance/purge", async (WorkflowPurgeRequest request, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var result = await engine.PurgeAsync(request, WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<WorkflowPurgeResultModel>.Success(result, result.PurgedInstancesCount);
        }).WithName("PurgeWorkflowInstances");

        group.MapGet("{workflow}", (string workflow, int? version, WorkflowRegistry registry, ClaimsPrincipal principal) =>
        {
            var definition = version is { } v ? registry.Get(workflow, v) : registry.GetLatest(workflow);
            var user = WorkflowUser.From(principal);
            if (definition.Roles.Count > 0 && !definition.Roles.Any(user.IsInRole))
            {
                throw new WorkflowForbiddenException($"You are not allowed to access workflow '{workflow}'.");
            }

            return BaseResponseDataModel<WorkflowDefinitionModel>.Success(ToModel(definition, user), 1);
        }).WithName("GetWorkflow");

        group.MapPost("{workflow}/instances", async (string workflow, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] StartWorkflowRequest? request, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            var startRequest = request ?? new StartWorkflowRequest();
            startRequest.Data = WorkflowHttpInput.FromHttpContext(http, startRequest.Data);
            var (instance, created) = await engine.StartAsync(workflow, startRequest, WorkflowUser.From(principal), ct);
            http.Response.Headers.ETag = ComputeInstanceETag(instance);
            var body = BaseResponseDataModel<WorkflowInstanceModel>.Success(instance, 1);
            if (created)
            {
                return Results.Created($"{http.Request.Path}/{instance.Id}", body);
            }

            // Same business key: the existing instance is returned (200). The message tells clients that nothing was started.
            body.MessageList.Add(ExistingInstanceMessage);
            return Results.Ok(body);
        }).WithName("StartWorkflowInstance");

        group.MapGet("{workflow}/instances", async (string workflow, string? state, WorkflowInstanceStatus? status, string? key, int? page, int? pageSize, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            var filter = ExtractDataFilter(http.Request.Query);
            var (items, count) = await engine.ListAsync(workflow, WorkflowUser.From(principal), state, status, key, page ?? 1, pageSize ?? 20, filter, ct);
            return BaseResponseDataModel<List<WorkflowInstanceModel>>.Success(items, count);
        }).WithName("ListWorkflowInstances");

        group.MapGet("{workflow}/instances/{id:guid}", async (string workflow, Guid id, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            var instance = await engine.GetAsync(workflow, id, WorkflowUser.From(principal), ct);
            var etag = ComputeInstanceETag(instance);
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',', StringSplitOptions.TrimEntries).Any(t => t == "*" || string.Equals(t, etag, StringComparison.Ordinal))))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Ok(BaseResponseDataModel<WorkflowInstanceModel>.Success(instance, 1));
        }).WithName("GetWorkflowInstance");

        group.MapMethods("{workflow}/instances/{id:guid}/transitions/{transition}", ["POST", "PATCH"], async (string workflow, Guid id, string transition, bool? async, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonNode? input, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            // A bare JSON array is a JSON Patch (input.format: jsonPatch); it travels as { "patch": [...] }.
            var request = input switch
            {
                null => null,
                JsonObject obj => obj,
                JsonArray patch => new JsonObject { [WorkflowEngine.PatchProperty] = patch.DeepClone() },
                _ => throw new WorkflowValidationException(["The body must be a JSON object or a JSON Patch array."]),
            };

            // The engine checks access and the workflow on the row it loads anyway (no separate read).
            var enrichedInput = WorkflowHttpInput.FromHttpContext(http, request);
            var instance = await engine.ExecuteTransitionAsync(workflow, id, transition, enrichedInput, WorkflowUser.From(principal), async == true, ct);
            http.Response.Headers.ETag = ComputeInstanceETag(instance);
            var body = BaseResponseDataModel<WorkflowInstanceModel>.Success(instance, 1);
            return async == true ? Results.Accepted(null, body) : Results.Ok(body);
        })
        .Accepts<JsonNode>("application/json", "application/json-patch+json")
        .WithName("ExecuteWorkflowTransition");

        group.MapGet("{workflow}/instances/{id:guid}/view", async (string workflow, Guid id, WorkflowEngine engine, WorkflowViewRenderer renderer, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var instance = await engine.GetAsync(workflow, id, WorkflowUser.From(principal), ct);
            var html = await renderer.RenderAsync(instance, WorkflowUser.From(principal), ct)
                ?? throw new WorkflowNotFoundException($"State '{instance.State}' has no registered view.");
            return Results.Content(html, "text/html; charset=utf-8");
        }).WithName("RenderWorkflowInstanceView");

        group.MapGet("{workflow}/instances/{id:guid}/history", async (string workflow, Guid id, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var items = await engine.HistoryAsync(workflow, id, WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<List<WorkflowHistoryModel>>.Success(items, items.Count);
        }).WithName("GetWorkflowInstanceHistory");

        group.MapGet("{workflow}/instances/{id:guid}/incidents", async (string workflow, Guid id, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var items = await engine.IncidentsAsync(workflow, id, WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<List<WorkflowIncidentModel>>.Success(items, items.Count);
        }).WithName("GetWorkflowInstanceIncidents");

        group.MapGet("{workflow}/instances/{id:guid}/agent-runs", async (string workflow, Guid id, WorkflowEngine engine, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var items = await engine.AgentRunsAsync(workflow, id, WorkflowUser.From(principal), ct);
            return BaseResponseDataModel<List<WorkflowAgentRunModel>>.Success(items, items.Count);
        }).WithName("GetWorkflowInstanceAgentRuns");

        group.MapPost("{workflow}/instances/{id:guid}/retry", async (string workflow, Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonObject? input, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
        {
            var enrichedInput = input is { Count: > 0 } ? WorkflowHttpInput.FromHttpContext(http, input) : null;
            return BaseResponseDataModel<WorkflowInstanceModel>.Success(await engine.RetryAsync(workflow, id, enrichedInput, WorkflowUser.From(principal), ct), 1);
        }).WithName("RetryWorkflowInstance");

        group.MapPost("{workflow}/instances/{id:guid}/messages/{message}", (string workflow, Guid id, string message, string? bufferFor, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonObject? data, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
            DeliverMessageAsync(new WorkflowMessage { Name = message, Workflow = workflow, InstanceId = id, Data = data, BufferFor = ParseBuffer(bufferFor) }, engine, principal, http, ct))
            .WithName("DeliverWorkflowInstanceMessage");

        group.MapPost("{workflow}/messages/{message}", (string workflow, string message, string? key, string? bufferFor, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] JsonObject? data, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct) =>
            DeliverMessageAsync(new WorkflowMessage { Name = message, Workflow = workflow, Key = key, Data = data, BufferFor = ParseBuffer(bufferFor) }, engine, principal, http, ct))
            .WithName("DeliverWorkflowMessage");

        return group;
    }

    private static async Task<IResult> DeliverMessageAsync(WorkflowMessage message, WorkflowEngine engine, ClaimsPrincipal principal, HttpContext http, CancellationToken ct)
    {
        var user = WorkflowUser.From(principal);
        var result = await engine.DeliverMessageAsync(message, user, ct);
        var instance = result.Instance ?? await engine.GetAsync(message.Workflow, result.InstanceId, user, ct);
        http.Response.Headers.ETag = ComputeInstanceETag(instance);
        var body = BaseResponseDataModel<WorkflowInstanceModel>.Success(instance, 1);
        return result.Outcome == WorkflowMessageOutcome.Buffered ? Results.Accepted(null, body) : Results.Ok(body);
    }

    private static TimeSpan ParseBuffer(string? bufferFor) =>
        string.IsNullOrWhiteSpace(bufferFor) ? TimeSpan.Zero
        : Middt.Framework.Expressions.Durations.TryParse(bufferFor, out var duration) ? duration
        : throw new WorkflowValidationException([$"bufferFor '{bufferFor}' is not a valid duration (ISO-8601 such as PT10M, or hh:mm:ss)."]);

    private static async ValueTask<object?> HandleWorkflowErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (WorkflowValidationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["input"] = [.. ex.Errors] }, title: ex.Message);
        }
        catch (WorkflowException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }
    }

    /// <summary>Projects a <see cref="WorkflowDefinition"/> to its client <see cref="WorkflowDefinitionModel"/> contract.</summary>
    public static WorkflowDefinitionModel ToModel(WorkflowDefinition definition, Middt.Framework.Expressions.ExpressionUser? user = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new WorkflowDefinitionModel
        {
            Key = definition.Key,
            Version = definition.Version,
            Title = definition.Title,
            Icon = definition.Icon,
            StartState = definition.Start.Target,
            StartInputSchema = (JsonObject?)definition.Start.Input?.Schema?.DeepClone(),
            DataSchema = Schema.JsonSchemaValidator.ProjectSchema(definition.DataSchema, user ?? Expressions.WorkflowUser.System),
            States = definition.States.Select(s => new WorkflowStateModel
            {
                Key = s.Key,
                Title = s.Title,
                Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description,
                Type = s.Type.ToString(),
                SubType = s.SubType.ToString(),
                View = s.View?.Component,
                Layout = s.View?.Layout,
                Step = s.Step,
            }).ToList(),
            Transitions = definition.States
                .SelectMany(s => s.Transitions.Select(t => (From: (string?)s.Key, Transition: t)))
                .Concat(definition.SharedTransitions.Select(t => (From: (string?)null, Transition: t)))
                .Select(x => new WorkflowTransitionModel
                {
                    Key = x.Transition.Key,
                    Title = x.Transition.Title,
                    Description = string.IsNullOrWhiteSpace(x.Transition.Description) ? null : x.Transition.Description,
                    From = x.From,
                    Target = x.Transition.Target,
                    Trigger = x.Transition.Trigger.ToString(),
                    Style = string.IsNullOrWhiteSpace(x.Transition.Style) ? null : x.Transition.Style,
                    Icon = string.IsNullOrWhiteSpace(x.Transition.Icon) ? null : x.Transition.Icon,
                })
                .ToList(),
        };
    }

    private static readonly HashSet<string> ReservedInstanceQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "state", "status", "key", "page", "pageSize",
    };

    private static Dictionary<string, string?>? ExtractDataFilter(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return null;
        }

        Dictionary<string, string?>? result = null;
        foreach (var (rawKey, values) in query)
        {
            if (string.IsNullOrWhiteSpace(rawKey) || ReservedInstanceQueryKeys.Contains(rawKey))
            {
                continue;
            }

            var value = values.FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var fieldKey = rawKey.StartsWith("data.", StringComparison.OrdinalIgnoreCase)
                ? rawKey[5..]
                : rawKey.StartsWith("filter.", StringComparison.OrdinalIgnoreCase)
                    ? rawKey[7..]
                    : rawKey;

            if (!string.IsNullOrWhiteSpace(fieldKey))
            {
                result ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                result[fieldKey] = value;
            }
        }

        return result;
    }

    private static async Task<JsonObject> PersistAndCommitDefinitionAsync(
        JsonObject definition,
        WorkflowRegistry registry,
        Persistence.WorkflowDbContext db,
        MiddtWorkflowOptions workflowOptions,
        WorkflowUser user,
        CancellationToken ct)
    {
        var key = definition["key"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WorkflowValidationException(["'key' is required."]);
        }

        int? dbMaxVersion = null;
        var hasDefinitionsTable = await WorkflowStartup.TableExistsAsync(db, workflowOptions.Schema, "WorkflowDefinitions", ct);
        if (hasDefinitionsTable)
        {
            dbMaxVersion = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MaxAsync(
                db.Definitions.Where(d => d.Key == key).Select(d => (int?)d.Version),
                ct);
        }

        var (savedDef, savedJson) = registry.PrepareSave(definition, dbMaxVersion);
        var serialized = savedJson.ToJsonString(WorkflowJson.Options);

        if (hasDefinitionsTable)
        {
            var existingRecord = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                db.Definitions,
                d => d.Key == savedDef.Key && d.Version == savedDef.Version,
                ct);

            if (existingRecord is null)
            {
                db.Definitions.Add(new Persistence.WorkflowDefinitionRecord
                {
                    Key = savedDef.Key,
                    Version = savedDef.Version,
                    Title = string.IsNullOrWhiteSpace(savedDef.Title) ? savedDef.Key : savedDef.Title,
                    Content = serialized,
                    UpdatedBy = user.Id,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existingRecord.Title = string.IsNullOrWhiteSpace(savedDef.Title) ? savedDef.Key : savedDef.Title;
                existingRecord.Content = serialized;
                existingRecord.UpdatedBy = user.Id;
                existingRecord.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowException)
            {
                throw new WorkflowPersistenceException(
                    $"Workflow definition '{savedDef.Key}' could not be persisted to the database.",
                    ex);
            }
        }

        registry.CommitSaved(savedDef, savedJson);

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            await WorkflowPartitionManager.EnsurePartitionsAndIndexesAsync(db, registry, workflowOptions.Schema, null, ct);
        }

        return savedJson;
    }

    private static string ComputeSha256Hex(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    private static string ComputeInstanceETag(WorkflowInstanceModel instance) =>
        $"\"{instance.Id:N}-v{instance.DataVersion}-{instance.State}-{instance.Status}-{instance.UpdatedAt.Ticks}-{instance.ActiveSubFlow?.DataVersion}-{instance.ActiveSubFlow?.State}\"";
}
