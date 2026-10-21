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
                    tools: [MiddtWorkflowAgentTools.EvaluateExpressionToolName],
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
                  "tools": ["calculate_tax", "workflow_evaluate_expression", "workflow_list_definitions"],
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

        // Built-in Middt tools are also registered and callable via IMiddtAgentRegistry
        var ct = TestContext.Current.CancellationToken;
        var registry = host.Services.GetRequiredService<IMiddtAgentRegistry>();
        var evalTool = (AIFunction)registry.GetTool(MiddtWorkflowAgentTools.EvaluateExpressionToolName)!;
        var evalResult = await evalTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["expression"] = "data.total - data.paidAmount",
                ["dataJson"] = """{"total": 5000, "paidAmount": 1250}""",
            }),
            ct);
        evalResult?.ToString().Should().Be("3750");

        var getTool = (AIFunction)registry.GetTool(MiddtWorkflowAgentTools.GetInstanceToolName)!;
        var getResult = await getTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["instanceId"] = instance.Id,
            }),
            ct);
        getResult?.ToString().Should().Contain("tool-and-schema-test");

        // Triggering an agent whose output violates outputSchema faults the transition as expected
        var faulted = await host.TransitionAsync(instance.Id, "trigger-invalid-schema", new { }, user: Manager);
        faulted.Status.Should().Be(WorkflowInstanceStatus.Faulted);
    }

    [Fact]
    public async Task Demo_invoice_collection_executes_ai_risk_audit_and_renders_in_invoice_detail_layout()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Workflows");
        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Agents");
        var layoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Layouts", "invoice-detail.json");
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
        audited.Data["aiAudit"]!["riskLevel"]!.GetValue<string>().Should().Be("Düşük");
        audited.Data["aiAudit"]!["score"]!.GetValue<double>().Should().Be(92);
        audited.Data["aiAudit"]!["summary"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        audited.Data["aiAudit"]!["recommendation"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        audited.Data["aiAuditMeta"]!["agent"]!.GetValue<string>().Should().Be("invoice-risk-auditor");

        var layout = UiJson.Parse(await File.ReadAllTextAsync(layoutPath, ct));
        var uiOptions = new MiddtUiOptions();
        var html = new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
            layout,
            new Dictionary<string, JsonNode?>
            {
                [UiExpressions.Data] = audited.Data.DeepClone(),
                [UiExpressions.Instance] = new JsonObject { ["state"] = audited.State, ["stateTitle"] = audited.StateTitle, ["workflow"] = audited.Workflow },
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

        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Agents");
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
        var workflowsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Workflows");
        var agentsDir = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Agents");
        var detailLayoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Layouts", "ai-dispute-detail.json");
        var resultLayoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Api", "Layouts", "ai-dispute-result.json");
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
        autoResolved.Data["aiNextTransition"]!.GetValue<string>().Should().Be("route-auto-refund");
        autoResolved.Data["aiDecision"]!["decision"]!.GetValue<string>().Should().Be("Otomatik İade Onayı");
        autoResolved.Data["aiDecision"]!["riskLevel"]!.GetValue<string>().Should().Be("Düşük");
        autoResolved.Data["aiDecision"]!["recommendedRefund"]!.GetValue<double>().Should().Be(2450);
        autoResolved.Data["aiDecision"]!["policyCode"]!.GetValue<string>().Should().Be("CB-FAST-2026");
        autoResolved.Data["mcpPolicy"]!["policyVersion"]!.GetValue<string>().Should().Be("CB-2026.4");
        autoResolved.Data["mcpReceipt"]!["settlementStatus"]!.GetValue<string>().Should().Be("Otomatik İadeye Uygun");
        autoResolved.Data["aiToolCalls"]!.AsArray().Count.Should().BeGreaterThanOrEqualTo(3);
        autoResolved.Data["resolution"]!["status"]!.GetValue<string>().Should().Be("AI Otomatik İade Onayı");
        autoResolved.Data["resolution"]!["refundedAmount"]!.GetValue<double>().Should().Be(2450);

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
        specialistCase.Data["aiNextTransition"]!.GetValue<string>().Should().Be("route-specialist-review");
        specialistCase.Data["aiDecision"]!["decision"]!.GetValue<string>().Should().Be("Uzman İncelemesi");
        specialistCase.Data["aiDecision"]!["riskLevel"]!.GetValue<string>().Should().Be("Orta");

        var afterCopilot = await host.TransitionAsync(
            specialistCase.Id,
            "re-evaluate-ai",
            new { specialistNote = "Kargo teslim tutanağı bulunamadı, müşteri haklı." },
            user: Manager);

        afterCopilot.State.Should().Be("specialist-review");
        afterCopilot.Data["copilotReview"]!["approvedRefundAmount"]!.GetValue<double>().Should().Be(14500);
        afterCopilot.Data["copilotReview"]!["totalCredit"]!.GetValue<double>().Should().Be(14650);
        afterCopilot.Data["copilotToolCalls"]!.AsArray().Should().NotBeEmpty();

        var afterMcpAudit = await host.TransitionAsync(
            specialistCase.Id,
            "invoke-mcp-audit",
            new { },
            user: Manager);

        afterMcpAudit.Data["mcpHistoryCheck"]!["velocityScore"]!.GetValue<int>().Should().Be(45);

        // Verify Server-Driven UI detail layout renders AI & MCP sections
        var detailLayout = UiJson.Parse(await File.ReadAllTextAsync(detailLayoutPath, ct));
        var uiOptions = new MiddtUiOptions();
        var detailHtml = System.Net.WebUtility.HtmlDecode(
            new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
                detailLayout,
                new Dictionary<string, JsonNode?>
                {
                    [UiExpressions.Data] = afterMcpAudit.Data.DeepClone(),
                    [UiExpressions.Instance] = new JsonObject { ["state"] = afterMcpAudit.State, ["stateTitle"] = afterMcpAudit.StateTitle, ["workflow"] = afterMcpAudit.Workflow },
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
        fraudCase.Data["aiNextTransition"]!.GetValue<string>().Should().Be("route-fraud-investigation");
        fraudCase.Data["aiDecision"]!["decision"]!.GetValue<string>().Should().Be("Fraud Eskalasyonu");
        fraudCase.Data["aiDecision"]!["riskLevel"]!.GetValue<string>().Should().Be("Yüksek");
        fraudCase.Data["aiDecision"]!["riskScore"]!.GetValue<double>().Should().Be(89);

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
                    [UiExpressions.Instance] = new JsonObject { ["state"] = fraudResolved.State, ["stateTitle"] = fraudResolved.StateTitle, ["workflow"] = fraudResolved.Workflow },
                },
                new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
                CultureInfo.InvariantCulture));

        resultHtml.Should().Contain("Kart Kapatıldı & Yenilendi");
        resultHtml.Should().Contain("route-fraud-investigation");
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
}
