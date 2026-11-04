using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middt.Framework.Workflow.Runtime;
using OpenAI;

namespace Middt.Framework.Agents;

/// <summary>
/// Default implementation of <see cref="IMiddtAgentRegistry"/> backed by <see cref="AgentSettings"/>,
/// <see cref="MiddtAgentOptions"/>, <see cref="IMiddtMcpClient"/>, <c>Microsoft.Agents.AI</c>, and <c>Microsoft.Extensions.AI</c>.
/// </summary>
public sealed partial class MiddtAgentRegistry : IMiddtAgentRegistry, IWorkflowCatalogInvalidationHandler
{
    private static readonly long SyncCooldownTicks = Stopwatch.Frequency;
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IServiceProvider _services;
    private readonly AgentSettings _settings;
    private readonly MiddtAgentOptions _options;
    private readonly IMiddtMcpClient _mcpClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, AgentDefinition> _baselineDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AITool> _baselineTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentToolDefinition> _baselineDeclarativeTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentToolDefinition> _declarativeTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();
    private DateTime _persistedUpdatedAt = DateTime.MinValue;
    private long _lastSyncTicks;
    private volatile bool _cacheInvalidated = true;

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

        foreach (var (key, declTool) in _settings.Tools)
        {
            if (string.IsNullOrWhiteSpace(declTool.Name))
            {
                declTool.Name = key;
            }

            _declarativeTools[declTool.Name] = declTool;
            _tools[declTool.Name] = new DeclarativeAiFunction(declTool, services, mcpClient);
        }

        foreach (var (key, declTool) in options.DeclarativeTools)
        {
            if (string.IsNullOrWhiteSpace(declTool.Name))
            {
                declTool.Name = key;
            }

            _declarativeTools[declTool.Name] = declTool;
            _tools[declTool.Name] = new DeclarativeAiFunction(declTool, services, mcpClient);
        }

        var initialServers = mcpClient is MiddtMcpClient concreteMcp
            ? concreteMcp.GetRegisteredServersSnapshot()
            : mcpClient.GetServers();
        foreach (var server in initialServers)
        {
            RegisterMcpServerTools(server);
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

        foreach (var (k, v) in _definitions)
        {
            _baselineDefinitions[k] = v;
        }

        foreach (var (k, v) in _tools)
        {
            _baselineTools[k] = v;
        }

        foreach (var (k, v) in _declarativeTools)
        {
            _baselineDeclarativeTools[k] = v;
        }
    }

    /// <summary>Gets the active <see cref="AgentSettings"/> for runtime inspection by the Agent Studio.</summary>
    public AgentSettings Settings => _settings;

    /// <summary>Gets whether an external LLM provider or custom <see cref="IChatClient"/> is active.</summary>
    public bool HasExternalProvider =>
        _options.ChatClientFactory is not null
        || _services.GetService<IChatClient>() is not null
        || !string.IsNullOrWhiteSpace(_settings.ApiKey)
        || string.Equals(_settings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase);

    /// <summary>Invalidates the in-memory persisted catalog watermark so the next read re-checks the database immediately.</summary>
    public void InvalidateCache() => _cacheInvalidated = true;

    /// <inheritdoc />
    public void OnCatalogInvalidated(string catalog)
    {
        if (string.IsNullOrEmpty(catalog) || string.Equals(catalog, "agents", StringComparison.OrdinalIgnoreCase))
        {
            InvalidateCache();
        }
    }

    internal void MarkPersistedAt(DateTime updatedAt)
    {
        lock (_sync)
        {
            _persistedUpdatedAt = updatedAt;
            _lastSyncTicks = Stopwatch.GetTimestamp();
            _cacheInvalidated = false;
        }
    }

    internal void ReloadPersistedCatalog() => EnsurePersistedCatalogLoaded(force: true);

    public void RegisterCatalog(AgentCatalogFile catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsurePersistedCatalogLoaded(force: true);
        RegisterCatalogCore(catalog);
    }

    private void ResetToBaselineCore()
    {
        if (_mcpClient is MiddtMcpClient concreteMcp)
        {
            concreteMcp.ResetToBaseline();
        }

        _definitions.Clear();
        foreach (var (k, v) in _baselineDefinitions)
        {
            _definitions[k] = v;
        }

        _tools.Clear();
        foreach (var (k, v) in _baselineTools)
        {
            _tools[k] = v;
        }

        _declarativeTools.Clear();
        foreach (var (k, v) in _baselineDeclarativeTools)
        {
            _declarativeTools[k] = v;
        }
    }

    private void RegisterCatalogCore(AgentCatalogFile catalog)
    {
        foreach (var server in catalog.McpServers.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            _mcpClient.RegisterServer(server);
            RegisterMcpServerTools(server);
        }

        foreach (var declTool in catalog.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            _declarativeTools[declTool.Name] = declTool;
            _tools[declTool.Name] = new DeclarativeAiFunction(declTool, _services, _mcpClient);
        }

        foreach (var agent in catalog.Agents.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            _definitions[agent.Name] = agent;
        }
    }

    public bool RemoveAgent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsurePersistedCatalogLoaded(force: true);
        return _definitions.TryRemove(name, out _);
    }

    public bool RemoveDeclarativeTool(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsurePersistedCatalogLoaded(force: true);
        var removedDecl = _declarativeTools.TryRemove(name, out _);
        var removedTool = _tools.TryRemove(name, out _);
        return removedDecl || removedTool;
    }

    public bool RemoveMcpServer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsurePersistedCatalogLoaded(force: true);
        var prefix = $"mcp:{name}:";
        foreach (var key in _tools.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _tools.TryRemove(key, out _);
        }

        return _mcpClient is MiddtMcpClient concreteMcp && concreteMcp.RemoveServer(name);
    }

    public AgentCatalogFile ExportCatalog()
    {
        EnsurePersistedCatalogLoaded();
        var servers = _mcpClient is MiddtMcpClient concreteMcp
            ? concreteMcp.GetRegisteredServersSnapshot()
            : _mcpClient.GetServers();
        return new()
        {
            Agents = [.. _definitions.Values.OrderBy(a => a.Name, StringComparer.Ordinal)],
            Tools = [.. _declarativeTools.Values.OrderBy(t => t.Name, StringComparer.Ordinal)],
            McpServers = [.. servers.OrderBy(s => s.Name, StringComparer.Ordinal)],
        };
    }

    /// <summary>Validates a draft <see cref="AgentCatalogFile"/> and returns any structural or reference errors.</summary>
    public List<string> ValidateCatalog(AgentCatalogFile catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EnsurePersistedCatalogLoaded();

        var errors = new List<string>();
        var knownTools = new HashSet<string>(_tools.Keys, StringComparer.OrdinalIgnoreCase);
        var knownServers = new HashSet<string>(
            (_mcpClient is MiddtMcpClient concreteMcp ? concreteMcp.GetRegisteredServersSnapshot() : _mcpClient.GetServers())
                .Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var server in catalog.McpServers)
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                errors.Add("MCP sunucu adı (name) boş olamaz.");
                continue;
            }

            knownServers.Add(server.Name);
            foreach (var tool in server.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name))
                {
                    errors.Add($"MCP sunucusu '{server.Name}' içinde adı boş olan bir araç (tool) var.");
                }
                else
                {
                    knownTools.Add(tool.Name);
                    knownTools.Add($"mcp:{server.Name}:{tool.Name}");
                }
            }
        }

        foreach (var tool in catalog.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                errors.Add("Araç (tool) adı boş olamaz.");
                continue;
            }

            var kind = (tool.Kind ?? "expression").Trim().ToLowerInvariant();
            if (kind is not ("expression" or "task" or "http" or "mcp"))
            {
                errors.Add($"Araç '{tool.Name}': geçersiz kind '{tool.Kind}' (expression, task, http veya mcp olmalı).");
            }
            else if (kind == "task" && string.IsNullOrWhiteSpace(tool.Task))
            {
                errors.Add($"Araç '{tool.Name}': kind 'task' seçildiğinde 'task' alanı zorunludur.");
            }
            else if (kind == "http" && string.IsNullOrWhiteSpace(tool.Url))
            {
                errors.Add($"Araç '{tool.Name}': kind 'http' seçildiğinde 'url' alanı zorunludur.");
            }
            else if (kind == "mcp" && (string.IsNullOrWhiteSpace(tool.McpServer) || string.IsNullOrWhiteSpace(tool.McpTool)))
            {
                errors.Add($"Araç '{tool.Name}': kind 'mcp' seçildiğinde 'mcpServer' ve 'mcpTool' alanları zorunludur.");
            }

            knownTools.Add(tool.Name);
        }

        foreach (var agent in catalog.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
            {
                errors.Add("Ajan adı (name) boş olamaz.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(agent.Instructions))
            {
                errors.Add($"Ajan '{agent.Name}': sistem talimatları (instructions) boş olamaz.");
            }

            foreach (var toolName in agent.Tools)
            {
                if (!string.IsNullOrWhiteSpace(toolName) && !knownTools.Contains(toolName))
                {
                    errors.Add($"Ajan '{agent.Name}': tanımsız araç '{toolName}' referans gösterildi.");
                }
            }

            foreach (var serverName in agent.McpServers)
            {
                if (!string.IsNullOrWhiteSpace(serverName) && !knownServers.Contains(serverName))
                {
                    errors.Add($"Ajan '{agent.Name}': tanımsız MCP sunucusu '{serverName}' referans gösterildi.");
                }
            }
        }

        return errors;
    }

    private void RegisterMcpServerTools(McpServerDefinition server)
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
                Roles = [.. mcpTool.Roles],
            };
            var fn = new DeclarativeAiFunction(boundTool, _services, _mcpClient);
            _tools.TryAdd(mcpTool.Name, fn);
            _tools[$"mcp:{server.Name}:{mcpTool.Name}"] = new DeclarativeAiFunction(boundTool, _services, _mcpClient, $"{server.Name}_{mcpTool.Name}");
        }
    }

    internal void EnsurePersistedCatalogLoaded(bool force = false)
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if (!force && !_cacheInvalidated && nowTicks - Interlocked.Read(ref _lastSyncTicks) < SyncCooldownTicks)
        {
            return;
        }

        lock (_sync)
        {
            nowTicks = Stopwatch.GetTimestamp();
            if (!force && !_cacheInvalidated && nowTicks - _lastSyncTicks < SyncCooldownTicks)
            {
                return;
            }

            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetService<Middt.Framework.Workflow.Persistence.WorkflowDbContext>();
                if (db is null)
                {
                    _lastSyncTicks = nowTicks;
                    _cacheInvalidated = false;
                    return;
                }

                var meta = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Definitions)
                    .Where(d => d.Key == AgentEndpoints.PersistedCatalogKey && d.Version == 1)
                    .Select(d => new { d.UpdatedAt })
                    .FirstOrDefault();

                _lastSyncTicks = nowTicks;
                _cacheInvalidated = false;

                if (meta is null || meta.UpdatedAt <= _persistedUpdatedAt)
                {
                    return;
                }

                var record = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Definitions)
                    .FirstOrDefault(d => d.Key == AgentEndpoints.PersistedCatalogKey && d.Version == 1);
                if (record is not null && !string.IsNullOrWhiteSpace(record.Content))
                {
                    var catalog = JsonSerializer.Deserialize<AgentCatalogFile>(record.Content, CatalogJsonOptions);
                    if (catalog is not null)
                    {
                        ResetToBaselineCore();
                        RegisterCatalogCore(catalog);
                        _persistedUpdatedAt = record.UpdatedAt;
                    }
                }
            }
            catch
            {
                // Database or table may not be initialized yet; will retry on subsequent calls.
            }
        }
    }

    public AgentDefinition? GetDefinition(string name)
    {
        EnsurePersistedCatalogLoaded();
        return !string.IsNullOrWhiteSpace(name) && _definitions.TryGetValue(name, out var def) ? def : null;
    }

    public IReadOnlyCollection<AgentDefinition> GetAllDefinitions()
    {
        EnsurePersistedCatalogLoaded();
        return [.. _definitions.Values];
    }

    public AITool? GetTool(string name)
    {
        EnsurePersistedCatalogLoaded();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (_tools.TryGetValue(name, out var tool))
        {
            return tool;
        }

        var taskName = name.StartsWith("task:", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
        if (!string.IsNullOrWhiteSpace(taskName) && MiddtMcpClient.ResolveTaskRegistry(_services).Find(taskName) is not null)
        {
            return new DeclarativeAiFunction(
                new AgentToolDefinition
                {
                    Name = name,
                    Kind = "task",
                    Task = taskName,
                    Description = $"Executes the '{taskName}' workflow task.",
                },
                _services,
                _mcpClient);
        }

        return null;
    }

    public IReadOnlyCollection<AITool> GetAllTools()
    {
        EnsurePersistedCatalogLoaded();
        return [.. _tools.Values];
    }

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

        var agentName = string.IsNullOrWhiteSpace(definition.Name) ? "agent" : definition.Name;
        var modelId = !string.IsNullOrWhiteSpace(definition.ModelId) ? definition.ModelId : _settings.ModelId;
        var chatClient = BuildChatClient(modelId);

        var resolvedTools = new List<AITool>();
        var seenToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolName in definition.Tools)
        {
            if (GetTool(toolName) is { } tool)
            {
                if (IsToolAllowedForAmbientCaller(tool) && seenToolNames.Add(tool.Name))
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
                if (GetTool(mcpTool.Name) is { } tool && IsToolAllowedForAmbientCaller(tool) && seenToolNames.Add(tool.Name))
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
            if (!_settings.UseJsonSchemaResponseFormat && HasExternalProviderConfigured())
            {
                var schemaHint = $"\n\nRespond ONLY with a valid json object matching this JSON Schema (no markdown code fences):\n{schemaNode.ToJsonString()}";
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

        if (definition.AllowedTransitions.Count > 0)
        {
            var transitionHint = $"\nAllowed workflow transitions for routing: [{string.Join(", ", definition.AllowedTransitions)}].";
            chatOptions.Instructions = string.IsNullOrWhiteSpace(chatOptions.Instructions)
                ? transitionHint.TrimStart()
                : chatOptions.Instructions + transitionHint;
        }

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = agentName,
                Name = agentName,
                Description = definition.Description,
                ChatOptions = chatOptions,
            },
            _loggerFactory,
            _services);
    }

    public AIAgent CreateWorkflowAgent(string mode, IReadOnlyList<AIAgent> agents, string? workflowName = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
        {
            throw new ArgumentException("At least one AIAgent is required to build an agent workflow.", nameof(agents));
        }

        var name = string.IsNullOrWhiteSpace(workflowName) ? $"{mode}-workflow" : workflowName;
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

    private static bool IsToolAllowedForAmbientCaller(AITool tool)
    {
        if (tool is not DeclarativeAiFunction { Definition.Roles.Count: > 0 } declFn)
        {
            return true;
        }

        var ambient = WorkflowTaskAmbientContext.Current;
        if (ambient is null)
        {
            return true;
        }

        var caller = ambient.Scope.User;
        if (string.Equals(caller.Id, Middt.Framework.Workflow.Expressions.WorkflowUser.System.Id, StringComparison.Ordinal)
            || caller.IsInRole("ui-admin"))
        {
            return true;
        }

        return declFn.Definition.Roles.Any(caller.IsInRole);
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

    private bool HasExternalProviderConfigured()
    {
        if (_options.ChatClientFactory is not null || _services.GetService<IChatClient>() is not null)
        {
            return false;
        }

        var isOllama = string.Equals(_settings.Provider?.Trim(), "Ollama", StringComparison.OrdinalIgnoreCase);
        return isOllama
            ? (!string.IsNullOrWhiteSpace(_settings.Endpoint) || !string.IsNullOrWhiteSpace(_settings.ApiKey))
            : !string.IsNullOrWhiteSpace(_settings.ApiKey);
    }

    private IChatClient CreateProviderChatClient(string modelId)
    {
        var isLocalProvider = string.Equals(_settings.Provider?.Trim(), "Local", StringComparison.OrdinalIgnoreCase);
        var hasExternalConfig = HasExternalProviderConfigured();

        if (isLocalProvider || (!hasExternalConfig && _settings.UseLocalFallback))
        {
            return new LocalAgentChatClient(modelId);
        }

        if (!hasExternalConfig)
        {
            throw new InvalidOperationException(
                "AgentSettings:ApiKey is required when UseLocalFallback is false.");
        }

        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(_settings.ApiKey) ? "local" : _settings.ApiKey);
        OpenAIClient openAiClient;

        if (!string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(_settings.Endpoint, UriKind.Absolute),
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
