using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Middt.Demo.Workflows;

/// <summary>
/// Deploys all JSON AI agents/tools/MCP servers (<c>Agents/**</c>), workflow definitions (<c>Workflows/*.json</c>),
/// server-driven UI layouts (<c>Layouts/*.json</c>), and initial demo workflow instances (<c>Seeds/*.json</c>)
/// into a running <c>Middt.Workflow.Runtime</c> instance over HTTP, then stops the host cleanly.
/// </summary>
public sealed class DemoFlowDeployer(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<DemoFlowDeployer> logger) : BackgroundService
{
    private static readonly JsonDocumentOptions JsonDocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var runtimeUrl = (configuration["Runtime:Url"] ?? "http://localhost:8180").TrimEnd('/');
            var identityUrl = (configuration["Identity:Url"] ?? "http://localhost:8083").TrimEnd('/');
            var clientId = configuration["Identity:ClientId"] ?? "middt-postman";
            var clientSecret = configuration["Identity:ClientSecret"] ?? string.Empty;
            var username = configuration["Identity:Username"] ?? "test@test.com";
            var password = configuration["Identity:Password"] ?? "test";

            using var http = httpClientFactory.CreateClient("Runtime");

            await WaitForServiceAsync(http, $"{runtimeUrl}/alive", "Middt.Workflow.Runtime", stoppingToken);
            await WaitForServiceAsync(http, $"{identityUrl}/health", "Middt.Identity", stoppingToken);

            var accessToken = await AcquireAdminTokenAsync(http, identityUrl, clientId, clientSecret, username, password, stoppingToken);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            await DeployAgentCatalogAsync(http, runtimeUrl, stoppingToken);
            await DeployWorkflowsAsync(http, runtimeUrl, stoppingToken);
            await DeployLayoutsAsync(http, runtimeUrl, stoppingToken);
            await SeedInitialInstancesAsync(http, runtimeUrl, stoppingToken);

            logger.LogInformation("All demo AI agents, workflows, layouts, and initial instances deployed successfully.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Failed to deploy demo workflows and layouts to Middt.Workflow.Runtime.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task WaitForServiceAsync(HttpClient http, string healthUrl, string serviceName, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 60; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(healthUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("{Service} is ready at {Url}.", serviceName, healthUrl);
                    return;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Service still starting up.
            }

            logger.LogInformation("Waiting for {Service} ({Url}) - attempt {Attempt}/60...", serviceName, healthUrl, attempt);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException($"Timed out waiting for {serviceName} at {healthUrl}.");
    }

    private async Task<string> AcquireAdminTokenAsync(
        HttpClient http,
        string identityUrl,
        string clientId,
        string clientSecret,
        string username,
        string password,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
                ["scope"] = "openid profile email roles offline_access api",
                ["client_id"] = clientId,
            };

            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                form["client_secret"] = clientSecret;
            }

            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync($"{identityUrl}/connect/token", content, ct);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
                var token = payload?["access_token"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            var errorText = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Token request attempt {Attempt}/30 returned {Status}: {Error}", attempt, (int)response.StatusCode, errorText);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new InvalidOperationException($"Could not acquire admin access token from {identityUrl}/connect/token.");
    }

    private async Task DeployAgentCatalogAsync(HttpClient http, string runtimeUrl, CancellationToken ct)
    {
        var agentsDir = Path.Combine(environment.ContentRootPath, "Agents");
        if (!Directory.Exists(agentsDir))
        {
            return;
        }

        var agentsArray = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(agentsDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct), documentOptions: JsonDocOptions);
            if (node is JsonObject obj)
            {
                if (obj["name"] is null)
                {
                    obj["name"] = Path.GetFileNameWithoutExtension(file);
                }

                agentsArray.Add(obj);
            }
        }

        var toolsArray = new JsonArray();
        var toolsDir = Path.Combine(agentsDir, "Tools");
        if (Directory.Exists(toolsDir))
        {
            foreach (var file in Directory.EnumerateFiles(toolsDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct), documentOptions: JsonDocOptions);
                if (node is JsonObject obj)
                {
                    if (obj["name"] is null)
                    {
                        obj["name"] = Path.GetFileNameWithoutExtension(file);
                    }

                    toolsArray.Add(obj);
                }
            }
        }

        var mcpServersArray = new JsonArray();
        var mcpDir = Path.Combine(agentsDir, "McpServers");
        if (Directory.Exists(mcpDir))
        {
            foreach (var file in Directory.EnumerateFiles(mcpDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct), documentOptions: JsonDocOptions);
                if (node is JsonObject obj)
                {
                    if (obj["name"] is null)
                    {
                        obj["name"] = Path.GetFileNameWithoutExtension(file);
                    }

                    mcpServersArray.Add(obj);
                }
            }
        }

        var catalogPayload = new JsonObject
        {
            ["agents"] = agentsArray,
            ["tools"] = toolsArray,
            ["mcpServers"] = mcpServersArray,
        };

        using var content = new StringContent(catalogPayload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{runtimeUrl}/api/agents/catalog", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Agent catalog deployment failed ({(int)response.StatusCode}): {errorBody}");
        }

        logger.LogInformation(
            "Deployed {AgentCount} AI agents, {ToolCount} tools, and {McpCount} MCP servers to {Url}.",
            agentsArray.Count,
            toolsArray.Count,
            mcpServersArray.Count,
            runtimeUrl);
    }

    private async Task DeployWorkflowsAsync(HttpClient http, string runtimeUrl, CancellationToken ct)
    {
        var workflowsDir = Path.Combine(environment.ContentRootPath, "Workflows");
        var files = Directory.EnumerateFiles(workflowsDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var array = new JsonArray();
        foreach (var file in files)
        {
            var raw = await File.ReadAllTextAsync(file, ct);
            var node = JsonNode.Parse(raw, documentOptions: JsonDocOptions)
                ?? throw new InvalidOperationException($"Workflow file '{file}' is empty.");
            array.Add(node);
        }

        using var content = new StringContent(array.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{runtimeUrl}/api/workflows/definitions", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Workflow deployment failed ({(int)response.StatusCode}): {errorBody}");
        }

        logger.LogInformation("Deployed {Count} workflow definitions to {Url}.", files.Count, runtimeUrl);
    }

    private async Task DeployLayoutsAsync(HttpClient http, string runtimeUrl, CancellationToken ct)
    {
        var layoutsDir = Path.Combine(environment.ContentRootPath, "Layouts");
        var files = Directory.EnumerateFiles(layoutsDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var array = new JsonArray();
        foreach (var file in files)
        {
            var raw = await File.ReadAllTextAsync(file, ct);
            var node = JsonNode.Parse(raw, documentOptions: JsonDocOptions)
                ?? throw new InvalidOperationException($"Layout file '{file}' is empty.");
            array.Add(node);
        }

        using var content = new StringContent(array.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{runtimeUrl}/api/ui/layouts/seed", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Layout deployment failed ({(int)response.StatusCode}): {errorBody}");
        }

        logger.LogInformation("Deployed {Count} UI layouts to {Url}.", files.Count, runtimeUrl);
    }

    private async Task SeedInitialInstancesAsync(HttpClient http, string runtimeUrl, CancellationToken ct)
    {
        var seedsDir = Path.Combine(environment.ContentRootPath, "Seeds");
        if (!Directory.Exists(seedsDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(seedsDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var raw = await File.ReadAllTextAsync(file, ct);
            if (JsonNode.Parse(raw, documentOptions: JsonDocOptions) is not JsonArray entries)
            {
                continue;
            }

            var grouped = entries
                .OfType<JsonObject>()
                .Select(e => (Workflow: e["workflow"]?.GetValue<string>() ?? string.Empty, Key: e["key"]?.GetValue<string>(), Data: e["data"] as JsonObject))
                .Where(e => !string.IsNullOrWhiteSpace(e.Workflow))
                .GroupBy(e => e.Workflow, StringComparer.Ordinal);

            foreach (var group in grouped)
            {
                if (group.All(e => string.IsNullOrWhiteSpace(e.Key)))
                {
                    using var listResponse = await http.GetAsync($"{runtimeUrl}/api/workflows/{group.Key}/instances?pageSize=1", ct);
                    if (listResponse.IsSuccessStatusCode)
                    {
                        var envelope = await listResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
                        var existingCount = envelope?["count"]?.GetValue<int>() ?? 0;
                        if (existingCount >= group.Count())
                        {
                            logger.LogInformation("Initial instances for '{Workflow}' already exist ({Count}); skipping seed.", group.Key, existingCount);
                            continue;
                        }
                    }
                }

                var seededCount = 0;
                foreach (var entry in group)
                {
                    var reqObj = new JsonObject();
                    if (!string.IsNullOrWhiteSpace(entry.Key))
                    {
                        reqObj["key"] = entry.Key;
                    }

                    if (entry.Data is not null)
                    {
                        reqObj["data"] = entry.Data.DeepClone();
                    }

                    using var content = new StringContent(reqObj.ToJsonString(), Encoding.UTF8, "application/json");
                    using var startRes = await http.PostAsync($"{runtimeUrl}/api/workflows/{entry.Workflow}/instances", content, ct);
                    if (startRes.IsSuccessStatusCode)
                    {
                        seededCount++;
                    }
                }

                logger.LogInformation("Seeded {Count} initial '{Workflow}' workflow instances.", seededCount, group.Key);
            }
        }
    }
}

