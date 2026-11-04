using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Agents;

/// <summary>
/// Default implementation of <see cref="IMiddtMcpClient"/> supporting remote HTTP JSON-RPC 2.0 MCP servers,
/// declarative in-process MCP servers/tools, and direct bridging to <see cref="WorkflowTaskRegistry"/> (<see cref="IWorkflowTask"/>).
/// </summary>
public sealed class MiddtMcpClient : IMiddtMcpClient
{
    public const string DefaultHttpClientName = "MiddtMcp";
    private const string DefaultToolResultSlot = "__toolResult";
    private static readonly WorkflowExpressionEngine DefaultExpressions = new();

    private readonly IServiceProvider _services;
    private readonly Dictionary<string, McpServerDefinition> _baselineServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, McpServerDefinition> _servers = new(StringComparer.OrdinalIgnoreCase);

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

            _baselineServers[server.Name] = server;
            _servers[server.Name] = server;
        }

        foreach (var (key, server) in options.McpServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = key;
            }

            _baselineServers[server.Name] = server;
            _servers[server.Name] = server;
        }
    }

    public IReadOnlyCollection<McpServerDefinition> GetServers()
    {
        EnsurePersistedLoaded();
        return [.. _servers.Values];
    }

    internal IReadOnlyCollection<McpServerDefinition> GetRegisteredServersSnapshot() => [.. _servers.Values];

    internal void ResetToBaseline()
    {
        _servers.Clear();
        foreach (var (name, server) in _baselineServers)
        {
            _servers[name] = server;
        }
    }

    public McpServerDefinition? GetServer(string serverName)
    {
        EnsurePersistedLoaded();
        return !string.IsNullOrWhiteSpace(serverName) && _servers.TryGetValue(serverName, out var s) ? s : null;
    }

    private void EnsurePersistedLoaded()
    {
        if (_services.GetService<IMiddtAgentRegistry>() is MiddtAgentRegistry concrete)
        {
            concrete.EnsurePersistedCatalogLoaded();
        }
    }

    public void RegisterServer(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.Name))
        {
            throw new ArgumentException("McpServerDefinition.Name is required.", nameof(server));
        }

        _servers[server.Name] = server;
    }

    internal bool RemoveServer(string serverName) =>
        !string.IsNullOrWhiteSpace(serverName) && _servers.TryRemove(serverName, out _);

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
            var rpcParams = new JsonObject
            {
                ["uri"] = uri,
            };

            try
            {
                var rpcResult = await SendJsonRpcAsync(server, "resources/read", rpcParams, cancellationToken);
                if (rpcResult?["contents"] is JsonArray { Count: > 0 } contents)
                {
                    var first = contents[0];
                    if (first?["text"]?.GetValue<string>() is { } text)
                    {
                        return TryParseJsonOrValue(text);
                    }

                    return first?.DeepClone();
                }

                return rpcResult;
            }
            catch when (localResource is not null)
            {
                return RenderWithArgs(localResource.Content, args);
            }
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
        if (tool.Roles.Count > 0)
        {
            var callerUser = WorkflowTaskAmbientContext.Current?.Scope.User ?? WorkflowUser.System;
            if (!string.Equals(callerUser.Id, WorkflowUser.System.Id, StringComparison.Ordinal)
                && !tool.Roles.Any(callerUser.IsInRole)
                && !callerUser.IsInRole("ui-admin"))
            {
                throw new InvalidOperationException(
                    $"User is not allowed to invoke tool '{tool.Name}'. Required roles: [{string.Join(", ", tool.Roles)}].");
            }
        }

        args = CoerceArgumentsBySchema(args, tool.Parameters);

        if (!string.IsNullOrWhiteSpace(tool.Task))
        {
            return await ExecuteWorkflowTaskToolAsync(tool, tool.Task, args, services, cancellationToken);
        }

        var kind = (tool.Kind ?? "expression").Trim().ToLowerInvariant();
        switch (kind)
        {
            case "task" or "workflowtask" or "workflow-task":
                var explicitTask = tool.Task ?? tool.Config?["task"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"Declarative task tool '{tool.Name}' requires 'task'.");
                return await ExecuteWorkflowTaskToolAsync(tool, explicitTask, args, services, cancellationToken);

            case "expression":
                if (tool.Output is not null)
                {
                    return RenderWithArgs(tool.Output, args);
                }

                if (!string.IsNullOrWhiteSpace(tool.Expression))
                {
                    return (WorkflowTaskAmbientContext.Current?.Expressions ?? DefaultExpressions).EvaluateToNode(tool.Expression, CreateExpressionScope(args));
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
                var registry = ResolveTaskRegistry(services);
                if (registry.Find(kind) is not null)
                {
                    return await ExecuteWorkflowTaskToolAsync(tool, kind, args, services, cancellationToken);
                }

                throw new InvalidOperationException($"Unsupported declarative tool kind '{tool.Kind}' on tool '{tool.Name}'.");
        }
    }

    internal static async Task<JsonNode?> ExecuteWorkflowTaskToolAsync(
        AgentToolDefinition tool,
        string taskName,
        JsonObject args,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var taskRegistry = ResolveTaskRegistry(scopedServices);
        var taskImpl = taskRegistry.Resolve(scopedServices, taskName);

        var config = tool.Config?.DeepClone() as JsonObject ?? [];
        if (!string.IsNullOrWhiteSpace(tool.Url) && config["url"] is null)
        {
            config["url"] = tool.Url;
        }

        if (!string.IsNullOrWhiteSpace(tool.Method) && config["method"] is null)
        {
            config["method"] = tool.Method;
        }

        if (tool.Headers is not null && config["headers"] is null)
        {
            config["headers"] = tool.Headers.DeepClone();
        }

        if (tool.QueryParams is not null && config["queryParams"] is null)
        {
            config["queryParams"] = tool.QueryParams.DeepClone();
        }

        if (tool.Body is not null && config["body"] is null)
        {
            config["body"] = tool.Body.DeepClone();
        }

        if (taskImpl is HttpTask)
        {
            if (string.IsNullOrWhiteSpace(config["url"]?.GetValue<string>()))
            {
                throw new InvalidOperationException($"HTTP tool '{tool.Name}' requires 'url'.");
            }

            if (config["method"] is null && config["body"] is not null)
            {
                config["method"] = "POST";
            }
        }

        if (taskImpl is SetTask && config["values"] is null && tool.Output is JsonObject outputValues)
        {
            config["values"] = outputValues.DeepClone();
        }

        var explicitInto = !string.IsNullOrWhiteSpace(tool.Into)
            ? tool.Into
            : config["into"]?.GetValue<string>();
        var intoPath = !string.IsNullOrWhiteSpace(explicitInto) ? explicitInto : DefaultToolResultSlot;
        if (config["into"] is null)
        {
            config["into"] = intoPath;
        }

        var ambient = WorkflowTaskAmbientContext.Current;
        var workingData = ambient is not null ? (JsonObject)ambient.Data.DeepClone() : [];
        foreach (var (k, v) in args)
        {
            workingData[k] = v?.DeepClone();
        }

        var preExecutionSnapshot = (JsonObject)workingData.DeepClone();

        var expressionScope = CreateExpressionScope(args, workingData);

        var loggerFactory = scopedServices.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var taskDef = new TaskDefinition
        {
            Key = string.IsNullOrWhiteSpace(tool.Name) ? taskName : tool.Name,
            Task = taskName,
            Config = config,
        };

        var taskContext = new WorkflowTaskContext
        {
            Task = taskDef,
            Data = workingData,
            Scope = expressionScope,
            Expressions = ambient?.Expressions ?? DefaultExpressions,
            InstanceId = ambient?.InstanceId ?? Guid.Empty,
            IdempotencyKey = ambient is not null ? $"{ambient.IdempotencyKey}:tool:{tool.Name}" : $"tool:{tool.Name}:{Guid.CreateVersion7():N}",
            Workflow = ambient?.Workflow ?? string.Empty,
            State = ambient?.State ?? string.Empty,
            Services = scopedServices,
            Logger = ambient?.Logger ?? loggerFactory.CreateLogger($"Middt.AgentTool.{tool.Name}"),
            BusinessKey = ambient?.BusinessKey,
            SetBusinessKey = ambient?.SetBusinessKey,
            StartWorkflow = ambient?.StartWorkflow,
        };

        await taskImpl.ExecuteAsync(taskContext, cancellationToken);

        if (tool.WriteToWorkflowData && ambient is not null)
        {
            foreach (var (key, value) in workingData)
            {
                if (string.Equals(key, DefaultToolResultSlot, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!JsonNode.DeepEquals(value, preExecutionSnapshot[key]))
                {
                    ambient.Data[key] = value?.DeepClone();
                }
            }

            if (!string.IsNullOrWhiteSpace(explicitInto) && JsonPath.Get(workingData, explicitInto) is { } explicitNode)
            {
                JsonPath.Set(ambient.Data, explicitInto, explicitNode.DeepClone());
            }
        }

        var produced = JsonPath.Get(workingData, intoPath);
        if (produced is not null)
        {
            if (taskImpl is HttpTask
                && config["fullResponse"]?.GetValue<bool>() != true
                && produced is JsonObject httpObj
                && httpObj.ContainsKey("statusCode")
                && httpObj.ContainsKey("body"))
            {
                return httpObj["body"]?.DeepClone();
            }

            return produced.DeepClone();
        }

        var diff = new JsonObject();
        foreach (var (key, value) in workingData)
        {
            if (string.Equals(key, DefaultToolResultSlot, StringComparison.Ordinal))
            {
                continue;
            }

            if (!JsonNode.DeepEquals(value, preExecutionSnapshot[key]))
            {
                diff[key] = value?.DeepClone();
            }
        }

        return diff.Count > 0
            ? diff
            : new JsonObject
            {
                ["status"] = "completed",
                ["task"] = taskName,
            };
    }

    internal static WorkflowTaskRegistry ResolveTaskRegistry(IServiceProvider services)
    {
        if (services.GetService<WorkflowTaskRegistry>() is { } registered)
        {
            return registered;
        }

        var configuredOptions = services.GetService<IOptions<MiddtWorkflowOptions>>()?.Value;
        if (configuredOptions is null)
        {
            configuredOptions = new MiddtWorkflowOptions();
            configuredOptions.AddAgentTasks();
        }

        return new WorkflowTaskRegistry(Options.Create(configuredOptions));
    }

    internal static JsonNode? TryParseJsonOrValue(string? text, bool parseJson = true)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        if (parseJson && ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Fall back to string value when text is not valid JSON.
            }
        }

        return JsonValue.Create(text);
    }

    internal static bool TryGetCaseInsensitive(JsonObject obj, string key, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(key, out value))
        {
            return true;
        }

        foreach (var (k, v) in obj)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = null;
        return false;
    }

    internal static JsonNode? CoerceValueToSchema(JsonNode? source, JsonObject schema)
    {
        var candidate = source?.DeepClone();
        var expectedType = schema["type"]?.GetValue<string>();

        if (expectedType == "integer" && candidate is JsonValue iVal)
        {
            if (iVal.TryGetValue<double>(out var d) && !iVal.TryGetValue<long>(out _))
            {
                candidate = JsonValue.Create((long)Math.Round(d, MidpointRounding.AwayFromZero));
            }
            else if (iVal.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
            {
                candidate = JsonValue.Create(parsedLong);
            }
        }
        else if (expectedType == "number" && candidate is JsonValue nVal && nVal.TryGetValue<string>(out var numStr)
                 && double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
        {
            candidate = JsonValue.Create(parsedDouble);
        }
        else if (expectedType == "boolean" && candidate is JsonValue bStr && bStr.TryGetValue<string>(out var boolText)
                 && bool.TryParse(boolText, out var bVal))
        {
            candidate = JsonValue.Create(bVal);
        }

        if (schema["enum"] is JsonArray allowed && allowed.Count > 0)
        {
            var matchesEnum = allowed.Any(a => JsonNode.DeepEquals(a, candidate));
            if (!matchesEnum)
            {
                return null;
            }
        }

        return candidate;
    }

    private static Task<JsonNode?> ExecuteHttpToolAsync(
        AgentToolDefinition tool,
        JsonObject args,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ExecuteWorkflowTaskToolAsync(tool, "http", args, services, cancellationToken);

    private async Task<JsonNode?> SendJsonRpcAsync(
        McpServerDefinition server,
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"MCP server '{server.Name}' has an invalid HTTP/HTTPS endpoint '{server.Endpoint}'.");
        }

        var workflowOptions = _services.GetService<Microsoft.Extensions.Options.IOptions<Middt.Framework.Workflow.MiddtWorkflowOptions>>()?.Value;
        if (workflowOptions is { AllowedHttpHosts.Count: > 0 }
            && !workflowOptions.AllowedHttpHosts.Any(h => string.Equals(h, endpointUri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"MCP server '{server.Name}' host '{endpointUri.Host}' is not in MiddtWorkflowOptions.AllowedHttpHosts.");
        }

        var httpClient = _services.GetRequiredService<IHttpClientFactory>().CreateClient(DefaultHttpClientName);

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
            var msg = err["message"]?.GetValue<string>() ?? "Unknown MCP JSON-RPC error.";
            throw new InvalidOperationException($"MCP server '{server.Name}' returned error: {msg}");
        }

        return root?["result"];
    }

    private static JsonNode? ExtractMcpToolContent(JsonNode? rpcResult)
    {
        if (rpcResult is null)
        {
            return null;
        }

        if (rpcResult["structuredContent"] is { } structured)
        {
            return structured.DeepClone();
        }

        if (rpcResult["content"] is JsonArray { Count: > 0 } contentArr)
        {
            var first = contentArr[0];
            if (first?["text"]?.GetValue<string>() is { } text)
            {
                return TryParseJsonOrValue(text);
            }

            return first?.DeepClone();
        }

        return rpcResult.DeepClone();
    }

    private McpServerDefinition RequireServer(string serverName) =>
        GetServer(serverName)
        ?? throw new InvalidOperationException($"MCP server '{serverName}' is not registered in AgentSettings.McpServers.");

    private static JsonObject CoerceArgumentsBySchema(JsonObject args, JsonObject? schema)
    {
        if (schema?["properties"] is not JsonObject props || args.Count == 0)
        {
            return args;
        }

        var normalized = new JsonObject();
        foreach (var (key, val) in args)
        {
            normalized[key] = val?.DeepClone();
        }

        foreach (var (propName, propNode) in props)
        {
            if (propNode is JsonObject propSchema && normalized[propName] is { } existing)
            {
                normalized[propName] = CoerceValueToSchema(existing, propSchema) ?? existing.DeepClone();
            }
        }

        return normalized;
    }

    private static ExpressionScope CreateExpressionScope(JsonObject args, JsonObject? workingData = null)
    {
        var ambient = WorkflowTaskAmbientContext.Current;
        JsonObject dataObj;
        if (workingData is not null)
        {
            dataObj = workingData;
        }
        else if (ambient is not null)
        {
            dataObj = (JsonObject)ambient.Data.DeepClone();
            foreach (var (k, v) in args)
            {
                dataObj[k] = v?.DeepClone();
            }
        }
        else
        {
            dataObj = args;
        }

        return new ExpressionScope
        {
            Data = dataObj,
            Input = (JsonObject)args.DeepClone(),
            Args = (JsonObject)args.DeepClone(),
            User = ambient?.Scope.User ?? WorkflowUser.System,
            Instance = ambient?.Scope.Instance.DeepClone() as JsonObject ?? [],
            Child = ambient?.Scope.Child?.DeepClone() as JsonObject,
            Now = ambient?.Scope.Now ?? DateTimeOffset.UtcNow,
        };
    }

    private static JsonNode? RenderWithArgs(JsonNode? template, JsonObject args) =>
        (WorkflowTaskAmbientContext.Current?.Expressions ?? DefaultExpressions).Render(template, CreateExpressionScope(args));

    private static string RenderTextWithArgs(string template, JsonObject args) =>
        (WorkflowTaskAmbientContext.Current?.Expressions ?? DefaultExpressions).RenderText(template, CreateExpressionScope(args));
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
