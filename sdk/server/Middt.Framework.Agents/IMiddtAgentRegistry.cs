using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Middt.Framework.Agents;

/// <summary>
/// Registry and factory for Microsoft Agent Framework (<see cref="AIAgent"/> / <see cref="ChatClientAgent"/>) instances,
/// multi-agent workflows (<c>Microsoft.Agents.AI.Workflows</c>), and <see cref="AITool"/> functions.
/// </summary>
public interface IMiddtAgentRegistry
{
    /// <summary>Looks up a registered <see cref="AgentDefinition"/> by name (case-insensitive).</summary>
    AgentDefinition? GetDefinition(string name);

    /// <summary>Returns all registered <see cref="AgentDefinition"/> entries.</summary>
    IReadOnlyCollection<AgentDefinition> GetAllDefinitions();

    /// <summary>Looks up a registered <see cref="AITool"/> by name (case-insensitive).</summary>
    AITool? GetTool(string name);

    /// <summary>Returns all registered <see cref="AITool"/> functions.</summary>
    IReadOnlyCollection<AITool> GetAllTools();

    /// <summary>Registers or updates agents, declarative tools, and MCP servers at runtime from an <see cref="AgentCatalogFile"/>.</summary>
    void RegisterCatalog(AgentCatalogFile catalog);

    /// <summary>
    /// Creates a <see cref="ChatClientAgent"/> (<see cref="AIAgent"/>) from a registered <paramref name="agentName"/>,
    /// with optional runtime overrides.
    /// </summary>
    AIAgent CreateAgent(
        string agentName,
        string? instructionsOverride = null,
        IEnumerable<string>? additionalTools = null,
        string? modelOverride = null);

    /// <summary>
    /// Creates an ad-hoc <see cref="ChatClientAgent"/> (<see cref="AIAgent"/>) from the supplied <paramref name="definition"/>.
    /// </summary>
    AIAgent CreateAgent(AgentDefinition definition);

    /// <summary>
    /// Orchestrates multiple <see cref="AIAgent"/> instances into a unified <see cref="AIAgent"/> using
    /// <c>Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder</c> (<c>sequential</c>, <c>concurrent</c>, or <c>handoff</c>).
    /// </summary>
    AIAgent CreateWorkflowAgent(string mode, IReadOnlyList<AIAgent> agents, string? workflowName = null);
}
