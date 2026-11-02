# sdk/client/Middt.Framework.Blazor – Blazor frontend (read the root AGENTS.md first)

`Middt.Framework.Blazor` (Blazor Server + Radzen).

- Authentication comes from `Middt.Framework.Bff`; `ServerSessionState` reads tokens from the server store for the
  circuit, API calls go through `Middt.Framework.Client` typed clients.
- Layout renderer lives in `Middt.Framework.Blazor/Ui/` (namespace `Middt.Framework.Blazor.Ui`): `UiNodeView`
  display widgets, `StatWidget` (`stat`), `TabsWidget` (`tabs`), `TimelineWidget` (`timeline`), `UiGridWidget` (`grid`), `UiRecordWidget` (`record` – keys reloads by `(entity, recordId)` with per-request cancellation & version guards), `UiLayoutWidget` (`layout` – embeds another JSON
  layout by `key` with optional `data` scope and `MaxDepth = 5` recursion guard), `UiFieldEditor`, `UiLayoutView`, `UiLayoutHost`, `UiPageHost`,
  `UiLayoutEditor` (visual drag-and-drop Screen Designer at `/ui-admin`), registry `UiBlazorWidgets`. Markup must match `UiHtmlRenderer` and the Angular widgets (same
  classes, ids, aria labels) – a parity test compares visible text.
- Widgets derive from `UiWidgetBase`, which skips updates with unchanged `Node`/`Context`; override
  `SkipUnchangedParameters` if a widget renders outside state. Pass stable `Variables` dictionaries to `UiLayoutHost`.
- Performance rules: no per-render allocations of parameter dictionaries/contexts, no JS interop on every render,
  cancellation-safe caches (`UiLookupCache`), `PersistentComponentState` for prerender → circuit hand-over,
  fingerprinted assets (`~/` URLs + `WithStaticAssets`).
- Workflow & Agent screens: `Workflow/` (list, detail with stepper/wizard and live SSE `WatchEventsAsync` auto-refresh, JSON Schema form, dialogs, visual drag-and-drop `WorkflowDesigner` at `/workflow-admin` with bi-directional `/ui-admin` and `/agent-admin` links, and visual `AgentStudio` at `/agent-admin` with live Playground).
- Tests: `tests/…/Blazor` and `tests/…/Ui/UiBlazorRenderingTests.cs` (HtmlRenderer with `UiBlazorWidgets` registered).
