# İş Akışı Tabloları Bölümleme (Partitioning) ve İndeksleme Kılavuzu

Bu belge, Middt Framework Workflow Engine içerisindeki tüm çalışma zamanı veritabanı tablolarının (`WorkflowInstances`, `WorkflowInstanceData`, `WorkflowHistory`, `WorkflowJobs`, `WorkflowIncidents`, `WorkflowAgentSessions`, `WorkflowAgentRuns`, `WorkflowAgentToolCalls`) PostgreSQL üzerinde **Declarative Partitioning (Flow Bazlı Liste Bölümleme)** ve **akışa özel indeksleme** stratejileriyle nasıl ölçeklendirileceğini, yapılandırılacağını ve optimize edileceğini açıklar.

---

## 1. Mimari Genel Bakış

Middt Framework'te iş akışı motoru 8 bölümlenmiş (partitioned) çalışma zamanı tablosundan (ve akış/ajan tanımlarını tutan `WorkflowDefinitions` tablosundan) oluşur; çalışma zamanı tablolarının tamamı **`WorkflowKey`** etrafında birincil sınıf vatandaş olarak tasarlanmıştır:

| Tablo | Sorumluluk | Anahtar (PK) | Partition Tipi | Büyüme Hızı |
| :--- | :--- | :---: | :---: | :---: |
| **`WorkflowInstances`** | Akışın durum ve yaşam döngüsü metadata'sı (`State`, `Status`, `BusySince`, `Revision`, `BackgroundTaskId`, `HistoryPatchCount`, `Audit`) | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Orta |
| **`WorkflowInstanceData`** | Akışın tüm JSON iş verisi (`Data` column `jsonb`, `DataVersion`, `DataHash`, `ETag`) | `(InstanceId, WorkflowKey)` | `LIST (WorkflowKey)` | Hızlı (Büyük payload) |
| **`WorkflowHistory`** | Her geçişin audit kaydı. `history: full` modunda geçiş anındaki JSON snapshot (`Data`), `delta` modunda çoğunlukla yalnızca değişiklik (`Patch`, JSON Patch) ve arada snapshot'lar; `none` modunda satır yazılmaz | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Çok Hızlı (En büyük tablo) |
| **`WorkflowJobs`** | Zamanlanmış geçişler, asenkron geçişler, zaman aşımları, bekletilen mesajlar (`Message`) ve arka plan görevleri (`Task`); PostgreSQL'de `FOR UPDATE SKIP LOCKED` ile alınır | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` / Queue | Dinamik (Geçici kuyruk) |
| **`WorkflowIncidents`** | Akış sırasında oluşan hatalar, retry denemeleri ve incident kayıtları | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Düşük/Orta |
| **`WorkflowAgentSessions`** | `agent` / `aiAgent` görevlerinin çok turlu konuşma oturumu durumları (`SessionData` column `jsonb`, `TurnCount`) | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Orta |
| **`WorkflowAgentRuns`** | Her AI ajan çalıştırmasının denetim kaydı (token kullanımı, süre, model, seçilen geçiş) | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Orta/Hızlı |
| **`WorkflowAgentToolCalls`** | AI ajanların çağırdığı araçların (`expression`, `task`, `http`, `mcp`) girdi, sonuç ve sıra kayıtları | `(Id, WorkflowKey)` | `LIST (WorkflowKey)` | Hızlı |

### Neden Tüm Tablolarda Bileşik Anahtar: `(..., WorkflowKey)`?
PostgreSQL kuralları gereği, bir tablo bölümlendiğinde (partitioned table):
1. **Birincil Anahtar (Primary Key)** ve **Tekil İndeksler (Unique Indexes)** mutlaka bölümleme anahtarını (`WorkflowKey`) içermek zorundadır.
2. Tablolar arası **Foreign Key (Yabancı Anahtar)** ilişkileri kurulurken, ana tablo bölümlenmişse alt tablonun yabancı anahtarı da `(InstanceId, WorkflowKey)` şeklinde bileşik olmalıdır.

Bu mimari sayesinde PostgreSQL:
- **Partition-Wise Join:** `WorkflowInstances` ile `WorkflowInstanceData` veya `WorkflowHistory` tablolarını birleştirirken (join), her iki tablonun da aynı flow partition'larını doğrudan eşleştirir. Diğer flow'ların verilerine hiç dokunmaz.
- **Partition Pruning:** Bir sorgu `WHERE "WorkflowKey" = 'order-payment-orchestration'` içerdiğinde, PostgreSQL 8 tablonun tamamında yalnızca sipariş akışının partition'larını tarar.

---

## 2. PostgreSQL DDL Şablonu (Tüm Tablolar)

Aşağıdaki SQL şablonu, 8 çalışma zamanı tablosunun tamamını root partitioned tablo ve `DEFAULT` güvenlik ağı partition'larıyla birlikte tanımlar:

```sql
CREATE SCHEMA IF NOT EXISTS wf;

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. WorkflowInstances (Durum & Yaşam Döngüsü)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowInstances" (
    "Id" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "WorkflowVersion" int NOT NULL,
    "BusinessKey" varchar(256),
    "State" varchar(128) NOT NULL,
    "Status" varchar(16) NOT NULL,
    "PendingTransition" varchar(128),
    "PendingInput" text,
    "StartedBy" varchar(256),
    "LastExecutedBy" varchar(256),
    "Revision" int NOT NULL DEFAULT 0,
    "BusySince" timestamptz,
    "ParentInstanceId" uuid,
    "WaitingForInstanceId" uuid,
    "Error" text,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    "CompletedAt" timestamptz,
    "BackgroundTaskId" uuid,       -- beklenen arka plan görevinin job id'si
    "HistoryPatchCount" int,       -- history: delta – son snapshot'tan beri yazılan patch sayısı
    "AssignedTo" varchar(256),     -- insan görevi üstlenen/atanan kullanıcı(lar) veya rol
    "AssignedAt" timestamptz,      -- görevin üstlenildiği/atandığı zaman
    CONSTRAINT "PK_WorkflowInstances" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowInstances_Default" 
    PARTITION OF wf."WorkflowInstances" DEFAULT;

CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_WorkflowKey_BusinessKey" 
    ON wf."WorkflowInstances" ("WorkflowKey", "BusinessKey");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_WorkflowKey_CreatedAt" 
    ON wf."WorkflowInstances" ("WorkflowKey", "CreatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_Status_BusySince" 
    ON wf."WorkflowInstances" ("Status", "BusySince");

-- ─────────────────────────────────────────────────────────────────────────────
-- 2. WorkflowInstanceData (JSON Veri Yükü)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowInstanceData" (
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "Data" jsonb NOT NULL,
    "DataVersion" int NOT NULL DEFAULT 0,
    "DataHash" varchar(64) NOT NULL,
    "ETag" varchar(64) NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_WorkflowInstanceData" PRIMARY KEY ("InstanceId", "WorkflowKey"),
    CONSTRAINT "FK_WorkflowInstanceData_Instances" FOREIGN KEY ("InstanceId", "WorkflowKey") 
        REFERENCES wf."WorkflowInstances" ("Id", "WorkflowKey") ON DELETE CASCADE
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowInstanceData_Default" 
    PARTITION OF wf."WorkflowInstanceData" DEFAULT;

CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceData_DataHash" 
    ON wf."WorkflowInstanceData" ("DataHash");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceData_WorkflowKey_UpdatedAt" 
    ON wf."WorkflowInstanceData" ("WorkflowKey", "UpdatedAt" DESC);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceData_Data_GIN" 
    ON wf."WorkflowInstanceData" USING gin ("Data" jsonb_path_ops);
-- (Aynı jsonb_path_ops GIN indeksi WorkflowHistory."Data" üzerinde de oluşturulur: IX_WorkflowHistory_Data_GIN)

-- ─────────────────────────────────────────────────────────────────────────────
-- 3. WorkflowHistory (Geçiş & Denetim İzleri)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowHistory" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "TransitionKey" varchar(128) NOT NULL,
    "FromState" varchar(128),
    "ToState" varchar(128) NOT NULL,
    "Trigger" varchar(16) NOT NULL,
    "ExecutedBy" varchar(256),
    "Input" jsonb,
    "DataVersion" int NOT NULL,
    "Data" jsonb,                  -- snapshot (full modda her satır, delta modda yalnızca checkpoint satırları)
    "Tasks" jsonb,
    "StartedAt" timestamptz NOT NULL,
    "FinishedAt" timestamptz NOT NULL,
    "Patch" jsonb,                 -- history: delta – önceki sürüme göre değişiklik (RFC 6902 JSON Patch, örnekler: README "History modes")
    CONSTRAINT "PK_WorkflowHistory" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowHistory_Default" 
    PARTITION OF wf."WorkflowHistory" DEFAULT;

CREATE INDEX IF NOT EXISTS "IX_WorkflowHistory_WorkflowKey_InstanceId" 
    ON wf."WorkflowHistory" ("WorkflowKey", "InstanceId");

-- ─────────────────────────────────────────────────────────────────────────────
-- 4. WorkflowJobs (Zamanlanmış ve Asenkron İşler)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowJobs" (
    "Id" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "Kind" varchar(16) NOT NULL,
    "TransitionKey" varchar(128),
    "State" varchar(128),
    "Payload" jsonb,
    "RequestedBy" varchar(256),
    "DueAt" timestamptz NOT NULL,
    "LockedUntil" timestamptz,
    "Attempts" int NOT NULL DEFAULT 0,
    "LastError" text,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_WorkflowJobs" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowJobs_Default" 
    PARTITION OF wf."WorkflowJobs" DEFAULT;

CREATE INDEX IF NOT EXISTS "IX_WorkflowJobs_DueAt" 
    ON wf."WorkflowJobs" ("DueAt");
CREATE INDEX IF NOT EXISTS "IX_WorkflowJobs_WorkflowKey_DueAt" 
    ON wf."WorkflowJobs" ("WorkflowKey", "DueAt");

-- İşler node'lar arasında böyle alınır: kilitli satır atlanır, kimse kimseyi beklemez.
-- UPDATE wf."WorkflowJobs" AS j SET "LockedUntil" = @lease, "Attempts" = j."Attempts" + 1
-- FROM (SELECT "Id", "WorkflowKey" FROM wf."WorkflowJobs"
--       WHERE "DueAt" <= @now AND ("LockedUntil" IS NULL OR "LockedUntil" < @now)
--       ORDER BY "DueAt" LIMIT 1 FOR UPDATE SKIP LOCKED) AS next
-- WHERE j."Id" = next."Id" AND j."WorkflowKey" = next."WorkflowKey" RETURNING j."Id";
-- Job yazan node NOTIFY middt_wf_jobs_<şema> gönderir; diğer node'lar LISTEN ile hemen uyanır.

-- ─────────────────────────────────────────────────────────────────────────────
-- 5. WorkflowIncidents (Hata & Retry Kayıtları)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowIncidents" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "State" varchar(128),
    "TransitionKey" varchar(128),
    "TaskKey" varchar(128),
    "Error" text NOT NULL,
    "Attempts" int NOT NULL DEFAULT 1,
    "CreatedAt" timestamptz NOT NULL,
    "ResolvedAt" timestamptz,
    CONSTRAINT "PK_WorkflowIncidents" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowIncidents_Default" 
    PARTITION OF wf."WorkflowIncidents" DEFAULT;

CREATE INDEX IF NOT EXISTS "IX_WorkflowIncidents_WorkflowKey_InstanceId" 
    ON wf."WorkflowIncidents" ("WorkflowKey", "InstanceId");

-- ─────────────────────────────────────────────────────────────────────────────
-- 6. WorkflowAgentSessions (AI Ajan Oturum Durumları)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentSessions" (
    "Id" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "AgentName" varchar(128) NOT NULL,
    "TaskKey" varchar(128) NOT NULL,
    "SessionData" jsonb NOT NULL,
    "TurnCount" int NOT NULL DEFAULT 1,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_WorkflowAgentSessions" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowAgentSessions_Default"
    PARTITION OF wf."WorkflowAgentSessions" DEFAULT;

-- ─────────────────────────────────────────────────────────────────────────────
-- 7. WorkflowAgentRuns (AI Ajan Çalıştırma Denetim İzleri)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentRuns" (
    "Id" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "State" varchar(128) NOT NULL,
    "TransitionKey" varchar(128),
    "TaskKey" varchar(128) NOT NULL,
    "AgentName" varchar(128) NOT NULL,
    "Mode" varchar(32) NOT NULL,
    "SelectedTransition" varchar(128),
    "AllowedTransitions" jsonb,
    "ResponseId" varchar(128),
    "FinishReason" varchar(64),
    "InputTokens" bigint,
    "OutputTokens" bigint,
    "TotalTokens" bigint,
    "ToolCallCount" int NOT NULL DEFAULT 0,
    "Output" jsonb,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_WorkflowAgentRuns" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowAgentRuns_Default"
    PARTITION OF wf."WorkflowAgentRuns" DEFAULT;

-- ─────────────────────────────────────────────────────────────────────────────
-- 8. WorkflowAgentToolCalls (AI Araç Çağrıları)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentToolCalls" (
    "Id" uuid NOT NULL,
    "RunId" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "WorkflowKey" varchar(128) NOT NULL,
    "AgentName" varchar(128) NOT NULL,
    "TaskKey" varchar(128) NOT NULL,
    "CallId" varchar(128) NOT NULL,
    "ToolName" varchar(128) NOT NULL,
    "Arguments" jsonb,
    "Result" jsonb,
    "Order" int NOT NULL DEFAULT 1,
    "CreatedAt" timestamptz NOT NULL,
    CONSTRAINT "PK_WorkflowAgentToolCalls" PRIMARY KEY ("Id", "WorkflowKey")
) PARTITION BY LIST ("WorkflowKey");

CREATE TABLE IF NOT EXISTS wf."WorkflowAgentToolCalls_Default"
    PARTITION OF wf."WorkflowAgentToolCalls" DEFAULT;
```

---

## 3. Dedicated Partition Ekleme (Örnek Akış)

Yeni ve yüksek hacimli bir akış (örneğin `order-payment-orchestration`) devreye alındığında, 8 tablo için dedicated partition'lar oluşturulur (`WorkflowPartitionManager` bunu akış tanımlandığında otomatik olarak yapar):

```sql
-- 1. Instances
CREATE TABLE IF NOT EXISTS wf."WorkflowInstances_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowInstances" 
    FOR VALUES IN ('order-payment-orchestration');

-- 2. Data
CREATE TABLE IF NOT EXISTS wf."WorkflowInstanceData_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowInstanceData" 
    FOR VALUES IN ('order-payment-orchestration');

-- 3. History
CREATE TABLE IF NOT EXISTS wf."WorkflowHistory_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowHistory" 
    FOR VALUES IN ('order-payment-orchestration');

-- 4. Jobs
CREATE TABLE IF NOT EXISTS wf."WorkflowJobs_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowJobs" 
    FOR VALUES IN ('order-payment-orchestration');

-- 5. Incidents
CREATE TABLE IF NOT EXISTS wf."WorkflowIncidents_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowIncidents" 
    FOR VALUES IN ('order-payment-orchestration');

-- 6. Agent Sessions
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentSessions_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowAgentSessions" 
    FOR VALUES IN ('order-payment-orchestration');

-- 7. Agent Runs
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentRuns_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowAgentRuns" 
    FOR VALUES IN ('order-payment-orchestration');

-- 8. Agent Tool Calls
CREATE TABLE IF NOT EXISTS wf."WorkflowAgentToolCalls_OrderPaymentOrchestration" 
    PARTITION OF wf."WorkflowAgentToolCalls" 
    FOR VALUES IN ('order-payment-orchestration');
```

> [!NOTE]
> **Otomatik Güvenlik:** Eğer bu SQL çalıştırılmadan önce `order-payment-orchestration` başlatılırsa, kayıtlar otomatik olarak `_Default` partition'larına düşer. Sistem kesintiye uğramaz.

---

## 4. Akışa Özel JSONB İndeksleri

### 4.1 Otomatik indeksleme kuralları (`WorkflowPartitionManager.ExtractIndexedProperties`)

Dedicated partition açıldıktan sonra `WorkflowPartitionManager`, tanımın **yalnızca `dataSchema.properties`** bölümünü okur (`start.input.schema` okunmaz) ve `WorkflowInstanceData_{Suffix}` üzerinde `"Data"` kolonuna tipli B-Tree fonksiyonel indeks (`IX_Part_{Suffix}_{alan}`) oluşturur. Her tipli alan için **değil**, yalnızca şu alanlar için:

- `"x-index": true` olan alanlar, **veya**
- `x-search` (`true` ya da mod metni) olup tipi `enum` / `number` / `integer` / `boolean` olan alanlar (SQL'e itilebilen eşitlik / aralık filtreleri).
- `x-mask` taşıyan alanlar (maskeli alan filtrelenemez) ve `"x-index": false` alanlar **asla** indekslenmez; `x-search` verilmiş düz `string` alan (metin araması, `STRPOS`) da indeks almaz.

İndeks ifadeleri (kolon adı tırnaklı `"Data"`): `integer` / `number` → `((("Data"->>'alan')::numeric))`, `boolean` → `((("Data"->>'alan')::boolean))`, `string` / `enum` → `(("Data"->>'alan')::text)`. SQLite'ta aynı küme `json_extract("Data", '$.alan')` üzerinde `WHERE "WorkflowKey" = '…'` kısmi indeks olarak açılır.

> [!WARNING]
> **Eski indeks temizliği:** Hesaplanan küme oluşturulduktan sonra `IX_Part_{Suffix}_*` ön ekine sahip olup kümede bulunmayan **tüm** indeksler her startup / deploy'da silinir (`DropStaleFlowIndexesAsync`). Bu ön ek yöneticiye ayrılmıştır; elle yazılan indekslere farklı bir ad verin (ör. `IX_Custom_…`).

Örnek – `order-payment-orchestration` için:

```jsonc
"dataSchema": {
  "type": "object",
  "properties": {
    "customerId": { "type": "string", "x-index": true },                        // indeks (x-index)
    "total":      { "type": "number", "x-search": true },                       // indeks (x-search + number)
    "statusCode": { "type": "string", "enum": ["NEW", "PAID"], "x-search": true }, // indeks (x-search + enum)
    "note":       { "type": "string", "x-search": true },                       // indeks YOK (düz metin araması)
    "cardNumber": { "type": "string", "x-mask": { "pattern": "last4", "exceptRoles": ["manager"] } } // indeks YOK
  }
}
```

Bu tanım için yönetici şu SQL'in eşdeğerini çalıştırır (suffix `OrderPaymentOrchestration`):

```sql
-- Müşteri ID araması (x-index: true)
CREATE INDEX IF NOT EXISTS "IX_Part_OrderPaymentOrchestration_customerId" 
ON wf."WorkflowInstanceData_OrderPaymentOrchestration" ((("Data"->>'customerId')::text));

-- Tutar sıralaması ve aralık sorguları (x-search + number)
CREATE INDEX IF NOT EXISTS "IX_Part_OrderPaymentOrchestration_total" 
ON wf."WorkflowInstanceData_OrderPaymentOrchestration" (((("Data"->>'total')::numeric)));

-- Sipariş durum kodu (x-search + enum)
CREATE INDEX IF NOT EXISTS "IX_Part_OrderPaymentOrchestration_statusCode" 
ON wf."WorkflowInstanceData_OrderPaymentOrchestration" ((("Data"->>'statusCode')::text));
```

### 4.2 Elle eklenen bileşik indeksler

Bileşik veya özel indeksler (ör. `(customerId, total)`) EF migration'ı ya da hosted service ile idempotent olarak eklenir. Kolon `"Data"` (tırnaklı) olmalı ve ad `IX_Part_` ile **başlamamalıdır**:

```sql
CREATE INDEX IF NOT EXISTS "IX_Custom_OrderPaymentOrchestration_Customer_Total"
ON wf."WorkflowInstanceData_OrderPaymentOrchestration" (
    ((("Data"->>'customerId')::text)),
    ((("Data"->>'total')::numeric))
);
```

Keyfi JSON kapsama sorguları (`"Data" @> '{"statusCode": "PAID"}'`) için `WorkflowInstanceData` ve `WorkflowHistory` üzerindeki global `jsonb_path_ops` GIN indeksleri (`IX_WorkflowInstanceData_Data_GIN`, `IX_WorkflowHistory_Data_GIN`) zaten tüm partition'larda geçerlidir; trigram (`pg_trgm`) indeksi yoktur.

---

## 5. Operasyonel Avantajlar ve Yaşam Döngüsü Yönetimi

1. **Partition-Wise Join Performansı:**
   `WorkflowInstances` ile `WorkflowInstanceData` join edildiğinde PostgreSQL yalnızca `WorkflowInstances_OrderPaymentOrchestration` ile `WorkflowInstanceData_OrderPaymentOrchestration` tablolarını birleştirir; veri eşleştirmesi doğrudan CPU önbelleğinde tamamlanır.
2. **Sıfır Kesintiyle Arşivleme:**
   Eski sipariş geçmişini arşivlemek için devasa tablolarda milyonlarca satırı `DELETE` edip diski kilitlemek yerine:
   ```sql
   ALTER TABLE wf."WorkflowHistory" DETACH PARTITION wf."WorkflowHistory_OrderPaymentOrchestration";
   ```
   komutuyla emekliye ayrılan akışın partition'ı bir milisaniyede ana tablodan ayrılır ve soğuk depoya taşınır (yöneticinin oluşturduğu partition'lar akış anahtarı başına tektir; yıl bazlı alt bölümleme elle yapılır).
3. **İzole Bakım:**
   `VACUUM (ANALYZE)` veya `REINDEX` işlemleri yoğun çalışan partition'lara ayrı ayrı uygulanabilir.

---

## 6. Çoklu Veritabanı (Multi-Database) Uyumluluğu (PostgreSQL, MS SQL Server, MySQL, Oracle, SQLite)

Declarative Partitioning (`PARTITION BY LIST`) PostgreSQL'e özgü bir performans optimizasyonu olmakla birlikte, Middt Framework iş akışı ve UI motorları **veritabanından bağımsız (database-agnostic)** çalışacak şekilde tasarlanmıştır:

| Veritabanı Sağlayıcısı | Şema (`wf` / `ui`) | JSON Filtreleme & Sıralama (SQL Pushdown) | Kuyruk (`WorkflowJobs`) Tüketimi | Çoklu Pod Senkronizasyonu |
| :--- | :--- | :--- | :--- | :--- |
| **PostgreSQL (`Npgsql`)** | `wf`, `ui` (Declarative Partitioning + `jsonb`) | `"Data"->>'prop'`, `::numeric`, `STRPOS` | `FOR UPDATE SKIP LOCKED` | `LISTEN` / `NOTIFY` + `IWorkflowClusterBus` |
| **MS SQL Server (`SqlServer`)** | `wf`, `ui` (`IF SCHEMA_ID(...) IS NULL EXEC(...)`) | `JSON_VALUE([Data], '$.prop')`, `TRY_CAST`, `CHARINDEX` | `WITH (UPDLOCK, READPAST, ROWLOCK)` + `OUTPUT inserted.[Id]` | `IWorkflowClusterBus` + `UpdatedAt` Watermark |
| **MySQL / MariaDB (`MySql`)** | Şemasız (`ModelSchema = null`) | `JSON_UNQUOTE(JSON_EXTRACT(\`Data\`, '$.prop'))`, `LOCATE` | Atomik `ExecuteUpdateAsync` | `IWorkflowClusterBus` + `UpdatedAt` Watermark |
| **Oracle (`Oracle`)** | Şemasız (`ModelSchema = null`, `NCLOB`, Function-Based Unique Index) | `JSON_VALUE("Data", '$.prop')`, `TO_NUMBER(... DEFAULT NULL ON CONVERSION ERROR)`, `INSTR` | Atomik `ExecuteUpdateAsync` | `IWorkflowClusterBus` + `UpdatedAt` Watermark |
| **SQLite (`Sqlite`)** | Şemasız (`ModelSchema = null`) | `json_extract("Data", '$.prop')`, `INSTR` | Atomik `ExecuteUpdateAsync` | `IWorkflowClusterBus` + `UpdatedAt` Watermark |

Ayrıca bu 5 veritabanı dışında herhangi bir EF Core sağlayıcısı kullanıldığında `WorkflowInstanceDataQuery` otomatik olarak **bellek-içi (in-memory) filtreleme ve sıralama fallback** mekanizmasına geçer.
