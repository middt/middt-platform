using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Agents;

/// <summary>
/// HTTP endpoints for inspecting, editing, validating, testing, and deploying AI agent catalogs
/// (<see cref="AgentDefinition"/>, <see cref="AgentToolDefinition"/>, and <see cref="McpServerDefinition"/>) at runtime.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Reserved key in <c>wf.WorkflowDefinitions</c> used to persist the deployed AI agent catalog.</summary>
    public const string PersistedCatalogKey = "__agents__";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Maps the AI agent catalog and studio endpoints under <paramref name="prefix"/>:
    /// <list type="bullet">
    ///   <item><c>GET /</c> lists registered <see cref="AgentDefinition"/> entries</item>
    ///   <item><c>GET /catalog</c> returns the complete <see cref="AgentCatalogModel"/> (<c>agents</c>, <c>tools</c>, <c>mcpServers</c>, and provider metadata)</item>
    ///   <item><c>POST /catalog</c> deploys an <see cref="AgentCatalogFile"/> and persists it to the database</item>
    ///   <item><c>PUT /{name}</c> and <c>DELETE /{name}</c> upsert or remove a single <see cref="AgentDefinition"/></item>
    ///   <item><c>PUT /tools/{name}</c> and <c>DELETE /tools/{name}</c> upsert or remove a single declarative <see cref="AgentToolDefinition"/></item>
    ///   <item><c>PUT /mcp-servers/{name}</c> and <c>DELETE /mcp-servers/{name}</c> upsert or remove a single <see cref="McpServerDefinition"/></item>
    ///   <item><c>POST /validate</c> validates an agent or catalog without persisting</item>
    ///   <item><c>POST /test</c> executes an interactive dry-run playground test against a registered or inline draft agent</item>
    /// </list>
    /// </summary>
    public static RouteGroupBuilder MapMiddtAgents(
        this IEndpointRouteBuilder endpoints,
        string prefix = "api/agents",
        bool requireAuthorization = true)
    {
        var group = endpoints.MapGroup(prefix).WithTags("AI Agents");
        if (requireAuthorization)
        {
            group.RequireAuthorization();
        }

        group.MapGet("", (IMiddtAgentRegistry registry) =>
        {
            var items = registry.GetAllDefinitions().OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
            return BaseResponseDataModel<List<AgentDefinition>>.Success(items, items.Count);
        }).WithName("ListAgents");

        group.MapGet("catalog", (IMiddtAgentRegistry registry, IMiddtMcpClient mcpClient) =>
        {
            var exported = registry is MiddtAgentRegistry concrete
                ? concrete.ExportCatalog()
                : new AgentCatalogFile
                {
                    Agents = [.. registry.GetAllDefinitions().OrderBy(a => a.Name, StringComparer.Ordinal)],
                    McpServers = [.. mcpClient.GetServers().OrderBy(s => s.Name, StringComparer.Ordinal)],
                };

            var serialized = JsonSerializer.Serialize(exported, JsonOptions);
            var dto = JsonSerializer.Deserialize<AgentCatalogModel>(serialized, JsonOptions) ?? new AgentCatalogModel();

            if (registry is MiddtAgentRegistry concreteRegistry)
            {
                dto.Provider = string.IsNullOrWhiteSpace(concreteRegistry.Settings.Provider)
                    ? "Local"
                    : concreteRegistry.Settings.Provider;
                dto.DefaultModelId = concreteRegistry.Settings.ModelId;
                dto.UseLocalFallback = concreteRegistry.Settings.UseLocalFallback;
                dto.HasExternalApiKey = concreteRegistry.HasExternalProvider;
            }

            return BaseResponseDataModel<AgentCatalogModel>.Success(dto, dto.Agents.Count);
        }).WithName("GetAgentCatalog");

        group.MapPost("catalog", async (
            AgentCatalogFile catalog,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Deploying AI agent catalogs requires the 'ui-admin' or 'idp-admin' role.");
            }

            registry.RegisterCatalog(catalog);
            await PersistCatalogAsync(registry, catalog, services, user.Id, ct);

            var names = catalog.Agents
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => a.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(BaseResponseDataModel<List<string>>.Success(names, names.Count));
        }).WithName("DeployAgentCatalog");

        group.MapPut("{name}", async (
            string name,
            AgentDefinition definition,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Saving AI agent definitions requires the 'ui-admin' or 'idp-admin' role.");
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                definition.Name = name.Trim();
            }

            if (!string.Equals(name.Trim(), definition.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Route agent name and definition 'name' must match.");
            }

            definition.Name = definition.Name.Trim();
            if (registry is MiddtAgentRegistry concrete)
            {
                var errors = concrete.ValidateCatalog(new AgentCatalogFile { Agents = [definition] });
                if (errors.Count > 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: string.Join(" ", errors));
                }
            }

            var catalogUpdate = new AgentCatalogFile { Agents = [definition] };
            registry.RegisterCatalog(catalogUpdate);
            await PersistCatalogAsync(registry, catalogUpdate, services, user.Id, ct);

            return Results.Ok(BaseResponseDataModel<AgentDefinition>.Success(definition, 1));
        }).WithName("SaveAgentDefinition");

        group.MapDelete("{name}", async (
            string name,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Deleting AI agent definitions requires the 'ui-admin' or 'idp-admin' role.");
            }

            var removed = registry is MiddtAgentRegistry concrete && concrete.RemoveAgent(name.Trim());
            if (!removed)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: $"Agent '{name}' was not found.");
            }

            await PersistCatalogAsync(registry, null, services, user.Id, ct);
            return Results.Ok(BaseResponseDataModel<bool>.Success(true, 1));
        }).WithName("DeleteAgentDefinition");

        group.MapPut("tools/{name}", async (
            string name,
            AgentToolDefinition tool,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Saving declarative AI tools requires the 'ui-admin' or 'idp-admin' role.");
            }

            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                tool.Name = name.Trim();
            }

            if (!string.Equals(name.Trim(), tool.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Route tool name and definition 'name' must match.");
            }

            tool.Name = tool.Name.Trim();
            if (registry is MiddtAgentRegistry concrete)
            {
                var errors = concrete.ValidateCatalog(new AgentCatalogFile { Tools = [tool] });
                if (errors.Count > 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: string.Join(" ", errors));
                }
            }

            var catalogUpdate = new AgentCatalogFile { Tools = [tool] };
            registry.RegisterCatalog(catalogUpdate);
            await PersistCatalogAsync(registry, catalogUpdate, services, user.Id, ct);

            return Results.Ok(BaseResponseDataModel<AgentToolDefinition>.Success(tool, 1));
        }).WithName("SaveAgentToolDefinition");

        group.MapDelete("tools/{name}", async (
            string name,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Deleting declarative AI tools requires the 'ui-admin' or 'idp-admin' role.");
            }

            var removed = registry is MiddtAgentRegistry concrete && concrete.RemoveDeclarativeTool(name.Trim());
            if (!removed)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: $"Declarative tool '{name}' was not found.");
            }

            await PersistCatalogAsync(registry, null, services, user.Id, ct);
            return Results.Ok(BaseResponseDataModel<bool>.Success(true, 1));
        }).WithName("DeleteAgentToolDefinition");

        group.MapPut("mcp-servers/{name}", async (
            string name,
            McpServerDefinition server,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Saving MCP server definitions requires the 'ui-admin' or 'idp-admin' role.");
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = name.Trim();
            }

            if (!string.Equals(name.Trim(), server.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Route MCP server name and definition 'name' must match.");
            }

            server.Name = server.Name.Trim();
            if (registry is MiddtAgentRegistry concrete)
            {
                var errors = concrete.ValidateCatalog(new AgentCatalogFile { McpServers = [server] });
                if (errors.Count > 0)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: string.Join(" ", errors));
                }
            }

            var catalogUpdate = new AgentCatalogFile { McpServers = [server] };
            registry.RegisterCatalog(catalogUpdate);
            await PersistCatalogAsync(registry, catalogUpdate, services, user.Id, ct);

            return Results.Ok(BaseResponseDataModel<McpServerDefinition>.Success(server, 1));
        }).WithName("SaveMcpServerDefinition");

        group.MapDelete("mcp-servers/{name}", async (
            string name,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Deleting MCP server definitions requires the 'ui-admin' or 'idp-admin' role.");
            }

            var removed = registry is MiddtAgentRegistry concrete && concrete.RemoveMcpServer(name.Trim());
            if (!removed)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: $"MCP server '{name}' was not found.");
            }

            await PersistCatalogAsync(registry, null, services, user.Id, ct);
            return Results.Ok(BaseResponseDataModel<bool>.Success(true, 1));
        }).WithName("DeleteMcpServerDefinition");

        group.MapPost("validate", (JsonNode? body, IMiddtAgentRegistry registry) =>
        {
            var result = new AgentValidationResultModel();
            if (body is not JsonObject obj)
            {
                result.Errors.Add("Doğrulanacak gövde geçerli bir JSON nesnesi olmalıdır.");
                return BaseResponseDataModel<AgentValidationResultModel>.Success(result, 1);
            }

            AgentCatalogFile catalog;
            if (obj["agents"] is JsonArray || obj["tools"] is JsonArray { Count: > 0 } && obj["tools"]![0] is JsonObject || obj["mcpServers"] is JsonArray { Count: > 0 } && obj["mcpServers"]![0] is JsonObject)
            {
                catalog = obj.Deserialize<AgentCatalogFile>(JsonOptions) ?? new AgentCatalogFile();
            }
            else if (obj["kind"] is not null || obj["expression"] is not null || obj["url"] is not null || obj["task"] is not null)
            {
                var tool = obj.Deserialize<AgentToolDefinition>(JsonOptions);
                catalog = new AgentCatalogFile { Tools = tool is not null ? [tool] : [] };
            }
            else
            {
                var agent = obj.Deserialize<AgentDefinition>(JsonOptions);
                catalog = new AgentCatalogFile { Agents = agent is not null ? [agent] : [] };
            }

            if (registry is MiddtAgentRegistry concrete)
            {
                result.Errors.AddRange(concrete.ValidateCatalog(catalog));
            }

            return BaseResponseDataModel<AgentValidationResultModel>.Success(result, 1);
        }).WithName("ValidateAgentCatalog");

        group.MapPost("test", async (
            AgentPlaygroundRequest request,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !IsAdmin(user))
            {
                return ForbiddenResult("Running AI agent playground tests requires the 'ui-admin' or 'idp-admin' role.");
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Prompt (istem metni) zorunludur.");
            }

            AgentDefinition? targetAgent = null;
            if (request.Definition is not null)
            {
                var defJson = JsonSerializer.Serialize(request.Definition, JsonOptions);
                targetAgent = JsonSerializer.Deserialize<AgentDefinition>(defJson, JsonOptions);
                if (targetAgent is not null && string.IsNullOrWhiteSpace(targetAgent.Name))
                {
                    targetAgent.Name = !string.IsNullOrWhiteSpace(request.Agent) ? request.Agent.Trim() : "playground-agent";
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.Agent))
            {
                targetAgent = registry.GetDefinition(request.Agent.Trim());
                if (targetAgent is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: $"Agent '{request.Agent}' was not found.");
                }
            }

            if (targetAgent is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Test edilecek ajan adı ('agent') veya taslak tanımı ('definition') belirtilmelidir.");
            }

            var workingData = request.Data is not null
                ? (JsonObject)request.Data.DeepClone()
                : [];

            var taskConfig = new JsonObject
            {
                ["agent"] = targetAgent.Name,
                ["prompt"] = request.Prompt,
                ["into"] = "__playgroundOutput",
                ["inlineAgent"] = JsonSerializer.SerializeToNode(targetAgent, JsonOptions),
            };

            if (request.AllowedTransitions is { Count: > 0 })
            {
                var transArray = new JsonArray();
                foreach (var tr in request.AllowedTransitions.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    transArray.Add(tr.Trim());
                }

                taskConfig["allowedTransitions"] = transArray;
            }

            var taskDef = new TaskDefinition
            {
                Key = "playground-test",
                Task = "agent",
                Config = taskConfig,
            };

            var expressions = new WorkflowExpressionEngine();
            var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            WorkflowAgentRunModel? capturedRun = null;

            var context = new WorkflowTaskContext
            {
                Task = taskDef,
                Data = workingData,
                Scope = new ExpressionScope
                {
                    Data = workingData,
                    Input = new JsonObject(),
                    User = user,
                    Instance = new JsonObject
                    {
                        ["id"] = Guid.Empty.ToString(),
                        ["key"] = "PLAYGROUND",
                        ["workflow"] = "playground",
                        ["version"] = 1,
                        ["state"] = "playground",
                    },
                    Now = DateTimeOffset.UtcNow,
                },
                Expressions = expressions,
                InstanceId = Guid.Empty,
                IdempotencyKey = $"playground-{Guid.CreateVersion7():N}",
                Workflow = "playground",
                State = "playground",
                Transition = "test",
                Services = services,
                Logger = loggerFactory.CreateLogger<AiAgentTask>(),
                RecordAgentRun = run => capturedRun = run,
            };

            var startTicks = Stopwatch.GetTimestamp();
            try
            {
                var aiTask = services.GetService<AiAgentTask>() ?? new AiAgentTask();
                await aiTask.ExecuteAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: ex.Message);
            }

            var elapsedMs = (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            var outputNode = workingData["__playgroundOutput"]?.DeepClone();
            workingData.Remove("__playgroundOutput");

            var concreteReg = registry as MiddtAgentRegistry;
            var effectiveModelId = !string.IsNullOrWhiteSpace(targetAgent.ModelId)
                ? targetAgent.ModelId
                : concreteReg?.Settings.ModelId;
            var provider = concreteReg?.Settings.Provider;
            var usedLocalFallback = concreteReg is null || !concreteReg.HasExternalProvider || string.Equals(provider?.Trim(), "Local", StringComparison.OrdinalIgnoreCase);

            var playgroundResult = new AgentPlaygroundResultModel
            {
                AgentName = targetAgent.Name,
                ModelId = effectiveModelId,
                Provider = string.IsNullOrWhiteSpace(provider) ? "Local" : provider,
                UsedLocalFallback = usedLocalFallback,
                Prompt = request.Prompt,
                Output = capturedRun?.Output?.DeepClone() ?? outputNode,
                RawText = (capturedRun?.Output ?? outputNode)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                SelectedTransition = capturedRun?.SelectedTransition,
                AllowedTransitions = capturedRun?.AllowedTransitions ?? [],
                ToolCalls = capturedRun?.ToolCalls ?? [],
                DataAfter = workingData,
                DurationMs = elapsedMs,
                InputTokens = capturedRun?.InputTokens,
                OutputTokens = capturedRun?.OutputTokens,
                TotalTokens = capturedRun?.TotalTokens,
                FinishReason = capturedRun?.FinishReason,
                ResponseId = capturedRun?.ResponseId,
            };

            return Results.Ok(BaseResponseDataModel<AgentPlaygroundResultModel>.Success(playgroundResult, 1));
        }).WithName("TestAgentPlayground");

        return group;
    }

    private static bool IsAdmin(WorkflowUser user) =>
        user.IsInRole("ui-admin") || user.IsInRole("idp-admin");

    private static IResult ForbiddenResult(string title) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: title);

    private static async Task PersistCatalogAsync(
        IMiddtAgentRegistry registry,
        AgentCatalogFile? fallbackCatalog,
        IServiceProvider services,
        string? userId,
        CancellationToken ct)
    {
        var toPersist = registry is MiddtAgentRegistry concrete
            ? concrete.ExportCatalog()
            : (fallbackCatalog ?? new AgentCatalogFile());
        var serialized = JsonSerializer.Serialize(toPersist, JsonOptions);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<WorkflowDbContext>();
        if (db is not null)
        {
            var existing = await db.Definitions
                .FirstOrDefaultAsync(d => d.Key == PersistedCatalogKey && d.Version == 1, ct);
            if (existing is null)
            {
                db.Definitions.Add(new WorkflowDefinitionRecord
                {
                    Key = PersistedCatalogKey,
                    Version = 1,
                    Title = "AI Agent Catalog",
                    Content = serialized,
                    UpdatedBy = userId,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Content = serialized;
                existing.UpdatedBy = userId;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
