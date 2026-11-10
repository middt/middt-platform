# sdk/client/Middt.Framework.Blazor – Blazor frontend (read the root AGENTS.md first)

`Middt.Framework.Blazor` (Blazor Server + Radzen).

- Authentication comes from `Middt.Framework.Bff`; `ServerSessionState` reads tokens from the server store for the
  circuit, API calls go through `Middt.Framework.Client` typed clients.
- Layout renderer lives in `Middt.Framework.Blazor/Ui/` (namespace `Middt.Framework.Blazor.Ui`): `Ui/Components/` (`UiNodeView`, `UiNodeList`, `UiLayoutView`) with the display widgets in `Ui/Components/Display/` (`HeadingWidget`, `TextWidget`, `FieldsWidget`, `AlertWidget`, `BadgeWidget`, `StatWidget` (`stat`), `TabsWidget` (`tabs`), `TimelineWidget` (`timeline`), `SectionWidget`, `ColumnsWidget`, `DividerWidget`, `ListWidget`, `LinkWidget`), interactive widgets in `Ui/Widgets/` (`UiGridWidget` (`grid`), `UiRecordWidget` (`record` – keys reloads by `(entity, recordId)` with per-request cancellation & version guards), `UiLayoutWidget` (`layout` – embeds another JSON
  layout by `key` with optional `data` scope and `MaxDepth = 5` recursion guard), `UiFieldEditor`, `UiDataGrid`, dialogs), `UiLayoutHost`, `UiPageHost`,
  `UiLayoutEditor` (visual drag-and-drop Screen Designer at `/ui-admin`), registry `Ui/Rendering/UiBlazorWidgets.cs` (`CreateDefault()` – add every new widget there). Markup must match `UiHtmlRenderer` and the Angular widgets (same
  classes, ids, aria labels) – a parity test compares visible text.
- Widgets derive from `UiWidgetBase`, which skips updates with unchanged `Node`/`Context`; override
  `SkipUnchangedParameters` if a widget renders outside state. Pass stable `Variables` dictionaries to `UiLayoutHost`.
- Performance rules: no per-render allocations of parameter dictionaries/contexts, no JS interop on every render,
  cancellation-safe caches (`UiLookupCache`), `PersistentComponentState` for prerender → circuit hand-over,
  fingerprinted assets (`~/` URLs + `WithStaticAssets`).
- Workflow & Agent screens: `Workflow/` (`WorkflowInstanceList.razor`, `WorkflowInstanceDetail.razor` with stepper/wizard and live SSE `WatchEventsAsync` auto-refresh, `JsonSchemaForm.razor`, `WorkflowDialog.razor`, `WorkflowDiagram.razor`, visual drag-and-drop `WorkflowDesigner.razor` at `/workflow-admin` with bi-directional `/ui-admin` and `/agent-admin` links, and the AI agent studio `AgentDesigner.razor` at `/agent-admin` with live Playground).
- Tests: `tests/…/Blazor` and `tests/…/Ui/UiBlazorRenderingTests.cs` (HtmlRenderer with `UiBlazorWidgets` registered).
