using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Agents;

/// <summary>
/// <c>mcp</c> (also registered as <c>mcp-call</c>, <c>mcpCall</c>, <c>mcp-tool</c>, <c>mcpTool</c>,
/// <c>mcp-resource</c>, <c>mcpResource</c>, <c>mcp-prompt</c>, <c>mcpPrompt</c>):
/// executes Model Context Protocol (MCP) operations (<c>callTool</c>, <c>listTools</c>, <c>readResource</c>,
/// <c>listResources</c>, <c>getPrompt</c>) against a registered MCP server.
/// <code>
/// {
///   "task": "mcp",
///   "config": {
///     "server": "banking-mcp",
///     "action": "callTool",
///     "tool": "get_transaction_telemetry",
///     "arguments": {
///       "transactionRef": "{{ data.transactionRef }}",
///       "amount": "{{ data.amount }}",
///       "category": "{{ data.category }}"
///     },
///     "into": "mcpTelemetry"
///   }
/// }
/// </code>
/// </summary>
public sealed class McpTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        var config = task.Config;
        if (config["server"] is null)
        {
            yield return "mcp: 'server' is required.";
        }

        var action = DetermineAction(task);
        switch (action)
        {
            case "calltool" or "call" or "tool":
                if (config["tool"] is null && config["name"] is null)
                {
                    yield return "mcp: 'tool' (or 'name') is required for tool call actions.";
                }

                break;

            case "readresource" or "resource":
                if (config["uri"] is null && config["resource"] is null)
                {
                    yield return "mcp: 'uri' (or 'resource') is required for resource read actions.";
                }

                break;

            case "getprompt" or "prompt":
                if (config["prompt"] is null && config["name"] is null)
                {
                    yield return "mcp: 'prompt' (or 'name') is required for prompt actions.";
                }

                break;

            case "listtools" or "listresources":
                break;

            default:
                yield return $"mcp: unsupported action '{action}'. Use 'callTool', 'listTools', 'readResource', 'listResources', or 'getPrompt'.";
                break;
        }

        foreach (var error in expressions.ValidateTemplate(config["server"])
                     .Concat(expressions.ValidateTemplate(config["action"]))
                     .Concat(expressions.ValidateTemplate(config["op"]))
                     .Concat(expressions.ValidateTemplate(config["tool"]))
                     .Concat(expressions.ValidateTemplate(config["name"]))
                     .Concat(expressions.ValidateTemplate(config["uri"]))
                     .Concat(expressions.ValidateTemplate(config["resource"]))
                     .Concat(expressions.ValidateTemplate(config["prompt"]))
                     .Concat(expressions.ValidateTemplate(config["arguments"]))
                     .Concat(expressions.ValidateTemplate(config["args"])))
        {
            yield return "mcp: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        using var _ = WorkflowTaskAmbientContext.Enter(context);
        var mcpClient = context.Services.GetService<IMiddtMcpClient>()
            ?? throw new WorkflowTaskException("mcp task requires IMiddtMcpClient (call services.AddMiddtAgents(...)).");

        var config = context.Config;
        var server = context.RenderText("server")
            ?? throw new WorkflowTaskException("mcp: 'server' resolved to empty.");
        var action = DetermineAction(context);
        var argsNode = (context.Render("arguments") ?? context.Render("args")) as JsonObject ?? [];

        JsonNode? output;
        try
        {
            switch (action)
            {
                case "calltool" or "call" or "tool":
                    var toolName = context.RenderText("tool") ?? context.RenderText("name")
                        ?? throw new WorkflowTaskException("mcp: 'tool' resolved to empty.");
                    output = await mcpClient.CallToolAsync(server, toolName, argsNode, cancellationToken);
                    break;

                case "listtools":
                    output = await mcpClient.ListToolsAsync(server, cancellationToken);
                    break;

                case "readresource" or "resource":
                    var uri = context.RenderText("uri") ?? context.RenderText("resource")
                        ?? throw new WorkflowTaskException("mcp: 'uri' resolved to empty.");
                    output = await mcpClient.ReadResourceAsync(server, uri, argsNode, cancellationToken);
                    break;

                case "listresources":
                    output = await mcpClient.ListResourcesAsync(server, cancellationToken);
                    break;

                case "getprompt" or "prompt":
                    var promptName = context.RenderText("prompt") ?? context.RenderText("name")
                        ?? throw new WorkflowTaskException("mcp: 'prompt' resolved to empty.");
                    var promptText = await mcpClient.GetPromptAsync(server, promptName, argsNode, cancellationToken);
                    output = JsonValue.Create(promptText);
                    break;

                default:
                    throw new WorkflowTaskException($"mcp: unsupported action '{action}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowTaskException)
        {
            throw new WorkflowTaskException($"mcp '{server}' ({action}) failed: {ex.Message}", ex);
        }

        var meta = new JsonObject
        {
            ["server"] = server,
            ["action"] = action,
            ["tool"] = context.RenderText("tool") ?? context.RenderText("name"),
            ["uri"] = context.RenderText("uri") ?? context.RenderText("resource"),
            ["executedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        if (config["into"]?.GetValue<string>() is { Length: > 0 } into)
        {
            if (output is JsonObject outputObj)
            {
                outputObj["meta"] ??= meta.DeepClone();
            }

            JsonPath.Set(context.Data, into, output);
        }

        if (config["metaInto"]?.GetValue<string>() is { Length: > 0 } metaInto)
        {
            JsonPath.Set(context.Data, metaInto, meta);
        }
    }

    private static string DetermineAction(TaskDefinition task) =>
        ResolveActionName(task.Config["action"]?.GetValue<string>() ?? task.Config["op"]?.GetValue<string>(), task.Task);

    private static string DetermineAction(WorkflowTaskContext context) =>
        ResolveActionName(context.RenderText("action") ?? context.RenderText("op"), context.Task.Task);

    private static string ResolveActionName(string? configured, string taskName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant();
        }

        return taskName.ToLowerInvariant() switch
        {
            "mcp-resource" or "mcpresource" => "readresource",
            "mcp-prompt" or "mcpprompt" => "getprompt",
            _ => "calltool",
        };
    }
}
