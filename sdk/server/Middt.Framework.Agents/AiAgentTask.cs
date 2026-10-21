using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
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
    /// <summary>Default JSON property name inspected on structured agent output when <c>transitionInto</c> is configured.</summary>
    public const string DefaultTransitionProperty = "nextTransition";

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        using var _ = WorkflowTaskAmbientContext.Enter(context);
        var registry = context.Services.GetService<IMiddtAgentRegistry>()
            ?? throw new WorkflowTaskException("agent task requires IMiddtAgentRegistry (call services.AddMiddtAgents(...)).");

        var config = context.Config;
        var mode = (context.RenderText("mode") ?? "single").Trim().ToLowerInvariant();
        var agentName = context.RenderText("agent") ?? context.RenderText("name");
        var instructions = context.RenderText("instructions") ?? context.RenderText("system");
        var modelOverride = context.RenderText("model") ?? context.RenderText("modelId");
        var toolsList = ExtractStringList(config["tools"]);
        var mcpServersList = ExtractStringList(config["mcpServers"]);
        var explicitAllowedTransitions = ExtractStringList(config["allowedTransitions"]);
        var allowedTransitions = new List<string>(explicitAllowedTransitions);
        var inlineOutputSchema = config["outputSchema"] as JsonObject;
        var transitionProperty = config["transitionProperty"]?.GetValue<string>() is { Length: > 0 } tp
            ? tp
            : DefaultTransitionProperty;
        var hasTransitionInto = config["transitionInto"]?.GetValue<string>() is { Length: > 0 };
        var stateTransitions = ResolveStateTransitions(context);

        AIAgent agent;
        JsonObject? effectiveOutputSchema = inlineOutputSchema;
        List<WorkflowTransitionOptionModel> allowedTransitionOptions = [];

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

            PopulateDynamicTransitions(allowedTransitions, explicitAllowedTransitions.Count > 0, stateTransitions, effectiveOutputSchema, transitionProperty, hasTransitionInto);
            allowedTransitionOptions = BuildTransitionOptions(allowedTransitions, stateTransitions);
            effectiveOutputSchema = ApplyDynamicTransitionsToSchema(effectiveOutputSchema, allowedTransitions, allowedTransitionOptions, transitionProperty, hasTransitionInto);

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

            var baseInstructions = !string.IsNullOrWhiteSpace(instructions) ? instructions : registeredDef.Instructions;
            agent = registry.CreateAgent(new AgentDefinition
            {
                Name = registeredDef.Name,
                Description = registeredDef.Description,
                Instructions = AppendTransitionRoutingGuidance(baseInstructions, allowedTransitionOptions, transitionProperty),
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
            PopulateDynamicTransitions(allowedTransitions, explicitAllowedTransitions.Count > 0, stateTransitions, effectiveOutputSchema, transitionProperty, hasTransitionInto);
            allowedTransitionOptions = BuildTransitionOptions(allowedTransitions, stateTransitions);
            effectiveOutputSchema = ApplyDynamicTransitionsToSchema(effectiveOutputSchema, allowedTransitions, allowedTransitionOptions, transitionProperty, hasTransitionInto);

            var adHocDef = new AgentDefinition
            {
                Name = !string.IsNullOrWhiteSpace(agentName) ? agentName : $"{context.Workflow}-{context.State}-agent",
                Description = context.RenderText("description"),
                Instructions = AppendTransitionRoutingGuidance(instructions, allowedTransitionOptions, transitionProperty),
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

        if (mode is "sequential" or "concurrent" or "parallel" or "handoff")
        {
            PopulateDynamicTransitions(allowedTransitions, explicitAllowedTransitions.Count > 0, stateTransitions, effectiveOutputSchema, transitionProperty, hasTransitionInto);
            allowedTransitionOptions = BuildTransitionOptions(allowedTransitions, stateTransitions);
            effectiveOutputSchema = ApplyDynamicTransitionsToSchema(effectiveOutputSchema, allowedTransitions, allowedTransitionOptions, transitionProperty, hasTransitionInto);
        }

        var messages = BuildMessages(context, allowedTransitionOptions);
        var db = context.Services.GetService<WorkflowDbContext>();
        var resolvedAgentName = !string.IsNullOrWhiteSpace(agent.Name)
            ? agent.Name
            : (!string.IsNullOrWhiteSpace(agentName) ? agentName : $"{context.Workflow}-{context.State}-agent");
        var taskKey = context.Task.DisplayKey;
        var sessionAgentKey = context.RenderText("sessionKey") ?? resolvedAgentName;
        var (session, hasRestoredSession, existingSessionEntity) = await RestoreOrCreateSessionAsync(
            agent,
            context,
            db,
            sessionAgentKey,
            taskKey,
            cancellationToken);

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
        catch (JsonException) when (hasRestoredSession)
        {
            // Fallback to a fresh session if the persisted session state cannot be deserialized.
            session = await agent.CreateSessionAsync(cancellationToken);
            try
            {
                response = await agent.RunAsync(messages, session, runOptions, cancellationToken);
            }
            catch (Exception retryEx) when (retryEx is not OperationCanceledException)
            {
                throw new WorkflowTaskException($"agent '{agent.Name}' execution failed: {retryEx.Message}", retryEx);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowTaskException($"agent '{agent.Name}' execution failed: {ex.Message}", ex);
        }

        var nowUtc = DateTime.UtcNow;
        var serializedSessionElement = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        var normalizedSessionNode = serializedSessionElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? NormalizePolymorphicJson(JsonNode.Parse(serializedSessionElement.GetRawText()))
            : null;

        if (db is not null
            && context.InstanceId != Guid.Empty
            && !string.IsNullOrWhiteSpace(context.Workflow)
            && normalizedSessionNode is not null)
        {
            var serializedSessionJson = normalizedSessionNode.ToJsonString();
            if (existingSessionEntity is not null)
            {
                existingSessionEntity.SessionData = serializedSessionJson;
                existingSessionEntity.TurnCount += 1;
                existingSessionEntity.UpdatedAt = nowUtc;
            }
            else
            {
                db.AgentSessions.Add(new WorkflowAgentSession
                {
                    Id = Guid.CreateVersion7(),
                    InstanceId = context.InstanceId,
                    WorkflowKey = context.Workflow,
                    AgentName = sessionAgentKey,
                    TaskKey = taskKey,
                    SessionData = serializedSessionJson,
                    TurnCount = 1,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc,
                });
            }
        }

        var sessionInto = config["sessionInto"]?.GetValue<string>() ?? config["threadInto"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sessionInto) && normalizedSessionNode is not null)
        {
            JsonPath.Set(context.Data, sessionInto, normalizedSessionNode.DeepClone());
        }

        var toolCallsArray = ExtractToolCalls(response.Messages);
        var meta = new JsonObject
        {
            ["agent"] = resolvedAgentName,
            ["mode"] = mode,
            ["responseId"] = response.ResponseId,
            ["finishReason"] = response.FinishReason?.Value,
            ["inputTokens"] = response.Usage?.InputTokenCount,
            ["outputTokens"] = response.Usage?.OutputTokenCount,
            ["totalTokens"] = response.Usage?.TotalTokenCount,
            ["toolCallCount"] = toolCallsArray.Count,
            ["toolCalls"] = toolCallsArray.DeepClone(),
            ["allowedTransitions"] = ToJsonArray(allowedTransitionOptions),
            ["evaluatedAt"] = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
        };

        var into = config["into"]?.GetValue<string>();
        var transitionInto = config["transitionInto"]?.GetValue<string>();
        var shouldRoute = !string.IsNullOrWhiteSpace(transitionInto) || allowedTransitions.Count > 0;

        var outputNode = ParseAndValidateOutput(response.Text, response.Messages, effectiveOutputSchema, config, resolvedAgentName);
        string? selectedTransition = null;

        if (shouldRoute)
        {
            selectedTransition = ExtractSelectedTransition(outputNode, response.Text, transitionProperty);
            if (!string.IsNullOrWhiteSpace(selectedTransition))
            {
                if (allowedTransitions.Count > 0 && !allowedTransitions.Contains(selectedTransition, StringComparer.OrdinalIgnoreCase))
                {
                    throw new WorkflowTaskException(
                        $"agent '{resolvedAgentName}' selected transition '{selectedTransition}', which is not in allowedTransitions [{string.Join(", ", allowedTransitions)}].");
                }

                context.SelectTransition?.Invoke(selectedTransition);

                if (outputNode is JsonObject routedObj)
                {
                    RemovePropertyCaseInsensitive(routedObj, transitionProperty);
                }

                if (!string.IsNullOrWhiteSpace(transitionInto))
                {
                    JsonPath.Set(context.Data, transitionInto, JsonValue.Create(selectedTransition));
                }
            }
        }

        var runId = Guid.CreateVersion7();
        var toolCallModels = new List<WorkflowAgentToolCallModel>(toolCallsArray.Count);
        var toolCallEntities = new List<WorkflowAgentToolCall>(toolCallsArray.Count);
        for (var i = 0; i < toolCallsArray.Count; i++)
        {
            if (toolCallsArray[i] is not JsonObject tcObj)
            {
                continue;
            }

            var tcId = Guid.CreateVersion7();
            var callId = tcObj["callId"]?.GetValue<string>() ?? $"call_{i + 1}";
            var toolName = tcObj["name"]?.GetValue<string>() ?? string.Empty;
            var argsClone = tcObj["arguments"]?.DeepClone();
            var resultClone = tcObj["result"]?.DeepClone();

            toolCallModels.Add(new WorkflowAgentToolCallModel
            {
                Id = tcId,
                RunId = runId,
                AgentName = resolvedAgentName,
                TaskKey = taskKey,
                CallId = callId,
                Name = toolName,
                Arguments = argsClone,
                Result = resultClone,
                Order = i,
                CreatedAt = nowUtc,
            });

            toolCallEntities.Add(new WorkflowAgentToolCall
            {
                Id = tcId,
                RunId = runId,
                InstanceId = context.InstanceId,
                WorkflowKey = context.Workflow,
                AgentName = resolvedAgentName,
                TaskKey = taskKey,
                CallId = callId,
                ToolName = toolName,
                Arguments = argsClone?.ToJsonString(),
                Result = resultClone?.ToJsonString(),
                Order = i,
                CreatedAt = nowUtc,
            });
        }

        var runModel = new WorkflowAgentRunModel
        {
            Id = runId,
            State = context.State,
            TransitionKey = context.Transition,
            TaskKey = taskKey,
            AgentName = resolvedAgentName,
            Mode = mode,
            SelectedTransition = selectedTransition,
            AllowedTransitions = [.. allowedTransitionOptions],
            ResponseId = response.ResponseId,
            FinishReason = response.FinishReason?.Value,
            InputTokens = response.Usage?.InputTokenCount,
            OutputTokens = response.Usage?.OutputTokenCount,
            TotalTokens = response.Usage?.TotalTokenCount,
            ToolCallCount = toolCallModels.Count,
            Output = outputNode?.DeepClone(),
            CreatedAt = nowUtc,
            ToolCalls = toolCallModels,
        };

        context.RecordAgentRun?.Invoke(runModel);

        if (db is not null && context.InstanceId != Guid.Empty && !string.IsNullOrWhiteSpace(context.Workflow))
        {
            db.AgentRuns.Add(new WorkflowAgentRun
            {
                Id = runId,
                InstanceId = context.InstanceId,
                WorkflowKey = context.Workflow,
                State = context.State,
                TransitionKey = context.Transition,
                TaskKey = taskKey,
                AgentName = resolvedAgentName,
                Mode = mode,
                SelectedTransition = selectedTransition,
                AllowedTransitions = allowedTransitionOptions.Count > 0
                    ? JsonSerializer.Serialize(allowedTransitionOptions, CamelCaseJsonOptions)
                    : null,
                ResponseId = response.ResponseId,
                FinishReason = response.FinishReason?.Value,
                InputTokens = response.Usage?.InputTokenCount,
                OutputTokens = response.Usage?.OutputTokenCount,
                TotalTokens = response.Usage?.TotalTokenCount,
                ToolCallCount = toolCallEntities.Count,
                Output = outputNode?.ToJsonString(),
                CreatedAt = nowUtc,
            });

            if (toolCallEntities.Count > 0)
            {
                db.AgentToolCalls.AddRange(toolCallEntities);
            }
        }

        if (!string.IsNullOrWhiteSpace(into))
        {
            JsonPath.Set(context.Data, into, outputNode?.DeepClone());
        }

        if (config["toolCallsInto"]?.GetValue<string>() is { Length: > 0 } toolCallsInto)
        {
            JsonPath.Set(context.Data, toolCallsInto, toolCallsArray.DeepClone());
        }

        if (config["metaInto"]?.GetValue<string>() is { Length: > 0 } metaInto)
        {
            JsonPath.Set(context.Data, metaInto, meta.DeepClone());
        }
    }

    private static void RemovePropertyCaseInsensitive(JsonObject obj, string propertyName)
    {
        var matchingKey = obj.Select(kv => kv.Key)
            .FirstOrDefault(k => string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase));
        if (matchingKey is not null)
        {
            obj.Remove(matchingKey);
        }
    }

    private static string? ExtractSelectedTransition(JsonNode? outputNode, string? rawText, string transitionProperty)
    {
        if (outputNode is JsonObject obj && MiddtMcpClient.TryGetCaseInsensitive(obj, transitionProperty, out var propNode) && propNode?.GetValue<string>() is { Length: > 0 } val)
        {
            return val;
        }

        if (outputNode is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
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
                    resultNode = MiddtMcpClient.TryParseJsonOrValue(resContent.Result?.ToString());
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

    private static List<TransitionDefinition> ResolveStateTransitions(WorkflowTaskContext context)
    {
        var workflowRegistry = context.Services.GetService<WorkflowRegistry>();
        if (workflowRegistry is null || string.IsNullOrWhiteSpace(context.Workflow))
        {
            return [];
        }

        WorkflowDefinition? workflowDef;
        try
        {
            var version = context.Scope.Instance["version"]?.GetValue<int>();
            workflowDef = version is > 0
                ? workflowRegistry.Get(context.Workflow, version.Value)
                : workflowRegistry.GetLatest(context.Workflow);
        }
        catch (WorkflowNotFoundException)
        {
            return [];
        }

        var stateKey = !string.IsNullOrWhiteSpace(context.State) ? context.State : workflowDef.Start.Target;
        var stateDef = workflowDef.FindState(stateKey);
        if (stateDef is null || stateDef.IsFinal)
        {
            return [];
        }

        var allTransitions = stateDef.Transitions
            .Concat(workflowDef.SharedTransitions.Where(t => t.AvailableIn.Contains("*") || t.AvailableIn.Contains(stateKey)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Key))
            .ToList();

        var nonScheduled = allTransitions.Where(t => t.Trigger != TriggerType.Scheduled).ToList();
        return nonScheduled.Count > 0 ? nonScheduled : allTransitions;
    }

    private static void PopulateDynamicTransitions(
        List<string> allowedTransitions,
        bool hasExplicitTaskOverride,
        List<TransitionDefinition> stateTransitions,
        JsonObject? outputSchema,
        string transitionProperty,
        bool hasTransitionInto)
    {
        if (hasExplicitTaskOverride || stateTransitions.Count == 0)
        {
            return;
        }

        var hasTransitionSchemaProperty = outputSchema?["properties"] is JsonObject props
                                          && MiddtMcpClient.TryGetCaseInsensitive(props, transitionProperty, out _);

        var automaticRoutingTransitions = stateTransitions
            .Where(t => t.Trigger == TriggerType.Automatic
                        && (string.IsNullOrWhiteSpace(t.Rule)
                            || t.Rule.Contains(transitionProperty, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (!hasTransitionInto
            && !hasTransitionSchemaProperty
            && allowedTransitions.Count == 0
            && automaticRoutingTransitions.Count == 0)
        {
            return;
        }

        var sourceTransitions = automaticRoutingTransitions.Count > 0 && !hasTransitionInto
            ? automaticRoutingTransitions
            : stateTransitions;

        foreach (var tr in sourceTransitions)
        {
            if (!allowedTransitions.Contains(tr.Key, StringComparer.OrdinalIgnoreCase))
            {
                allowedTransitions.Add(tr.Key);
            }
        }
    }

    private static List<WorkflowTransitionOptionModel> BuildTransitionOptions(
        List<string> allowedTransitions,
        List<TransitionDefinition> stateTransitions)
    {
        if (allowedTransitions.Count == 0)
        {
            return [];
        }

        var list = new List<WorkflowTransitionOptionModel>(allowedTransitions.Count);
        foreach (var key in allowedTransitions)
        {
            var match = stateTransitions.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            list.Add(new WorkflowTransitionOptionModel
            {
                Key = match?.Key ?? key,
                Title = string.IsNullOrWhiteSpace(match?.Title) ? null : match.Title,
                Description = string.IsNullOrWhiteSpace(match?.Description) ? null : match.Description,
                Target = string.IsNullOrWhiteSpace(match?.Target) ? null : match.Target,
            });
        }

        return list;
    }

    private static JsonArray ToJsonArray(List<WorkflowTransitionOptionModel> options) =>
        new([.. options.Select(t => (JsonNode?)new JsonObject
        {
            ["key"] = t.Key,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["target"] = t.Target,
        })]);

    private static string? AppendTransitionRoutingGuidance(
        string? instructions,
        List<WorkflowTransitionOptionModel> options,
        string transitionProperty)
    {
        var guidance = BuildTransitionRoutingGuidance(options, transitionProperty);
        if (string.IsNullOrWhiteSpace(guidance))
        {
            return instructions;
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            return guidance;
        }

        return instructions.TrimEnd() + "\n\n" + guidance;
    }

    private static string BuildTransitionRoutingGuidance(
        List<WorkflowTransitionOptionModel> options,
        string transitionProperty)
    {
        if (options.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("Available workflow transitions for '")
          .Append(transitionProperty)
          .Append("': ");

        for (var i = 0; i < options.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("; ");
            }

            var opt = options[i];
            sb.Append(opt.Key);
            if (!string.IsNullOrWhiteSpace(opt.Title) || !string.IsNullOrWhiteSpace(opt.Target) || !string.IsNullOrWhiteSpace(opt.Description))
            {
                sb.Append(" (");
                var hasPart = false;
                if (!string.IsNullOrWhiteSpace(opt.Title))
                {
                    sb.Append(opt.Title);
                    hasPart = true;
                }

                if (!string.IsNullOrWhiteSpace(opt.Target))
                {
                    if (hasPart)
                    {
                        sb.Append(" -> ");
                    }

                    sb.Append(opt.Target);
                    hasPart = true;
                }

                if (!string.IsNullOrWhiteSpace(opt.Description))
                {
                    if (hasPart)
                    {
                        sb.Append(": ");
                    }

                    sb.Append(opt.Description);
                }

                sb.Append(')');
            }
        }

        sb.Append('.');
        return sb.ToString();
    }

    private static JsonObject? ApplyDynamicTransitionsToSchema(
        JsonObject? schema,
        List<string> allowedTransitions,
        List<WorkflowTransitionOptionModel> transitionOptions,
        string transitionProperty,
        bool hasTransitionInto)
    {
        if (schema is null || allowedTransitions.Count == 0)
        {
            return schema;
        }

        var shouldInject = hasTransitionInto || allowedTransitions.Count > 0;
        var cloned = (JsonObject)schema.DeepClone();
        if (cloned["properties"] is not JsonObject props)
        {
            if (!shouldInject)
            {
                return cloned;
            }

            props = [];
            cloned["properties"] = props;
        }

        var targetPropKey = transitionProperty;
        JsonObject? propSchema = null;
        foreach (var (k, v) in props)
        {
            if (string.Equals(k, transitionProperty, StringComparison.OrdinalIgnoreCase) && v is JsonObject existingObj)
            {
                targetPropKey = k;
                propSchema = existingObj;
                break;
            }
        }

        if (propSchema is null)
        {
            if (!shouldInject)
            {
                return cloned;
            }

            propSchema = new JsonObject { ["type"] = "string" };
            props[targetPropKey] = propSchema;
        }

        propSchema["type"] ??= "string";

        var guidance = BuildTransitionRoutingGuidance(transitionOptions, targetPropKey);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            var existingDescription = propSchema["description"]?.GetValue<string>();
            propSchema["description"] = string.IsNullOrWhiteSpace(existingDescription)
                ? guidance
                : existingDescription.TrimEnd() + " " + guidance;
        }

        var enumArray = new JsonArray();
        if (propSchema["enum"] is JsonArray existingEnum)
        {
            foreach (var item in existingEnum)
            {
                if (item?.GetValue<string>() is { Length: > 0 } existingKey
                    && allowedTransitions.Contains(existingKey, StringComparer.OrdinalIgnoreCase))
                {
                    enumArray.Add(JsonValue.Create(existingKey));
                }
            }
        }

        foreach (var trKey in allowedTransitions)
        {
            if (!enumArray.Any(e => string.Equals(e?.GetValue<string>(), trKey, StringComparison.OrdinalIgnoreCase)))
            {
                enumArray.Add(JsonValue.Create(trKey));
            }
        }

        propSchema["enum"] = enumArray;

        var currentDefault = propSchema["default"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(currentDefault)
            || !allowedTransitions.Contains(currentDefault, StringComparer.OrdinalIgnoreCase))
        {
            propSchema["default"] = JsonValue.Create(allowedTransitions[^1]);
        }

        if (shouldInject)
        {
            if (cloned["required"] is not JsonArray reqArray)
            {
                reqArray = [];
                cloned["required"] = reqArray;
            }

            if (!reqArray.Any(r => string.Equals(r?.GetValue<string>(), targetPropKey, StringComparison.OrdinalIgnoreCase)))
            {
                reqArray.Add(JsonValue.Create(targetPropKey));
            }
        }

        return cloned;
    }

    private static List<ChatMessage> BuildMessages(
        WorkflowTaskContext context,
        List<WorkflowTransitionOptionModel> allowedTransitions)
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
            contextObj["allowedTransitions"] = ToJsonArray(allowedTransitions);
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

    private static async Task<(AgentSession Session, bool Restored, WorkflowAgentSession? Entity)> RestoreOrCreateSessionAsync(
        AIAgent agent,
        WorkflowTaskContext context,
        WorkflowDbContext? db,
        string sessionAgentKey,
        string taskKey,
        CancellationToken cancellationToken)
    {
        WorkflowAgentSession? sessionEntity = null;
        JsonNode? existingNode = null;

        if (db is not null && context.InstanceId != Guid.Empty && !string.IsNullOrWhiteSpace(context.Workflow))
        {
            sessionEntity = db.AgentSessions.Local.FirstOrDefault(s =>
                                s.InstanceId == context.InstanceId
                                && string.Equals(s.WorkflowKey, context.Workflow, StringComparison.Ordinal)
                                && string.Equals(s.AgentName, sessionAgentKey, StringComparison.Ordinal)
                                && string.Equals(s.TaskKey, taskKey, StringComparison.Ordinal))
                            ?? db.AgentSessions.Local.FirstOrDefault(s =>
                                s.InstanceId == context.InstanceId
                                && string.Equals(s.WorkflowKey, context.Workflow, StringComparison.Ordinal)
                                && string.Equals(s.AgentName, sessionAgentKey, StringComparison.Ordinal));

            if (sessionEntity is null && context.Scope.Instance["agentRuns"] is JsonArray { Count: > 0 })
            {
                var instanceSessions = await db.AgentSessions
                    .Where(s => s.InstanceId == context.InstanceId && s.WorkflowKey == context.Workflow && s.AgentName == sessionAgentKey)
                    .ToListAsync(cancellationToken);

                sessionEntity = instanceSessions.FirstOrDefault(s => string.Equals(s.TaskKey, taskKey, StringComparison.Ordinal))
                                ?? instanceSessions.FirstOrDefault();
            }

            if (sessionEntity is not null && !string.IsNullOrWhiteSpace(sessionEntity.SessionData))
            {
                try
                {
                    existingNode = JsonNode.Parse(sessionEntity.SessionData);
                }
                catch (JsonException)
                {
                    existingNode = null;
                }
            }
        }

        if (existingNode is null)
        {
            var sessionPath = context.Config["session"]?.GetValue<string>() ?? context.Config["thread"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sessionPath))
            {
                existingNode = JsonPath.Get(context.Data, sessionPath) ?? context.Render("session") ?? context.Render("thread");
            }
        }

        if (existingNode is JsonObject or JsonArray)
        {
            try
            {
                var normalizedNode = NormalizePolymorphicJson(existingNode)!;
                using var doc = JsonDocument.Parse(normalizedNode.ToJsonString());
                var restored = await agent.DeserializeSessionAsync(doc.RootElement.Clone(), cancellationToken: cancellationToken);
                return (restored, true, sessionEntity);
            }
            catch (JsonException)
            {
                // Fall back to a fresh session if the stored session JSON cannot be deserialized.
            }
        }

        return (await agent.CreateSessionAsync(cancellationToken), false, sessionEntity);
    }

    /// <summary>
    /// Recursively ensures that any <c>$type</c> (and other <c>$</c>-prefixed metadata properties) appear first in every
    /// <see cref="JsonObject"/>. PostgreSQL <c>jsonb</c> sorts object keys by length first (e.g. 4-char <c>"text"</c> or
    /// <c>"name"</c> before 5-char <c>"$type"</c>), which causes <see cref="JsonSerializer"/> polymorphic deserialization
    /// to fail unless <c>$type</c> is restored to the first position.
    /// </summary>
    internal static JsonNode? NormalizePolymorphicJson(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var normalized = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (key.StartsWith('$'))
                    {
                        normalized[key] = NormalizePolymorphicJson(value);
                    }
                }

                foreach (var (key, value) in obj)
                {
                    if (!key.StartsWith('$'))
                    {
                        normalized[key] = NormalizePolymorphicJson(value);
                    }
                }

                return normalized;
            }

            case JsonArray arr:
            {
                var normalized = new JsonArray();
                foreach (var item in arr)
                {
                    normalized.Add(NormalizePolymorphicJson(item));
                }

                return normalized;
            }

            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? ParseAndValidateOutput(
        string? rawText,
        IList<ChatMessage>? messages,
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
                if (TryMergeMultiAgentJsonMessages(messages, out var merged))
                {
                    return merged;
                }
            }
        }

        return JsonValue.Create(text);
    }

    private static bool TryMergeMultiAgentJsonMessages(IList<ChatMessage>? messages, out JsonObject merged)
    {
        merged = [];
        if (messages is null || messages.Count == 0)
        {
            return false;
        }

        var steps = new JsonArray();
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.Assistant || string.IsNullOrWhiteSpace(msg.Text))
            {
                continue;
            }

            var candidate = UnwrapMarkdownCodeFence(msg.Text.Trim());
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}'))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(candidate) is JsonObject stepObj)
                {
                    steps.Add(stepObj.DeepClone());
                    foreach (var (k, v) in stepObj)
                    {
                        merged[k] = v?.DeepClone();
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON assistant messages in multi-agent transcripts.
            }
        }

        if (steps.Count == 0)
        {
            return false;
        }

        merged["steps"] = steps;
        return true;
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
