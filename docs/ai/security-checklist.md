# Security checklist for changes

Answer these before finishing a change (add tests for anything security relevant):

- **AuthN/AuthZ**: is every new endpoint authenticated? Role checks? Row level: workflow `roles` + `instanceAccess`
  (`{ "mode": "owner", "supervisorRoles": [...] }`, enforced by `WorkflowEngine.EnsureCanAccess` and `WorkflowInstanceDataQuery`)
  where data belongs to users; field level: `x-roles` / `x-mask` applied by `JsonSchemaValidator.ProjectData` / `ProjectSchema`
  (a new read path must project through them). For workflow lists, are `roles` and `instanceAccess` checked against each
  instance's stored definition version before counting and paging?
- **HTTP envelope**: `WorkflowHttpInput` puts every request header (including `Authorization` and `Cookie`) and query parameter
  into expression scope as `input.header.*` / `input.query.*`. Does any `set`, `log`, `http` or AI tool template copy them into
  `data.*`, history, logs or outbound requests? Are header/query values treated as untrusted client input (never as a
  row-level filter – take the branch/tenant from `user` claims)?
- **Input**: request bodies validated and size-bounded? Page sizes clamped? No dynamic SQL/LINQ strings with user input
  (values as parameters)? Regex with timeouts?
- **Output**: data HTML-encoded (Razor/Angular do it – never `MarkupString`/`bypassSecurityTrust*`/`Raw()` with data)?
  URLs through `UiUrl`/`LocalUrl`? Error messages without internal details (ProblemDetails, hidden workflow errors)?
- **Tokens/session**: tokens never reach the browser (BFF), no new proxy routes around `X-CSRF`/deny list, cookies
  HttpOnly/SameSite, re-login revokes the old session.
- **Outbound calls**: allow-listed hosts, no redirects, timeouts (`http` task, remote MCP `http`/`sse` transports, AI HTTP tools, HttpClients).
- **Workflow retries**: are secret input fields declared in `input.sensitive` and absent from pending input/history? Does `RetryAsync` re-verify the pending transition's `roles`, `denyRoles` (including `$previousUser` 4-eyes), `rule`, and `input.schema` / `input.rule` / `DataSchema` before executing?
- **Cross-workflow tasks & AI tools**: do `loadWorkflow`, `queryWorkflow`, `updateWorkflow`, and `triggerWorkflow` enforce target workflow `roles`, row-level `instanceAccess`, and `DataSchema` field masking (`x-roles` / `x-mask`) when running under a user caller? Are AI tools gated by `roles` and bounded by `maxToolCalls` / `maxTotalTokens`?
- **Workflow messages**: do event transitions that change money or status carry `roles`? The sender is checked at send time and buffered messages later run as the system; message bodies are validated by the transition `input.schema` – keep `additionalProperties: false`.
- **Background tasks**: an `http` background task still honours `AllowedHttpHosts`; its side effects can be retried after a crash, so the receiver must deduplicate on `Idempotency-Key` (stable per background job).
  Can external task side effects and event handlers tolerate retries or duplicate delivery? Is the HTTP task's
  `Idempotency-Key` honored by the receiver when needed?
- **Secrets/config**: nothing in source or compose defaults; `.env` / user secrets / environment variables only.
- **Headers**: CSP not loosened (no inline scripts), security headers kept, compression not applied to pages that
  reflect input next to secrets (BREACH).
- **Dependencies**: `dotnet list package --vulnerable --include-transitive`, `npm audit --omit=dev` clean.
- **JSON Patch inputs** (`input.format: jsonPatch`): is `into` a subtree the caller may own, and does `schema` (with `additionalProperties: false`) bound what the patch can produce? Never point `into` at data written by tasks, roles or approvals.
