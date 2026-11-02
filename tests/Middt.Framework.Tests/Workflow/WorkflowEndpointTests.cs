using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Middt.Framework.Common.Model.Data;
using Middt.Framework.Model.Enumerations;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Workflow.Definitions;

namespace Middt.Framework.Tests.Workflow;

public sealed class WorkflowEndpointTests
{
    private const string Base = "api/workflows";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<WorkflowTestHost> Host() =>
        WorkflowTestHost.StartAsync(TestWorkflows.Pipeline, TestWorkflows.Restricted, TestWorkflows.Errors, TestWorkflows.Timers);

    private static async Task<HttpResponseMessage> SendAsync(WorkflowTestHost host, HttpMethod method, string uri, object? body = null, string? user = "alice", params string[] roles)
    {
        using var request = host.Request(method, uri, body, user, roles);
        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<BaseResponseDataModel<T>> EnvelopeAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<BaseResponseDataModel<T>>(WorkflowTestHost.Json, Ct))!;

    private static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>(WorkflowTestHost.Json, Ct))!;
    }

    private static async Task<WorkflowInstanceModel> StartAsync(WorkflowTestHost host, string workflow = "pipeline", object? body = null)
    {
        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/{workflow}/instances", body ?? new { data = new { name = "widget" } });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
    }

    private static async Task<WorkflowInstanceModel> TransitionAsync(WorkflowTestHost host, string workflow, Guid id, string transition, object? body = null, params string[] roles)
    {
        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/{workflow}/instances/{id}/transitions/{transition}", body, roles: roles);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
    }

    [Fact]
    public async Task Requests_without_authentication_are_rejected()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Get, Base, user: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Workflow_list_is_filtered_by_workflow_roles()
    {
        await using var host = await Host();

        using var user = await SendAsync(host, HttpMethod.Get, Base);
        using var clerk = await SendAsync(host, HttpMethod.Get, Base, roles: "clerk");

        var visible = await EnvelopeAsync<List<WorkflowSummaryModel>>(user);
        visible.Result.Should().Be(ResultEnum.Success);
        visible.Data!.Select(w => w.Key).Should().Equal("errors", "pipeline", "timers");
        visible.Count.Should().Be(3);
        (await EnvelopeAsync<List<WorkflowSummaryModel>>(clerk)).Data!.Select(w => w.Key).Should().Contain("restricted");
    }

    [Fact]
    public async Task Workflow_definition_endpoint_describes_states_and_transitions()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline");
        using var forbidden = await SendAsync(host, HttpMethod.Get, $"{Base}/restricted");
        using var missing = await SendAsync(host, HttpMethod.Get, $"{Base}/nope");
        using var missingVersion = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline?version=9");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var model = (await EnvelopeAsync<WorkflowDefinitionModel>(response)).Data!;
        model.Title.Should().Be("Pipeline");
        model.StartState.Should().Be("draft");
        model.StartInputSchema.Should().NotBeNull();
        model.States.Select(s => s.Key).Should().Equal("draft", "review", "approved", "cancelled");
        model.Transitions.Should().Contain(t => t.Key == "submit" && t.From == "draft" && t.Target == "review" && t.Trigger == "Manual");
        model.Transitions.Should().Contain(t => t.Key == "cancel" && t.From == null);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingVersion.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_returns_201_with_location_and_envelope()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances", new { data = new { name = "widget" } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var envelope = await EnvelopeAsync<WorkflowInstanceModel>(response);
        envelope.Result.Should().Be(ResultEnum.Success);
        envelope.Count.Should().Be(1);
        envelope.Data!.State.Should().Be("draft");
        envelope.Data.Status.Should().Be(WorkflowInstanceStatus.Active);
        envelope.Data.StartedBy.Should().Be("alice");
        response.Headers.Location!.ToString().Should().Be($"/{Base}/pipeline/instances/{envelope.Data.Id}");

        using var get = await SendAsync(host, HttpMethod.Get, response.Headers.Location.ToString());
        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Start_without_a_body_is_allowed_for_workflows_without_input()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/restricted/instances", roles: "clerk");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Start_with_an_existing_key_returns_200_and_the_same_instance()
    {
        await using var host = await Host();
        var body = new { key = "order-9", data = new { name = "widget" } };

        using var first = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances", body);
        using var second = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances", body);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.Location.Should().BeNull();
        var created = await EnvelopeAsync<WorkflowInstanceModel>(first);
        var existing = await EnvelopeAsync<WorkflowInstanceModel>(second);
        existing.Data!.Id.Should().Be(created.Data!.Id);
        existing.MessageList.Should().Equal(Middt.Framework.Workflow.Endpoints.WorkflowEndpoints.ExistingInstanceMessage);
        created.MessageList.Should().BeEmpty("a new instance has no message");
    }

    [Fact]
    public async Task Invalid_input_returns_a_validation_problem()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances", new { data = new { name = "x", role = "admin" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(WorkflowTestHost.Json, Ct))!;
        problem.Title.Should().Be("The request is not valid for this transition.");
        problem.Errors["input"].Should().BeEquivalentTo("$.name: must be at least 2 characters.", "$.role: is not allowed.");
    }

    [Fact]
    public async Task Get_returns_the_instance_with_transitions_available_to_the_caller()
    {
        await using var host = await Host();
        var instance = await StartAsync(host);

        using var asUser = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/instances/{instance.Id}");
        using var asManager = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/instances/{instance.Id}", roles: "manager");

        asUser.StatusCode.Should().Be(HttpStatusCode.OK);
        var model = (await EnvelopeAsync<WorkflowInstanceModel>(asUser)).Data!;
        model.Data["name"]!.GetValue<string>().Should().Be("widget");
        model.AvailableTransitions.Select(t => t.Key).Should().BeEquivalentTo("submit", "plain", "touch", "cancel");
        model.AvailableTransitions.Single(t => t.Key == "submit").InputSchema.Should().NotBeNull();
        (await EnvelopeAsync<WorkflowInstanceModel>(asManager)).Data!.AvailableTransitions.Select(t => t.Key).Should().Contain("approve");
    }

    [Fact]
    public async Task Instance_requested_under_another_workflow_or_unknown_id_is_404()
    {
        await using var host = await Host();
        var instance = await StartAsync(host);

        using var wrongWorkflow = await SendAsync(host, HttpMethod.Get, $"{Base}/timers/instances/{instance.Id}");
        using var wrongWorkflowTransition = await SendAsync(host, HttpMethod.Post, $"{Base}/timers/instances/{instance.Id}/transitions/submit");
        using var unknown = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/instances/{Guid.NewGuid()}");

        wrongWorkflow.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemAsync(wrongWorkflow)).Title.Should().Contain("does not belong to workflow 'timers'");
        wrongWorkflowTransition.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync(instance.Id)).State.Should().Be("draft");
    }

    [Fact]
    public async Task Transition_returns_200_with_the_updated_instance()
    {
        await using var host = await Host();
        var instance = await StartAsync(host);

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{instance.Id}/transitions/submit", new { comment = "go" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var model = (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
        model.State.Should().Be("review");
        model.Data["submission"]!["comment"]!.GetValue<string>().Should().Be("go");
    }

    [Fact]
    public async Task Transition_without_the_required_role_is_403()
    {
        await using var host = await Host();
        var instance = await StartAsync(host);

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{instance.Id}/transitions/approve");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemAsync(response)).Title.Should().Contain("not allowed");
    }

    [Fact]
    public async Task Workflow_roles_are_enforced_on_instances()
    {
        await using var host = await Host();
        using var start = await SendAsync(host, HttpMethod.Post, $"{Base}/restricted/instances", roles: "clerk");
        var id = (await EnvelopeAsync<WorkflowInstanceModel>(start)).Data!.Id;

        using var startForbidden = await SendAsync(host, HttpMethod.Post, $"{Base}/restricted/instances");
        using var getForbidden = await SendAsync(host, HttpMethod.Get, $"{Base}/restricted/instances/{id}");
        using var listForbidden = await SendAsync(host, HttpMethod.Get, $"{Base}/restricted/instances");
        using var historyForbidden = await SendAsync(host, HttpMethod.Get, $"{Base}/restricted/instances/{id}/history");

        startForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        getForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        listForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        historyForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unavailable_transition_terminal_and_busy_instances_are_409()
    {
        await using var host = await Host();
        var instance = await StartAsync(host);
        var busy = await StartAsync(host);
        await host.SetStatusAsync(busy.Id, WorkflowInstanceStatus.Busy, host.Time.GetUtcNow().UtcDateTime);

        using var unavailable = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{instance.Id}/transitions/nope");
        using var busyResponse = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{busy.Id}/transitions/submit");
        using var cancel = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{instance.Id}/transitions/cancel");
        using var afterFinish = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{instance.Id}/transitions/submit");

        unavailable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemAsync(unavailable)).Title.Should().Be("Transition 'nope' is not available in state 'draft'.");
        busyResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemAsync(busyResponse)).Title.Should().Contain("busy");
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        afterFinish.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Async_transition_returns_202_and_is_executed_by_the_processor()
    {
        await using var host = await Host();
        var instance = await StartAsync(host, "timers", new { });

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/timers/instances/{instance.Id}/transitions/proceed?async=true");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!.State.Should().Be("waiting");

        await host.RunJobsAsync();
        (await host.GetAsync(instance.Id)).State.Should().Be("working");
    }

    [Fact]
    public async Task History_incidents_and_retry_endpoints()
    {
        await using var host = await Host();
        var instance = await StartAsync(host, "errors", new { });
        var uri = $"{Base}/errors/instances/{instance.Id}";

        using var notFaulted = await SendAsync(host, HttpMethod.Post, $"{uri}/retry");
        notFaulted.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var fault = await SendAsync(host, HttpMethod.Post, $"{uri}/transitions/fault", new { attempt = 3 });
        fault.StatusCode.Should().Be(HttpStatusCode.OK);
        (await EnvelopeAsync<WorkflowInstanceModel>(fault)).Data!.Status.Should().Be(WorkflowInstanceStatus.Faulted);

        using var incidents = await SendAsync(host, HttpMethod.Get, $"{uri}/incidents");
        var incidentList = await EnvelopeAsync<List<WorkflowIncidentModel>>(incidents);
        incidentList.Count.Should().Be(1);
        incidentList.Data!.Single().Should().Match<WorkflowIncidentModel>(i => i.TaskKey == "fault-once" && i.ResolvedAt == null);

        using var retry = await SendAsync(host, HttpMethod.Post, $"{uri}/retry");
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retried = (await EnvelopeAsync<WorkflowInstanceModel>(retry)).Data!;
        retried.Status.Should().Be(WorkflowInstanceStatus.Completed);
        retried.Data["faultInput"]!["attempt"]!.GetValue<int>().Should().Be(3);

        using var resolved = await SendAsync(host, HttpMethod.Get, $"{uri}/incidents");
        (await EnvelopeAsync<List<WorkflowIncidentModel>>(resolved)).Data!.Single().ResolvedAt.Should().NotBeNull();

        using var history = await SendAsync(host, HttpMethod.Get, $"{uri}/history");
        var items = await EnvelopeAsync<List<WorkflowHistoryModel>>(history);
        items.Data!.Select(h => h.TransitionKey).Should().Equal("start", "fault");
        items.Data!.Select(h => h.Trigger).Should().Equal("Start", "Retry");
        items.Count.Should().Be(2);
    }

    [Fact]
    public async Task List_filters_by_state_status_and_key_and_reports_the_total_count()
    {
        await using var host = await Host();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await StartAsync(host, "pipeline", new { key = $"k{i}", data = new { name = $"item{i}" } })).Id);
            host.Time.Advance(TimeSpan.FromSeconds(1));
        }

        (await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{ids[0]}/transitions/cancel")).Dispose();
        (await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/instances/{ids[1]}/transitions/submit")).Dispose();

        async Task<BaseResponseDataModel<List<WorkflowInstanceModel>>> ListAsync(string query)
        {
            using var response = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/instances{query}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await EnvelopeAsync<List<WorkflowInstanceModel>>(response);
        }

        (await ListAsync("")).Count.Should().Be(5);
        (await ListAsync("?state=draft")).Data!.Select(i => i.Key).Should().Equal("k4", "k3", "k2");
        (await ListAsync("?status=Cancelled")).Data!.Should().ContainSingle().Which.Id.Should().Be(ids[0]);
        (await ListAsync("?key=k1")).Data!.Should().ContainSingle().Which.State.Should().Be("review");

        var page = await ListAsync("?page=2&pageSize=2");
        page.Count.Should().Be(5);
        page.Data!.Select(i => i.Key).Should().Equal("k2", "k1");
        page.Data!.Should().AllSatisfy(i => i.AvailableTransitions.Should().NotBeEmpty());
    }

    [Fact]
    public async Task Transition_automatically_captures_headers_query_and_body_into_input()
    {
        const string httpContextWf = """
            {
              "key": "http-context-wf",
              "start": { "key": "start", "target": "initial" },
              "states": [
                {
                  "key": "initial",
                  "transitions": [
                    {
                      "key": "process",
                      "target": "completed",
                      "input": {
                        "schema": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": [ "note" ],
                          "properties": { "note": { "type": "string" } }
                        },
                        "set": {
                          "capturedHeaderBranch": "input.header.branchId",
                          "capturedHeaderXBranch": "input.header.xBranchId",
                          "capturedHeaderExact": "input.header[\"X-Branch-Id\"]",
                          "capturedQueryChannel": "input.query.channel",
                          "capturedQueryPage": "input.query.page",
                          "capturedBodyNote": "input.body.note",
                          "capturedDirectNote": "input.note",
                          "fallbackChain": "input.header.branchId ?? input.query.channel ?? input.note"
                        }
                      }
                    }
                  ]
                },
                { "key": "completed", "type": "Finish", "subType": "Success" }
              ]
            }
            """;

        await using var host = await WorkflowTestHost.StartAsync([httpContextWf]);
        var instance = (await StartAsync(host, "http-context-wf", new { })).Id;

        using var request = host.Request(HttpMethod.Post, $"{Base}/http-context-wf/instances/{instance}/transitions/process?channel=mobile&page=3", new { note = "test-note" });
        request.Headers.Add("X-Branch-Id", "101");

        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var model = (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
        model.State.Should().Be("completed");
        model.Data["capturedHeaderBranch"]!.GetValue<long>().Should().Be(101);
        model.Data["capturedHeaderXBranch"]!.GetValue<long>().Should().Be(101);
        model.Data["capturedHeaderExact"]!.GetValue<long>().Should().Be(101);
        model.Data["capturedQueryChannel"]!.GetValue<string>().Should().Be("mobile");
        model.Data["capturedQueryPage"]!.GetValue<long>().Should().Be(3);
        model.Data["capturedBodyNote"]!.GetValue<string>().Should().Be("test-note");
        model.Data["capturedDirectNote"]!.GetValue<string>().Should().Be("test-note");
        model.Data["fallbackChain"]!.GetValue<long>().Should().Be(101);
    }

    [Fact]
    public async Task Start_instance_automatically_captures_headers_and_query_into_input()
    {
        const string startContextWf = """
            {
              "key": "start-context-wf",
              "start": {
                "key": "start",
                "target": "created",
                "input": {
                  "schema": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": [ "name" ],
                    "properties": { "name": { "type": "string" } }
                  },
                  "set": {
                    "branch": "input.header.branchId",
                    "source": "input.query.source",
                    "product": "input.body.name"
                  }
                }
              },
              "states": [
                { "key": "created", "type": "Finish", "subType": "Success" }
              ]
            }
            """;

        await using var host = await WorkflowTestHost.StartAsync([startContextWf]);

        using var request = host.Request(HttpMethod.Post, $"{Base}/start-context-wf/instances?source=campaign", new { data = new { name = "Bicycle" } });
        request.Headers.Add("X-Branch-Id", "77");

        using var response = await host.Client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var model = (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
        model.Data["branch"]!.GetValue<long>().Should().Be(77);
        model.Data["source"]!.GetValue<string>().Should().Be("campaign");
        model.Data["product"]!.GetValue<string>().Should().Be("Bicycle");
    }

    [Fact]
    public async Task Expressions_safely_fallback_when_headers_or_query_parameters_are_absent()
    {
        const string safeFallbackWf = """
            {
              "key": "safe-fallback-wf",
              "start": {
                "key": "start",
                "target": "initial",
                "input": {
                  "schema": {
                    "type": "object",
                    "properties": { "defaultBranch": { "type": "integer" } }
                  }
                }
              },
              "states": [
                {
                  "key": "initial",
                  "transitions": [
                    {
                      "key": "proceed",
                      "target": "done",
                      "input": {
                        "set": {
                          "resolvedBranch": "input.header.branchId ?? input.query.branchId ?? input.body.branchId ?? data.defaultBranch"
                        }
                      }
                    }
                  ]
                },
                { "key": "done", "type": "Finish", "subType": "Success" }
              ]
            }
            """;

        await using var host = await WorkflowTestHost.StartAsync([safeFallbackWf]);
        var instance = (await StartAsync(host, "safe-fallback-wf", new { data = new { defaultBranch = 999 } })).Id;

        using var response = await SendAsync(host, HttpMethod.Post, $"{Base}/safe-fallback-wf/instances/{instance}/transitions/proceed", new { note = "ok" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var model = (await EnvelopeAsync<WorkflowInstanceModel>(response)).Data!;
        model.Data["resolvedBranch"]!.GetValue<long>().Should().Be(999);
    }

    [Fact]
    public async Task Task_catalog_endpoint_returns_all_built_in_tasks_and_parameters()
    {
        await using var host = await Host();

        using var response = await SendAsync(host, HttpMethod.Get, $"{Base}/tasks");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var envelope = await EnvelopeAsync<List<WorkflowTaskSpecModel>>(response);
        envelope.Result.Should().Be(ResultEnum.Success);
        envelope.Data.Should().NotBeNull();
        envelope.Data!.Select(t => t.Name).Should().Contain([
            "set", "setKey", "loadWorkflow", "queryWorkflow", "updateWorkflow",
            "triggerWorkflow", "startWorkflow", "http", "publish", "rabbitmq",
            "operation", "fanOut", "cache", "agent", "mcp", "log"
        ]);
        envelope.Data.Should().AllSatisfy(t =>
        {
            t.Title.Should().NotBeNullOrWhiteSpace();
            t.Category.Should().NotBeNullOrWhiteSpace();
            t.Description.Should().NotBeNullOrWhiteSpace();
            t.Params.Should().NotBeEmpty();
        });
    }

    [Fact]
    public async Task Validate_and_save_workflow_definition_endpoints_work_at_runtime()
    {
        await using var host = await Host();

        using var getRaw = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/definition");
        getRaw.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawEnvelope = await EnvelopeAsync<System.Text.Json.Nodes.JsonObject>(getRaw);
        rawEnvelope.Data!["key"]!.GetValue<string>().Should().Be("pipeline");

        var invalidDef = new
        {
            key = "custom-flow",
            version = 1,
            start = new { key = "start", target = "missing-state" },
            states = new[] { new { key = "created", type = "Finish", subType = "Success" } }
        };

        using var validateInvalid = await SendAsync(host, HttpMethod.Post, $"{Base}/validate", invalidDef);
        validateInvalid.StatusCode.Should().Be(HttpStatusCode.OK);
        var invalidResult = (await EnvelopeAsync<WorkflowValidationResultModel>(validateInvalid)).Data!;
        invalidResult.IsValid.Should().BeFalse();
        invalidResult.Errors.Should().NotBeEmpty();

        var validDef = new
        {
            key = "custom-flow",
            version = 1,
            title = "Custom Runtime Flow",
            start = new { key = "start", target = "active" },
            states = new object[]
            {
                new
                {
                    key = "active",
                    title = "Active",
                    type = "Intermediate",
                    subType = "Human",
                    onEntry = new object[]
                    {
                        new { key = "init-flag", task = "set", config = new { values = new { ready = "true" } } }
                    },
                    transitions = new object[]
                    {
                        new { key = "complete", title = "Complete", target = "done", trigger = "Manual" }
                    }
                },
                new { key = "done", title = "Done", type = "Finish", subType = "Success" }
            }
        };

        using var validateValid = await SendAsync(host, HttpMethod.Post, $"{Base}/validate", validDef);
        validateValid.StatusCode.Should().Be(HttpStatusCode.OK);
        var validResult = (await EnvelopeAsync<WorkflowValidationResultModel>(validateValid)).Data!;
        validResult.IsValid.Should().BeTrue();
        validResult.Errors.Should().BeEmpty();

        // Saving requires ui-admin role when authorization is enabled
        using var saveForbidden = await SendAsync(host, HttpMethod.Put, $"{Base}/custom-flow/definition", validDef);
        saveForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var saveAllowed = await SendAsync(host, HttpMethod.Put, $"{Base}/custom-flow/definition", validDef, roles: "ui-admin");
        saveAllowed.StatusCode.Should().Be(HttpStatusCode.OK);
        var savedModel = (await EnvelopeAsync<WorkflowDefinitionModel>(saveAllowed)).Data!;
        savedModel.Key.Should().Be("custom-flow");
        savedModel.Title.Should().Be("Custom Runtime Flow");

        // Immediately start an instance of the newly saved workflow
        var instance = await StartAsync(host, "custom-flow", new { });
        instance.State.Should().Be("active");
        instance.Data["ready"]!.GetValue<bool>().Should().BeTrue();

        // B-02: Saving an existing workflow again (even with version = 1 in payload) creates a new immutable version (v2)
        // and keeps v1 intact for the already-running instance.
        var updatedDef = new
        {
            key = "custom-flow",
            version = 1,
            title = "Custom Runtime Flow v2",
            start = new { key = "start", target = "active" },
            states = new object[]
            {
                new
                {
                    key = "active",
                    title = "Active v2",
                    type = "Intermediate",
                    subType = "Human",
                    onEntry = new object[]
                    {
                        new { key = "init-flag", task = "set", config = new { values = new { ready = "true", v2Flag = "true" } } }
                    },
                    transitions = new object[]
                    {
                        new { key = "complete-v2", title = "Complete v2", target = "done", trigger = "Manual" }
                    }
                },
                new { key = "done", title = "Done", type = "Finish", subType = "Success" }
            }
        };

        using var saveV2 = await SendAsync(host, HttpMethod.Put, $"{Base}/custom-flow/definition", updatedDef, roles: "ui-admin");
        saveV2.StatusCode.Should().Be(HttpStatusCode.OK);
        var savedV2Json = (await EnvelopeAsync<System.Text.Json.Nodes.JsonObject>(saveV2)).Data!;
        savedV2Json["version"]!.GetValue<int>().Should().Be(2);

        var registry = host.Services.GetRequiredService<WorkflowRegistry>();
        registry.Get("custom-flow", 1).Title.Should().Be("Custom Runtime Flow");
        registry.Get("custom-flow", 2).Title.Should().Be("Custom Runtime Flow v2");
        registry.GetLatest("custom-flow").Version.Should().Be(2);

        // Existing v1 instance still has v1 transition 'complete'
        var completedV1 = await TransitionAsync(host, "custom-flow", instance.Id, "complete");
        completedV1.Status.Should().Be(WorkflowInstanceStatus.Completed);
        completedV1.WorkflowVersion.Should().Be(1);
    }

    [Fact]
    public async Task Save_workflow_definition_does_not_mutate_registry_when_database_write_fails()
    {
        await using var host = await Host();

        // Install a SQLite trigger that aborts inserts on WorkflowDefinitions (B-01 reproduction)
        await host.WithDbAsync(async db =>
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
                db.Database,
                "CREATE TRIGGER reject_def_write BEFORE INSERT ON WorkflowDefinitions BEGIN SELECT RAISE(ABORT, 'simulated db failure'); END;",
                Ct);
            return 0;
        });

        var updatedPipeline = new
        {
            key = "pipeline",
            version = 1,
            title = "Should Not Persist",
            start = new { key = "start", target = "draft" },
            states = new object[]
            {
                new { key = "draft", title = "Draft", type = "Finish", subType = "Success" }
            }
        };

        using var saveResp = await SendAsync(host, HttpMethod.Put, $"{Base}/pipeline/definition", updatedPipeline, roles: "ui-admin");
        saveResp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var registry = host.Services.GetRequiredService<WorkflowRegistry>();
        registry.GetLatest("pipeline").Version.Should().Be(1);
        registry.GetLatest("pipeline").Title.Should().NotBe("Should Not Persist");
    }

    [Fact]
    public async Task Persisted_definitions_restore_when_AutoCreateSchema_is_false_and_sync_across_nodes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"middt-wf-defs-{Guid.NewGuid():N}.db");
        try
        {
            var connString = $"DataSource={dbPath}";
            const string v1Json = """
                {
                  "key": "shared-flow",
                  "version": 1,
                  "title": "Shared v1",
                  "start": { "key": "start", "target": "done" },
                  "states": [ { "key": "done", "type": "Finish" } ]
                }
                """;

            await using var nodeA = await WorkflowTestHost.StartAsync(
                [v1Json],
                configureDb: db => Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(db, connString));
            await using var nodeB = await WorkflowTestHost.StartAsync(
                [v1Json],
                configure: o => o.AutoCreateSchema = false,
                configureDb: db => Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(db, connString));

            var v2Payload = new
            {
                key = "shared-flow",
                version = 1,
                title = "Shared v2",
                start = new { key = "start", target = "done" },
                states = new object[] { new { key = "done", type = "Finish" } }
            };

            // Save v2 on Node A
            using var saveOnA = await SendAsync(nodeA, HttpMethod.Put, $"{Base}/shared-flow/definition", v2Payload, roles: "ui-admin");
            saveOnA.StatusCode.Should().Be(HttpStatusCode.OK);

            // B-04: Running Node B immediately observes v2 from the shared database without restarting
            var nodeBRegistry = nodeB.Services.GetRequiredService<WorkflowRegistry>();
            nodeBRegistry.GetLatest("shared-flow").Version.Should().Be(2);
            nodeBRegistry.Get("shared-flow", 2).Title.Should().Be("Shared v2");

            // B-03: A newly started Node C with AutoCreateSchema = false restores v2 at startup
            await using var nodeC = await WorkflowTestHost.StartAsync(
                [v1Json],
                configure: o => o.AutoCreateSchema = false,
                configureDb: db => Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(db, connString));

            var nodeCRegistry = nodeC.Services.GetRequiredService<WorkflowRegistry>();
            nodeCRegistry.GetLatest("shared-flow").Version.Should().Be(2);
            nodeCRegistry.GetLatest("shared-flow").Title.Should().Be("Shared v2");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task Version_lifecycle_lists_versions_computes_diff_and_rolls_back_to_new_version()
    {
        await using var host = await Host();

        var v2Payload = new
        {
            key = "pipeline",
            version = 1,
            title = "Pipeline v2",
            start = new { key = "start", target = "draft" },
            states = new object[]
            {
                new
                {
                    key = "draft",
                    title = "Draft v2",
                    type = "Intermediate",
                    subType = "Human",
                    transitions = new object[]
                    {
                        new { key = "escalate", title = "Escalate", target = "done", trigger = "Manual" }
                    }
                },
                new { key = "audit", title = "Audit", type = "Intermediate", subType = "Human" },
                new { key = "done", title = "Done", type = "Finish", subType = "Success" }
            }
        };

        using var saveV2 = await SendAsync(host, HttpMethod.Put, $"{Base}/pipeline/definition", v2Payload, user: "alice", roles: "ui-admin");
        saveV2.StatusCode.Should().Be(HttpStatusCode.OK);

        // List versions (F-01)
        using var versionsResp = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/versions");
        versionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = (await EnvelopeAsync<List<WorkflowVersionSummaryModel>>(versionsResp)).Data!;
        versions.Should().HaveCount(2);
        versions[0].Version.Should().Be(2);
        versions[0].IsLatest.Should().BeTrue();
        versions[0].UpdatedBy.Should().Be("alice");
        versions[1].Version.Should().Be(1);
        versions[1].IsLatest.Should().BeFalse();

        // Diff v1 -> v2 (F-01)
        using var diffResp = await SendAsync(host, HttpMethod.Get, $"{Base}/pipeline/diff?fromVersion=1&toVersion=2");
        diffResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var diff = (await EnvelopeAsync<WorkflowVersionDiffModel>(diffResp)).Data!;
        diff.FromVersion.Should().Be(1);
        diff.ToVersion.Should().Be(2);
        diff.AddedStates.Should().Contain("audit");
        diff.AddedTransitions.Should().Contain("draft:escalate");
        diff.Patch.Should().NotBeEmpty();

        // Rollback to v1 publishes v3 (F-01)
        using var rollbackResp = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/versions/1/rollback", roles: "ui-admin");
        rollbackResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rolledBack = (await EnvelopeAsync<System.Text.Json.Nodes.JsonObject>(rollbackResp)).Data!;
        rolledBack["version"]!.GetValue<int>().Should().Be(3);

        var registry = host.Services.GetRequiredService<WorkflowRegistry>();
        registry.GetLatest("pipeline").Version.Should().Be(3);
        registry.GetLatest("pipeline").States.Select(s => s.Key).Should().NotContain("audit");
    }

    [Fact]
    public async Task Inbox_operations_center_simulation_bundle_and_purge_endpoints_work_end_to_end()
    {
        await using var host = await Host();

        // Start an active human task instance and verify unified inbox (F-03) + built-in trace/correlation metadata (F-04)
        var created = await StartAsync(host, "pipeline", new { key = "ORD-INBOX-1", data = new { name = "Ada", amount = 150 } });
        created.State.Should().Be("draft");

        using var inboxResp = await SendAsync(host, HttpMethod.Get, $"{Base}/inbox");
        inboxResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var inboxEnvelope = await EnvelopeAsync<List<WorkflowInboxItemModel>>(inboxResp);
        inboxEnvelope.Data.Should().ContainSingle(i => i.InstanceId == created.Id && i.Key == "ORD-INBOX-1");
        inboxEnvelope.Data![0].AvailableTransitions.Should().NotBeEmpty();

        // Dry-run process simulator (F-05) runs in memory without writing to DB
        var simReq = new WorkflowSimulationRequest
        {
            Key = "SIM-001",
            Data = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "Grace",
                ["amount"] = 40,
            },
            Steps =
            [
                new WorkflowSimulationStepRequest
                {
                    Transition = "submit",
                    Input = new System.Text.Json.Nodes.JsonObject { ["comment"] = "Fast-track" },
                },
            ],
        };
        using var simResp = await SendAsync(host, HttpMethod.Post, $"{Base}/pipeline/simulate", simReq);
        simResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var simResult = (await EnvelopeAsync<WorkflowSimulationResultModel>(simResp)).Data!;
        simResult.Success.Should().BeTrue();
        simResult.VisitedStates.Should().Contain("draft");
        simResult.ExecutedTransitions.Should().Contain(["start", "submit"]);

        // Operations summary (F-02)
        using var opsResp = await SendAsync(host, HttpMethod.Get, $"{Base}/operations");
        opsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ops = (await EnvelopeAsync<WorkflowOperationsSummaryModel>(opsResp)).Data!;
        ops.Should().NotBeNull();

        // Bundle export & dry-run / live import (F-07 & F-10)
        using var bundleResp = await SendAsync(host, HttpMethod.Get, $"{Base}/bundle");
        bundleResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = (await EnvelopeAsync<WorkflowBundleModel>(bundleResp)).Data!;
        bundle.Items.Should().NotBeEmpty();
        bundle.Checksum.Should().NotBeNullOrWhiteSpace();

        var importDryRun = new WorkflowBundleImportRequest
        {
            DryRun = true,
            ExpectedChecksum = bundle.Checksum,
            Items = bundle.Items,
        };
        using var importResp = await SendAsync(host, HttpMethod.Post, $"{Base}/bundle/import", importDryRun);
        importResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var importResult = (await EnvelopeAsync<WorkflowBundleImportResultModel>(importResp)).Data!;
        importResult.IsValid.Should().BeTrue();
        importResult.ImportedWorkflows.Should().Contain("pipeline");

        // Complete the instance and test retention purge (F-09)
        var submitted = await TransitionAsync(host, "pipeline", created.Id, "submit", new { comment = "Ship" });
        if (submitted.Status != WorkflowInstanceStatus.Completed)
        {
            await TransitionAsync(host, "pipeline", created.Id, "approve", roles: "manager");
        }

        using var purgeDryResp = await SendAsync(host, HttpMethod.Post, $"{Base}/maintenance/purge", new WorkflowPurgeRequest { RetentionDays = 0, DryRun = true }, roles: "ui-admin");
        purgeDryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var purgeDry = (await EnvelopeAsync<WorkflowPurgeResultModel>(purgeDryResp)).Data!;
        purgeDry.PurgedInstancesCount.Should().BeGreaterThanOrEqualTo(1);

        using var purgeLiveResp = await SendAsync(host, HttpMethod.Post, $"{Base}/maintenance/purge", new WorkflowPurgeRequest { RetentionDays = 0, DryRun = false }, roles: "ui-admin");
        purgeLiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var purgeLive = (await EnvelopeAsync<WorkflowPurgeResultModel>(purgeLiveResp)).Data!;
        purgeLive.PurgedInstancesCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Event_stream_delivers_real_time_sse_notifications_on_start_and_transition()
    {
        await using var host = await Host();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        using var sseReq = host.Request(HttpMethod.Get, $"{Base}/events?workflow=pipeline", null, "alice", "clerk", "manager");
        using var sseResp = await host.Client.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        sseResp.StatusCode.Should().Be(HttpStatusCode.OK);
        sseResp.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        await using var stream = await sseResp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Read initial ': connected' comment line
        var firstLine = await reader.ReadLineAsync(cts.Token);
        firstLine.Should().Be(": connected");

        // Trigger a workflow start and transition
        var started = await StartAsync(host, "pipeline", new { key = "ORD-SSE-1", data = new { name = "Ada", amount = 80 } });
        await TransitionAsync(host, "pipeline", started.Id, "submit", new { comment = "SSE test" });

        var received = new List<WorkflowEventStreamModel>();
        while (!received.Any(e => e.State == "review") && !cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var json = line[5..].Trim();
                var ev = System.Text.Json.JsonSerializer.Deserialize<WorkflowEventStreamModel>(json, WorkflowTestHost.Json);
                if (ev is not null && ev.InstanceId == started.Id)
                {
                    received.Add(ev);
                }
            }
        }

        received.Should().Contain(e => e.Type == "Started" && e.Workflow == "pipeline" && e.Key == "ORD-SSE-1");
        received.Should().Contain(e => e.State == "review" && e.Transition == "submit");
    }
}
