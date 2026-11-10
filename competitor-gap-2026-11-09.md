# Middt Framework – Competitive Gap Analysis (2026-11-09)

Scope: Middt (`github.com/middt/middt-platform`, .NET 10, GPL-2.0-only) vs. workflow engines, low-code/server-driven-UI builders and agent-orchestration platforms. Middt facts come from `README.md`, `docs/architecture.md`, `docs/citizen-developer-guide.md` and `docs/ai/add-agent.md` plus a grep of the source tree. Competitor facts were checked with web search on 2026-11-09; items marked **[unconfirmed]** could not be verified from a primary source and rest on secondary write-ups or prior knowledge.

Legend for matrix cells: **Y** = yes/first-class, **P** = partial, **N** = no/not found, **?** = unconfirmed.

---

## 1. Capability matrix

Columns: Middt · Camunda 8 (8.9, Apr 2026) · Temporal (Replay 2026) · Elsa 3 (3.9, Oct 2026) · Dapr Workflow (1.17 + Dapr Agents 1.0) · n8n (3.0, Oct 2026) · Flowable (2026.1) · Retool/ToolJet (low-code UI reference) · LangGraph + LangSmith (agent reference)

| Capability | Middt | Camunda 8 | Temporal | Elsa 3 | Dapr WF | n8n | Flowable | Retool / ToolJet | LangGraph/LangSmith |
|---|---|---|---|---|---|---|---|---|---|
| **Definition model** | Y – JSON state machine (states/transitions/tasks) | Y – BPMN 2.0 + DMN + Forms | Y – code (workflows-as-code, 7 SDKs) | Y – code + JSON/designer flowchart | Y – code (Workflow-as-code, Java/Go/.NET/Python/JS) | Y – visual node graph (JSON export) | Y – BPMN + CMMN + DMN | P – UI apps + simple workflows | Y – graph in code |
| Standard notation import/export (BPMN/DMN) | N | Y | N | N (own format) | N | N | Y | N | N |
| Decision tables / rules engine | P – expression `rule`s only | Y – DMN FEEL | N | P – flowchart branches | N | P – IF/Switch nodes | Y – DMN | N | N |
| Visual process designer (runtime editable) | Y – `/workflow-admin`, immutable versions | Y – Web/Desktop Modeler | N (code) | Y – Elsa Studio | N (code) | Y – editor | Y – Design | Y (UI), P (workflow) | P – LangGraph Studio (debug) |
| Human tasks: claim/unclaim/assign/inbox | Y – incl. unified `/inbox`, role pools, 4-eyes | Y – Tasklist, candidate groups, listeners | P – via Signals/Updates, no built-in inbox | P – Elsa.Users/bookmarks | N (hand-rolled via events) | P – Wait/Form, HITL approvals in agents | Y – Work/Engage | P – app-level | P – `interrupt` HITL |
| Timers / scheduled transitions, durable | Y – jobs table, SKIP LOCKED, LISTEN/NOTIFY | Y – Zeebe timers | Y – durable timers at scale | Y – Quartz/Hangfire | Y – Scheduler service | Y – Wait/Schedule | Y | P | P – checkpointer, process-bound |
| Message/event correlation, buffering | Y – `Event` transitions by id/business key, `bufferFor` | Y – message correlation keys, TTL buffering | Y – Signals/Updates | Y – bookmarks/triggers | Y – `RaiseEvent` | P – webhooks | Y | N | P – external resume |
| Sub-workflows / child cancel | Y – `SubFlow`, parent cancel → child cancel | Y – call activities | Y – child workflows, cancellation policies | Y – composite | Y – child workflows | P – Execute Workflow node | Y | N | Y – subgraphs |
| Parallel / fan-out | P – `fanOut` task over array (in-process), `concurrent` agents | Y – parallel gateways, multi-instance | Y – arbitrary async | Y – fork/join | Y – fan-out/fan-in | Y – branches | Y | N | Y |
| Saga / compensation | N (idempotency keys + onError only; Rebus sagas opt-in) | Y – BPMN compensation events | Y – Saga pattern (code) | P | Y – documented pattern | N | Y | N | N |
| Versioning + running-instance migration | Y – immutable versions, diff, rollback, `/migrate` with stateMap | Y – process instance migration, incl. ad-hoc subprocess (8.9) | Y – Worker Versioning GA (Mar 2026) | Y – versioning + migration API | Y – named workflows + patching (1.17) | P – workflow history only | Y | P – app versions/releases | N |
| Dry-run / simulation / replay testing | P – `/simulate` dry-run with task mocks | P – Modeler token simulation, test env | Y – `WorkflowReplayer`, time-skipping test server | P | P | P – test executions, evaluations | P | N | P – LangSmith datasets/evals |
| Ops dashboard (incidents, batch retry) | P – `/operations` API + designer tabs, no dedicated ops UI | Y – Operate, Optimize (KPIs, reports) | Y – Web UI, Worker status UI | Y – Studio instance monitoring, 3.8 diagnostics | P – CLI (`dapr workflow list/history/...`) | Y – executions, Insights | Y – Control/Inspect | Y – audit logs | Y – LangSmith traces |
| Process analytics / mining / SLA | N | Y – Optimize (KPI wizard), heatmaps | N | N | N | P – Insights | Y – reports | N | P – eval metrics |
| Multi-DB persistence | Y – PG/SQL Server/MySQL/Oracle/SQLite (PG partitioned) | P – Zeebe + RDBMS secondary storage (8.9) | N – Temporal server (Cassandra/PG/MySQL/SQLite) | Y – EF Core/MongoDB/Dapper | Y – via state store components | P – PG/SQLite | Y – RDBMS | Y – many data sources (as connectors) | P – checkpointer backends |
| HA multi-node job processing | Y – leases, SKIP LOCKED/READPAST, graceful release | Y – Zeebe partitions | Y – history service, serverless workers | P | Y – Scheduler/Actors | P – queue mode (Redis) | Y | n/a | P |
| Real-time push to UI | Y – SSE, cross-pod | P – Operate polling/websockets | P – Workflow Streams (2026) | P | N | P | Y | P | Y – streaming |
| **Server-driven UI from JSON** | Y – 16 widgets, 2 frontends (Blazor + Angular) + static HTML | P – Forms (form.js) only | N | N | N | P – Form trigger | Y – Forms + Pages (Work) | Y – full app builder (one renderer) | N |
| Drag-and-drop screen designer at runtime | Y – `/ui-admin`, revisions, optimistic concurrency | P – Form Builder | N | N | N | P – Form node | Y | Y | N |
| Data-source connectors in UI | P – only own workflow entities (`grid`/`record`/`x-lookup`) | P – connectors in process, not UI | N | N | N | Y – 400+ integrations | P | Y – DBs, REST, GraphQL, SaaS | N |
| JS / code escape hatch in UI | N (by design: no code in layouts; custom widgets need C# + TS) | P – form.js JS expressions (FEEL) | n/a | n/a | n/a | Y – Code node | Y | Y – JS everywhere | n/a |
| Theming / dark mode / i18n | N – UI texts hard-coded Turkish, no theme tokens found | Y | n/a | P | n/a | Y | Y | Y | n/a |
| Mobile / offline | N (responsive only? [unconfirmed]) | P | n/a | n/a | n/a | N | Y (Flowable Engage) | P (mobile layouts) | n/a |
| RBAC on widgets/fields | Y – `visibleWhen user.IsInRole`, `x-roles`, `x-mask`, layout `roles` | P – task access | n/a | n/a | n/a | P – projects/custom roles | Y | Y | n/a |
| Git sync / CI promotion of definitions | P – bundle export/import w/ SHA-256 + `dryRun` (workflows only) | Y – Web Modeler Git sync, Modeler in IDE | Y – code in Git | Y – export/import JSON | Y – code | Y – source control, selective promotion (3.0) | Y | Y – Git sync (paid tiers) | Y – code |
| **AI agent inside a workflow step** | Y – `agent` task (single/sequential/concurrent/handoff), AI routing | Y – AI Agent connector + ad-hoc subprocess | Y – OpenAI Agents SDK integration GA, LangGraph plugin | P – Elsa.Agents (3.8 AI copilot) [unconfirmed depth] | Y – Dapr Agents 1.0 (durable agents) | Y – AI Agent node v3 | Y – 4 agent types, multi-agent | Y – Retool Agents, ToolJet agents | Y (it *is* the agent) |
| MCP client / MCP server | Y client (HTTP JSON-RPC + in-process declarative); N server | Y client (OAuth/API key) + Y built-in MCP server (8.9) | P – community | ? | Y – via Dapr Agents | Y – MCP Client Tool + MCP Server Trigger + instance MCP server | Y – MCP support (2026.1) | Y – Retool MCP | Y |
| A2A protocol | N | Y – A2A client connectors (8.9) | N | N | ? | N | Y – A2A external agents (2025.2) | N | P – via LangGraph Platform [unconfirmed] |
| Human approval gate for tool calls | N (only `allowedTransitions` and human states) | Y – via process modelling | P – code | ? | P – code | Y – per-tool approval setting (2.44/3.0) | Y – guardrails + HITL | P | Y – `interrupt` |
| Agent memory / RAG / vector store | P – session persistence per instance only; no RAG | P – Vector DB connector, embeddings connector | N (bring your own) | ? | P – Conversation API | Y – vector store nodes, memory nodes | Y – unstructured data first-class (2026.1) | P – Retool Vectors | Y – long-term memory store |
| Guardrails / evaluator | P – `outputSchema` validation, token & tool budgets | P | N | ? | N | P – evaluations wizard | Y – guardrails + evaluator | P | Y – LangSmith evals |
| Traces / cost tracking / prompt versioning | P – OTel spans, token usage per run; agent defs versioned only through `__agents__` record | P – Operate history, no cost UI | P – OTel | P – 3.8 OTel diagnostics | Y – OTel tracing for agents | Y – monthly budget & session spend caps (2.43) | Y – "full insight" | P | Y – traces, costs, Prompt Hub versions |
| **Multi-tenancy** | N (no tenant concept found) | Y – multi-tenancy (identity + tenant ids) | Y – Namespaces | Y – tenant support (3.x) | P – per app/namespace | Y – Projects (cloud/enterprise) | Y – tenants | Y – orgs/workspaces | Y – workspaces |
| SaaS / managed hosting | N | Y – Camunda SaaS, new regions | Y – Temporal Cloud (multi-region, serverless workers) | N | P – Diagrid Catalyst | Y – n8n Cloud | Y – Flowable Cloud | Y | Y – LangSmith Deployment |
| Kubernetes operator / Helm | N (Dockerfile + compose only) | Y – Helm, ref. architectures | Y – Helm, Worker Controller GA (May 2026) | N | Y – Dapr operator | Y – Helm (community) | Y | Y – Helm | P |
| SDKs in other languages | P – .NET + TypeScript (`@middt/ui-core`); REST for others | Y – Java/Go/Node/Python/.NET/REST | Y – Go/Java/Py/TS/.NET/PHP/Ruby | N – .NET only | Y – Py/JS/.NET/Java/Go | n/a (HTTP) | Java | n/a | Py/JS |
| CLI | N | Y (`camunda`/zbctl, C8 Run) | Y (`temporal`) | N | Y (`dapr`) | Y (`n8n` CLI, promote) | P | P | Y (`langgraph`) |
| Connector marketplace | N – `http`, `rabbitmq`, `rebus`, `mcp` only | Y – 100+ connectors + Marketplace | N (code) | P – Elsa Extensions (3.9 train) | P – Dapr components (~100 bindings) | Y – 400+ nodes + templates | Y | Y | Y – LangChain integrations |
| OpenAPI → connector import | N | Y – REST connector templates from OpenAPI | N | N | N | Y – HTTP node import cURL/OpenAPI | Y | Y | P (tools) |
| License / commercial support | GPL-2.0-only, no vendor support | Source-available (Camunda License 1.0) + commercial | MIT + Temporal Cloud | MIT | Apache-2.0 (CNCF) + Diagrid | Sustainable Use License + Enterprise | Dual (OSS Apache-2 engine + commercial) | Closed / AGPL (ToolJet) | MIT + commercial |
| Docs / community size | P – good English README + Turkish guide; small community | Y – large | Y – large | P – medium | Y – CNCF | Y – very large | Y – large | Y | Y – very large |

Reading the matrix: Middt is **at or above parity** on the state-machine core (versioning + migration, HA jobs, event correlation, human-task governance, multi-DB, SSE) and clearly **ahead on "JSON screens rendered by two frontends" and "agent + MCP as a workflow task with AI routing"**. It is **behind** on standards (BPMN/DMN), analytics/ops UX, compensation, AI safety/observability features (approval gates, RAG, cost control), and almost the whole **platform** column (multi-tenancy, SaaS, k8s, CLI, connectors, other-language SDKs, license).

---

## 2. Gaps (what competitors have that Middt lacks)

### (a) Workflow engine

| Gap | Who has it | Middt today | Severity |
|---|---|---|---|
| **BPMN 2.0 / DMN import & export** | Camunda, Flowable | Own JSON state machine; `/workflow-admin` canvas is not BPMN. Business analysts cannot reuse existing BPMN diagrams or tooling (bpmn.io, Signavio). | High for enterprise sales; low for Middt's current users |
| **Decision tables (DMN/FEEL)** | Camunda, Flowable | Routing is `rule` expressions chained via `Automatic` transitions with one `default`. No table editor, no hit policies, no decision versioning separate from the flow. | High – most LOB flows have pricing/eligibility tables |
| **Saga / compensation handlers** | Camunda (compensation events), Temporal (Saga), Dapr (pattern), WorkflowCore (`CompensateWith`), Flowable | `onError` can `Transition` to a cleanup state, HTTP task sends `Idempotency-Key`; Rebus sagas only via `rebus.Configure`. No declarative "undo these tasks in reverse order". | Medium-High |
| **Signals / broadcast events to many instances** | Camunda (signals), Temporal (Signals), Dapr | `triggerWorkflow … all: true` and `POST /messages` by key exist (correlation to **one** instance per call or by query). No named signal catalog, no cross-workflow wildcard subscription. | Medium |
| **Parallel branches inside one instance** | Camunda/Flowable gateways, Temporal, Dapr | `fanOut` is a task-level parallel loop; the state machine itself is single-token. No join/fork states, no multi-instance human tasks. | Medium |
| **Replay/determinism testing & time-skipping test harness** | Temporal (`WorkflowReplayer`, time-skipping `WorkflowEnvironment`), Dapr (test harness) | `/simulate` dry-run with `taskMocks` and step traces is good, but no replay of real history against a new definition version, no time-skipping for `Scheduled` transitions in tests. | Medium |
| **Versioned code activities / worker versioning** | Temporal Worker Versioning GA, Dapr named workflows + patching | JSON definitions are versioned; **C# `IWorkflowTask` implementations are not** – deploying a changed task silently affects all in-flight versions. | Medium |
| **Operate/Optimize-class dashboards** | Camunda (Operate, Optimize KPI wizard), Flowable Control, Temporal Web UI, n8n Insights | `/operations`, `/incidents`, `agent-runs`, OTel to OpenObserve. No bundled ops console (instance search across workflows, incident drill-down, heatmaps, throughput/cycle-time charts). | High |
| **SLA / cycle-time analytics, process mining** | Camunda Optimize, Flowable reports | History tables have everything needed (`WorkflowHistory`, JSON patch deltas) but no aggregation API or UI. | Medium |
| **Audit log of operator actions** | Camunda 8.9 audit log, Elsa 3.8 | `$migrate` and retries land in history; layout revisions exist. No unified "who deployed/rolled back/purged what" log. | Medium |
| **Durable timers at very large scale** | Temporal (history service), Camunda Zeebe | Single `WorkflowJobs` table per DB; partition-pruned and indexed, PG-only `LISTEN/NOTIFY`. Fine to ~10⁶ jobs **[unconfirmed – no published benchmark]**; no sharding story beyond one database. | Low-Medium |
| **Workflow-level secrets store** | Camunda connector secrets, Elsa 3.8 first-class secrets, n8n credentials | Secrets only via `AgentSettings`/environment; definitions must not contain them; no `{{ secrets.X }}` indirection. | Medium |
| **External task / job-worker pattern (polyglot workers)** | Camunda job workers, Temporal activities, Dapr multi-app | Tasks run in-process in the .NET host (`http` is the only out-of-process hook). A Python/Go team cannot implement a task. | Medium |

### (b) UI builder / server-driven UI

| Gap | Who has it | Middt today | Severity |
|---|---|---|---|
| **Data-source connectors** (SQL, REST, GraphQL, SaaS) | Retool, ToolJet, Appsmith, Budibase, Power Apps (Dataverse + connectors) | `grid`/`record`/`x-lookup` only read **workflow entities** via `WorkflowInstanceDataQuery`. External data must be pulled into a workflow first (`http` task). | High if positioned as low-code builder; acceptable if positioned as "workflow screens" |
| **Code escape hatch** (JS transforms, custom components without redeploy) | Retool/ToolJet/Appsmith (JS everywhere), Power Apps (Power Fx), form.js (FEEL) | Deliberate: no code in layouts; custom widgets need C# + TS + spec. Portable language has no user-defined functions, no array `map/filter` **[unconfirmed – check `Portable` grammar]**. | Medium (security trade-off is a strength; lack of functions is a gap) |
| **Theming / white-label / dark mode / i18n** | All builders | UI texts are hard-coded Turkish ("Kaydet", "Bu alan zorunludur."); no resource files, no theme tokens found in `middt-ui.css`. | High for any non-Turkish customer |
| **Forms standard (form.js / JSON Forms) interop** | Camunda (form.js), Flowable Forms | Forms are generated from JSON Schema subset + `x-*` hints; not importable into/out of form.js or JSON Forms UI-schema. | Low-Medium |
| **Widget catalog breadth** (charts, file upload, rich text, maps, kanban, calendar) | Retool/ToolJet 50–100 components, Power Apps | 16 widgets; no chart, file upload, rich text, signature, kanban. | Medium-High |
| **Mobile / offline** | Power Apps, Flowable Engage, Retool Mobile | Responsive web only **[unconfirmed]**. | Low |
| **Audit of designer changes / approval before publish** | Camunda Web Modeler (review), Flowable lifecycle governance (2026.1), Appsmith Git PR flow | Layout revisions and workflow versions are stored; no "draft → review → publish" state, no diff UI for layouts (workflows have `/diff`). | Medium |
| **Git sync of definitions** | Appsmith, Retool, n8n source control + selective promotion, Camunda Web Modeler Git | `GET /bundle` + `POST /bundle/import` (workflows only; layouts & agents excluded) – CI-friendly but manual; no Git push/pull from the studios. | Medium |
| **Row-level security on external data / per-field encryption** | Retool, Power Apps | Strong RLS on workflow data (`instanceAccess`, `x-roles`, `x-mask`), nothing for external sources (none exist). | Low |
| **Embeddable renderer for React/Vue** | Retool Embed, form.js | `@middt/ui-core` is framework-agnostic but only an Angular renderer ships; "add a React renderer" is a documented recipe, not a package. | Medium |

### (c) AI

| Gap | Who has it | Middt today | Severity |
|---|---|---|---|
| **Human approval gate per tool call** | n8n (per-tool approval, 2.44/3.0), LangGraph `interrupt`, Flowable guardrails, MAF workflows HITL checkpoints | Agent runs to completion inside one task; a human can only intervene at `Human` states before/after. No "pause agent, ask human, resume with same session". | High – core governance ask for banking |
| **Agent memory / RAG / vector store** | n8n, LangGraph memory store, Camunda vector DB connector, Flowable unstructured data, Retool Vectors | `session`/`sessionInto` persists chat history per instance only; no embeddings, no retrieval tool kind, no document ingestion. | High |
| **Guardrails (input/output policies, PII, jailbreak)** | Flowable 2026.1, LangSmith, Azure AI Content Safety in Logic Apps | `outputSchema` + budgets + `roles` on tools + `AllowedHttpHosts`. No content filters, no policy DSL. | Medium-High |
| **Evaluations / regression datasets** | LangSmith evals, n8n evaluations wizard, Flowable evaluator | Playground single-turn test only; no datasets, no LLM-as-judge, no CI gating on agent quality. | Medium-High |
| **Cost tracking & budgets at tenant/workflow level** | n8n monthly budget + session spend caps (2.43), LangSmith unified cost view, Logic Apps token billing | Per-run token usage stored; `maxTotalTokens` per agent/task. No price tables, no aggregated spend per workflow/user/month, no hard monthly cap. | Medium |
| **Prompt versioning & A/B** | LangSmith Prompt Hub, Flowable lifecycle governance | Agents live in one `__agents__` definition record; no per-agent version history, diff or rollback (workflows have all three). | Medium |
| **Multi-provider per agent** | Everyone | One global `AgentSettings.Provider`; no per-agent provider/model routing, no Azure OpenAI/Anthropic/Gemini providers out of the box (OpenAI-compatible endpoint works) **[Anthropic/Gemini unconfirmed]**. | Medium |
| **Streaming responses to UI** | n8n (2.37), LangGraph, MAF | Agent output lands in `data` after completion; SSE exists but no token streaming to the Playground/screens. | Low-Medium |
| **A2A protocol (client/server)** | Camunda 8.9 A2A connectors, Flowable 2025.2 | None. | Low-Medium (early market) |
| **MCP server exposure of Middt itself** | Camunda built-in MCP server (8.9), n8n instance MCP server | Middt is an MCP *client* only. External agents (Claude, Copilot) cannot drive Middt workflows/inbox via MCP. | Medium – cheap win |
| **Durable agent loop (checkpoint inside the tool loop)** | Dapr Agents, Temporal OpenAI Agents SDK, MAF Durable Task | Agent task is a single in-process call; a pod crash mid-loop restarts the whole task (idempotent via `Idempotency-Key` for tools). | Medium |

### (d) Platform

| Gap | Who has it | Middt today | Severity |
|---|---|---|---|
| **Multi-tenancy** | Camunda, Temporal namespaces, Elsa tenants, Flowable, all SaaS builders | No tenant concept in `wf`/`ui` schemas, identity or layouts. Separate schema per deployment is the only isolation. | High for SaaS/ISV use |
| **SaaS / managed offering** | Camunda SaaS, Temporal Cloud, n8n Cloud, Flowable Cloud, LangSmith Deployment, Logic Apps Automation | None. | Strategic decision |
| **Kubernetes operator / Helm chart** | Camunda, Temporal Worker Controller, Dapr | Dockerfile + `docker compose` only. | Medium |
| **CLI** | `camunda`, `temporal`, `dapr workflow …`, `n8n`, `langgraph` | None (REST only; Postman collections provided). | Medium – cheap |
| **SDKs in other languages** | Camunda (6), Temporal (7), Dapr (5) | .NET + TypeScript; REST/OpenAPI for others. | Low-Medium |
| **Connector catalog / marketplace** | Camunda 100+, n8n 400+, Dapr components, Power Platform 1000+ | `http`, `rabbitmq`, `rebus`, `mcp`. Email/SMS/Slack/Teams/SAP/DB-write all need `http` to something or custom `IWorkflowTask`. | High for citizen-developer positioning |
| **OpenAPI → task/tool import** | Camunda REST connector templates, n8n, Retool | Declarative `http` tool/task must be hand-written per endpoint. | Medium – cheap given existing JSON Schema plumbing |
| **Docs, tutorials, community** | All | Thorough README/guide (Turkish guide is 2 400 lines), AGENTS.md playbooks; no docs site, no tutorial series, no forum/Discord, tiny GitHub community **[star count unconfirmed]**. | High |
| **Licensing & commercial support** | MIT/Apache (Temporal, Elsa, Dapr), source-available + support (Camunda, n8n) | **GPL-2.0-only**: copyleft blocks most ISV/enterprise embedding; no dual license, no support SLA. | High – likely the #1 adoption blocker |
| **Secrets management integration** (Vault/Key Vault) | Camunda, Elsa 3.8, n8n | Configuration/env only. | Medium |
| **Workflow data export / BI connector** | Camunda Optimize exports, Flowable | Direct SQL on `jsonb` is documented; no CSV/Parquet export endpoint. | Low |
| **Single sign-on for the studios beyond own IdP** | all | Works via OIDC BFF with any provider; fine. | – |

---

## 3. Middt's differentiators

1. **AI agent + MCP as a first-class workflow task with AI-driven routing.** `agent`/`mcp` tasks, declarative tools bridged to *any* `IWorkflowTask`, `allowedTransitions`/`transitionInto` validated against the live state's transitions, tool-call audit (`toolCallsInto`, `WorkflowAgentToolCalls`), token/tool budgets, RBAC on tools and a Playground in both frontends. Camunda needs an AI Agent connector + ad-hoc subprocess, Temporal/Dapr need code, n8n has no state-machine governance around the agent. Nobody else combines "agent picks the next transition" with transition `roles`/`rule` enforcement.
2. **100 % server-driven JSON UI rendered by two frontends + static HTML**, validated by a generated JSON Schema, with runtime drag-and-drop editing, revisions and optimistic concurrency. Camunda/Flowable only do forms; Retool-class tools own the single renderer. Middt's "add a React renderer on `@middt/ui-core`" story is unique.
3. **Portable expression language with a shared conformance suite** (`spec/expressions.conformance.json`, `json-patch.conformance.json`) executed by both .NET and TS – guarantees `visibleWhen`/`requiredWhen`/`disabledWhen` behave identically client- and server-side (server strips hidden fields, enforces conditional required, rejects disabled-field edits). Competitors have FEEL/JS with client/server drift risk.
4. **Identity journeys as workflows**: sign-in, registration, reset, MFA enrollment run on the same engine (OpenIddict IdP). No competitor's workflow engine powers its own IdP flows.
5. **BFF-by-default security**: cookie session, server-side tokens with distributed refresh leases, CSRF header, strict CSP, deny-listed proxy paths, OWASP defaults in both frontends. Low-code competitors ship tokens to the browser.
6. **Data-centric engine on the customer's RDBMS**: JSON data with `dataSchema`-driven **indexes, filters, masking (`x-mask`), field RBAC (`x-roles`), SQL pushdown on 5 databases, PostgreSQL declarative partitioning per workflow**, delta history as RFC 6902 patches with checkpointing, `jsonPatch` transitions with `test` guards. Camunda/Temporal treat payload as opaque; Elsa/WorkflowCore have no indexing/RLS story.
7. **Enterprise governance in a small embeddable package**: claim/unclaim/assign to users/roles, unified inbox, 4-eyes via `$previousUser`/`denyRoles`, immutable versions + structural/JSON-patch diff + rollback + running-instance migration with `stateMap` and timer re-homing, dry-run simulator, SHA-256 bundles with `dryRun` import, retention purge – all REST, no separate cluster (vs Zeebe/Temporal server).
8. **Cross-pod real-time SSE** with per-event access checks, PG `LISTEN/NOTIFY` wake-ups and catalog invalidation across pods – uncommon in embedded .NET engines (Elsa, WorkflowCore).
9. **One runtime image, zero domain code**: `Middt.Workflow.Runtime` accepts workflows, layouts and agent catalogs over HTTP; demos are 100 % JSON.
10. **AI-assistant-ready repo**: `AGENTS.md`, playbooks, slash commands, graph knowledge base – lowers the cost of extending the framework with coding agents.

---

## 4. Prioritised recommendations (top 10)

| # | Recommendation | Effort | Why / what it unlocks |
|---|---|---|---|
| 1 | **Re-license** (dual-license GPL-2.0 + commercial, or move to Apache-2.0/MIT for `sdk/shared` + `sdk/client` at minimum; keep GPL/commercial for runtime if desired). Publish NuGet/npm packages. | S (legal) / M (packaging) | GPL-2.0-only blocks embedding in proprietary LOB apps – every competitor in the matrix is MIT/Apache/source-available. Nothing else matters if nobody can adopt. |
| 2 | **Human approval gate inside the agent loop** (`"approval": { "tools": [...], "roles": [...] }` → agent task suspends as a durable job, creates an inbox item, resumes the same session on approve/reject; emit SSE). Reuse `background: true` + `WorkflowJobs`. | M | Top governance gap vs n8n/LangGraph/Flowable; directly sellable for banking (4-eyes on AI actions). Also makes the agent loop durable across pod restarts (checkpoint per tool call). |
| 3 | **Decision tables** (`decision` task + `Decisions/*.json` with hit policies first/collect/unique, inputs as portable expressions, output into `data`; grid editor in `/workflow-admin`; versioned like workflows; optional DMN 1.3 XML import for the table subset). | M | Pricing/eligibility/routing tables are in nearly every LOB flow; closes the DMN gap without adopting full BPMN. Expression evaluator and JSON-Schema plumbing already exist. |
| 4 | **Operations console** in both frontends (`/ops-admin`): cross-workflow instance search, incident drill-down with retry/batch retry, job queue, SLA/cycle-time per state from `WorkflowHistory`, agent run cost. Add an aggregation endpoint (`GET /api/workflows/analytics?workflow=&groupBy=state`). | M-L | Closes the Operate/Optimize-class gap; `/operations`, `/incidents`, `/agent-runs` and OTel already supply the data. Strong demo value. |
| 5 | **Connector layer v1**: `email` (SMTP), `slack/teams` webhook, `sql` read (named connection, parameterised, read-only), `file/blob` upload task + `file` widget, and **OpenAPI → `http` task/tool template import** (`POST /api/workflows/tasks/import-openapi`). | M | Citizen-developer positioning collapses without 5–10 common connectors; OpenAPI import gives dozens of connectors for free using the existing JSON Schema validator. |
| 6 | **Agent RAG + memory**: `kind: "retrieval"` tool over pgvector (PG already required), `ingest` task for documents/instance data, `memory` scope (`instance` / `user` / `workflow`) with TTL; show retrieved chunks in `toolCallsInto`. | M-L | Second-biggest AI gap; pgvector keeps the "your database, one process" promise. |
| 7 | **AI observability & governance pack**: per-agent version history/diff/rollback (reuse workflow versioning code on `__agents__`), price table + aggregated spend per workflow/user/month with hard monthly caps, eval datasets (`POST /api/agents/evaluate` with expected `selectedTransition`/schema asserts, runnable in CI), basic guardrail hooks (`IAgentGuardrail` + regex/PII built-ins). | M | Matches n8n 2.43/LangSmith/Flowable features; mostly reuse of existing versioning, OTel and Playground code. |
| 8 | **i18n + theming**: move all UI strings to resource files (`tr`, `en`), CSS custom-property theme tokens + dark mode in `middt-ui.css`/Angular Material theme, `label`/`title` localisation map in layouts and `dataSchema` (`"title": { "tr": "...", "en": "..." }`). | M | Hard blocker for any non-Turkish customer and for the OSS community; also needed before publishing docs in English. |
| 9 | **Multi-tenancy**: `TenantId` column on `wf`/`ui` tables (partition key candidate), tenant claim from OIDC, tenant-scoped registries/caches, per-tenant layout/agent overrides; `ITenantResolver` default = claim. | L | Required for SaaS/ISV use and for one runtime serving several business units; expensive to retrofit later – design now even if shipped in phases. |
| 10 | **Developer-experience & distribution**: `middt` CLI (`validate`, `bundle export/import`, `simulate`, `migrate --dry-run`, `scaffold workflow/layout/agent`), Helm chart + health/readiness docs, **MCP server exposing Middt** (`list_workflows`, `start_instance`, `inbox`, `execute_transition`, `deploy_definition`) so Claude/Copilot/Cursor can drive it, docs site (English, Docusaurus/MkDocs) with 3 tutorials, GitHub Discussions. | S-M each | Cheap items with outsized adoption effect; the MCP server reuses the existing in-process declarative MCP implementation and REST clients. |

Deferred / consciously not recommended now: full BPMN 2.0 execution (huge, conflicts with the state-machine model – offer **BPMN import mapping to states/transitions** later if sales demands it), A2A (market still early; add after MCP server), polyglot workers (an `externalTask` job-polling endpoint is a cheaper middle step), SaaS (business decision, blocked by #1 and #9).

---

## Sources

Camunda: [8.9 release notes](https://docs.camunda.io/docs/reference/announcements-release-notes/890/890-release-notes/) · [What's new in 8.9](https://docs.camunda.io/docs/reference/announcements-release-notes/890/whats-new-in-89/) · [8.9 agentic orchestration blog](https://camunda.com/blog/2026/04/camunda-8-9-fastest-path-to-agentic-orchestration/) · [A2A, MCP and connectors in 8.9-alpha2](https://camunda.com/blog/2026/01/a2a-mcp-and-enhanced-connectors-with-camunda-8-9-alpha2/) · [8.9-alpha3 (ad-hoc subprocess migration)](https://camunda.com/blog/2026/02/camunda-8-9-alpha3-release/) · [Three ways Camunda speaks MCP](https://camunda.com/blog/2026/08/three-ways-camunda-speaks-mcp-and-why-the-direction-matters/) · [Camunda Forms reference (form.js)](https://docs.camunda.io/docs/components/modeler/forms/camunda-forms-reference/)

Temporal: [Replay 2026 announcements](https://temporal.io/blog/replay-2026-product-announcements) · [Feature changelog](https://temporal.io/changelog/type/feature) · [Durable Digest Aug 2026 (Nexus .NET GA, standalone Nexus ops)](https://temporal.io/blog/durable-digest-august-2026) · [.NET testing suite (replay, time-skipping)](https://docs.temporal.io/develop/dotnet/best-practices/testing-suite) · [WorkflowReplayer API](https://dotnet.temporal.io/api/Temporalio.Worker.WorkflowReplayer.html) · [Temporal LangGraph plugin](https://temporal.io/blog/temporal-langgraph-plugin-durable-execution)

Elsa: [elsa-core releases (3.8.0 Sep 2026, 3.9.0 Oct 2026 via GitHub API)](https://github.com/elsa-workflows/elsa-core/releases) · [Elsa docs](https://docs.elsaworkflows.io) · [Elsa Studio design](https://docs.elsaworkflows.io/studio/design)

WorkflowCore: [1.6.0 release (Saga + CompensateWith)](https://github.com/danielgerlag/workflow-core/releases/tag/1.6.0) · [docs](https://docsearch.algolia.com/mcp/docs/repo/danielgerlag/workflow-core)

Dapr: [Dapr v1.17 announcement (workflow versioning, workflow CLI)](https://blog.dapr.io/posts/2026/02/27/dapr-v1.17-is-now-available/) · [Dapr Agents v1.0 GA (CNCF, Mar 2026)](https://www.cncf.io/announcements/2026/03/23/general-availability-of-dapr-agents-delivers-production-reliability-for-enterprise-ai/) · [Dapr Agents intro](https://v1-17.docs.dapr.io/developing-ai/dapr-agents/dapr-agents-introduction) · [Dapr.Workflow.Versioning NuGet](https://www.nuget.org/packages/Dapr.Workflow.Versioning)

Azure Logic Apps / Power Platform: [Agent Loop GA (Standard)](https://mc.merill.net/message/AZ527649) · [Agent workflows concepts](https://learn.microsoft.com/azure/logic-apps/agent-workflows-concepts) · [Create autonomous agent workflows (token billing)](https://learn.microsoft.com/da-dk/azure/logic-apps/create-autonomous-agent-workflows) · [InfoQ Logic Apps coverage](https://www.infoq.com/LogicApps/news/) · [Power Apps 2026 release wave 1](https://learn.microsoft.com/power-platform/release-plan/2026wave1/power-apps/) · [Power Platform 2026 wave 1 plan](https://learn.microsoft.com/power-platform/release-plan/2026wave1/)

n8n: [Release notes (2.37–3.0: agent budgets, approvals, MCP, source control promotion, RBAC)](https://docs.n8n.io/release-notes/) · [n8n MCP guide](https://uibakery.io/blog/n8n-mcp-guide) · [n8n MCP server trigger & client](https://eseospace.com/blog/n8n-mcp-server-trigger-and-client/)

Flowable: [2026.1 release](https://www.flowable.com/blog/releases/2026-1) · [2026.1.0 release notes](https://documentation.flowable.com/latest/admin/release-notes/2026.1.0-release) · [AI orchestration docs](https://documentation.flowable.com/latest/ai/ai-orchestration) · [Summer 2025 agentic release](https://www.newsfilecorp.com/release/259603/Agentic-AI-Takes-Center-Stage-Flowable-Revolutionizes-Business-Process-Automation-with-Summer-2025-Release)

Low-code builders (vendor-authored, directional): [ToolJet vs Appsmith vs Superblocks vs Retool 2026](https://blog.tooljet.com/tooljet-vs-appsmith-vs-superblocks-vs-retool-which-internal-tool-platform-is-best-in-2026/) · [Retool alternatives 2026 (ToolJet)](https://blog.tooljet.com/retool-alternatives-2026/) · [Budibase alternatives 2026 (Jet Admin)](https://www.jetadmin.io/blog/budibase-alternatives-2026-guide-to-internal-app-builders/) · [Retool alternatives & AI agents (Encore)](https://encore.dev/articles/retool-alternatives-ai-agents)

Agent frameworks: [LangGraph docs (durable execution, HITL, memory)](https://docs.langchain.com/oss/python/langgraph) · [LangSmith Deployment rename](https://dev.to/jangwook_kim_e31e7291ad98/langgraph-platform-ga-studio-v2-one-click-deploy-guide-4m10) · [LangSmith observability guide](https://www.metacto.com/blogs/what-is-langsmith-a-comprehensive-guide-to-llm-observability) · [Microsoft Agent Framework migration from Semantic Kernel](https://learn.microsoft.com/el-gr/agent-framework/migration-guide/from-semantic-kernel/) · [MAF durable workflows via Durable Task (prerelease)](https://startdebugging.net/de/2026/05/agent-framework-durable-workflows-checkpoint-restart/) · [MAF 1.0 multi-agent guide](https://noqta.tn/en/blog/microsoft-agent-framework-1-0-multi-agent-orchestration-guide-2026)

Unconfirmed items (flagged inline): Middt scale limits of the single `WorkflowJobs` table; Middt mobile/responsive behaviour; portable-language lack of user functions/array ops; Anthropic/Gemini provider support in `AgentSettings`; Elsa 3.x agent-feature depth; LangGraph Platform A2A; Semantic Kernel Process Framework official deprecation (only migration guides found, no deprecation notice); Camunda process-mining specifics; Middt GitHub community size.
