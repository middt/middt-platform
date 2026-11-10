# Playbook: 100% Backend-Driven JSON UI, Workflows & Reusable Layout Widgets

Every screen, navigation menu item, workflow list table, transition form, state detail view, and reusable sub-widget in Middt Framework is defined **entirely in backend JSON** (`Workflows/*.json` + `Layouts/*.json`) with **zero custom `.razor` or `.ts` view code**.

Both **Blazor Server** and **Angular** render these JSON definitions identically at runtime.

---

## 1. Architecture Overview

| UI Surface | JSON Source | Rendered By (Blazor / Angular) |
|---|---|---|
| **Left Navigation Menu** (Workflows) | `Workflows/*.json` (`key`, `title`, `icon`) via `GET /api/workflows` | `MenuService` / `MenuService` |
| **Left Navigation Menu** (UI Pages) | `Layouts/*.json` (`kind: "page"` **with a `menu` object** `{ "title", "icon", "order" }` – pages without `menu` are reachable by URL only; optional `roles`) via `GET /api/ui/layouts?kind=page` | `MenuService` / `MenuService` |
| **Workflow Instance List Table** (`/workflow/{key}`) | `Workflows/*.json` (`dataSchema.properties`, `x-list`, `x-mask`, `x-search`) | `WorkflowInstanceList` / `MiddtWorkflowInstanceListComponent` |
| **Workflow Start & Action Forms** | `Workflows/*.json` (`transitions[].input.schema`, `x-lookup`, `x-enumFrom`) | `JsonSchemaForm` / `MiddtJsonSchemaFormComponent` |
| **Workflow State Detail Screen** (`/workflow/{key}/{id}`) | `Workflows/*.json` (`states[].view.layout`) $\rightarrow$ `Layouts/*.json` (`kind: "view"`) | `WorkflowInstanceDetail` + `UiLayoutHost` / `MiddtWorkflowInstanceDetailComponent` + `MiddtLayoutHostComponent` |
| **Reusable Nested Widgets** | `Layouts/*.json` (`kind: "view"`) embedded via `{ "widget": "layout", "key": "...", "data": "..." }` | `UiLayoutWidget` / `UiLayoutWidget` |
| **Directory / CRUD / Overview Pages** (`/ui/{key}`) | `Layouts/*.json` (`kind: "page"`) | `UiPageHost` / `MiddtLayoutPageComponent` |
| **Visual Screen Designer** (`/ui-admin`) | `Layouts/*.json` (`GET/PUT /api/ui/layouts/{key}`) | `UiLayoutEditor` / `MiddtLayoutEditorComponent` |
| **Visual Workflow Designer** (`/workflow-admin`) | `Workflows/*.json` (`GET/PUT /api/workflows/{key}/definition`) | `WorkflowDesigner` / `MiddtWorkflowDesignerComponent` |
| **Visual AI Agent, Tool & MCP Studio** (`/agent-admin`) | `Agents/**/*.json` (`GET/POST /api/agents/catalog`, `PUT/DELETE /api/agents/*`, `POST /api/agents/test`) | `AgentDesigner` (`Workflow/AgentDesigner.razor`) / `MiddtAgentDesignerComponent` |

> **Rule**: Never create hardcoded `.razor` or Angular `.ts` view components for business workflows or screens unless you are building a brand-new SDK primitive widget (`sdk/client/...`). All domain screens belong in `Layouts/*.json` and `Workflows/*.json`.

---

## 2. Dynamic Menu & List Columns from Workflow JSON (`Workflows/*.json`)

A workflow definition automatically registers:
1. Its **sidebar menu item** (`title` + `icon`, navigating to `/workflow/{key}`).
2. Its **instance list columns** (`dataSchema.properties` in property order).
3. Its **read-only REST query endpoint** (`POST /api/v1/{entity}/getitems`, `GET /api/v1/{entity}/getall`, `GET /api/v1/{entity}/getbyid?id=`), backed by `WorkflowInstanceDataQuery`. The entity name is resolved from the workflow `key` (`customer-onboarding`), its PascalCase form (`CustomerOnboarding`) or a prefix (`Customer`) – the prefix form only works when **exactly one** workflow key starts with it. These endpoints are mapped only by `WorkflowDataController` in `runtime/Middt.Workflow.Runtime` (`MapMiddtWorkflows` does not add them); a custom host needs its own controller over `WorkflowInstanceDataQuery` for `grid`, `record` and `x-lookup` to work.

```json
{
  "key": "customer-onboarding",
  "version": 1,
  "title": "Müşteri Kaydı & KYC",
  "icon": "badge",
  "description": "Gerçek ve tüzel kişi müşteri kabul, kimlik doğrulama ve KYC uyumluluk akışı",
  "dataSchema": {
    "type": "object",
    "required": ["fullName", "identityNumber", "segment"],
    "properties": {
      "fullName":       { "type": "string", "title": "Ad Soyad / Ünvan", "x-search": true },
      "identityNumber": { "type": "string", "title": "TCKN / VKN", "x-mask": { "pattern": "last4", "exceptRoles": [ "manager" ] } },
      "segment":        { "type": "string", "title": "Segment", "enum": ["Bireysel", "KOBİ", "Kurumsal"], "x-search": true },
      "kycScore":       { "type": "integer", "title": "KYC Skoru", "x-search": true },
      "riskLevel":      { "type": "string", "title": "Risk Seviyesi", "enum": ["Düşük", "Orta", "Yüksek"], "x-search": true },
      "approvalNote":   { "type": "string", "title": "Uyum Onay Notu", "x-list": false }
    }
  }
}
```

### `dataSchema` Extensions
- **`"x-list": false`**: Hides the property from the workflow instance table (`/workflow/{key}`) while keeping it in the schema for documentation, validation, and detail layouts.
- **`"x-search": true | "<mode>"`**: Adds a filter control to the workflow instance list toolbar (`WorkflowInstanceList` in Blazor & `MiddtWorkflowInstanceListComponent` in Angular). Properties with `"enum": [...]` render as a selectable dropdown; `number` / `integer` properties expand into two bounds (`<field>_min` ≥ and `<field>_max` ≤); other scalars render as a text search box. The string modes `"text"`, `"select"`, `"range"`, `"min"`, `"max"`, `"gte"`, `"lte"`, `"date-range"` force a control (`"min"`/`"gte"` and `"max"`/`"lte"` render a single bound). As soon as one searchable field exists, the toolbar also gets `createdFrom` / `createdTo` date filters on `CreatedAt`; set the root-level `"x-date-search": true | false` on `dataSchema` to force or suppress them. Filter keys accepted by the list APIs: `<field>` (equality / contains), `<field>_min|_gte|_from` (≥), `<field>_max|_lte|_to` (≤), `<field>_gt`, `<field>_lt`, and `createdFrom` / `createdAt_from`, `createdTo` / `createdAt_to`.
- **`"x-index": true | false`**: Forces (or suppresses) the per-workflow B-Tree index on that property (`WorkflowPartitionManager`). Without it an index is only created for `x-search` properties that are `enum`, `number`, `integer` or `boolean` – see [`workflow-partitioning-and-indexing.md`](workflow-partitioning-and-indexing.md).
- **`"x-mask": { "pattern": "last4" | "email" | "***", "exceptRoles": ["manager"] }`**: Masks string values in every projected response (`JsonSchemaValidator.ProjectData`: instance detail, list, `WorkflowInstanceDataQuery`, `loadWorkflow`) unless the caller has one of `exceptRoles`. The object form is the only accepted syntax (`"x-mask": "middle"` is rejected by the validator). Masked fields are never indexed and cannot be used as SQL-pushed filters.
- **`"x-roles": ["manager"]`**: Strips the property from projected data and from the projected schema (`JsonSchemaValidator.ProjectSchema`) for callers without one of the roles.
- **No `dataSchema`?** The instance list and its filters fall back to `start.input.schema.properties`.
- **`"x-lookup"`**: Populates dropdown options dynamically from an entity endpoint in transition forms:
  ```json
  "customerKey": {
    "type": "string",
    "title": "Müşteri",
    "x-lookup": {
      "entity": "CustomerOnboarding",
      "valueField": "key",
      "textField": "fullName"
    }
  }
  ```
- **`"x-enumFrom": "<arrayPath>.<field>"` or `{ "source": "<arrayPath>", "format": "{{ ... }}" }`**: Populates a transition form property's `enum` and `default` dynamically from an array in the current workflow instance's `data` (e.g. `"x-enumFrom": "partialPayment.reference"` or formatted `{ "source": "partialPayment", "format": "{{ row.reference }} ({{ row.amount }} ₺)" }` on `delete-payment`).
- **`"visibleWhen"`, `"requiredWhen"`, `"disabledWhen"` (or `"x-*"` aliases) with `ui.<field>`**: Controls live field visibility, conditional requiredness, and disabled state inside an open start/transition form (`JsonSchemaForm` / `MiddtJsonSchemaFormComponent`) or a layout `grid.form` / `grid.search` (`UiField`) **before** clicking Save/Submit:
  ```json
  "segment": {
    "type": "string",
    "title": "Müşteri Segmenti",
    "enum": [ "Bireysel", "KOBİ", "Kurumsal" ],
    "default": "Bireysel"
  },
  "taxOffice": {
    "type": "string",
    "maxLength": 100,
    "title": "Vergi Dairesi",
    "visibleWhen": "ui.segment != \"Bireysel\"",
    "requiredWhen": "ui.segment == \"Kurumsal\"",
    "disabledWhen": "ui.locked == true"
  }
  ```
  - `ui.<field>` reads the live in-memory value of `<field>` in the currently open form.
  - `data.<field>` reads the persisted workflow instance data (or falls back to `ui` on start forms).
  - `user` (`user.IsInRole("manager")`, `user.Name`, `user.Id`) reflects the authenticated caller on both frontends and the server (defaulting to an anonymous user with no roles when unauthenticated).
  - When `visibleWhen` evaluates to `false`, the field is hidden in the UI, omitted from the submitted payload, and stripped by `JsonSchemaValidator` on the server so unvalidated hidden values can never be injected via API.
  - When `requiredWhen` evaluates to `true` (and the field is neither hidden nor disabled), the field displays the `*` indicator and is enforced as required on both client and server (`JsonSchemaValidator`).
  - When `disabledWhen` evaluates to `true`, the field is prefilled from `data.<field>` (or schema `default`), rendered as disabled (`read-only`) in the UI, exempted from `required`/`requiredWhen`, and protected on the server (`JsonSchemaValidator` rejects any modified value with `$.<field>: is disabled.`).
  - On inline wizard step forms, `WorkflowInstanceDetail` also exposes the live step form values as `ui` in the state layout's `UiRenderContext`.

---

## 3. Backend-Driven State Views (`view.layout`)

Every `Human` or `Finish` state in a workflow points to a `kind: "view"` layout key:

```json
{
  "key": "kyc-review",
  "title": "Uyum / KYC İncelemesi",
  "subType": "Human",
  "view": { "layout": "kyc-review" },
  "transitions": [ ... ]
}
```

When a user opens `/workflow/customer-onboarding/{instanceId}`, `WorkflowInstanceDetail` (Blazor) or `MiddtWorkflowInstanceDetailComponent` (Angular) fetches the `kyc-review` layout from `/api/ui/layouts/kyc-review` and passes `{ data: instance.data, instance: ... }` into the layout's `UiRenderContext`.

Inside the `kind: "view"` layout:
- `{{ data.fullName }}` and `{{ instance.key }}` render properties from the workflow instance.
- `visibleWhen` expressions (`instance.state == "kyc-review"`, `data.kycScore >= 750`, `user.IsInRole("manager")`) dynamically show or hide alerts, badges, fields, or sections.

---

## 4. Reusable Widgets via `"widget": "layout"` (Composition & Nesting)

You can build reusable partial layouts (`kind: "view"`) and embed them inside any other layout (`page` or `view`) using `"widget": "layout"`.

### Spec of `"widget": "layout"` (`UiWidgets.Layout`)
| Property | Type | Required | Description |
|---|---|---|---|
| `widget` | `"layout"` | Yes | Identifies the embedded layout widget |
| `key` | `string` (`a-z 0-9 - _ .`) | Yes | Key of the target `UiLayoutDocument` stored in the backend |
| `data` | `string` (expression) | No | Optional portable expression evaluated against the parent scope to bind as the child layout's `data` variable (e.g. `"data.applicant"` or `"row"`). When omitted, the child layout inherits the parent's `UiRenderContext` (`data`, `instance`, `record`, `row`, `query`, `user`). |
| `title` | `string` (template) | No | Optional heading (`{{ }}` template) rendered above the embedded layout |
| `visibleWhen` | `string` (expression) | No | Optional condition controlling whether the embedded layout is rendered |

> **Cycle & Depth Guards**:
> 1. **Compile/Seed-time Validation**: `UiLayoutValidator.Validate` rejects direct self-embedding (`layout 'x' cannot embed itself`), and `UiLayoutValidator.ValidateGraph` rejects missing target layouts, non-`view` targets, and multi-hop circular chains (`A -> B -> A`). `ValidateGraph` runs only for **directory seeding** (`UiStartup` over `MiddtUiOptions.SeedDirectories`) and in `UiSampleLayoutTests`; `POST /api/ui/layouts/seed` and `PUT /api/ui/layouts/{key}` validate each layout on its own, so a broken cross-layout reference deployed over HTTP is caught only at render time (the runtime guard below).
> 2. **Runtime Recursion Guard**: Both Blazor (`UiLayoutWidget`) and Angular (`UiLayoutWidget`) enforce a maximum nesting depth of **5** (`MaxDepth = 5`).

### Pattern A: Shared Footer / Banner Across Multiple Workflows (Inheriting Parent Scope)

1. Define the reusable partial layout `Layouts/workflow-footer.json`:
   ```json
   {
     "$schema": "../../../spec/ui-layout.schema.json",
     "key": "workflow-footer",
     "title": "Ortak Akış Durum Altbilgisi",
     "kind": "view",
     "version": 1,
     "body": [
       { "widget": "divider" },
       { "widget": "text", "style": "small", "text": "Durum: {{ instance.stateTitle }} · Akış: {{ instance.workflow }}" }
     ]
   }
   ```

2. Embed it inside `kyc-review.json`, `credit-review.json`, `order-review.json`, `order-result.json`, and `risk-detail.json`:
   ```json
   { "widget": "layout", "key": "workflow-footer" }
   ```

### Pattern B: Reusable Widget Groups Scoped with `"data"` (`customer-summary-card` & `risk-eval-card`)

1. **`Layouts/customer-summary-card.json`** groups a `section`, conditional `badge`s, and `fields` (`fullName`, `identityNumber`, `segment`, `riskLevel`, `monthlyIncome`, `email`, `phone`):
   - In **`kyc-review.json`** (`customer-onboarding`), where the root `data` IS the customer object, it is embedded without `data`:
     ```json
     { "widget": "layout", "key": "customer-summary-card" }
     ```
   - In **`credit-review.json`** (`credit-allocation`), where the `loadWorkflow` task loaded the customer into `data.customer`, the **exact same widget group** is embedded with `"data": "data.customer"`:
     ```json
     {
       "widget": "layout",
       "key": "customer-summary-card",
       "data": "data.customer",
       "visibleWhen": "data.customer != null"
     }
     ```

2. **`Layouts/risk-eval-card.json`** groups the risk score `badge`s and evaluation `fields` (`checkType`, `riskScore`, `recommendedLimit`, `underwriterNote`, `evaluatedAt`):
   - In **`risk-detail.json`** (`risk-limit-eval`), it is embedded at root scope:
     ```json
     { "widget": "layout", "key": "risk-eval-card" }
     ```
   - In **`credit-review.json`** (`credit-allocation`), where the `risk-limit-eval` subflow wrote its result into `data.riskResult`, the **exact same widget group** is embedded with `"data": "data.riskResult"`:
     ```json
     {
       "widget": "layout",
       "key": "risk-eval-card",
       "data": "data.riskResult",
       "visibleWhen": "data.riskResult != null"
     }
     ```

---

## 5. Complete Reference of Built-in JSON Widgets (`UiWidgets.All`)

| Widget | Category | Key Properties | Description |
|---|---|---|---|
| `section` | Container | `title`, `children` | Titled card/panel grouping child widgets |
| `columns` | Container | `widths: ["2", "1"]`, `children` | Responsive multi-column layout |
| `tabs` | Container | `titles` (optional string array), `children` | Interactive tabbed container rendering each child (`section` etc.) as a switchable tab using `titles` or each child's `title` |
| `list` | Container | `source` (array expression), `empty`, `rowTransition` / `rowTransitions` (`[{ key, text, icon, style, input }]`), `children` | Repeats `children` for each item in `source` as `row` with `rowNumber` (`1..N`) in scope across Blazor, Angular, and static HTML `UiHtmlRenderer` (and optionally renders one or more row-level workflow transition buttons when each `key` is in `AvailableTransitions`, including `{ "patch": [...] }` payloads for `input.format: "jsonPatch"`) |
| `layout` | Container / Reusable | `key`, `data` (optional expression), `title` | Embeds another JSON layout by `key` with optional `data` scope |
| `heading` | Display | `text`, `level` (`1`..`6`) | Section or page heading (`{{ }}` template) |
| `text` | Display | `text`, `style` (`normal`/`lead`/`muted`/`small`/`code`) | Paragraph or inline text (`{{ }}` template) |
| `stat` | Display / KPI | `label`, `value` (`{{ }}` template), `format`, `subtitle` (`{{ }}` template), `icon`, `color` (`primary`/`secondary`/`success`/`warning`/`danger`/`info`) | KPI / metric summary card |
| `alert` | Display | `severity` (`info`/`success`/`warning`/`danger`), `text` | Callout banner (`{{ }}` template) |
| `badge` | Display | `text`, `color` (`primary`/`secondary`/`success`/`warning`/`danger`/`info`) | Colored status pill |
| `divider` | Display | – | Horizontal separator line |
| `link` | Display | `text`, `href` (`{{ }}` template, validated by `UiUrl.IsSafe`), `style` (`link`/`button`) | Safe navigation link or action button |
| `fields` | Display | `items: [{ label, value, format, visibleWhen }]` | Label/value definition list (`format`: `N0`–`N6`, `F0`–`F6`, `d`, `D`, `g`, `t`) |
| `timeline` | Display / Process | `source` (array expression, with `title`, `subtitle`, `time`, `color`, `empty`) or `items` (`[{ title, subtitle, time, color, visibleWhen }]`) | Vertical process / activity timeline |
| `record` | Interactive | `entity`, `recordId` (`{{ }}` template), `notFound`, `children` | Loads one entity by ID and renders `children` with `record` in scope |
| `grid` | Interactive | `entity`, `title`, `columns`, `search`, `filter`, `pageSize` (1–500, default 10), `autoLoad` (default true), `create`, `edit`, `delete`, `form`, `formTitle`, `rowActions` | Full server-paged CRUD / directory data grid |

Every node also accepts `id` (unique per layout, used for element ids) and `visibleWhen` (portable expression).

### `grid` details (`UiWidgets.Grid`)
- `columns[]`: `field` (required, camelCase row property), `title`, `format` (`N0`–`N6`, `F0`–`F6`, `d`, `D`, `g`, `t`), `width` (CSS, e.g. `120px`), `sortable` (default true), `template` (`{{ row.firstName + " " + row.lastName }}`).
- `search[]` / `form[]` fields: `field` (required), `label`, `type` (`text` default, `textarea`, `number`, `integer`, `date`, `datetime`, `checkbox`, `select`, `picker`), `required`, `min`, `max`, `maxLength`, `placeholder`, `options` (static `select`: strings or `{ value, text }`), `lookup` (required for `picker`, or for `select` without `options`: `{ entity, valueField, textField, title, columns[], search[] }` – `columns`/`search` configure the picker dialog), `visibleWhen`, `requiredWhen`, `disabledWhen`.
- `filter`: fixed filter template map, e.g. `{ "categoryId": "{{ query.id }}" }`; `rowActions[]`: `{ text, icon, navigate (required local URL template), style: primary|secondary|light|danger }`.
- `create` / `edit` require a non-empty `form`. Against workflow-backed entities the grid is read-only in practice: `WorkflowDataController` exposes only `getall` / `getitems` / `getbyid`, mutations go through workflow transitions (use `link` / `rowActions` to `/workflow/{key}?start=1` or the instance page).

### Page layouts and the sidebar menu
A `kind: "page"` layout appears in the sidebar of both frontends only when it has a `menu` object; `roles` (optional) limits who can open it:
```json
{
  "$schema": "../../../spec/ui-layout.schema.json",
  "key": "customer-directory",
  "title": "Müşteri Rehberi",
  "kind": "page",
  "version": 1,
  "menu": { "title": "Müşteriler", "icon": "people", "order": 10 },
  "roles": [ "manager" ],
  "body": [ { "widget": "grid", "entity": "CustomerOnboarding", "columns": [ ... ] } ]
}
```
`MenuService` (Blazor and Angular) lists `GET /api/ui/layouts?kind=page` entries with `menu != null`, ordered by `menu.order` then `menu.title`. The demo layouts (`customer-directory.json`, `credit-pipeline.json`, `order-monitor.json`) have **no** `menu` and are linked from workflow views – copying one of them does not create a menu item until you add `menu`.

### UI endpoints (`MapMiddtUi`, authenticated; writes need the admin role, `ui-admin` by default)
| Endpoint | Purpose |
|---|---|
| `GET /api/ui/layouts?kind=page\|view` | list (summaries, filtered by the caller's roles) |
| `GET /api/ui/layouts/{key}` | one layout (ETag) |
| `GET /api/ui/layouts/{key}/html` | static HTML rendering (`UiHtmlRenderer`) |
| `GET /api/ui/widgets`, `GET /api/ui/schema` | widget catalog and the generated JSON Schema (`spec/ui-layout.schema.json`) |
| `POST /api/ui/layouts/validate` | validate without saving |
| `POST /api/ui/layouts/seed` | seed one layout or an array; a stored layout is replaced only when the seed `version` is higher |
| `PUT /api/ui/layouts/{key}` | save (body `version` = the version the change is based on, 0 = new; 409 on conflict) |
| `DELETE /api/ui/layouts/{key}?version=` | delete (optional optimistic version) |
| `GET /api/ui/layouts/{key}/revisions` | revision history |

---

## 6. Verification Checklist

Whenever you add or modify a JSON layout or workflow seed in `demo/Middt.Demo.Workflows/`:

1. Run schema & sample validation tests:
   ```bash
   dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*UiSampleLayoutTests" --filter-class "*WorkflowDefinitionTests"
   ```
2. Layouts: `UiLayoutStore.SeedAsync` (used by directory seeding in `UiStartup` **and** by `POST /api/ui/layouts/seed`) **only overwrites a stored layout when the seed `version` is higher** – bump `"version"` whenever you change a seeded layout. Workflows: `POST /api/workflows/definitions` replaces the stored definition with the **same `key` + `version` in place** (no version comparison; running instances of that version pick up the new definition), so bump `version` when running instances must keep the old state machine.
3. In the demo nothing is seeded on API restart: the JSON files are pushed by the one-shot `middt-demo-workflows` compose service (`DemoFlowDeployer`). After editing a seed re-run it: `docker compose up -d --build middt-demo-workflows`. Hosts configured with seed folders (`MiddtUiOptions.SeedDirectories`, `AddDefinitionsFromDirectory`) apply directory seeding at startup instead.
