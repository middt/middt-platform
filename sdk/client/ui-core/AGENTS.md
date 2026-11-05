# @middt/ui-core – TypeScript contract (read the root AGENTS.md first)

Framework-independent twin of `sdk/shared` (+ API clients). Consumed by `@middt/angular` and any other JS frontend.

- Zero runtime dependencies. **Erasable TypeScript only** (no enums, namespaces, parameter properties, decorators);
  relative imports with `.ts` extensions (rewritten on build). Node runs the sources directly in tests.
- `npm run build` (tsc → `dist/` JS + d.ts) is required before Angular builds; `node --test test/` must stay green.
- `test/conformance.test.ts` runs `spec/expressions.conformance.json` and `test/json-patch.test.ts` runs `spec/json-patch.conformance.json` – the same files the .NET tests run. Never "fix"
  a failing case by changing only one side; the spec is the contract.
- Mirrors: `expression.ts` ↔ `Expressions/Portable`, `render-context.ts` (formats, `UiUrl`) ↔ `UiRenderContext.cs`,
  `fields.ts` ↔ `UiFieldModels.cs`, `client.ts`/`workflow.ts` (`WorkflowApiClient` + `AgentApiClient`) ↔ `Middt.Framework.Client` + endpoints, `workflow-ui.ts` ↔ `JsonPatch.cs` + `WorkflowUi.cs`, `types.ts` ↔ Model DTOs.
- Clients never throw for HTTP failures (error envelopes like .NET `ApiClientBase`) and validate path segments
  (`pathSegment`: no '', '.', '..').
