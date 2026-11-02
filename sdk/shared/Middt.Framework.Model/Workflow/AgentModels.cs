using System.Text.Json.Nodes;

namespace Middt.Framework.Model.Workflow;

/// <summary>Wire DTO representing a declarative AI agent definition in the AI Agent Studio (<c>/agent-admin</c>).</summary>
public sealed class AgentDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Instructions { get; set; }

    public string? ModelId { get; set; }

    public float? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }

    public int? MaxToolCalls { get; set; }

    public long? MaxTotalTokens { get; set; }

    public List<string> Tools { get; set; } = [];

    public List<string> McpServers { get; set; } = [];

    public List<string> AllowedTransitions { get; set; } = [];

    public JsonObject? OutputSchema { get; set; }
}

/// <summary>Wire DTO representing a declarative AI tool (<c>expression</c>, <c>task</c>, <c>http</c>, or <c>mcp</c>).</summary>
public sealed class AgentToolDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Kind { get; set; } = "expression";

    public JsonObject? Parameters { get; set; }

    public string? Task { get; set; }

    public JsonObject? Config { get; set; }

    public string? Into { get; set; }

    public bool WriteToWorkflowData { get; set; }

    public string? Expression { get; set; }

    public JsonNode? Output { get; set; }

    public string? Method { get; set; }

    public string? Url { get; set; }

    public JsonObject? QueryParams { get; set; }

    public JsonObject? Headers { get; set; }

    public JsonNode? Body { get; set; }

    public string? McpServer { get; set; }

    public string? McpTool { get; set; }

    public List<string> Roles { get; set; } = [];
}

/// <summary>Wire DTO representing a declarative Model Context Protocol (MCP) resource.</summary>
public sealed class McpResourceDefinitionModel
{
    public string Uri { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string MimeType { get; set; } = "application/json";

    public JsonNode? Content { get; set; }
}

/// <summary>Wire DTO representing a declarative Model Context Protocol (MCP) prompt template.</summary>
public sealed class McpPromptDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Template { get; set; } = string.Empty;
}

/// <summary>Wire DTO representing a Model Context Protocol (MCP) server configuration.</summary>
public sealed class McpServerDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Endpoint { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AgentToolDefinitionModel> Tools { get; set; } = [];

    public List<McpResourceDefinitionModel> Resources { get; set; } = [];

    public List<McpPromptDefinitionModel> Prompts { get; set; } = [];
}

/// <summary>Complete AI Agent, Declarative Tool, and MCP Server catalog returned by <c>GET /api/agents/catalog</c>.</summary>
public sealed class AgentCatalogModel
{
    /// <summary>Configured AI provider name (e.g. <c>DeepSeek</c>, <c>OpenAI</c>, <c>Ollama</c>, <c>Local</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>Default model identifier configured in <c>AgentSettings:ModelId</c>.</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>Whether the deterministic local fallback is enabled when no external API key is active.</summary>
    public bool UseLocalFallback { get; set; } = true;

    /// <summary>Whether an external provider API key or endpoint is currently configured (never exposes the key itself).</summary>
    public bool HasExternalApiKey { get; set; }

    public List<AgentDefinitionModel> Agents { get; set; } = [];

    public List<AgentToolDefinitionModel> Tools { get; set; } = [];

    public List<McpServerDefinitionModel> McpServers { get; set; } = [];
}

/// <summary>Validation result for an AI agent, declarative tool, or MCP server catalog (<c>POST /api/agents/validate</c>).</summary>
public sealed class AgentValidationResultModel
{
    public bool Valid => Errors.Count == 0;

    public List<string> Errors { get; set; } = [];
}

/// <summary>Dry-run playground request for testing an AI agent without persisting workflow instances (<c>POST /api/agents/test</c>).</summary>
public sealed class AgentPlaygroundRequest
{
    /// <summary>Registered agent name to test (optional when <see cref="Definition"/> is provided inline).</summary>
    public string? Agent { get; set; }

    /// <summary>Optional inline draft agent definition from the visual studio editor.</summary>
    public AgentDefinitionModel? Definition { get; set; }

    /// <summary>Prompt message sent to the agent.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Sample workflow <c>data</c> object available to <c>{{ data.* }}</c> templates and declarative/MCP tools.</summary>
    public JsonObject? Data { get; set; }

    /// <summary>Optional override of allowed workflow transition keys for testing AI routing (<c>nextTransition</c>).</summary>
    public List<string>? AllowedTransitions { get; set; }
}

/// <summary>Execution result of an interactive AI agent playground run (<c>POST /api/agents/test</c>).</summary>
public sealed class AgentPlaygroundResultModel
{
    public string AgentName { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public string? Provider { get; set; }

    public bool UsedLocalFallback { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public JsonNode? Output { get; set; }

    public string? RawText { get; set; }

    public string? SelectedTransition { get; set; }

    public List<WorkflowTransitionOptionModel> AllowedTransitions { get; set; } = [];

    public List<WorkflowAgentToolCallModel> ToolCalls { get; set; } = [];

    public JsonObject DataAfter { get; set; } = [];

    public long DurationMs { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }

    public string? FinishReason { get; set; }

    public string? ResponseId { get; set; }
}

