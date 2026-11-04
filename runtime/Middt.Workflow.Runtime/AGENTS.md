# runtime/Middt.Workflow.Runtime – Standalone Workflow & UI Runtime

Read root [`AGENTS.md`](../../AGENTS.md) and [`sdk/server/AGENTS.md`](../../sdk/server/AGENTS.md) first.

`Middt.Workflow.Runtime` is the standalone, reusable workflow, AI agent, and server-driven JSON UI runtime service (`middt-workflow-runtime` container image).

- **Zero domain code or baked-in demo files**: It does not ship hardcoded domain workflows, AI agents, or layouts.
- **Runtime HTTP Deployment**:
  - `POST /api/agents/catalog` (`ui-admin` or `idp-admin` role): Deploys an `AgentCatalogFile` (`agents`, `tools`, `mcpServers`), registers them in `MiddtAgentRegistry` / `MiddtMcpClient`, persists them to `wf."WorkflowDefinitions"` (`Key = "__agents__"`), and broadcasts a cross-pod catalog invalidation (`WorkflowJobNotifier.NotifyCatalogAsync`).
  - `POST /api/workflows/definitions` (`ui-admin` or `idp-admin` role): Validates one or more workflow JSON definitions, persists them to `wf."WorkflowDefinitions"`, registers them in `WorkflowRegistry`, broadcasts a cross-pod catalog invalidation, and provisions PostgreSQL list partitions + selective JSONB indexes on the fly.
  - `POST /api/ui/layouts/seed` (`ui-admin` role): Validates and seeds one or more `UiLayoutDocument` JSON screens into `ui."UiLayouts"` and broadcasts cross-pod layout cache invalidation (`middt_ui_layouts` via `UiLayoutCatalogInvalidationHandler`).
- **Generic Data Query**: `Controllers/WorkflowDataController.cs` exposes `GET /api/v1/{entity}/getall`, `POST /api/v1/{entity}/getitems`, and `GET /api/v1/{entity}/getbyid` backed by `WorkflowInstanceDataQuery`.
- **Health Checks**: `/health` and `/alive` (mapped automatically by `UseMiddtApi()`).

