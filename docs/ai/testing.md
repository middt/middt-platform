# Testing and verification

| Level | Command | Notes |
|---|---|---|
| CI build | `dotnet build Middt.Framework.slnx -p:ContinuousIntegrationBuild=true` | warnings are errors |
| .NET tests | `dotnet test --project tests/Middt.Framework.Tests` | complete xunit suite |
| PostgreSQL tests | `dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*WorkflowPostgresTests"` | Testcontainers (`postgres:17-alpine`, needs Docker; skipped with the reason when Docker is missing): partitions, jsonb GIN plans, two-node claim races, `SKIP LOCKED`, `LISTEN`/`NOTIFY`, schema upgrade. Each host gets its own schema (`WorkflowTestHost.StartPostgresAsync`); two hosts on one schema act as two nodes |
| Contracts | conformance + schema tests (part of the .NET tests: expressions, JSON Patch, UI layout schema) and `node --test` in ui-core (expressions and JSON Patch diff cases) | regenerate schema with `MIDDT_UPDATE_SPEC=1` |
| TypeScript | `cd sdk/client/ui-core && npm run build && node --test test/` | |
| Angular | `cd sdk/client/angular && npx ng build middt-angular`, then `cd demo/angular-app && npx ng build sample --configuration production` | 0 warnings |
| Full stack | `docker compose up -d --build` (needs `.env`) | Blazor :8080, Angular :8081, API :8180, Identity :8083; demo JSON is pushed by the one-shot `middt-demo-workflows` service |
| E2E | `tests/e2e` Playwright suite (see below) | both frontends, same scenarios |
| API / manual | `postman/` – `Middt.Local` environment + `Middt-All-Workflows` collection and six per-workflow collections (`01-Customer-Onboarding` … `06-Invoice-Collection`); see `postman/README.md` | no collection for `commercial-limit-revision` and `ai-dispute-resolution` (drive them from the UI or `POST /api/workflows/{key}/instances`) |

## E2E
The Playwright suite is one file, `tests/e2e/full-stack.spec.ts`, and runs against both demo frontends and the PostgreSQL
backed identity service in CI. Run `npm ci && npx playwright install chromium && npm test` in that folder while the Compose
stack is running. Base URLs come from `BLAZOR_BASE` (default `http://localhost:8080`), `ANGULAR_BASE` (`http://localhost:8081`)
and `IDENTITY_BASE` (`http://localhost:8083`). It covers, per frontend: sign-in through the identity provider and the
JSON-backed workflow/layout pages with their filters; the identity admin user search; a full JSON-backed workflow run
(ETag / `If-None-Match` 304, task trace, SubFlow transition forwarding, `$self`, `fanOut`); and real-time SSE updates of
the instance detail plus the `stat`, `tabs`, `timeline` widgets. Add new scenarios to this file using the stable
ids/classes/aria labels described in the nested `AGENTS.md` files.

## Writing tests
- Use the existing hosts (`TestApiHost`, `WorkflowTestHost`, `UiTestHost`, `SpaBffTestHost`, `BlazorTestHost`, `IdentityTestHost` in `tests/…/Identity`).
- Contract and architecture suites: `Expressions/PortableExpressionConformanceTests` and `Expressions/JsonPatchConformanceTests` run the `spec/*.conformance.json` files, `Architecture/LayeringTests` pins assembly references (fix the dependency, not the test), `Observability/OpenObserveObservabilityTests.cs` pins the OTLP exporter endpoint resolution (traces / logs / metrics, `http/protobuf` and `grpc`, OpenObserve and collector URLs).
- Prefer behaviour over implementation; for security fixes assert the attack fails (e.g. 403/404, no request forwarded).
- For performance fixes assert the mechanism (e.g. SQL contains parameters, store read count, command count).
- For workflow changes, use `WorkflowTestHost` and its fake clock. Check both the committed instance and `WorkflowJobs`:
  a Busy recovery must retain a usable pending transition; child cancellation must wait for a Busy child; undelivered
  events must survive later state changes. Test list access with multiple definition versions when roles or owner rules
  change. `WorkflowReliabilityTests` contains focused examples.
