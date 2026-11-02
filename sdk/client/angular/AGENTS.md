# sdk/client/angular – Angular frontend library (read the root AGENTS.md first)

Workspace with one library: `projects/middt-angular` → package **`@middt/angular`** (ng-packagr). The demo app is
NOT here (`demo/angular-app`). Depends on `@middt/ui-core` as a `file:` package – build ui-core first.

```bash
(cd ../ui-core && npm run build) && npx ng build middt-angular
```

- Structure: `core/src` (config/`provideMiddt`, `bffFetch` with `X-CSRF`, `AuthService` via `/bff/user`, guards,
  notifications), `ui/src` (layout renderer, display widgets including `stat`, `tabs`, `timeline`, `UiGridWidget`, `UiRecordWidget` keyed by `(entity, recordId)`, `UiLayoutWidget` for
  nested `"widget": "layout"`, field editor, dialogs, visual drag-and-drop `MiddtLayoutEditorComponent` at `/ui-admin`), `workflow/src` (API client tokens for workflows and agents,
  list/detail/wizard with live SSE `subscribeEvents` auto-refresh, JSON Schema form, visual drag-and-drop `MiddtWorkflowDesignerComponent` at `/workflow-admin`, and visual `MiddtAgentStudioComponent` at `/agent-admin`).
- Components: standalone, **OnPush**, signals/`computed` (zoneless app – plain field mutation after `await` won't
  re-render). No template function calls that allocate per change detection (use `computed`).
- Contract logic (expressions, formats, URL safety, field validation, API clients) belongs in **ui-core**, not here.
- Keep markup parity with Blazor (`middt-ui-*`, `middt-wf-*`, ids like `ui-form-{field}`, `wf-step-{transition}-{field}`,
  aria labels) – the Angular E2E suites use the same selectors.
- Public API: export new symbols from `src/public-api.ts`; the demo must only import from `@middt/angular`.
- Library entry points are `core`, `ui`, and `workflow`; the root entry point is a compatibility aggregate.
