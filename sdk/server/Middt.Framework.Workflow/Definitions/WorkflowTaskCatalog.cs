using System.Text.Json.Nodes;
using Middt.Framework.Model.Workflow;

namespace Middt.Framework.Workflow.Definitions;

/// <summary>
/// Metadata catalog of built-in workflow tasks for the visual workflow designer (<c>GET /api/workflows/tasks</c>).
/// </summary>
public static class WorkflowTaskCatalog
{
    public static IReadOnlyList<WorkflowTaskSpecModel> All { get; } = CreateSpecs();

    private static List<WorkflowTaskSpecModel> CreateSpecs() =>
    [
        new()
        {
            Name = "set",
            Title = "Veri Hesapla & Ata (set)",
            Category = "Veri & Anahtar",
            Icon = "calculate",
            Description = "C# ifadeleriyle hesaplama yaparak instance.data içerisine yeni alanlar yazar veya mevcut alanları günceller.",
            Template = new JsonObject
            {
                ["values"] = new JsonObject
                {
                    ["calculatedField"] = "data.amount * 1.20m",
                    ["updatedAt"] = "now",
                },
            },
            Params =
            [
                new()
                {
                    Name = "values",
                    Label = "Atanacak Alanlar ve İfadeler (values)",
                    Type = "json",
                    Required = true,
                    Description = "{ \"hedefAlan\": \"C# İfadesi\" } çiftleri (data.*, input.*, instance.*, user.*, now, Math kullanılabilir).",
                },
            ],
        },
        new()
        {
            Name = "setKey",
            Title = "İş Anahtarı Üret (setKey)",
            Category = "Veri & Anahtar",
            Icon = "vpn_key",
            Description = "Akış kaydına benzersiz bir iş anahtarı (BusinessKey) atar. Idempotent başlatma ve dış referanslama için kullanılır.",
            Template = new JsonObject
            {
                ["key"] = "REF-{{ data.code }}",
                ["ensureUnique"] = true,
            },
            Params =
            [
                new()
                {
                    Name = "key",
                    Label = "Anahtar Şablonu (key)",
                    Type = "string",
                    Required = true,
                    Description = "Kayda atanacak iş anahtarı şablonu (ör. CUST-{{ data.identityNumber }}).",
                },
                new()
                {
                    Name = "ensureUnique",
                    Label = "Çakışmada Otomatik Tekilleştir (ensureUnique)",
                    Type = "boolean",
                    DefaultValue = "true",
                    Description = "Aynı anahtar başka bir kayıtta varsa hata vermek yerine sonuna 6 haneli tekil kod ekler.",
                },
            ],
        },
        new()
        {
            Name = "loadWorkflow",
            Title = "Tekil Akış Verisi Yükle (loadWorkflow)",
            Category = "Akışlar Arası (Cross-Workflow)",
            Icon = "download",
            Description = "Başka bir iş akışı kaydını key, id veya where (Instance Data) koşuluyla bulup verisini, AI ajan çıktılarını ve tarihçesini mevcut akışa yükler.",
            Template = new JsonObject
            {
                ["workflow"] = "customer-onboarding",
                ["where"] = "target.customerNumber == data.customerNumber",
                ["includeAgents"] = true,
                ["includeHistory"] = false,
                ["failIfNotFound"] = false,
                ["into"] = "customer",
            },
            Params =
            [
                new() { Name = "workflow", Label = "Hedef İş Akışı (workflow)", Type = "string", Required = true, Description = "Okunacak iş akışının anahtarı (ör. customer-onboarding)." },
                new() { Name = "key", Label = "İş Anahtarı Filtresi (key)", Type = "string", Description = "Hedef kaydın BusinessKey değeri ({{ data.customerKey }})." },
                new() { Name = "id", Label = "Kayıt GUID Filtresi (id)", Type = "string", Description = "Hedef kaydın benzersiz InstanceId (GUID) değeri." },
                new() { Name = "where", Label = "Instance Data Koşulu (where)", Type = "string", Description = "Hedef akışın verisi (target.*) üzerinde birleşik SQL + C# koşulu (ör. target.identityNumber == data.identityNumber)." },
                new() { Name = "state", Label = "Durum Filtresi (state)", Type = "string", Description = "Yalnızca belirli bir durumdaki kaydı getirir (ör. approved)." },
                new() { Name = "status", Label = "Statü Filtresi (status)", Type = "enum", Options = ["", "Active", "Completed", "Cancelled", "Faulted"], Description = "Yalnızca belirli yaşam döngüsü statüsündeki kaydı getirir." },
                new() { Name = "select", Label = "Alan Seçimi / Dönüşüm (select)", Type = "json", Description = "Alan dizisi [\"companyName\", \"score\"] veya dönüşüm nesnesi { \"yeniAlan\": \"target.limit - target.used\" }." },
                new() { Name = "includeAgents", Label = "AI Ajan Çıktılarını Dahil Et (includeAgents)", Type = "boolean", DefaultValue = "false", Description = "Hedef akışta çalışan AI ajanlarının çıktılarını (agents, agentRuns) getirir." },
                new() { Name = "includeHistory", Label = "Geçiş Tarihçesini Dahil Et (includeHistory)", Type = "boolean", DefaultValue = "false", Description = "Hedef akışın durum geçiş tarihçesini (history) getirir." },
                new() { Name = "failIfNotFound", Label = "Bulunamazsa Hata Ver (failIfNotFound)", Type = "boolean", DefaultValue = "false", Description = "true ise kayıt bulunamadığında hata fırlatır; false ise { found: false } yazar." },
                new() { Name = "into", Label = "Hedef Veri Alanı (into)", Type = "string", Required = true, Description = "Okunan verinin instance.data içinde yazılacağı alan yolu (ör. customer)." },
            ],
        },
        new()
        {
            Name = "queryWorkflow",
            Title = "Çoklu Akış Sorgula & Topla (queryWorkflow)",
            Category = "Akışlar Arası (Cross-Workflow)",
            Icon = "query_stats",
            Description = "Başka bir iş akışının çoklu kayıtlarını birleşik where koşuluyla sorgular, sıralar ve sum/avg/min/max/count toplamalarını hesaplar.",
            Template = new JsonObject
            {
                ["workflow"] = "credit-allocation",
                ["state"] = "approved",
                ["excludeSelf"] = true,
                ["where"] = "target.customerKey == data.customerKey && target.amount >= 50000",
                ["orderBy"] = "amount",
                ["descending"] = true,
                ["take"] = 20,
                ["aggregate"] = new JsonObject
                {
                    ["totalExposure"] = "sum:amount",
                    ["maxLoanAmount"] = "max:amount",
                    ["activeLoanCount"] = "count",
                },
                ["into"] = "loanSummary",
            },
            Params =
            [
                new() { Name = "workflow", Label = "Hedef İş Akışı (workflow)", Type = "string", Required = true, Description = "Sorgulanacak iş akışının anahtarı." },
                new() { Name = "where", Label = "Instance Data Koşulu (where)", Type = "string", Description = "target.<alan> üzerinde birleşik SQL partition + C# filtre ifadesi (ör. target.customerKey == data.customerKey && target.amount > 10000)." },
                new() { Name = "state", Label = "Durum Filtresi (state)", Type = "string", Description = "Belirli bir durumdaki kayıtları filtreler (ör. approved)." },
                new() { Name = "status", Label = "Statü Filtresi (status)", Type = "enum", Options = ["", "Active", "Completed", "Cancelled", "Faulted"], Description = "Kayıt statüsü filtresi." },
                new() { Name = "excludeSelf", Label = "Mevcut Kaydı Hariç Tut (excludeSelf)", Type = "boolean", DefaultValue = "false", Description = "Aynı akış sorgulanırken çalışan mevcut kaydı sonuçlardan ve toplamalardan hariç tutar." },
                new() { Name = "orderBy", Label = "Sıralama Alanı (orderBy)", Type = "string", DefaultValue = "createdAt", Description = "createdAt, updatedAt, key, state, status veya bir veri alanı (ör. amount)." },
                new() { Name = "descending", Label = "Azalan Sıralama (descending)", Type = "boolean", DefaultValue = "true", Description = "true: büyükten küçüğe / yeniden eskiye; false: küçükten büyüğe." },
                new() { Name = "take", Label = "Maksimum Kayıt Sayısı (take)", Type = "number", DefaultValue = "50", Description = "Getirilecek maksimum kayıt adedi (1..500)." },
                new() { Name = "first", Label = "Yalnızca İlk Kaydı Yaz (first)", Type = "boolean", DefaultValue = "false", Description = "true ise liste özeti yerine doğrudan eşleşen ilk kaydın nesnesini into alanına yazar." },
                new() { Name = "select", Label = "Alan Seçimi (select)", Type = "json", Description = "Seçilecek alanlar dizisi veya dönüşüm nesnesi." },
                new() { Name = "aggregate", Label = "Toplama Hesapları (aggregate)", Type = "json", Description = "{ \"toplamRisk\": \"sum:amount\", \"ortalama\": \"avg:amount\", \"adet\": \"count\" } (sum, avg, min, max, count)." },
                new() { Name = "into", Label = "Hedef Veri Alanı (into)", Type = "string", Required = true, Description = "Sorgu ve toplama sonuçlarının yazılacağı alan yolu." },
            ],
        },
        new()
        {
            Name = "updateWorkflow",
            Title = "Başka Akış Verisini Güncelle (updateWorkflow)",
            Category = "Akışlar Arası (Cross-Workflow)",
            Icon = "edit_note",
            Description = "Başka bir iş akışı kaydını (veya all: true ile tüm eşleşenleri) key, id ya da where koşuluyla bulup values, merge veya patch ile günceller.",
            Template = new JsonObject
            {
                ["workflow"] = "customer-onboarding",
                ["where"] = "target.customerNumber == data.customerNumber",
                ["all"] = false,
                ["values"] = new JsonObject
                {
                    ["usedLimit"] = "(target.usedLimit ?? 0) + data.amount",
                    ["lastUpdatedAt"] = "now",
                },
                ["into"] = "updatedCustomer",
            },
            Params =
            [
                new() { Name = "workflow", Label = "Hedef İş Akışı (workflow)", Type = "string", Required = true, Description = "Güncellenecek iş akışının anahtarı." },
                new() { Name = "key", Label = "İş Anahtarı (key)", Type = "string", Description = "Hedef kaydın BusinessKey değeri." },
                new() { Name = "id", Label = "Kayıt GUID (id)", Type = "string", Description = "Hedef kaydın InstanceId (GUID) değeri." },
                new() { Name = "where", Label = "Instance Data Koşulu (where)", Type = "string", Description = "Hedef akışın verisi (target.*) üzerinde filtre koşulu." },
                new() { Name = "state", Label = "Durum Filtresi (state)", Type = "string", Description = "Yalnızca belirli durumdaki kayıtları hedefler." },
                new() { Name = "status", Label = "Statü Filtresi (status)", Type = "enum", Options = ["", "Active", "Completed", "Cancelled", "Faulted"], Description = "Yalnızca belirli statüdeki kayıtları hedefler." },
                new() { Name = "all", Label = "Eşleşen Tüm Kayıtları Güncelle (all)", Type = "boolean", DefaultValue = "false", Description = "true ise where koşulunu sağlayan tüm kayıtları günceller; false ise en güncel tek kaydı günceller." },
                new() { Name = "values", Label = "C# İfadeleriyle Güncelle (values)", Type = "json", Description = "{ \"alan\": \"target.usedLimit + data.amount\" } şeklinde target.* ve data.* kullanan ifadeler." },
                new() { Name = "merge", Label = "JSON Şablon Birleştir (merge)", Type = "json", Description = "Hedef kaydın verisiyle birleştirilecek {{ }} şablonlu JSON nesnesi." },
                new() { Name = "patch", Label = "RFC 6902 JSON Patch (patch)", Type = "json", Description = "[{ \"op\": \"replace\", \"path\": \"/limit\", \"value\": \"{{ data.newLimit }}\" }] dizisi." },
                new() { Name = "validateSchema", Label = "Hedef Şemayı Doğrula (validateSchema)", Type = "boolean", DefaultValue = "false", Description = "Güncelleme sonrası hedef akışın dataSchema kurallarını kontrol eder." },
                new() { Name = "failIfNotFound", Label = "Bulunamazsa Hata Ver (failIfNotFound)", Type = "boolean", DefaultValue = "true", Description = "Kayıt bulunamadığında hata fırlatılıp fırlatılmayacağını belirler." },
                new() { Name = "into", Label = "Sonuç Alanı (into)", Type = "string", Description = "Güncellenen kaydın yeni versiyon ve verisinin yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "triggerWorkflow",
            Title = "Başka Akışta Geçiş/Olay Tetikle (triggerWorkflow)",
            Category = "Akışlar Arası (Cross-Workflow)",
            Icon = "bolt",
            Description = "Başka bir iş akışı kaydını key, id veya where ile bulup doğrudan bir geçiş (transition) çalıştırır veya olay mesajı (event) gönderir.",
            Template = new JsonObject
            {
                ["workflow"] = "order-payment-orchestration",
                ["where"] = "target.orderCode == data.orderCode",
                ["event"] = "PaymentCaptured",
                ["bufferFor"] = "00:05:00",
                ["runAsync"] = false,
                ["all"] = false,
                ["input"] = new JsonObject
                {
                    ["paymentReference"] = "{{ data.paymentId }}",
                },
                ["into"] = "triggeredOrder",
            },
            Params =
            [
                new() { Name = "workflow", Label = "Hedef İş Akışı (workflow)", Type = "string", Required = true, Description = "Tetiklenecek iş akışının anahtarı." },
                new() { Name = "key", Label = "İş Anahtarı (key)", Type = "string", Description = "Hedef kaydın BusinessKey değeri." },
                new() { Name = "id", Label = "Kayıt GUID (id)", Type = "string", Description = "Hedef kaydın InstanceId (GUID) değeri." },
                new() { Name = "where", Label = "Instance Data Koşulu (where)", Type = "string", Description = "Hedef akışın verisi (target.*) üzerinde filtre koşulu." },
                new() { Name = "transition", Label = "Çalıştırılacak Geçiş (transition)", Type = "string", Description = "Hedef akışta doğrudan çalıştırılacak geçiş anahtarı (event ile birlikte kullanılmaz)." },
                new() { Name = "event", Label = "Gönderilecek Olay Mesajı (event)", Type = "string", Description = "Hedef akıştaki TriggerType.Event geçişine gönderilecek olay adı." },
                new() { Name = "bufferFor", Label = "Mesaj Kuyruk Bekleme Süresi (bufferFor)", Type = "string", Description = "Hedef akış henüz ilgili durumda değilse olayın kuyrukta bekleneceği süre (ör. 00:05:00 veya PT5M)." },
                new() { Name = "runAsync", Label = "Arka Planda Kuyruğa Al (runAsync)", Type = "boolean", DefaultValue = "false", Description = "transition modunda geçişi arka plan işi (WorkflowJobs) olarak kuyruğa alır." },
                new() { Name = "all", Label = "Eşleşen Tüm Kayıtları Tetikle (all)", Type = "boolean", DefaultValue = "false", Description = "where ile eşleşen tüm kayıtlarda geçişi/olayı tetikler." },
                new() { Name = "input", Label = "Geçiş/Olay Girdi Verisi (input)", Type = "json", Description = "Hedef geçişe veya olaya gönderilecek JSON girdi nesnesi." },
                new() { Name = "failIfNotFound", Label = "Bulunamazsa Hata Ver (failIfNotFound)", Type = "boolean", DefaultValue = "true", Description = "Hedef kayıt bulunamadığında hata fırlatır." },
                new() { Name = "into", Label = "Sonuç Alanı (into)", Type = "string", Description = "Tetikleme sonrası yeni durumun ve verinin yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "startWorkflow",
            Title = "Alt İş Akışı Başlat (startWorkflow)",
            Category = "Akışlar Arası (Cross-Workflow)",
            Icon = "call_split",
            Description = "Başka bir iş akışında yeni bir kayıt başlatır ve oluşturulan kaydın GUID değerini into alanına yazar.",
            Template = new JsonObject
            {
                ["workflow"] = "order-payment-orchestration",
                ["key"] = "ORD-{{ data.orderCode }}",
                ["data"] = new JsonObject
                {
                    ["orderCode"] = "{{ data.orderCode }}",
                    ["amount"] = "{{ data.amount }}",
                },
                ["into"] = "childInstanceId",
            },
            Params =
            [
                new() { Name = "workflow", Label = "Başlatılacak Akış (workflow)", Type = "string", Required = true, Description = "Yeni kayıt açılacak iş akışı anahtarı." },
                new() { Name = "key", Label = "İş Anahtarı Şablonu (key)", Type = "string", Description = "Yeni kayda atanacak BusinessKey şablonu." },
                new() { Name = "data", Label = "Başlangıç Verisi (data)", Type = "json", Description = "Yeni akışa gönderilecek başlangıç JSON nesnesi." },
                new() { Name = "into", Label = "Oluşan ID Alanı (into)", Type = "string", Description = "Başlatılan kaydın GUID kimliğinin yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "http",
            Title = "HTTP Servis Çağrısı (http)",
            Category = "Entegrasyon & Mesajlaşma",
            Icon = "http",
            Description = "Dış REST API servislerine güvenli HTTP çağrısı yapar (http:// veya https:// ile başlayan tam URL zorunludur).",
            Template = new JsonObject
            {
                ["method"] = "POST",
                ["url"] = "http://mockoon:3000/api/v1/service/check",
                ["headers"] = new JsonObject
                {
                    ["X-Channel"] = "Middt-Workflow",
                },
                ["body"] = new JsonObject
                {
                    ["reference"] = "{{ instance.key }}",
                    ["amount"] = "{{ data.amount }}",
                },
                ["failOnError"] = true,
                ["timeoutSeconds"] = 30,
                ["into"] = "serviceResponse",
            },
            Params =
            [
                new() { Name = "method", Label = "HTTP Metodu (method)", Type = "enum", Required = true, DefaultValue = "POST", Options = ["GET", "POST", "PUT", "PATCH", "DELETE"], Description = "HTTP istek metodu." },
                new() { Name = "url", Label = "Tam URL (url)", Type = "string", Required = true, Description = "http:// veya https:// ile başlayan tam servis adresi (host kısmında {{ }} kullanılamaz)." },
                new() { Name = "queryParams", Label = "Sorgu Parametreleri (queryParams)", Type = "json", Description = "URL sonuna güvenli olarak eklenecek anahtar-değer parametreleri." },
                new() { Name = "headers", Label = "HTTP Başlıkları (headers)", Type = "json", Description = "İsteğe eklenecek HTTP başlıkları." },
                new() { Name = "body", Label = "İstek Gövdesi (body)", Type = "json", Description = "Gönderilecek JSON gövdesi." },
                new() { Name = "acceptedStatusCodes", Label = "Kabul Edilen Durum Kodları (acceptedStatusCodes)", Type = "json", Description = "Başarılı sayılacak HTTP kodları dizisi (ör. [200, 201, 404])." },
                new() { Name = "failOnError", Label = "Hata Kodunda Exception Fırlat (failOnError)", Type = "boolean", DefaultValue = "true", Description = "false ise hata fırlatmadan yanıtı { isSuccess: false, statusCode: ... } olarak into alanına yazar." },
                new() { Name = "timeoutSeconds", Label = "Zaman Aşımı Saniye (timeoutSeconds)", Type = "number", DefaultValue = "30", Description = "İstek zaman aşımı süresi (1..300 sn)." },
                new() { Name = "into", Label = "Yanıt Alanı (into)", Type = "string", Description = "HTTP yanıtının yazılacağı alan yolu." },
            ],
        },
        new()
        {
            Name = "publish",
            Title = "Olay Yayınla (publish)",
            Category = "Entegrasyon & Mesajlaşma",
            Icon = "campaign",
            Description = "Mesaj otobüsü (Rebus) üzerinden uygulama içi veya dağıtık bir olay yayınlar.",
            Template = new JsonObject
            {
                ["type"] = "OrderApproved",
                ["payload"] = new JsonObject
                {
                    ["orderCode"] = "{{ data.orderCode }}",
                    ["amount"] = "{{ data.amount }}",
                },
            },
            Params =
            [
                new() { Name = "type", Label = "Olay Tipi (type)", Type = "string", Required = true, Description = "Yayınlanacak mesaj/olay adı." },
                new() { Name = "payload", Label = "Olay Verisi (payload)", Type = "json", Description = "Gönderilecek JSON mesaj içeriği." },
            ],
        },
        new()
        {
            Name = "rabbitmq",
            Title = "RabbitMQ Mesajı Gönder (rabbitmq)",
            Category = "Entegrasyon & Mesajlaşma",
            Icon = "outbox",
            Description = "Belirtilen RabbitMQ exchange ve routingKey adresine başlıklarıyla birlikte mesaj yayınlar.",
            Template = new JsonObject
            {
                ["exchange"] = "middt.workflow.events",
                ["routingKey"] = "workflow.event.{{ instance.workflow }}",
                ["payload"] = new JsonObject
                {
                    ["key"] = "{{ instance.key }}",
                },
                ["into"] = "rabbitPublishResult",
            },
            Params =
            [
                new() { Name = "routingKey", Label = "Routing Key / Kuyruk (routingKey)", Type = "string", Required = true, Description = "Mesajın yönlendirileceği RabbitMQ anahtarı." },
                new() { Name = "exchange", Label = "Exchange Adı (exchange)", Type = "string", DefaultValue = "middt.workflow.events", Description = "Hedef RabbitMQ exchange adı." },
                new() { Name = "headers", Label = "Mesaj Başlıkları (headers)", Type = "json", Description = "Mesaja eklenecek özel başlıklar." },
                new() { Name = "payload", Label = "Mesaj Gövdesi (payload)", Type = "json", Description = "Gönderilecek JSON mesaj gövdesi." },
                new() { Name = "into", Label = "Onay Sonuç Alanı (into)", Type = "string", Description = "Yayınlama bilgisinin yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "operation",
            Title = "İş Operasyonu Çalıştır (operation)",
            Category = "Entegrasyon & Mesajlaşma",
            Icon = "settings_suggest",
            Description = "Sunucuda kayıtlı bir IWorkflowOperation iş sınıfını parametreleriyle çalıştırır.",
            Template = new JsonObject
            {
                ["operation"] = "calculate-amortization",
                ["input"] = new JsonObject
                {
                    ["amount"] = "{{ data.amount }}",
                },
                ["into"] = "operationResult",
            },
            Params =
            [
                new() { Name = "operation", Label = "Operasyon Adı (operation)", Type = "string", Required = true, Description = "Kayıtlı IWorkflowOperation adı." },
                new() { Name = "input", Label = "Girdi Parametreleri (input)", Type = "json", Description = "Operasyona gönderilecek JSON nesnesi." },
                new() { Name = "into", Label = "Çıktı Alanı (into)", Type = "string", Description = "Operasyon çıktısının yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "fanOut",
            Title = "Paralel Çoklu Görev (fanOut)",
            Category = "Orkestrasyon & Performans",
            Icon = "account_tree",
            Description = "Bir dizi (items) içindeki her eleman için subTask görevini paralel çalıştırır ve sonuçları into dizisinde toplar.",
            Template = new JsonObject
            {
                ["items"] = "new string[] { \"Kontrol-1\", \"Kontrol-2\" }",
                ["maxConcurrency"] = 5,
                ["into"] = "parallelResults",
                ["subTask"] = new JsonObject
                {
                    ["task"] = "set",
                    ["config"] = new JsonObject
                    {
                        ["values"] = new JsonObject
                        {
                            ["checkedItem"] = "item.value",
                        },
                    },
                },
            },
            Params =
            [
                new() { Name = "items", Label = "Dizi İfadesi (items)", Type = "string", Required = true, Description = "Üzerinde dönülecek diziyi üreten C# ifadesi (alt görevde item ve index ile erişilir)." },
                new() { Name = "maxConcurrency", Label = "Maksimum Paralellik (maxConcurrency)", Type = "number", DefaultValue = "5", Description = "Aynı anda çalışacak maksimum alt görev sayısı (1..64)." },
                new() { Name = "into", Label = "Sonuç Dizisi Alanı (into)", Type = "string", Description = "Paralel görev çıktılarının JsonArray olarak yazılacağı alan." },
                new() { Name = "subTask", Label = "Alt Görev Tanımı (subTask)", Type = "json", Required = true, Description = "Her bir eleman için çalıştırılacak görev ({ \"task\": \"...\", \"config\": { ... } })." },
            ],
        },
        new()
        {
            Name = "cache",
            Title = "Önbellek / Cache-Aside (cache)",
            Category = "Orkestrasyon & Performans",
            Icon = "cached",
            Description = "Önce key ile önbelleği kontrol eder; varsa subTask'ı çalıştırmadan into alanına yazar, yoksa subTask'ı çalıştırıp sonucu ttl süresiyle önbelleğe alır.",
            Template = new JsonObject
            {
                ["key"] = "cache:{{ instance.key }}",
                ["ttl"] = "00:15:00",
                ["into"] = "cachedValue",
                ["subTask"] = new JsonObject
                {
                    ["task"] = "set",
                    ["config"] = new JsonObject
                    {
                        ["values"] = new JsonObject
                        {
                            ["cachedValue"] = "data.amount",
                        },
                    },
                },
            },
            Params =
            [
                new() { Name = "key", Label = "Önbellek Anahtarı (key)", Type = "string", Required = true, Description = "Önbellek anahtarı şablonu (ör. fx:{{ data.currency }})." },
                new() { Name = "ttl", Label = "Geçerlilik Süresi (ttl)", Type = "string", DefaultValue = "00:05:00", Description = "Önbellekte tutulma süresi (00:15:00 veya PT15M)." },
                new() { Name = "into", Label = "Hedef Veri Alanı (into)", Type = "string", Required = true, Description = "Önbellekten veya alt görevden gelen değerin yazılacağı alan." },
                new() { Name = "subTask", Label = "Alt Görev Tanımı (subTask)", Type = "json", Required = true, Description = "Önbellekte veri bulunamadığında çalıştırılacak görev." },
            ],
        },
        new()
        {
            Name = "agent",
            Title = "Yapay Zeka Ajanı (agent)",
            Category = "Yapay Zeka & MCP",
            Icon = "smart_toy",
            Description = "Microsoft Agent Framework üzerinde tekil veya çoklu (sequential/concurrent/handoff) AI ajanı çalıştırır ve otomatik geçiş yönlendirmesi yapar.",
            Template = new JsonObject
            {
                ["agent"] = "risk-limit-underwriter",
                ["mode"] = "single",
                ["prompt"] = "Müşteri: {{ data.customerKey }}, Tutar: {{ data.amount }} TL için değerlendirme yap.",
            },
            Params =
            [
                new() { Name = "mode", Label = "Orkestrasyon Modu (mode)", Type = "enum", DefaultValue = "single", Options = ["single", "sequential", "concurrent", "handoff"], Description = "single: tek ajan; sequential: sıralı boru hattı; concurrent: paralel; handoff: dinamik görev devri." },
                new() { Name = "agent", Label = "Ajan Adı (agent)", Type = "string", Description = "mode: single iken çalıştırılacak kayıtlı ajan adı (Agents/*.json)." },
                new() { Name = "agents", Label = "Çoklu Ajan Listesi (agents)", Type = "json", Description = "sequential/concurrent/handoff modlarında çalıştırılacak ajan dizisi." },
                new() { Name = "prompt", Label = "Kullanıcı İstemi (prompt)", Type = "string", Required = true, Description = "Ajana gönderilecek {{ }} şablonlu istem metni." },
                new() { Name = "instructions", Label = "Sistem Talimatları (instructions)", Type = "string", Description = "Kayıtlı ajanın sistem talimatlarını bu adım özelinde ezer." },
                new() { Name = "allowedTransitions", Label = "İzin Verilen Geçişler (allowedTransitions)", Type = "json", Description = "Ajanın seçebileceği geçiş anahtarları listesi (belirtilmezse durumdaki Automatic geçişler otomatik keşfedilir)." },
                new() { Name = "transitionProperty", Label = "Geçiş Karar Alanı (transitionProperty)", Type = "string", DefaultValue = "nextTransition", Description = "Ajan çıktısında geçiş anahtarının okunacağı alan adı." },
                new() { Name = "transitionInto", Label = "Seçilen Geçişi Veriye Yaz (transitionInto)", Type = "string", Description = "Ajanın seçtiği geçiş anahtarının data içinde yazılacağı alan." },
                new() { Name = "tools", Label = "Ek Araçlar (tools)", Type = "json", Description = "Bu adımda ajana açılacak ek araç adları dizisi." },
                new() { Name = "mcpServers", Label = "MCP Sunucuları (mcpServers)", Type = "json", Description = "Bu adımda ajana bağlanacak MCP sunucu adları dizisi." },
                new() { Name = "sessionKey", Label = "Konuşma Hafıza Anahtarı (sessionKey)", Type = "string", Description = "Çok turlu konuşma geçmişini paylaşmak için kullanılan oturum anahtarı." },
                new() { Name = "into", Label = "Çıktıyı Veriye Kopyala (into)", Type = "string", Description = "İsteğe bağlı: Ajan çıktısını instance.agents.<key> dışında instance.data içine de kopyalar." },
            ],
        },
        new()
        {
            Name = "mcp",
            Title = "MCP Sunucu Çağrısı (mcp)",
            Category = "Yapay Zeka & MCP",
            Icon = "hub",
            Description = "Model Context Protocol (MCP) sunucusu üzerinde araç çağırır (callTool), kaynak okur (readResource) veya istem şablonu alır (getPrompt).",
            Template = new JsonObject
            {
                ["server"] = "core-banking-mcp",
                ["action"] = "callTool",
                ["tool"] = "get_customer_exposure",
                ["arguments"] = new JsonObject
                {
                    ["customerKey"] = "{{ data.customerKey }}",
                },
                ["into"] = "mcpResult",
            },
            Params =
            [
                new() { Name = "server", Label = "MCP Sunucu Adı (server)", Type = "string", Required = true, Description = "Kayıtlı MCP sunucu anahtarı." },
                new() { Name = "action", Label = "İşlem Türü (action)", Type = "enum", Required = true, DefaultValue = "callTool", Options = ["callTool", "listTools", "readResource", "listResources", "getPrompt"], Description = "callTool: araç çağır; listTools: araçları listele; readResource: kaynak oku; listResources: kaynakları listele; getPrompt: şablon al." },
                new() { Name = "tool", Label = "Araç Adı (tool)", Type = "string", Description = "action: callTool seçildiğinde çağrılacak MCP aracının adı." },
                new() { Name = "uri", Label = "Kaynak URI (uri)", Type = "string", Description = "action: readResource seçildiğinde okunacak kaynağın URI adresi." },
                new() { Name = "prompt", Label = "İstem Şablonu Adı (prompt)", Type = "string", Description = "action: getPrompt seçildiğinde alınacak şablon adı." },
                new() { Name = "arguments", Label = "Argümanlar (arguments)", Type = "json", Description = "callTool veya getPrompt işlemine gönderilecek parametre nesnesi." },
                new() { Name = "into", Label = "Sonuç Alanı (into)", Type = "string", DefaultValue = "mcpResult", Description = "MCP yanıtının yazılacağı alan yolu." },
                new() { Name = "metaInto", Label = "Üst Veri Alanı (metaInto)", Type = "string", Description = "Çağrı üst verisinin ({ server, action, executedAt }) yazılacağı alan." },
            ],
        },
        new()
        {
            Name = "log",
            Title = "Log Kaydı Yaz (log)",
            Category = "Gözlemlenebilirlik",
            Icon = "terminal",
            Description = "Uygulama loglarına ve OpenTelemetry izlerine yapılandırılmış mesaj yazar.",
            Template = new JsonObject
            {
                ["level"] = "Information",
                ["message"] = "İş akışı ({{ instance.key }}) durumu güncellendi.",
            },
            Params =
            [
                new() { Name = "level", Label = "Log Seviyesi (level)", Type = "enum", DefaultValue = "Information", Options = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"], Description = "Log kayıt seviyesi." },
                new() { Name = "message", Label = "Log Mesajı (message)", Type = "string", Required = true, Description = "Yazılacak {{ }} şablonlu log metni." },
            ],
        },
    ];
}
