# demo – workflow & server-driven UI demo (read the root AGENTS.md first)

Flat project structure: `Middt.Demo.AppHost` (Aspire), `Middt.Demo.ServiceDefaults`, `Middt.Demo.Api`,
`Middt.Demo.Blazor`, `Middt.Demo.Angular` (BFF host) + `angular-app` (Angular SPA workspace).
The demo shows framework usage – keep framework logic in `sdk/`.

- Seeds: `Middt.Demo.Api/Layouts/*.json` (layouts, `$schema` → `spec/ui-layout.schema.json`),
  `Workflows/*.json` (JSONC), and `Seeds/initial-instances.json` (declarative initial workflow instances seeded by `DemoWorkflowSeeder`). Layouts and workflows are validated at startup. Bump a layout's `version` to overwrite the stored copy.
- Everything is 100% backend-driven from JSON (see [`docs/ai/backend-driven-json-ui.md`](../docs/ai/backend-driven-json-ui.md)):
  - Sidebar workflow menu items (`icon`, `title`, `/workflows/{key}`) come from `GET /api/workflows/definitions`.
  - Sidebar layout menu items are ordered by `menu.order` (then `menu.title`) in both Blazor and Angular.
  - Instance list grid columns come from `dataSchema` in `Workflows/*.json` (use `"x-list": false` to hide a field from the list table).
  - Start and transition forms come from `input.schema` in `Workflows/*.json` (supports `"x-lookup"`, `"x-enumFrom"`, and `"x-mask"`) and transition button styling comes from declarative `transition.style` and `transition.icon`.
  - State detail views come from `Layouts/*.json` (`kind: "view"`, referenced via `"view": { "layout": "..." }`) and are editable at runtime in `/admin/layouts`.
  - Reusable partial layouts (e.g. `workflow-footer.json`) can be embedded inside any layout via `{ "widget": "layout", "key": "workflow-footer", "data": "<optional-expr>" }`.
  - RabbitMQ queues are discovered dynamically from `WorkflowRegistry` (`publish` task configs) + configuration in `RabbitMqQueueInitializer`.
  - There are NO hardcoded Razor or Angular views in the demo.
- Workflows: `customer-onboarding`, `credit-allocation` (+ `risk-limit-eval` subflow), `order-payment-orchestration`,
  `sla-escalation`, and `invoice-collection` (payment messages → `Event` transition `payment-received` with `bufferFor`, manual twin `record-payment` appending to `partialPayment[]`, `delete-payment` removing an entry from `partialPayment` and appending to `deletedPayment[]`, automatic `payment-completed` when `paidAmount >= total`, a `background` ledger call, `history: delta`). Postman collection 06 drives both. `update-billing-contact` shows `input.format: jsonPatch` (the client sends an RFC 6902
  patch on `data.billingContact`).
- Sign-in: both BFFs use Middt Identity (`identity/Middt.Identity`, :8083) configured by `demo/identity.json` (seed).
  Demo user `test@test.com` / `test` has roles `manager`, `ui-admin`, `idp-admin`; `user@test.com` / `test` has none.
- Running: `docker compose up -d --build` with `.env` (see `.env.example`). Rebuild only what changed:
  `docker compose up -d --build middt-sample-angular`.
- Angular demo consumes `@middt/angular` and `@middt/ui-core` as packages (`file:` deps + `preserveSymlinks`);
  `npm run build:sdk` builds both.
