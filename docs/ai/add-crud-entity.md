# Playbook: add a CRUD / directory screen end to end

Middt Framework uses a **100% backend-driven JSON** pattern for server-paged `grid` screens (`/ui/{key}` in both Blazor and Angular) without writing any C# or TypeScript UI code:

---

## Workflow JSON-Backed Screen (Zero C# Code)

In `demo/Middt.Demo.Workflows`, domain records live as JSON documents inside PostgreSQL-partitioned workflow instances (`WorkflowInstances` + `WorkflowInstanceData`). `WorkflowInstanceDataQuery` exposes `POST /api/v1/{workflowKey}/getitems`, `GET /api/v1/{workflowKey}/getall` and `GET /api/v1/{workflowKey}/getbyid?id=` (read-only) for every registered workflow definition, while mutations run through workflow start/transition endpoints (`/api/workflows/{workflow}/instances` and `/transitions/{transition}`) – no C# entity, repository or custom controller is needed **in the runtime**: the controller is `runtime/Middt.Workflow.Runtime/Controllers/WorkflowDataController.cs` and is not mapped by `MapMiddtWorkflows`, so another host must add an equivalent controller. The data is projected through `JsonSchemaValidator.ProjectData` (`x-roles`, `x-mask`) and filtered by the definition's `roles` / `instanceAccess`.

### 1. Workflow definition (`Workflows/<key>.json`)
1. Define or reuse a workflow in `demo/Middt.Demo.Workflows/Workflows/` (e.g., `customer-onboarding.json`, `credit-allocation.json`, `order-payment-orchestration.json`).
2. Declare searchable/sortable fields in `dataSchema.properties` with explicit JSON types (`string`, `integer`, `number`, `boolean`). `WorkflowPartitionManager` creates the PostgreSQL partitions automatically, but a typed B-Tree expression index on `WorkflowInstanceData."Data"` only for properties with `"x-index": true` or with `"x-search": true` **and** an `enum` / `number` / `integer` / `boolean` type (plain `string` + `x-search` → no index; `x-mask` or `"x-index": false` → never). Mark the fields the grid sorts/filters on accordingly (see [`workflow-partitioning-and-indexing.md`](workflow-partitioning-and-indexing.md)).

### 2. Screen = JSON layout (`Layouts/<screen-key>.json`)
1. Copy `demo/Middt.Demo.Workflows/Layouts/customer-directory.json` (or `credit-pipeline.json` / `order-monitor.json`).
2. Set `key`, `version`, `title`, `kind: "page"`, **`menu`** (`{ "title": "Müşteriler", "icon": "people", "order": 10 }` – without it the page is reachable by URL only; the demo layouts have no `menu`) and optional `roles` (`["manager"]`), then configure the `grid` widget:
   - `entity`: the workflow key in PascalCase or kebab-case (e.g., `"CustomerOnboarding"` or `"customer-onboarding"` — `WorkflowInstanceDataQuery` resolves both; a prefix such as `"Customer"` works only when exactly one workflow key matches).
   - `columns`: `{ "field": "key", "title": "Müşteri Anahtarı", "sortable": true, "format": "N0", "width": "120px", "template": "{{ row.fullName }}" }`.
   - `search`: filter fields `{ "field", "label", "type": text|textarea|number|integer|date|datetime|checkbox|select|picker, "options", "lookup", "visibleWhen" }`; `pageSize` (1–500, default 10), `autoLoad` (default true), `title`, `filter` (`{ "segment": "{{ query.segment }}" }`), `rowActions` (`{ "text", "icon", "navigate": "/workflow/customer-onboarding/{{ row.id }}", "style" }`). `create` / `edit` / `delete` + `form` / `formTitle` exist for entities with a writable controller; workflow-backed entities are read-only here (mutations are transitions).
3. Deploy the layout: in the demo re-run the one-shot deployer `docker compose up -d --build middt-demo-workflows` (it posts `Layouts/*.json` to `POST /api/ui/layouts/seed`; a stored layout is replaced only when the file's `version` is higher), or create/edit the layout at runtime in `/ui-admin`. Hosts configured with `MiddtUiOptions.SeedDirectories` seed the folder at startup (`UiStartup`).
4. The screen appears in **both** menus (Blazor `:8080`, Angular `:8081`) at `/ui/<screen-key>` – ordered by `menu.order`.

---

## Verify
Run `/verify` (or `dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*UiSampleLayoutTests"`), then open both frontends (`/ui/<key>`) to test paging, sorting, and filtering.
