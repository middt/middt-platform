using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Middt.Framework.Agents;

/// <summary>
/// Default implementation of <see cref="IMiddtAgentRegistry"/> backed by <see cref="AgentSettings"/>,
/// <see cref="MiddtAgentOptions"/>, <see cref="IMiddtMcpClient"/>, <c>Microsoft.Agents.AI</c>, and <c>Microsoft.Extensions.AI</c>.
/// </summary>
public sealed partial class MiddtAgentRegistry : IMiddtAgentRegistry
{
    private readonly IServiceProvider _services;
    private readonly AgentSettings _settings;
    private readonly MiddtAgentOptions _options;
    private readonly IMiddtMcpClient _mcpClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, AgentDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public MiddtAgentRegistry(
        IServiceProvider services,
        IOptions<AgentSettings> settings,
        MiddtAgentOptions options,
        IMiddtMcpClient mcpClient,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mcpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _services = services;
        _settings = settings.Value;
        _options = options;
        _mcpClient = mcpClient;
        _loggerFactory = loggerFactory;

        foreach (var tool in MiddtWorkflowAgentTools.CreateDefaultTools(services))
        {
            _tools[tool.Name] = tool;
        }

        foreach (var (key, declTool) in _settings.Tools)
        {
            if (string.IsNullOrWhiteSpace(declTool.Name))
            {
                declTool.Name = key;
            }

            _tools[declTool.Name] = new DeclarativeAiFunction(declTool, services, mcpClient);
        }

        foreach (var (key, declTool) in options.DeclarativeTools)
        {
            if (string.IsNullOrWhiteSpace(declTool.Name))
            {
                declTool.Name = key;
            }

            _tools[declTool.Name] = new DeclarativeAiFunction(declTool, services, mcpClient);
        }

        foreach (var server in mcpClient.GetServers())
        {
            foreach (var mcpTool in server.Tools)
            {
                var boundTool = new AgentToolDefinition
                {
                    Name = mcpTool.Name,
                    Description = mcpTool.Description,
                    Kind = "mcp",
                    McpServer = server.Name,
                    McpTool = mcpTool.Name,
                    Parameters = mcpTool.Parameters,
                };
                var fn = new DeclarativeAiFunction(boundTool, services, mcpClient);
                _tools.TryAdd(mcpTool.Name, fn);
                _tools[$"mcp:{server.Name}:{mcpTool.Name}"] = new DeclarativeAiFunction(boundTool, services, mcpClient, $"{server.Name}_{mcpTool.Name}");
            }
        }

        foreach (var (name, tool) in options.Tools)
        {
            _tools[name] = tool;
        }

        foreach (var (key, def) in _settings.Agents)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                def.Name = key;
            }

            _definitions[def.Name] = def;
        }

        foreach (var (key, def) in options.Agents)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                def.Name = key;
            }

            _definitions[def.Name] = def;
        }
    }

    public AgentDefinition? GetDefinition(string name) =>
        !string.IsNullOrWhiteSpace(name) && _definitions.TryGetValue(name, out var def) ? def : null;

    public IReadOnlyCollection<AgentDefinition> GetAllDefinitions() => _definitions.Values;

    public AITool? GetTool(string name) =>
        !string.IsNullOrWhiteSpace(name) && _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyCollection<AITool> GetAllTools() => _tools.Values;

    public AIAgent CreateAgent(
        string agentName,
        string? instructionsOverride = null,
        IEnumerable<string>? additionalTools = null,
        string? modelOverride = null)
    {
        var baseDef = GetDefinition(agentName)
            ?? throw new InvalidOperationException($"Agent '{agentName}' is not registered in MiddtAgentRegistry.");

        var mergedTools = new List<string>(baseDef.Tools);
        if (additionalTools is not null)
        {
            foreach (var toolName in additionalTools)
            {
                if (!string.IsNullOrWhiteSpace(toolName) && !mergedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
                {
                    mergedTools.Add(toolName);
                }
            }
        }

        var effectiveDef = new AgentDefinition
        {
            Name = baseDef.Name,
            Description = baseDef.Description,
            Instructions = !string.IsNullOrWhiteSpace(instructionsOverride) ? instructionsOverride : baseDef.Instructions,
            ModelId = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : baseDef.ModelId,
            Temperature = baseDef.Temperature,
            MaxOutputTokens = baseDef.MaxOutputTokens,
            Tools = mergedTools,
            McpServers = [.. baseDef.McpServers],
            AllowedTransitions = [.. baseDef.AllowedTransitions],
            OutputSchema = baseDef.OutputSchema,
        };

        return CreateAgent(effectiveDef);
    }

    public AIAgent CreateAgent(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var agentName = string.IsNullOrWhiteSpace(definition.Name) ? "middt-agent" : definition.Name;
        var modelId = ResolveEffectiveModelId(definition.ModelId);
        var chatClient = BuildChatClient(modelId);

        var resolvedTools = new List<AITool>();
        var seenToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolName in definition.Tools)
        {
            if (GetTool(toolName) is { } tool)
            {
                if (seenToolNames.Add(tool.Name))
                {
                    resolvedTools.Add(tool);
                }
            }
            else
            {
                throw new InvalidOperationException($"Agent '{agentName}' references unknown tool '{toolName}'.");
            }
        }

        foreach (var serverName in definition.McpServers)
        {
            var server = _mcpClient.GetServer(serverName)
                ?? throw new InvalidOperationException($"Agent '{agentName}' references unknown MCP server '{serverName}'.");
            foreach (var mcpTool in server.Tools)
            {
                if (GetTool(mcpTool.Name) is { } tool && seenToolNames.Add(tool.Name))
                {
                    resolvedTools.Add(tool);
                }
            }
        }

        var chatOptions = new ChatOptions
        {
            Instructions = definition.Instructions,
            ModelId = modelId,
            Temperature = definition.Temperature ?? _settings.DefaultTemperature,
            MaxOutputTokens = definition.MaxOutputTokens ?? _settings.DefaultMaxOutputTokens,
            Tools = resolvedTools.Count > 0 ? resolvedTools : null,
        };

        if (definition.OutputSchema is { } schemaNode)
        {
            if (IsDeepSeekExternalActive(modelId))
            {
                var schemaHint = $"\n\nIMPORTANT: Respond ONLY with a valid json object matching this JSON Schema (no markdown code fences):\n{schemaNode.ToJsonString()}";
                chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
                    ? schemaHint.TrimStart()
                    : chatOptions.Instructions + schemaHint;

                if (resolvedTools.Count == 0)
                {
                    chatOptions.ResponseFormat = ChatResponseFormat.Json;
                }
            }
            else
            {
                chatOptions.ResponseFormat = CreateJsonSchemaResponseFormat(agentName, schemaNode);
            }
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = agentName,
            Description = definition.Description,
            ChatOptions = chatOptions,
        };

        return new ChatClientAgent(chatClient, agentOptions, _loggerFactory, _services);
    }

    public AIAgent CreateWorkflowAgent(string mode, IReadOnlyList<AIAgent> agents, string? workflowName = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
        {
            throw new ArgumentException("At least one AIAgent is required to build an agent workflow.", nameof(agents));
        }

        var name = string.IsNullOrWhiteSpace(workflowName) ? $"middt-{mode}-workflow" : workflowName;
        var normalizedMode = (mode ?? "sequential").Trim().ToLowerInvariant();

        Microsoft.Agents.AI.Workflows.Workflow workflow = normalizedMode switch
        {
            "sequential" => AgentWorkflowBuilder.BuildSequential(name, agents),
            "concurrent" or "parallel" => AgentWorkflowBuilder.BuildConcurrent(name, agents),
            "handoff" => BuildHandoffWorkflow(agents),
            _ => throw new InvalidOperationException($"Unsupported multi-agent workflow mode '{mode}'. Supported modes: 'sequential', 'concurrent', 'handoff'."),
        };

        return workflow.AsAIAgent(id: name, name: name, description: $"Multi-agent ({normalizedMode}) workflow");
    }

    internal static ChatResponseFormatJson CreateJsonSchemaResponseFormat(string schemaName, JsonObject schemaNode)
    {
        var sanitizedName = NonAlphanumericOrUnderscoreRegex().Replace(schemaName, "_");
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "response_schema";
        }

        using var doc = JsonDocument.Parse(schemaNode.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            doc.RootElement.Clone(),
            schemaName: sanitizedName,
            schemaDescription: schemaNode["description"]?.GetValue<string>());
    }

    private static Microsoft.Agents.AI.Workflows.Workflow BuildHandoffWorkflow(IReadOnlyList<AIAgent> agents)
    {
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(agents[0]);
        if (agents.Count > 1)
        {
            builder.WithHandoffs(agents[0], agents.Skip(1));
        }

        return builder.Build();
    }

    private IChatClient BuildChatClient(string modelId)
    {
        IChatClient innerClient;
        if (_options.ChatClientFactory is not null)
        {
            innerClient = _options.ChatClientFactory(_services, _settings, modelId);
        }
        else if (_services.GetService<IChatClient>() is { } registeredClient)
        {
            innerClient = registeredClient;
        }
        else
        {
            innerClient = CreateProviderChatClient(modelId);
        }

        var builder = innerClient.AsBuilder();
        if (_settings.EnableOpenTelemetry)
        {
            builder.UseOpenTelemetry(_loggerFactory);
        }

        if (_settings.EnableFunctionInvocation)
        {
            builder.UseFunctionInvocation(_loggerFactory);
        }

        return builder.Build(_services);
    }

    private string ResolveEffectiveModelId(string? definitionModelId)
    {
        if (!string.IsNullOrWhiteSpace(definitionModelId))
        {
            return definitionModelId;
        }

        var configured = _settings.ModelId;
        if (IsDeepSeekProvider(configured) && (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)))
        {
            return "deepseek-chat";
        }

        return string.IsNullOrWhiteSpace(configured) ? "gpt-4o-mini" : configured;
    }

    private bool IsDeepSeekProvider(string? modelId = null)
    {
        var provider = (_settings.Provider ?? string.Empty).Trim();
        return string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase)
               || (_settings.Endpoint?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) == true)
               || (modelId?.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) == true);
    }

    private string? ResolveApiKey(bool isDeepSeek)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return _settings.ApiKey;
        }

        return isDeepSeek
            ? (Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
            : (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"));
    }

    private string? ResolveEndpoint(bool isDeepSeek)
    {
        if (!string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            return _settings.Endpoint;
        }

        return isDeepSeek ? "https://api.deepseek.com/v1" : null;
    }

    private bool IsDeepSeekExternalActive(string modelId)
    {
        if (_options.ChatClientFactory is not null || _services.GetService<IChatClient>() is not null)
        {
            return false;
        }

        var isDeepSeek = IsDeepSeekProvider(modelId);
        return isDeepSeek && !string.IsNullOrWhiteSpace(ResolveApiKey(isDeepSeek: true));
    }

    private IChatClient CreateProviderChatClient(string modelId)
    {
        var provider = (_settings.Provider ?? "OpenAI").Trim();
        var isDeepSeek = IsDeepSeekProvider(modelId);
        var isOllama = string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase);
        var isLocalProvider = string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase);

        var apiKey = ResolveApiKey(isDeepSeek);
        var endpoint = ResolveEndpoint(isDeepSeek);

        // Ollama only requires an endpoint; OpenAI and DeepSeek require a non-empty API key.
        var hasExternalConfig = isOllama
            ? (!string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(apiKey))
            : !string.IsNullOrWhiteSpace(apiKey);

        if (isLocalProvider || (!hasExternalConfig && _settings.UseLocalFallback))
        {
            return new LocalAgentChatClient(modelId);
        }

        if (!hasExternalConfig)
        {
            throw new InvalidOperationException(
                "AgentSettings:ApiKey (or DEEPSEEK_API_KEY / OPENAI_API_KEY) is required when UseLocalFallback is false.");
        }

        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "ollama-or-compatible" : apiKey);
        OpenAIClient openAiClient;

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint, UriKind.Absolute),
            };
            openAiClient = new OpenAIClient(credential, clientOptions);
        }
        else
        {
            openAiClient = new OpenAIClient(credential);
        }

        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericOrUnderscoreRegex();
}
