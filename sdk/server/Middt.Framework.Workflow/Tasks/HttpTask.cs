using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;

namespace Middt.Framework.Workflow.Tasks;

/// <summary>
/// <c>http</c> / <c>http-request</c>: calls an external or internal HTTP service endpoint.
/// <code>
/// {
///   "method": "POST",
///   "url": "https://api.example.com/v1/customers/{{ data.customerId }}/charge",
///   "queryParams": { "notify": true, "source": "{{ data.channel }}" },
///   "headers": {
///     "Authorization": "Bearer {{ data.apiKey }}",
///     "X-Correlation-Id": "{{ data.correlationId }}"
///   },
///   "contentType": "application/json",
///   "body": {
///     "amount": "{{ data.totalAmount }}",
///     "currency": "TRY"
///   },
///   "acceptedStatusCodes": [200, 201],
///   "failOnError": true,
///   "timeoutSeconds": 30,
///   "client": "middt-workflow",
///   "select": ["body.transactionId", "statusCode"],
///   "into": "paymentResponse"
/// }
/// </code>
/// Use <c>select</c> (or <c>output</c>) as a field array (<c>["referenceCode", "statusCode"]</c>) or a mapping object
/// (<c>{ "paymentTxId": "result.body.referenceCode", "gatewayCode": "result.statusCode" }</c>) to write only selected parts
/// of the HTTP response into instance data.
/// Placeholders are not allowed in the scheme/host part of the URL and are URL-encoded elsewhere (SSRF protection).
/// </summary>
public sealed class HttpTask : IWorkflowTask
{
    public const string DefaultClientName = "middt-workflow";

    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        if (task.Config["url"]?.GetValue<string>() is not { } url)
        {
            yield return "http: 'url' is required.";
            yield break;
        }

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var authorityEnd = schemeEnd < 0 ? -1 : url.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        var authority = schemeEnd < 0 ? url : url[..(authorityEnd < 0 ? url.Length : authorityEnd)];
        if (schemeEnd < 0 || authority.Contains("{{", StringComparison.Ordinal))
        {
            yield return "http: 'url' must be absolute and must not contain placeholders in the scheme or host.";
        }

        if (task.Config["select"] is not null && task.Config["output"] is not null)
        {
            yield return "http: specify either 'select' or 'output', not both.";
        }

        if ((task.Config["select"] ?? task.Config["output"]) is { } selectNode)
        {
            foreach (var selectError in ValidateSelectOrOutput(selectNode, expressions))
            {
                yield return selectError;
            }
        }

        foreach (var error in expressions.ValidateTemplate(task.Config["url"])
                     .Concat(expressions.ValidateTemplate(task.Config["body"]))
                     .Concat(expressions.ValidateTemplate(task.Config["headers"]))
                     .Concat(expressions.ValidateTemplate(task.Config["queryParams"]))
                     .Concat(expressions.ValidateTemplate(task.Config["query"])))
        {
            yield return "http: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var method = new HttpMethod(config["method"]?.GetValue<string>()?.ToUpperInvariant() ?? "GET");
        var clientName = config["client"]?.GetValue<string>() ?? DefaultClientName;
        var client = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        var wfOptions = context.Services.GetService<IOptions<MiddtWorkflowOptions>>()?.Value ?? new MiddtWorkflowOptions();
        var baseUri = RenderUrl(config["url"]!.GetValue<string>(), context);

        var allowedHosts = wfOptions.AllowedHttpHosts;
        if (allowedHosts.Count > 0 && !allowedHosts.Contains(baseUri.Host))
        {
            throw new WorkflowTaskException($"http: host '{baseUri.Host}' is not in MiddtWorkflowOptions.AllowedHttpHosts.");
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new WorkflowTaskException($"http: scheme '{baseUri.Scheme}' is not supported.");
        }

        var fullUri = AppendQueryParams(baseUri, config, context);

        using var request = new HttpRequestMessage(method, fullUri);

        // Apply request headers
        var headersNode = context.Render("headers") as JsonObject;
        if (headersNode is not null)
        {
            foreach (var (name, value) in headersNode)
            {
                if (value is null)
                {
                    continue;
                }

                var headerStr = value switch
                {
                    JsonValue jv when jv.TryGetValue<string>(out var s) => s,
                    JsonValue jv => jv.ToString(),
                    _ => value.ToJsonString()
                };

                request.Headers.TryAddWithoutValidation(name, headerStr);
            }
        }

        if (!string.IsNullOrEmpty(context.IdempotencyKey) && !request.Headers.Contains("Idempotency-Key"))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", context.IdempotencyKey);
        }

        // Apply request body
        var contentType = config["contentType"]?.GetValue<string>() ?? "application/json";
        if (config["body"] is not null)
        {
            var renderedBody = context.Render("body");
            string bodyString;

            if (renderedBody is JsonValue jv && jv.TryGetValue<string>(out var strVal))
            {
                bodyString = strVal;
            }
            else
            {
                bodyString = renderedBody?.ToJsonString() ?? "null";
            }

            request.Content = new StringContent(bodyString, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var timeoutSec = config["timeoutSeconds"]?.GetValue<int>() ?? 30;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var failOnError = config["failOnError"]?.GetValue<bool>() ?? true;
        var into = config["into"]?.GetValue<string>();
        var selectNode = config["select"] ?? config["output"];

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (!failOnError)
        {
            if (!string.IsNullOrEmpty(into) || selectNode is not null)
            {
                WriteResponse(context, into, selectNode, new JsonObject
                {
                    ["statusCode"] = 0,
                    ["isSuccess"] = false,
                    ["error"] = ex.Message,
                    ["body"] = null,
                    ["headers"] = new JsonObject(),
                });
            }
            return;
        }

        using (response)
        {
            var accepted = config["acceptedStatusCodes"] switch
            {
                JsonArray codes => codes.Select(c => c!.GetValue<int>()).Contains((int)response.StatusCode),
                JsonValue singleCode when singleCode.TryGetValue<int>(out var sc) => (int)response.StatusCode == sc,
                _ => response.IsSuccessStatusCode
            };

            if (!accepted && failOnError)
            {
                throw new WorkflowTaskException(
                    $"HTTP {method} {fullUri.GetLeftPart(UriPartial.Path)} returned {(int)response.StatusCode}.",
                    statusCode: (int)response.StatusCode);
            }

            if (!string.IsNullOrEmpty(into) || selectNode is not null)
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                JsonNode? body;
                try
                {
                    body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = JsonValue.Create(text);
                }

                var responseHeaders = new JsonObject();
                foreach (var header in response.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }
                foreach (var header in response.Content.Headers)
                {
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                }

                var resultObj = new JsonObject
                {
                    ["statusCode"] = (int)response.StatusCode,
                    ["isSuccess"] = response.IsSuccessStatusCode,
                    ["body"] = body,
                    ["headers"] = responseHeaders
                };

                WriteResponse(context, into, selectNode, resultObj);
            }
        }
    }

    private static IEnumerable<string> ValidateSelectOrOutput(JsonNode selectNode, WorkflowExpressionEngine expressions)
    {
        if (selectNode is JsonArray arr)
        {
            if (arr.Count == 0)
            {
                yield return "http: 'select' array must contain at least one field path.";
                yield break;
            }

            foreach (var item in arr)
            {
                if (item is not JsonValue val || !val.TryGetValue<string>(out var path) || string.IsNullOrWhiteSpace(path))
                {
                    yield return "http: 'select' array items must be non-empty field paths.";
                }
            }

            yield break;
        }

        if (selectNode is JsonObject mapObj)
        {
            if (mapObj.Count == 0)
            {
                yield return "http: 'select'/'output' object must contain at least one mapping.";
                yield break;
            }

            foreach (var (targetPath, exprNode) in mapObj)
            {
                if (string.IsNullOrWhiteSpace(targetPath)
                    || exprNode is not JsonValue val
                    || !val.TryGetValue<string>(out var exprText)
                    || string.IsNullOrWhiteSpace(exprText))
                {
                    yield return $"http: mapping for '{targetPath}' must be a non-empty expression or field path.";
                    continue;
                }

                if (!IsSimpleDotPath(exprText) && expressions.Validate(NormalizeResponseExpression(exprText)) is { } exprError)
                {
                    yield return $"http: mapping '{targetPath}': {exprError}";
                }
            }

            yield break;
        }

        yield return "http: 'select'/'output' must be a field array or a mapping object.";
    }

    private static string NormalizeResponseExpression(string expression) =>
        System.Text.RegularExpressions.Regex.Replace(expression, @"\bresult\.", "target.");

    private static void WriteResponse(WorkflowTaskContext context, string? into, JsonNode? selectNode, JsonObject resultObj)
    {
        if (selectNode is null)
        {
            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, resultObj);
            }

            return;
        }

        var bodyObj = resultObj["body"] as JsonObject;

        if (selectNode is JsonArray fieldList)
        {
            var projected = new JsonObject();
            foreach (var item in fieldList)
            {
                if (item?.GetValue<string>() is not { Length: > 0 } fieldPath)
                {
                    continue;
                }

                var val = JsonPath.Get(resultObj, fieldPath)
                          ?? (bodyObj is not null ? JsonPath.Get(bodyObj, fieldPath) : null);
                JsonPath.Set(projected, fieldPath, val?.DeepClone());
            }

            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, projected);
            }
            else
            {
                JsonPath.Merge(context.Data, projected);
            }

            return;
        }

        if (selectNode is JsonObject mapObj)
        {
            var targetPayload = (JsonObject)resultObj.DeepClone();
            if (bodyObj is not null)
            {
                foreach (var (k, v) in bodyObj)
                {
                    if (!targetPayload.ContainsKey(k))
                    {
                        targetPayload[k] = v?.DeepClone();
                    }
                }
            }

            var responseScope = new ExpressionScope
            {
                Data = context.Scope.Data,
                Input = context.Scope.Input,
                Args = context.Scope.Args,
                Result = targetPayload,
                Child = context.Scope.Child,
                User = context.Scope.User,
                Instance = context.Scope.Instance,
                Item = targetPayload,
                Target = targetPayload,
                Index = context.Scope.Index,
                Now = context.Scope.Now,
            };

            var projected = new JsonObject();
            foreach (var (targetPath, sourceExprNode) in mapObj)
            {
                if (sourceExprNode?.GetValue<string>() is not { Length: > 0 } sourceExpr)
                {
                    continue;
                }

                JsonNode? val;
                if (IsSimpleDotPath(sourceExpr))
                {
                    var normalizedPath = sourceExpr.StartsWith("result.", StringComparison.Ordinal)
                        ? sourceExpr["result.".Length..]
                        : sourceExpr.StartsWith("target.", StringComparison.Ordinal)
                            ? sourceExpr["target.".Length..]
                            : sourceExpr;

                    val = (JsonPath.Get(resultObj, normalizedPath)
                           ?? JsonPath.Get(targetPayload, normalizedPath))?.DeepClone()
                          ?? context.Expressions.EvaluateToNode(NormalizeResponseExpression(sourceExpr), responseScope);
                }
                else
                {
                    val = context.Expressions.EvaluateToNode(NormalizeResponseExpression(sourceExpr), responseScope);
                }

                if (!string.IsNullOrEmpty(into))
                {
                    JsonPath.Set(projected, targetPath, val?.DeepClone());
                }
                else
                {
                    JsonPath.Set(context.Data, targetPath, val?.DeepClone());
                }
            }

            if (!string.IsNullOrEmpty(into))
            {
                JsonPath.Set(context.Data, into, projected);
            }
        }
    }

    private static bool IsSimpleDotPath(string text)
    {
        foreach (var ch in text)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return text.Length > 0
               && !string.Equals(text, "true", StringComparison.Ordinal)
               && !string.Equals(text, "false", StringComparison.Ordinal)
               && !string.Equals(text, "null", StringComparison.Ordinal)
               && !string.Equals(text, "now", StringComparison.Ordinal);
    }

    private static Uri AppendQueryParams(Uri baseUri, JsonObject config, WorkflowTaskContext context)
    {
        var queryParamsNode = context.Render("queryParams") as JsonObject
                              ?? context.Render("query") as JsonObject;

        if (queryParamsNode is null || queryParamsNode.Count == 0)
        {
            return baseUri;
        }

        var sb = new StringBuilder();
        sb.Append(baseUri.GetLeftPart(UriPartial.Path));

        var existingQuery = baseUri.Query.TrimStart('?');
        var isFirst = true;

        if (!string.IsNullOrEmpty(existingQuery))
        {
            sb.Append('?').Append(existingQuery);
            isFirst = false;
        }

        foreach (var (paramKey, paramVal) in queryParamsNode)
        {
            if (paramVal is null)
            {
                continue;
            }

            sb.Append(isFirst ? '?' : '&');
            isFirst = false;

            sb.Append(Uri.EscapeDataString(paramKey));
            sb.Append('=');

            var valString = paramVal switch
            {
                JsonValue val when val.TryGetValue<string>(out var s) => s,
                JsonValue val => val.ToString(),
                _ => paramVal.ToJsonString()
            };

            sb.Append(Uri.EscapeDataString(valString));
        }

        if (!string.IsNullOrEmpty(baseUri.Fragment))
        {
            sb.Append(baseUri.Fragment);
        }

        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    private static Uri RenderUrl(string template, WorkflowTaskContext context)
    {
        // Each placeholder value is escaped: data cannot inject path segments, query parameters or a different host.
        var rendered = ExpressionEvaluator.PlaceholderRegex().Replace(template, m =>
            Uri.EscapeDataString(Convert.ToString(context.Expressions.Evaluate(m.Groups[1].Value, context.Scope), CultureInfo.InvariantCulture) ?? string.Empty));
        return new Uri(rendered, UriKind.Absolute);
    }
}
