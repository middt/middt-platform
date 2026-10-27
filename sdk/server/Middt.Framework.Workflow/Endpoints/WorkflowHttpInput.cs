using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Middt.Framework.Workflow.Endpoints;

/// <summary>
/// Automatically enriches workflow transition and start inputs with HTTP request metadata
/// (<c>input.header</c>, <c>input.query</c>, <c>input.body</c>) so workflow definitions and expressions
/// can access headers, query string parameters, and request body directly without manual controller mapping.
/// </summary>
public static class WorkflowHttpInput
{
    private const string EnvelopeMarker = "$middt_http";

    /// <summary>
    /// Builds an input <see cref="JsonObject"/> containing <c>header</c>, <c>query</c>, and <c>body</c> sections,
    /// while preserving top-level body properties for backward compatibility and direct access.
    /// </summary>
    public static JsonObject FromHttpContext(HttpContext? http, JsonObject? body)
    {
        var input = new JsonObject();
        var headerObj = new JsonObject();
        var queryObj = new JsonObject();
        var bodyObj = body != null ? (JsonObject)body.DeepClone() : [];

        if (http is not null)
        {
            // 1. Query parameters
            foreach (var (key, values) in http.Request.Query)
            {
                var valueStr = values.ToString();
                var parsed = ParseValue(valueStr);
                queryObj[key] = parsed?.DeepClone();

                var camel = ToCamelCase(key);
                if (camel != key)
                {
                    queryObj[camel] = parsed?.DeepClone();
                }
            }

            // 2. Request headers
            foreach (var (key, values) in http.Request.Headers)
            {
                var valueStr = values.ToString();
                var parsed = ParseValue(valueStr);
                headerObj[key] = parsed?.DeepClone();

                var lower = key.ToLowerInvariant();
                if (lower != key)
                {
                    headerObj[lower] = parsed?.DeepClone();
                }

                var camel = ToCamelCase(key);
                if (camel != key)
                {
                    headerObj[camel] = parsed?.DeepClone();
                }

                if (lower.StartsWith("x-", StringComparison.Ordinal))
                {
                    var stripped = ToCamelCase(lower[2..]);
                    if (!string.IsNullOrEmpty(stripped))
                    {
                        headerObj[stripped] = parsed?.DeepClone();
                    }
                }
            }

            // 3. Built-in correlation, trace, and request origin metadata
            var correlationId = headerObj["correlationId"]?.GetValue<string>()
                ?? headerObj["requestId"]?.GetValue<string>()
                ?? Activity.Current?.TraceId.ToString()
                ?? http.TraceIdentifier;
            var traceId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
            var spanId = Activity.Current?.SpanId.ToString();
            var clientIp = http.Connection.RemoteIpAddress?.ToString();
            var userAgent = http.Request.Headers.UserAgent.ToString();

            if (!string.IsNullOrEmpty(correlationId) && headerObj["correlationId"] is null)
            {
                headerObj["correlationId"] = correlationId;
            }

            if (!string.IsNullOrEmpty(traceId) && headerObj["traceId"] is null)
            {
                headerObj["traceId"] = traceId;
            }

            if (!string.IsNullOrEmpty(spanId) && headerObj["spanId"] is null)
            {
                headerObj["spanId"] = spanId;
            }

            if (!string.IsNullOrEmpty(clientIp) && headerObj["clientIp"] is null)
            {
                headerObj["clientIp"] = clientIp;
            }

            if (!string.IsNullOrEmpty(userAgent) && headerObj["userAgent"] is null)
            {
                headerObj["userAgent"] = userAgent;
            }
        }

        input[EnvelopeMarker] = true;
        input["header"] = headerObj;
        input["query"] = queryObj;
        input["body"] = bodyObj;

        // Copy body properties to root for direct access if they don't overwrite envelope sections
        foreach (var (k, v) in bodyObj)
        {
            if (k is not "header" and not "query" and not "body" and not EnvelopeMarker)
            {
                input[k] = v?.DeepClone();
            }
        }

        return input;
    }

    /// <summary>
    /// Ensures that <paramref name="body"/> is wrapped in an envelope containing non-null <c>header</c>,
    /// <c>query</c>, and <c>body</c> objects, so expressions like <c>input.header.branchId</c> never throw
    /// <see cref="NullReferenceException"/> even when no headers or query parameters were passed.
    /// </summary>
    public static JsonObject EnsureEnvelope(JsonObject? body)
    {
        if (body is not null && body.ContainsKey(EnvelopeMarker))
        {
            return body;
        }

        var input = new JsonObject();
        var bodyObj = body != null ? (JsonObject)body.DeepClone() : [];

        input[EnvelopeMarker] = true;
        input["header"] = new JsonObject();
        input["query"] = new JsonObject();
        input["body"] = bodyObj;

        foreach (var (k, v) in bodyObj)
        {
            if (k is not "header" and not "query" and not "body" and not EnvelopeMarker)
            {
                input[k] = v?.DeepClone();
            }
        }

        return input;
    }

    /// <summary>
    /// Replaces the body payload inside an existing HTTP input envelope while preserving <c>header</c> and <c>query</c>.
    /// </summary>
    public static JsonObject WithBody(JsonObject? envelope, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (envelope is null || !envelope.ContainsKey(EnvelopeMarker))
        {
            return EnsureEnvelope(body);
        }

        var clone = (JsonObject)envelope.DeepClone();
        var bodyClone = (JsonObject)body.DeepClone();
        clone["body"] = bodyClone;

        var keysToRemove = clone
            .Select(kv => kv.Key)
            .Where(k => k is not "header" and not "query" and not "body" and not EnvelopeMarker)
            .ToList();
        foreach (var k in keysToRemove)
        {
            clone.Remove(k);
        }

        foreach (var (k, v) in bodyClone)
        {
            if (k is not "header" and not "query" and not "body" and not EnvelopeMarker)
            {
                clone[k] = v?.DeepClone();
            }
        }

        return clone;
    }

    /// <summary>
    /// Extracts the body payload that should be validated against transition input schemas.
    /// If the schema explicitly defines properties named "header" or "query", the entire
    /// envelope is validated. Otherwise, the inner body object is validated.
    /// </summary>
    public static JsonObject? ExtractBody(JsonObject? input, JsonObject? schema)
    {
        if (input is null)
        {
            return null;
        }

        if (input.ContainsKey(EnvelopeMarker) && input.TryGetPropertyValue("body", out var b) && b is JsonObject bodyObj)
        {
            return bodyObj;
        }

        return input;
    }

    /// <summary>
    /// Synchronizes top-level properties of an HTTP input envelope with its inner <c>body</c> object
    /// after validation removes hidden (<c>visibleWhen == false</c>) fields.
    /// </summary>
    public static void SyncEnvelopeBody(JsonObject? input)
    {
        if (input is null || !input.ContainsKey(EnvelopeMarker) || !input.TryGetPropertyValue("body", out var b) || b is not JsonObject bodyObj)
        {
            return;
        }

        var keysToRemove = input
            .Select(kv => kv.Key)
            .Where(k => k is not "header" and not "query" and not "body" and not EnvelopeMarker && !bodyObj.ContainsKey(k))
            .ToList();
        foreach (var k in keysToRemove)
        {
            input.Remove(k);
        }
    }

    internal static JsonNode? ParseValue(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (long.TryParse(text, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(text, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        if (bool.TryParse(text, out var b))
        {
            return JsonValue.Create(b);
        }

        return JsonValue.Create(text);
    }

    internal static string ToCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var capitalizeNext = false;
        var start = true;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '-' or '_' or ' ')
            {
                capitalizeNext = true;
            }
            else if (capitalizeNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
                start = false;
            }
            else if (start)
            {
                sb.Append(char.ToLowerInvariant(c));
                start = false;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
