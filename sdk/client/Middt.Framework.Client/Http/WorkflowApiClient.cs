using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middt.Framework.Api.Configuration.Model;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Client.Security;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Client.DependencyInjection;

namespace Middt.Framework.Client.Http;

/// <summary>
/// Typed client for the workflow REST API mapped by <c>MapMiddtWorkflows</c> (default prefix <c>api/workflows</c>).
/// Register with <c>services.AddMiddtApiClient&lt;WorkflowApiClient&gt;()</c>. Failures (ProblemDetails 400/403/404/409,
/// network errors) come back as <c>Result = Error</c> with the server messages in <see cref="BaseResponseModel.MessageList"/>.
/// </summary>
public class WorkflowApiClient(
    HttpClient httpClient,
    IBaseSessionState sessionState,
    ITokenService tokenService,
    IOptions<ApiSettings> apiSettings,
    ILogger<WorkflowApiClient> logger)
    : ApiClientBase(httpClient, sessionState, tokenService, logger)
{
    /// <summary>Route prefix the workflow endpoints are mapped under.</summary>
    protected virtual string RoutePrefix => "api/workflows";

    /// <summary>API settings (e.g. for derived clients that build versioned prefixes).</summary>
    protected ApiSettings ApiSettings => apiSettings.Value;

    public virtual Task<BaseResponseDataModel<List<WorkflowSummaryModel>>> ListWorkflowsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<WorkflowSummaryModel>>>(HttpMethod.Get, RoutePrefix, null, cancellationToken);

    /// <summary>Gets the catalog of built-in workflow tasks and their parameter specifications for the visual designer.</summary>
    public virtual Task<BaseResponseDataModel<List<WorkflowTaskSpecModel>>> ListTaskSpecsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<WorkflowTaskSpecModel>>>(HttpMethod.Get, $"{RoutePrefix.TrimEnd('/')}/tasks", null, cancellationToken);

    /// <summary>Gets the full raw JSON definition of <paramref name="workflow"/> for the visual workflow designer.</summary>
    public virtual Task<BaseResponseDataModel<JsonObject>> GetDefinitionJsonAsync(string workflow, int? version = null, CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "version", version?.ToString(CultureInfo.InvariantCulture));
        return SendAsync<BaseResponseDataModel<JsonObject>>(HttpMethod.Get, $"{WorkflowRoute(workflow)}/definition{query}", null, cancellationToken);
    }

    /// <summary>Validates a draft workflow JSON document without saving it.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowValidationResultModel>> ValidateDefinitionAsync(JsonObject definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SendAsync<BaseResponseDataModel<WorkflowValidationResultModel>>(HttpMethod.Post, $"{RoutePrefix.TrimEnd('/')}/validate", definition, cancellationToken);
    }

    /// <summary>Validates and saves a workflow JSON definition at runtime (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<JsonObject>> SaveDefinitionAsync(string workflow, JsonObject definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SendAsync<BaseResponseDataModel<JsonObject>>(HttpMethod.Put, $"{WorkflowRoute(workflow)}/definition", definition, cancellationToken);
    }

    /// <summary>Lists all immutable versions of <paramref name="workflow"/> ordered from newest to oldest.</summary>
    public virtual Task<BaseResponseDataModel<List<WorkflowVersionSummaryModel>>> ListVersionsAsync(string workflow, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<WorkflowVersionSummaryModel>>>(HttpMethod.Get, $"{WorkflowRoute(workflow)}/versions", null, cancellationToken);

    /// <summary>Computes structural and RFC 6902 JSON Patch diff between two versions of <paramref name="workflow"/>.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowVersionDiffModel>> GetVersionDiffAsync(
        string workflow,
        int? fromVersion = null,
        int? toVersion = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "fromVersion", fromVersion?.ToString(CultureInfo.InvariantCulture));
        AppendQuery(query, "toVersion", toVersion?.ToString(CultureInfo.InvariantCulture));
        return SendAsync<BaseResponseDataModel<WorkflowVersionDiffModel>>(HttpMethod.Get, $"{WorkflowRoute(workflow)}/diff{query}", null, cancellationToken);
    }

    /// <summary>Publishes an earlier <paramref name="version"/> of <paramref name="workflow"/> as a new latest version (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<JsonObject>> RollbackVersionAsync(string workflow, int version, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<JsonObject>>(HttpMethod.Post, $"{WorkflowRoute(workflow)}/versions/{version.ToString(CultureInfo.InvariantCulture)}/rollback", null, cancellationToken);

    /// <summary>Gets an operational summary of faulted instances, busy instances, pending jobs, and unresolved incidents.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowOperationsSummaryModel>> GetOperationsSummaryAsync(string? workflow = null, CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "workflow", workflow);
        return SendAsync<BaseResponseDataModel<WorkflowOperationsSummaryModel>>(HttpMethod.Get, $"{RoutePrefix.TrimEnd('/')}/operations{query}", null, cancellationToken);
    }

    /// <summary>Retries multiple faulted workflow instances in a batch.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowBatchOperationResultModel>> RetryBatchAsync(WorkflowBatchRetryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<WorkflowBatchOperationResultModel>>(HttpMethod.Post, $"{RoutePrefix.TrimEnd('/')}/operations/retry-batch", request, cancellationToken);
    }

    /// <summary>Lists actionable human tasks across all accessible workflows.</summary>
    public virtual Task<BaseResponseDataModel<List<WorkflowInboxItemModel>>> ListInboxAsync(
        string? workflow = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "workflow", workflow);
        AppendQuery(query, "page", page.ToString(CultureInfo.InvariantCulture));
        AppendQuery(query, "pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return SendAsync<BaseResponseDataModel<List<WorkflowInboxItemModel>>>(HttpMethod.Get, $"{RoutePrefix.TrimEnd('/')}/inbox{query}", null, cancellationToken);
    }

    /// <summary>Simulates a workflow execution in memory without database side effects.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowSimulationResultModel>> SimulateAsync(
        string workflow,
        WorkflowSimulationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<WorkflowSimulationResultModel>>(HttpMethod.Post, $"{WorkflowRoute(workflow)}/simulate", request, cancellationToken);
    }

    /// <summary>Exports all workflow definitions and UI layouts as a portable bundle with SHA-256 checksums.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowBundleModel>> ExportBundleAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<WorkflowBundleModel>>(HttpMethod.Get, $"{RoutePrefix.TrimEnd('/')}/bundle", null, cancellationToken);

    /// <summary>Validates (<c>DryRun = true</c>) or imports (<c>DryRun = false</c>) a portable workflow/layout bundle.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowBundleImportResultModel>> ImportBundleAsync(
        WorkflowBundleImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<WorkflowBundleImportResultModel>>(HttpMethod.Post, $"{RoutePrefix.TrimEnd('/')}/bundle/import", request, cancellationToken);
    }

    /// <summary>Purges completed/cancelled workflow instances older than <see cref="WorkflowPurgeRequest.RetentionDays"/> (requires <c>ui-admin</c> role).</summary>
    public virtual Task<BaseResponseDataModel<WorkflowPurgeResultModel>> PurgeAsync(
        WorkflowPurgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<WorkflowPurgeResultModel>>(HttpMethod.Post, $"{RoutePrefix.TrimEnd('/')}/maintenance/purge", request, cancellationToken);
    }

    /// <summary>Gets the definition of <paramref name="workflow"/>: the latest version, or <paramref name="version"/> when given
    /// (instances keep running on the version they were started with, see <see cref="WorkflowInstanceModel.WorkflowVersion"/>).</summary>
    public virtual Task<BaseResponseDataModel<WorkflowDefinitionModel>> GetDefinitionAsync(string workflow, int? version = null, CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "version", version?.ToString(CultureInfo.InvariantCulture));
        return SendAsync<BaseResponseDataModel<WorkflowDefinitionModel>>(HttpMethod.Get, $"{WorkflowRoute(workflow)}{query}", null, cancellationToken);
    }

    /// <summary>Starts an instance. Starting twice with the same business key returns the existing instance.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowInstanceModel>> StartAsync(string workflow, StartWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<BaseResponseDataModel<WorkflowInstanceModel>>(HttpMethod.Post, $"{WorkflowRoute(workflow)}/instances", request, cancellationToken);
    }

    /// <summary>Lists instances (one page); <see cref="BaseResponseDataModel{T}.Count"/> is the total count.</summary>
    public virtual Task<BaseResponseDataModel<List<WorkflowInstanceModel>>> ListInstancesAsync(
        string workflow,
        string? state = null,
        WorkflowInstanceStatus? status = null,
        string? key = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        ListInstancesAsync(workflow, state, status, key, page, pageSize, null, cancellationToken);

    /// <summary>Lists instances (one page) with optional workflow data property filters; <see cref="BaseResponseDataModel{T}.Count"/> is the total count.</summary>
    public virtual Task<BaseResponseDataModel<List<WorkflowInstanceModel>>> ListInstancesAsync(
        string workflow,
        string? state,
        WorkflowInstanceStatus? status,
        string? key,
        int page,
        int pageSize,
        IReadOnlyDictionary<string, string?>? filter,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder();
        AppendQuery(query, "state", state);
        AppendQuery(query, "status", status?.ToString());
        AppendQuery(query, "key", key);
        if (filter is not null)
        {
            foreach (var (field, value) in filter)
            {
                if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(value))
                {
                    AppendQuery(query, $"data.{field.Trim()}", value.Trim());
                }
            }
        }

        AppendQuery(query, "page", page.ToString(CultureInfo.InvariantCulture));
        AppendQuery(query, "pageSize", pageSize.ToString(CultureInfo.InvariantCulture));
        return SendAsync<BaseResponseDataModel<List<WorkflowInstanceModel>>>(HttpMethod.Get, $"{WorkflowRoute(workflow)}/instances{query}", null, cancellationToken);
    }

    /// <summary>Gets an instance including the transitions the current user may execute.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowInstanceModel>> GetInstanceAsync(string workflow, Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<WorkflowInstanceModel>>(HttpMethod.Get, InstanceRoute(workflow, id), null, cancellationToken);

    /// <summary>Executes a manual transition. With <paramref name="runAsync"/> the transition is queued (202).</summary>
    public virtual Task<BaseResponseDataModel<WorkflowInstanceModel>> ExecuteTransitionAsync(
        string workflow,
        Guid id,
        string transition,
        JsonObject? input = null,
        bool runAsync = false,
        CancellationToken cancellationToken = default)
    {
        var uri = $"{InstanceRoute(workflow, id)}/transitions/{EscapePathSegment(transition, nameof(transition))}";
        if (runAsync)
        {
            uri += "?async=true";
        }

        return SendAsync<BaseResponseDataModel<WorkflowInstanceModel>>(HttpMethod.Post, uri, input, cancellationToken);
    }

    public virtual Task<BaseResponseDataModel<List<WorkflowHistoryModel>>> GetHistoryAsync(string workflow, Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<WorkflowHistoryModel>>>(HttpMethod.Get, $"{InstanceRoute(workflow, id)}/history", null, cancellationToken);

    public virtual Task<BaseResponseDataModel<List<WorkflowIncidentModel>>> GetIncidentsAsync(string workflow, Guid id, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<List<WorkflowIncidentModel>>>(HttpMethod.Get, $"{InstanceRoute(workflow, id)}/incidents", null, cancellationToken);

    /// <summary>Retries the failed step of a <see cref="WorkflowInstanceStatus.Faulted"/> instance.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowInstanceModel>> RetryAsync(string workflow, Guid id, CancellationToken cancellationToken = default) =>
        RetryAsync(workflow, id, null, cancellationToken);

    /// <summary>Retries the failed step of a <see cref="WorkflowInstanceStatus.Faulted"/> instance with optional updated input/data.</summary>
    public virtual Task<BaseResponseDataModel<WorkflowInstanceModel>> RetryAsync(string workflow, Guid id, JsonObject? input, CancellationToken cancellationToken = default) =>
        SendAsync<BaseResponseDataModel<WorkflowInstanceModel>>(HttpMethod.Post, $"{InstanceRoute(workflow, id)}/retry", input, cancellationToken);

    protected string WorkflowRoute(string workflow) =>
        $"{RoutePrefix.TrimEnd('/')}/{EscapePathSegment(workflow, nameof(workflow))}";

    protected string InstanceRoute(string workflow, Guid id) =>
        $"{WorkflowRoute(workflow)}/instances/{id.ToString("D", CultureInfo.InvariantCulture)}";

    private static void AppendQuery(StringBuilder query, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        query.Append(query.Length == 0 ? '?' : '&')
            .Append(name)
            .Append('=')
            .Append(Uri.EscapeDataString(value));
    }
}
