using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Middt.Framework.Agents;

/// <summary>
/// Options for configuring Microsoft Agent Framework (<c>Microsoft.Agents.AI</c>) agents, declarative/delegate tools,
/// Model Context Protocol (MCP) servers, and <see cref="IChatClient"/> pipelines in Middt applications.
/// </summary>
public sealed class MiddtAgentOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Registered named agent definitions.</summary>
    public Dictionary<string, AgentDefinition> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered <see cref="AITool"/> functions available to agents by name.</summary>
    public Dictionary<string, AITool> Tools { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered declarative <see cref="AgentToolDefinition"/> entries.</summary>
    public Dictionary<string, AgentToolDefinition> DeclarativeTools { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered Model Context Protocol (<see cref="McpServerDefinition"/>) servers.</summary>
    public Dictionary<string, McpServerDefinition> McpServers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional custom factory for creating the underlying <see cref="IChatClient"/> for a given model ID.
    /// When <c>null</c>, the default provider factory creates an OpenAI / OpenAI-compatible client or local fallback client.
    /// </summary>
    public Func<IServiceProvider, AgentSettings, string, IChatClient>? ChatClientFactory { get; set; }

    /// <summary>Registers a named <see cref="AgentDefinition"/>.</summary>
    public MiddtAgentOptions AddAgent(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new ArgumentException("AgentDefinition.Name is required.", nameof(definition));
        }

        Agents[definition.Name] = definition;
        return this;
    }

    /// <summary>Registers a named AI agent with instructions, optional tools, and optional output JSON schema.</summary>
    public MiddtAgentOptions AddAgent(
        string name,
        string instructions,
        string? description = null,
        IEnumerable<string>? tools = null,
        JsonObject? outputSchema = null,
        string? modelId = null,
        IEnumerable<string>? mcpServers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return AddAgent(new AgentDefinition
        {
            Name = name,
            Instructions = instructions,
            Description = description,
            ModelId = modelId,
            Tools = tools is not null ? [.. tools] : [],
            McpServers = mcpServers is not null ? [.. mcpServers] : [],
            OutputSchema = outputSchema,
        });
    }

    /// <summary>Registers a declarative <see cref="AgentToolDefinition"/> (<c>expression</c>, <c>http</c>, or <c>mcp</c>).</summary>
    public MiddtAgentOptions AddDeclarativeTool(AgentToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("AgentToolDefinition.Name is required.", nameof(tool));
        }

        DeclarativeTools[tool.Name] = tool;
        return this;
    }

    /// <summary>Registers a Model Context Protocol (<see cref="McpServerDefinition"/>) server.</summary>
    public MiddtAgentOptions AddMcpServer(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.Name))
        {
            throw new ArgumentException("McpServerDefinition.Name is required.", nameof(server));
        }

        McpServers[server.Name] = server;
        return this;
    }

    /// <summary>
    /// Loads <see cref="AgentDefinition"/>, <see cref="AgentToolDefinition"/>, and <see cref="McpServerDefinition"/> JSON files
    /// (<c>*.json</c>) from <paramref name="directoryPath"/>.
    /// Each file may contain a single <see cref="AgentDefinition"/>, an array of <see cref="AgentDefinition"/>s,
    /// or an <see cref="AgentCatalogFile"/> (<c>{ "agents": [...], "tools": [...], "mcpServers": [...] }</c>).
    /// </summary>
    public MiddtAgentOptions AddAgentsFromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.IsPathRooted(directoryPath)
            ? directoryPath
            : Path.Combine(AppContext.BaseDirectory, directoryPath);

        if (!Directory.Exists(fullPath))
        {
            return this;
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var json = File.ReadAllText(file);
            var node = JsonNode.Parse(json);
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item.Deserialize<AgentDefinition>(JsonOptions) is { Name.Length: > 0 } def)
                    {
                        AddAgent(def);
                    }
                }
            }
            else if (node is JsonObject obj)
            {
                if (IsCatalogObject(obj))
                {
                    var catalog = obj.Deserialize<AgentCatalogFile>(JsonOptions);
                    if (catalog is not null)
                    {
                        foreach (var tool in catalog.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
                        {
                            AddDeclarativeTool(tool);
                        }

                        foreach (var mcp in catalog.McpServers.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                        {
                            AddMcpServer(mcp);
                        }

                        foreach (var agent in catalog.Agents.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
                        {
                            AddAgent(agent);
                        }
                    }
                }
                else
                {
                    var def = obj.Deserialize<AgentDefinition>(JsonOptions);
                    if (def is not null)
                    {
                        if (string.IsNullOrWhiteSpace(def.Name))
                        {
                            def.Name = Path.GetFileNameWithoutExtension(file);
                        }

                        AddAgent(def);
                    }
                }
            }
        }

        var nestedTools = Path.Combine(fullPath, "Tools");
        if (Directory.Exists(nestedTools))
        {
            AddToolsFromDirectory(nestedTools);
        }

        var nestedMcp = Path.Combine(fullPath, "McpServers");
        if (Directory.Exists(nestedMcp))
        {
            AddMcpServersFromDirectory(nestedMcp);
        }

        return this;
    }

    /// <summary>
    /// Loads declarative <see cref="AgentToolDefinition"/> JSON files (<c>*.json</c>) from <paramref name="directoryPath"/>.
    /// Each file may contain a single <see cref="AgentToolDefinition"/> or an array of <see cref="AgentToolDefinition"/>s.
    /// </summary>
    public MiddtAgentOptions AddToolsFromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.IsPathRooted(directoryPath)
            ? directoryPath
            : Path.Combine(AppContext.BaseDirectory, directoryPath);

        if (!Directory.Exists(fullPath))
        {
            return this;
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var json = File.ReadAllText(file);
            var node = JsonNode.Parse(json);
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item.Deserialize<AgentToolDefinition>(JsonOptions) is { Name.Length: > 0 } tool)
                    {
                        AddDeclarativeTool(tool);
                    }
                }
            }
            else if (node is JsonObject obj)
            {
                var tool = obj.Deserialize<AgentToolDefinition>(JsonOptions);
                if (tool is not null)
                {
                    if (string.IsNullOrWhiteSpace(tool.Name))
                    {
                        tool.Name = Path.GetFileNameWithoutExtension(file);
                    }

                    AddDeclarativeTool(tool);
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Loads <see cref="McpServerDefinition"/> JSON files (<c>*.json</c>) from <paramref name="directoryPath"/>.
    /// Each file may contain a single <see cref="McpServerDefinition"/> or an array of <see cref="McpServerDefinition"/>s.
    /// </summary>
    public MiddtAgentOptions AddMcpServersFromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.IsPathRooted(directoryPath)
            ? directoryPath
            : Path.Combine(AppContext.BaseDirectory, directoryPath);

        if (!Directory.Exists(fullPath))
        {
            return this;
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var json = File.ReadAllText(file);
            var node = JsonNode.Parse(json);
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item.Deserialize<McpServerDefinition>(JsonOptions) is { Name.Length: > 0 } server)
                    {
                        AddMcpServer(server);
                    }
                }
            }
            else if (node is JsonObject obj)
            {
                var server = obj.Deserialize<McpServerDefinition>(JsonOptions);
                if (server is not null)
                {
                    if (string.IsNullOrWhiteSpace(server.Name))
                    {
                        server.Name = Path.GetFileNameWithoutExtension(file);
                    }

                    AddMcpServer(server);
                }
            }
        }

        return this;
    }

    /// <summary>Registers an <see cref="AITool"/> (such as an <see cref="AIFunction"/>) by its <see cref="AITool.Name"/>.</summary>
    public MiddtAgentOptions AddTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Tools[tool.Name] = tool;
        return this;
    }

    /// <summary>Wraps a <see cref="Delegate"/> into an <see cref="AIFunction"/> via <see cref="AIFunctionFactory"/> and registers it by name.</summary>
    public MiddtAgentOptions AddTool(string name, Delegate method, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(method);
        var fn = AIFunctionFactory.Create(method, name, description);
        Tools[name] = fn;
        return this;
    }

    private static bool IsCatalogObject(JsonObject obj) =>
        obj["agents"] is JsonArray
        || (obj["tools"] is JsonArray toolsArr && toolsArr.Any(t => t is JsonObject))
        || (obj["mcpServers"] is JsonArray mcpArr && mcpArr.Any(s => s is JsonObject));
}
