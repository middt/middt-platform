using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Expressions;
using Middt.Framework.Model.Enumerations;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Diagnostics;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Persistence;
using Middt.Framework.Workflow.Schema;

namespace Middt.Framework.Workflow.Runtime;

/// <summary>
/// Queries and projects <see cref="WorkflowInstance"/> JSON documents (<c>Data</c> + instance metadata) into flat objects
/// suitable for <c>api/v1/{entity}/getall|getitems|getbyid</c>, Server-Driven UI grids/KPIs, and <c>x-lookup</c> selects
/// without requiring relational domain tables.
/// </summary>
public sealed class WorkflowInstanceDataQuery(WorkflowDbContext db, WorkflowRegistry registry, IOptions<MiddtWorkflowOptions>? options = null)
{
    /// <summary>Returns accessible instances of <paramref name="entity"/> (clamped to 200 for unpaged lookups/KPIs) with <c>dataSchema</c> role/mask projection applied.</summary>
    public async Task<BaseResponseDataModel<List<JsonObject>>> GetAllAsync(
        string entity,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        using var activity = WorkflowDiagnostics.ActivitySource.StartActivity($"workflow.query.get_all {entity}");
        activity?.SetTag("middt.workflow.entity", entity);

        var query = BuildAccessibleQuery(entity, user);
        if (query is null)
        {
            return new BaseResponseDataModel<List<JsonObject>> { Result = ResultEnum.Success, Count = 0, Data = [] };
        }

        var total = await query.IgnoreAutoIncludes().CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(i => i.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var items = ProjectRows(rows, user);
        return new BaseResponseDataModel<List<JsonObject>>
        {
            Result = ResultEnum.Success,
            Count = total,
            Data = items,
        };
    }

    /// <summary>Returns filtered, sorted, and paged accessible instances of <paramref name="entity"/>.</summary>
    public async Task<BaseResponseDataModel<List<JsonObject>>> GetItemsAsync(
        string entity,
        JsonObject? searchRequest,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        using var activity = WorkflowDiagnostics.ActivitySource.StartActivity($"workflow.query.get_items {entity}");
        activity?.SetTag("middt.workflow.entity", entity);

        var query = BuildAccessibleQuery(entity, user);
        if (query is null)
        {
            return new BaseResponseDataModel<List<JsonObject>> { Result = ResultEnum.Success, Count = 0, Data = [] };
        }

        var page = Math.Max(1, searchRequest?["currentPage"]?.GetValue<int>() ?? 1);
        var pageSize = Math.Clamp(searchRequest?["requestItemSize"]?.GetValue<int>() ?? 20, 1, 100);
        var filter = searchRequest?["requestModel"] as JsonObject;
        var hasActiveFilter = HasNonEmptyFilter(filter);
        var sortProperty = searchRequest?["sortProperty"]?.GetValue<string>()?.Trim();
        var ascending = searchRequest?["sortReverse"]?.GetValue<bool>() ?? false;
        var defaultSort = string.IsNullOrWhiteSpace(sortProperty) || string.Equals(sortProperty, "createdAt", StringComparison.OrdinalIgnoreCase);

        if (!hasActiveFilter && defaultSort)
        {
            var total = await query.IgnoreAutoIncludes().CountAsync(cancellationToken);
            var ordered = ascending
                ? query.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                : query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id);
            var rows = await ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return new BaseResponseDataModel<List<JsonObject>>
            {
                Result = ResultEnum.Success,
                Count = total,
                Data = ProjectRows(rows, user),
            };
        }

        var workflowKey = ResolveWorkflowKey(entity);
        var rawDataFilters = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unsupportedFilters = new List<string>();

        if (filter is not null)
        {
            foreach (var (k, v) in filter)
            {
                var text = v switch
                {
                    JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>().Trim(),
                    not null => v.ToString().Trim(),
                    null => null,
                };
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

#pragma warning disable CA1304, CA1311, CA1862
                if (string.Equals(k, "id", StringComparison.OrdinalIgnoreCase))
                {
                    if (Guid.TryParse(text, out var idGuid))
                    {
                        query = query.Where(i => i.Id == idGuid);
                    }
                    else
                    {
                        var lower = text.ToLowerInvariant();
                        query = query.Where(i => i.Id.ToString().ToLower().Contains(lower));
                    }
                }
                else if (string.Equals(k, "key", StringComparison.OrdinalIgnoreCase) || string.Equals(k, "businessKey", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => (i.BusinessKey != null && i.BusinessKey.ToLower().Contains(lower)) || i.Id.ToString().ToLower().Contains(lower));
                }
                else if (string.Equals(k, "state", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => i.State.ToLower().Contains(lower));
                }
                else if (string.Equals(k, "status", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<WorkflowInstanceStatus>(text, ignoreCase: true, out var statusEnum))
                    {
                        query = query.Where(i => i.Status == statusEnum);
                    }
                    else
                    {
                        unsupportedFilters.Add(k);
                    }
                }
                else if (string.Equals(k, "workflow", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => i.WorkflowKey.ToLower().Contains(lower));
                }
                else if (string.Equals(k, "version", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver))
                    {
                        query = query.Where(i => i.WorkflowVersion == ver);
                    }
                    else
                    {
                        unsupportedFilters.Add(k);
                    }
                }
                else if (string.Equals(k, "startedBy", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => i.StartedBy != null && i.StartedBy.ToLower().Contains(lower));
                }
                else if (string.Equals(k, "lastExecutedBy", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => i.LastExecutedBy != null && i.LastExecutedBy.ToLower().Contains(lower));
                }
                else if (string.Equals(k, "assignedTo", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = text.ToLowerInvariant();
                    query = query.Where(i => i.AssignedTo != null && i.AssignedTo.ToLower().Contains(lower));
                }
                else
                {
                    rawDataFilters[k] = text;
                }
#pragma warning restore CA1304, CA1311, CA1862
            }
        }

        JsonObject? rawSchema = null;
        JsonObject? projectedSchema = null;
        if (workflowKey is not null)
        {
            var versions = TryGetVersions(workflowKey);
            if (versions.Count > 0)
            {
                rawSchema = versions[^1].DataSchema;
                projectedSchema = JsonSchemaValidator.ProjectSchema(rawSchema, ExpressionUser.FromPrincipal(user));
            }
        }

        List<WorkflowPartitionManager.SqlDataCondition> activeSqlConditions = [];
        if (rawDataFilters.Count > 0)
        {
            var (sqlConditions, unsupportedData, createdFromUtc, createdToUtc) = WorkflowPartitionManager.ClassifyDataFilters(rawDataFilters, projectedSchema, rawSchema);
            activeSqlConditions = sqlConditions;
            unsupportedFilters.AddRange(unsupportedData);
            if (sqlConditions.Count > 0 || createdFromUtc is not null || createdToUtc is not null)
            {
                query = WorkflowPartitionManager.ApplySqlDataFilters(query, db, options?.Value.Schema, workflowKey, sqlConditions, createdFromUtc, createdToUtc);
            }
        }

        if (unsupportedFilters.Count > 0)
        {
            return new BaseResponseDataModel<List<JsonObject>>
            {
                Result = ResultEnum.Error,
                Count = 0,
                Data = [],
                MessageList = [$"Desteklenmeyen veya geçersiz filtre alanı: {string.Join(", ", unsupportedFilters)}."],
            };
        }

        var supportsSqlPushdown = WorkflowPartitionManager.SupportsSqlDataPushdown(db.Database.ProviderName);
        var isCustomDataSort = false;
        IQueryable<WorkflowInstance> sortedQuery;
        if (defaultSort)
        {
            sortedQuery = ascending
                ? query.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                : query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id);
        }
        else if (TryApplyInstanceSort(query, sortProperty!, ascending, out var instanceSorted))
        {
            sortedQuery = instanceSorted;
        }
        else
        {
            if (!WorkflowPartitionManager.IsPropertySortableAndUnmasked(sortProperty!, projectedSchema, rawSchema))
            {
                return new BaseResponseDataModel<List<JsonObject>>
                {
                    Result = ResultEnum.Error,
                    Count = 0,
                    Data = [],
                    MessageList = [$"Desteklenmeyen veya maskeli sıralama alanı: {sortProperty}."],
                };
            }

            isCustomDataSort = true;
            sortedQuery = WorkflowPartitionManager.OrderBySqlDataProperty(query, db, options?.Value.Schema, workflowKey, sortProperty!, ascending);
        }

        if (!supportsSqlPushdown && (activeSqlConditions.Count > 0 || isCustomDataSort))
        {
            var candidateRows = await sortedQuery.ToListAsync(cancellationToken);
            var projectedAll = ProjectRows(candidateRows, user);
            if (activeSqlConditions.Count > 0)
            {
                projectedAll = projectedAll.Where(item => MatchesAllConditions(item, activeSqlConditions)).ToList();
            }

            if (!defaultSort && !string.IsNullOrWhiteSpace(sortProperty))
            {
                projectedAll.Sort((left, right) => CompareItems(left, right, sortProperty, ascending));
            }

            var pagedInMemory = projectedAll
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new BaseResponseDataModel<List<JsonObject>>
            {
                Result = ResultEnum.Success,
                Count = projectedAll.Count,
                Data = pagedInMemory,
            };
        }

        var filteredTotal = await query.IgnoreAutoIncludes().CountAsync(cancellationToken);
        var pagedRows = await sortedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var paged = ProjectRows(pagedRows, user);
        if (!defaultSort && !string.IsNullOrWhiteSpace(sortProperty))
        {
            paged.Sort((left, right) => CompareItems(left, right, sortProperty, ascending));
        }

        return new BaseResponseDataModel<List<JsonObject>>
        {
            Result = ResultEnum.Success,
            Count = filteredTotal,
            Data = paged,
        };
    }

    /// <summary>Returns a single accessible instance of <paramref name="entity"/> by <c>Id</c> or <c>BusinessKey</c>.</summary>
    public async Task<BaseResponseDataModel<JsonObject>> GetByIdAsync(
        string entity,
        string id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var query = BuildAccessibleQuery(entity, user);
        if (query is null)
        {
            return new BaseResponseDataModel<JsonObject> { Result = ResultEnum.Error, Count = 0, Data = null };
        }

        if (Guid.TryParse(id, out var guid))
        {
            query = query.Where(i => i.Id == guid || i.BusinessKey == id);
        }
        else
        {
            query = query.Where(i => i.BusinessKey == id);
        }

        var row = await query.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new BaseResponseDataModel<JsonObject> { Result = ResultEnum.Error, Count = 0, Data = null };
        }

        var definition = TryGetDefinition(row.WorkflowKey, row.WorkflowVersion);
        var match = FlattenInstance(row, definition, user);

        return new BaseResponseDataModel<JsonObject>
        {
            Result = ResultEnum.Success,
            Count = 1,
            Data = match,
        };
    }

    private IQueryable<WorkflowInstance>? BuildAccessibleQuery(string entity, ClaimsPrincipal principal)
    {
        var workflowKey = ResolveWorkflowKey(entity);
        var wfUser = WorkflowUser.From(principal);
        IQueryable<WorkflowInstance> query = db.Instances.AsNoTracking();

        if (workflowKey is not null)
        {
            var versions = TryGetVersions(workflowKey);
            if (versions.Count > 0)
            {
                var readable = versions
                    .Where(d => d.Roles.Count == 0 || d.Roles.Any(wfUser.IsInRole))
                    .ToArray();
                if (readable.Length == 0)
                {
                    return null;
                }

                var allVersions = readable.Where(d => CanSeeAllInstances(d, wfUser)).Select(d => d.Version).ToArray();
                var ownerVersions = readable.Where(d => !CanSeeAllInstances(d, wfUser)).Select(d => d.Version).ToArray();
                var owner = wfUser.AuditName;

                if (ownerVersions.Length == 0 && allVersions.Length == versions.Count)
                {
                    return query.Where(i => i.WorkflowKey == workflowKey);
                }

                if (allVersions.Length == 0 && ownerVersions.Length == versions.Count)
                {
                    return query.Where(i => i.WorkflowKey == workflowKey && i.StartedBy == owner);
                }

                return query.Where(i => i.WorkflowKey == workflowKey
                    && (allVersions.Contains(i.WorkflowVersion) || (ownerVersions.Contains(i.WorkflowVersion) && i.StartedBy == owner)));
            }

            return query.Where(i => i.WorkflowKey == workflowKey);
        }

        return query;
    }

    private List<JsonObject> ProjectRows(List<WorkflowInstance> rows, ClaimsPrincipal user)
    {
        var wfUser = WorkflowUser.From(user);
        var result = new List<JsonObject>(rows.Count);
        foreach (var row in rows)
        {
            var definition = TryGetDefinition(row.WorkflowKey, row.WorkflowVersion);
            if (definition is not null && !CanReadInstance(definition, row, wfUser))
            {
                continue;
            }

            result.Add(FlattenInstance(row, definition, user));
        }

        return result;
    }

    private static bool HasNonEmptyFilter(JsonObject? filter)
    {
        if (filter is null)
        {
            return false;
        }

        foreach (var (_, v) in filter)
        {
            if (!string.IsNullOrWhiteSpace(v?.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<WorkflowDefinition> TryGetVersions(string key)
    {
        try
        {
            return registry.GetVersions(key);
        }
        catch (WorkflowNotFoundException)
        {
            return [];
        }
    }

    private WorkflowDefinition? TryGetDefinition(string key, int version)
    {
        try
        {
            return registry.Get(key, version);
        }
        catch (WorkflowNotFoundException)
        {
            return null;
        }
    }

    private static bool CanSeeAllInstances(WorkflowDefinition definition, WorkflowUser user) =>
        user.IsSystem
        || definition.InstanceAccess is not { IsOwnerOnly: true } access
        || access.SupervisorRoles.Any(user.IsInRole);

    private static bool CanReadInstance(WorkflowDefinition definition, WorkflowInstance instance, WorkflowUser user)
    {
        if (definition.Roles.Count > 0 && !definition.Roles.Any(user.IsInRole))
        {
            return false;
        }

        return CanSeeAllInstances(definition, user)
            || (!string.IsNullOrEmpty(instance.StartedBy) && string.Equals(instance.StartedBy, user.AuditName, StringComparison.Ordinal));
    }

    private static JsonObject FlattenInstance(WorkflowInstance instance, WorkflowDefinition? definition, ClaimsPrincipal user)
    {
        var rawData = JsonNode.Parse(string.IsNullOrWhiteSpace(instance.Data) ? "{}" : instance.Data) as JsonObject ?? [];
        var projected = JsonSchemaValidator.ProjectData(rawData, definition?.DataSchema, ExpressionUser.FromPrincipal(user));

        var flat = new JsonObject();
        foreach (var (propName, propValue) in projected)
        {
            flat[propName] = propValue?.DeepClone();
        }

        var id = instance.Id.ToString("D");
        flat["id"] = id;
        flat["key"] = instance.BusinessKey ?? id;
        flat["businessKey"] = instance.BusinessKey;
        flat["workflow"] = instance.WorkflowKey;
        flat["version"] = instance.WorkflowVersion;
        flat["state"] = instance.State;
        flat["stateTitle"] = definition?.FindState(instance.State)?.Title ?? instance.State;
        flat["status"] = instance.Status.ToString();
        flat["startedBy"] = instance.StartedBy;
        flat["lastExecutedBy"] = instance.LastExecutedBy;
        flat["assignedTo"] = instance.AssignedTo;
        flat["assignedAt"] = instance.AssignedAt.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(instance.AssignedAt.Value, DateTimeKind.Utc))
            : null;
        flat["createdAt"] = new DateTimeOffset(DateTime.SpecifyKind(instance.CreatedAt, DateTimeKind.Utc));
        flat["updatedAt"] = new DateTimeOffset(DateTime.SpecifyKind(instance.UpdatedAt, DateTimeKind.Utc));
        return flat;
    }

    private static bool TryApplyInstanceSort(
        IQueryable<WorkflowInstance> query,
        string sortProperty,
        bool ascending,
        out IQueryable<WorkflowInstance> ordered)
    {
        switch (sortProperty.ToLowerInvariant())
        {
            case "createdat":
                ordered = ascending
                    ? query.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id);
                return true;
            case "updatedat":
                ordered = ascending
                    ? query.OrderBy(i => i.UpdatedAt).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id);
                return true;
            case "id":
                ordered = ascending
                    ? query.OrderBy(i => i.Id)
                    : query.OrderByDescending(i => i.Id);
                return true;
            case "key":
            case "businesskey":
                ordered = ascending
                    ? query.OrderBy(i => i.BusinessKey).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.BusinessKey).ThenByDescending(i => i.Id);
                return true;
            case "state":
                ordered = ascending
                    ? query.OrderBy(i => i.State).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.State).ThenByDescending(i => i.Id);
                return true;
            case "status":
                ordered = ascending
                    ? query.OrderBy(i => i.Status).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.Status).ThenByDescending(i => i.Id);
                return true;
            case "workflow":
                ordered = ascending
                    ? query.OrderBy(i => i.WorkflowKey).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.WorkflowKey).ThenByDescending(i => i.Id);
                return true;
            case "version":
                ordered = ascending
                    ? query.OrderBy(i => i.WorkflowVersion).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.WorkflowVersion).ThenByDescending(i => i.Id);
                return true;
            case "startedby":
                ordered = ascending
                    ? query.OrderBy(i => i.StartedBy).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.StartedBy).ThenByDescending(i => i.Id);
                return true;
            case "lastexecutedby":
                ordered = ascending
                    ? query.OrderBy(i => i.LastExecutedBy).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.LastExecutedBy).ThenByDescending(i => i.Id);
                return true;
            case "assignedto":
                ordered = ascending
                    ? query.OrderBy(i => i.AssignedTo).ThenBy(i => i.Id)
                    : query.OrderByDescending(i => i.AssignedTo).ThenByDescending(i => i.Id);
                return true;
            default:
                ordered = query;
                return false;
        }
    }

    /// <summary>
    /// Compares two projected instance objects by <paramref name="property"/> using true numeric comparison for numbers
    /// (preserving sign and full <see cref="decimal"/> precision), chronological comparison for dates, and a deterministic
    /// secondary tie-breaker (<c>id</c>).
    /// </summary>
    internal static int CompareItems(JsonObject left, JsonObject right, string property, bool ascending)
    {
        var leftNode = GetCaseInsensitive(left, property);
        var rightNode = GetCaseInsensitive(right, property);

        int cmp;
        if (leftNode is null && rightNode is null)
        {
            cmp = 0;
        }
        else if (leftNode is null)
        {
            return 1;
        }
        else if (rightNode is null)
        {
            return -1;
        }
        else if (WorkflowPartitionManager.TryGetNodeDecimal(leftNode, out var leftDec) &&
                 WorkflowPartitionManager.TryGetNodeDecimal(rightNode, out var rightDec))
        {
            cmp = decimal.Compare(leftDec, rightDec);
        }
        else if (leftNode is JsonValue lvBool && lvBool.GetValueKind() is JsonValueKind.True or JsonValueKind.False &&
                 rightNode is JsonValue rvBool && rvBool.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            cmp = lvBool.GetValue<bool>().CompareTo(rvBool.GetValue<bool>());
        }
        else
        {
            var leftText = leftNode is JsonValue lvStr && lvStr.GetValueKind() == JsonValueKind.String ? lvStr.GetValue<string>() : leftNode.ToString();
            var rightText = rightNode is JsonValue rvStr && rvStr.GetValueKind() == JsonValueKind.String ? rvStr.GetValue<string>() : rightNode.ToString();
            if (DateTimeOffset.TryParse(leftText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var leftDto) &&
                DateTimeOffset.TryParse(rightText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rightDto))
            {
                cmp = DateTimeOffset.Compare(leftDto, rightDto);
            }
            else
            {
                cmp = StringComparer.OrdinalIgnoreCase.Compare(
                    WorkflowPartitionManager.FoldTurkishLower(leftText),
                    WorkflowPartitionManager.FoldTurkishLower(rightText));
            }
        }

        if (cmp != 0)
        {
            return ascending ? cmp : -cmp;
        }

        var leftId = left["id"]?.ToString() ?? string.Empty;
        var rightId = right["id"]?.ToString() ?? string.Empty;
        var idCmp = StringComparer.OrdinalIgnoreCase.Compare(leftId, rightId);
        return ascending ? idCmp : -idCmp;
    }

    internal static bool MatchesAllConditions(JsonObject item, IReadOnlyList<WorkflowPartitionManager.SqlDataCondition> conditions)
    {
        foreach (var cond in conditions)
        {
            if (!MatchesCondition(item, cond))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCondition(JsonObject item, WorkflowPartitionManager.SqlDataCondition cond)
    {
        var node = GetCaseInsensitive(item, cond.Property);
        if (node is null)
        {
            return false;
        }

        switch (cond.Kind)
        {
            case WorkflowPartitionManager.SqlConditionKind.Numeric:
                if (!WorkflowPartitionManager.TryGetNodeDecimal(node, out var actualDec) || cond.Parameter is not decimal targetDec)
                {
                    return false;
                }

                return CompareByOp(decimal.Compare(actualDec, targetDec), cond.SqlOp);

            case WorkflowPartitionManager.SqlConditionKind.Boolean:
                if (cond.Parameter is not bool targetBool)
                {
                    return false;
                }

                if (node is JsonValue boolVal && boolVal.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                {
                    return boolVal.GetValue<bool>() == targetBool;
                }

                return bool.TryParse(node.ToString(), out var parsedBool) && parsedBool == targetBool;

            case WorkflowPartitionManager.SqlConditionKind.DateBound:
            case WorkflowPartitionManager.SqlConditionKind.ExactText:
            {
                var actualStr = node is JsonValue strVal && strVal.GetValueKind() == JsonValueKind.String ? strVal.GetValue<string>() : node.ToString();
                var targetStr = cond.Parameter?.ToString() ?? string.Empty;
                return CompareByOp(StringComparer.Ordinal.Compare(actualStr, targetStr), cond.SqlOp);
            }

            case WorkflowPartitionManager.SqlConditionKind.ContainsText:
            {
                var actualStr = node is JsonValue strVal && strVal.GetValueKind() == JsonValueKind.String ? strVal.GetValue<string>() : node.ToString();
                var targetStr = cond.Parameter?.ToString() ?? string.Empty;
                return WorkflowPartitionManager.FoldTurkishLower(actualStr).Contains(targetStr, StringComparison.Ordinal);
            }

            default:
                return false;
        }
    }

    private static bool CompareByOp(int cmp, string sqlOp) => sqlOp switch
    {
        "=" => cmp == 0,
        ">" => cmp > 0,
        ">=" => cmp >= 0,
        "<" => cmp < 0,
        "<=" => cmp <= 0,
        _ => false,
    };

    private static JsonNode? GetCaseInsensitive(JsonObject item, string property)
    {
        if (item.TryGetPropertyValue(property, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in item)
        {
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private string? ResolveWorkflowKey(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity) || string.Equals(entity, "WorkflowInstance", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryGetVersions(entity) is { Count: > 0 } direct)
        {
            return direct[0].Key;
        }

        var kebab = ToKebabCase(entity);
        if (TryGetVersions(kebab) is { Count: > 0 } kebabMatch)
        {
            return kebabMatch[0].Key;
        }

        var normalized = NormalizeKey(entity);
        foreach (var def in registry.Latest)
        {
            if (string.Equals(NormalizeKey(def.Key), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return def.Key;
            }
        }

        var prefixMatches = registry.Latest
            .Where(d => d.Key.StartsWith(kebab + "-", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (prefixMatches.Count == 1)
        {
            return prefixMatches[0];
        }

        return kebab;
    }

    private static string ToKebabCase(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '_' or ' ')
            {
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append('-');
                }

                continue;
            }

            if (char.IsUpper(c) && i > 0 && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static string NormalizeKey(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
