# Architecture: contracts, backend, frontends

Middt is split so that the **frontend technology is replaceable**. Two frontends run side by side against the same API,
workflows and layouts: Blazor Server (`sdk/client/Middt.Framework.Blazor`) and Angular (`sdk/client/angular`). Both sit behind the same
backend-for-frontend blocks (`sdk/client/Middt.Framework.Bff`), so no frontend ever holds an API token.

```
                         ┌────────────────────────────── 1. Contracts (no UI technology) ──────────────────────────────┐
                         │ Middt.Framework.Model            wire DTOs: envelope, auth, workflow                         │
                         │ Middt.Framework.Expressions      portable expression language (+ DynamicExpresso for workflows)│
                         │ Middt.Framework.Ui.Abstractions  layout model, widget specs, validator, formats, JSON Schema   │
                         │ spec/                            expressions.conformance.json, ui-layout.schema.json           │
                         └───────────────▲───────────────────────────────▲──────────────────────────────▲───────────────┘
                                         │                               │                              │ same rules
┌──────────── 2. Backend ────────────────┴───┐   ┌──── 3. .NET client SDK ──┴──────┐   ┌──── TypeScript ───┴────────────┐
│ Api, Common (server helpers)               │   │ Middt.Framework.Client           │   │ sdk/client/ui-core     │
│ Workflow   (engine; IWorkflowViewRenderer, │   │ typed workflow/layout/data       │   │ @middt/ui-core: types,         │
│             WorkflowInstanceDataQuery)      │   │ clients, token refresh           │   │ expressions, formats, clients  │
│ Ui.Server  (layout store, REST, schema,    │   └───────────────▲──────────────────┘   └───────────────▲────────────────┘
│            plain C# static HTML renderer)  │                   │                                      │
│ Rebus      (RabbitMQ / in-memory messaging)│   ┌──── 4. Blazor frontend ┴──────────┐   ┌──── Angular frontend ─────────┴┐
│ Agents     (Microsoft Agent Framework AI)  │   │ Blazor      BFF auth, workflow    │   │ @middt/angular: widgets as     │
└─────────────────────▲──────────────────────┘   │             screens, layout       │   │ Angular components, BFF/cookie │
                      │  HTTPS / JSON (REST,     │             widgets + editor (Ui/)│   │ auth, workflow screens         │
                      │  ProblemDetails)         └───────────────────────────────────┘   └────────────────────────────────┘
                      └───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Dependency rules

| Layer | May reference | Must not reference |
|---|---|---|
| Contracts | BCL, each other (Model ← Expressions ← Ui.Abstractions) | ASP.NET Core UI, Razor, Radzen, EF Core |
| Backend | Contracts, EF Core, ASP.NET Core | Client, Bff, Blazor, Radzen |
| Client SDK | Contracts | Backend, any UI technology |
| Blazor frontend | Contracts, Client, Bff, Common (host helpers) | Backend engine packages (Workflow, Ui.Server, Api) |

## What is the contract between backend and frontends?

1. **REST + JSON** – every API returns `BaseResponseDataModel<T>` (`result`, `messageList`, `count`, `data`); errors are
   RFC 9457 ProblemDetails. Workflow data queries: `api/v{version}/{entity}/getitems|getall|getbyid|insert|update|delete`; workflows:
   `api/workflows/...`; layouts: `api/ui/...` (incl. `GET api/ui/widgets`, `GET api/ui/schema`).
2. **Layouts** – JSON documents validated against `spec/ui-layout.schema.json` (generated from the widget catalog)
   and the server validator. A layout names widgets; every frontend has its own implementation of each widget name.
3. **Portable expressions** – `visibleWhen`, `requiredWhen`, `disabledWhen`, `{{ }}` templates, list sources and filters use a small sandboxed language
   with identical semantics in .NET (`Middt.Framework.Expressions.Portable`) and TypeScript (`@middt/ui-core`) over `data`, `query`, `row`, `record`, `instance`, `ui` (live open-form values before submit), `user`, and `now`.
   `spec/expressions.conformance.json` is executed by both test suites, so they cannot drift apart. Workflow rules and
   mappings run only on the server and keep the richer C# (DynamicExpresso) dialect.
4. **Formats and CSS** – value formats are limited to a portable set (`N0`–`N6`, `F0`–`F6`, `d`, `D`, `g`, `t`);
   display widgets share the `middt-ui-*` CSS classes (`sdk/client/Middt.Framework.Blazor/wwwroot/middt-ui.css`).

## Renderers of a layout

| Renderer | Where | Widgets |
|---|---|---|
| Blazor (interactive) | `Middt.Framework.Blazor` (`Ui/`) | display widgets + `grid`/`record`/`layout` (Radzen) |
| Static HTML | `Middt.Framework.Ui.Server` (`UiHtmlRenderer`, plain C#) | display widgets; interactive ones render a placeholder |
| Angular | `@middt/angular` on top of `@middt/ui-core` | same names, Angular components (Material) |

Custom widgets are registered in each place: the **spec** in the API (`MiddtUiOptions.AddWidget(spec, html?)`) and
in every frontend (`AddMiddtUi(w => w.Add<MyWidget>(spec))` in Blazor, a component map in Angular). Reusable composite
widgets can also be defined in pure JSON (`kind: "view"`) and embedded via `{ "widget": "layout", "key": "<layout-key>", "data": "<optional-expr>" }` without writing any C# or TypeScript code (see [`docs/ai/backend-driven-json-ui.md`](ai/backend-driven-json-ui.md)).

## How the Angular frontend was added (and how to add another)

1. **Workspace**: `sdk/client/angular` (Angular 22 + Material) with the library `@middt/angular` (demo app in `demo/angular-app`); it
   consumes `@middt/ui-core` as a `file:` package.
2. **Authentication**: the SPA is served by a BFF host (`demo/Middt.Demo.Angular`: `AddMiddtSpaBff()` +
   `UseMiddtSpaBff()`). It owns the cookie session and the tokens, serves the server rendered login/logout pages,
   `GET /bff/user`, and proxies `/api/**` (YARP) to the API with the session's
   access token, refreshing it before it expires (coordinated across threads and multiple BFF instances via distributed refresh leases `TryAcquireRefreshLeaseAsync` and optimistic session version checks `CompareAndSetAsync` / `RemoveIfMatchAsync`). CSRF: API calls carry an `X-CSRF` header, WebSocket upgrades must be
   same-origin. The API is unchanged.
3. **Layouts**: `UiLayoutViewComponent` / `MiddtLayoutHostComponent` loads layouts and maps widget names to Angular
   components (`MIDDT_UI_WIDGETS`); `@middt/ui-core` evaluates `visibleWhen`, templates and
   formats exactly like .NET; the same CSS classes (`middt-ui-*`) and E2E selectors work in both frontends.
   The Angular package has focused `core`, `ui`, and `workflow` entry points; the root entry point remains
   an aggregate for compatibility.
4. **Workflows**: `WorkflowApiClient` (ui-core) + list/detail/wizard components; all workflow states use `view.layout`
   JSON layouts (`kyc-review`, `credit-review`, `limit-revision-detail`, `order-review`, `order-result`, `order-escalated`, `risk-detail`, `invoice-detail`, `invoice-result`, `ai-dispute-detail`, `ai-dispute-result`) rendered by
   the layout host in both frontends (`SubFlow` states delegate rendering to the active child workflow state automatically or use an optional fallback `view.layout`).
   Transition input schemas are rendered as forms; properties support live `visibleWhen` (`x-visibleWhen`), `requiredWhen` (`x-requiredWhen`), and `disabledWhen` (`x-disabledWhen`) expressions referencing `ui.<field>`, `data.<field>`, and `user` (also enforced by `JsonSchemaValidator` on the server, which strips hidden fields, validates conditional requiredness, and rejects unauthorized changes to disabled fields), and a property with `x-lookup` (`entity`, `valueField`, `textField`)
   becomes a select loaded dynamically from the endpoint – the engine only checks the hint's shape.
5. **Visual Designers**: both frontends ship a visual drag-and-drop **Screen Designer** (`/ui-admin`, `UiLayoutEditor` / `MiddtLayoutEditorComponent` with 16 built-in widgets including `stat`, `tabs`, `timeline`), a visual drag-and-drop **Workflow Designer** (`/workflow-admin`, `WorkflowDesigner` / `MiddtWorkflowDesignerComponent`), and a visual **AI Agent, Tool & MCP Studio** (`/agent-admin`, `AgentStudio` / `MiddtAgentStudioComponent` with a live dry-run Playground) with bi-directional integration to create or edit state `view.layout` screens and `aiAgent` tasks directly from workflow states. Saving a workflow definition at runtime (`PUT /api/workflows/{workflow}/definition`) creates a new immutable version (`v2`, `v3`, …) so in-flight instances continue on their original version, and supports version listing (`/versions`), structural + RFC 6902 diff (`/diff`), rollback (`/rollback`), and live running-instance version migration (`/migrate` via the **🔄 Sürüm & Göç** tab).

For a third frontend (e.g. React) repeat 1–4: reuse `@middt/ui-core` and a BFF host built with `AddMiddtSpaBff()`.

## Workflow execution and recovery

Workflow JSON uses the framework's general task types (`set`, `setKey`, `log`, `http`, `publish`, `rabbitmq`, `startWorkflow`,
`loadWorkflow`, `queryWorkflow`, `updateWorkflow`, `triggerWorkflow`, `fanOut`, `cache`, `agent`, `mcp`). The `operation` task delegates named domain actions to an
explicitly registered `IWorkflowOperationDispatcher`; identity allowlists its password, MFA and OIDC operations there.
The demo's flows store and query all domain data as JSON inside PostgreSQL-partitioned workflow tables (`WorkflowInstances` +
`WorkflowInstanceData`) and expose dynamic CRUD query endpoints via `WorkflowInstanceDataQuery` (pushing scalar filters and numeric/text sorting with a deterministic `id` tie-breaker down to SQL before pagination, clamped to `getitems` ≤ 100 and `getall` ≤ 200). Task definitions remain independent of
application-specific `IWorkflowTask` implementations.

The engine claims an instance with a conditional `Active`/`Faulted` → `Busy` update and, for transition commands, stores
its pending transition and persistable input in that claim. The claim is separate from task execution. On success, the
instance snapshot, history, incidents and follow-up jobs commit together. A running claim refreshes `BusySince` every
`BusyHeartbeatInterval`; only a claim whose heartbeat stopped (the process died) and that is older than `BusyTimeout` is
marked Faulted by `WorkflowJobProcessor`. The retry endpoint can rerun the saved transition when its required input is
available. Long calls belong in `"background": true` onEntry tasks, which run as jobs outside the claim and merge their
data changes (a JSON Patch) when they finish.

Scheduled and asynchronous transitions, child cancellation, parent resumption and event delivery use the workflow
database's `WorkflowJobs` table, as do buffered messages for event transitions and background tasks. Workers claim due
jobs with leases that a running job renews; on PostgreSQL the claim is `FOR UPDATE SKIP LOCKED`, so nodes never wait for
or race over the same row, and a node that writes jobs sends `NOTIFY` so the `WorkflowJobListener` of every other node
wakes its processor immediately instead of after `JobPollingInterval`. Child cancellation waits while the child is Busy;
event handlers may receive duplicates and must be idempotent. A failed job is retried with backoff and, after 20
attempts, atomically removed and recorded as a `WorkflowIncident` in a single database transaction. Task data is copied for each in-process attempt; external side effects
need an idempotency key. The built-in HTTP task sends `WorkflowTaskContext.IdempotencyKey` as `Idempotency-Key` unless
the definition supplies that header. An explicit retry is a new command and can use a different key.

History rows keep a full data snapshot (`history: full`), mostly only the change as a JSON Patch with a snapshot
checkpoint at the first and final row, every `historySnapshotEvery` rows and when a patch saves nothing (`delta`), or
nothing (`none`; the instance data row is still written on every transition). These history patches are derived by the
engine after each transition (`JsonPatch.Diff`) and replayed by `HistoryDataAsync` – examples in the README section
"History modes". Separately, a transition with `input.format: jsonPatch` takes a client's RFC 6902 patch on the data at
its `into` path (`JsonPatch.ApplyStrict`, atomic, validated by the input schema); `JsonPatch` lives in the shared
`Middt.Framework.Expressions` so the Blazor frontend computes form patches with the same code, and its TypeScript twin is
checked against it by `spec/json-patch.conformance.json`. The EF model is cached per workflow schema, so several workflow schemas can live in one process.

Definition versions are retained for running instances. Instance-level access checks and list filters apply each
instance's stored version of `roles` and `instanceAccess`, rather than the newest definition's rules.

In addition to core instance execution, `Middt.Framework.Workflow` exposes enterprise governance and operations APIs:
- **Unified Human Task Inbox (`GET /api/workflows/inbox`) & Task Claim / Multi-Target Delegation (`POST /api/workflows/{workflow}/instances/{id}/claim`, `/unclaim`, `/assign`)**: aggregates active human-state instances across all workflows (`?assignment=all|mine|unassigned|claimed`), allows claiming exclusive task ownership (`🙋 Üzerime Al`), releasing back to the pool (`🔓 Görevi Bırak`), or delegating (`↪️ Devret / Ata`) to a single user, multiple users (`users`), or roles (`roles` / `role:<name>` / `@<name>`), and automatically clears `AssignedTo` when moving to a new state.
- **Running Instance Version Migration (`POST /api/workflows/{workflow}/migrate`)**: migrates `Active` / `Faulted` instances from an older definition version to a target version with optional `stateMap` (`oldState -> newState`) and `dryRun` preview, recording `$migrate` (`Trigger = "Migration"`) in `WorkflowHistory`.
- **Operations Center & Batch Retry (`GET /api/workflows/operations`, `POST /api/workflows/operations/retry-batch`)**: surfaces faulted instances, unresolved incidents, and pending jobs, and supports batch retry with per-item input overrides.
- **Dry-Run Simulator (`POST /api/workflows/{workflow}/simulate`)**: executes a workflow definition (or an inline candidate definition) through a sequence of transitions on an isolated in-memory clone without writing to the database or calling external HTTP/message brokers.
- **Real-Time Workflow SSE (`GET /api/workflows/events`, `GET /api/workflows/{workflow}/instances/{id}/events`)**: `WorkflowEventStreamHub` broadcasts every committed transition, start, retry, background task completion, and event message delivery as a `text/event-stream` (`event: workflow-event`) after checking `EnsureCanAccess`, so open Blazor and Angular detail views refresh immediately.
- **Portable Bundle Export/Import (`GET /api/workflows/bundle`, `POST /api/workflows/bundle/import`), Runtime Deployment (`POST /api/agents/catalog`, `POST /api/workflows/definitions`, `POST /api/ui/layouts/seed`), & Retention Purge (`POST /api/workflows/maintenance/purge`)**: deploys AI agent catalogs, workflow definitions, and UI layouts to the standalone runtime over HTTP, exports and imports complete bundles with SHA-256 integrity verification and `dryRun` support for CI/CD promotion, and purges terminal (`Completed`, `Cancelled`, `Faulted`) instances older than a retention threshold in bounded batches.

## Project map

Folders follow the layers: `sdk/shared` (contracts), `sdk/server` (backend), `sdk/client` (`.NET` client SDK, BFF, Blazor, TypeScript `ui-core`, Angular), `runtime/Middt.Workflow.Runtime`, `identity/Middt.Identity`, and `demo/` (`Middt.Demo.Workflows`, `Middt.Demo.Blazor`, `Middt.Demo.Angular`, `angular-app`); `spec`, `tests`, `docs` at the root.

| Project | Layer | Notes |
|---|---|---|
| Middt.Framework.Model | contracts | DTOs, settings |
| Middt.Framework.Expressions | contracts | portable language + DynamicExpresso evaluator (workflows) |
| Middt.Framework.Ui.Abstractions | contracts | layout model, specs, validator, formats, schema generator |
| Middt.Framework.Common | backend / host helpers | security headers, forwarded headers, local URL check |
| Middt.Framework.Api | backend | hosting, controllers, JWT, ProblemDetails, OpenAPI |
| Middt.Framework.Workflow | backend | engine, endpoints, `IWorkflowViewRenderer`, `WorkflowInstanceDataQuery` |
| Middt.Framework.Ui.Server | backend | layout store, endpoints, `UiHtmlRenderer` |
| Middt.Framework.Rebus | backend | messaging on Rebus (RabbitMQ / in-memory), `publish` workflow task |
| Middt.Framework.Agents | backend | Microsoft Agent Framework (`Microsoft.Agents.AI` + `Microsoft.Extensions.AI`), `agent` & `mcp` workflow tasks, declarative tools bridged to `IWorkflowTask`, AI-driven routing, runtime CRUD/validate/test endpoints (`MapMiddtAgents()`) |
| Middt.Workflow.Runtime | standalone runtime | generic container image (`runtime/Middt.Workflow.Runtime`) hosting the workflow engine, JSON UI server, AI agents/MCP runtime, Rebus/RabbitMQ, and dynamic CRUD controller with zero domain flows/layouts |
| Middt.Demo.Workflows | demo deployer | domain `Agents/`, `Layouts/`, `Workflows/`, and `Seeds/initial-instances.json` deployed over HTTP to `Middt.Workflow.Runtime` |
| Middt.Framework.Client | client SDK | typed API clients (`WorkflowApiClient` incl. SSE, `UiApiClient`, `UiDataClient`, `AgentApiClient`), token refresh |
| Middt.Framework.Bff | frontends | cookie session, token store + refresh, login pages, `/bff/user`, `/api` proxy (YARP) |
| Middt.Framework.Blazor | Blazor | BFF auth, workflow screens (`Workflow/`), AI agent studio (`AgentStudio`), layout widgets/hosts/editor (`Ui/`) |
| sdk/client/ui-core | TypeScript | `@middt/ui-core` – contract, expressions, formats, layout/data/workflow/agent clients |
| sdk/client/angular | Angular | `@middt/angular` library workspace (demo app: `demo/angular-app`) |
