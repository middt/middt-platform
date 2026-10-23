using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Middt.Framework.Model.Workflow;

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowInstanceStatus>))]
public enum WorkflowInstanceStatus
{
    /// <summary>Waiting for the next transition (or for a subflow).</summary>
    Active,

    /// <summary>A transition is executing; further transitions are rejected with 409.</summary>
    Busy,

    Completed,

    /// <summary>A task failed; see incidents and use retry.</summary>
    Faulted,

    Cancelled,
}

public sealed class StartWorkflowRequest
{
    /// <summary>Optional business key (unique per workflow); starting twice with the same key returns the existing instance.</summary>
    public string? Key { get; set; }

    /// <summary>Validated against the start transition's input schema.</summary>
    public JsonObject? Data { get; set; }
}

public sealed class WorkflowSummaryModel
{
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string? Title { get; set; }
    public string? Icon { get; set; }
}

public sealed class WorkflowStateModel
{
    public string Key { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Type { get; set; } = string.Empty;
    public string SubType { get; set; } = string.Empty;

    /// <summary>Name of the registered view component for this state (server driven UI), if any.</summary>
    public string? View { get; set; }

    /// <summary>Key of the JSON layout (server driven UI) shown for this state, if any.</summary>
    public string? Layout { get; set; }

    /// <summary>Wizard step number (progress indicator), if the state is a step.</summary>
    public int? Step { get; set; }
}

public sealed class WorkflowTransitionModel
{
    public string Key { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? From { get; set; }
    public string Target { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string? Style { get; set; }
    public string? Icon { get; set; }
}

/// <summary>Transition option exposed to AI agents for dynamic state routing, including its key, title, description, and target state.</summary>
public sealed class WorkflowTransitionOptionModel
{
    public string Key { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Target { get; set; }
}

/// <summary>States and transitions of a workflow (e.g. for diagrams).</summary>
public sealed class WorkflowDefinitionModel
{
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string? Title { get; set; }
    public string? Icon { get; set; }
    public string StartState { get; set; } = string.Empty;
    public JsonObject? StartInputSchema { get; set; }
    public JsonObject? DataSchema { get; set; }
    public List<WorkflowStateModel> States { get; set; } = [];
    public List<WorkflowTransitionModel> Transitions { get; set; } = [];
}

/// <summary>A manual transition the caller may execute in the instance's current state.</summary>
public sealed class AvailableTransitionModel
{
    public string Key { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Target { get; set; } = string.Empty;

    /// <summary>Optional button style (<c>primary</c>, <c>secondary</c>, <c>success</c>, <c>warning</c>, <c>danger</c>, <c>light</c>).</summary>
    public string? Style { get; set; }

    /// <summary>Optional Material icon name for transition buttons.</summary>
    public string? Icon { get; set; }

    /// <summary>Whether the transition is shown as a button in the top instance toolbar (<c>true</c> by default).</summary>
    public bool Toolbar { get; set; } = true;

    /// <summary>JSON Schema of the expected request body, when the transition takes input.</summary>
    public JsonObject? InputSchema { get; set; }

    /// <summary>Name of a registered input form component that replaces the schema generated form, if any.</summary>
    public string? View { get; set; }

    /// <summary>Render the input form inside the instance screen (wizard step) instead of a dialog.</summary>
    public bool Inline { get; set; }

    /// <summary>Data path the input is stored under (lets UIs pre-fill the form when a step is revisited).</summary>
    public string? InputInto { get; set; }

    /// <summary>
    /// <c>jsonPatch</c> when the transition expects an RFC 6902 JSON Patch on the data at <see cref="InputInto"/> (body
    /// <c>[ {op, path, value} ]</c> or <c>{ "patch": [...] }</c>) instead of an object; <c>null</c> for a plain object body.
    /// UIs pre-fill the form with that data and send the difference.
    /// </summary>
    public string? InputFormat { get; set; }
}

public sealed class ScheduledTransitionModel
{
    public string Key { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
}

public sealed class WorkflowInstanceModel
{
    public Guid Id { get; set; }
    public string? Key { get; set; }
    public string Workflow { get; set; } = string.Empty;
    public int WorkflowVersion { get; set; }
    public string State { get; set; } = string.Empty;
    public string? StateTitle { get; set; }
    public string StateType { get; set; } = string.Empty;
    public string StateSubType { get; set; } = string.Empty;

    /// <summary>Name of the view component of the current state (server driven UI), if any.</summary>
    public string? View { get; set; }

    /// <summary>Key of the JSON layout of the current state (rendered with <c>data</c> and <c>instance</c>), if any.</summary>
    public string? Layout { get; set; }

    public WorkflowInstanceStatus Status { get; set; }
    public JsonObject Data { get; set; } = [];
    public int DataVersion { get; set; }
    public Guid? ParentInstanceId { get; set; }
    public Guid? WaitingForInstanceId { get; set; }
    public string? Error { get; set; }

    /// <summary>Key of the failed transition (or <c>$background</c>) when <see cref="Status"/> is <see cref="WorkflowInstanceStatus.Faulted"/>.</summary>
    public string? PendingTransition { get; set; }

    /// <summary>Input payload of the failed transition (or current data when retrying a non-input step) when <see cref="Status"/> is <see cref="WorkflowInstanceStatus.Faulted"/>.</summary>
    public JsonObject? PendingInput { get; set; }

    /// <summary>JSON Schema for editing <see cref="PendingInput"/> before retrying a <see cref="WorkflowInstanceStatus.Faulted"/> instance.</summary>
    public JsonObject? PendingInputSchema { get; set; }

    public string? StartedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<AvailableTransitionModel> AvailableTransitions { get; set; } = [];
    public List<ScheduledTransitionModel> ScheduledTransitions { get; set; } = [];

    /// <summary>Latest agent/MCP execution state indexed by agent name and task key (populated from <c>WorkflowAgentRuns</c>).</summary>
    public JsonObject Agents { get; set; } = [];

    /// <summary>Chronological execution records of all AI agents and MCP tasks for this instance.</summary>
    public List<WorkflowAgentRunModel> AgentRuns { get; set; } = [];

    /// <summary>Chronological tool call records across all AI agent and MCP executions for this instance.</summary>
    public List<WorkflowAgentToolCallModel> ToolCalls { get; set; } = [];

    /// <summary>Projected state and available transitions of the active child subflow when <see cref="WaitingForInstanceId"/> is set.</summary>
    public WorkflowInstanceModel? ActiveSubFlow { get; set; }
}

/// <summary>Execution record of an AI agent or MCP task persisted in <c>WorkflowAgentRuns</c>.</summary>
public sealed class WorkflowAgentRunModel
{
    public Guid Id { get; set; }
    public string State { get; set; } = string.Empty;
    public string? TransitionKey { get; set; }
    public string TaskKey { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string? SelectedTransition { get; set; }
    public List<WorkflowTransitionOptionModel> AllowedTransitions { get; set; } = [];
    public string? ResponseId { get; set; }
    public string? FinishReason { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public int ToolCallCount { get; set; }
    public JsonNode? Output { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<WorkflowAgentToolCallModel> ToolCalls { get; set; } = [];
}

/// <summary>Execution record of a single tool invocation persisted in <c>WorkflowAgentToolCalls</c>.</summary>
public sealed class WorkflowAgentToolCallModel
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string TaskKey { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JsonNode? Arguments { get; set; }
    public JsonNode? Result { get; set; }
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Execution record of a single task within a transition.</summary>
public sealed class WorkflowTaskExecutionModel
{
    public string TaskKey { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public string Hook { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public long DurationMs { get; set; }
    public string Outcome { get; set; } = string.Empty;
}

public sealed class WorkflowHistoryModel
{
    public string TransitionKey { get; set; } = string.Empty;
    public string? FromState { get; set; }
    public string ToState { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string? ExecutedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int DataVersion { get; set; }
    public List<WorkflowTaskExecutionModel> Tasks { get; set; } = [];
}

public sealed class WorkflowIncidentModel
{
    public long Id { get; set; }
    public string? State { get; set; }
    public string? TransitionKey { get; set; }
    public string? TaskKey { get; set; }
    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Parameter specification for a built-in or registered workflow task in the visual designer.</summary>
public sealed class WorkflowTaskParamSpecModel
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public List<string> Options { get; set; } = [];
    public string Description { get; set; } = string.Empty;
}

/// <summary>Catalog specification of a workflow task exposed to the visual workflow designer (<c>GET /api/workflows/tasks</c>).</summary>
public sealed class WorkflowTaskSpecModel
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = "bolt";
    public string Description { get; set; } = string.Empty;
    public JsonObject Template { get; set; } = [];
    public List<WorkflowTaskParamSpecModel> Params { get; set; } = [];
}

/// <summary>Result of validating a draft workflow definition (<c>POST /api/workflows/validate</c>).</summary>
public sealed class WorkflowValidationResultModel
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = [];
}
