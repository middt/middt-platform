# Playbook: add an AI agent, a declarative tool or an MCP server

Agents, tools and MCP servers are JSON files under `demo/Middt.Demo.Workflows/Agents/` (`Agents/*.json`,
`Agents/Tools/*.json`, `Agents/McpServers/*.json`), loaded by `Middt.Framework.Agents` (`MiddtAgentRegistry` /
`MiddtMcpClient`) and used by the `agent` / `mcp` workflow tasks. How the demo agents behave end to end:
[`docs/ai-agent-scenarios.md`](../ai-agent-scenarios.md). Full examples: `risk-limit-underwriter.json`,
`Tools/evaluate-collateral-and-sector-exposure.json` (expression), `Tools/audit-invoice-payments.json` (task),
`Tools/verify-central-bank-registry-http.json` (HTTP through the `http` task), `McpServers/banking-mcp.json`.

1. **Agent (`Agents/<name>.json`, `AgentDefinition`)**: `name` (file name when omitted), `description`, `instructions`
   (system prompt; `{{ }}` workflow expressions are rendered when the agent runs inside a task), `tools` (tool names),
   `mcpServers` (server names), `outputSchema` (JSON Schema the answer must satisfy; `default`s are used by the local
   fallback), `allowedTransitions` (transition keys the agent may select), `maxToolCalls`, `maxTotalTokens`,
   `maxOutputTokens`, `temperature`, `modelId`. There is no per-agent provider: the provider/model come from
   `AgentSettings` (`Provider`, `Endpoint`, `ApiKey`, `ModelId`, `UseLocalFallback`), shown read-only in the studio.
2. **Tool (`Agents/Tools/<name>.json`, `AgentToolDefinition`)**: `name` (the function name the model sees, snake_case),
   `description` (when to call it), `parameters` (JSON Schema of the arguments → `args.*`), `kind`:
   - `expression` (default): `expression` (single expression) or `output` (object of `{{ }}` templates) with `args` and
     `data` in scope.
   - `task`: `task` (any registered `IWorkflowTask`: `set`, `http`, `cache`, `operation`, `loadWorkflow`,
     `queryWorkflow`, `updateWorkflow`, `triggerWorkflow`, …) + `config` (the task's config, templated with `args`);
     `into` names the result path (default `__toolResult`); `writeToWorkflowData: true` persists changes into the
     instance data.
   - `http`: **top-level** `url` (required), `method` (POST when `body` is set, else GET), `queryParams`, `headers`,
     `body` – templated with `{{ args.* }}` / `{{ data.* }}`, executed through `HttpTask` (so `AllowedHttpHosts`, no
     redirects, timeouts apply). Not inside `config` – that is the `kind: "task", "task": "http"` form.
   - `mcp`: `mcpServer` **and** `mcpTool` are both required.
   - Optional `roles`: only callers in one of the roles (or `ui-admin` / `system`) may execute the tool.
3. **MCP server (`Agents/McpServers/<name>.json`, `McpServerDefinition`)**: `name`, `description`, `endpoint` (HTTP
   JSON-RPC 2.0 URL of a remote server), `headers` (static, e.g. `Authorization`), `tools[]` (tool definitions as above),
   `resources[]` (`uri`, `name`, `description`, `mimeType`, `content`), `prompts[]` (`name`, `description`, `template`
   with `{{ args.* }}`). **Without `endpoint` the server runs in-process** and serves its declarative `tools` /
   `resources` / `prompts` directly (also the fallback when `UseLocalFallback` is on and the endpoint is unreachable).
   There are no `transport`, `command`, `arguments` or `allowedTools` keys.
4. **Use it in a workflow**: `{ "task": "agent", "config": { "agent": "<name>", "prompt": "...", "context": { ... },
   "into": "<path>", "mode": "single" | "sequential" | "concurrent" | "handoff", "agents": [...] } }`; per-task overrides
   `maxToolCalls`, `maxTotalTokens`, `temperature`, `model`, `sessionKey` / `sessionInto`, `allowedTransitions` /
   `transitionInto`, `toolCallsInto`, `metaInto`, `parseJson`, or an `inlineAgent`. In a state with outgoing `Automatic`
   transitions the task injects `nextTransition` into the output schema and selects the chosen transition itself – the
   selection is applied before transition `rule`s, so keep `allowedTransitions` tight (see
   [`security-checklist.md`](security-checklist.md)). `mcp` task: `callTool`, `listTools`, `readResource`,
   `listResources`, `getPrompt`.
5. **Deploy & validate** (`ui-admin` or `idp-admin`): `POST /api/agents/validate` with a single agent, a single tool or a
   whole `{ agents, tools, mcpServers }` catalog returns the validation errors without saving; `POST /api/agents/catalog`
   deploys a catalog (what `DemoFlowDeployer` does: `docker compose up -d --build middt-demo-workflows`); `PUT/DELETE
   /api/agents/{name}`, `/tools/{name}`, `/mcp-servers/{name}` edit single items (also from the studio at `/agent-admin`:
   `AgentDesigner.razor` / `MiddtAgentDesignerComponent`); `POST /api/agents/test` (`{ agent | definition, prompt,
   data }`) runs a dry playground turn. `GET /api/agents` lists agents, `GET /api/agents/catalog` the whole catalog plus
   `provider`, `defaultModelId`, `hasExternalApiKey` – both only need authentication, so instructions are visible to
   every signed-in user. Hosts without HTTP deployment load the folders with
   `AddMiddtAgents(configuration, o => o.AddAgentsFromDirectory("Agents"))` (`AddToolsFromDirectory`,
   `AddMcpServersFromDirectory`).
6. **Verify**: `dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*Agent*"` (`tests/…/Agents`) and run
   the scenario in [`docs/ai-agent-scenarios.md`](../ai-agent-scenarios.md) through `/agent-admin` → Playground or the
   workflow; agent runs are visible in `GET /api/workflows/{workflow}/instances/{id}/agent-runs`.

Rules: tools must be deterministic for the local fallback (give `outputSchema` properties a `default`), every outbound
call goes through `HttpTask` or an MCP endpoint (allow-listed hosts), budgets (`maxToolCalls`, `maxTotalTokens`) are
mandatory for agents that call tools, and never put API keys into the JSON (`AgentSettings` / environment only).
