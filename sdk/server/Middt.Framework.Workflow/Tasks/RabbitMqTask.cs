using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Diagnostics;
using Middt.Framework.Workflow.Expressions;
using RabbitMQ.Client;

namespace Middt.Framework.Workflow.Tasks;

/// <summary>
/// Abstraction for publishing messages to RabbitMQ, allowing custom DI implementations, pooling, or test mocks.
/// </summary>
public interface IWorkflowRabbitMqPublisher
{
    /// <summary>Publishes a message to RabbitMQ.</summary>
    Task PublishAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        IDictionary<string, object?>? headers = null,
        string? contentType = "application/json",
        bool persistent = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>rabbitmq</c> / <c>rabbit-publish</c>: publishes a message or event to a RabbitMQ exchange or queue.
/// <code>
/// {
///   "exchange": "orders.topic",
///   "routingKey": "order.created.{{ data.orderId }}",
///   "payload": {
///     "orderId": "{{ data.orderId }}",
///     "customerId": "{{ data.customerId }}",
///     "amount": "{{ data.totalAmount }}"
///   },
///   "headers": {
///     "correlationId": "{{ data.correlationId }}"
///   },
///   "persistent": true,
///   "into": "publishResult"
/// }
/// </code>
/// </summary>
public sealed class RabbitMqTask : IWorkflowTask
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IConnection> SharedConnections = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim ConnectionGate = new(1, 1);

    public IEnumerable<string> Validate(TaskDefinition task, WorkflowExpressionEngine expressions)
    {
        var config = task.Config;
        var hasExchange = config["exchange"]?.GetValue<string>() is not null;
        var hasRoutingKey = (config["routingKey"] ?? config["queue"])?.GetValue<string>() is not null;

        if (!hasExchange && !hasRoutingKey)
        {
            yield return "rabbitmq: either 'exchange' or 'routingKey' (or 'queue') is required.";
        }

        var rawPayload = config["payload"] ?? config["message"] ?? config["body"] ?? config["input"] ?? config["data"];
        if (rawPayload is null)
        {
            yield return "rabbitmq: 'payload' (or 'message' / 'body') is required.";
        }

        foreach (var error in expressions.ValidateTemplate(config["exchange"])
                     .Concat(expressions.ValidateTemplate(config["routingKey"]))
                     .Concat(expressions.ValidateTemplate(config["queue"]))
                     .Concat(expressions.ValidateTemplate(rawPayload))
                     .Concat(expressions.ValidateTemplate(config["headers"])))
        {
            yield return "rabbitmq: " + error;
        }
    }

    public async Task ExecuteAsync(WorkflowTaskContext context, CancellationToken cancellationToken)
    {
        var config = context.Config;
        var exchange = context.RenderText("exchange") ?? string.Empty;
        var routingKey = context.RenderText("routingKey")
                         ?? context.RenderText("queue")
                         ?? string.Empty;

        var rawPayload = config["payload"] ?? config["message"] ?? config["body"] ?? config["input"] ?? config["data"];
        var payloadNode = context.Expressions.Render(rawPayload, context.Scope);

        string payloadString;
        if (payloadNode is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            payloadString = s;
        }
        else
        {
            payloadString = payloadNode?.ToJsonString() ?? "{}";
        }

        var bodyBytes = (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(payloadString);
        var contentType = config["contentType"]?.GetValue<string>() ?? "application/json";
        var persistent = config["persistent"]?.GetValue<bool>() ?? true;

        var headersNode = context.Render("headers") as JsonObject;
        Dictionary<string, object?>? headers = null;
        if (headersNode is not null && headersNode.Count > 0)
        {
            headers = [];
            foreach (var (key, val) in headersNode)
            {
                if (val is null)
                {
                    headers[key] = null;
                }
                else if (val is JsonValue v && v.TryGetValue<string>(out var strVal))
                {
                    headers[key] = strVal;
                }
                else if (val is JsonValue numVal && numVal.TryGetValue<long>(out var lVal))
                {
                    headers[key] = lVal;
                }
                else
                {
                    headers[key] = val.ToJsonString();
                }
            }
        }

        var destination = !string.IsNullOrEmpty(exchange) && !string.IsNullOrEmpty(routingKey)
            ? $"{exchange}/{routingKey}"
            : (!string.IsNullOrEmpty(routingKey) ? routingKey : exchange);
        using var activity = WorkflowDiagnostics.ActivitySource.StartActivity($"messaging.rabbitmq.publish {destination}", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation", "publish");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.body.size", bodyBytes.Length);

        if (activity?.Id is { Length: > 0 } traceparent)
        {
            headers ??= [];
            headers.TryAdd("traceparent", traceparent);
        }

        // 1. Check if custom IWorkflowRabbitMqPublisher is registered in DI
        var customPublisher = context.Services.GetService<IWorkflowRabbitMqPublisher>();
        if (customPublisher is not null)
        {
            await customPublisher.PublishAsync(exchange, routingKey, bodyBytes, headers, contentType, persistent, cancellationToken);
            SetResult(context, exchange, routingKey);
            return;
        }

        // 2. Direct publish via RabbitMQ.Client
        await PublishDirectAsync(context, exchange, routingKey, bodyBytes, headers, contentType, persistent, cancellationToken);
        SetResult(context, exchange, routingKey);
    }

    private static async Task PublishDirectAsync(
        WorkflowTaskContext context,
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        Dictionary<string, object?>? headers,
        string contentType,
        bool persistent,
        CancellationToken cancellationToken)
    {
        var config = context.Config;
        var connectionString = context.RenderText("connectionString");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var appConfig = context.Services.GetService<IConfiguration>();
            connectionString = appConfig?.GetConnectionString("RabbitMQ")
                               ?? appConfig?["RabbitMQ:ConnectionString"]
                               ?? appConfig?["RebusSettings:ConnectionString"];
        }

        IConnectionFactory? factory = context.Services.GetService<IConnectionFactory>();
        string connectionKey;
        if (factory is not null)
        {
            connectionKey = $"di:{factory.GetHashCode()}";
        }
        else if (!string.IsNullOrWhiteSpace(connectionString))
        {
            connectionKey = $"uri:{connectionString}";
            factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        }
        else
        {
            var host = context.RenderText("host") ?? "localhost";
            var port = config["port"]?.GetValue<int>() ?? 5672;
            var user = context.RenderText("user") ?? context.RenderText("username") ?? "guest";
            var pass = context.RenderText("pass") ?? context.RenderText("password") ?? "guest";
            var vhost = context.RenderText("vhost") ?? context.RenderText("virtualHost") ?? "/";

            connectionKey = $"host:{host}:{port}:{vhost}:{user}";
            factory = new ConnectionFactory
            {
                HostName = host,
                Port = port,
                UserName = user,
                Password = pass,
                VirtualHost = vhost
            };
        }

        var connection = await GetOrCreateConnectionAsync(connectionKey, factory, cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        var basicProperties = new BasicProperties
        {
            ContentType = contentType,
            DeliveryMode = persistent ? DeliveryModes.Persistent : DeliveryModes.Transient
        };

        if (headers is not null && headers.Count > 0)
        {
            basicProperties.Headers = headers;
        }

        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: basicProperties,
            body: body,
            cancellationToken: cancellationToken);
    }

    private static async Task<IConnection> GetOrCreateConnectionAsync(
        string connectionKey,
        IConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        if (SharedConnections.TryGetValue(connectionKey, out var existing) && existing.IsOpen)
        {
            return existing;
        }

        await ConnectionGate.WaitAsync(cancellationToken);
        try
        {
            if (SharedConnections.TryGetValue(connectionKey, out existing))
            {
                if (existing.IsOpen)
                {
                    return existing;
                }

                SharedConnections.TryRemove(connectionKey, out _);
                try
                {
                    await existing.DisposeAsync();
                }
                catch
                {
                    // Ignore disposal errors on stale closed connection.
                }
            }

            var created = await factory.CreateConnectionAsync(cancellationToken);
            SharedConnections[connectionKey] = created;
            return created;
        }
        finally
        {
            ConnectionGate.Release();
        }
    }

    private static void SetResult(WorkflowTaskContext context, string exchange, string routingKey)
    {
        if (context.Config["into"]?.GetValue<string>() is { Length: > 0 } into)
        {
            JsonPath.Set(context.Data, into, new JsonObject
            {
                ["published"] = true,
                ["exchange"] = exchange,
                ["routingKey"] = routingKey
            });
        }
    }
}
