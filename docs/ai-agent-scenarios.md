# AI agent usage scenarios in the demo workflows

This document walks through every place in the demo where a workflow calls an AI agent
(`"task": "agent"`) or a Model Context Protocol server (`"task": "mcp"`), and what the business
outcome of each path is. It complements the reference material in
[`citizen-developer-guide.md`](citizen-developer-guide.md) (agent / tool / MCP property tables) and
the architecture notes in the [README](../README.md#ai-agents-declarative-tools--model-context-protocol-mcp-middtframeworkagents).

All agents run on [`Middt.Framework.Agents`](../sdk/server/Middt.Framework.Agents) (Microsoft Agent
Framework). Without a configured LLM provider the deterministic local fallback is used: the agent
still calls its tools, and the `default` values in its `outputSchema` plus the tool results drive the
very same routing, so every scenario below is reproducible with `docker compose up`.

| Workflow | Agent(s) | Role of AI | Entry point |
|---|---|---|---|
| [`ai-dispute-resolution`](../demo/Middt.Demo.Workflows/Workflows/ai-dispute-resolution.json) | `dispute-orchestrator`, `dispute-specialist-copilot`, MCP `banking-mcp` | Autonomous triage + routing, copilot for specialists, sequential multi-agent committee | `/workflow/ai-dispute-resolution` |
| [`risk-limit-eval`](../demo/Middt.Demo.Workflows/Workflows/risk-limit-eval.json) (sub-flow of `credit-allocation`) | `risk-limit-underwriter` | Autonomous underwriting decision + routing, re-check on demand | started by `credit-allocation` → `to-risk-subflow` |
| [`invoice-collection`](../demo/Middt.Demo.Workflows/Workflows/invoice-collection.json) | `invoice-risk-auditor` | Advisory risk audit on demand, no routing | `AI Risk Analizi` button in `awaiting-payment` / `overdue` |

Agent, tool and MCP definitions live under
[`demo/Middt.Demo.Workflows/Agents`](../demo/Middt.Demo.Workflows/Agents) and are deployed with
`POST /api/agents/catalog`. They can also be edited at runtime in the AI Agent & MCP Studio
(`/agent-admin`) and dry-run in its Live Playground (`POST /api/agents/test`).

## 1. Dispute & fraud management (`ai-dispute-resolution`)

### Cast

| Definition | Kind | Purpose |
|---|---|---|
| [`dispute-orchestrator`](../demo/Middt.Demo.Workflows/Agents/dispute-orchestrator.json) | agent | Analyses the dispute, picks `nextTransition`. Budgets: `maxToolCalls: 10`, `maxTotalTokens: 16000`. Output: `decision`, `riskLevel`, `riskScore`, `recommendedRefund`, `policyCode`, `summary`, `recommendation`, … |
| [`dispute-specialist-copilot`](../demo/Middt.Demo.Workflows/Agents/dispute-specialist-copilot.json) | agent | Assists the human specialist: turns a note / extra evidence into a refund proposal (`copilotVerdict`, `approvedRefundAmount`, `totalCredit`, `policyCode`, `settlementStatus`). |
| [`evaluate_dispute_policy`](../demo/Middt.Demo.Workflows/Agents/Tools/evaluate-dispute-policy.json) | `expression` tool | Applies the chargeback policy: category and amount → `nextTransition`, risk level / score, recommended refund, summary. |
| [`calculate_reimbursement`](../demo/Middt.Demo.Workflows/Agents/Tools/calculate-reimbursement.json) | `task` tool (bridged to the `set` workflow task) | Refund amount, fee reversal (`0` up to 5.000 ₺, otherwise `150`), total credit and policy code (`CB-FAST-2026` / `CB-STD-2026`). |
| [`banking-mcp`](../demo/Middt.Demo.Workflows/Agents/McpServers/banking-mcp.json) | MCP server | Tools `lookup_chargeback_history`, `verify_merchant_receipt`; resource `mcp://banking/policies/chargeback-2026` (`autoApproveLimit: 5000`, `fraudEscalationLimit: 25000`, `slaHours: 24`); prompt `dispute-triage-prompt`. |

### Common entry: `ai-triage`

The start form collects dispute number, customer, merchant, amount, category (`Mükerrer Çekim`,
`Hizmet Alınamadı`, `Hatalı Tutar`, `Yetkisiz İşlem`) and the customer's statement. On entering
`ai-triage` the `onEntry` chain runs in order:

1. `setKey` assigns the business key `DSP-{{ data.disputeNo }}`.
2. `mcp` / `readResource` reads the 2026 chargeback policy.
3. `mcp` / `callTool` `verify_merchant_receipt` checks the merchant slip and settlement status.
4. `agent` `dispute-orchestrator` evaluates the case. `AiAgentTask` injects the state's transitions
   into the agent context and output schema (`allowedTransitions` / `nextTransition`), validates the
   returned key against the state, and the matching `trigger: Automatic` transition fires.

The agent output is addressable as `instance.agents.disputeOrchestrator.*` in later expressions.

### Scenario 1a – low amount, automatic refund

- **Input**: 3.200 ₺, `Mükerrer Çekim` (the form defaults).
- **Agent**: risk `Düşük` (score 14), `nextTransition = route-auto-refund`, recommended refund = full
  amount, policy `CB-FAST-2026`.
- **Outcome**: the instance moves to the final state `auto-resolved` without any human action.
  `resolution.*` is filled from the agent output (refunded amount, policy code, summary) and
  `resolution.resolvedBy` is set to `AI Orkestratör (dispute-orchestrator)`.
- **Demonstrates**: fully autonomous AI routing, structured `outputSchema`, reading agent results
  from `instance.agents`.

### Scenario 1b – medium amount, specialist review with copilot

- **Input**: 5.000 ₺ < amount ≤ 25.000 ₺, any non-fraud category (e.g. 12.500 ₺, `Hizmet Alınamadı`).
- **Agent**: risk `Orta` (score 52), `route-specialist-review`, settlement status `Belge Bekleniyor`.
- **Outcome**: the instance waits in `specialist-review`. The specialist has four transitions:
  - `AI Copilot Analizi` (`re-evaluate-ai`, target `$self`): the specialist enters a note or extra
    evidence; `dispute-specialist-copilot` calls `calculate_reimbursement` and returns the final refund
    proposal (amount + 150 ₺ fee reversal above 5.000 ₺). The state does not change, so this can be
    repeated as evidence arrives.
  - `MCP Tarihçe Sorgula` (`invoke-mcp-audit`, `$self`): calls the MCP tool
    `lookup_chargeback_history` directly, without an agent, and stores prior disputes, velocity score
    and merchant risk on the instance.
  - `İadeyi Onayla` (`approve-refund` → `resolved-refunded`): the human enters the approved amount and
    note; `policyCode` is inherited from the orchestrator's recommendation (falls back to `CB-STD-2026`).
  - `İtirazı Reddet` (`reject-dispute` → `rejected`): closes the case with a reason.
- **Demonstrates**: human-in-the-loop copilot, repeatable analysis on the same state, MCP used both
  inside an agent and as a plain workflow task.

### Scenario 1c – unauthorized transaction or high amount, fraud escalation

- **Input**: category `Yetkisiz İşlem` **or** amount > 25.000 ₺.
- **Agent**: risk `Yüksek` (score 89), `route-fraud-investigation`, recommended refund 0, recommendation
  "card block and 3D Secure log review".
- **Outcome**: the instance moves to `fraud-investigation`. The fraud team can:
  - `AI Fraud Komitesi (Multi-Agent)` (`run-multi-agent-committee`, `$self`): a `mode: sequential`
    agent task runs `dispute-orchestrator` and then `dispute-specialist-copilot` on the same case, each
    receiving the previous output.
  - `Fraud Doğrulandı & İade Et` (`confirm-fraud-refund` → `resolved-refunded`): picks a security
    action (`Kart Kapatıldı & Yenilendi`, `Üye İşyeri Kara Listeye Alındı`, `Geçici Blokaj Uygulandı`),
    refunds the full amount under policy `CB-FRAUD-2026`.
  - `Fraud Şüphesini Reddet` (`reject-fraud-claim` → `rejected`).
- **Demonstrates**: multi-agent `sequential` orchestration, agent budgets, MCP resources + tools +
  prompts in one server definition.

### Routing summary

| Condition | Transition | Target | Risk |
|---|---|---|---|
| `category == "Yetkisiz İşlem"` or `amount > 25000` | `route-fraud-investigation` | `fraud-investigation` | Yüksek / 89 |
| `amount <= 5000` | `route-auto-refund` | `auto-resolved` (finish) | Düşük / 14 |
| otherwise | `route-specialist-review` | `specialist-review` | Orta / 52 |

## 2. Risk & limit evaluation (`risk-limit-eval`, sub-flow of `credit-allocation`)

### Cast

| Definition | Kind | Purpose |
|---|---|---|
| [`risk-limit-underwriter`](../demo/Middt.Demo.Workflows/Agents/risk-limit-underwriter.json) | agent | Corporate underwriting: calls all four tools, returns a full allocation report (score, grade, DTI, payment behaviour, sector index, collateral, sanctions, bureau reference, …) and `nextTransition`. |
| [`query_customer_risk_score`](../demo/Middt.Demo.Workflows/Agents/Tools/query-customer-risk-score.json) | `expression` tool | KKB / Findeks score, credit grade, debt-to-income ratio, recommended limit (`amount × 1.25`). |
| [`lookup_customer_payment_history`](../demo/Middt.Demo.Workflows/Agents/Tools/lookup-customer-payment-history.json) | `expression` tool | On-time payment rate, late payments, average delay, bounced cheques, closed loans. |
| [`verify_central_bank_registry_http`](../demo/Middt.Demo.Workflows/Agents/Tools/verify-central-bank-registry-http.json) | `task` tool (bridged to the `http` task) | `POST http://mockoon:3000/api/v1/credit-bureau/check` with custom headers; shows an agent calling an external HTTP service. |
| [`evaluate_collateral_and_sector_exposure`](../demo/Middt.Demo.Workflows/Agents/Tools/evaluate-collateral-and-sector-exposure.json) | `expression` tool | Sector risk, limit utilisation, collateral coverage, sanctions check and the routing decision. |

### Common entry: `evaluate`

`credit-allocation` starts this workflow through its `risk-subflow` state (`to-risk-subflow`). On
entering `evaluate`, a `set` task normalises the input, a `cache` task memoises the base risk score
for 15 minutes (`risk-eval:{customerKey}-{checkType}`), then the agent runs and a `log` task records
the result.

### Scenario 2a – automatic approval

- **Input**: 250.000 ₺ (default), `requireUnderwriting: false`.
- **Agent**: KKB 810 / `A+ (Prime)`, on-time rate 98 %, DTI 26 %, `Tam Teminatlı / Uygun`, sanctions
  clean → `to-approved`.
- **Outcome**: `approved` with `recommendedLimit = amount × 1.25`; the parent flow continues.

### Scenario 2b – manual underwriting

- **Input**: amount > 500.000 ₺ **and** `requireUnderwriting: true`.
- **Agent**: KKB 640 / `B+ (Yakın İzleme)`, 1 late payment, DTI 44 %, "additional collateral advised"
  → `to-underwriting`.
- **Outcome**: the instance waits in `manual-underwriting` for the `manager` role. The underwriter can
  run `AI Derinlemesine Risk & Ödeme Analizi` (`ai-underwriting-recheck`, `$self`) to re-evaluate with
  the same agent, then `Alt Akış Limitini Onayla` (inline note) or `Alt Akış Limitini Reddet`.

### Scenario 2c – automatic rejection

- **Input**: amount > 5.000.000 ₺.
- **Agent**: grade `D (Limit Aşımı)`, DTI 78 %, utilisation 96 %, collateral insufficient →
  `to-rejected`.
- **Outcome**: `rejected`; the recommendation asks for a lower amount or extra guarantees.

### Routing summary

| Condition | Transition | Target |
|---|---|---|
| `amount > 5000000` | `to-rejected` | `rejected` |
| `requireUnderwriting && amount > 500000` | `to-underwriting` | `manual-underwriting` |
| otherwise | `to-approved` | `approved` |

**Demonstrates**: an agent inside a sub-flow, a tool bridged to the `http` task that leaves the
process, `cache` before the agent, re-running the same agent from a human task.

## 3. Invoice collection (`invoice-collection`)

### Cast

| Definition | Kind | Purpose |
|---|---|---|
| [`invoice-risk-auditor`](../demo/Middt.Demo.Workflows/Agents/invoice-risk-auditor.json) | agent | Collection risk audit: `riskLevel`, `score`, `summary`, `recommendation`. |
| [`audit_invoice_payments`](../demo/Middt.Demo.Workflows/Agents/Tools/audit-invoice-payments.json) | `task` tool (bridged to `set`) | Scores the invoice from `instance.state` and `args.total`: `overdue` or total > 250.000 ₺ → `Yüksek` / 38; total > 100.000 ₺ → `Orta` / 74; otherwise `Düşük` / 92. |

### Scenario 3 – on-demand collection risk audit

- **Trigger**: a user with the `manager` role presses `AI Risk Analizi` (`ai-risk-audit`, target
  `$self`) while the invoice is in `awaiting-payment` or `overdue` (`availableIn`).
- **Agent**: receives invoice number, totals, remaining amount, `partialPayment[]` and
  `deletedPayment[]` as context and calls `audit_invoice_payments`.
- **Outcome**: the risk level, summary and recommended action ("urgent reconciliation and payment
  plan" vs. "continue the standard collection flow") are stored on the instance and rendered in the
  state view. The workflow state is unchanged; the collection decision stays with the human.
- **Demonstrates**: an agent in a purely advisory role, a tool bridged to an existing
  `IWorkflowTask`, tool expressions reading workflow context (`instance.state`).

## Cross-cutting behaviour

- **Deterministic fallback**: with no LLM configured (`AddMiddtAgents()` default), tool calls and
  `outputSchema` defaults produce the same routing as above, which is what the E2E and demo seeds
  rely on.
- **Budgets**: `maxToolCalls` / `maxTotalTokens` on each agent cap runaway loops; exceeding them fails
  the task like any other workflow task failure.
- **Playground**: in `/agent-admin` any saved or inline agent can be run with a sample `prompt`,
  `data` and `allowedTransitions`; the response shows `output`, `selectedTransition`, `toolCalls`,
  token `usage` and `durationMs`.
- **Runtime redeploy**: `POST /api/agents/catalog` replaces the catalog and invalidates it on every
  pod; the workflows pick up the new definitions on their next agent call.
- **Observability**: agent runs emit OpenTelemetry spans and the tool calls can be persisted on the
  instance with `toolCallsInto`.
