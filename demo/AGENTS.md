# demo – workflow & server-driven UI demo (read the root AGENTS.md first)

Flat project structure: `Middt.Demo.Workflows` (JSON workflows, AI agents, layouts, seeds, and HTTP deployer targeting `runtime/Middt.Workflow.Runtime`),
`Middt.Demo.Blazor`, `Middt.Demo.Angular` (BFF host) + `angular-app` (Angular SPA workspace).
The demo shows framework usage – keep framework logic in `sdk/` and the standalone runtime host in `runtime/Middt.Workflow.Runtime`.

- Seeds: `Middt.Demo.Workflows/Layouts/*.json` (layouts, `$schema` → `spec/ui-layout.schema.json`),
  `Workflows/*.json` (JSONC), `Agents/` (`Agents/*.json`, `Agents/Tools/*.json`, `Agents/McpServers/*.json`), and `Seeds/initial-instances.json` (declarative initial workflow instances). Agents, workflows, layouts, and initial instances are deployed over HTTP by `DemoFlowDeployer` (`POST /api/agents/catalog`, `POST /api/workflows/definitions`, `POST /api/ui/layouts/seed`, `POST /api/workflows/{workflow}/instances`). Bump a layout's `version` to overwrite the stored copy.
- Everything is 100% backend-driven from JSON (see [`docs/ai/backend-driven-json-ui.md`](../docs/ai/backend-driven-json-ui.md)):
  - Sidebar workflow menu items (`icon`, `title`, `/workflow/{key}`) come from `GET /api/workflows`.
  - Sidebar layout menu items are ordered by `menu.order` (then `menu.title`) in both Blazor and Angular.
  - Instance list grid columns come from `dataSchema` in `Workflows/*.json` (use `"x-list": false` to hide a field from the list table, `"x-mask": "middle" | "end"` to mask sensitive values, and `"x-search": true` to add a list filter).
  - Start and transition forms come from `input.schema` in `Workflows/*.json` (supports `"x-lookup"` and `"x-enumFrom"`) and transition button styling comes from declarative `transition.style` and `transition.icon`.
  - State detail views come from `Layouts/*.json` (`kind: "view"`, referenced via `"view": { "layout": "..." }`) and are editable at runtime in `/ui-admin`.
  - Reusable partial layouts (e.g. `workflow-footer.json`) can be embedded inside any layout via `{ "widget": "layout", "key": "workflow-footer", "data": "<optional-expr>" }`.
  - RabbitMQ queues are discovered dynamically from `WorkflowRegistry` (`publish` task configs) + configuration in `RabbitMqQueueInitializer`.
  - There are NO hardcoded Razor or Angular views in the demo.
- Workflows: `customer-onboarding`, `credit-allocation` (+ `risk-limit-eval` subflow), `commercial-limit-revision` (`loadWorkflow`, `queryWorkflow` portfolio aggregation, 4-eyes `$previousUser`, `updateWorkflow` on `customer-onboarding`, `input.header.correlationId`), `order-payment-orchestration`,
  `sla-escalation`, `ai-dispute-resolution` (Microsoft Agent Framework orchestrator + specialist copilot + multi-agent sequential committee, declarative `expression` and `task` tools bridged to `IWorkflowTask`, `banking-mcp` MCP server, `maxToolCalls`/`maxTotalTokens` budgets, and AI-driven routing via `allowedTransitions` / `transitionInto`), and `invoice-collection` (payment messages → `Event` transition `payment-received` with `bufferFor`, manual twin `record-payment` appending to `partialPayment[]`, `delete-payment` removing an entry from `partialPayment` and appending to `deletedPayment[]`, automatic `payment-completed` when `paidAmount >= total`, a `background` ledger call, `history: delta` – driven by Postman collection 06). `update-billing-contact` shows `input.format: jsonPatch` (the client sends an RFC 6902
  patch on `data.billingContact`).
- AI usage per workflow (`ai-dispute-resolution`, `risk-limit-eval` → `risk-limit-underwriter`, `invoice-collection` → `invoice-risk-auditor`, all tools and the MCP server) is walked through in [`docs/ai-agent-scenarios.md`](../docs/ai-agent-scenarios.md).
- Sign-in: both BFFs use Middt Identity (`identity/Middt.Identity`, :8083) configured by `identity/Middt.Identity/Seeds/identity.json` (seed).
  Demo user `test@test.com` / `test` has roles `manager`, `ui-admin`, `idp-admin`; `user@test.com` / `test` has none.
- Running: `docker compose up -d --build` with `.env` (see `.env.example`). Rebuild only what changed:
  `docker compose up -d --build middt-sample-angular`.
- Angular demo consumes `@middt/angular` and `@middt/ui-core` as packages (`file:` deps + `preserveSymlinks`);
  `npm run build:sdk` builds both.
