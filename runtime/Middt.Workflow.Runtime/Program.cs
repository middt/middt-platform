using Microsoft.EntityFrameworkCore;
using Middt.Framework.Agents;
using Middt.Framework.Api.DependencyInjection;
using Middt.Framework.Common.Observability;
using Middt.Framework.Rebus;
using Middt.Framework.Ui.Server.DependencyInjection;
using Middt.Framework.Ui.Server.Endpoints;
using Middt.Framework.Workflow.DependencyInjection;
using Middt.Framework.Workflow.Endpoints;
using Middt.Framework.Workflow.Runtime;
using Middt.Workflow.Runtime;
using Middt.Workflow.Runtime.Handlers;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.AddMiddtOpenTelemetry();
builder.AddMiddtApi(options => options.Title = "Middt Workflow Runtime");

// Optional infrastructure: enabled only when a connection string is configured (Docker Compose injects them).
if (builder.Configuration.GetConnectionString("redis") is { Length: > 0 } redis)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redis); // L2 for CacheTask / RefreshTokenStore
}

// Messaging (Rebus): RabbitMQ when a connection string is configured, otherwise the in-memory transport for local runs.
var messaging = builder.Configuration.GetConnectionString("messaging");
builder.Services.AddMiddtRebus(
    settings =>
    {
        builder.Configuration.GetSection(RebusSettings.SectionName).Bind(settings);
        settings.ConnectionString = messaging;
        settings.UseInMemoryTransport = string.IsNullOrEmpty(messaging);
    },
    rebus => rebus.Subscribe<RebusWorkflowMessage>());
builder.Services.AddMiddtRebusHandlersFromAssemblyOf<WorkflowNotificationHandler>();
builder.Services.AddHostedService<RabbitMqQueueInitializer>();

var dbConnectionString = builder.Configuration.GetConnectionString("WorkflowDb");

// AI Agents (Microsoft Agent Framework): JSON agent definitions deployed via /api/agents/catalog or optional ./Agents folder.
builder.Services.AddMiddtAgents(
    builder.Configuration,
    agents =>
    {
        var agentsDir = Path.Combine(builder.Environment.ContentRootPath, "Agents");
        if (Directory.Exists(agentsDir))
        {
            agents.AddAgentsFromDirectory("Agents");
        }
    });

// Workflow engine: definitions are persisted in wf."WorkflowDefinitions" and deployed via POST /api/workflows/definitions.
// Optional ./Workflows directory is loaded only when present.
builder.Services.AddMiddtWorkflow(workflow =>
{
    workflow.UseDbContext(db => db.UseNpgsql(dbConnectionString));
    workflow.AutoCreateSchema = true;
    workflow.ExposeErrorDetails = builder.Environment.IsDevelopment();
    var workflowsDir = Path.Combine(builder.Environment.ContentRootPath, "Workflows");
    if (Directory.Exists(workflowsDir))
    {
        workflow.AddDefinitionsFromDirectory("Workflows");
    }
});

// Server-driven UI: JSON layouts (pages and workflow state views) in the "ui" schema, deployed via POST /api/ui/layouts/seed.
// Optional ./Layouts directory is loaded only when present.
builder.Services.AddMiddtUi(ui =>
{
    ui.UseDbContext(db => db.UseNpgsql(dbConnectionString));
    ui.AutoCreateSchema = true;
    var layoutsDir = Path.Combine(builder.Environment.ContentRootPath, "Layouts");
    if (Directory.Exists(layoutsDir))
    {
        ui.AddLayoutsFromDirectory("Layouts");
    }
});
builder.Services.AddScoped<IWorkflowViewRenderer, WorkflowLayoutRenderer>();

var app = builder.Build();

app.UseMiddtApi();
app.MapMiddtAgents("api/agents");
app.MapMiddtWorkflows("api/workflows");
app.MapMiddtUi("api/ui");

app.Run();

/// <summary>Entry point marker for <c>WebApplicationFactory</c> based integration tests.</summary>
public partial class Program;
