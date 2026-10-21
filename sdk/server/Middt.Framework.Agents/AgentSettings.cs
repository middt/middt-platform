namespace Middt.Framework.Agents;

/// <summary>
/// Configuration settings for the Microsoft Agent Framework integration (<c>AgentSettings</c> section).
/// </summary>
public sealed class AgentSettings
{
    public const string SectionName = "AgentSettings";

    /// <summary>
    /// Underlying AI provider: <c>OpenAI</c>, <c>OpenAICompatible</c>, <c>Ollama</c>, or <c>Local</c>.
    /// When <see cref="UseLocalFallback"/> is <c>true</c> and neither <see cref="ApiKey"/> nor <see cref="Endpoint"/> is set,
    /// the deterministic local <c>IChatClient</c> is used automatically.
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    /// <summary>
    /// Optional base URI for OpenAI-compatible endpoints (e.g. Azure OpenAI, Ollama <c>http://localhost:11434/v1</c>, vLLM, Foundry).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API key for the model provider. Falls back to the <c>OPENAI_API_KEY</c> environment variable when not set.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Default model identifier used when an <see cref="AgentDefinition"/> or task does not specify one.</summary>
    public string ModelId { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// When <c>true</c> (default), uses the deterministic local <c>IChatClient</c> if no external API key or endpoint is configured,
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

    /// <summary>Named agent definitions configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, AgentDefinition> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarative <see cref="AgentToolDefinition"/> entries configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, AgentToolDefinition> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model Context Protocol (MCP) server definitions configured via <c>appsettings.json</c>.</summary>
    public Dictionary<string, McpServerDefinition> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
