using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Middt.Framework.Expressions;

namespace Middt.Framework.Agents;

/// <summary>
/// Default implementation of <see cref="IMiddtMcpClient"/> supporting both remote HTTP JSON-RPC 2.0 MCP servers
/// and declarative in-process MCP servers/tools.
/// </summary>
public sealed class MiddtMcpClient : IMiddtMcpClient
{
    public const string DefaultHttpClientName = "MiddtMcp";

    private static readonly ExpressionEvaluator Evaluator = new(["args", "data", "input"], []);
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, McpServerDefinition> _servers = new(StringComparer.OrdinalIgnoreCase);

    public MiddtMcpClient(
        IServiceProvider services,
        IOptions<AgentSettings> settings,
        MiddtAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        _services = services;

        foreach (var (key, server) in settings.Value.McpServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = key;
            }

            _servers[server.Name] = server;
        }

        foreach (var (key, server) in options.McpServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = key;
            }

            _servers[server.Name] = server;
        }
    }

    public IReadOnlyCollection<McpServerDefinition> GetServers() => _servers.Values;

    public McpServerDefinition? GetServer(string serverName) =>
        !string.IsNullOrWhiteSpace(serverName) && _servers.TryGetValue(serverName, out var s) ? s : null;

    public async Task<JsonArray> ListToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var server = RequireServer(serverName);
        if (!string.IsNullOrWhiteSpace(server.Endpoint) && server.Tools.Count == 0)
        {
            var rpcResult = await SendJsonRpcAsync(server, "tools/list", new JsonObject(), cancellationToken);
            if (rpcResult?["tools"] is JsonArray remoteTools)
            {
                return (JsonArray)remoteTools.DeepClone();
            }
        }

        var tools = new JsonArray();
        foreach (var t in server.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.Parameters?.DeepClone() ?? new JsonObject { ["type"] = "object" },
            });
        }

        return tools;
    }

    public async Task<JsonNode?> CallToolAsync(
        string serverName,
        string toolName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var server = RequireServer(serverName);
        var args = arguments ?? [];

        var localTool = server.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (localTool is not null && string.IsNullOrWhiteSpace(server.Endpoint))
        {
            return await ExecuteDeclarativeToolAsync(localTool, args, _services, this, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(server.Endpoint))
        {
            var rpcParams = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args.DeepClone(),
            };

            try
            {
                var rpcResult = await SendJsonRpcAsync(server, "tools/call", rpcParams, cancellationToken);
                return ExtractMcpToolContent(rpcResult);
            }
            catch when (localTool is not null)
            {
                return await ExecuteDeclarativeToolAsync(localTool, args, _services, this, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Tool '{toolName}' was not found on MCP server '{serverName}'.");
    }

    public async Task<JsonArray> ListResourcesAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var server = RequireServer(serverName);
        if (!string.IsNullOrWhiteSpace(server.Endpoint) && server.Resources.Count == 0)
        {
            var rpcResult = await SendJsonRpcAsync(server, "resources/list", new JsonObject(), cancellationToken);
            if (rpcResult?["resources"] is JsonArray remoteResources)
            {
                return (JsonArray)remoteResources.DeepClone();
            }
        }

        var resources = new JsonArray();
        foreach (var r in server.Resources)
        {
            resources.Add(new JsonObject
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["mimeType"] = r.MimeType,
            });
        }

        return resources;
    }

    public async Task<JsonNode?> ReadResourceAsync(
        string serverName,
        string uri,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var server = RequireServer(serverName);
        var args = arguments ?? [];

        var localResource = server.Resources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase)
                                                                 || string.Equals(r.Name, uri, StringComparison.OrdinalIgnoreCase));
        if (localResource is not null && string.IsNullOrWhiteSpace(server.Endpoint))
        {
            return RenderWithArgs(localResource.Content, args);
        }

        if (!string.IsNullOrWhiteSpace(server.Endpoint))
        {
            var rpcResult = await SendJsonRpcAsync(server, "resources/read", new JsonObject { ["uri"] = uri }, cancellationToken);
            if (rpcResult?["contents"] is JsonArray { Count: > 0 } contents && contents[0] is JsonObject first)
            {
                if (first["text"]?.GetValue<string>() is { } text)
                {
                    return TryParseJsonOrValue(text);
                }

                return first.DeepClone();
            }

            return rpcResult;
        }

        throw new InvalidOperationException($"Resource '{uri}' was not found on MCP server '{serverName}'.");
    }

    public async Task<string> GetPromptAsync(
        string serverName,
        string promptName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var server = RequireServer(serverName);
        var args = arguments ?? [];

        var localPrompt = server.Prompts.FirstOrDefault(p => string.Equals(p.Name, promptName, StringComparison.OrdinalIgnoreCase));
        if (localPrompt is not null && string.IsNullOrWhiteSpace(server.Endpoint))
        {
            return RenderTextWithArgs(localPrompt.Template, args);
        }

        if (!string.IsNullOrWhiteSpace(server.Endpoint))
        {
            var rpcParams = new JsonObject
            {
                ["name"] = promptName,
                ["arguments"] = args.DeepClone(),
            };
            var rpcResult = await SendJsonRpcAsync(server, "prompts/get", rpcParams, cancellationToken);
            if (rpcResult?["messages"] is JsonArray { Count: > 0 } messages
                && messages[0]?["content"]?["text"]?.GetValue<string>() is { } text)
            {
                return text;
            }
        }

        throw new InvalidOperationException($"Prompt '{promptName}' was not found on MCP server '{serverName}'.");
    }

    internal static async Task<JsonNode?> ExecuteDeclarativeToolAsync(
        AgentToolDefinition tool,
        JsonObject args,
        IServiceProvider services,
        IMiddtMcpClient mcpClient,
        CancellationToken cancellationToken)
    {
        args = CoerceArgumentsBySchema(args, tool.Parameters);
        var kind = (tool.Kind ?? "expression").Trim().ToLowerInvariant();
        switch (kind)
        {
            case "expression":
                if (tool.Output is not null)
                {
                    return RenderWithArgs(tool.Output, args);
                }

                if (!string.IsNullOrWhiteSpace(tool.Expression))
                {
                    var context = CreateExpressionContext(args);
                    return Evaluator.EvaluateToNode(tool.Expression, context);
                }

                return args.DeepClone();

            case "http":
                return await ExecuteHttpToolAsync(tool, args, services, cancellationToken);

            case "mcp":
                var server = !string.IsNullOrWhiteSpace(tool.McpServer)
                    ? tool.McpServer
                    : throw new InvalidOperationException($"Declarative MCP tool '{tool.Name}' requires 'mcpServer'.");
                var targetTool = !string.IsNullOrWhiteSpace(tool.McpTool) ? tool.McpTool : tool.Name;
                return await mcpClient.CallToolAsync(server, targetTool, args, cancellationToken);

            default:
                throw new InvalidOperationException($"Unsupported declarative tool kind '{tool.Kind}' on tool '{tool.Name}'.");
        }
    }

    private static async Task<JsonNode?> ExecuteHttpToolAsync(
        AgentToolDefinition tool,
        JsonObject args,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tool.Url))
        {
            throw new InvalidOperationException($"HTTP tool '{tool.Name}' requires 'url'.");
        }

        var url = RenderTextWithArgs(tool.Url, args);
        var methodStr = !string.IsNullOrWhiteSpace(tool.Method)
            ? tool.Method
            : (tool.Body is not null ? "POST" : "GET");

        var clientFactory = services.GetService<IHttpClientFactory>();
        using var httpClient = clientFactory is not null
            ? clientFactory.CreateClient(DefaultHttpClientName)
            : new HttpClient();

        using var request = new HttpRequestMessage(new HttpMethod(methodStr.ToUpperInvariant()), url);
        if (tool.Headers is not null && RenderWithArgs(tool.Headers, args) is JsonObject headersObj)
        {
            foreach (var (k, v) in headersObj)
            {
                if (v?.GetValue<string>() is { Length: > 0 } headerVal)
                {
                    request.Headers.TryAddWithoutValidation(k, headerVal);
                }
            }
        }

        if (tool.Body is not null)
        {
            var renderedBody = RenderWithArgs(tool.Body, args);
            request.Content = new StringContent(
                renderedBody?.ToJsonString() ?? "{}",
                Encoding.UTF8,
                "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryParseJsonOrValue(responseText);
    }

    private async Task<JsonNode?> SendJsonRpcAsync(
        McpServerDefinition server,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var clientFactory = _services.GetService<IHttpClientFactory>();
        using var httpClient = clientFactory is not null
            ? clientFactory.CreateClient(DefaultHttpClientName)
            : new HttpClient();

        var rpcRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.CreateVersion7().ToString("N"),
            ["method"] = method,
            ["params"] = parameters,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = new StringContent(rpcRequest.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        foreach (var (k, v) in server.Headers)
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body) as JsonObject;
        if (root?["error"] is JsonObject err)
        {
            var msg = err["message"]?.GetValue<string>() ?? err.ToJsonString();
            throw new InvalidOperationException($"MCP server '{server.Name}' returned JSON-RPC error for '{method}': {msg}");
        }

        return root?["result"];
    }

    private static JsonNode? ExtractMcpToolContent(JsonNode? rpcResult)
    {
        if (rpcResult is not JsonObject obj)
        {
            return rpcResult;
        }

        if (obj["structuredContent"] is { } structured)
        {
            return structured.DeepClone();
        }

        if (obj["content"] is JsonArray { Count: > 0 } contentArr && contentArr[0] is JsonObject first)
        {
            if (first["text"]?.GetValue<string>() is { } text)
            {
                return TryParseJsonOrValue(text);
            }
        }

        return obj.DeepClone();
    }

    private McpServerDefinition RequireServer(string serverName) =>
        GetServer(serverName)
        ?? throw new InvalidOperationException($"MCP server '{serverName}' is not registered.");

    private static JsonObject CoerceArgumentsBySchema(JsonObject args, JsonObject? parametersSchema)
    {
        var normalized = args.DeepClone().AsObject();
        if (parametersSchema?["properties"] is not JsonObject props)
        {
            return normalized;
        }

        foreach (var (propName, propNode) in props)
        {
            if (propNode is not JsonObject propSchema || !normalized.TryGetPropertyValue(propName, out var valNode) || valNode is not JsonValue jv)
            {
                continue;
            }

            if (!jv.TryGetValue<string>(out var rawStr))
            {
                continue;
            }

            var expectedType = propSchema["type"]?.GetValue<string>();
            if (expectedType == "integer" && long.TryParse(rawStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var lVal))
            {
                normalized[propName] = JsonValue.Create(lVal);
            }
            else if (expectedType == "number" && double.TryParse(rawStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dVal))
            {
                normalized[propName] = JsonValue.Create(dVal);
            }
            else if (expectedType == "boolean" && bool.TryParse(rawStr, out var bVal))
            {
                normalized[propName] = JsonValue.Create(bVal);
            }
        }

        return normalized;
    }

    private static ExpressionContext CreateExpressionContext(JsonObject args) =>
        new ExpressionContext { Now = DateTime.UtcNow }
            .Set("args", args)
            .Set("data", args)
            .Set("input", args);

    private static JsonNode? RenderWithArgs(JsonNode? template, JsonObject args) =>
        Evaluator.Render(template, CreateExpressionContext(args));

    private static string RenderTextWithArgs(string template, JsonObject args) =>
        Evaluator.RenderText(template, CreateExpressionContext(args));

    private static JsonNode? TryParseJsonOrValue(string text)
    {
        var trimmed = text.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Fall back to string value
            }
        }

        return JsonValue.Create(text);
    }
}

/// <summary>
/// Wraps a declarative <see cref="AgentToolDefinition"/> as a <see cref="Microsoft.Extensions.AI.AIFunction"/>
/// so it can be invoked natively by <c>Microsoft.Agents.AI</c> (<c>ChatClientAgent</c> and <c>FunctionInvokingChatClient</c>).
/// </summary>
public sealed class DeclarativeAiFunction : AIFunction
{
    private readonly AgentToolDefinition _definition;
    private readonly string _name;
    private readonly IServiceProvider _services;
    private readonly IMiddtMcpClient _mcpClient;
    private readonly JsonElement _jsonSchema;

    public DeclarativeAiFunction(
        AgentToolDefinition definition,
        IServiceProvider services,
        IMiddtMcpClient mcpClient,
        string? nameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mcpClient);

        _definition = definition;
        _name = !string.IsNullOrWhiteSpace(nameOverride) ? nameOverride : definition.Name;
        _services = services;
        _mcpClient = mcpClient;

        var schemaText = definition.Parameters?.ToJsonString() ?? """{"type":"object"}""";
        using var doc = JsonDocument.Parse(schemaText);
        _jsonSchema = doc.RootElement.Clone();
    }

    public override string Name => _name;

    public override string Description => _definition.Description ?? string.Empty;

    public override JsonElement JsonSchema => _jsonSchema;

    public AgentToolDefinition Definition => _definition;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var argsObj = ConvertArgumentsToJsonObject(arguments);
        var resultNode = await MiddtMcpClient.ExecuteDeclarativeToolAsync(
            _definition,
            argsObj,
            _services,
            _mcpClient,
            cancellationToken);

        return resultNode?.ToJsonString() ?? "null";
    }

    internal static JsonObject ConvertArgumentsToJsonObject(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var obj = new JsonObject();
        if (arguments is null)
        {
            return obj;
        }

        foreach (var (key, value) in arguments)
        {
            obj[key] = value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                decimal dec => JsonValue.Create(dec),
                float f => JsonValue.Create(f),
                _ => JsonSerializer.SerializeToNode(value),
            };
        }

        return obj;
    }
}
