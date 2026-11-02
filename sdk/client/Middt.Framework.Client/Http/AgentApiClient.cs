using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Middt.Framework.Client.Security;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Model.Workflow;

namespace Middt.Framework.Client.Http;

/// <summary>
/// Typed client for the AI Agent Studio and Playground REST API mapped by <c>MapMiddtAgents</c> (default prefix <c>api/agents</c>).
/// Register with <c>services.AddMiddtApiClient&lt;AgentApiClient&gt;()</c>.
/// </summary>
public class AgentApiClient(
    HttpClient httpClient,
    IBaseSessionState sessionState,
    ITokenService tokenService,
    ILogger<AgentApiClient> logger)
    : ApiClientBase(httpClient, sessionState, tokenService, logger)
{
    protected virtual string RoutePrefix => "api/agents";

    /// <summary>Lists all registered AI agent definitions.</summary>
    public virtual Task<BaseResponseDataModel<List<AgentDefinitionModel>>> ListAgentsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<AgentDefinitionModel>>>(HttpMethod.Get, RoutePrefix, null, cancellationToken);

    /// <summary>Loads the full AI agent, declarative tool, and MCP server catalog along with active provider info.</summary>
    public virtual Task<BaseResponseDataModel<AgentCatalogModel>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<AgentCatalogModel>>(HttpMethod.Get, $"{RoutePrefix.TrimEnd('/')}/catalog", null, cancellationToken);

    /// <summary>Deploys a full or partial catalog (<c>agents</c>, <c>tools</c>, <c>mcpServers</c>) and persists it (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<List<string>>> DeployCatalogAsync(AgentCatalogModel catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return SendAsync<BaseResponseDataModel<List<string>>>(HttpMethod.Post, $"{RoutePrefix.TrimEnd('/')}/catalog", catalog, cancellationToken);
    }

    /// <summary>Creates or updates a single AI agent definition (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<AgentDefinitionModel>> SaveAgentAsync(AgentDefinitionModel agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return SendAsync<BaseResponseDataModel<AgentDefinitionModel>>(
            HttpMethod.Put,
            $"{RoutePrefix.TrimEnd('/')}/{EscapePathSegment(agent.Name, nameof(agent.Name))}",
            agent,
            cancellationToken);
    }

    /// <summary>Deletes an AI agent definition by name (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<bool>> DeleteAgentAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<bool>>(
            HttpMethod.Delete,
            $"{RoutePrefix.TrimEnd('/')}/{EscapePathSegment(name, nameof(name))}",
            null,
            cancellationToken);

    /// <summary>Creates or updates a single declarative AI tool (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<AgentToolDefinitionModel>> SaveToolAsync(AgentToolDefinitionModel tool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return SendAsync<BaseResponseDataModel<AgentToolDefinitionModel>>(
            HttpMethod.Put,
            $"{RoutePrefix.TrimEnd('/')}/tools/{EscapePathSegment(tool.Name, nameof(tool.Name))}",
            tool,
            cancellationToken);
    }

    /// <summary>Deletes a declarative AI tool by name (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<bool>> DeleteToolAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<bool>>(
            HttpMethod.Delete,
            $"{RoutePrefix.TrimEnd('/')}/tools/{EscapePathSegment(name, nameof(name))}",
            null,
            cancellationToken);

    /// <summary>Creates or updates a Model Context Protocol (MCP) server configuration (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<McpServerDefinitionModel>> SaveMcpServerAsync(McpServerDefinitionModel server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return SendAsync<BaseResponseDataModel<McpServerDefinitionModel>>(
            HttpMethod.Put,
            $"{RoutePrefix.TrimEnd('/')}/mcp-servers/{EscapePathSegment(server.Name, nameof(server.Name))}",
            server,
            cancellationToken);
    }

    /// <summary>Deletes an MCP server configuration by name (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<bool>> DeleteMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<bool>>(
            HttpMethod.Delete,
            $"{RoutePrefix.TrimEnd('/')}/mcp-servers/{EscapePathSegment(name, nameof(name))}",
            null,
            cancellationToken);

    /// <summary>Validates a draft agent, tool, or catalog JSON object without saving it.</summary>
    public virtual Task<BaseResponseDataModel<AgentValidationResultModel>> ValidateAsync(JsonObject body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync<BaseResponseDataModel<AgentValidationResultModel>>(
            HttpMethod.Post,
            $"{RoutePrefix.TrimEnd('/')}/validate",
            body,
            cancellationToken);
    }

    /// <summary>Executes an interactive dry-run playground test against a registered or inline draft AI agent.</summary>
    public virtual Task<BaseResponseDataModel<AgentPlaygroundResultModel>> TestPlaygroundAsync(AgentPlaygroundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<AgentPlaygroundResultModel>>(
            HttpMethod.Post,
            $"{RoutePrefix.TrimEnd('/')}/test",
            request,
            cancellationToken);
    }
}

