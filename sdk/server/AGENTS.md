# sdk/server – backend (read the root AGENTS.md first)

Common · Api · Workflow · Ui.Server · Rebus · Agents. **Must not reference any client/frontend package**
(Client, Bff, Blazor, Radzen) – LayeringTests enforce it.

## Api (`Middt.Framework.Api`)
- Hosting: `AddMiddtApi()` / `UseMiddtApi()` (ProblemDetails, versioning, JWT, rate limiting, CORS,
  compression, OpenAPI+Scalar **Development-only by default**).
- Controllers: `BaseController`, `BaseTokenController` (refresh token rotation & revocation via `IRefreshTokenStore`).

## Workflow (`Middt.Framework.Workflow`)
- Definitions are JSON (`demo/Middt.Demo.Workflows/Workflows`), validated at startup by `WorkflowRegistry`
  (`WorkflowDefinitionValidator`) – add validation for every new definition property (including transition `style`/`icon` and `input.rule` expressions).
- State availability vs. input guard: `transition.rule` evaluates whether a transition is available in the current state (`data`, `instance`, `user`, `now`), whereas `transition.input.rule` evaluates when a transition is executed or an event message is delivered (`input`, `data`, `instance`, `user`, `now`).
- Errors shown to callers are hidden unless `ExposeErrorDetails`; the `http` task never follows redirects, requires an
  explicit absolute URL, and honours `AllowedHttpHosts` – keep all of these.
- `WorkflowEngine` runs transitions with an optimistic claim (Busy + revision), automatic chains, subflows, jobs
  (`WorkflowJobProcessor`), history and incidents. Keep round trips low (there is a test pinning the command count).
- Persist the pending command in the Busy claim. Final state, history and follow-up jobs commit together; event
  delivery, parent resume and child cancellation are replayable jobs. Do not delete pending event jobs when an instance
  finishes. Handlers and external task side effects must be idempotent; task attempts share an idempotency key but an
  explicit later retry can get a new one. Never persist `input.sensitive` values.
- A running claim refreshes `BusySince` (`StartBusyHeartbeat`, `BusyHeartbeatInterval`); recovery only faults claims whose
  heartbeat stopped. Long calls belong in `"background": true` onEntry tasks (`WorkflowEngine.Background.cs`): a `Task` job
  runs the task outside the claim, stores the outcome on the job, then claims and merges it as a JSON Patch
  (`JsonPatch` (Middt.Framework.Expressions)); a result for a state that was left is discarded (`BackgroundTaskId`).
- Jobs: on PostgreSQL claimed one at a time with `FOR UPDATE SKIP LOCKED`, leases renewed while running
  (`WorkflowHeartbeat`). After committing jobs call `WorkflowJobNotifier.NotifyAsync` (local signal + `NOTIFY`); other nodes
  wake through `WorkflowJobListener` (`LISTEN`).
- Messages (`WorkflowEngine.Messages.cs`): `Event` transitions, correlated by instance id or business key; transition roles
  are checked at send time; an undeliverable message is rejected or buffered as a `Message` job, which `SaveAsync` wakes when
  the instance enters a state that takes it.
- History: `AddHistory` is the only writer – `full` snapshot, `delta` (patch + checkpoint snapshots: first/final row,
  `HistorySnapshotEvery`, patch not smaller than data), `none`. `HistoryDataAsync` replays both kinds of rows.
- `input.format: jsonPatch`: the body is an RFC 6902 patch applied by `JsonPatch.ApplyStrict` (shared
  `Middt.Framework.Expressions`) to the data at `into` only, then validated by the input schema (`ApplyPatchInput`). Keep
  `into` + `schema` mandatory (the validator enforces it) – a root-level patch would let clients overwrite task output.
  Bare array bodies are wrapped as `{ "patch": [...] }` by the endpoint. Any change to `JsonPatch` must keep
  `spec/json-patch.conformance.json` green in .NET and ui-core.
- Schema changes: new columns go at the END of the partitioned DDL (`WorkflowPartitionManager`, positional row copies) and
  nullable (`EnsureColumnsAsync` adds them to existing databases). The EF model is cached per schema
  (`WorkflowModelCacheKeyFactory`).
- Access: definition `roles`, transition `roles`, `instanceAccess` (owner mode + supervisor roles). Every instance-level
  entry point must go through `EnsureCanAccess`; list filtering uses each instance's stored definition version.
- Static view HTML: `IWorkflowViewRenderer` implementations (e.g. `WorkflowLayoutRenderer` using `UiHtmlRenderer`).
- Persistence & Querying: `WorkflowDbContext` (schema `wf`, PostgreSQL declarative partitioning per workflow key, `jsonb` columns, functional B-Tree indexes from `dataSchema`), `WorkflowInstanceDataQuery` (exposes `/api/v1/{entity}/getitems|getall|getbyid` over workflow JSON instances; clamps `getitems` ≤ 100 and `getall` ≤ 200; pushes scalar filters and numeric/text sorting with deterministic `InstanceId` tie-breaker down to SQL before pagination, rejecting masked or non-pushable fields); ids use UUIDv7 (`Guid.CreateVersion7()`).
- Abandoned jobs: when a job reaches `MaxJobAttempts` (20), `WorkflowJobProcessor` atomically removes the job and writes the `WorkflowIncident` inside a single database transaction.
- Real-Time SSE (`WorkflowEventStreamHub`): `GET /api/workflows/events` and `GET /api/workflows/{workflow}/instances/{id:guid}/events` stream `text/event-stream` (`event: workflow-event`) notifications for every committed workflow state change, background task completion, timer, or event message after checking `EnsureCanAccess`.

## Agents (`Middt.Framework.Agents`)
- Built on Microsoft Agent Framework (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) and `Microsoft.Extensions.AI`.
- Registration & Deployment: `services.AddMiddtAgents(configuration, o => o.AddAgentsFromDirectory("Agents"))` loads agent definitions (`Agents/*.json`), declarative tools (`Agents/Tools/*.json`), and MCP servers (`Agents/McpServers/*.json`), and registers the `agent` (`aiAgent`, `ai-agent`) and `mcp` (`mcp-call`, `mcp-tool`, `mcp-resource`, `mcp-prompt`) workflow tasks into `WorkflowTaskRegistry`. `app.MapMiddtAgents()` exposes `GET /api/agents`, `GET/POST /api/agents/catalog`, `PUT/DELETE /api/agents/{name}`, `PUT/DELETE /api/agents/tools/{name}`, `PUT/DELETE /api/agents/mcp-servers/{name}`, `POST /api/agents/validate`, and `POST /api/agents/test` (`ui-admin` / `idp-admin`), persisting runtime catalog changes to `wf."WorkflowDefinitions"` (`Key = "__agents__"`).
- Providers: supports OpenAI, DeepSeek (`Provider: "DeepSeek"`, auto-handles `json_object` response format and `DEEPSEEK_API_KEY`), Ollama, custom `IChatClient`, and deterministic in-process `LocalAgentChatClient` fallback when `UseLocalFallback = true`.
- Tool <-> Workflow Task Bridge: `AgentToolDefinition` supports `kind: "expression"`, `kind: "task"` (or setting `"task": "<taskName>"` to execute any registered `IWorkflowTask` in `WorkflowTaskRegistry` such as `set`, `http`, `cache`, `operation`, `publish`, `rabbitmq`, `loadWorkflow`, `startWorkflow`), `kind: "http"` (delegates to `HttpTask`), and `kind: "mcp"`.
- Ambient Context: `WorkflowTaskAmbientContext` propagates the active `WorkflowTaskContext` across async agent/MCP calls so tools can access `data`, `args`, `input`, `instance`, and `user` (and optionally persist changes back to the workflow instance when `writeToWorkflowData: true`).
- State-aware routing & unified `into`: When an `agent` task runs in a state with outgoing `Automatic` transitions, it automatically discovers the state's transitions from `WorkflowRegistry`, injects `nextTransition` (`enum` + `default`) into `outputSchema` at runtime, and selects the chosen transition directly via `WorkflowTaskContext.SelectTransition`. Setting `"into": "<path>"` automatically embeds `nextTransition`, `toolCalls`, `meta`, and `session` inside `data.<into>` and restores `data.<into>.session` on subsequent turns.

## Ui.Server (`Middt.Framework.Ui.Server`)
- `UiLayoutStore` (EF, schema `ui`, revisions, optimistic version, cached snapshot with a reload lock), seeding from
  directories (higher `version` wins), endpoints `MapMiddtUi()` (admin role for writes), `UiHtmlRenderer` (plain C#,
  HTML-encodes everything – never write raw data with `Raw()`).

## Tests
`tests/Middt.Framework.Tests/{Api,Workflow,Ui,Messaging,Security,Agents}` – hosts: `TestApiHost`, `WorkflowTestHost`
(SQLite in-memory, or `StartPostgresAsync` on the Testcontainers PostgreSQL of `Workflow/Postgres`; FakeTimeProvider,
header based fake auth), `UiTestHost`. Behaviour that only PostgreSQL shows (partitions, jsonb indexes, claim races,
`SKIP LOCKED`, `LISTEN`/`NOTIFY`) is tested in `WorkflowPostgresTests`.
