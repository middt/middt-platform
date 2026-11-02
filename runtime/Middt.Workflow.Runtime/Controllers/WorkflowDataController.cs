using System.Text.Json.Nodes;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Workflow.Runtime;

namespace Middt.Workflow.Runtime.Controllers;

/// <summary>
/// Exposes workflow instances' JSON <c>Data</c> over the standard <c>api/v1/{entity}/getall|getitems|getbyid</c> contract so
/// Server-Driven UI layouts (<c>grid</c>, <c>record</c>, <c>layout</c>) and <c>x-lookup</c> selects operate directly on
/// <c>WorkflowDbContext</c> JSON documents without separate relational domain tables.
/// </summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/{entity}")]
public sealed class WorkflowDataController(WorkflowInstanceDataQuery query) : ControllerBase
{
    [HttpGet("getall")]
    public Task<BaseResponseDataModel<List<JsonObject>>> GetAllAsync(string entity, CancellationToken cancellationToken) =>
        query.GetAllAsync(entity, User, cancellationToken);

    [HttpPost("getitems")]
    public Task<BaseResponseDataModel<List<JsonObject>>> GetItemsAsync(
        string entity,
        [FromBody] JsonObject? searchRequest,
        CancellationToken cancellationToken) =>
        query.GetItemsAsync(entity, searchRequest, User, cancellationToken);

    [HttpGet("getbyid")]
    public Task<BaseResponseDataModel<JsonObject>> GetByIdAsync(
        string entity,
        [FromQuery] string id,
        CancellationToken cancellationToken) =>
        query.GetByIdAsync(entity, id, User, cancellationToken);
}
