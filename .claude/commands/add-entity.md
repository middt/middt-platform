---
description: Add a CRUD / directory screen end to end (JSON-backed workflow + JSON layout in both frontends)
argument-hint: <ScreenOrEntityName> [details]
---

Add the CRUD / directory screen described here: $ARGUMENTS

Follow `docs/ai/add-crud-entity.md` and `docs/ai/backend-driven-json-ui.md`:
- Back the `grid` layout with a workflow definition's `dataSchema` (`entity: "<WorkflowKey>"`, copy `customer-directory.json` in `demo/Middt.Demo.Workflows/Layouts/`); mark filtered/sorted properties with `x-search` (enum/number/boolean) or `x-index: true` so an index is created.
- Add a `menu` object (`{ "title", "icon", "order" }`, optional `roles`) to the page layout – the demo layouts have none, and without it the page is not in the sidebar.
- Deploy with `docker compose up -d --build middt-demo-workflows` (bump the layout `version`).
- Run `/verify` and list the URLs to try in both frontends (`/ui/<key>`).
