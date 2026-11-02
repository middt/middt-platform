using System.Text.Json.Nodes;

namespace Middt.Framework.Agents;

/// <summary>
/// Client and registry for interacting with Model Context Protocol (MCP) servers (both remote HTTP JSON-RPC 2.0
/// MCP endpoints and in-process declarative MCP servers).
/// </summary>
public interface IMiddtMcpClient
{
    /// <summary>Returns all registered <see cref="McpServerDefinition"/> configurations.</summary>
    IReadOnlyCollection<McpServerDefinition> GetServers();

    /// <summary>Looks up a registered <see cref="McpServerDefinition"/> by name (case-insensitive).</summary>
    McpServerDefinition? GetServer(string serverName);

    /// <summary>Registers or replaces an <see cref="McpServerDefinition"/> at runtime.</summary>
    void RegisterServer(McpServerDefinition server);

    /// <summary>Lists the tools exposed by the specified MCP server (<c>tools/list</c>).</summary>
    Task<JsonArray> ListToolsAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>Invokes a tool on the specified MCP server (<c>tools/call</c>).</summary>
    Task<JsonNode?> CallToolAsync(
        string serverName,
        string toolName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the resources exposed by the specified MCP server (<c>resources/list</c>).</summary>
    Task<JsonArray> ListResourcesAsync(string serverName, CancellationToken cancellationToken = default);

    /// <summary>Reads a resource from the specified MCP server by URI (<c>resources/read</c>).</summary>
    Task<JsonNode?> ReadResourceAsync(
        string serverName,
        string uri,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a prompt template from the specified MCP server (<c>prompts/get</c>).</summary>
    Task<string> GetPromptAsync(
        string serverName,
        string promptName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default);
}
