using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;
using Middt.Framework.Workflow.Schema;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Agents;

/// <summary>
/// <c>agent</c> (also registered as <c>ai-agent</c>, <c>aiAgent</c>, <c>ai</c>, <c>llm</c>):
/// executes single-agent or multi-agent (<c>sequential</c>, <c>concurrent</c>, <c>handoff</c>) workflows using the
/// Microsoft Agent Framework (<c>Microsoft.Agents.AI</c> and <c>Microsoft.Agents.AI.Workflows</c>), with support for
/// declarative/MCP tools, structured JSON output validation, multi-turn sessions, and AI-driven workflow routing
/// (<c>allowedTransitions</c> / <c>transitionInto</c>).
/// </summary>
public sealed class AiAgentTask : IWorkflowTask
{
    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        var config = task.Config;
        var mode = (config["mode"]?.GetValue<string>() ?? "single").Trim().ToLowerInvariant();

        if (mode is not ("single" or "sequential" or "concurrent" or "parallel" or "handoff"))
        {
            yield return $"agent: unsupported mode '{mode}'. Use 'single', 'sequential', 'concurrent', or 'handoff'.";
        }

        if (mode is "sequential" or "concurrent" or "parallel" or "handoff")
        {
            if (config["agents"] is not JsonArray { Count: > 0 })
            {
                yield return $"agent: 'agents' must be a non-empty array when mode is '{mode}'.";
            }
        }

        var hasPrompt = config["prompt"] is not null
                        || config["input"] is not null
                        || config["message"] is not null
                        || config["messages"] is JsonArray { Count: > 0 };
        if (!hasPrompt)
        {
            yield return "agent: 'prompt' (or 'input', 'message', or non-empty 'messages') is required.";
        }

        if (config["outputSchema"] is not null and not JsonObject)
        {
            yield return "agent: 'outputSchema' must be a JSON Schema object.";
        }

        foreach (var error in expressions.ValidateTemplate(config["agent"])
                     .Concat(expressions.ValidateTemplate(config["instructions"]))
                     .Concat(expressions.ValidateTemplate(config["system"]))
                     .Concat(expressions.ValidateTemplate(config["prompt"]))
                     .Concat(expressions.ValidateTemplate(config["input"]))
                     .Concat(expressions.ValidateTemplate(config["message"]))
                     .Concat(expressions.ValidateTemplate(config["context"]))
                     .Concat(expressions.ValidateTemplate(config["messages"])))
        {
            yield return "agent: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var registry = context.Services.GetService<IMiddtAgentRegistry>()
            ?? throw new WorkflowTaskException("agent task requires IMiddtAgentRegistry (call services.AddMiddtAgents(...)).");

        var config = context.Config;
        var mode = (context.RenderText("mode") ?? "single").Trim().ToLowerInvariant();
        var agentName = context.RenderText("agent") ?? context.RenderText("name");
        var instructions = context.RenderText("instructions") ?? context.RenderText("system");
        var modelOverride = context.RenderText("model") ?? context.RenderText("modelId");
        var toolsList = ExtractStringList(config["tools"]);
        var mcpServersList = ExtractStringList(config["mcpServers"]);
        var allowedTransitions = ExtractStringList(config["allowedTransitions"]);
        var inlineOutputSchema = config["outputSchema"] as JsonObject;

        AIAgent agent;
        JsonObject? effectiveOutputSchema = inlineOutputSchema;

        if (mode is "sequential" or "concurrent" or "parallel" or "handoff")
        {
            var participants = ResolveMultiAgents(config["agents"] as JsonArray, registry, context);
            var workflowName = !string.IsNullOrWhiteSpace(agentName) ? agentName : $"{context.Workflow}-{mode}";
            agent = registry.CreateWorkflowAgent(mode, participants, workflowName);
        }
        else if (!string.IsNullOrWhiteSpace(agentName) && registry.GetDefinition(agentName) is { } registeredDef)
        {
            effectiveOutputSchema ??= registeredDef.OutputSchema;
            if (allowedTransitions.Count == 0 && registeredDef.AllowedTransitions.Count > 0)
            {
                allowedTransitions.AddRange(registeredDef.AllowedTransitions);
            }

            var mergedTools = new List<string>(registeredDef.Tools);
            foreach (var t in toolsList.Where(t => !mergedTools.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                mergedTools.Add(t);
            }

            var mergedMcp = new List<string>(registeredDef.McpServers);
            foreach (var s in mcpServersList.Where(s => !mergedMcp.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                mergedMcp.Add(s);
            }

            agent = registry.CreateAgent(new AgentDefinition
            {
                Name = registeredDef.Name,
                Description = registeredDef.Description,
                Instructions = !string.IsNullOrWhiteSpace(instructions) ? instructions : registeredDef.Instructions,
                ModelId = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : registeredDef.ModelId,
                Temperature = registeredDef.Temperature,
                MaxOutputTokens = registeredDef.MaxOutputTokens,
                Tools = mergedTools,
                McpServers = mergedMcp,
                AllowedTransitions = allowedTransitions,
                OutputSchema = effectiveOutputSchema,
            });
        }
        else
        {
            var adHocDef = new AgentDefinition
            {
                Name = !string.IsNullOrWhiteSpace(agentName) ? agentName : $"{context.Workflow}-{context.State}-agent",
                Description = context.RenderText("description"),
                Instructions = instructions,
                ModelId = modelOverride,
                Temperature = config["temperature"]?.GetValue<float>(),
                MaxOutputTokens = config["maxOutputTokens"]?.GetValue<int>(),
                Tools = toolsList,
                McpServers = mcpServersList,
                AllowedTransitions = allowedTransitions,
                OutputSchema = effectiveOutputSchema,
            };
            agent = registry.CreateAgent(adHocDef);
        }

        var messages = BuildMessages(context, allowedTransitions);
        var session = await RestoreOrCreateSessionAsync(agent, context, cancellationToken);

        AgentRunOptions? runOptions = null;
        if (effectiveOutputSchema is not null && mode is "sequential" or "concurrent" or "parallel" or "handoff")
        {
            var chatOptions = new ChatOptions
            {
                ResponseFormat = MiddtAgentRegistry.CreateJsonSchemaResponseFormat(
                    agent.Name ?? "agent_output",
                    effectiveOutputSchema),
            };
            runOptions = new ChatClientAgentRunOptions(chatOptions);
        }

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(messages, session, runOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowTaskException($"agent '{agent.Name}' execution failed: {ex.Message}", ex);
        }

        var sessionInto = config["sessionInto"]?.GetValue<string>() ?? config["threadInto"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sessionInto))
        {
            var serializedSession = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
            if (serializedSession.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                JsonPath.Set(context.Data, sessionInto, JsonNode.Parse(serializedSession.GetRawText()));
            }
        }

        JsonNode? outputNode = null;
        if (config["into"]?.GetValue<string>() is { Length: > 0 } into)
        {
            outputNode = ParseAndValidateOutput(response.Text, effectiveOutputSchema, config, agent.Name ?? "agent");
            JsonPath.Set(context.Data, into, outputNode);
        }

        if (config["transitionInto"]?.GetValue<string>() is { Length: > 0 } transitionInto)
        {
            outputNode ??= ParseAndValidateOutput(response.Text, effectiveOutputSchema, config, agent.Name ?? "agent");
            var selectedTransition = ExtractSelectedTransition(outputNode, response.Text);
            if (!string.IsNullOrWhiteSpace(selectedTransition))
            {
                if (allowedTransitions.Count > 0 && !allowedTransitions.Contains(selectedTransition, StringComparer.OrdinalIgnoreCase))
                {
                    throw new WorkflowTaskException(
                        $"agent '{agent.Name}' selected transition '{selectedTransition}', which is not in allowedTransitions [{string.Join(", ", allowedTransitions)}].");
                }

                JsonPath.Set(context.Data, transitionInto, JsonValue.Create(selectedTransition));
            }
        }

        var toolCallsArray = ExtractToolCalls(response.Messages);
        if (config["toolCallsInto"]?.GetValue<string>() is { Length: > 0 } toolCallsInto)
        {
            JsonPath.Set(context.Data, toolCallsInto, toolCallsArray.DeepClone());
        }

        if (config["metaInto"]?.GetValue<string>() is { Length: > 0 } metaInto)
        {
            var meta = new JsonObject
            {
                ["agent"] = agent.Name,
                ["mode"] = mode,
                ["responseId"] = response.ResponseId,
                ["finishReason"] = response.FinishReason?.Value,
                ["inputTokens"] = response.Usage?.InputTokenCount,
                ["outputTokens"] = response.Usage?.OutputTokenCount,
                ["totalTokens"] = response.Usage?.TotalTokenCount,
                ["toolCallCount"] = toolCallsArray.Count,
                ["toolCalls"] = toolCallsArray,
                ["evaluatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            JsonPath.Set(context.Data, metaInto, meta);
        }
    }

    private static string? ExtractSelectedTransition(JsonNode? outputNode, string? rawText)
    {
        if (outputNode is JsonObject obj)
        {
            foreach (var key in (string[])["nextTransition", "transition", "route", "targetState", "selectedTransition"])
            {
                if (obj[key]?.GetValue<string>() is { Length: > 0 } val)
                {
                    return val;
                }
            }
        }
        else if (outputNode is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s.Trim();
        }

        return rawText?.Trim();
    }

    private static JsonArray ExtractToolCalls(IList<ChatMessage>? messages)
    {
        var calls = new JsonArray();
        if (messages is null || messages.Count == 0)
        {
            return calls;
        }

        var resultsByCallId = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            foreach (var res in msg.Contents.OfType<FunctionResultContent>())
            {
                if (!string.IsNullOrEmpty(res.CallId))
                {
                    resultsByCallId[res.CallId] = res;
                }
            }
        }

        foreach (var msg in messages)
        {
            foreach (var call in msg.Contents.OfType<FunctionCallContent>())
            {
                var argsNode = DeclarativeAiFunction.ConvertArgumentsToJsonObject(call.Arguments);
                JsonNode? resultNode = null;
                if (!string.IsNullOrEmpty(call.CallId) && resultsByCallId.TryGetValue(call.CallId, out var resContent))
                {
                    var rawResult = resContent.Result?.ToString();
                    if (!string.IsNullOrWhiteSpace(rawResult))
                    {
                        try
                        {
                            resultNode = JsonNode.Parse(rawResult);
                        }
                        catch (JsonException)
                        {
                            resultNode = JsonValue.Create(rawResult);
                        }
                    }
                }

                calls.Add(new JsonObject
                {
                    ["callId"] = call.CallId,
                    ["name"] = call.Name,
                    ["arguments"] = argsNode,
                    ["result"] = resultNode,
                });
            }
        }

        return calls;
    }

    private static List<AIAgent> ResolveMultiAgents(
        JsonArray? agentsArray,
        IMiddtAgentRegistry registry,
        WorkflowTaskContext context)
    {
        if (agentsArray is null || agentsArray.Count == 0)
        {
            throw new WorkflowTaskException("agent: 'agents' array is required for multi-agent workflow modes.");
        }

        var list = new List<AIAgent>(agentsArray.Count);
        foreach (var item in agentsArray)
        {
            if (item is JsonValue jv && jv.TryGetValue<string>(out var refName) && !string.IsNullOrWhiteSpace(refName))
            {
                var renderedName = context.Expressions.RenderText(refName, context.Scope);
                list.Add(registry.CreateAgent(renderedName));
            }
            else if (item is JsonObject obj)
            {
                var name = obj["name"]?.GetValue<string>() ?? obj["agent"]?.GetValue<string>() ?? $"step-{list.Count + 1}";
                var rawInstructions = obj["instructions"]?.GetValue<string>() ?? obj["system"]?.GetValue<string>();
                var renderedInstructions = rawInstructions is not null
                    ? context.Expressions.RenderText(rawInstructions, context.Scope)
                    : null;
                var model = obj["model"]?.GetValue<string>() ?? obj["modelId"]?.GetValue<string>();
                var tools = ExtractStringList(obj["tools"]);
                var mcpServers = ExtractStringList(obj["mcpServers"]);
                var outputSchema = obj["outputSchema"] as JsonObject;

                if (registry.GetDefinition(name) is not null && mcpServers.Count == 0)
                {
                    list.Add(registry.CreateAgent(name, renderedInstructions, tools, model));
                }
                else
                {
                    list.Add(registry.CreateAgent(new AgentDefinition
                    {
                        Name = name,
                        Description = obj["description"]?.GetValue<string>(),
                        Instructions = renderedInstructions,
                        ModelId = model,
                        Temperature = obj["temperature"]?.GetValue<float>(),
                        MaxOutputTokens = obj["maxOutputTokens"]?.GetValue<int>(),
                        Tools = tools,
                        McpServers = mcpServers,
                        OutputSchema = outputSchema,
                    }));
                }
            }
            else
            {
                throw new WorkflowTaskException("agent: each entry in 'agents' must be an agent name string or an agent definition object.");
            }
        }

        return list;
    }

    private static List<ChatMessage> BuildMessages(WorkflowTaskContext context, List<string> allowedTransitions)
    {
        var messages = new List<ChatMessage>();

        if (context.Render("messages") is JsonArray messagesArray)
        {
            foreach (var entry in messagesArray)
            {
                if (entry is JsonObject msgObj)
                {
                    var roleStr = (msgObj["role"]?.GetValue<string>() ?? "user").Trim().ToLowerInvariant();
                    var content = msgObj["content"]?.GetValue<string>() ?? msgObj["text"]?.GetValue<string>() ?? string.Empty;
                    var role = roleStr switch
                    {
                        "system" => ChatRole.System,
                        "assistant" => ChatRole.Assistant,
                        "tool" => ChatRole.Tool,
                        _ => ChatRole.User,
                    };
                    messages.Add(new ChatMessage(role, content));
                }
                else if (entry is JsonValue val && val.TryGetValue<string>(out var text))
                {
                    messages.Add(new ChatMessage(ChatRole.User, text));
                }
            }
        }

        var prompt = context.RenderText("prompt")
                     ?? context.RenderText("input")
                     ?? context.RenderText("message");

        var contextNode = context.Render("context");
        if (allowedTransitions.Count > 0)
        {
            var contextObj = contextNode as JsonObject ?? [];
            contextObj["allowedTransitions"] = new JsonArray([.. allowedTransitions.Select(t => (JsonNode?)JsonValue.Create(t))]);
            contextNode = contextObj;
        }

        if (!string.IsNullOrWhiteSpace(prompt) || contextNode is not null)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                builder.Append(prompt);
            }

            if (contextNode is not null)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.Append("Context JSON: ");
                builder.Append(contextNode.ToJsonString());
            }

            messages.Add(new ChatMessage(ChatRole.User, builder.ToString()));
        }

        return messages;
    }

    private static async Task<AgentSession> RestoreOrCreateSessionAsync(
        AIAgent agent,
        WorkflowTaskContext context,
        CancellationToken cancellationToken)
    {
        var sessionPath = context.Config["session"]?.GetValue<string>() ?? context.Config["thread"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sessionPath))
        {
            var existingNode = JsonPath.Get(context.Data, sessionPath) ?? context.Render("session") ?? context.Render("thread");
            if (existingNode is JsonObject or JsonArray)
            {
                using var doc = JsonDocument.Parse(existingNode.ToJsonString());
                return await agent.DeserializeSessionAsync(doc.RootElement.Clone(), cancellationToken: cancellationToken);
            }
        }

        return await agent.CreateSessionAsync(cancellationToken);
    }

    private static JsonNode? ParseAndValidateOutput(
        string? rawText,
        JsonObject? outputSchema,
        JsonObject config,
        string agentName)
    {
        var text = (rawText ?? string.Empty).Trim();
        var unwrapped = UnwrapMarkdownCodeFence(text);

        if (outputSchema is not null)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(unwrapped);
            }
            catch (JsonException ex)
            {
                throw new WorkflowTaskException(
                    $"agent '{agentName}' did not return valid JSON matching outputSchema: {ex.Message}",
                    ex);
            }

            var errors = JsonSchemaValidator.Validate(parsed, outputSchema);
            if (errors.Count > 0)
            {
                var summary = string.Join("; ", errors);
                throw new WorkflowTaskException($"agent '{agentName}' output failed schema validation: {summary}");
            }

            return parsed;
        }

        var parseJson = config["parseJson"]?.GetValue<bool>() ?? true;
        if (parseJson && ((unwrapped.StartsWith('{') && unwrapped.EndsWith('}')) || (unwrapped.StartsWith('[') && unwrapped.EndsWith(']'))))
        {
            try
            {
                return JsonNode.Parse(unwrapped);
            }
            catch (JsonException)
            {
                // Fall back to plain string value when response is not valid JSON.
            }
        }

        return JsonValue.Create(text);
    }

    private static string UnwrapMarkdownCodeFence(string text)
    {
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine)
            {
                text = text[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            var firstBrace = text.IndexOf('{');
            var lastBrace = text.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return text[firstBrace..(lastBrace + 1)].Trim();
            }
        }

        return text;
    }

    private static List<string> ExtractStringList(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return [];
        }

        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            if (item?.GetValue<string>() is { Length: > 0 } str)
            {
                list.Add(str);
            }
        }

        return list;
    }
}
