namespace Middt.Framework.Agents;

/// <summary>
/// Configuration settings for the Microsoft Agent Framework integration (<c>AgentSettings</c> section).
/// </summary>
public sealed class AgentSettings
{
    public const string SectionName = "AgentSettings";

    /// <summary>
    /// Underlying AI provider (<c>OpenAI</c>, <c>DeepSeek</c>, <c>Ollama</c>, <c>Local</c>, or any OpenAI-compatible provider).
    /// When <see cref="UseLocalFallback"/> is <c>true</c> and no external credentials are active,
    /// the deterministic local <c>IChatClient</c> is used automatically.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Base URI for OpenAI-compatible endpoints (configured via <c>appsettings.json</c> or <c>AgentSettings__Endpoint</c>).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API key for the model provider (configured via <c>appsettings.Local.json</c> or <c>AgentSettings__ApiKey</c>).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Default model identifier used when an <see cref="AgentDefinition"/> or task does not specify one.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c> (default), uses <c>response_format: { type: "json_schema" }</c> for structured output.
    /// Set to <c>false</c> for OpenAI-compatible providers that require <c>json_object</c> + schema instructions.
    /// </summary>
    public bool UseJsonSchemaResponseFormat { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (default), uses the deterministic local <c>IChatClient</c> if no external API key is configured,
    /// allowing local development, demos, and CI tests to run the full <c>Microsoft.Agents.AI</c> pipeline without external secrets.
    /// </summary>
    public bool UseLocalFallback { get; set; } = true;

    /// <summary>Wraps the <c>IChatClient</c> pipeline with OpenTelemetry distributed tracing (<c>gen_ai.*</c> spans).</summary>
    public bool EnableOpenTelemetry { get; set; } = true;

    /// <summary>Wraps the <c>IChatClient</c> pipeline with automatic <c>AIFunction</c> tool invocation.</summary>
    public bool EnableFunctionInvocation { get; set; } = true;

    /// <summary>Default sampling temperature (<c>0.0</c> to <c>2.0</c>) when not overridden by an agent or task.</summary>
    public float? DefaultTemperature { get; set; }

    /// <summary>Default maximum output tokens when not overridden by an agent or task.</summary>
    public int? DefaultMaxOutputTokens { get; set; }

    /// <summary>Default maximum number of tool calls per agent execution when not overridden by an agent or task.</summary>
    public int? DefaultMaxToolCalls { get; set; }

    /// <summary>Default maximum total token budget (input + output) per agent execution when not overridden by an agent or task.</summary>
    public long? DefaultMaxTotalTokens { get; set; }

    /// <summary>Named agent definitions configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, AgentDefinition> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarative <see cref="AgentToolDefinition"/> entries configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, AgentToolDefinition> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model Context Protocol (MCP) server definitions configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, McpServerDefinition> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
