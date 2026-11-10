# Best Practice: Satır Bazlı Güvenlik ve Şube Yetkilendirme Rehberi

Bu rehber; bankacılık, sigortacılık veya çok şubeli/kurumsal yapılarda **Satır Bazlı Güvenlik (Row-Level Security - RLS)**, **Kayıt Bazlı Yetkilendirme (Record-Level Authorization)** ve **İş Akışlarında Şube İzolasyonunu** uçtan uca uygulamak için en iyi pratikleri sunar.

---

## 1. Mimari Katmanlar

Kurumsal uygulamalarda güvenlik tek bir noktaya (yalnızca UI veya yalnızca controller) bırakılamaz. Savunma derinliği (**Defense in Depth**) ilkesiyle 4 katmanda ele alınır:

```
┌────────────────────────────────────────────────────────┐
│ 1. Frontend & UI (Server-Driven Layouts & Workflows)    │
│    - Şubeye göre filtrelenmiş dropdown/picker seçimleri │
│    - Yetkisiz butonların gizlenmesi (visibleWhen)       │
├────────────────────────────────────────────────────────┤
│ 2. Workflow Engine (İş Akışı Katmanı)                  │
│    - Transition kuralları: roles + rule                 │
│    - Şube müdürü yetki doğrulaması                      │
├────────────────────────────────────────────────────────┤
│ 3. API & Veri Sorgusu (WorkflowInstanceDataQuery)       │
│    - instanceAccess: owner + supervisorRoles (satır)    │
│    - x-roles / x-mask projeksiyonu (alan)               │
│    - Yetkisiz getbyid → kayıt yok gibi davranır         │
├────────────────────────────────────────────────────────┤
│ 4. Veritabanı & EF Core (kendi tablolarınız için)       │
│    - HasQueryFilter (SQL seviyesinde otomatik WHERE)    │
│    - İstemci veya geliştirici unutsun, veri sızamaz     │
└────────────────────────────────────────────────────────┘
```

---

## 2. Kullanıcı Şube Bağlamı (`ICurrentBranchContext`)

Kullanıcının şube bilgisi kimlik doğrulama token'ından (OIDC / JWT Claim) okunur. Tüm katmanların erişebileceği hafif ve test edilebilir bir servis tanımlanır:

```csharp
public interface ICurrentBranchContext
{
    int? BranchId { get; }
    string? UserId { get; }
    bool IsHeadquarters { get; }
}

public sealed class CurrentBranchContext(IHttpContextAccessor httpContextAccessor) : ICurrentBranchContext
{
    private ClaimsPrincipal? User => httpContextAccessor.HttpContext?.User;

    public int? BranchId =>
        int.TryParse(User?.FindFirst("branch_id")?.Value, out var id) ? id : null;

    public string? UserId =>
        User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User?.FindFirst(ClaimTypes.Sid)?.Value;

    public bool IsHeadquarters =>
        User?.IsInRole("Headquarters") == true || User?.IsInRole("Auditor") == true;
}
```

---

## 3. Katman 1: Veritabanı Seviyesi (EF Core Global Query Filters)

Geliştirici yeni bir sorgu yazsa, `Include` ile ilişkili tabloya atlasa dahi başka şubenin verisinin sorguya karışmasını engellemek için `DbContext.OnModelCreating` içerisinde **Global Query Filter** tanımlanır:

```csharp
public class BankingDbContext(
    DbContextOptions<BankingDbContext> options,
    ICurrentBranchContext branchContext) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Müşteri Tablosu
        modelBuilder.Entity<Customer>().HasQueryFilter(c =>
            branchContext.IsHeadquarters || 
            (branchContext.BranchId.HasValue && c.BranchId == branchContext.BranchId.Value));

        // 2. Müşteri Hesapları Tablosu
        modelBuilder.Entity<Account>().HasQueryFilter(a =>
            branchContext.IsHeadquarters || 
            (branchContext.BranchId.HasValue && a.BranchId == branchContext.BranchId.Value));
    }
}
```

> **Önemli Kural:** Global filtreler Genel Müdürlük / Teftiş kullanıcıları için `branchContext.IsHeadquarters` ile şeffaf şekilde bypass edilebilir.

---

## 4. Katman 2: API & Veri Sorgusu Seviyesi

Middt'te iş verisi ilişkisel tablolarda değil, iş akışı örneklerinin JSON `Data` belgelerinde yaşar; framework'te genel amaçlı CRUD controller'ı (`ApplyAccessFilter` / `AuthorizeAsync` kancaları) **yoktur**. Satır ve alan bazlı güvenlik iş akışı tanımında bildirilir ve motor tarafından uygulanır:

### A. Satır Bazlı Erişim: `instanceAccess` (`owner` + `supervisorRoles`)

```jsonc
{
  "key": "customer-onboarding",
  "roles": [ "branch-user", "branch-manager" ],              // tanıma kim erişebilir
  "instanceAccess": { "mode": "owner", "supervisorRoles": [ "branch-manager", "Headquarters" ] },
  "dataSchema": { ... }
}
```

- `mode: "owner"`: Bir kullanıcı yalnızca **kendi başlattığı** (`StartedBy`) örnekleri görür ve sürer; `supervisorRoles` içindeki bir role sahip olanlar (ve sistem) tümünü görür. Varsayılan `mode: "all"` tanım `roles` kontrolünden geçen herkese tüm örnekleri açar.
- Kural `WorkflowEngine.EnsureCanAccess` (detay, geçiş, SSE, inbox) ve [`WorkflowInstanceDataQuery`](../sdk/server/Middt.Framework.Workflow/Runtime/WorkflowInstanceDataQuery.cs) (`getall` / `getitems` / `getbyid`, `x-lookup` kaynakları) tarafından **sayfalama ve `count` öncesinde** uygulanır; her örnek için o örneğin saklı tanım sürümündeki kural kullanılır.
- `getbyid` ile başka kullanıcının örneği istendiğinde kayıt bulunamamış gibi davranılır (ID enumeration'a bilgi sızdırmaz). Cross-workflow görevler (`loadWorkflow`, `queryWorkflow`, `updateWorkflow`, `triggerWorkflow`) kullanıcı bağlamında çalışırken hedef akışın `roles` + `instanceAccess` kuralına tabidir; `access: "system"` bunu bilinçli olarak atlar.
- Şube gibi `StartedBy` dışı bir ölçüte göre satır filtresi istiyorsanız motor bunu sağlamaz (bkz. §7-B); kaynağın sunucuda doğrulanmış claim olması gerekir.

### B. Alan Bazlı Erişim: `x-roles` ve `x-mask` (`JsonSchemaValidator.ProjectData` / `ProjectSchema`)

```jsonc
"dataSchema": {
  "type": "object",
  "properties": {
    "identityNumber": { "type": "string", "x-mask": { "pattern": "last4", "exceptRoles": [ "branch-manager", "auditor" ] } },
    "email":          { "type": "string", "x-mask": { "pattern": "email" } },
    "internalNote":   { "type": "string", "x-roles": [ "branch-manager" ] }
  }
}
```

- `x-roles`: Rolü olmayan çağıran için alan hem veriden (`ProjectData`) hem de istemciye giden şemadan (`ProjectSchema`, kolon/form üretimi) çıkarılır.
- `x-mask`: Yalnızca nesne biçimi geçerlidir – `pattern` `"last4"` | `"email"` | `"***"`, isteğe bağlı `exceptRoles`. Dize biçimi (`"x-mask": "middle"`) doğrulayıcı tarafından reddedilir. Maskeli alanlar indekslenmez ve SQL'e itilen filtre olarak kullanılamaz.
- Projeksiyon motorun tüm okuma yollarında (örnek detayı, liste, inbox, `WorkflowInstanceDataQuery`, `loadWorkflow`, statik HTML görünümü) uygulanır. **`WorkflowInstanceData` tablosunu doğrudan okuyan özel bir endpoint bu projeksiyonu atlar**; özel okuma yolları `WorkflowEngine` / `WorkflowInstanceDataQuery` üzerinden geçmeli ya da `JsonSchemaValidator.ProjectData(data, definition.DataSchema, user)` çağırmalıdır.
- UI tarafında ek kayıt gerekmez: `AddMiddtUi` (Ui.Server) yalnızca layout mağazası ve uç noktaları kurar; maskeleme sunucuda, iş akışı `dataSchema`'sından yapılır.

### C. Kütle Atama (Mass-Assignment) Koruması

İstemciden gelen gövde yalnızca `input.schema` ile bildirilen alanları taşıyabilir (`additionalProperties: false` tutun); şube gibi yetki belirleyen alanlar formdan değil, geçiş üzerindeki `set` ile sunucu bağlamından yazılır:

```jsonc
"start": {
  "key": "start",
  "target": "draft",
  "input": {
    "schema": { "type": "object", "additionalProperties": false, "properties": { "fullName": { "type": "string" } } },
    "set": { "branchId": "user.Id" }   // ya da sunucuda doğrulanmış bir claim'den türetilen değer
  }
}
```

`user` ifade kapsamında `Id`, `Name`, `Roles`, `IsInRole(r)` bulunur; özel claim'ler (ör. `branch_id`) için kimlik sağlayıcıda claim'i bir role ya da kullanıcı kimliğine bağlayın veya kendi `WorkflowUser` dönüşümünüzü kullanın. İstemciden gelen `input.header.*` / `input.query.*` değerleri (tüm başlıklar ve sorgu parametreleri otomatik olarak kapsama girer) **yetki ölçütü olarak asla** kullanılmamalıdır.

---

## 5. Katman 3: İş Akışlarında Şubeye Özel Dinamik Veri Okuma

> **Vaka:** Müşteri ekleme veya hesap açılış akışında, açılır kutudan (dropdown) **yalnızca o şubenin personelleri** seçilebilmelidir.

### Yöntem 1: Akışlar Arası Dinamik Sorgulama (`queryWorkflow`)
Akışın ilgili adımına (`onEntry`) doğrudan bir `queryWorkflow` görevi eklenerek şube ID'si ve aktiflik durumu PostgreSQL partition indeksleri üzerinden sorgulanabilir:

```json
{
  "task": "queryWorkflow",
  "config": {
    "workflow": "branch-staff",
    "state": "active",
    "where": "target.branchId == (input.branchId ?? data.branchId)",
    "select": [ "id", "key", "fullName", "branchId" ],
    "into": "availableStaffList"
  }
}
```

> 💡 **Not:** Parametre yalnızca akış verisinden (`data.branchId`) değil; kullanıcının formda gönderdiği istek gövdesinden (`input.branchId`), HTTP Header'dan veya QueryString'den de gelebilir. Aşağıda bu kaynakların nasıl yönetileceği detaylandırılmıştır.

> ⚠️ **Uyarı:** Aynı sorguyu birden fazla iş akışında tekrar tekrar yazmak (kredi, müşteri ekleme, hesap açma) **kod tekrarına (DRY ihlali)** ve şema değiştiğinde tüm JSON dosyalarını güncelleme yüküne yol açar. Bu nedenle aşağıdaki **SubFlow (Alt Akış)** deseni tercih edilmelidir.

---

### Yöntem 2: Tekrar Kullanılabilir SubFlow (Alt Akış) Deseni *(Önerilen Yaklaşım)*

Sorguyu her akışa gömmek yerine, veri çekme mantığı **bağımsız ve tekrar kullanılabilir bir Alt Akış (SubFlow)** haline getirilir:

#### 1. Reusable Alt Akış Tanımı (`get-branch-staff.json`)

Bu demo akışı parametreli `queryWorkflow` sorgusunu ve alt akış veri eşlemesini gösterir; **şube yetkilendirmesi yapmaz**. Aşağıdaki
`branchId` değerini istemciden alıp doğrudan sorguda kullanmak gerçek personel verisi için yeterli değildir. Üretim
uygulamasında yetkili şube sunucuda doğrulanmış kimlik bilgisinden gelmeli ve sorgudan önce kontrol edilmelidir.
Bu akış parametre olarak `branchId` alır, doğrular, sorguyu çalıştırır ve sonucu döner:

```json
{
  "key": "get-branch-staff",
  "version": 1,
  "title": "Şube Personel Listesini Getir",
  "start": { "key": "start", "target": "validate" },
  "states": [
    {
      "key": "validate",
      "title": "Girdi Doğrulama",
      "transitions": [
        { "key": "valid", "target": "query", "trigger": "Automatic", "rule": "data.branchId != null && data.branchId > 0" },
        { "key": "invalid", "target": "failed", "trigger": "Automatic", "default": true }
      ]
    },
    {
      "key": "query",
      "title": "Personel Sorgusu",
      "onEntry": [
        {
          "task": "queryWorkflow",
          "config": {
            "workflow": "branch-staff",
            "state": "active",
            "where": "target.branchId == data.branchId",
            "select": [ "id", "key", "fullName" ],
            "into": "items"
          }
        }
      ],
      "transitions": [
        { "key": "done", "target": "completed", "trigger": "Automatic", "default": true }
      ]
    },
    { "key": "completed", "title": "Başarılı", "type": "Finish", "subType": "Success" },
    { "key": "failed", "title": "Geçersiz Şube", "type": "Finish", "subType": "Error" }
  ]
}
```

#### 2. Ebeveyn Akıştan Alt Akışın Çağrılması
Herhangi bir ana iş akışında (müşteri açılışı, transfer onayı, vb.) tek satır SQL yazmadan bu alt akış bir adım olarak bağlanır:

```json
{
  "key": "load-staff",
  "title": "Şube Personelini Yükle",
  "type": "SubFlow",
  "subFlow": {
    "workflow": "get-branch-staff",
    "input": {
      "branchId": "data.branchId"
    },
    "into": "availableStaff"
  },
  "transitions": [
    {
      "key": "staff-loaded",
      "target": "assign-staff-step",
      "trigger": "Automatic",
      "rule": "child.subType == \"Success\""
    },
    {
      "key": "load-failed",
      "target": "staff-load-error",
      "trigger": "Automatic",
      "default": true
    }
  ]
}
```

#### 3. Ekranda Verinin Kullanılması (`assign-staff-step`)
Alt akış tamamlandığında veriler ebeveyn akışın `data.availableStaff` alanına yazılır. Formdaki açılır kutu doğrudan bu veriye bağlanır:

```json
{
  "key": "assign-staff-step",
  "title": "Müşteri Temsilcisi Atama",
  "view": {
    "type": "Form",
    "schema": {
      "type": "object",
      "properties": {
        "selectedStaffId": {
          "type": "integer",
          "title": "Atanan Müşteri Temsilcisi",
          "enumSource": "data.availableStaff.items"
        }
      },
      "required": [ "selectedStaffId" ]
    }
  },
  "transitions": [
    { "key": "submit-staff", "target": "next-step", "trigger": "Manual" }
  ]
}
```

#### Neden SubFlow Deseni?
1. **Sıfır Bekleme Süresi (Senkron Yürütme):** Alt akışın tüm adımları `Automatic` olduğunda, Middt motoru alt akışı başlatır, sorguyu çalıştırır ve ebeveyne geri döner. Ebeveyn akış tek bir tick içinde `assign-staff-step` ekranına geçer; kullanıcı gecikme hissetmez.
2. **Kapsülleme (Encapsulation):** Tablo adı, kolon adı veya filtre kuralı (`active = 1`, `store_id`) değiştiğinde yalnızca `get-branch-staff.json` dosyası güncellenir; onlarca iş akışına dokunulmaz.
3. **Versiyonlama Desteği:** İleride `version: 2` çıkartılabilir ve eski çalışan akışlar bozulmadan yeni akışlara entegre edilebilir (`"version": 2`).
4. **Kolay Zenginleştirme:** Gelecekte alt akışın içine sorgu öncesinde Redis önbellek kontrolü (`cache`) veya harici İK servisinden LDAP kontrolü (`http`) eklemek mümkündür.

---

### Parametre Kaynakları: Header, QueryString, Request Body ve JWT Claim'lerinin Çözümlenmesi

Gerçek hayat senaryolarında `branchId` gibi filtre veya iş parametreleri yalnızca akışın o anki durum verisinde (`data.branchId`) bulunmayabilir. Bilgi şu kaynaklardan biri üzerinden sisteme gelebilir:

1. **Request Body / Form Girdisi (`input`):** Kullanıcının ekrandaki formu doldurup "İlerle" butonuna bastığı an veya REST API üzerinden `POST /transitions/{transition}` gövdesinde gönderilen JSON verisi.
2. **HTTP Header (`X-Branch-Id`) veya JWT Claim:** Kimlik doğrulama token'ı (OIDC/OAuth2) içindeki kullanıcının bağlı olduğu şube (`branch_id`) veya kurumsal ağ geçidinden (API Gateway/BFF) gelen özel header.
3. **QueryString (`?branchId=101`):** Sayfa rotasında (URL) veya API çağrısında parametre olarak iletilen değer.
4. **Mevcut Akış Durumu (`data`):** Akışın önceki bir adımında kullanıcı tarafından seçilmiş veya veritabanından çekilip hafızada saklanan durum verisi.

Middt Framework bu farklı kaynakları şu stratejilerle destekler:

#### A. Otomatik HTTP Bağlamı (`input.header`, `input.query`, `input.body`)
Middt Framework'te REST API (`WorkflowEndpoints`) üzerinden gelen HTTP isteklerinde, framework gelen tüm HTTP bağlamını **otomatik olarak** `input` zarfına yerleştirir:

- **`input.header.*`**: İstek başlıkları (örneğin `X-Branch-Id: 101` gönderildiğinde `input.header.branchId`, `input.header.xBranchId` veya `input.header["X-Branch-Id"]`) ve otomatik üretilen izleme/denetim alanları (`input.header.correlationId`, `input.header.traceId`, `input.header.spanId`, `input.header.clientIp`, `input.header.userAgent`).
- **`input.query.*`**: URL sorgu parametreleri (örneğin `?branchId=101` gönderildiğinde `input.query.branchId`).
- **`input.body.*`**: İstek gövdesi (JSON body içerisindeki alanlar).
- **`input.*`**: Geriye dönük uyumluluk ve doğrudan erişim için gövdedeki alanlar (`body`) doğrudan `input` kökünde de mevcuttur (örneğin `input.branchId`).

> ⚡ **Otomatik ve Tip Güvenli:** Sayfa rotasında `?branchId=101` veya başlıkta `X-Branch-Id: 101` olduğunda **tek bir satır C# eşleme kodu yazmanıza gerek kalmaz**. Framework header ve query değerlerini otomatik olarak parse eder (sayı, boolean veya metin olarak) ve akış adımlarına aktarır.

#### B. İfade Düzeyinde Güvenli Fallback Zinciri (`??` Operatörü)
Middt'in taşınabilir ifade motoru (`ExpressionEvaluator`) **null-coalescing (`??`)** operatörünü destekler. Header ve query nesneleri her zaman güvenli boş nesne olarak sağlandığından `NullReferenceException` oluşmaz. İstediğiniz öncelik sırasına göre zincirleme yapabilirsiniz:

```json
{
  "task": "queryWorkflow",
  "config": {
    "workflow": "branch-staff",
    "state": "active",
    "where": "target.branchId == (input.header.branchId ?? input.query.branchId ?? input.body.branchId ?? data.branchId)",
    "into": "availableStaffList"
  }
}
```

Alt akış çağrısında (`subFlow.input`) da aynı ifade doğrudan kullanılabilir:
```json
"subFlow": {
  "workflow": "get-branch-staff",
  "input": {
    "branchId": "input.header.branchId ?? input.query.branchId ?? input.body.branchId ?? data.branchId"
  },
  "into": "availableStaff"
}
```
*Açıklama: HTTP başlığında şube varsa o kullanılır; yoksa URL query'deki şube; yoksa formdan girilen; o da yoksa akışın hafızasındaki şube devreye girer.*

#### C. Özel API Controller veya BFF Endpoint'lerinde Özel Müdahale (Opsiyonel)
Standart iş akışı uç noktaları (`/api/workflows/...`) yukarıdaki aktarımı otomatik olarak yaparken; eğer özel bir Controller üzerinden akış tetiklemek ve JWT token'daki `branch_id` gibi claim'leri zorunlu kılmak isterseniz:

```csharp
// Özel API Controller veya Minimal API Endpoint
app.MapPost("/api/branch-operations/{id}/assign-staff", async (
    Guid id,
    [FromHeader(Name = "X-Branch-Id")] long? headerBranchId,
    [FromQuery] long? queryBranchId,
    [FromBody] JsonObject input,
    WorkflowEngine engine,
    ClaimsPrincipal principal) =>
{
    // Güvenlik Öncelik Sıralaması:
    // 1. JWT Claim (Kullanıcının token'ındaki yetkili şubesi - en güvenli / manipüle edilemez)
    // 2. Framework'ün otomatik input.header veya input.query çözümü
    var branchId = principal.FindFirst("branch_id")?.Value
                   ?? headerBranchId?.ToString()
                   ?? queryBranchId?.ToString();

    if (branchId != null && !input.ContainsKey("branchId"))
    {
        input["branchId"] = long.Parse(branchId);
    }

    return await engine.ExecuteTransitionAsync("branch-operation", id, "submit", input, WorkflowUser.From(principal));
});
```

> 🔒 **Güvenlik Kuralı:** Eğer `branchId` kullanıcının görmeye yetkili olduğu veriyi sınırlıyorsa (Row-Level Security), istemciden gelen query string veya request body değerine **asla körü körüne güvenilmemelidir**. Token'daki yetkili şube claim'i (`user.Claims`) ile istemcinin talep ettiği şube uyuşmuyorsa istek `403 Forbidden` ile reddedilmelidir.

#### C. Önyüzden (Angular / Blazor) QueryString ve Context Aktarımı
Eğer kullanıcı sayfaya `?branchId=101` linki ile gelmişse veya aktif şubesi UI oturumunda saklanıyorsa:

- **Angular (`@middt/angular`):**
  ```typescript
  // Rota query parametresinden veya SessionService'ten okunur
  const branchId = this.route.snapshot.queryParamMap.get('branchId') ?? this.session.currentBranchId;

  // Geçiş tetiklenirken input'a eklenir
  this.workflowClient.executeTransition(workflow, instanceId, 'submit-store', {
    branchId: Number(branchId)
  });
  ```

- **Blazor (`Middt.Framework.Blazor`):**
  ```csharp
  [SupplyParameterFromQuery]
  public long? BranchId { get; set; }

  // Geçiş çalıştırılırken aktarılır
  await WorkflowClient.ExecuteTransitionAsync(Workflow, instance.Id, "submit-store", new
  {
      branchId = BranchId ?? UserSession.CurrentBranchId
  });
  ```

#### D. Dış Servis Çağrılarında Header ve Query Aktarımı (`http` Task)
İş akışının bir adımı harici bir REST API çağrısı yapıyorsa (`HttpTask`), URL Query ve Header değerleri doğrudan JSON tanımı üzerinden `{{ ... }}` şablonlarıyla beslenebilir:

```json
{
  "task": "http",
  "config": {
    "method": "GET",
    "url": "https://api.internal/v1/staffs",
    "queryParams": {
      "branchId": "{{ input.branchId ?? data.branchId }}"
    },
    "headers": {
      "X-Branch-Id": "{{ input.branchId ?? data.branchId }}",
      "Authorization": "Bearer {{ data.accessToken }}"
    },
    "into": "availableStaff"
  }
}
```

---

### Yöntem 3: Dinamik Lookup (`x-lookup` + Controller Satır Filtresi)
Form şemasında `x-lookup` kullanıldığında:
```json
"staffId": {
  "type": "integer",
  "title": "Personel Seçiniz",
  "x-lookup": {
    "entity": "Staff",
    "valueField": "staffId",
    "textField": "name"
  }
}
```
Önyüz otomatik olarak `/api/v1/Staff/getall` (ya da sayfalı `getitems`) çağrısı yapar. Bu uç noktalar `WorkflowInstanceDataQuery` üzerinden `staff` iş akışının örneklerini döndürür; liste, akışın `roles` + `instanceAccess` kuralıyla (ör. `owner` modunda yalnızca kullanıcının başlattığı kayıtlar) ve `x-roles` / `x-mask` projeksiyonuyla sınırlanır. Şube bazlı bir daraltma için ya `instanceAccess` ile ifade edilebilen bir sahiplik modeli kurun ya da lookup'ı §5'teki `queryWorkflow` / SubFlow deseniyle sunucuda doldurun; istemci tarafı `filter` değerleri güvenlik sınırı değildir.

---

## 6. Katman 4: İş Akışlarında Şubeye Özel Onay Yetkisi

> **Vaka:** Müşteri 750.000 TL tutarında yüksek bir EFT yaptı. Bu transferin onayını **yalnızca işlemi yapan şubenin müdürü** verebilmelidir. Başka bir şubenin müdürü veya gişe görevlisi bu onayı veremez.

### A. İş Akışı Tanımında `roles` + `rule` Korunması

İş akışı JSON tanımında onay geçişine (transition) hem rol hem de şube eşleşme kuralı yazılır:

```json
{
  "key": "approve-high-value-eft",
  "title": "Yüksek Tutarlı EFT Onayla",
  "target": "eft-processed",
  "trigger": "Manual",
  "roles": [ "branch-manager" ],
  "rule": "user.isInRole('branch-manager') && user.branchId == data.branchId"
}
```

#### Bu Kuralın Sağladığı Güvenlik:
1. **Ekran Seviyesi:** Kadıköy Şube Müdürü (`user.branchId == 101`) Kadıköy'de açılan EFT'yi (`data.branchId == 101`) incelediğinde onay butonunu görür. Beşiktaş Şube Müdürü (`user.branchId == 102`) aynı ekrana baksa dahi **onay butonu ekranda görünmez**.
2. **API Seviyesi:** Beşiktaş Şube Müdürü API üzerinden doğrudan tetiklemeye kalksa dahi (`POST /api/workflows/eft-approval/instances/{id}/transitions/approve-high-value-eft`), `WorkflowEngine` transition kuralını doğrular ve isteği **409 Conflict / 403 Forbidden** ile reddeder.

### B. Tutar Limitine Göre Akıllı Yönlendirme (Automatic Transitions)
Limit aşımlarında Genel Müdürlük ya da Şube Müdürü ayrımı otomatik adımlarla dallandırılabilir:

```json
{
  "key": "route-by-amount",
  "title": "Onay Seviyesi Belirleme",
  "transitions": [
    {
      "key": "to-branch-mgr",
      "target": "pending-branch-manager",
      "trigger": "Automatic",
      "rule": "data.amount <= 1000000"
    },
    {
      "key": "to-hq-approval",
      "target": "pending-headquarters",
      "trigger": "Automatic",
      "rule": "data.amount > 1000000",
      "default": true
    }
  ]
}
```

---

## 7. Katman 5: Onay Havuzu ve Görev Listesi (Worklist / Inbox)

Şube müdürü sisteme girdiğinde tüm bankadaki binlerce işlemi değil, sadece kendi yetkisinde onay bekleyen kayıtları görmelidir:

### A. Birleşik Görev Kutusu (`GET /api/workflows/inbox`)

`WorkflowEngine.ListInboxAsync` (`GET /api/workflows/inbox`) tüm iş akışlarındaki aktif ve `subType: "Human"` durumundaki kayıtları tarar; her kaydın kendi tanım sürümündeki (`WorkflowVersion`) `roles` ve `instanceAccess` kurallarını ve durumdaki manuel geçişlerin `CanExecute` koşullarını (`roles`, `denyRoles`, `$previousUser` 4-göz ilkesi ve geçiş `rule` ifadesi) değerlendirir. Yalnızca çağrıyı yapan kullanıcının o anda tetikleyebileceği en az bir manuel geçişi bulunan kayıtlar (`AvailableTransitions`) ve kullanıcının rolüne göre projeksiyonlanmış (`JsonSchemaValidator.ProjectData`: `x-roles`, `x-mask`) veri özeti döndürülür.

### B. Liste Sorgularında SQL Seviyesinde Şube Filtreleme

`WorkflowEngine.ListAsync` iş akışı, durum, iş anahtarı ve tanım sürümünün `roles` / `instanceAccess` kurallarını
uygular; **şube filtresi sağlamaz**. Bu nedenle `ListAsync` sonucunu bellekte `Data["branchId"]` ile daraltmak doğru
bir onay havuzu oluşturmaz: yetkisiz kayıtlar sayfalama ve `Count` hesabına girmiş olur.

Şube havuzu için motora ya da uygulamanın listeleme katmanına **SQL düzeyinde yetkilendirilmiş** bir şube filtresi
ekleyin. Şube kimliği, istemci parametresinden değil sunucuda doğrulanmış token/oturum bilgisinden gelmeli; filtre
sayfalama ve `Count` öncesinde uygulanmalıdır. Sorgu, her örneğin saklı tanım sürümüne ait `roles` ve
`instanceAccess` koşullarını da korumalıdır. Şube kimliği JSON verisindeyse sağlayıcının JSON sorgu desteğini veya
iş akışı güncellemesiyle tutarlı biçimde güncellenen ayrı bir indeks kolonunu kullanın. Genel Müdürlük istisnasını
sunucudaki rolle belirleyin. Böyle bir genişletme olmadan yerleşik liste endpoint'ini şube onay havuzu olarak
kullanmayın.

---

## 8. Best Practice Kontrol Listesi (Checklist)

Yeni bir ekran veya iş akışı geliştirirken bu adımları mutlaka doğrulayın:

- [ ] **Şube Bilgisi Token'da Var mı?** Kullanıcının `branch_id` bilgisi login anında JWT/Identity claims içerisine yazıldı mı?
- [ ] **EF Core Seviyesinde Global Filtre Var mı?** Kendi ilişkisel tablolarınız varsa entity `HasQueryFilter` ile şubeye bağlandı mı?
- [ ] **`instanceAccess` Tanımlandı mı?** Kullanıcıya ait kayıtlar taşıyan akışlarda `{ "mode": "owner", "supervisorRoles": [...] }` ve tanım `roles` yazıldı mı; eski ve yeni tanım sürümlerinde liste/detay davranışı kontrol edildi mi?
- [ ] **Alan Projeksiyonu Yapıldı mı?** Hassas alanlar `x-mask` (nesne biçimi) / `x-roles` ile işaretlendi mi; özel okuma yolları `WorkflowEngine` / `WorkflowInstanceDataQuery` üzerinden ya da `JsonSchemaValidator.ProjectData` ile geçiyor mu?
- [ ] **İstemci Sahte Şube Kodu Gönderebilir mi?** `input.schema` `additionalProperties: false` mı; yetki belirleyen alanlar `input`, `input.header` veya `input.query` yerine `set` ile sunucu bağlamından (`user`) yazılıyor mu?
- [ ] **İş Akışında Şube Kuralı Var mı?** Şube onay geçişlerinde `user.branchId == data.branchId` kontrolü yazıldı mı?
- [ ] **Lookuplar İzolasyona Uygun mu?** Personel/hesap seçim kutuları sadece ilgili şubenin kayıtlarını çekiyor mu?
