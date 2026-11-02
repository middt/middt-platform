using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;

namespace Middt.Framework.Agents;

/// <summary>
/// HTTP endpoints for inspecting and deploying AI agent catalogs (<see cref="AgentDefinition"/>,
/// <see cref="AgentToolDefinition"/>, and <see cref="McpServerDefinition"/>) at runtime.
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
    /// Maps the AI agent catalog endpoints under <paramref name="prefix"/>:
    /// <list type="bullet">
    ///   <item><c>GET /</c> lists registered <see cref="AgentDefinition"/> entries</item>
    ///   <item><c>POST /catalog</c> deploys an <see cref="AgentCatalogFile"/> (<c>agents</c>, <c>tools</c>, <c>mcpServers</c>) and persists it to the database</item>
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

        group.MapPost("catalog", async (
            AgentCatalogFile catalog,
            IMiddtAgentRegistry registry,
            IServiceProvider services,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = WorkflowUser.From(principal);
            if (requireAuthorization && !user.IsInRole("ui-admin") && !user.IsInRole("idp-admin"))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Deploying AI agent catalogs requires the 'ui-admin' or 'idp-admin' role.");
            }

            registry.RegisterCatalog(catalog);

            var toPersist = registry is MiddtAgentRegistry concrete
                ? concrete.ExportCatalog()
                : catalog;
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
                        UpdatedBy = user.Id,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Content = serialized;
                    existing.UpdatedBy = user.Id;
                    existing.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
            }

            var names = catalog.Agents
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => a.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(BaseResponseDataModel<List<string>>.Success(names, names.Count));
        }).WithName("DeployAgentCatalog");

        return group;
    }
}

