# Playbook: workflow partitioning & indexing

Every line-of-business workflow system faces a predictable database scaling bottleneck: workflow state tables grow unbounded, and instance payload data (`jsonb`) degrades index maintenance and cache hit rates when mixed into a single monolithic table.

Middt Framework solves this by implementing **declarative list partitioning by `WorkflowKey`** across all 8 runtime tables (plus the unpartitioned `WorkflowDefinitions` catalog table), combined with **automated schema-driven B-Tree and GIN indexing**.

---

## 1. Partitioning architecture

All 8 runtime workflow & AI agent tables in PostgreSQL are partitioned by `LIST ("WorkflowKey")`:

| Root Table | Partition Key | PostgreSQL PK | SQLite PK |
|---|---|---|---|
| `WorkflowInstances` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `("Id", "WorkflowKey")` |
| `WorkflowInstanceData` | `"WorkflowKey"` | `("InstanceId", "WorkflowKey")` | `("InstanceId", "WorkflowKey")` |
| `WorkflowJobs` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `("Id", "WorkflowKey")` |
| `WorkflowHistory` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `"Id"` (rowid autoincrement) |
| `WorkflowIncidents` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `"Id"` (rowid autoincrement) |
| `WorkflowAgentSessions` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `("Id", "WorkflowKey")` |
| `WorkflowAgentRuns` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `("Id", "WorkflowKey")` |
| `WorkflowAgentToolCalls` | `"WorkflowKey"` | `("Id", "WorkflowKey")` | `("Id", "WorkflowKey")` |

### Partition-wise joins
Because `WorkflowInstances` and `WorkflowInstanceData` share the exact same partition key (`WorkflowKey`) and composite key `(Id, WorkflowKey)`, PostgreSQL automatically enables **Partition-Wise Joins**:
```sql
SET enable_partitionwise_join = on;
```
When querying an instance with its data:
```sql
SELECT i."Id", i."State", d."Data"
FROM wf."WorkflowInstances" i
JOIN wf."WorkflowInstanceData" d ON i."Id" = d."InstanceId" AND i."WorkflowKey" = d."WorkflowKey"
WHERE i."WorkflowKey" = 'order-approval';
```
The query planner completely bypasses cross-table shuffling and joins only `WorkflowInstances_OrderApproval` with `WorkflowInstanceData_OrderApproval`.

---

## 2. Automatic partition and index lifecycle

Developers and AI assistants do **not** need to manually write raw partition DDL migrations for each workflow. 

When the runtime starts up or when definitions are deployed via `POST /api/workflows/definitions`:
1. `WorkflowStartup` / `WorkflowEndpoints` invokes `WorkflowPartitionManager.EnsurePartitionsAndIndexesAsync(...)`.
2. The manager inspects all definitions loaded in `WorkflowRegistry` (seeded or deployed from `demo/Middt.Demo.Workflows/Workflows/*.json`).
3. For each workflow:
   - Computes PascalCase suffix via `WorkflowPartitionManager.PartitionSuffix(workflowKey)`.
     *(e.g., `order-approval` $\to$ `OrderApproval`, `bike-repair_v2` $\to$ `BikeRepairV2`)*
   - Automatically executes `CREATE TABLE IF NOT EXISTS {schema}."{Table}_{Suffix}" PARTITION OF {schema}."{Table}" FOR VALUES IN ('{workflowKey}');` for all 8 partitioned tables.
   - Creates a catch-all fallback `{Table}_Default` for untracked or dynamic definitions.
4. **Inspects `dataSchema.properties` only** (`WorkflowPartitionManager.ExtractIndexedProperties`; `start.input.schema` is *not* read) and creates a typed B-Tree functional index `IX_Part_{Suffix}_{prop}` on the workflow's `WorkflowInstanceData` partition **only** for properties that are:
   - `"x-index": true`, **or**
   - `x-search` (`true` or a mode string) **and** `enum` / `number` / `integer` / `boolean` (SQL-pushable equality or range filters).
   - Never for properties with `x-mask` (masked fields are not filterable) or `"x-index": false`; a plain `string` + `x-search` text filter (`STRPOS`) gets no index.
   - Index expressions (note the quoted `"Data"` column): `integer` / `number` → `((("Data"->>'field')::numeric))`, `boolean` → `((("Data"->>'field')::boolean))`, `string` / `enum` → `(("Data"->>'field')::text)`. On an unpartitioned PostgreSQL table the index is partial (`WHERE "WorkflowKey" = '{key}'`).
   - **Stale index cleanup**: after creating the computed set, every existing index named `IX_Part_{Suffix}_*` that is not in that set is **dropped** (`DropStaleFlowIndexesAsync`) – this prefix is reserved for the manager.
5. Creates global `jsonb_path_ops` GIN indexes `IX_WorkflowInstanceData_Data_GIN` and `IX_WorkflowHistory_Data_GIN` on `"Data"` for arbitrary JSON containment queries (`@>`). There is no trigram (`pg_trgm`) index.
6. On **SQLite** (used in unit tests and local dev without Docker), the same property set gets partial expression indexes:
   `CREATE INDEX IF NOT EXISTS "IX_Part_{Suffix}_{prop}" ON "WorkflowInstanceData" (json_extract("Data", '$.{prop}')) WHERE "WorkflowKey" = '{key}';`
7. **Multi-Database SQL Pushdown & Universal Fallback (`WorkflowPartitionManager` & `WorkflowInstanceDataQuery`)**:
   - `BuildSqlDataFilterQuery` and `BuildOrderBySqlDataQuery` generate native SQL JSON filter & sort queries across:
     - **PostgreSQL (`Npgsql`)**: `"Data"->>'prop'`, `::numeric`, `STRPOS(...) > 0`
     - **SQL Server (`SqlServer`)**: `JSON_VALUE([Data], '$.prop')`, `TRY_CAST(... AS decimal(38,10))`, `CHARINDEX(...) > 0`
     - **MySQL / MariaDB (`MySql`)**: `JSON_UNQUOTE(JSON_EXTRACT(\`Data\`, '$.prop'))`, `CAST(... AS DECIMAL(38,10))`, `LOCATE(...) > 0`
     - **Oracle (`Oracle`)**: `JSON_VALUE("Data", '$.prop')`, `TO_NUMBER(... DEFAULT NULL ON CONVERSION ERROR)`, `INSTR(...) > 0`
     - **SQLite (`Sqlite`)**: `json_extract("Data", '$.prop')`, `CAST(... AS REAL)`, `INSTR(...) > 0`
   - For any other EF Core provider where `SupportsSqlDataPushdown` is `false`, `WorkflowInstanceDataQuery.MatchesAllConditions` evaluates filters and sorting in memory before pagination (`Skip`/`Take`).

---

## 3. How to configure a future workflow for indexing

When creating a new workflow JSON file (e.g. `invoice-processing.v1.json`):

### Step 1: Provide explicit property types and `x-index` / `x-search` in `dataSchema`
Define the properties you anticipate querying, filtering, sorting, or joining on in `dataSchema.properties` **and mark them**: `"x-index": true` for an index without a UI filter, or `"x-search": true` on an `enum` / `number` / `integer` / `boolean` property (which also adds the list filter). A typed property alone creates no index.

```jsonc
{
  "key": "invoice-processing",
  "version": 1,
  "title": "Fatura Onay Süreci",
  "dataSchema": {
    "type": "object",
    "properties": {
      "invoiceNumber": { "type": "string", "x-index": true },
      "vendorId": { "type": "integer", "x-search": true },
      "totalAmount": { "type": "number", "x-search": true },
      "isPriority": { "type": "boolean", "x-search": true },
      "status": { "type": "string", "enum": ["open", "paid"], "x-search": true },
      "dueDate": { "type": "string", "format": "date-time" },
      "iban": { "type": "string", "x-mask": { "pattern": "last4", "exceptRoles": ["auditor"] } }
    }
  },
  "start": { ... },
  "states": [ ... ]
}
```

### What happens automatically:
At next boot or deployment, the framework will create:
- `wf."WorkflowInstances_InvoiceProcessing"`
- `wf."WorkflowInstanceData_InvoiceProcessing"`
- `wf."WorkflowJobs_InvoiceProcessing"`
- `wf."WorkflowHistory_InvoiceProcessing"`
- `wf."WorkflowIncidents_InvoiceProcessing"`
- `wf."WorkflowAgentSessions_InvoiceProcessing"`
- `wf."WorkflowAgentRuns_InvoiceProcessing"`
- `wf."WorkflowAgentToolCalls_InvoiceProcessing"`
- Index `IX_Part_InvoiceProcessing_invoiceNumber` on `(("Data"->>'invoiceNumber')::text)` (`x-index: true`)
- Index `IX_Part_InvoiceProcessing_vendorId` on `((("Data"->>'vendorId')::numeric))` (`x-search` + integer)
- Index `IX_Part_InvoiceProcessing_totalAmount` on `((("Data"->>'totalAmount')::numeric))` (`x-search` + number)
- Index `IX_Part_InvoiceProcessing_isPriority` on `((("Data"->>'isPriority')::boolean))` (`x-search` + boolean)
- Index `IX_Part_InvoiceProcessing_status` on `(("Data"->>'status')::text)` (`x-search` + enum)
- **No** index for `dueDate` (typed only) or `iban` (`x-mask`).

### Step 2: Querying indexed workflow fields
When querying workflow instance data in custom endpoints, repositories, or `WorkflowInstanceDataQuery`:
- **Always include `WorkflowKey` in the query** to trigger partition pruning:
  ```csharp
  var results = await db.InstanceData
      .Where(d => d.WorkflowKey == "invoice-processing" && EF.Functions.JsonContains(d.Data, ...))
      .ToListAsync(cancellationToken);
  ```
- With typed B-Tree functional index in place, PostgreSQL can perform index range scans on numbers:
  ```sql
  SELECT * FROM wf."WorkflowInstanceData_InvoiceProcessing"
  WHERE (("Data"->>'totalAmount')::numeric) > 50000;
  ```

---

## 4. Advanced: custom composite indexes or special requirements

If a workflow requires compound indexes (e.g. `(vendorId, dueDate)` together):
1. Add an idempotent SQL statement in a custom hosted service or EF Core migration targeting the specific partition. The column is `"Data"` (quoted, case-sensitive) and the name must **not** start with `IX_Part_{Suffix}_` – indexes with that prefix that the manager did not compute are dropped on every startup/deploy:
   ```sql
   CREATE INDEX IF NOT EXISTS "IX_Custom_InvoiceProcessing_Vendor_DueDate"
   ON wf."WorkflowInstanceData_InvoiceProcessing" (
       ((("Data"->>'vendorId')::numeric)),
       ((("Data"->>'dueDate')::text))
   );
   ```
   The global GIN indexes (`IX_WorkflowInstanceData_Data_GIN`, `IX_WorkflowHistory_Data_GIN`, `jsonb_path_ops`) already serve containment queries (`"Data" @> '{"vendorId": 42}'`) on every partition.
2. Or declare a custom PostgreSQL SQL view over the partition tables:
   ```sql
   CREATE OR REPLACE VIEW wf."v_InvoiceProcessing" AS
   SELECT 
       i."Id",
       i."BusinessKey",
       i."State",
       i."Status",
       i."CreatedAt",
       (d."Data"->>'invoiceNumber') AS "InvoiceNumber",
       (d."Data"->>'vendorId')::numeric AS "VendorId",
       (d."Data"->>'totalAmount')::numeric AS "TotalAmount"
   FROM wf."WorkflowInstances_InvoiceProcessing" i
   JOIN wf."WorkflowInstanceData_InvoiceProcessing" d 
     ON i."Id" = d."InstanceId" AND i."WorkflowKey" = d."WorkflowKey";
   ```

---

## 5. Lifecycle and maintenance operations

### Detaching or Archiving Old Workflow Partitions
When a workflow version is retired or instances must be purged after legal retention limits:
```sql
-- 1. Detach partition from live traffic (instantaneous, zero table lock on active flows)
ALTER TABLE wf."WorkflowInstances" DETACH PARTITION wf."WorkflowInstances_OldFlow";
ALTER TABLE wf."WorkflowInstanceData" DETACH PARTITION wf."WorkflowInstanceData_OldFlow";

-- 2. Export, archive to cold storage, or drop
DROP TABLE wf."WorkflowInstances_OldFlow";
DROP TABLE wf."WorkflowInstanceData_OldFlow";
```
No WAL bloat, no table rewrite, zero impact on other workflows.

---

## 6. Verification checklist for AI assistants

When adding or altering a workflow:
1. Ensure the JSON file has `"key"` matching lowercase kebab-case (e.g. `"crm-lead"`).
2. Ensure properties intended for filtering or sorting are declared with `"type"` in `dataSchema.properties` **and** carry `"x-index": true` or `x-search` with an `enum` / numeric / boolean type – otherwise no index is created. Never rely on `start.input.schema` for indexing and never name hand-written indexes `IX_Part_*`.
3. Run tests:
   ```bash
   dotnet test --project tests/Middt.Framework.Tests -- --filter-class "*Workflow*"
   ```
4. Verify that CI build passes with 0 warnings:
   ```bash
   dotnet build Middt.Framework.slnx -p:ContinuousIntegrationBuild=true
   ```
