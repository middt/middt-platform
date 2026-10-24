using System.Text.Json.Nodes;

namespace Middt.Framework.Agents;

/// <summary>
/// Declarative specification of an <c>AITool</c> that can be invoked by Microsoft Agent Framework (<c>Microsoft.Agents.AI</c>)
/// agents. Can be defined in <c>Agents/Tools/*.json</c>, <c>appsettings.json</c>, or via <see cref="MiddtAgentOptions"/>.
/// Supports direct execution of any registered <see cref="Middt.Framework.Workflow.Tasks.IWorkflowTask"/> via <see cref="Task"/>
/// or <c>kind: "task"</c>.
/// </summary>
public sealed class AgentToolDefinition
{
    /// <summary>Unique function name exposed to the agent (e.g. <c>evaluate_dispute_policy</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description telling the AI model when and how to invoke this tool.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Execution kind:
    /// <list type="bullet">
    ///   <item><description><c>expression</c> (default when <see cref="Task"/> is not set): Evaluates <see cref="Expression"/> or renders <see cref="Output"/> with <c>args</c> and workflow <c>data</c> in scope.</description></item>
    ///   <item><description><c>task</c> (or any registered workflow task name): Executes a registered <see cref="Middt.Framework.Workflow.Tasks.IWorkflowTask"/> from <see cref="Middt.Framework.Workflow.Definitions.WorkflowTaskRegistry"/> (<c>set</c>, <c>http</c>, <c>cache</c>, <c>operation</c>, <c>publish</c>, <c>rabbitmq</c>, <c>loadWorkflow</c>, <c>startWorkflow</c>, or custom tasks).</description></item>
    ///   <item><description><c>http</c>: Executes an HTTP request through the workflow <see cref="Middt.Framework.Workflow.Tasks.HttpTask"/> pipeline (<see cref="Method"/>, <see cref="Url"/>, <see cref="QueryParams"/>, <see cref="Headers"/>, <see cref="Body"/>) templated with <c>{{ args.* }}</c> / <c>{{ data.* }}</c>.</description></item>
    ///   <item><description><c>mcp</c>: Calls a tool on a configured Model Context Protocol (MCP) server (<see cref="McpServer"/>, <see cref="McpTool"/>).</description></item>
    /// </list>
    /// </summary>
    public string Kind { get; set; } = "expression";

    /// <summary>Optional JSON Schema describing the tool's input parameters.</summary>
    public JsonObject? Parameters { get; set; }

    /// <summary>
    /// For <c>kind: "task"</c>: name of the workflow task in <see cref="Middt.Framework.Workflow.Definitions.WorkflowTaskRegistry"/>
    /// (e.g. <c>set</c>, <c>http</c>, <c>cache</c>, <c>operation</c>, <c>publish</c>, <c>rabbitmq</c>, <c>loadWorkflow</c>, <c>startWorkflow</c>).
    /// When set, the tool automatically executes through the workflow task pipeline even if <see cref="Kind"/> is omitted.
    /// </summary>
    public string? Task { get; set; }

    /// <summary>
    /// For <c>kind: "task"</c> (or <c>kind: "http"</c>): configuration object passed as <c>TaskDefinition.Config</c> to the
    /// <see cref="Middt.Framework.Workflow.Tasks.IWorkflowTask"/>. Supports <c>{{ args.* }}</c>, <c>{{ input.* }}</c>, and <c>{{ data.* }}</c> templates.
    /// </summary>
    public JsonObject? Config { get; set; }

    /// <summary>
    /// For <c>kind: "task"</c>: path in the task's <c>Data</c> object from which the tool output is read and returned to the AI model
    /// (defaults to <c>Config["into"]</c> or <c>__toolResult</c>).
    /// </summary>
    public string? Into { get; set; }

    /// <summary>
    /// When <c>true</c> and the tool is invoked by an agent running inside a workflow (<see cref="AiAgentTask"/>),
    /// any fields written to <c>Data</c> by the underlying <see cref="Middt.Framework.Workflow.Tasks.IWorkflowTask"/>
    /// are also merged back into the active workflow instance's <c>Data</c>.
    /// </summary>
    public bool WriteToWorkflowData { get; set; }

    /// <summary>For <c>kind: "expression"</c>: single Middt expression evaluated with <c>args</c> and <c>data</c> in scope.</summary>
    public string? Expression { get; set; }

    /// <summary>
    /// For <c>kind: "expression"</c>: structured JSON template whose <c>{{ }}</c> placeholders are evaluated with <c>args</c> and <c>data</c> in scope.
    /// </summary>
    public JsonNode? Output { get; set; }

    /// <summary>For <c>kind: "http"</c>: HTTP method (<c>GET</c>, <c>POST</c>, etc.; defaults to <c>POST</c> when <see cref="Body"/> is set, otherwise <c>GET</c>).</summary>
    public string? Method { get; set; }

    /// <summary>For <c>kind: "http"</c>: Target URL template (supports <c>{{ args.* }}</c> and <c>{{ data.* }}</c>).</summary>
    public string? Url { get; set; }

    /// <summary>For <c>kind: "http"</c>: Optional query string parameters template.</summary>
    public JsonObject? QueryParams { get; set; }

    /// <summary>For <c>kind: "http"</c>: Optional HTTP headers object template.</summary>
    public JsonObject? Headers { get; set; }

    /// <summary>For <c>kind: "http"</c>: Optional request body template.</summary>
    public JsonNode? Body { get; set; }

    /// <summary>For <c>kind: "mcp"</c>: Name of the target MCP server.</summary>
    public string? McpServer { get; set; }

    /// <summary>For <c>kind: "mcp"</c>: Name of the tool on the MCP server (defaults to <see cref="Name"/>).</summary>
    public string? McpTool { get; set; }

    /// <summary>Optional roles required to invoke this tool. When non-empty, only callers in at least one listed role (or <c>ui-admin</c> / <c>system</c>) may execute the tool.</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>
/// Configuration of a Model Context Protocol (MCP) server (remote HTTP JSON-RPC 2.0 endpoint or in-process declarative server).
/// </summary>
public sealed class McpServerDefinition
{
    /// <summary>Unique identifier of the MCP server (e.g. <c>banking-mcp</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description of the MCP server.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional HTTP JSON-RPC 2.0 endpoint URI for remote MCP servers.
    /// When <c>null</c> or empty (or when <c>UseLocalFallback</c> is active and the endpoint is unreachable),
    /// the server's declarative <see cref="Tools"/>, <see cref="Resources"/>, and <see cref="Prompts"/> are served in-process.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Optional static HTTP headers (such as <c>Authorization</c> or <c>X-API-Key</c>) sent to remote MCP servers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Declarative MCP tools hosted or mirrored by this server.</summary>
    public List<AgentToolDefinition> Tools { get; set; } = [];

    /// <summary>Declarative MCP resources (<c>resources/list</c>, <c>resources/read</c>) hosted by this server.</summary>
    public List<McpResourceDefinition> Resources { get; set; } = [];

    /// <summary>Declarative MCP prompt templates (<c>prompts/get</c>) hosted by this server.</summary>
    public List<McpPromptDefinition> Prompts { get; set; } = [];
}

/// <summary>Declarative Model Context Protocol (MCP) resource definition.</summary>
public sealed class McpResourceDefinition
{
    public string Uri { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string MimeType { get; set; } = "application/json";

    /// <summary>Static or templated JSON content returned when the resource is read.</summary>
    public JsonNode? Content { get; set; }
}

/// <summary>Declarative Model Context Protocol (MCP) prompt template definition.</summary>
public sealed class McpPromptDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Prompt message template supporting <c>{{ args.* }}</c> placeholders.</summary>
    public string Template { get; set; } = string.Empty;
}

/// <summary>Catalog file model capable of loading agents, declarative tools, and MCP servers from a single JSON file.</summary>
public sealed class AgentCatalogFile
{
    public List<AgentDefinition> Agents { get; set; } = [];

    public List<AgentToolDefinition> Tools { get; set; } = [];

    public List<McpServerDefinition> McpServers { get; set; } = [];
}
