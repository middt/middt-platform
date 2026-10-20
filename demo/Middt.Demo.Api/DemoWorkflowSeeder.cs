using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Runtime;

namespace Middt.Demo.Api;

/// <summary>
/// Seeds initial workflow instances from declarative JSON files in <c>Seeds/*.json</c> when the target workflow has no
/// existing instances, so <c>x-lookup</c> dropdowns and Server-Driven UI grids have initial JSON records out of the box.
/// </summary>
public sealed class DemoWorkflowSeeder(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<DemoWorkflowSeeder> logger) : IHostedService
{
    private static readonly WorkflowUser SeedUser = new()
    {
        Id = "test@test.com",
        Name = "test@test.com",
        Roles = ["manager", "idp-admin"],
    };

    private sealed class SeedEntry
    {
        public string Workflow { get; set; } = string.Empty;
        public string? Key { get; set; }
        public JsonObject? Data { get; set; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seedsDir = Path.Combine(environment.ContentRootPath, "Seeds");
            if (!Directory.Exists(seedsDir))
            {
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();

            foreach (var file in Directory.EnumerateFiles(seedsDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<SeedEntry>>(json, WorkflowJson.Options) ?? [];
                foreach (var group in entries.Where(e => !string.IsNullOrWhiteSpace(e.Workflow)).GroupBy(e => e.Workflow, StringComparer.Ordinal))
                {
                    if (await db.Instances.AsNoTracking().AnyAsync(i => i.WorkflowKey == group.Key, cancellationToken))
                    {
                        continue;
                    }

                    foreach (var entry in group)
                    {
                        await engine.StartAsync(
                            entry.Workflow,
                            new StartWorkflowRequest
                            {
                                Key = entry.Key,
                                Data = entry.Data,
                            },
                            SeedUser,
                            cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipping initial demo workflow seed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
