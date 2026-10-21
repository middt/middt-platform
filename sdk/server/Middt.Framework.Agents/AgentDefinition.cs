using System.Text.Json.Nodes;

namespace Middt.Framework.Agents;

/// <summary>
/// Declarative specification of an AI agent built on the Microsoft Agent Framework (<c>Microsoft.Agents.AI</c>).
/// Can be configured in <c>appsettings.json</c>, loaded from JSON files (<c>Agents/*.json</c>), or registered via
/// <see cref="MiddtAgentOptions"/>.
/// </summary>
public sealed class AgentDefinition
{
    /// <summary>Unique identifier/name of the agent (e.g. <c>invoice-risk-auditor</c> or <c>dispute-orchestrator</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description of the agent's purpose, used in multi-agent routing and handoffs.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// System instructions defining the agent's persona, constraints, and behavior.
    /// Supports <c>{{ }}</c> workflow expressions when executed inside a workflow task.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>Optional model identifier override for this agent (defaults to <see cref="AgentSettings.ModelId"/>).</summary>
    public string? ModelId { get; set; }

    /// <summary>Optional sampling temperature override (<c>0.0</c> to <c>2.0</c>).</summary>
    public float? Temperature { get; set; }

    /// <summary>Optional maximum number of output tokens generated per turn.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Names of registered <c>AITool</c> functions granted to this agent.</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>Names of registered Model Context Protocol (MCP) servers whose tools are granted to this agent.</summary>
    public List<string> McpServers { get; set; } = [];

    /// <summary>Optional list of valid workflow transition/state keys when this agent acts as a workflow router.</summary>
    public List<string> AllowedTransitions { get; set; } = [];

    /// <summary>Optional JSON Schema enforcing structured JSON output from the agent.</summary>
    public JsonObject? OutputSchema { get; set; }
}
