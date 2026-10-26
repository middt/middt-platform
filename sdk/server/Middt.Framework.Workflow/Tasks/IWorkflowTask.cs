using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;

namespace Middt.Framework.Workflow.Tasks;

/// <summary>
/// A unit of work run on state entry/exit or transition execution. Modify <see cref="WorkflowTaskContext.Data"/> to change
/// instance data; throw <see cref="Runtime.WorkflowTaskException"/> (or any exception) to fail and trigger the error policy.
/// Tasks may run more than once (retries, retry endpoint), so keep side effects idempotent.
/// </summary>
public interface IWorkflowTask
{
    /// <summary>Validates the task's configuration when definitions are loaded; return error messages.</summary>
    IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions) => [];

    Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken);
}

public sealed class WorkflowTaskContext
{
    public required TaskDefinition Task { get; init; }

    /// <summary>Mutable instance data.</summary>
    public required JsonObject Data { get; init; }

    public required ExpressionScope Scope { get; init; }

    public required WorkflowExpressionEngine Expressions { get; init; }

    public required Guid InstanceId { get; init; }

    /// <summary>Stable across attempts of this task execution; send it to external systems that support idempotency.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    public required string Workflow { get; init; }

    public required string State { get; init; }

    /// <summary>Key of the transition currently being executed, if any.</summary>
    public string? Transition { get; init; }

    public required IServiceProvider Services { get; init; }

    public required ILogger Logger { get; init; }

    public JsonObject Config => Task.Config;

    /// <summary>Renders a config value (resolving <c>{{ expression }}</c> placeholders against the current data).</summary>
    public JsonNode? Render(string configKey) => Expressions.Render(Config[configKey], Scope);

    public string? RenderText(string configKey) =>
        Config[configKey]?.GetValue<string>() is { } text ? Expressions.RenderText(text, Scope) : null;

    /// <summary>Business key of the instance when the task started (<c>null</c> when it has none).</summary>
    public string? BusinessKey { get; init; }

    /// <summary>Sets the instance's business key (wired by the engine, see the <c>setKey</c> task).</summary>
    public Func<string, CancellationToken, Task>? SetBusinessKey { get; init; }

    /// <summary>Starts another workflow (wired by the engine).</summary>
    public Func<string, JsonObject, string?, CancellationToken, Task<Guid>>? StartWorkflow { get; init; }

    /// <summary>Selects the outgoing transition key to be taken automatically after the current state's entry tasks finish.</summary>
    public Action<string>? SelectTransition { get; init; }

    /// <summary>Records an AI agent or MCP task execution into the active workflow run scope.</summary>
    public Action<WorkflowAgentRunModel>? RecordAgentRun { get; init; }
}

/// <summary>Publishes workflow messages (<c>publish</c> task); register an implementation, e.g. on Rebus.</summary>
public interface IWorkflowMessagePublisher
{
    Task PublishAsync(string messageType, JsonObject payload, CancellationToken cancellationToken);
}

/// <summary>
/// Shared validation and projection helper for <c>into</c>, <c>select</c>, and <c>output</c> across workflow tasks.
/// </summary>
public static class WorkflowOutputProjection
{
    public static bool HasSelectOrOutput(JsonObject config) =>
        config["select"] is not null || config["output"] is not null;

    public static JsonNode? GetSelectOrOutput(JsonObject config) =>
        config["select"] ?? config["output"];

    public static IEnumerable<string> ValidateSelectOrOutput(
        JsonObject config,
        WorkflowExpressionEngine expressions,
        string taskName)
    {
        var selectNode = GetSelectOrOutput(config);
        if (selectNode is null)
        {
            yield break;
        }

        var propName = config["select"] is not null ? "select" : "output";
        if (selectNode is JsonArray selectArr)
        {
            foreach (var item in selectArr)
            {
                if (item?.GetValue<string>() is not { Length: > 0 })
                {
                    yield return $"{taskName}: '{propName}' array items must be non-empty field path strings.";
                }
            }
        }
        else if (selectNode is JsonObject selectObj)
        {
            foreach (var (targetKey, exprNode) in selectObj)
            {
                if (exprNode?.GetValue<string>() is not { Length: > 0 } expr)
                {
                    yield return $"{taskName}: '{propName}[\"{targetKey}\"]' must be a field path or expression string.";
                }
                else if (!IsSimpleFieldPath(expr) && expressions.Validate(expr) is { } exprErr)
                {
                    yield return $"{taskName}: '{propName}[\"{targetKey}\"]': {exprErr}";
                }
            }
        }
        else
        {
            yield return $"{taskName}: '{propName}' must be an array of field paths or an object mapping target keys to expressions/field paths.";
        }
    }

    public static void ApplyToContextData(
        WorkflowTaskContext context,
        JsonObject sourcePayload,
        JsonNode? rawResult = null,
        string? intoOverride = null)
    {
        var into = intoOverride ?? context.Config["into"]?.GetValue<string>();
        var selectNode = GetSelectOrOutput(context.Config);

        if (selectNode is null)
        {
            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, (rawResult ?? sourcePayload).DeepClone());
            }

            return;
        }

        var projected = new JsonObject();
        var writeToInto = !string.IsNullOrEmpty(into);
        var destination = writeToInto ? projected : context.Data;

        if (selectNode is JsonArray fieldList)
        {
            foreach (var item in fieldList)
            {
                if (item?.GetValue<string>() is { Length: > 0 } fieldPath)
                {
                    var val = JsonPath.Get(sourcePayload, fieldPath);
                    JsonPath.Set(destination, fieldPath, val?.DeepClone());
                }
            }
        }
        else if (selectNode is JsonObject mapObj)
        {
            var evalScope = new ExpressionScope
            {
                Data = context.Scope.Data,
                Input = context.Scope.Input,
                Args = context.Scope.Args,
                Result = sourcePayload,
                Child = context.Scope.Child,
                User = context.Scope.User,
                Instance = context.Scope.Instance,
                Item = sourcePayload,
                Target = sourcePayload,
                Index = context.Scope.Index,
                Now = context.Scope.Now,
            };

            foreach (var (targetKey, sourceExprNode) in mapObj)
            {
                if (sourceExprNode?.GetValue<string>() is not { Length: > 0 } sourceExpr)
                {
                    continue;
                }

                var val = JsonPath.Get(sourcePayload, sourceExpr)?.DeepClone()
                          ?? context.Expressions.EvaluateToNode(sourceExpr, evalScope);
                JsonPath.Set(destination, targetKey, val);
            }
        }

        if (writeToInto)
        {
            JsonPath.Set(context.Data, into!, projected);
        }
    }

    public static bool IsSimpleFieldPath(string expr) =>
        expr.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')
        && !expr.StartsWith("data.", StringComparison.Ordinal)
        && !expr.StartsWith("input.", StringComparison.Ordinal)
        && !expr.StartsWith("args.", StringComparison.Ordinal)
        && !expr.StartsWith("target.", StringComparison.Ordinal)
        && !expr.StartsWith("item.", StringComparison.Ordinal)
        && !expr.StartsWith("result.", StringComparison.Ordinal)
        && !expr.StartsWith("child.", StringComparison.Ordinal)
        && !expr.StartsWith("user.", StringComparison.Ordinal)
        && !expr.StartsWith("instance.", StringComparison.Ordinal)
        && !expr.StartsWith("now.", StringComparison.Ordinal);
}
