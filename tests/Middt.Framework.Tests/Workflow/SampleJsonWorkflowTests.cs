using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Middt.Framework.Agents;
using Middt.Framework.Model.Enumerations;
using Middt.Framework.Model.Workflow;
using Middt.Framework.Ui.Model;
using Middt.Framework.Ui.Rendering;
using Middt.Framework.Ui.Server;
using Middt.Framework.Ui.Server.Rendering;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Runtime;
using Middt.Framework.Workflow.Tasks;

namespace Middt.Framework.Tests.Workflow;

/// <summary>
/// Comprehensive integration tests for all 8 JSON-backed demo workflows (<c>demo/Middt.Demo.Workflows/Workflows</c>) and
/// <see cref="WorkflowInstanceDataQuery"/> operating purely on <c>WorkflowDbContext</c> JSON documents without relational domain tables.
/// </summary>
public sealed class SampleJsonWorkflowTests
{
    private static readonly WorkflowUser Owner = WorkflowTestHost.User("owner@test.com");
    private static readonly WorkflowUser Alice = WorkflowTestHost.User("alice@test.com", "manager");
    private static readonly WorkflowUser Bob = WorkflowTestHost.User("bob@test.com", "manager", "idp-admin");
    private static readonly ClaimsPrincipal OwnerPrincipal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "owner@test.com"), new Claim(ClaimTypes.Name, "owner@test.com")],
        "test"));
    private static readonly ClaimsPrincipal StrangerPrincipal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "stranger@test.com"), new Claim(ClaimTypes.Name, "stranger@test.com")],
        "test"));
    private static readonly ClaimsPrincipal ManagerPrincipal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "alice@test.com"), new Claim(ClaimTypes.Name, "alice@test.com"), new Claim(ClaimTypes.Role, "manager")],
        "test"));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Middt.Framework.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static string[] SampleWorkflowFiles()
    {
        var path = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Workflows");
        return Directory.GetFiles(path, "*.json").Select(File.ReadAllText).ToArray();
    }

    private static Task<WorkflowTestHost> CreateHostAsync(
        FakeHttpHandler? httpHandler = null,
        FakePublisher? publisher = null,
        FakeTimeProvider? time = null) =>
        WorkflowTestHost.StartAsync(
            definitions: SampleWorkflowFiles(),
            services: s =>
            {
                s.AddMiddtAgents(
                    configure: a => a.AddAgentsFromDirectory(Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Agents")));

                if (publisher is not null)
                {
                    s.AddSingleton<IWorkflowMessagePublisher>(publisher);
                }

                if (httpHandler is not null)
                {
                    s.AddHttpClient(HttpTask.DefaultClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => httpHandler);
                }
            },
            time: time);

    [Fact]
    public async Task Customer_onboarding_supports_auto_approval_manual_kyc_self_note_income_update_rejection_and_unique_setKey()
    {
        await using var host = await CreateHostAsync();

        // 1. Starting customer-onboarding twice with identical default identityNumber ("12345678901") disambiguates businessKey via ensureUnique
        var firstDefault = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Ahmet Yılmaz",
                identityNumber = "12345678901",
                email = "musteri@ornek.com",
                phone = "05321234567",
                segment = "Bireysel",
                monthlyIncome = 65000,
            },
            user: Owner);

        firstDefault.State.Should().Be("active");
        firstDefault.Key.Should().Be("CUST-12345678901");

        var secondDefault = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Ahmet Yılmaz",
                identityNumber = "12345678901",
                email = "musteri@ornek.com",
                phone = "05321234567",
                segment = "Bireysel",
                monthlyIncome = 65000,
            },
            user: Owner);

        secondDefault.State.Should().Be("active");
        secondDefault.Key.Should().StartWith("CUST-12345678901-");

        // 2. Close account transition on firstDefault -> 'closed' (Cancelled)
        var closed = await host.TransitionAsync(firstDefault.Id, "close-account", new { closeReason = "Müşteri talebi ile kapatıldı." }, user: Alice);
        closed.State.Should().Be("closed");
        closed.Status.Should().Be(WorkflowInstanceStatus.Cancelled);

        // 3. Corporate customer requires taxOffice via requiredWhen ("ui.segment == \"Kurumsal\"")
        var missingTaxOffice = () => host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Anadolu Lojistik A.Ş.",
                identityNumber = "98765432109",
                email = "finans@anadolulojistik.com",
                phone = "02124440123",
                segment = "Kurumsal",
                monthlyIncome = 450000,
            },
            user: Owner);
        await missingTaxOffice.Should().ThrowAsync<WorkflowValidationException>();

        // Corporate customer goes to manual 'kyc-review', supports $self note, approves to 'active', updates income back to 'kyc-review', and rejects
        var corporate = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Anadolu Lojistik A.Ş.",
                identityNumber = "98765432109",
                email = "finans@anadolulojistik.com",
                phone = "02124440123",
                segment = "Kurumsal",
                taxOffice = "Büyük Mükellefler V.D.",
                monthlyIncome = 450000,
            },
            user: Owner);

        corporate.State.Should().Be("kyc-review");
        corporate.Key.Should().Be("CUST-98765432109");

        // $self transition: add-kyc-note
        corporate = await host.TransitionAsync(corporate.Id, "add-kyc-note", new { lastReviewNote = "Ticaret sicil gazetesi doğrulandı." }, user: Owner);
        corporate.State.Should().Be("kyc-review");
        corporate.Data["lastReviewNote"]!.GetValue<string>().Should().Be("Ticaret sicil gazetesi doğrulandı.");

        // Manager approves KYC -> 'active'
        corporate = await host.TransitionAsync(corporate.Id, "approve-kyc", new { approvalNote = "MASAK uygun" }, user: Alice);
        corporate.State.Should().Be("active");

        // Non-manager cannot modify disabled taxOffice when data.segment == "Kurumsal"
        var modifyDisabledTaxOffice = () => host.TransitionAsync(
            corporate.Id,
            "update-income",
            new { monthlyIncome = 500000, segment = "Kurumsal", taxOffice = "Yetkisiz Vergi Dairesi" },
            user: Owner);
        await modifyDisabledTaxOffice.Should().ThrowAsync<WorkflowValidationException>();

        // Hidden field (taxOffice when ui.segment == "Bireysel") is stripped even if invalid and does not overwrite persisted data
        var bireyselWithHiddenInvalidTaxOffice = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Mehmet Demir",
                identityNumber = "11122233344",
                email = "mehmet@ornek.com",
                phone = "05321112233",
                segment = "Bireysel",
                taxOffice = "X",
                monthlyIncome = 70000,
            },
            user: Owner);
        bireyselWithHiddenInvalidTaxOffice.Data.ContainsKey("taxOffice").Should().BeFalse();

        // Update income & segment from 'active' -> re-runs 'kyc-scoring' without failing setKey and lands in 'kyc-review'
        corporate = await host.TransitionAsync(corporate.Id, "update-income", new { monthlyIncome = 15000, segment = "KOBİ" }, user: Owner);
        corporate.State.Should().Be("kyc-review");
        corporate.Key.Should().Be("CUST-98765432109");

        // Manager rejects KYC -> 'rejected'
        corporate = await host.TransitionAsync(corporate.Id, "reject-kyc", new { reason = "Risk politikası gereği reddedildi" }, user: Alice);
        corporate.State.Should().Be("rejected");
        corporate.Status.Should().Be(WorkflowInstanceStatus.Completed);
    }

    [Fact]
    public async Task Credit_allocation_works_both_when_customer_is_missing_and_when_customer_exists_with_subflow_and_4_eyes()
    {
        await using var host = await CreateHostAsync();

        // 1. Start credit-allocation BEFORE any customer-onboarding exists (regression test for null customer reference!)
        var withoutCustomer = await host.StartInstanceAsync(
            "credit-allocation",
            new
            {
                customerKey = "CUST-NONEXISTENT",
                amount = 350000,
                termMonths = 24,
                collateralAmount = 500000,
                iban = "TR330006100519786457841326",
                requireUnderwriting = false,
            },
            user: Bob);

        withoutCustomer.Status.Should().Be(WorkflowInstanceStatus.Active);
        withoutCustomer.State.Should().Be("stage1-approval");
        withoutCustomer.Data["customerName"]!.GetValue<string>().Should().Be("CUST-NONEXISTENT");
        withoutCustomer.Data["ltvRatio"]!.GetValue<decimal>().Should().Be(70m);

        // Complete via approve-stage1 + executive-override (single-user demo flow with idp-admin role)
        withoutCustomer = await host.TransitionAsync(withoutCustomer.Id, "approve-stage1", new { stage1Comment = "Onay" }, user: Bob);
        withoutCustomer.State.Should().Be("stage2-approval");
        withoutCustomer = await host.TransitionAsync(withoutCustomer.Id, "executive-override", new { overrideReason = "Tek kullanıcı demo onayı" }, user: Bob);
        withoutCustomer.State.Should().Be("disbursed");
        withoutCustomer.Status.Should().Be(WorkflowInstanceStatus.Completed);

        // 2. Now create a customer-onboarding instance and verify WorkflowInstanceDataQuery projections + masking
        var customer = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Zeynep Kaya",
                identityNumber = "12345678901",
                email = "zeynep@kaya.com",
                phone = "05329876543",
                segment = "Bireysel",
                monthlyIncome = 65000,
            },
            user: Owner);

        customer.State.Should().Be("active");
        customer.Key.Should().Be("CUST-12345678901");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var dataQuery = scope.ServiceProvider.GetRequiredService<WorkflowInstanceDataQuery>();

            var ownerView = await dataQuery.GetAllAsync("CustomerOnboarding", OwnerPrincipal, TestContext.Current.CancellationToken);
            ownerView.Result.Should().Be(ResultEnum.Success);
            ownerView.Data.Should().ContainSingle();
            var row = ownerView.Data![0];
            row["key"]!.GetValue<string>().Should().Be("CUST-12345678901");
            row["fullName"]!.GetValue<string>().Should().Be("Zeynep Kaya");
            row["identityNumber"]!.GetValue<string>().Should().Be("*******8901");
            row.ContainsKey("internalRiskNote").Should().BeFalse();

            var strangerView = await dataQuery.GetAllAsync("CustomerOnboarding", StrangerPrincipal, TestContext.Current.CancellationToken);
            strangerView.Data.Should().BeEmpty();

            var managerView = await dataQuery.GetAllAsync("CustomerOnboarding", ManagerPrincipal, TestContext.Current.CancellationToken);
            managerView.Data.Should().ContainSingle();
            managerView.Data![0]["identityNumber"]!.GetValue<string>().Should().Be("12345678901");
            managerView.Data[0].ContainsKey("internalRiskNote").Should().BeTrue();
        }

        // 3. Start credit-allocation referencing CUST-12345678901 with requireUnderwriting=true and amount=750000
        var credit = await host.StartInstanceAsync(
            "credit-allocation",
            new
            {
                customerKey = "CUST-12345678901",
                amount = 750000,
                termMonths = 36,
                collateralAmount = 1000000,
                iban = "TR330006100519786457841326",
                requireUnderwriting = true,
            },
            user: Alice);

        credit.State.Should().Be("risk-subflow");
        credit.ActiveSubFlow.Should().NotBeNull();
        credit.ActiveSubFlow!.Workflow.Should().Be("risk-limit-eval");
        credit.ActiveSubFlow.State.Should().Be("manual-underwriting");

        // Forward 'approve-underwriting' directly through the parent credit-allocation instance
        credit = await host.TransitionAsync(credit.Id, "approve-underwriting", new { underwriterNote = "Limit uygun" }, user: Alice);
        credit.State.Should().Be("stage1-approval");
        credit.Data["customerName"]!.GetValue<string>().Should().Be("Zeynep Kaya");
        credit.Data["ltvRatio"]!.GetValue<decimal>().Should().Be(75m);

        // Trigger $self transition 'update-collateral' (reenter: true) -> recalculates ltvRatio in onEntry
        credit = await host.TransitionAsync(credit.Id, "update-collateral", new { collateralAmount = 1500000 }, user: Alice);
        credit.State.Should().Be("stage1-approval");
        credit.Data["ltvRatio"]!.GetValue<decimal>().Should().Be(50m);

        // Alice approves stage 1 -> moves to stage2-approval, then returns to stage 1, then approves stage 1 again
        credit = await host.TransitionAsync(credit.Id, "approve-stage1", new { stage1Comment = "Şube onayı verildi" }, user: Alice);
        credit.State.Should().Be("stage2-approval");
        credit = await host.TransitionAsync(credit.Id, "return-to-stage1", user: Alice);
        credit.State.Should().Be("stage1-approval");
        credit = await host.TransitionAsync(credit.Id, "approve-stage1", new { stage1Comment = "Tekrar onaylandı" }, user: Alice);
        credit.State.Should().Be("stage2-approval");

        // 4-Eyes check: Alice ($previousUser) cannot see or trigger 'approve-stage2-four-eyes', but Bob can
        credit.AvailableTransitions.Select(t => t.Key).Should().NotContain("approve-stage2-four-eyes");
        credit = await host.TransitionAsync(credit.Id, "approve-stage2-four-eyes", new { stage2Comment = "Komite 4-göz onayı" }, user: Bob);
        credit.State.Should().Be("disbursed");

        // 4. Over-limit credit (> 5,000,000) is automatically rejected by the risk-limit-eval subflow
        var overLimit = await host.StartInstanceAsync(
            "credit-allocation",
            new
            {
                customerKey = "CUST-12345678901",
                amount = 6000000,
                termMonths = 24,
                collateralAmount = 8000000,
                iban = "TR330006100519786457841326",
                requireUnderwriting = false,
            },
            user: Alice);

        overLimit.State.Should().Be("rejected");
        overLimit.Status.Should().Be(WorkflowInstanceStatus.Completed);
    }

    [Fact]
    public async Task Order_payment_orchestration_runs_fanOut_http_gateway_cache_publish_and_error_fallback()
    {
        var httpHandler = new FakeHttpHandler
        {
            StatusCode = HttpStatusCode.OK,
            ResponseBody = """{"status":"Approved","verified":true,"referenceCode":"GW-TEST9999"}""",
        };
        var publisher = new FakePublisher();
        var time = new FakeTimeProvider(WorkflowTestHost.StartTime)
        {
            AutoAdvanceAmount = TimeSpan.FromSeconds(2),
        };

        await using var host = await CreateHostAsync(httpHandler, publisher, time);

        // 1. Standard order (< 50,000) executes fanOut (3 child workflows), HTTP charge, cache, publish, and completes automatically
        var normalOrder = await host.StartInstanceAsync(
            "order-payment-orchestration",
            new
            {
                orderCode = "ORD-2026-1001",
                customerName = "Ahmet Yılmaz",
                amount = 18500,
                paymentMethod = "Kredi Kartı",
            },
            user: Alice);

        normalOrder.State.Should().Be("completed");
        normalOrder.Status.Should().Be(WorkflowInstanceStatus.Completed);
        normalOrder.Key.Should().Be("ORD-ORD-2026-1001");
        normalOrder.Data["paymentReceipt"]!.GetValue<string>().Should().Be("GW-TEST9999");
        normalOrder.Data["parallelChecks"]!.AsArray().Should().HaveCount(3);
        publisher.Published.Should().ContainSingle(p => p.Type == "OrderPaymentCaptured");

        // 2. High-value order (>= 50,000) pauses in 'manual-verification' and completes after manager confirmation
        var highValueOrder = await host.StartInstanceAsync(
            "order-payment-orchestration",
            new
            {
                orderCode = "ORD-2026-1002",
                customerName = "Anadolu Lojistik A.Ş.",
                amount = 65000,
                paymentMethod = "Kurumsal Açık Hesap",
            },
            user: Alice);

        highValueOrder.State.Should().Be("manual-verification");
        highValueOrder = await host.TransitionAsync(highValueOrder.Id, "confirm-order", new { financeNote = "Kurumsal limit onaylandı" }, user: Alice);
        highValueOrder.State.Should().Be("completed");
        highValueOrder.Status.Should().Be(WorkflowInstanceStatus.Completed);

        // 3. Gateway 503 failure triggers smart retry and redirects via 'gateway-fallback' to 'manual-verification', then cancels
        httpHandler.StatusCode = HttpStatusCode.ServiceUnavailable;
        var fallbackOrder = await host.StartInstanceAsync(
            "order-payment-orchestration",
            new
            {
                orderCode = "ORD-2026-1003",
                customerName = "Marmara Ticaret",
                amount = 12000,
                paymentMethod = "Kredi Kartı",
            },
            user: Alice);

        fallbackOrder.State.Should().Be("manual-verification");
        var incidents = await host.IncidentsAsync(fallbackOrder.Id);
        incidents.Should().NotBeEmpty();

        fallbackOrder = await host.TransitionAsync(fallbackOrder.Id, "cancel-order", new { reason = "Ödeme geçidi yanıt vermedi" }, user: Alice);
        fallbackOrder.State.Should().Be("cancelled");
        fallbackOrder.Status.Should().Be(WorkflowInstanceStatus.Cancelled);
    }

    [Fact]
    public async Task Invoice_collection_takes_payment_messages_reconciles_in_the_background_and_keeps_delta_history()
    {
        var httpHandler = new FakeHttpHandler
        {
            StatusCode = HttpStatusCode.OK,
            ResponseBody = """{"status":"Approved","referenceCode":"GW-LEDGER01"}""",
        };
        await using var host = await CreateHostAsync(httpHandler);
        async Task<WorkflowMessageResult> PayAsync(string key, decimal amount, TimeSpan bufferFor = default) =>
            await host.WithEngineAsync(e => e.DeliverMessageAsync(new WorkflowMessage
            {
                Name = "payment.received",
                Workflow = "invoice-collection",
                Key = key,
                Data = new JsonObject { ["amount"] = amount, ["reference"] = "EFT-" + amount.ToString(CultureInfo.InvariantCulture) },
                BufferFor = bufferFor,
            }, Owner, TestContext.Current.CancellationToken));

        // 1. A partial payment stays in awaiting-payment ($self event transition chosen by its rule).
        var invoice = await host.StartInstanceAsync("invoice-collection",
            new { invoiceNo = "FTR-2026-0001", customerName = "Anadolu Lojistik A.Ş.", total = 12500, description = new string('x', 300) }, user: Owner);
        invoice.State.Should().Be("awaiting-payment");
        invoice.Key.Should().Be("INV-FTR-2026-0001");

        var partial = await PayAsync("INV-FTR-2026-0001", 5000);
        partial.Instance!.State.Should().Be("awaiting-payment");
        partial.Instance.Data["partialPayment"]!.AsArray().Should().ContainSingle();
        partial.Instance.Data["partialPayment"]![0]!["amount"]!.GetValue<decimal>().Should().Be(5000);

        // 2. The due date passes (scheduled transition); a late remaining payment then starts the reconciliation (overpayment is rejected).
        await host.RunJobsAsync(TimeSpan.FromDays(7));
        (await host.GetAsync(invoice.Id)).State.Should().Be("overdue");

        var overpaidEvent = () => PayAsync("INV-FTR-2026-0001", 12500);
        await overpaidEvent.Should().ThrowAsync<WorkflowConflictException>("paying 12500 when remainingAmount is 7500 exceeds the remaining balance");

        var paid = await PayAsync("INV-FTR-2026-0001", 7500);
        paid.Instance!.State.Should().Be("reconciling");
        paid.Instance.Status.Should().Be(WorkflowInstanceStatus.Active, "the ledger call runs in the background, not inside the lock");
        httpHandler.Requests.Should().BeEmpty();

        // 3. The background job posts to the ledger, then the remaining onEntry task and the automatic transition run.
        await host.RunJobsAsync();
        var done = await host.GetAsync(invoice.Id);
        done.State.Should().Be("paid");
        done.Data["ledgerResponse"]!.ToJsonString().Should().Contain("GW-LEDGER01");
        done.Data["reconciledAt"].Should().NotBeNull();
        httpHandler.Requests.Should().ContainSingle().Which.Uri.AbsolutePath.Should().Be("/api/v1/InvoiceCollection/ping");

        var history = await host.HistoryAsync(invoice.Id);
        history.Select(h => (h.TransitionKey, h.Trigger)).Should().Equal(
            ("start", "Start"), ("payment-received", "Event"), ("mark-overdue", "Scheduled"),
            ("late-payment-received", "Event"), ("late-payment-completed", "Automatic"), ("$background", "Background"), ("reconciled", "Automatic"));

        // 4. history: delta – snapshots for the first and the final row, patches in between; every version replays.
        var rows = await host.WithDbAsync(db => db.History.AsNoTracking().Where(h => h.InstanceId == invoice.Id).OrderBy(h => h.Id).ToListAsync(TestContext.Current.CancellationToken));
        rows.First().Data.Should().NotBeNull();
        rows.Last().Data.Should().NotBeNull();
        rows.Skip(1).SkipLast(1).Should().OnlyContain(r => r.Data == null && r.Patch != null);
        var replay = await host.WithEngineAsync(e => e.HistoryDataAsync(invoice.Id, WorkflowUser.System, TestContext.Current.CancellationToken));
        replay[^1].Data.ToJsonString().Should().Be(done.Data.ToJsonString());

        // 5. A payment for an invoice that is not waiting yet can be buffered; it is delivered when the state takes it.
        var second = await host.StartInstanceAsync("invoice-collection", new { invoiceNo = "FTR-2026-0002", customerName = "Deniz Ticaret", total = 900 }, user: Owner);
        await host.SetStatusAsync(second.Id, WorkflowInstanceStatus.Busy, WorkflowTestHost.StartTime.UtcDateTime);
        (await PayAsync("INV-FTR-2026-0002", 900, TimeSpan.FromHours(1))).Outcome.Should().Be(WorkflowMessageOutcome.Buffered);
        await host.SetStatusAsync(second.Id, WorkflowInstanceStatus.Active);
        await host.RunJobsAsync(TimeSpan.FromSeconds(30));
        await host.RunJobsAsync();
        (await host.GetAsync(second.Id)).State.Should().Be("paid");
    }

    [Fact]
    public async Task Invoice_collection_can_be_driven_manually_without_a_message_source()
    {
        var httpHandler = new FakeHttpHandler { StatusCode = HttpStatusCode.OK, ResponseBody = """{"status":"Approved","referenceCode":"GW-LEDGER02"}""" };
        await using var host = await CreateHostAsync(httpHandler);
        var invoice = await host.StartInstanceAsync("invoice-collection",
            new { invoiceNo = "FTR-2026-0101", customerName = "Deniz Ticaret", total = 12500, description = new string('x', 300) }, user: Alice);

        // The billing contact is edited with a JSON Patch on data.billingContact (input.format: jsonPatch).
        var contactTransition = (await host.GetAsync(invoice.Id, Alice)).AvailableTransitions.Single(t => t.Key == "update-billing-contact");
        contactTransition.InputFormat.Should().Be("jsonPatch");
        await host.TransitionAsync(invoice.Id, "update-billing-contact", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""[ { "op": "add", "path": "/email", "value": "finans@deniz.com" }, { "op": "add", "path": "/phone", "value": "02125550000" } ]"""),
        }, Alice);
        var contactUpdated = await host.TransitionAsync(invoice.Id, "update-billing-contact", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""[ { "op": "test", "path": "/phone", "value": "02125550000" }, { "op": "replace", "path": "/phone", "value": "02125551111" } ]"""),
        }, Alice);
        contactUpdated.Data["billingContact"]!.ToJsonString().Should().Be("""{"email":"finans@deniz.com","phone":"02125551111"}""");
        contactUpdated.Data["contactUpdatedBy"]!.GetValue<string>().Should().Be("alice@test.com");

        // Without a message source the same steps are buttons for a manager; delete-payment and patch-payment are hidden before any payment exists.
        (await host.GetAsync(invoice.Id, Alice)).AvailableTransitions.Select(t => t.Key)
            .Should().Contain(["record-payment", "cancel-invoice"]).And.NotContain(["payment-received", "delete-payment", "patch-payment"]);
        var afterFirstPartial = await host.TransitionAsync(invoice.Id, "record-payment", new { amount = 5000, reference = "EFT-0101" }, Alice);
        afterFirstPartial.State.Should().Be("awaiting-payment");
        afterFirstPartial.Data["paidAmount"]!.GetValue<decimal>().Should().Be(5000m);
        afterFirstPartial.Data["remainingAmount"]!.GetValue<decimal>().Should().Be(7500m);

        // A mistakenly entered partial payment can be deleted via 'delete-payment' (with dynamic enum options from partialPayment.reference).
        // Even if two payments share the same reference ("EFT-0101"), every payment row appears distinctly in the dropdown.
        var afterDuplicateRefPartial = await host.TransitionAsync(invoice.Id, "record-payment", new { amount = 2000, reference = "EFT-0101" }, Alice);
        var afterWrongPartial = await host.TransitionAsync(invoice.Id, "record-payment", new { amount = 3000, reference = "EFT-WRONG" }, Alice);
        afterWrongPartial.Data["paidAmount"]!.GetValue<decimal>().Should().Be(10000m);
        afterWrongPartial.Data["remainingAmount"]!.GetValue<decimal>().Should().Be(2500m);
        var deleteTransition = afterWrongPartial.AvailableTransitions.Single(t => t.Key == "delete-payment");
        deleteTransition.InputSchema!["properties"]!["reference"]!["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>()).Should().Equal(
                "#1 - 5000 ₺ (EFT-0101)",
                "#2 - 2000 ₺ (EFT-0101)",
                "#3 - 3000 ₺ (EFT-WRONG)");
        deleteTransition.InputSchema["properties"]!["reference"]!["default"]!.GetValue<string>().Should().Be("#3 - 3000 ₺ (EFT-WRONG)");

        var deleteNonExistent = () => host.TransitionAsync(invoice.Id, "delete-payment", new { reference = "EFT-NOPE" }, Alice);
        await deleteNonExistent.Should().ThrowAsync<WorkflowConflictException>();

        // Delete the 2nd payment (#2 - 2000 ₺) by its dropdown label, then delete the 3rd payment by its plain reference ("EFT-WRONG").
        var afterDeleteSecond = await host.TransitionAsync(invoice.Id, "delete-payment", new { reference = "#2 - 2000 ₺ (EFT-0101)", reason = "Mükerrer kayıt silindi" }, Alice);
        afterDeleteSecond.Data["paidAmount"]!.GetValue<decimal>().Should().Be(8000m);
        afterDeleteSecond.Data["partialPayment"]!.AsArray().Should().HaveCount(2);

        var afterDeleteWrong = await host.TransitionAsync(invoice.Id, "delete-payment", new { reference = "EFT-WRONG", reason = "Hatalı tutar girildi" }, Alice);
        afterDeleteWrong.State.Should().Be("awaiting-payment");
        afterDeleteWrong.Data["paidAmount"]!.GetValue<decimal>().Should().Be(5000m);
        afterDeleteWrong.Data["remainingAmount"]!.GetValue<decimal>().Should().Be(7500m);
        afterDeleteWrong.Data["payment"]!["reference"]!.GetValue<string>().Should().Be("EFT-0101");
        afterDeleteWrong.Data["partialPayment"]!.AsArray().Should().ContainSingle();
        afterDeleteWrong.Data["deletedPayment"]!.AsArray().Should().HaveCount(2);
        afterDeleteWrong.Data["deletedPayment"]![0]!["amount"]!.GetValue<decimal>().Should().Be(2000m);
        afterDeleteWrong.Data["deletedPayment"]![1]!["reference"]!.GetValue<string>().Should().Be("EFT-WRONG");
        afterDeleteWrong.Data["deletedPayment"]![1]!["amount"]!.GetValue<decimal>().Should().Be(3000m);
        afterDeleteWrong.Data["deletedPayment"]![1]!["deletedBy"]!.GetValue<string>().Should().Be("alice@test.com");

        // Also test 'patch-payment' (input.format: jsonPatch on the partialPayment array):
        // Add another payment, then remove it via RFC 6902 JSON Patch (test + remove), and verify failed test / overpay rule return 409.
        var beforePatch = await host.TransitionAsync(invoice.Id, "record-payment", new { amount = 1500, reference = "EFT-PATCH-DEL" }, Alice);
        beforePatch.Data["paidAmount"]!.GetValue<decimal>().Should().Be(6500m);
        beforePatch.Data["partialPayment"]!.AsArray().Should().HaveCount(2);

        var patchTestMismatch = () => host.TransitionAsync(invoice.Id, "patch-payment", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""[ { "op": "test", "path": "/1/reference", "value": "YANLIS-REF" }, { "op": "remove", "path": "/1" } ]"""),
        }, Alice);
        await patchTestMismatch.Should().ThrowAsync<WorkflowConflictException>();

        var patchOverpay = () => host.TransitionAsync(invoice.Id, "patch-payment", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""[ { "op": "replace", "path": "/0/amount", "value": 50000 } ]"""),
        }, Alice);
        await patchOverpay.Should().ThrowAsync<WorkflowConflictException>();

        var afterPatchRemove = await host.TransitionAsync(invoice.Id, "patch-payment", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""[ { "op": "test", "path": "/1/reference", "value": "EFT-PATCH-DEL" }, { "op": "remove", "path": "/1" } ]"""),
        }, Alice);
        afterPatchRemove.Data["paidAmount"]!.GetValue<decimal>().Should().Be(5000m);
        afterPatchRemove.Data["remainingAmount"]!.GetValue<decimal>().Should().Be(7500m);
        afterPatchRemove.Data["partialPayment"]!.AsArray().Should().ContainSingle();
        afterPatchRemove.Data["deletedPayment"]!.AsArray().Should().HaveCount(3);
        afterPatchRemove.Data["deletedPayment"]![2]!["reference"]!.GetValue<string>().Should().Be("EFT-PATCH-DEL");
        afterPatchRemove.Data["deletedPayment"]![2]!["amount"]!.GetValue<decimal>().Should().Be(1500m);
        afterPatchRemove.Data["deletedPayment"]![2]!["reason"]!.GetValue<string>().Should().Be("JSON Patch ile silindi.");
        afterPatchRemove.Data["deletedPayment"]![2]!["deletedBy"]!.GetValue<string>().Should().Be("alice@test.com");

        // Overpaying (e.g., 12500 or 8000 when remainingAmount is 7500) is rejected with WorkflowConflictException.
        var overpayFull = () => host.TransitionAsync(invoice.Id, "record-payment", new { amount = 12500, reference = "EFT-OVER" }, Alice);
        await overpayFull.Should().ThrowAsync<WorkflowConflictException>();
        var overpayPartial = () => host.TransitionAsync(invoice.Id, "record-payment", new { amount = 8000, reference = "EFT-OVER2" }, Alice);
        await overpayPartial.Should().ThrowAsync<WorkflowConflictException>();

        var reconciling = await host.TransitionAsync(invoice.Id, "record-payment", new { amount = 7500, reference = "EFT-0102" }, Alice);
        reconciling.State.Should().Be("reconciling");
        reconciling.Status.Should().Be(WorkflowInstanceStatus.Active);
        reconciling.Data["paidAmount"]!.GetValue<decimal>().Should().Be(12500m);
        reconciling.Data["remainingAmount"]!.GetValue<decimal>().Should().Be(0m);
        reconciling.Data["partialPayment"]!.AsArray().Select(p => p!["amount"]!.GetValue<decimal>()).Should().Equal(5000m, 7500m);

        await host.RunJobsAsync();
        (await host.GetAsync(invoice.Id)).State.Should().Be("paid");

        (await host.HistoryAsync(invoice.Id)).Select(h => h.TransitionKey).Should().Equal(
            "start", "update-billing-contact", "update-billing-contact",
            "record-payment", "record-payment", "record-payment", "delete-payment", "delete-payment",
            "record-payment", "patch-payment", "record-payment",
            "payment-completed", "$background", "reconciled");
    }

    [Fact]
    public async Task Sla_escalation_chains_triage_supports_critical_escalation_self_transitions_dynamic_timer_and_timeout()
    {
        await using var host = await CreateHostAsync();

        // 1. Yüksek priority ticket: chains triage -> priority-routing -> waiting-agent
        var ticket = await host.StartInstanceAsync(
            "sla-escalation",
            new
            {
                ticketTitle = "API Gecikmesi",
                customerName = "Marmara Finans",
                amount = 30000,
                severity = "Yüksek",
                slaDuration = "00:02:00",
            },
            user: Alice);

        ticket.State.Should().Be("waiting-agent");
        ticket.Data["assignedQueue"]!.GetValue<string>().Should().Be("Kıdemli Operasyon");

        // $self transition without reenter increments heartbeatCount
        ticket = await host.TransitionAsync(ticket.Id, "heartbeat-ping", new { progressNote = "Loglar inceleniyor" }, user: Alice);
        ticket.State.Should().Be("waiting-agent");
        ticket.Data["heartbeatCount"]!.GetValue<decimal>().Should().Be(1m);

        // $self transition with reenterreschedules dynamic SLA timer to 5 minutes
        ticket = await host.TransitionAsync(ticket.Id, "extend-sla", new { slaDuration = "00:05:00" }, user: Alice);
        ticket.State.Should().Be("waiting-agent");

        // After 2 minutes it should still be waiting-agent because SLA was extended to 5 minutes
        await host.RunJobsAsync(TimeSpan.FromMinutes(2));
        (await host.GetAsync(ticket.Id, user: Alice)).State.Should().Be("waiting-agent");

        // Advance another 3 minutes + 1s -> dynamic timer fires and moves to 'escalated'
        await host.RunJobsAsync(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        var escalated = await host.GetAsync(ticket.Id, user: Alice);
        escalated.State.Should().Be("escalated");

        // Verify order-escalated layout renders cleanly for sla-escalation without any '⚠' expression error
        var layoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Layouts", "order-escalated.json");
        var layout = UiJson.Parse(File.ReadAllText(layoutPath));
        var uiOptions = new MiddtUiOptions();
        var renderedHtml = new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
            layout,
            new Dictionary<string, JsonNode?>
            {
                [UiExpressions.Data] = escalated.Data,
                [UiExpressions.Instance] = new JsonObject { ["stateTitle"] = escalated.StateTitle, ["workflow"] = escalated.Workflow },
            },
            new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
            CultureInfo.InvariantCulture);
        renderedHtml.Should().NotContain("⚠");

        // Manager resolves the escalated ticket
        var resolved = await host.TransitionAsync(ticket.Id, "manager-resolve", new { resolutionNote = "Kök neden giderildi" }, user: Alice);
        resolved.State.Should().Be("resolved");
        resolved.Status.Should().Be(WorkflowInstanceStatus.Completed);

        // 2. Kritik priority ticket escalates immediately on start
        var critical = await host.StartInstanceAsync(
            "sla-escalation",
            new
            {
                ticketTitle = "Ana Veritabanı Kesintisi",
                customerName = "Genel Müdürlük",
                amount = 250000,
                severity = "Kritik",
                slaDuration = "00:05:00",
            },
            user: Alice);

        critical.State.Should().Be("escalated");
        critical.Data["assignedQueue"]!.GetValue<string>().Should().Be("Kriz Masası");

        // 3. Workflow-level 24h timeout transitions an uncompleted ticket to 'expired'
        await host.RunJobsAsync(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        var expired = await host.GetAsync(critical.Id, user: Alice);
        expired.State.Should().Be("expired");
        expired.StateSubType.Should().Be("Timeout");
    }

    [Fact]
    public async Task Risk_limit_eval_supports_auto_approval_manual_underwriting_and_over_limit_rejection()
    {
        await using var host = await CreateHostAsync();

        // 1. Standard amount (<= 500,000) auto-approves
        var approved = await host.StartInstanceAsync(
            "risk-limit-eval",
            new
            {
                customerKey = "CUST-12345678901",
                amount = 200000,
                checkType = "Kredi Limit Analizi",
                requireUnderwriting = false,
            },
            user: Alice);

        approved.State.Should().Be("approved");
        approved.StateSubType.Should().Be("Success");
        approved.Data["riskScore"]!.GetValue<int>().Should().Be(810);
        approved.Data["recommendedLimit"]!.GetValue<decimal>().Should().Be(250000m);

        var aiUnderwriter = approved.Agents["riskLimitUnderwriter"]!;
        aiUnderwriter["selectedTransition"]!.GetValue<string>().Should().Be("to-approved");
        aiUnderwriter["riskScore"]!.GetValue<int>().Should().Be(810);
        aiUnderwriter["creditGrade"]!.GetValue<string>().Should().Be("A+ (Prime)");
        aiUnderwriter["onTimePaymentRate"]!.GetValue<double>().Should().Be(98);
        aiUnderwriter["paymentBehavior"]!.GetValue<string>().Should().Contain("Kusursuz");
        aiUnderwriter["sanctionCheckStatus"]!.GetValue<string>().Should().Contain("MASAK");
        aiUnderwriter["centralBankRecord"]!.GetValue<string>().Should().Contain("TCMB Memzuç");
        aiUnderwriter["bureauStatus"]!.GetValue<string>().Should().Contain("KKB HTTP Gateway");
        aiUnderwriter["externalBureauRef"]!.GetValue<string>().Should().StartWith("KKB-HTTP-");
        approved.ToolCalls.Select(t => t.Name).Should().Contain([
            "query_customer_risk_score",
            "lookup_customer_payment_history",
            "verify_central_bank_registry_http",
            "evaluate_collateral_and_sector_exposure",
        ]);

        var httpToolCall = approved.ToolCalls.First(t => t.Name == "verify_central_bank_registry_http");
        httpToolCall.Result!["gateway"]!.GetValue<string>().Should().Be("TCMB-KKB-Mockoon");
        httpToolCall.Result["verified"]!.GetValue<bool>().Should().BeTrue();
        httpToolCall.Result["bureauStatus"]!.GetValue<string>().Should().Be("Onaylandı (KKB HTTP Gateway)");
        httpToolCall.Result["centralBankRecord"]!.GetValue<string>().Should().Be("Temiz (TCMB Memzuç Kaydı Uygun)");
        httpToolCall.Result["externalBureauRef"]!.GetValue<string>().Should().StartWith("KKB-HTTP-");
        httpToolCall.Result["echo"]!["customerKey"]!.GetValue<string>().Should().Be("CUST-12345678901");
        httpToolCall.Result["echo"]!["amount"]!.GetValue<decimal>().Should().Be(200000m);

        // 2. High amount (> 500,000) with requireUnderwriting=true pauses in manual-underwriting, supports AI recheck, and supports rejection
        var underwriting = await host.StartInstanceAsync(
            "risk-limit-eval",
            new
            {
                customerKey = "CUST-12345678901",
                amount = 800000,
                checkType = "Ticari Limit Analizi",
                requireUnderwriting = true,
            },
            user: Alice);

        underwriting.State.Should().Be("manual-underwriting");
        underwriting.Agents["riskLimitUnderwriter"]!["selectedTransition"]!.GetValue<string>().Should().Be("to-underwriting");
        underwriting.Agents["riskLimitUnderwriter"]!["creditGrade"]!.GetValue<string>().Should().Contain("B+");

        underwriting = await host.TransitionAsync(underwriting.Id, "ai-underwriting-recheck", new { }, user: Alice);
        underwriting.State.Should().Be("manual-underwriting");
        underwriting.AgentRuns.Count(r => r.AgentName == "risk-limit-underwriter").Should().Be(2);

        underwriting = await host.TransitionAsync(underwriting.Id, "reject-underwriting", new { reason = "Finansal rasyolar yetersiz" }, user: Alice);
        underwriting.State.Should().Be("rejected");
        underwriting.StateSubType.Should().Be("Error");

        // 3. Over-limit amount (> 5,000,000) auto-rejects immediately via AI agent routing
        var rejected = await host.StartInstanceAsync(
            "risk-limit-eval",
            new
            {
                customerKey = "CUST-12345678901",
                amount = 5500000,
                checkType = "Kurumsal Limit Analizi",
                requireUnderwriting = false,
            },
            user: Alice);

        rejected.State.Should().Be("rejected");
        rejected.StateSubType.Should().Be("Error");
        rejected.Agents["riskLimitUnderwriter"]!["selectedTransition"]!.GetValue<string>().Should().Be("to-rejected");
        rejected.Data["reason"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Customer_onboarding_supports_search_by_fullName_segment_and_riskLevel()
    {
        await using var host = await CreateHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // 1. Bireysel + Düşük risk (monthlyIncome = 65000 >= 50000)
        await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Zeynep Kaya",
                identityNumber = "11111111111",
                email = "zeynep@kaya.com",
                phone = "05321111111",
                segment = "Bireysel",
                monthlyIncome = 65000,
            },
            user: Alice);

        // 2. KOBİ + Orta risk (monthlyIncome = 30000 >= 20000)
        await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "İbrahim Öztürk Ticaret",
                identityNumber = "22222222222",
                email = "ibrahim@ozturk.com",
                phone = "05322222222",
                segment = "KOBİ",
                monthlyIncome = 30000,
            },
            user: Alice);

        // 3. Kurumsal + Yüksek risk (monthlyIncome = 12000 < 20000)
        await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Anadolu Lojistik A.Ş.",
                identityNumber = "33333333333",
                email = "info@anadolu.com",
                phone = "05323333333",
                segment = "Kurumsal",
                taxOffice = "Büyük Mükellefler V.D.",
                monthlyIncome = 12000,
            },
            user: Alice);

        // Filter by segment (uses partition B-Tree index pushdown)
        var (kobiItems, kobiCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["segment"] = "KOBİ" }, ct));
        kobiCount.Should().Be(1);
        kobiItems.Should().ContainSingle().Which.Data["fullName"]!.GetValue<string>().Should().Be("İbrahim Öztürk Ticaret");

        // Filter by riskLevel (uses partition B-Tree index pushdown)
        var (highRiskItems, highRiskCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["riskLevel"] = "Yüksek" }, ct));
        highRiskCount.Should().Be(1);
        highRiskItems.Should().ContainSingle().Which.Data["fullName"]!.GetValue<string>().Should().Be("Anadolu Lojistik A.Ş.");

        // Filter by fullName (Turkish case-insensitive substring match: "ibrahim" matches "İbrahim")
        var (nameItems, nameCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["fullName"] = "ibrahim", ["segment"] = "KOBİ", ["riskLevel"] = "Orta" }, ct));
        nameCount.Should().Be(1);
        nameItems.Should().ContainSingle().Which.Key.Should().Be("CUST-22222222222");

        // Filter by numeric range (monthlyIncome_min & monthlyIncome_max pushed to SQL)
        var (midIncomeItems, midIncomeCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["monthlyIncome_min"] = "20000", ["monthlyIncome_max"] = "50000" }, ct));
        midIncomeCount.Should().Be(1);
        midIncomeItems.Should().ContainSingle().Which.Key.Should().Be("CUST-22222222222");

        // Filter by date range (createdFrom & createdTo pushed to SQL)
        var (dateItems, dateCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["createdFrom"] = "2026-01-01", ["createdTo"] = "2026-12-31" }, ct));
        dateCount.Should().Be(3);
        dateItems.Should().HaveCount(3);

        var (_, futureCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["createdFrom"] = "2030-01-01" }, ct));
        futureCount.Should().Be(0);

        // Verify selective indexing: only enum/numeric x-search properties are indexed, not fullName or masked identityNumber
        var indexes = await host.WithDbAsync(db =>
            db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'index'").ToListAsync(ct));
        indexes.Should().Contain(["IX_Part_CustomerOnboarding_segment", "IX_Part_CustomerOnboarding_riskLevel", "IX_Part_CustomerOnboarding_monthlyIncome"]);
        indexes.Should().NotContain(["IX_Part_CustomerOnboarding_fullName", "IX_Part_CustomerOnboarding_identityNumber", "IX_Part_CustomerOnboarding_email"]);

        // Also verify WorkflowInstanceDataQuery (used by customer-directory.json grid) with text, dropdown, numeric range, and date range
        await using var scope = host.Services.CreateAsyncScope();
        var dataQuery = scope.ServiceProvider.GetRequiredService<WorkflowInstanceDataQuery>();
        var directoryFiltered = await dataQuery.GetItemsAsync(
            "CustomerOnboarding",
            new System.Text.Json.Nodes.JsonObject
            {
                ["currentPage"] = 1,
                ["requestItemSize"] = 10,
                ["requestModel"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["fullName"] = "ibrahim",
                    ["segment"] = "KOBİ",
                    ["riskLevel"] = "Orta",
                    ["monthlyIncome_min"] = 25000,
                    ["monthlyIncome_max"] = 40000,
                    ["createdAt_from"] = "2026-01-01",
                    ["createdAt_to"] = "2026-12-31",
                },
            },
            ManagerPrincipal,
            ct);
        directoryFiltered.Result.Should().Be(ResultEnum.Success);
        directoryFiltered.Count.Should().Be(1);
        directoryFiltered.Data.Should().ContainSingle().Which["fullName"]!.GetValue<string>().Should().Be("İbrahim Öztürk Ticaret");
    }

    [Fact]
    public async Task DataQuery_and_ListAsync_push_filters_and_numeric_sorting_to_sql_beyond_1000_records()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await CreateHostAsync();

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldestId = Guid.CreateVersion7(new DateTimeOffset(baseTime));
        var negTwoId = Guid.CreateVersion7(new DateTimeOffset(baseTime.AddSeconds(1)));
        var decOneId = Guid.CreateVersion7(new DateTimeOffset(baseTime.AddSeconds(2)));
        var decTwoId = Guid.CreateVersion7(new DateTimeOffset(baseTime.AddSeconds(3)));

        await host.WithDbAsync(async db =>
        {
            void AddCustomerRow(Guid id, int index, string fullName, string monthlyIncomeJson, DateTime createdAt)
            {
                var key = $"CUST-{index:D6}";
                var dataJson = $$"""{"fullName":"{{fullName}}","identityNumber":"1000000{{index:D4}}","email":"c{{index}}@test.com","phone":"05320000000","segment":"Bireysel","riskLevel":"Düşük","monthlyIncome":{{monthlyIncomeJson}}}""";
                db.Instances.Add(new Middt.Framework.Workflow.Persistence.WorkflowInstance
                {
                    Id = id,
                    WorkflowKey = "customer-onboarding",
                    WorkflowVersion = 1,
                    BusinessKey = key,
                    State = "active",
                    Status = WorkflowInstanceStatus.Active,
                    StartedBy = "alice",
                    LastExecutedBy = "alice",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                    InstanceData = new Middt.Framework.Workflow.Persistence.WorkflowInstanceData
                    {
                        InstanceId = id,
                        WorkflowKey = "customer-onboarding",
                        Data = dataJson,
                        DataVersion = 1,
                        DataHash = $"h-{index}",
                        ETag = $"e-{index}",
                        UpdatedAt = createdAt,
                    },
                });
            }

            AddCustomerRow(oldestId, 1, "Eski Müşteri Zeynep", "-100.5", baseTime);
            AddCustomerRow(negTwoId, 2, "Negatif Bakiye İki", "-5.25", baseTime.AddSeconds(1));
            AddCustomerRow(decOneId, 3, "Hassas Ondalık Bir", "1.00001", baseTime.AddSeconds(2));
            AddCustomerRow(decTwoId, 4, "Hassas Ondalık İki", "1.00009", baseTime.AddSeconds(3));

            for (var i = 5; i <= 1005; i++)
            {
                var t = baseTime.AddSeconds(i);
                AddCustomerRow(Guid.CreateVersion7(new DateTimeOffset(t)), i, $"Yeni Kayıt {i}", "500", t);
            }

            return await db.SaveChangesAsync(ct);
        });

        // 1. Finding 1: Oldest record beyond the newest 1,000 rows is found via SQL filter pushdown
        var (engineItems, engineCount) = await host.WithEngineAsync(e =>
            e.ListAsync("customer-onboarding", Alice, null, null, null, 1, 20, new Dictionary<string, string?> { ["fullName"] = "zeynep" }, ct));
        engineCount.Should().Be(1);
        engineItems.Should().ContainSingle().Which.Id.Should().Be(oldestId);

        await using var scope = host.Services.CreateAsyncScope();
        var dataQuery = scope.ServiceProvider.GetRequiredService<WorkflowInstanceDataQuery>();

        var queryOldest = await dataQuery.GetItemsAsync(
            "CustomerOnboarding",
            new System.Text.Json.Nodes.JsonObject
            {
                ["currentPage"] = 1,
                ["requestItemSize"] = 10,
                ["requestModel"] = new System.Text.Json.Nodes.JsonObject { ["fullName"] = "zeynep" },
            },
            ManagerPrincipal,
            ct);
        queryOldest.Result.Should().Be(ResultEnum.Success);
        queryOldest.Count.Should().Be(1);
        queryOldest.Data.Should().ContainSingle().Which["id"]!.GetValue<string>().Should().Be(oldestId.ToString());

        // Unsupported / masked filter returns an explicit error instead of silently returning truncated rows
        var maskedFilterResult = await dataQuery.GetItemsAsync(
            "CustomerOnboarding",
            new System.Text.Json.Nodes.JsonObject
            {
                ["currentPage"] = 1,
                ["requestItemSize"] = 10,
                ["requestModel"] = new System.Text.Json.Nodes.JsonObject { ["identityNumber"] = "1000" },
            },
            OwnerPrincipal,
            ct);
        maskedFilterResult.Result.Should().Be(ResultEnum.Error);

        // 2. Finding 6: Numeric sort orders negative numbers (-100.5 < -5.25) and >4 decimal places (1.00001 < 1.00009) accurately
        var sortedAsc = await dataQuery.GetItemsAsync(
            "CustomerOnboarding",
            new System.Text.Json.Nodes.JsonObject
            {
                ["currentPage"] = 1,
                ["requestItemSize"] = 6,
                ["sortProperty"] = "monthlyIncome",
                ["sortReverse"] = true,
            },
            ManagerPrincipal,
            ct);
        sortedAsc.Result.Should().Be(ResultEnum.Success);
        sortedAsc.Count.Should().Be(1005);
        sortedAsc.Data!.Take(4).Select(r => r["id"]!.GetValue<string>()).Should().Equal(
            oldestId.ToString(),
            negTwoId.ToString(),
            decOneId.ToString(),
            decTwoId.ToString());
    }

    [Fact]
    public async Task Commercial_limit_revision_aggregates_credit_portfolio_enforces_4_eyes_updates_customer_and_renders_layout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await CreateHostAsync();

        // 1. Create a customer in customer-onboarding and an active credit-allocation
        var customer = await host.StartInstanceAsync(
            "customer-onboarding",
            new
            {
                fullName = "Ege İhracat Sanayi A.Ş.",
                identityNumber = "55555555555",
                email = "finans@egeihracat.com",
                phone = "02324445566",
                segment = "Kurumsal",
                taxOffice = "Hasan Tahsin V.D.",
                monthlyIncome = 600000,
            },
            user: Alice);

        customer.Key.Should().Be("CUST-55555555555");

        var loan = await host.StartInstanceAsync(
            "credit-allocation",
            new
            {
                customerKey = "CUST-55555555555",
                amount = 400000,
                termMonths = 24,
                collateralAmount = 600000,
                iban = "TR330006100519786457841326",
                requireUnderwriting = false,
            },
            user: Alice);
        loan.State.Should().Be("stage1-approval");

        // 2. Start commercial-limit-revision (> 500,000) -> loads customer, queries credit-allocation portfolio, and routes to maker-review
        var revision = await host.StartInstanceAsync(
            "commercial-limit-revision",
            new
            {
                customerKey = "CUST-55555555555",
                requestedLimit = 1500000,
                revisionType = "Ek Limit Artırımı",
                justification = "Yeni ihracat bağlantıları için ek ticari limit.",
            },
            user: Alice);

        revision.State.Should().Be("maker-review");
        revision.Key.Should().Be("REV-CUST-55555555555");
        revision.Data["customerName"]!.GetValue<string>().Should().Be("Ege İhracat Sanayi A.Ş.");
        revision.Data["currentExposure"]!.GetValue<decimal>().Should().Be(400000m);
        revision.Data["activeLoanCount"]!.GetValue<int>().Should().Be(1);
        revision.Data["netAvailableLimit"]!.GetValue<decimal>().Should().Be(1100000m);

        // 3. Revise requestedLimit via $self transition 'revise-requested-limit'
        revision = await host.TransitionAsync(revision.Id, "revise-requested-limit", new { requestedLimit = 2000000 }, user: Alice);
        revision.State.Should().Be("maker-review");
        revision.Data["requestedLimit"]!.GetValue<decimal>().Should().Be(2000000m);
        revision.Data["netAvailableLimit"]!.GetValue<decimal>().Should().Be(1600000m);

        // 4. Alice approves stage 1 (maker) -> moves to checker-approval
        revision = await host.TransitionAsync(revision.Id, "submit-to-checker", new { makerComment = "Nakit akışı uygun" }, user: Alice);
        revision.State.Should().Be("checker-approval");

        // 4-Eyes: Alice ($previousUser) cannot see 'approve-four-eyes', Bob approves -> updates customer-onboarding and completes
        revision.AvailableTransitions.Select(t => t.Key).Should().NotContain("approve-four-eyes");
        revision = await host.TransitionAsync(revision.Id, "approve-four-eyes", new { checkerComment = "Komite 4-göz onayı verildi" }, user: Bob);
        revision.State.Should().Be("completed");
        revision.Status.Should().Be(WorkflowInstanceStatus.Completed);

        // Verify updateWorkflow updated the customer-onboarding record
        var updatedCustomer = await host.GetAsync(customer.Id, user: Alice);
        updatedCustomer.Data["approvedCommercialLimit"]!.GetValue<decimal>().Should().Be(2000000m);
        updatedCustomer.Data["lastLimitRevisionKey"]!.GetValue<string>().Should().Be("REV-CUST-55555555555");

        // Verify limit-revision-detail layout renders cleanly without any '⚠' expression error
        var layoutPath = Path.Combine(RepositoryRoot(), "demo", "Middt.Demo.Workflows", "Layouts", "limit-revision-detail.json");
        var layout = UiJson.Parse(await File.ReadAllTextAsync(layoutPath, ct));
        var uiOptions = new MiddtUiOptions();
        var renderedHtml = new UiHtmlRenderer(uiOptions.Widgets, Options.Create(uiOptions)).Render(
            layout,
            new Dictionary<string, JsonNode?>
            {
                [UiExpressions.Data] = revision.Data,
                [UiExpressions.Instance] = new JsonObject
                {
                    ["state"] = revision.State,
                    ["stateTitle"] = revision.StateTitle,
                    ["workflow"] = revision.Workflow,
                },
            },
            new Middt.Framework.Expressions.ExpressionUser { Name = "alice", Roles = ["manager"] },
            CultureInfo.InvariantCulture);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(renderedHtml);
        decodedHtml.Should().NotContain("⚠");
        decodedHtml.Should().Contain("Ege İhracat Sanayi A.Ş.");
        decodedHtml.Should().Contain("CUST-55555555555");
    }
}
