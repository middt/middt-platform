using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;

namespace Middt.Framework.Agents;

/// <summary>
/// Built-in <see cref="AIFunction"/> tools that allow Microsoft Agent Framework (<c>Microsoft.Agents.AI</c>) agents
/// to evaluate Middt portable expressions, inspect workflow definitions, and query workflow instance state.
/// </summary>
public static class MiddtWorkflowAgentTools
{
    public const string EvaluateExpressionToolName = "workflow_evaluate_expression";
    public const string ListDefinitionsToolName = "workflow_list_definitions";
    public const string GetInstanceToolName = "workflow_get_instance";

    /// <summary>Creates the standard set of Middt workflow <see cref="AIFunction"/> tools.</summary>
    public static IReadOnlyList<AIFunction> CreateDefaultTools(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return
        [
            AIFunctionFactory.Create(
                ([Description("Middt portable expression to evaluate (e.g. 'data.total - data.paidAmount').")] string expression,
                 [Description("Optional JSON object representing 'data' in the expression scope.")] string? dataJson = null) =>
                    EvaluateExpression(expression, dataJson),
                EvaluateExpressionToolName,
                "Evaluates a Middt portable expression against a JSON data object and returns the computed result."),

            AIFunctionFactory.Create(
                () => ListWorkflowDefinitions(services),
                ListDefinitionsToolName,
                "Lists all registered Middt workflow definitions, including their keys, initial states, and state names."),

            AIFunctionFactory.Create(
                async ([Description("Workflow instance GUID.")] Guid instanceId,
                       CancellationToken cancellationToken) =>
                    await GetWorkflowInstanceAsync(services, instanceId, cancellationToken),
                GetInstanceToolName,
                "Gets the current state, status, business key, and JSON data of a workflow instance by instance ID."),
        ];
    }

    private static string EvaluateExpression(string expression, string? dataJson)
    {
        var data = !string.IsNullOrWhiteSpace(dataJson) && JsonNode.Parse(dataJson) is JsonObject parsed
            ? parsed
            : [];

        var engine = new WorkflowExpressionEngine();
        var scope = new ExpressionScope
        {
            Data = data,
            Now = DateTime.UtcNow,
        };
        var node = engine.EvaluateToNode(expression, scope);
        return node?.ToJsonString() ?? "null";
    }

    private static string ListWorkflowDefinitions(IServiceProvider services)
    {
        var registry = services.GetService<WorkflowRegistry>();
        if (registry is null)
        {
            return "[]";
        }

        var result = new JsonArray();
        foreach (var def in registry.Latest)
        {
            result.Add(new JsonObject
            {
                ["key"] = def.Key,
                ["version"] = def.Version,
                ["initialState"] = def.Start.Target,
                ["states"] = new JsonArray([.. def.States.Select(s => (JsonNode?)JsonValue.Create(s.Key))]),
            });
        }

        return result.ToJsonString();
    }

    private static async Task<string> GetWorkflowInstanceAsync(
        IServiceProvider services,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var engine = scope.ServiceProvider.GetService<WorkflowEngine>();
        if (engine is null)
        {
            return new JsonObject { ["error"] = "WorkflowEngine is not registered." }.ToJsonString();
        }

        try
        {
            var systemUser = new WorkflowUser { Name = "agent", IsSystem = true };
            var instance = await engine.GetAsync(instanceId, systemUser, cancellationToken);
            return new JsonObject
            {
                ["found"] = true,
                ["id"] = instance.Id.ToString(),
                ["workflowKey"] = instance.Workflow,
                ["key"] = instance.Key,
                ["state"] = instance.State,
                ["status"] = instance.Status.ToString(),
                ["data"] = instance.Data.DeepClone(),
            }.ToJsonString();
        }
        catch (WorkflowNotFoundException)
        {
            return new JsonObject { ["found"] = false, ["instanceId"] = instanceId.ToString() }.ToJsonString();
        }
    }
}
