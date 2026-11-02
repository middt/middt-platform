# Playbook: add or change a workflow

Definitions are JSON(C) files in `demo/Middt.Demo.Workflows/Workflows/` (`customer-onboarding.json`, `credit-allocation.json`, and `order-payment-orchestration.json` are full examples: `dataSchema`, `icon`, rules, subflows, `fanOut`, `loadWorkflow`, `cache`, roles, timers, and backend-driven `view.layout` screens; `invoice-collection.json` shows message-driven `Event` transitions, a `background` task and `history: delta`).

1. **Definition & Dynamic Menu / List Columns**:
   - Top-level properties: `key`, `version` (bump `version` when updating a seeded file so `WorkflowStartup` or `POST /api/workflows/definitions` updates the DB), `title`, `icon` (Material icon rendered automatically in the sidebar menu of both frontends), `roles`, optional `instanceAccess` (`owner` + `supervisorRoles`), `dataSchema`, optional `history` (`full` default, `delta`, `none`) and `historySnapshotEvery` (delta only, default 20), `start`, `states` (`type`, `subType`, `step`, `view`, `actors`, `onEntry`, `onExit`, `transitions`).
   - **History**: keep `full` unless the history grows large. `delta` stores mostly the change per transition (a JSON Patch) with a snapshot at the first and final row, every `historySnapshotEvery` rows and when a patch saves nothing. `none` writes no history rows – the instance data is still saved on every transition; use it for one-shot or high-volume flows whose audit lives elsewhere. What a row looks like, how the patch is produced and how to read versions back: [README – History modes](../../README.md#history-modes-and-how-json-patch-is-used).
   - If no external system sends the messages yet, give the same step a manual twin (as `record-payment` next to the `payment-received` event transition in `invoice-collection.json`) so it can be driven by hand.
   - **`dataSchema`**: Declares the JSON Schema for the workflow's `Data` document. Both frontends automatically generate the **workflow instance table columns** (`/workflow/{key}`) from `dataSchema.properties` in property order.
     - Set `"x-list": false` on any property you want to hide from the list table while keeping it in the schema.
     - Set `"x-search": true` to add a filter control to the workflow instance list toolbar.
     - Use `"x-mask": "middle" | "end"` for automatic sensitive field masking and `"x-roles": ["manager"]` for field-level role visibility.
   - Expressions in `rule`, `input.rule`, and `set` tasks use the server-side C# dialect (DynamicExpresso): `data.Amount > 500000`, `user.IsInRole("Manager")`, plus JSON array helpers `data.partialPayment.Sum("amount")`, `data.partialPayment.Any("reference", input.reference)`, `data.partialPayment.Find("reference", input.reference)`, and `data.partialPayment.Remove("reference", input.reference)`.
   - **State availability `rule` vs. input validation `input.rule`**:
     - `transition.rule` evaluates whether the transition is available in the current state (`data`, `instance`, `user`, `now`).
     - `transition.input.rule` evaluates when the transition is executed or when an event message arrives (`input`, `data`, `instance`, `user`, `now`), rejecting invalid input with 409 before tasks run (e.g., `"rule": "input.amount <= data.remainingAmount"`).
   - **Declarative button appearance (`style` & `icon`)**: Set `"style": "primary" | "success" | "danger" | "warning" | "secondary"` and `"icon": "<material_icon>"` directly on a transition definition (e.g., `"style": "danger", "icon": "delete"` on `delete-payment`).
   - A new version's access rules apply only to its instances. When changing `roles`, `instanceAccess`, or supervisor roles, check both old and new instances in the list and detail endpoints.

2. **Validation** runs at startup and during HTTP deployment (`POST /api/workflows/definitions`) – fix every reported error; add validator rules in `WorkflowRegistry` for new definition properties (with tests in `tests/Middt.Framework.Tests/Workflow`).

3. **Screens per state (100% Backend-Driven JSON)**:
   - Every `Human` and `Finish` state should specify `"view": { "layout": "<layout-key>" }` pointing to a `kind: "view"` JSON layout in `demo/Middt.Demo.Workflows/Layouts/` (e.g., `kyc-review.json`, `credit-review.json`, `order-review.json`, `order-result.json`, `risk-detail.json`, `invoice-detail.json`, `ai-dispute-detail.json`).
   - Reuse common partial layouts across multiple workflow views with `{ "widget": "layout", "key": "workflow-footer" }` (see [`backend-driven-json-ui.md`](backend-driven-json-ui.md)).
   - **Transition forms**: Define `input.schema` on transitions (`JsonSchemaForm` in Blazor / `JsonSchemaFormComponent` in Angular).
     - **Appending to an array (`into: "<array>[]"` & `[-1]`)**: Setting `"into": "partialPayment[]"` appends the validated input object to `data.partialPayment` (creating the `JsonArray` if needed), and `"set": { "partialPayment[-1].paidAt": "now" }` sets fields on the newly appended element.
     - **Dynamic enum from instance array (`x-enumFrom`)**: Setting `"x-enumFrom": "partialPayment.reference"` on a schema property dynamically populates `enum` and `default` from the current instance's `data.partialPayment` array when `GetAsync` projects `AvailableTransitions`.
     - **Partial updates**: `"input": { "format": "jsonPatch", "into": "<owned subtree>", "schema": { … } }` makes the body an RFC 6902 JSON Patch on `data.<into>` (both `into` and `schema` are required; the schema validates the subtree after the patch; a failed `test` is 409). Both frontends prefill the form with the current values and send only the difference. Not on the start transition, event transitions, `sensitive` inputs or `?async=true`. Example: `update-billing-contact` in `invoice-collection.json`; details in the [README](../../README.md#updating-data-with-a-json-patch-inputformat-jsonpatch).
     - **Reactive form field rules (`visibleWhen`, `requiredWhen`, `disabledWhen` & `ui.<field>`)**: Schema properties in `start.input.schema.properties` and `transition.input.schema.properties` support `"visibleWhen"` (or `"x-visibleWhen"`), `"requiredWhen"` (or `"x-requiredWhen"`), and `"disabledWhen"` (or `"x-disabledWhen"`). These evaluate portable expressions against `ui.<field>` (the live value selected/typed in the open form before clicking Save/Submit), `data.<field>` (the persisted workflow instance data, falling back to `ui` on start forms), and `user` (`user.IsInRole("manager")` of the authenticated caller, defaulting to anonymous). Both frontends reactively show/hide, require, or disable fields as the user edits the form, and `JsonSchemaValidator` enforces all three on the server (stripping hidden fields from the input payload so unvalidated values never enter `instance.Data`, enforcing `requiredWhen` unless the field is hidden or disabled, and rejecting any attempt to modify a disabled field away from its persisted `data.<field>` or schema `default` with `$.<field>: is disabled.`):
       ```json
       "segment": { "type": "string", "title": "Müşteri Segmenti", "enum": ["Bireysel", "KOBİ", "Kurumsal"], "default": "Bireysel" },
       "taxOffice": {
         "type": "string",
         "title": "Vergi Dairesi",
         "visibleWhen": "ui.segment != \"Bireysel\"",
         "requiredWhen": "ui.segment == \"Kurumsal\"",
         "disabledWhen": "data.segment == \"Kurumsal\" && !user.IsInRole(\"manager\")"
       }
       ```
     - Dynamic dropdown lookups against another workflow's data:
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

4. **Triggers**:
   - `Manual` (buttons), `Automatic` (`rule` / `default`), `Scheduled` (`after` / `at`) and `Event`: `"trigger": "Event", "event": "payment.received"` runs when a message of that name is delivered to the instance (`WorkflowEngine.DeliverMessageAsync`, `POST /api/workflows/{key}/instances/{id}/messages/{name}` or `POST /api/workflows/{key}/messages/{name}?key=<business key>`). The message body is the transition `input`; several event transitions may listen for one message and the first whose `rule` holds (it can read `input`) runs. Event transitions are never shown as buttons. The sender needs the transition `roles`; `?bufferFor=PT10M` keeps a message the instance cannot take yet. The start transition cannot be an event transition.

5. **Tasks**:
   - Built-in tasks: `set`, `setKey` (business key once known, optional `ensureUnique: true`), `log`, `http` (requires an explicit absolute URL, respects `AllowedHttpHosts`, no redirects; supports `into` for full `{ statusCode, isSuccess, body, headers }` envelope or `select` / `output` field array/mapping object to write only selected parts of `result.body.*` / `result.statusCode` into `instance.Data`), `publish`, `rabbitmq`, `operation`, `startWorkflow` (sync or async subflow), `loadWorkflow` (loads another workflow instance's JSON data, `select` projection array or mapping object, optional `includeAgents: true` and `includeHistory: true` by `key`/`id`/`where` into scope), `queryWorkflow` (queries multiple instances across workflows using unified `where` expressions with automatic PostgreSQL partition index pushdown, `orderBy`, `select`, `first`, and `aggregate` `count`/`sum`/`avg`/`min`/`max`), `updateWorkflow` (updates single or `all: true` workflow instances by `key`/`id`/`where` via `values` expressions referencing `target.*` & `data.*`, `merge`, or RFC 6902 `patch`), `triggerWorkflow` (executes a `transition` with optional `runAsync: true` or delivers an `event` message with optional `bufferFor` on single or `all: true` workflow instances and stores the result into `into`), `fanOut` (parallel execution of `subTask` over `items` with `maxConcurrency` and `into`), `cache` (cache-aside execution of `subTask` by `key` with `ttl` and `into`), `agent` / `aiAgent` (Microsoft Agent Framework `single`/`sequential`/`concurrent`/`handoff` execution with `tools`, `mcpServers`, `outputSchema`, `maxToolCalls`/`maxTotalTokens` budgets, automatic state-aware transition discovery/routing, and dedicated persistence in `WorkflowAgentSessions` / `WorkflowAgentRuns` / `WorkflowAgentToolCalls`), and `mcp` (Model Context Protocol `callTool`, `listTools`, `readResource`, `listResources`, `getPrompt`).
   - Declarative AI tools (`Agents/Tools/*.json`) can execute portable expressions (`kind: "expression"`), any registered `IWorkflowTask` (`kind: "task"`, `"task": "set" | "cache" | "http" | "operation" | "loadWorkflow" | "queryWorkflow" | "updateWorkflow" | "triggerWorkflow" | ...` with `into` and optional `writeToWorkflowData: true`), HTTP calls (`kind: "http"`), or MCP tools (`kind: "mcp"`), and support optional `roles` for RBAC gating.
   - **Unified Input / Output / Set / Into Conventions**:
     - **Outgoing input (`input` / `data` / `body` / `payload` / `arguments`)**: `transition.input`, `subFlow.input`, `startWorkflow` (`data`/`input`), `triggerWorkflow` (`input`/`data`/`payload`), `http` (`body`/`payload`/`input`), `operation` (`input`/`args`/`payload`), `mcp` (`arguments`/`args`/`input`), `publish` & `rabbitmq` (`payload`/`body`/`message`/`input`/`data`).
     - **Expression assignment (`set` / `values`)**: `transition.input.set` (or `values`), `set` task (`values` or `set`), `updateWorkflow` (`values` or `set`).
     - **Target container path (`into`)**: `transition.input.into`, `subFlow.into`, and `into` across all tasks.
     - **Selective field projection & mapping (`select` / `output`)**: interchangeable across `subFlow`, `http`, `loadWorkflow`, `queryWorkflow`, `updateWorkflow`, `triggerWorkflow`, `startWorkflow`, `operation`, `mcp`, and `agent` (accepts either a field array `["a", "b"]` or a mapping object `{ "targetPath": "result.score * 2" }`).
   - **Audit & trace headers (`input.header`)**: Every start, transition, retry, and event delivery injects `input.header.correlationId`, `input.header.traceId`, `input.header.spanId`, `input.header.clientIp`, and `input.header.userAgent` into expression and task scope so you can persist trace metadata into `data.*` via `set` tasks (see `commercial-limit-revision.json`).
   - **Long calls**: mark a state `onEntry` task `"background": true` (not in finish or SubFlow states, not `setKey`). The state commits first and the instance stays Active; the task runs as a job outside the lock, its data changes are merged when it finishes, then the remaining `onEntry` tasks and automatic transitions run. A failure follows the task's `onError` (`Fault` → `retry` runs it again). Background tasks receive no transition `input`.
   - **Record lifecycle** (master data such as `customer-onboarding.json`): don't finish immediately after creation – set the business key with `setKey`, stay in a `Human` `Active` state with update (`update-income`, `add-kyc-note`) or close (`close-account`) transitions, and expose the active instances as a directory via `WorkflowInstanceDataQuery` (`/api/v1/CustomerOnboarding/getitems` / `/getall`).

6. **Partitioning & indexing**:
   - Declare searchable/filterable fields in `dataSchema.properties` (with explicit types: `integer`, `number`, `boolean`, `string`). The engine automatically creates dedicated PostgreSQL partition tables (`WorkflowInstances_{Suffix}`, `WorkflowInstanceData_{Suffix}`, `WorkflowJobs_{Suffix}`, `WorkflowHistory_{Suffix}`, `WorkflowIncidents_{Suffix}`, `WorkflowAgentSessions_{Suffix}`, `WorkflowAgentRuns_{Suffix}`, `WorkflowAgentToolCalls_{Suffix}`) and typed functional B-Tree indexes + trigram GIN index at startup or when deployed via `POST /api/workflows/definitions`.
   - See [`workflow-partitioning-and-indexing.md`](workflow-partitioning-and-indexing.md) for architecture, partition-wise joins, and custom indexing patterns.

7. **Governance, Simulation & Operations**:
   - **Immutable Runtime Versioning**: Saving a definition via `PUT /api/workflows/{workflow}/definition` automatically creates a new immutable version (`v2`, `v3`, …) when the workflow already exists, preserving the active version for running instances. Inspect versions (`GET /api/workflows/{workflow}/versions`), diff two versions (`GET /api/workflows/{workflow}/diff?fromVersion=1&toVersion=2`), or roll back the active version (`POST /api/workflows/{workflow}/versions/{version}/rollback`).
   - **Dry-Run Simulation**: Test transitions, rules, expressions, and JSON Schema validations without writing to the database or calling external side effects via `POST /api/workflows/{workflow}/simulate`.
   - **Unified Inbox & Operations**: Query actionable human tasks across all workflows (`GET /api/workflows/inbox`), inspect faulted instances/incidents/jobs (`GET /api/workflows/operations`), or batch-retry faulted instances (`POST /api/workflows/operations/retry-batch`).
   - **Bundles & Retention Purge**: Export/import workflows, layouts, and AI agent/tool/MCP catalogs (`GET /api/workflows/bundle`, `POST /api/workflows/bundle/import`), and purge old completed/cancelled/faulted instances (`POST /api/workflows/maintenance/purge`).

8. **Verify**:
   - Run `dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*WorkflowDefinitionTests" --filter-class "*UiSampleLayoutTests" --filter-class "*SampleJsonWorkflowTests"`.
   - Click through both frontends (`/workflow/{key}`).

For a transition that accepts secrets, declare each top-level secret property in `input.sensitive`. The engine strips those values from persisted history and pending input, and rejects asynchronous execution of such transitions.
