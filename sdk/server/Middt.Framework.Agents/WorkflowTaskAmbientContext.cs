using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Agents;

/// <summary>
/// Propagates the active <see cref="WorkflowTaskContext"/> across asynchronous AI Agent and MCP tool invocations
/// so that any <see cref="IWorkflowTask"/> or declarative tool invoked by an agent inside a workflow has access to the
/// ambient workflow instance's state, data, user, and services.
/// </summary>
public static class WorkflowTaskAmbientContext
{
    private static readonly AsyncLocal<WorkflowTaskContext?> CurrentContext = new();

    /// <summary>Gets the active <see cref="WorkflowTaskContext"/> when an agent or MCP tool is executing inside a workflow task.</summary>
    public static WorkflowTaskContext? Current => CurrentContext.Value;

    /// <summary>Pushes <paramref name="context"/> as the ambient workflow task context for the duration of the returned scope.</summary>
    public static IDisposable Enter(WorkflowTaskContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(WorkflowTaskContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentContext.Value = previous;
        }
    }
}
