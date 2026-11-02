using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Middt.Framework.Agents;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Tests.Workflow;
using Middt.Framework.Ui.Model;
using Middt.Framework.Ui.Rendering;
using Middt.Framework.Ui.Server;
using Middt.Framework.Ui.Server.Rendering;

namespace Middt.Framework.Tests.Agents;

public sealed class MiddtAgentTests
{
    private static readonly Middt.Framework.Workflow.Expressions.WorkflowUser Manager =
        WorkflowTestHost.User("alice@test.com", "manager");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Middt.Framework.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    [Fact]
    public async Task Single_agent_task_executes_with_structured_output_schema_session_persistence_and_metadata()
    {
        const string definition = """
        {
          "key": "ai-credit-check",
          "version": 1,
          "start": {
            "key": "start",
            "target": "underwriting",
            "input": {
              "schema": {
                "type": "object",
                "required": ["applicant", "requestedAmount"],
                "properties": {
                  "applicant": { "type": "string" },
                  "requestedAmount": { "type": "number" }
                }
              }
            },
            "onExecute": [
              {
                "key": "initial-ai-underwriting",
                "task": "agent",
                "config": {
                  "agent": "credit-underwriter",
                  "prompt": "Evaluate credit application for {{ data.applicant }} requesting {{ data.requestedAmount }}.",
                  "context": {
                    "applicant": "{{ data.applicant }}",
                    "requestedAmount": "{{ data.requestedAmount }}"
                  },
                  "sessionInto": "aiSession",
                  "into": "decision",
                  "metaInto": "decisionMeta"
                }
              }
            ]
          },
          "states": [
            {
              "key": "underwriting",
              "type": "Intermediate",
              "subType": "Human",
              "transitions": [
                {
                  "key": "re-evaluate",
                  "target": "$self",
                  "onExecute": [
                    {
                      "key": "followup-ai-underwriting",
                      "task": "agent",
                      "config": {
                        "agent": "credit-underwriter",
                        "prompt": "Re-evaluate with updated collateral.",
                        "session": "aiSession",
                        "sessionInto": "aiSession",
                        "into": "decision",
                        "metaInto": "decisionMeta"
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var outputSchema = JsonNode.Parse("""
        {
          "type": "object",
          "required": ["verdict", "confidence", "rationale"],
          "properties": {
            "verdict": { "type": "string", "enum": ["Approved", "Review", "Rejected"], "default": "Approved" },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.95 },
            "rationale": { "type": "string", "default": "Applicant meets underwriting criteria." }
          }
        }
        """)!.AsObject();

        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            services: s => s.AddMiddtAgents(
                configure: a => a.AddAgent(
                    name: "credit-underwriter",
                    instructions: "You are a credit underwriting specialist.",
                    description: "Evaluates credit risk.",
                    outputSchema: outputSchema)));

        var instance = await host.StartInstanceAsync(
            "ai-credit-check",
            new { applicant = "Anadolu Lojistik A.Ş.", requestedAmount = 250000 },
            user: Manager);

        instance.State.Should().Be("underwriting");
        instance.Data["decision"]!["verdict"]!.GetValue<string>().Should().Be("Approved");
        instance.Data["decision"]!["confidence"]!.GetValue<double>().Should().Be(0.95);
        instance.Data["decision"]!["rationale"]!.GetValue<string>().Should().Be("Applicant meets underwriting criteria.");
        instance.Data["decisionMeta"]!["agent"]!.GetValue<string>().Should().Be("credit-underwriter");
        instance.Data["decisionMeta"]!["mode"]!.GetValue<string>().Should().Be("single");
        instance.Data["decisionMeta"]!["totalTokens"]!.GetValue<long>().Should().BeGreaterThan(0);
        instance.Data["aiSession"].Should().NotBeNull();

        // Multi-turn follow-up restoring serialized AgentSession from data.aiSession
        var updated = await host.TransitionAsync(instance.Id, "re-evaluate", new { }, user: Manager);
        updated.State.Should().Be("underwriting");
        updated.Data["decision"]!["verdict"]!.GetValue<string>().Should().Be("Approved");
        updated.Data["aiSession"].Should().NotBeNull();
    }

    [Fact]
    public async Task Multi_agent_sequential_and_concurrent_workflows_execute_via_AgentWorkflowBuilder()
    {
        const string definition = """
        {
          "key": "multi-agent-pipeline",
          "version": 1,
          "start": {
            "key": "start",
            "target": "completed",
            "input": {
              "schema": {
                "type": "object",
                "required": ["topic"],
                "properties": {
                  "topic": { "type": "string" }
                }
              }
            },
            "onExecute": [
              {
                "key": "run-sequential-agents",
                "task": "ai-agent",
                "config": {
                  "mode": "sequential",
                  "agent": " editorial-pipeline",
                  "agents": [
                    "researcher-agent",
                    {
                      "name": "editor-agent",
                      "instructions": "Refine the research output into a concise summary."
                    }
                  ],
                  "prompt": "Analyze topic: {{ data.topic }}",
                  "into": "sequentialOutput",
                  "metaInto": "sequentialMeta"
                }
              },
              {
                "key": "run-concurrent-agents",
                "task": "ai-agent",
                "config": {
                  "mode": "concurrent",
                  "agents": [
                    { "name": "legal-reviewer", "instructions": "Perform legal check." },
                    { "name": "financial-reviewer", "instructions": "Perform financial check." }
                  ],
                  "prompt": "Review topic: {{ data.topic }}",
                  "into": "concurrentOutput"
                }
              }
            ]
          },
          "states": [
            {
              "key": "completed",
              "type": "Finish",
              "subType": "Success"
            }
          ]
        }
        """;

        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            services: s => s.AddMiddtAgents(
                configure: a => a.AddAgent(
                    name: "researcher-agent",
                    instructions: "Gather key facts on the requested topic.")));

        var instance = await host.StartInstanceAsync(
            "multi-agent-pipeline",
            new { topic = "ISO 20022 Payment Messaging" },
            user: Manager);

        instance.State.Should().Be("completed");
        instance.Status.Should().Be(WorkflowInstanceStatus.Completed);
        instance.Data["sequentialOutput"]!.GetValue<string>().Should().Contain("ISO 20022 Payment Messaging");
        instance.Data["sequentialMeta"]!["mode"]!.GetValue<string>().Should().Be("sequential");
        instance.Data["concurrentOutput"]!.GetValue<string>().Should().Contain("ISO 20022 Payment Messaging");
    }

    [Fact]
    public async Task Agent_invokes_registered_AIFunction_tools_and_validates_output_schema_errors()
    {
        var toolInvokedWith = string.Empty;

        const string definition = """
        {
          "key": "tool-and-schema-test",
          "version": 1,
          "start": {
            "key": "start",
            "target": "active",
            "input": {
              "schema": {
                "type": "object",
                "required": ["amount"],
                "properties": {
                  "amount": { "type": "number" }
                }
              }
            },
            "onExecute": [
              {
                "key": "call-tax-tool",
                "task": "agent",
                "config": {
                  "name": "tax-agent",
                  "instructions": "Calculate tax using the calculate_tax tool.",
                  "tools": ["calculate_tax"],
                  "prompt": "Compute VAT for {{ data.amount }}",
                  "outputSchema": {
                    "type": "object",
                    "required": ["vatAmount", "currency"],
                    "properties": {
                      "vatAmount": { "type": "number", "minimum": 0 },
                      "currency": { "type": "string", "enum": ["TRY", "USD", "EUR"] }
                    }
                  },
                  "into": "taxResult"
                }
              }
            ]
          },
          "states": [
            {
              "key": "active",
              "type": "Intermediate",
              "subType": "Human",
              "transitions": [
                {
                  "key": "trigger-invalid-schema",
                  "target": "$self",
                  "onExecute": [
                    {
                      "key": "bad-schema-output",
                      "task": "agent",
                      "config": {
                        "name": "bad-output-agent",
                        "prompt": "Return invalid output",
                        "outputSchema": {
                          "type": "object",
                          "required": ["vatAmount"],
                          "properties": {
                            "vatAmount": { "type": "number", "minimum": 100 }
                          }
                        },
                        "into": "badResult"
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            services: s => s.AddMiddtAgents(
                configure: a =>
                {
                    a.AddTool(
                        "calculate_tax",
                        ([Description("Base amount")] double amount) =>
                        {
                            toolInvokedWith = $"amount={amount}";
                            return amount * 0.20;
                        },
                        "Calculates 20% VAT for a given amount.");

                    a.ChatClientFactory = (_, _, modelId) => new ToolTestingChatClient(modelId);
                }));

        var instance = await host.StartInstanceAsync(
            "tool-and-schema-test",
            new { amount = 1000 },
            user: Manager);

        toolInvokedWith.Should().Be("amount=1000");
        instance.Data["taxResult"]!["vatAmount"]!.GetValue<double>().Should().Be(200);
        instance.Data["taxResult"]!["currency"]!.GetValue<string>().Should().Be("TRY");

        // Registered tools and WorkflowTaskRegistry tasks are callable via IMiddtAgentRegistry
        var ct = TestContext.Current.CancellationToken;
        var registry = host.Services.GetRequiredService<IMiddtAgentRegistry>();
        var taxTool = (AIFunction)registry.GetTool("calculate_tax")!;
        var taxResult = await taxTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["amount"] = 2500d,
            }),
            ct);
        taxResult?.ToString().Should().Be("500");

        var logTaskTool = (AIFunction)registry.GetTool("log")!;
        logTaskTool.Should().NotBeNull();

        // Triggering an agent whose output violates outputSchema faults the transition as expected
        var faulted = await host.TransitionAsync(instance.Id, "trigger-invalid-schema", new { }, user: Manager);
        faulted.Status.Should().Be(WorkflowInstanceStatus.Faulted);
    }

    [Fact]
    public async Task Demo_invoice_collection_executes_ai_risk_audit_and_renders_in_invoice_detail_layout()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Workflows");
        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Agents");
        var layoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Layouts", "invoice-detail.json");
        var workflowDefinitions = Directory.GetFiles(workflowsDir, "*.json").Select(File.ReadAllText).ToArray();

        await using var host = await WorkflowTestHost.StartAsync(
            workflowDefinitions,
            services: s => s.AddMiddtAgents(configure: a => a.AddAgentsFromDirectory(agentsDir)));

        var invoice = await host.StartInstanceAsync(
            "invoice-collection",
            new
            {
                invoiceNo = "FAT-2026-9001",
                customerName = "Anadolu Lojistik A.Ş.",
                total = 59000,
                description = "AI Risk Analizi Test Faturası",
            },
            user: Manager);

        invoice.AvailableTransitions.Select(t => t.Key).Should().Contain("ai-risk-audit");

        var audited = await host.TransitionAsync(invoice.Id, "ai-risk-audit", new { }, user: Manager);
        audited.State.Should().Be("awaiting-payment");

        // instance.Data contains ONLY flow business data (no agent/session/meta clutter)
        audited.Data["aiAudit"].Should().BeNull();
        audited.Data["lastAuditedWorkflow"].Should().BeNull();
        audited.Data["invoiceNo"]!.GetValue<string>().Should().Be("FAT-2026-9001");

        // Dedicated tables / instance.Agents hold the agent run, metadata, and tool calls
        var auditorNode = audited.Agents["invoiceRiskAuditor"]!;
        auditorNode["riskLevel"]!.GetValue<string>().Should().Be("Düşük");
        auditorNode["score"]!.GetValue<double>().Should().Be(92);
        auditorNode["summary"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        auditorNode["recommendation"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        auditorNode["agent"]!.GetValue<string>().Should().Be("invoice-risk-auditor");
        audited.AgentRuns.Should().ContainSingle(r => r.AgentName == "invoice-risk-auditor");
        audited.ToolCalls.Should().Contain(tc => tc.Name == "audit_invoice_payments");

        // Simulate PostgreSQL jsonb key reordering (shorter keys like "text" / "name" sorted before "$type")
        // in the persisted WorkflowAgentSessions table and verify a second multi-turn execution of ai-risk-audit succeeds cleanly.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Middt.Framework.Workflow.Persistence.WorkflowDbContext>();
            var sessionRow = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.AgentSessions,
                s => s.InstanceId == invoice.Id && s.AgentName == "invoice-risk-auditor",
                ct);
            sessionRow.TurnCount.Should().Be(1);
            var mutatedSession = SimulatePostgresJsonbKeyOrder(JsonNode.Parse(sessionRow.SessionData))!;
            sessionRow.SessionData = mutatedSession.ToJsonString();
            await db.SaveChangesAsync(ct);
        }

        var reAudited = await host.TransitionAsync(invoice.Id, "ai-risk-audit", new { }, user: Manager);
        reAudited.State.Should().Be("awaiting-payment");
        reAudited.Status.Should().Be(WorkflowInstanceStatus.Active);
        reAudited.Agents["invoiceRiskAuditor"]!["riskLevel"]!.GetValue<string>().Should().Be("Düşük");
        reAudited.AgentRuns.Count(r => r.AgentName == "invoice-risk-auditor").Should().Be(2);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Middt.Framework.Workflow.Persistence.WorkflowDbContext>();
            var sessionRow = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                db.AgentSessions,
                s => s.InstanceId == invoice.Id && s.AgentName == "invoice-risk-auditor",
                ct);
            sessionRow.TurnCount.Should().Be(2);
        }

        var layout = UiJson.Parse(await File.ReadAllTextAsync(layoutPath, ct));
        var uiOptions = new MiddtUiOptions();
        var html = new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
            layout,
            new Dictionary<string, JsonNode?>
            {
                [UiExpressions.Data] = audited.Data.DeepClone(),
                [UiExpressions.Instance] = new JsonObject
                {
                    ["state"] = audited.State,
                    ["stateTitle"] = audited.StateTitle,
                    ["workflow"] = audited.Workflow,
                    ["agents"] = audited.Agents.DeepClone(),
                    ["agentRuns"] = System.Text.Json.JsonSerializer.SerializeToNode(audited.AgentRuns, System.Text.Json.JsonSerializerOptions.Web),
                    ["toolCalls"] = System.Text.Json.JsonSerializer.SerializeToNode(audited.ToolCalls, System.Text.Json.JsonSerializerOptions.Web),
                },
            },
            new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
            CultureInfo.InvariantCulture);

        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);
        decodedHtml.Should().Contain("AI Risk Analizi");
        decodedHtml.Should().Contain("Düşük");
        decodedHtml.Should().Contain("invoice-risk-auditor");
    }

    [Fact]
    public async Task Mcp_task_executes_callTool_listTools_readResource_listResources_and_getPrompt()
    {
        const string definition = """
        {
          "key": "mcp-workflow-test",
          "version": 1,
          "start": {
            "key": "start",
            "target": "inspected",
            "input": {
              "schema": {
                "type": "object",
                "required": ["customerName", "amount", "category"],
                "properties": {
                  "customerName": { "type": "string" },
                  "amount": { "type": "number" },
                  "category": { "type": "string" }
                }
              }
            }
          },
          "states": [
            {
              "key": "inspected",
              "onEntry": [
                {
                  "task": "mcp",
                  "config": {
                    "server": "banking-mcp",
                    "action": "callTool",
                    "tool": "lookup_chargeback_history",
                    "arguments": {
                      "customerName": "{{ data.customerName }}",
                      "amount": "{{ data.amount }}",
                      "category": "{{ data.category }}"
                    },
                    "into": "historyResult",
                    "metaInto": "historyMeta"
                  }
                },
                {
                  "task": "mcp",
                  "config": {
                    "server": "banking-mcp",
                    "action": "listTools",
                    "into": "availableTools"
                  }
                },
                {
                  "task": "mcp",
                  "config": {
                    "server": "banking-mcp",
                    "action": "readResource",
                    "uri": "mcp://banking/policies/chargeback-2026",
                    "into": "policyResource"
                  }
                },
                {
                  "task": "mcp",
                  "config": {
                    "server": "banking-mcp",
                    "action": "listResources",
                    "into": "availableResources"
                  }
                },
                {
                  "task": "mcp",
                  "config": {
                    "server": "banking-mcp",
                    "action": "getPrompt",
                    "prompt": "dispute-triage-prompt",
                    "arguments": {
                      "disputeNo": "ITR-900",
                      "customerName": "{{ data.customerName }}",
                      "amount": "{{ data.amount }}",
                      "category": "{{ data.category }}"
                    },
                    "into": "renderedPrompt"
                  }
                }
              ]
            }
          ]
        }
        """;

        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Agents");
        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            services: s => s.AddMiddtAgents(configure: a => a.AddAgentsFromDirectory(agentsDir)));

        var instance = await host.StartInstanceAsync(
            "mcp-workflow-test",
            new { customerName = "Zeynep Kaya", amount = 32000, category = "Yetkisiz İşlem" },
            user: Manager);

        instance.State.Should().Be("inspected");
        instance.Data["historyResult"]!["priorDisputes"]!.GetValue<int>().Should().Be(2);
        instance.Data["historyResult"]!["velocityScore"]!.GetValue<int>().Should().Be(88);
        instance.Data["historyResult"]!["merchantRisk"]!.GetValue<string>().Should().Be("Yüksek");
        instance.Data["historyMeta"]!["server"]!.GetValue<string>().Should().Be("banking-mcp");
        instance.Data["historyMeta"]!["action"]!.GetValue<string>().Should().Be("calltool");

        instance.Data["availableTools"]!.AsArray().Count.Should().Be(2);
        instance.Data["policyResource"]!["policyVersion"]!.GetValue<string>().Should().Be("CB-2026.4");
        instance.Data["policyResource"]!["autoApproveLimit"]!.GetValue<int>().Should().Be(5000);
        instance.Data["availableResources"]!.AsArray().Count.Should().Be(1);
        instance.Data["renderedPrompt"]!.GetValue<string>().Should().Contain("ITR-900").And.Contain("Zeynep Kaya");
    }

    [Fact]
    public async Task Demo_ai_dispute_resolution_workflow_routes_auto_refund_specialist_review_and_fraud_via_agent_tools_and_mcp()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Workflows");
        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Agents");
        var detailLayoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Layouts", "ai-dispute-detail.json");
        var resultLayoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Layouts", "ai-dispute-result.json");
        var workflowDefinitions = Directory.GetFiles(workflowsDir, "*.json").Select(File.ReadAllText).ToArray();

        await using var host = await WorkflowTestHost.StartAsync(
            workflowDefinitions,
            services: s => s.AddMiddtAgents(configure: a => a.AddAgentsFromDirectory(agentsDir)));

        // 1. Low-amount dispute (2450 <= 5000): AI Orchestrator invokes tools + MCP and automatically routes to auto-resolved
        var autoResolved = await host.StartInstanceAsync(
            "ai-dispute-resolution",
            new
            {
                disputeNo = "ITR-2026-2001",
                customerName = "Ahmet Yılmaz",
                merchantName = "Bulut Market Dijital A.Ş.",
                transactionRef = "TRX-2026-7001",
                amount = 2450,
                category = "Mükerrer Çekim",
                reason = "Aynı işlem iki kez tahsil edildi.",
            },
            user: Manager);

        autoResolved.State.Should().Be("auto-resolved");

        // Verify instance.Data contains ONLY flow business data (no aiMeta, aiToolCalls, aiDecision, mcpPolicy, mcpReceipt)
        autoResolved.Data.Select(kv => kv.Key).Should().BeEquivalentTo(
            ["disputeNo", "customerName", "merchantName", "transactionRef", "amount", "category", "reason", "createdBy", "createdAt", "resolution"]);
        autoResolved.Data["resolution"]!["status"]!.GetValue<string>().Should().Be("AI Otomatik İade Onayı");
        autoResolved.Data["resolution"]!["refundedAmount"]!.GetValue<double>().Should().Be(2450);

        // Verify dedicated agent/MCP projections in instance.Agents, instance.AgentRuns, and instance.ToolCalls
        var orchestrator = autoResolved.Agents["disputeOrchestrator"]!;
        orchestrator["selectedTransition"]!.GetValue<string>().Should().Be("route-auto-refund");
        var allowedArray = orchestrator["allowedTransitions"]!.AsArray();
        allowedArray.Select(x => x!["key"]!.GetValue<string>())
            .Should().BeEquivalentTo(["route-auto-refund", "route-fraud-investigation", "route-specialist-review"]);
        allowedArray.Select(x => x!["description"]!.GetValue<string>())
            .Should().AllSatisfy(d => d.Should().NotBeNullOrWhiteSpace());
        allowedArray.Select(x => x!["title"]!.GetValue<string>())
            .Should().AllSatisfy(t => t.Should().NotBeNullOrWhiteSpace());
        autoResolved.AgentRuns.First(r => r.AgentName == "dispute-orchestrator").AllowedTransitions
            .Should().Contain(t => t.Key == "route-auto-refund" && !string.IsNullOrWhiteSpace(t.Description));
        orchestrator["decision"]!.GetValue<string>().Should().Be("Otomatik İade Onayı");
        orchestrator["riskLevel"]!.GetValue<string>().Should().Be("Düşük");
        orchestrator["recommendedRefund"]!.GetValue<double>().Should().Be(2450);
        orchestrator["policyCode"]!.GetValue<string>().Should().Be("CB-FAST-2026");
        autoResolved.Agents["readChargebackPolicy"]!["policyVersion"]!.GetValue<string>().Should().Be("CB-2026.4");
        autoResolved.Agents["verifyMerchantReceipt"]!["settlementStatus"]!.GetValue<string>().Should().Be("Otomatik İadeye Uygun");
        autoResolved.ToolCalls.Count.Should().BeGreaterThanOrEqualTo(4);

        // 2. Medium-amount dispute (14500): AI Orchestrator routes to specialist-review; specialist runs Copilot + MCP audit + approves
        var specialistCase = await host.StartInstanceAsync(
            "ai-dispute-resolution",
            new
            {
                disputeNo = "ITR-2026-2002",
                customerName = "Anadolu Lojistik A.Ş.",
                merchantName = "Global Elektronik A.Ş.",
                transactionRef = "TRX-2026-7002",
                amount = 14500,
                category = "Hizmet Alınamadı",
                reason = "Sipariş edilen ekipman teslim edilmedi.",
            },
            user: Manager);

        specialistCase.State.Should().Be("specialist-review");
        specialistCase.Agents["disputeOrchestrator"]!["selectedTransition"]!.GetValue<string>().Should().Be("route-specialist-review");
        specialistCase.Agents["disputeOrchestrator"]!["decision"]!.GetValue<string>().Should().Be("Uzman İncelemesi");
        specialistCase.Agents["disputeOrchestrator"]!["riskLevel"]!.GetValue<string>().Should().Be("Orta");

        var afterCopilot = await host.TransitionAsync(
            specialistCase.Id,
            "re-evaluate-ai",
            new { specialistNote = "Kargo teslim tutanağı bulunamadı, müşteri haklı." },
            user: Manager);

        afterCopilot.State.Should().Be("specialist-review");
        // Both agents (dispute-orchestrator AND dispute-specialist-copilot) coexist in instance.Agents and WorkflowAgentSessions/Runs!
        afterCopilot.Agents["disputeOrchestrator"]!["decision"]!.GetValue<string>().Should().Be("Uzman İncelemesi");
        afterCopilot.Agents["disputeSpecialistCopilot"]!["approvedRefundAmount"]!.GetValue<double>().Should().Be(14500);
        afterCopilot.Agents["disputeSpecialistCopilot"]!["totalCredit"]!.GetValue<double>().Should().Be(14650);
        afterCopilot.Agents["disputeSpecialistCopilot"]!["toolCalls"]!.AsArray().Should().NotBeEmpty();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Middt.Framework.Workflow.Persistence.WorkflowDbContext>();
            var sessions = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.AgentSessions.Where(s => s.InstanceId == specialistCase.Id),
                ct);
            sessions.Select(s => s.AgentName).Should().Contain(["dispute-orchestrator", "dispute-specialist-copilot"]);
        }

        var afterMcpAudit = await host.TransitionAsync(
            specialistCase.Id,
            "invoke-mcp-audit",
            new { },
            user: Manager);

        afterMcpAudit.Agents["lookupChargebackHistory"]!["velocityScore"]!.GetValue<int>().Should().Be(45);

        // Verify Server-Driven UI detail layout renders AI & MCP sections
        var detailLayout = UiJson.Parse(await File.ReadAllTextAsync(detailLayoutPath, ct));
        var uiOptions = new MiddtUiOptions();
        var detailHtml = System.Net.WebUtility.HtmlDecode(
            new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
                detailLayout,
                new Dictionary<string, JsonNode?>
                {
                    [UiExpressions.Data] = afterMcpAudit.Data.DeepClone(),
                    [UiExpressions.Instance] = new JsonObject
                    {
                        ["state"] = afterMcpAudit.State,
                        ["stateTitle"] = afterMcpAudit.StateTitle,
                        ["workflow"] = afterMcpAudit.Workflow,
                        ["agents"] = afterMcpAudit.Agents.DeepClone(),
                        ["agentRuns"] = System.Text.Json.JsonSerializer.SerializeToNode(afterMcpAudit.AgentRuns, System.Text.Json.JsonSerializerOptions.Web),
                        ["toolCalls"] = System.Text.Json.JsonSerializer.SerializeToNode(afterMcpAudit.ToolCalls, System.Text.Json.JsonSerializerOptions.Web),
                    },
                },
                new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
                CultureInfo.InvariantCulture));

        detailHtml.Should().Contain("AI Orkestratör Kararı ve Akış Yönlendirmesi");
        detailHtml.Should().Contain("MCP (Model Context Protocol) Politika ve İşyeri Mutabakatı");
        detailHtml.Should().Contain("Çağrılan AI Araçları (Function / MCP Tool Calls)");
        detailHtml.Should().Contain("AI Copilot Uzman Değerlendirmesi");

        var specialistApproved = await host.TransitionAsync(
            specialistCase.Id,
            "approve-refund",
            new { refundedAmount = 14500, note = "Uzman incelemesiyle iade onaylandı." },
            user: Manager);

        specialistApproved.State.Should().Be("resolved-refunded");
        specialistApproved.Data["resolution"]!["policyCode"]!.GetValue<string>().Should().Be("CB-STD-2026");

        // 3. High-risk / unauthorized transaction (38000, Yetkisiz İşlem): AI Orchestrator routes to fraud-investigation
        var fraudCase = await host.StartInstanceAsync(
            "ai-dispute-resolution",
            new
            {
                disputeNo = "ITR-2026-2003",
                customerName = "Zeynep Kaya",
                merchantName = "Yurt Dışı Döviz Oyun Ltd.",
                transactionRef = "TRX-2026-7003",
                amount = 38000,
                category = "Yetkisiz İşlem",
                reason = "Bilgim dışında gece yarısı işlem yapılmış.",
            },
            user: Manager);

        fraudCase.State.Should().Be("fraud-investigation");
        fraudCase.Agents["disputeOrchestrator"]!["selectedTransition"]!.GetValue<string>().Should().Be("route-fraud-investigation");
        fraudCase.Agents["disputeOrchestrator"]!["decision"]!.GetValue<string>().Should().Be("Fraud Eskalasyonu");
        fraudCase.Agents["disputeOrchestrator"]!["riskLevel"]!.GetValue<string>().Should().Be("Yüksek");
        fraudCase.Agents["disputeOrchestrator"]!["riskScore"]!.GetValue<double>().Should().Be(89);

        var committeeEvaluated = await host.TransitionAsync(
            fraudCase.Id,
            "run-multi-agent-committee",
            new { },
            user: Manager);
        committeeEvaluated.State.Should().Be("fraud-investigation");
        committeeEvaluated.Agents["fraudCommittee"].Should().NotBeNull();
        committeeEvaluated.Agents["fraudCommittee"]!["mode"]!.GetValue<string>().Should().Be("sequential");
        committeeEvaluated.Agents["disputeOrchestrator"]!["decision"]!.GetValue<string>().Should().Be("Fraud Eskalasyonu");

        var fraudResolved = await host.TransitionAsync(
            fraudCase.Id,
            "confirm-fraud-refund",
            new { securityAction = "Kart Kapatıldı & Yenilendi", note = "Yetkisiz işlem teyit edildi ve iade yapıldı." },
            user: Manager);

        fraudResolved.State.Should().Be("resolved-refunded");
        fraudResolved.Data["resolution"]!["securityAction"]!.GetValue<string>().Should().Be("Kart Kapatıldı & Yenilendi");

        var resultLayout = UiJson.Parse(await File.ReadAllTextAsync(resultLayoutPath, ct));
        var resultHtml = System.Net.WebUtility.HtmlDecode(
            new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
                resultLayout,
                new Dictionary<string, JsonNode?>
                {
                    [UiExpressions.Data] = fraudResolved.Data.DeepClone(),
                    [UiExpressions.Instance] = new JsonObject
                    {
                        ["state"] = fraudResolved.State,
                        ["stateTitle"] = fraudResolved.StateTitle,
                        ["workflow"] = fraudResolved.Workflow,
                        ["agents"] = fraudResolved.Agents.DeepClone(),
                        ["agentRuns"] = System.Text.Json.JsonSerializer.SerializeToNode(fraudResolved.AgentRuns, System.Text.Json.JsonSerializerOptions.Web),
                        ["toolCalls"] = System.Text.Json.JsonSerializer.SerializeToNode(fraudResolved.ToolCalls, System.Text.Json.JsonSerializerOptions.Web),
                    },
                },
                new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
                CultureInfo.InvariantCulture));

        resultHtml.Should().Contain("Kart Kapatıldı & Yenilendi");
        resultHtml.Should().Contain("route-fraud-investigation");
    }

    [Fact]
    public async Task Agent_tools_execute_registered_workflow_tasks_with_ambient_workflow_context()
    {
        const string definition = """
        {
          "key": "ai-task-bridge-flow",
          "version": 1,
          "start": {
            "key": "start",
            "target": "completed",
            "input": {
              "schema": {
                "type": "object",
                "required": ["orderId", "baseAmount", "customerSegment"],
                "properties": {
                  "orderId": { "type": "string" },
                  "baseAmount": { "type": "number" },
                  "customerSegment": { "type": "string" }
                }
              }
            },
            "onExecute": [
              {
                "key": "run-pricing-agent",
                "task": "aiAgent",
                "config": {
                  "agent": "pricing-agent",
                  "prompt": "Calculate discounted pricing for order {{ data.orderId }}.",
                  "context": {
                    "orderId": "{{ data.orderId }}",
                    "baseAmount": "{{ data.baseAmount }}",
                    "customerSegment": "{{ data.customerSegment }}"
                  },
                  "into": "pricingResult",
                  "metaInto": "pricingMeta"
                }
              }
            ]
          },
          "states": [
            {
              "key": "completed",
              "type": "Finish",
              "subType": "Success"
            }
          ]
        }
        """;

        var pricingSchema = JsonNode.Parse("""
        {
          "type": "object",
          "required": ["finalPrice", "discountRate", "segmentTag", "workflowKey", "bureauStatus", "externalBureauRef"],
          "properties": {
            "finalPrice": { "type": "number", "default": 0 },
            "discountRate": { "type": "number", "default": 0 },
            "segmentTag": { "type": "string", "default": "STD" },
            "workflowKey": { "type": "string", "default": "unknown" },
            "bureauStatus": { "type": "string", "default": "Pending" },
            "externalBureauRef": { "type": "string", "default": "NONE" }
          }
        }
        """)!.AsObject();

        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            services: s =>
            {
                s.AddMemoryCache();
                s.AddMiddtAgents(
                    configure: a =>
                    {
                        // 1. Declarative tool using workflow "set" task + WriteToWorkflowData = true
                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "compute_segment_pricing",
                            Kind = "task",
                            Task = "set",
                            Description = "Computes discount and final price using the workflow set task and ambient instance context.",
                            WriteToWorkflowData = true,
                            Into = "pricingBreakdown",
                            Parameters = JsonNode.Parse("""
                            {
                              "type": "object",
                              "properties": {
                                "baseAmount": { "type": "number" },
                                "customerSegment": { "type": "string" }
                              }
                            }
                            """)!.AsObject(),
                            Config = JsonNode.Parse("""
                            {
                              "values": {
                                "pricingBreakdown.discountRate": "args.customerSegment == \"VIP\" ? 0.2 : 0.05",
                                "pricingBreakdown.finalPrice": "args.baseAmount * (args.customerSegment == \"VIP\" ? 0.8 : 0.95)",
                                "pricingBreakdown.workflowKey": "instance.workflow",
                                "auditTrailTag": "data.orderId + \":\" + args.customerSegment"
                              }
                            }
                            """)!.AsObject(),
                        });

                        // 2. Declarative tool using workflow "cache" task wrapping an inner "set" subTask
                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "cached_segment_tag",
                            Kind = "cache",
                            Description = "Caches segment tag computation via the workflow cache task.",
                            Parameters = JsonNode.Parse("""
                            {
                              "type": "object",
                              "properties": {
                                "customerSegment": { "type": "string" }
                              }
                            }
                            """)!.AsObject(),
                            Into = "cachedTag",
                            Config = JsonNode.Parse("""
                            {
                              "key": "segment-tag:{{ args.customerSegment }}",
                              "ttl": "PT5M",
                              "into": "cachedTag",
                              "subTask": {
                                "task": "set",
                                "config": {
                                  "into": "cachedTag",
                                  "values": {
                                    "cachedTag.segmentTag": "\"SEG-\" + args.customerSegment"
                                  }
                                }
                              }
                            }
                            """)!.AsObject(),
                        });

                        // 3. Declarative tool using workflow "http" task
                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "verify_order_http_gateway",
                            Kind = "task",
                            Task = "http",
                            Description = "Calls the HTTP gateway endpoint via the workflow http task.",
                            Parameters = JsonNode.Parse("""
                            {
                              "type": "object",
                              "properties": {
                                "orderId": { "type": "string" },
                                "baseAmount": { "type": "number" }
                              }
                            }
                            """)!.AsObject(),
                            Config = JsonNode.Parse("""
                            {
                              "method": "POST",
                              "url": "http://mockoon:3000/api/v1/PricingGateway/ping",
                              "headers": {
                                "X-Pricing-Segment": "{{ data.customerSegment }}"
                              },
                              "body": {
                                "orderId": "{{ args.orderId }}",
                                "amount": "{{ args.baseAmount }}"
                              }
                            }
                            """)!.AsObject(),
                        });

                        a.AddAgent(
                            name: "pricing-agent",
                            instructions: "Use compute_segment_pricing, cached_segment_tag, and verify_order_http_gateway to calculate pricing.",
                            tools: ["compute_segment_pricing", "cached_segment_tag", "verify_order_http_gateway"],
                            outputSchema: pricingSchema);
                    });
            });

        var instance = await host.StartInstanceAsync(
            "ai-task-bridge-flow",
            new
            {
                orderId = "ORD-900",
                baseAmount = 1000,
                customerSegment = "VIP",
            },
            user: Manager);

        instance.State.Should().Be("completed");
        instance.Data["pricingResult"]!["discountRate"]!.GetValue<double>().Should().Be(0.2);
        instance.Data["pricingResult"]!["finalPrice"]!.GetValue<double>().Should().Be(800);
        instance.Data["pricingResult"]!["segmentTag"]!.GetValue<string>().Should().Be("SEG-VIP");
        instance.Data["pricingResult"]!["workflowKey"]!.GetValue<string>().Should().Be("ai-task-bridge-flow");
        instance.Data["pricingResult"]!["bureauStatus"]!.GetValue<string>().Should().Be("Onaylandı (KKB HTTP Gateway)");
        instance.Data["pricingResult"]!["externalBureauRef"]!.GetValue<string>().Should().StartWith("KKB-HTTP-");

        // WriteToWorkflowData = true persisted both into path and mutated workflow data fields back to instance.Data
        instance.Data["auditTrailTag"]!.GetValue<string>().Should().Be("ORD-900:VIP");
        instance.Data["pricingBreakdown"]!["finalPrice"]!.GetValue<double>().Should().Be(800);
        instance.Data["pricingMeta"]!["toolCallCount"]!.GetValue<int>().Should().Be(3);
        instance.ToolCalls.Should().Contain(tc =>
            tc.Name == "verify_order_http_gateway" &&
            tc.Result!["gateway"]!.GetValue<string>() == "PricingGateway");
    }

    [Fact]
    public async Task Agent_enforces_mcp_allowed_hosts_tool_role_rbac_and_max_tool_calls_budget()
    {
        const string definition = """
        {
          "key": "ai-policy-budget-flow",
          "version": 1,
          "start": {
            "key": "start",
            "target": "done",
            "onExecute": [
              {
                "key": "run-policy-agent",
                "task": "aiAgent",
                "config": {
                  "agent": "policy-agent",
                  "prompt": "Run tool checks.",
                  "maxToolCalls": 1,
                  "into": "result"
                }
              }
            ]
          },
          "states": [
            { "key": "done", "type": "Finish", "subType": "Success" }
          ]
        }
        """;

        await using var host = await WorkflowTestHost.StartAsync(
            [definition],
            configure: o =>
            {
                o.AllowedHttpHosts.Add("allowed.internal");
                o.ExposeErrorDetails = true;
            },
            services: s =>
            {
                s.AddMiddtAgents(
                    configure: a =>
                    {
                        a.AddMcpServer(new McpServerDefinition
                        {
                            Name = "untrusted-remote-mcp",
                            Endpoint = "https://evil.example.com/mcp",
                        });

                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "manager_only_tool",
                            Kind = "expression",
                            Roles = ["manager"],
                            Expression = "42",
                        });

                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "public_tool_1",
                            Kind = "expression",
                            Expression = "1",
                        });

                        a.AddDeclarativeTool(new AgentToolDefinition
                        {
                            Name = "public_tool_2",
                            Kind = "expression",
                            Expression = "2",
                        });

                        a.AddAgent(new AgentDefinition
                        {
                            Name = "policy-agent",
                            Instructions = "Execute tools.",
                            Tools = ["manager_only_tool", "public_tool_1", "public_tool_2"],
                        });
                    });
            });

        // 1. R-08: Remote MCP server whose host is not in AllowedHttpHosts is blocked before any HTTP call
        var mcpClient = host.Services.GetRequiredService<IMiddtMcpClient>();
        var mcpAct = () => mcpClient.ListToolsAsync("untrusted-remote-mcp");
        await mcpAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AllowedHttpHosts*");

        // 2. F-08: Non-manager user runs policy-agent: manager_only_tool is filtered out by RBAC,
        // and calling 2 public tools when maxToolCalls == 1 faults the instance with a budget error.
        var nonManager = new Middt.Framework.Workflow.Expressions.WorkflowUser { Id = "u-1", Name = "bob", Roles = ["clerk"] };
        var instance = await host.StartInstanceAsync("ai-policy-budget-flow", new { }, user: nonManager);
        instance.Status.Should().Be(WorkflowInstanceStatus.Faulted);
        instance.Error.Should().Contain("exceeded maximum tool calls");
    }

    [Fact]
    public async Task Agent_studio_crud_validation_and_live_playground_endpoints_roundtrip_end_to_end()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await WorkflowTestHost.StartAsync([]);
        host.Client.DefaultRequestHeaders.Add("X-Test-User", "studio-admin");
        host.Client.DefaultRequestHeaders.Add("X-Test-Roles", "ui-admin,manager");

        var session = new Middt.Framework.Tests.Client.CountingSessionState("test-token", "rt");
        var tokens = new Middt.Framework.Tests.Client.FakeTokenService(_ => new Middt.Framework.Model.Authentication.TokenResponseModel());
        var agentClient = new Middt.Framework.Client.Http.AgentApiClient(
            host.Client,
            session,
            tokens,
            Middt.Framework.Tests.Client.NullLoggers.For<Middt.Framework.Client.Http.AgentApiClient>());

        // 1. Save a declarative tool via PUT /api/agents/tools/{name}
        var saveToolResp = await agentClient.SaveToolAsync(new AgentToolDefinitionModel
        {
            Name = "calc_refund_bonus",
            Description = "Calculates refund bonus amount from disputedAmount.",
            Kind = "expression",
            Parameters = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["disputedAmount"],
              "properties": {
                "disputedAmount": { "type": "number" }
              }
            }
            """)!.AsObject(),
            Output = JsonNode.Parse("""
            {
              "bonus": "{{ args.disputedAmount * 0.1 }}",
              "eligible": "{{ args.disputedAmount >= 100 }}"
            }
            """)!.AsObject(),
        }, ct);
        saveToolResp.IsSuccess.Should().BeTrue();
        saveToolResp.Data!.Name.Should().Be("calc_refund_bonus");

        // 2. Save an MCP server via PUT /api/agents/mcp-servers/{name}
        var saveMcpResp = await agentClient.SaveMcpServerAsync(new McpServerDefinitionModel
        {
            Name = "studio-mcp",
            Description = "In-process test MCP server created in Studio.",
            Tools =
            [
                new AgentToolDefinitionModel
                {
                    Name = "lookup_merchant_tier",
                    Kind = "expression",
                    Description = "Looks up merchant risk tier.",
                    Output = JsonNode.Parse("""{ "tier": "GOLD", "verified": true }""")!.AsObject(),
                },
            ],
        }, ct);
        saveMcpResp.IsSuccess.Should().BeTrue();
        saveMcpResp.Data!.Name.Should().Be("studio-mcp");

        // 3. Save an AI agent via PUT /api/agents/{name}
        var saveAgentResp = await agentClient.SaveAgentAsync(new AgentDefinitionModel
        {
            Name = "studio-dispute-agent",
            Description = "Evaluates disputes in Studio.",
            Instructions = "Evaluate dispute for {{ data.customerName }} ({{ data.disputedAmount }} TRY).",
            Tools = ["calc_refund_bonus"],
            McpServers = ["studio-mcp"],
            AllowedTransitions = ["route-auto-refund", "route-specialist-review"],
            OutputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "required": ["verdict", "confidence", "nextTransition"],
              "properties": {
                "verdict": { "type": "string", "default": "AUTO_REFUND" },
                "confidence": { "type": "number", "default": 0.94 },
                "nextTransition": { "type": "string", "default": "route-auto-refund" }
              }
            }
            """)!.AsObject(),
        }, ct);
        saveAgentResp.IsSuccess.Should().BeTrue();
        saveAgentResp.Data!.Name.Should().Be("studio-dispute-agent");

        // 4. GET /api/agents/catalog returns the persisted agent, tool, MCP server, and runtime provider metadata
        var catalogResp = await agentClient.GetCatalogAsync(ct);
        catalogResp.IsSuccess.Should().BeTrue();
        catalogResp.Data!.Agents.Should().Contain(a => a.Name == "studio-dispute-agent");
        catalogResp.Data.Tools.Should().Contain(t => t.Name == "calc_refund_bonus");
        catalogResp.Data.McpServers.Should().Contain(m => m.Name == "studio-mcp");

        // 4b. A newly started MiddtAgentRegistry instance loads the persisted __agents__ catalog from WorkflowDbContext without recursion
        var freshMcp = new MiddtMcpClient(
            host.Services,
            Options.Create(new AgentSettings()),
            new MiddtAgentOptions());
        var freshRegistry = new MiddtAgentRegistry(
            host.Services,
            Options.Create(new AgentSettings()),
            new MiddtAgentOptions(),
            freshMcp,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        freshRegistry.GetDefinition("studio-dispute-agent").Should().NotBeNull();
        freshRegistry.GetTool("calc_refund_bonus").Should().NotBeNull();
        freshMcp.GetServer("studio-mcp").Should().NotBeNull();

        // 5. POST /api/agents/validate catches broken references and approves valid catalogs
        var invalidBody = JsonNode.Parse("""
        {
          "agents": [
            {
              "name": "broken-agent",
              "instructions": "Test",
              "tools": ["non_existent_tool"],
              "mcpServers": ["missing_mcp"]
            }
          ]
        }
        """)!.AsObject();
        var invalidCatalogResp = await agentClient.ValidateAsync(invalidBody, ct);
        invalidCatalogResp.IsSuccess.Should().BeTrue();
        invalidCatalogResp.Data!.Valid.Should().BeFalse();
        invalidCatalogResp.Data.Errors.Should().HaveCountGreaterThanOrEqualTo(2);

        var validBody = System.Text.Json.JsonSerializer.SerializeToNode(catalogResp.Data, System.Text.Json.JsonSerializerOptions.Web)!.AsObject();
        var validCatalogResp = await agentClient.ValidateAsync(validBody, ct);
        validCatalogResp.IsSuccess.Should().BeTrue();
        validCatalogResp.Data!.Valid.Should().BeTrue();

        // 6. POST /api/agents/test executes the agent in the Live Playground (both saved and unsaved inline draft)
        var playgroundResp = await agentClient.TestPlaygroundAsync(new AgentPlaygroundRequest
        {
            Agent = "studio-dispute-agent",
            Definition = saveAgentResp.Data,
            Prompt = "Analyze dispute TX-100 for {{ data.customerName }}.",
            Data = JsonNode.Parse("""
            {
              "customerName": "Ayşe Demir",
              "disputedAmount": 250
            }
            """)!.AsObject(),
        }, ct);
        playgroundResp.IsSuccess.Should().BeTrue();
        playgroundResp.Data!.AgentName.Should().Be("studio-dispute-agent");
        playgroundResp.Data.SelectedTransition.Should().Be("route-auto-refund");
        playgroundResp.Data.Output.Should().NotBeNull();
        playgroundResp.Data.Output!["verdict"]!.GetValue<string>().Should().Be("AUTO_REFUND");
        playgroundResp.Data.ToolCalls.Should().NotBeEmpty();

        // 7. DELETE endpoints remove the agent, tool, and MCP server cleanly
        (await agentClient.DeleteAgentAsync("studio-dispute-agent", ct)).IsSuccess.Should().BeTrue();
        (await agentClient.DeleteToolAsync("calc_refund_bonus", ct)).IsSuccess.Should().BeTrue();
        (await agentClient.DeleteMcpServerAsync("studio-mcp", ct)).IsSuccess.Should().BeTrue();

        var afterDeleteCatalog = await agentClient.GetCatalogAsync(ct);
        afterDeleteCatalog.Data!.Agents.Should().NotContain(a => a.Name == "studio-dispute-agent");
        afterDeleteCatalog.Data.Tools.Should().NotContain(t => t.Name == "calc_refund_bonus");
        afterDeleteCatalog.Data.McpServers.Should().NotContain(m => m.Name == "studio-mcp");
    }

    private sealed class ToolTestingChatClient(string modelId) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("test-tool-client", null, modelId);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var lastPrompt = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

            if (lastPrompt.Contains("Return invalid output", StringComparison.Ordinal))
            {
                // Intentionally violates minimum: 100
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"vatAmount": 5}""")));
            }

            var hasToolResult = list.Any(m => m.Role == ChatRole.Tool);
            if (!hasToolResult && options?.Tools?.Any(t => t.Name == "calculate_tax") == true)
            {
                var callMsg = new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("call-1", "calculate_tax", new Dictionary<string, object?> { ["amount"] = 1000d }),
                ]);
                return Task.FromResult(new ChatResponse(callMsg) { FinishReason = ChatFinishReason.ToolCalls });
            }

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, """{"vatAmount": 200, "currency": "TRY"}"""))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resp = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static JsonNode? SimulatePostgresJsonbKeyOrder(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var reordered = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    reordered[key] = SimulatePostgresJsonbKeyOrder(value);
                }

                return reordered;
            }

            case JsonArray arr:
            {
                var reordered = new JsonArray();
                foreach (var item in arr)
                {
                    reordered.Add(SimulatePostgresJsonbKeyOrder(item));
                }

                return reordered;
            }

            default:
                return node?.DeepClone();
        }
    }
}
