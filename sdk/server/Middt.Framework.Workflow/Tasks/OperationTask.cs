using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Diagnostics;
using Middt.Framework.Workflow.Expressions;

namespace Middt.Framework.Workflow.Tasks;

/// <summary>Data passed to an application operation without exposing workflow engine internals.</summary>
public sealed class WorkflowOperationContext
{
    public required JsonObject Data { get; init; }

    /// <summary>The current transition input (or rendered task <c>input</c>/<c>args</c>), including sensitive fields that are never persisted.</summary>
    public JsonObject? Input { get; init; }

    /// <summary>Rendered task-level <c>input</c> / <c>args</c> / <c>payload</c> object when specified in task config.</summary>
    public JsonObject? Args { get; init; }

    public required JsonObject Config { get; init; }

    /// <summary>Stable within the attempts of one workflow command.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Optional result produced by the operation, which the engine writes/projects via <c>into</c>, <c>select</c>, or <c>output</c>.</summary>
    public JsonNode? Result { get; set; }
}

/// <summary>Explicitly dispatches approved application operations invoked by the general <c>operation</c> task.</summary>
public interface IWorkflowOperationDispatcher
{
    bool CanHandle(string operation);

    Task ExecuteAsync(string operation, WorkflowOperationContext context, CancellationToken cancellationToken);
}

/// <summary>Invokes a registered application operation by name. The dispatcher owns its allowlist and domain logic.</summary>
public sealed class OperationTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["operation"] is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
        {
            yield return "operation: 'operation' is required.";
        }

        var inputTemplate = task.Config["input"] ?? task.Config["args"] ?? task.Config["payload"] ?? task.Config["data"];
        foreach (var error in expressions.ValidateTemplate(inputTemplate)
                     .Concat(WorkflowOutputProjection.ValidateSelectOrOutput(task.Config, expressions, "operation")))
        {
            yield return error.StartsWith("operation:", StringComparison.Ordinal) ? error : "operation: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var name = context.Config["operation"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The operation name is missing.");
        var dispatcher = context.Services.GetServices<IWorkflowOperationDispatcher>().SingleOrDefault(d => d.CanHandle(name))
            ?? throw new InvalidOperationException($"No dispatcher is registered for operation '{name}'.");

        using var activity = WorkflowDiagnostics.ActivitySource.StartActivity($"workflow.operation {name}");
        activity?.SetTag("middt.workflow.operation", name);
        activity?.SetTag("middt.workflow.dispatcher", dispatcher.GetType().Name);

        var inputTemplate = context.Config["input"] ?? context.Config["args"] ?? context.Config["payload"] ?? context.Config["data"];
        var renderedArgs = context.Expressions.Render(inputTemplate, context.Scope) as JsonObject;

        var opContext = new WorkflowOperationContext
        {
            Data = context.Data,
            Input = renderedArgs ?? context.Scope.Input,
            Args = renderedArgs,
            Config = context.Config,
            IdempotencyKey = context.IdempotencyKey,
        };

        await dispatcher.ExecuteAsync(name, opContext, cancellationToken);

        if (opContext.Result is not null)
        {
            var sourcePayload = opContext.Result as JsonObject
                                ?? new JsonObject { ["value"] = opContext.Result.DeepClone() };
            WorkflowOutputProjection.ApplyToContextData(context, sourcePayload, opContext.Result);
        }
    }
}
