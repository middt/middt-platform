using Middt.Framework.Rebus;
using Rebus.Handlers;

namespace Middt.Workflow.Runtime.Handlers;

/// <summary>Subscriber for messages published by workflows.</summary>
public sealed partial class WorkflowNotificationHandler(ILogger<WorkflowNotificationHandler> logger) : IHandleMessages<RebusWorkflowMessage>
{
    public Task Handle(RebusWorkflowMessage message)
    {
        LogNotification(logger, message.Type, message.Payload);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow notification {Type}: {Payload}")]
    private static partial void LogNotification(ILogger logger, string type, string payload);
}
