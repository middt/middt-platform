# Playbook: change the portable expression language

The language is a cross-platform contract: .NET (`sdk/shared/Middt.Framework.Expressions/Portable`) and TypeScript
(`sdk/client/ui-core/src/expression.ts`) must behave **identically**.

1. Write the intended behaviour as cases in `spec/expressions.conformance.json` (`expr`/`template`/`truthy` +
   `expected`, or `error: true`). Cover edge cases: null propagation, types, number text (ECMAScript rules), errors.
2. Run both suites and watch the new cases fail:
   `dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*PortableExpressionConformanceTests"` and
   `(cd sdk/client/ui-core && node --test test/)`.
3. Implement in C# (lexer/parser/evaluator) and TypeScript. New methods: add to the whitelist in both parsers.
4. Both suites green; update the language summary in the XML doc of `PortableExpression` and the README section.
5. Rebuild ui-core and the Angular library before testing the app.

Do not: add reflection-like features, allow arbitrary method calls, or make evaluation throw on data errors
(evaluation must be total – undefined → null).

## Portable whitelist (must stay identical on both sides)

Methods: `IsInRole`, `Contains`, `StartsWith`, `EndsWith`, `ToUpper`, `ToLower`, `Trim`, `Round`, `Abs`, `Floor`,
`Ceiling`, `Min`, `Max` – `PortableEvaluator` in C# and the `PORTABLE_METHODS` constant in `ui-core/src/expression.ts`
(unknown methods are parse errors). Members: `.Length` / `.Count` on arrays, `.Length` on strings, `user.Name`, `user.Id`,
`user.Roles`, `user.IsInRole(r)`, `now`. Adding a method means: conformance cases, both whitelists, both evaluators.
`spec/json-patch.conformance.json` is the sibling contract for `JsonPatch` (`JsonPatchConformanceTests` and
`ui-core/test/json-patch.test.ts`).

## Server-side dialect (DynamicExpresso) – not portable

Workflow `rule`, `input.rule`, `set` / `values`, `where` and task templates on the server use `ExpressionEvaluator`
(`sdk/shared/Middt.Framework.Expressions/ExpressionEvaluator.cs`, DynamicExpresso) – a richer C# dialect that is never
exposed to layouts or clients. Changing the portable language does **not** change it, and vice versa.
- Scope (`WorkflowExpressionEngine`): `data`, `input`, `args`, `result`, `child`, `instance`, `item`, `target`, `index`,
  plus `user` and `now`; `Math` and `TimeSpan` are referenced (`Math.Round(data.amount, 2)`, `TimeSpan.FromHours(2)`).
- JSON arrays are `JsonDynamicArray`: `Any()`, `Any(field, value)`, `Contains(value)`, `Find(field, value)`,
  `Sum(field)`, `Remove(field, value)`, `RemoveAll(field, value)`, `RemoveAt(index)`, `Last()`, `Last(field)`,
  `With(field, value)`, `Count` / `Length`; objects are `JsonDynamic` with `.With(field, value)`.
- Reflection and type access stay disabled; keep that when touching the interpreter setup.
