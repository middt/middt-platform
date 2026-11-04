using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Middt.Framework.Workflow;

namespace Middt.Framework.Agents;

/// <summary>
/// Dependency injection extensions for registering the Microsoft Agent Framework (<c>Microsoft.Agents.AI</c>),
/// Model Context Protocol (<see cref="IMiddtMcpClient"/>), and their workflow tasks (<see cref="AiAgentTask"/>,
/// <see cref="McpTask"/>) in Middt applications.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMiddtAgentRegistry"/>, <see cref="IMiddtMcpClient"/>, built-in Middt workflow <c>AIFunction</c> tools,
    /// <see cref="AiAgentTask"/>, and <see cref="McpTask"/> bound to the <c>AgentSettings</c> configuration section.
    /// </summary>
    public static IServiceCollection AddMiddtAgents(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MiddtAgentOptions>? configure = null) =>
        services.AddMiddtAgents(settings => configuration.GetSection(AgentSettings.SectionName).Bind(settings), configure);

    /// <inheritdoc cref="AddMiddtAgents(IServiceCollection, IConfiguration, Action{MiddtAgentOptions}?)"/>
    public static IServiceCollection AddMiddtAgents(
        this IServiceCollection services,
        Action<AgentSettings>? configureSettings = null,
        Action<MiddtAgentOptions>? configure = null)
    {
        var options = new MiddtAgentOptions();
        configure?.Invoke(options);

        var optionsBuilder = services.AddOptions<AgentSettings>();
        if (configureSettings is not null)
        {
            optionsBuilder.Configure(configureSettings);
        }

        services.AddHttpClient(MiddtMcpClient.DefaultHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton(options);
        services.TryAddSingleton<IMiddtMcpClient, MiddtMcpClient>();
        services.TryAddSingleton<IMiddtAgentRegistry, MiddtAgentRegistry>();
        services.AddSingleton<Middt.Framework.Workflow.Runtime.IWorkflowCatalogInvalidationHandler>(sp =>
            sp.GetRequiredService<IMiddtAgentRegistry>() as Middt.Framework.Workflow.Runtime.IWorkflowCatalogInvalidationHandler
            ?? new NoopCatalogInvalidationHandler());
        services.TryAddTransient<AiAgentTask>();
        services.TryAddTransient<McpTask>();
        services.Configure<MiddtWorkflowOptions>(o => o.AddAgentTasks());

        return services;
    }

    private sealed class NoopCatalogInvalidationHandler : Middt.Framework.Workflow.Runtime.IWorkflowCatalogInvalidationHandler
    {
        public void OnCatalogInvalidated(string catalog)
        {
        }
    }

    /// <summary>
    /// Registers the Microsoft Agent Framework (<see cref="AiAgentTask"/>) and Model Context Protocol (<see cref="McpTask"/>)
    /// workflow tasks on <see cref="MiddtWorkflowOptions"/>.
    /// </summary>
    public static MiddtWorkflowOptions AddAgentTasks(this MiddtWorkflowOptions options) => options
        .AddTask<AiAgentTask>("agent")
        .AddTask<AiAgentTask>("ai-agent")
        .AddTask<AiAgentTask>("aiAgent")
        .AddTask<AiAgentTask>("ai")
        .AddTask<AiAgentTask>("llm")
        .AddTask<McpTask>("mcp")
        .AddTask<McpTask>("mcp-call")
        .AddTask<McpTask>("mcpCall")
        .AddTask<McpTask>("mcp-tool")
        .AddTask<McpTask>("mcpTool")
        .AddTask<McpTask>("mcp-resource")
        .AddTask<McpTask>("mcpResource")
        .AddTask<McpTask>("mcp-prompt")
        .AddTask<McpTask>("mcpPrompt");
}
