using Middt.Framework.Workflow.Definitions;
using RabbitMQ.Client;

namespace Middt.Demo.Api;

/// <summary>
/// Pre-declares queues referenced by workflow definitions (or configured under <c>RabbitMq:Queues</c>) in RabbitMQ so that
/// messaging workflows can send and defer messages.
/// </summary>
public sealed class RabbitMqQueueInitializer(
    IConfiguration configuration,
    WorkflowRegistry registry,
    ILogger<RabbitMqQueueInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var messaging = configuration.GetConnectionString("messaging");
        if (string.IsNullOrWhiteSpace(messaging))
        {
            return;
        }

        var queues = DiscoverQueues();
        if (queues.Count == 0)
        {
            return;
        }

        // Retry a few times in case RabbitMQ is still initializing during compose startup
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(messaging) };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                foreach (var queue in queues)
                {
                    await channel.QueueDeclareAsync(
                        queue: queue,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        cancellationToken: stoppingToken);
                }

                logger.LogInformation("RabbitMQ queues declared successfully: {Queues}", string.Join(", ", queues));
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "RabbitMQ queue initialization attempt {Attempt}/10 failed; retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private HashSet<string> DiscoverQueues()
    {
        var queues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configured in configuration.GetSection("RabbitMq:Queues").Get<string[]>() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                queues.Add(configured.Trim());
            }
        }

        foreach (var wf in registry.Latest)
        {
            var tasks = wf.Start.OnExecute
                .Concat(wf.States.SelectMany(s => s.OnEntry.Concat(s.OnExit).Concat(s.Transitions.SelectMany(t => t.OnExecute))))
                .Concat(wf.SharedTransitions.SelectMany(t => t.OnExecute));
            foreach (var task in tasks)
            {
                if (task.Task is "rabbitmq" or "rabbit" or "rabbit-publish" or "rabbitPublish"
                    && task.Config["queue"]?.GetValue<string>() is { Length: > 0 } q
                    && !q.Contains("{{", StringComparison.Ordinal))
                {
                    queues.Add(q);
                }
            }
        }

        return queues;
    }
}
