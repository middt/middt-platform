using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Middt.Framework.Agents;

/// <summary>
/// Deterministic in-process <see cref="IChatClient"/> implementation used when <see cref="AgentSettings.UseLocalFallback"/>
/// is enabled and no external AI provider credentials are configured. Runs through the full <c>Microsoft.Agents.AI</c>
/// (<c>ChatClientAgent</c>, <c>FunctionInvokingChatClient</c>, <c>OpenTelemetryChatClient</c>, and <c>AgentWorkflowBuilder</c>)
/// pipeline, invokes registered <see cref="AIFunction"/> tools via <see cref="FunctionCallContent"/>, and synthesizes
/// schema-compliant JSON responses enriched with tool outputs.
/// </summary>
public sealed class LocalAgentChatClient(string modelId = "local-deterministic") : IChatClient
{
    private const string ContextJsonMarker = "Context JSON: ";

    public ChatClientMetadata Metadata { get; } = new("middt-local", null, modelId);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var messageList = messages as IList<ChatMessage> ?? [.. messages];
        var lastUserText = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                           ?? messageList.LastOrDefault()?.Text
                           ?? string.Empty;

        var contextObj = ExtractContextJson(messageList);
        var hasToolResults = messageList.Any(m => m.Role == ChatRole.Tool || m.Contents.OfType<FunctionResultContent>().Any());

        if (!hasToolResults && options?.Tools is { Count: > 0 } availableTools)
        {
            var toolCalls = BuildToolCalls(availableTools, contextObj);
            if (toolCalls.Count > 0)
            {
                var toolCallMessage = new ChatMessage(ChatRole.Assistant, [.. toolCalls.Cast<AIContent>()])
                {
                    MessageId = Guid.CreateVersion7().ToString("N"),
                };
                return Task.FromResult(new ChatResponse(toolCallMessage)
                {
                    ResponseId = Guid.CreateVersion7().ToString("N"),
                    ModelId = options.ModelId ?? Metadata.DefaultModelId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    FinishReason = ChatFinishReason.ToolCalls,
                });
            }
        }

        var mergedToolOutput = ExtractMergedToolOutputs(messageList);

        string responseText;
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { ValueKind: JsonValueKind.Object } schemaElement })
        {
            var schemaNode = JsonNode.Parse(schemaElement.GetRawText()) as JsonObject ?? [];
            var synthesized = SynthesizeFromSchema(schemaNode, lastUserText, propertyName: null, mergedToolOutput);
            responseText = synthesized?.ToJsonString() ?? "{}";
        }
        else if (mergedToolOutput.Count > 0)
        {
            responseText = TryExtractSummaryText(mergedToolOutput) ?? mergedToolOutput.ToJsonString();
        }
        else
        {
            responseText = string.IsNullOrWhiteSpace(lastUserText)
                ? "OK"
                : lastUserText;
        }

        var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText)
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
        };

        var inputTokens = EstimateTokens(messageList.Sum(m => m.Text?.Length ?? 0));
        var outputTokens = EstimateTokens(responseText.Length);

        var response = new ChatResponse(assistantMessage)
        {
            ResponseId = Guid.CreateVersion7().ToString("N"),
            ModelId = options?.ModelId ?? Metadata.DefaultModelId,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens,
            },
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
        {
            ResponseId = response.ResponseId,
            MessageId = response.Messages.FirstOrDefault()?.MessageId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Generically synthesizes a JSON value conforming to the given JSON Schema definition,
    /// preferring matching fields returned by executed <see cref="AIFunction"/> tools when present.
    /// </summary>
    public static JsonNode? SynthesizeFromSchema(
        JsonObject schema,
        string promptText,
        string? propertyName,
        JsonObject? toolOutputs = null)
    {
        if (propertyName is not null && toolOutputs is not null && TryGetCaseInsensitive(toolOutputs, propertyName, out var toolVal) && toolVal is not null)
        {
            var coerced = CoerceToSchema(toolVal, schema);
            if (coerced is not null)
            {
                return coerced;
            }
        }

        if (schema["default"] is { } defaultNode)
        {
            return defaultNode.DeepClone();
        }

        if (schema["const"] is { } constNode)
        {
            return constNode.DeepClone();
        }

        if (schema["enum"] is JsonArray { Count: > 0 } enumArray)
        {
            return enumArray[0]?.DeepClone();
        }

        var type = schema["type"]?.GetValue<string>() ?? "object";
        return type switch
        {
            "object" => SynthesizeObject(schema, promptText, toolOutputs),
            "array" => SynthesizeArray(schema, promptText, toolOutputs),
            "integer" => SynthesizeInteger(schema),
            "number" => SynthesizeNumber(schema),
            "boolean" => JsonValue.Create(true),
            "string" => SynthesizeString(schema, promptText, propertyName),
            _ => new JsonObject(),
        };
    }

    private static List<FunctionCallContent> BuildToolCalls(IList<AITool> availableTools, JsonObject contextObj)
    {
        var calls = new List<FunctionCallContent>();
        var callIndex = 1;

        foreach (var tool in availableTools)
        {
            if (tool is not AIFunction fn)
            {
                continue;
            }

            if (fn.Name is MiddtWorkflowAgentTools.EvaluateExpressionToolName
                && !contextObj.ContainsKey("expression"))
            {
                continue;
            }

            if (fn.Name is MiddtWorkflowAgentTools.GetInstanceToolName
                && !contextObj.ContainsKey("instanceId"))
            {
                continue;
            }

            if (fn.Name is MiddtWorkflowAgentTools.ListDefinitionsToolName
                && availableTools.Count > 1)
            {
                continue;
            }

            var argsDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in contextObj)
            {
                argsDict[k] = ConvertJsonNodeToPrimitive(v);
            }

            calls.Add(new FunctionCallContent($"call_{callIndex++}", fn.Name, argsDict));
        }

        return calls;
    }

    private static object? ConvertJsonNodeToPrimitive(JsonNode? node) => node switch
    {
        null => (object?)null,
        JsonValue jv when jv.TryGetValue<string>(out var s) => (object)s,
        JsonValue jv when jv.TryGetValue<bool>(out var b) => (object)b,
        JsonValue jv when jv.TryGetValue<long>(out var l) => (object)l,
        JsonValue jv when jv.TryGetValue<double>(out var d) => (object)d,
        _ => (object)node.DeepClone(),
    };

    private static JsonObject ExtractContextJson(IList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var text = messages[i].Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var idx = text.LastIndexOf(ContextJsonMarker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var jsonSlice = text[(idx + ContextJsonMarker.Length)..].Trim();
                try
                {
                    if (JsonNode.Parse(jsonSlice) is JsonObject parsed)
                    {
                        return parsed;
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed context tail
                }
            }
        }

        return [];
    }

    private static JsonObject ExtractMergedToolOutputs(IList<ChatMessage> messages)
    {
        var merged = new JsonObject();
        foreach (var msg in messages)
        {
            foreach (var resultContent in msg.Contents.OfType<FunctionResultContent>())
            {
                var raw = resultContent.Result?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(raw) is JsonObject obj)
                    {
                        foreach (var (k, v) in obj)
                        {
                            merged[k] = v?.DeepClone();
                        }
                    }
                }
                catch (JsonException)
                {
                    merged["result"] = JsonValue.Create(raw);
                }
            }
        }

        return merged;
    }

    private static string? TryExtractSummaryText(JsonObject obj)
    {
        foreach (var key in (string[])["advice", "summary", "text", "message", "reasoning"])
        {
            if (TryGetCaseInsensitive(obj, key, out var node)
                && node is JsonValue jv
                && jv.TryGetValue<string>(out var str)
                && !string.IsNullOrWhiteSpace(str))
            {
                return str;
            }
        }

        return null;
    }

    private static bool TryGetCaseInsensitive(JsonObject obj, string propertyName, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(propertyName, out value))
        {
            return true;
        }

        foreach (var (k, v) in obj)
        {
            if (string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static JsonNode? CoerceToSchema(JsonNode value, JsonObject schema)
    {
        var expectedType = schema["type"]?.GetValue<string>();
        JsonNode? candidate = value.DeepClone();

        if (expectedType is "number" or "integer" && candidate is JsonValue jvStr && jvStr.TryGetValue<string>(out var numStr))
        {
            if (expectedType == "integer" && long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lVal))
            {
                candidate = JsonValue.Create(lVal);
            }
            else if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dVal))
            {
                candidate = JsonValue.Create(dVal);
            }
        }
        else if (expectedType == "boolean" && candidate is JsonValue bStr && bStr.TryGetValue<string>(out var boolText)
                 && bool.TryParse(boolText, out var bVal))
        {
            candidate = JsonValue.Create(bVal);
        }

        if (schema["enum"] is JsonArray allowed && allowed.Count > 0)
        {
            var matchesEnum = allowed.Any(a => JsonNode.DeepEquals(a, candidate));
            if (!matchesEnum)
            {
                return null;
            }
        }

        return candidate;
    }

    private static JsonObject SynthesizeObject(JsonObject schema, string promptText, JsonObject? toolOutputs)
    {
        var obj = new JsonObject();
        if (schema["properties"] is JsonObject props)
        {
            foreach (var (propName, propNode) in props)
            {
                if (propNode is JsonObject propSchema)
                {
                    obj[propName] = SynthesizeFromSchema(propSchema, promptText, propName, toolOutputs);
                }
            }
        }

        return obj;
    }

    private static JsonArray SynthesizeArray(JsonObject schema, string promptText, JsonObject? toolOutputs)
    {
        var arr = new JsonArray();
        var minItems = schema["minItems"]?.GetValue<int>() ?? 0;
        if (minItems > 0 && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < minItems; i++)
            {
                arr.Add(SynthesizeFromSchema(itemSchema, promptText, propertyName: null, toolOutputs));
            }
        }

        return arr;
    }

    private static JsonValue SynthesizeInteger(JsonObject schema)
    {
        if (schema["minimum"] is { } min && min.AsValue().TryGetValue<long>(out var minVal))
        {
            return JsonValue.Create(minVal);
        }

        if (schema["maximum"] is { } max && max.AsValue().TryGetValue<long>(out var maxVal) && maxVal < 0)
        {
            return JsonValue.Create(maxVal);
        }

        return JsonValue.Create(0L);
    }

    private static JsonValue SynthesizeNumber(JsonObject schema)
    {
        if (schema["minimum"] is { } min && min.AsValue().TryGetValue<double>(out var minVal))
        {
            return JsonValue.Create(minVal);
        }

        if (schema["maximum"] is { } max && max.AsValue().TryGetValue<double>(out var maxVal) && maxVal < 0)
        {
            return JsonValue.Create(maxVal);
        }

        return JsonValue.Create(0d);
    }

    private static JsonValue SynthesizeString(JsonObject schema, string promptText, string? propertyName)
    {
        if (schema["format"]?.GetValue<string>() is "date-time")
        {
            return JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        if (schema["format"]?.GetValue<string>() is "date")
        {
            return JsonValue.Create(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var candidate = schema["title"]?.GetValue<string>()
                        ?? schema["description"]?.GetValue<string>()
                        ?? (!string.IsNullOrWhiteSpace(promptText) ? FirstLine(promptText) : propertyName ?? "value");

        if (schema["maxLength"]?.GetValue<int>() is > 0 and var maxLen && candidate.Length > maxLen)
        {
            candidate = candidate[..maxLen];
        }

        if (schema["minLength"]?.GetValue<int>() is > 0 and var minLen && candidate.Length < minLen)
        {
            candidate = candidate.PadRight(minLen, '.');
        }

        return JsonValue.Create(candidate);
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        var line = newlineIndex >= 0 ? trimmed[..newlineIndex].Trim() : trimmed;
        return line.Length > 240 ? line[..240] : line;
    }

    private static int EstimateTokens(int charCount) => Math.Max(1, (charCount + 3) / 4);
}
