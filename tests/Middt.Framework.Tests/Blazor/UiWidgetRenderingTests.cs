using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Middt.Framework.Blazor.Ui.Components;
using Middt.Framework.Blazor.Ui.Rendering;
using Middt.Framework.Tests.Ui;
using Middt.Framework.Ui.Model;
using Middt.Framework.Ui.Rendering;
using Middt.Framework.Ui.Widgets;

namespace Middt.Framework.Tests.Blazor;

/// <summary>Widgets are not re-rendered when a parent re-renders with the same node and render context.</summary>
public sealed class UiWidgetRenderingTests
{
    public sealed class CountingWidget : UiWidgetBase
    {
        private static int renders;

        public static int Renders => Volatile.Read(ref renders);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Interlocked.Increment(ref renders);
            builder.AddContent(0, Text("text"));
        }
    }

    /// <summary>Parent that re-renders on demand (like a workflow screen re-rendering around an embedded layout).</summary>
    public sealed class Parent : ComponentBase
    {
        [Parameter]
        public UiNode Node { get; set; } = default!;

        [Parameter]
        public UiRenderContext Context { get; set; } = default!;

        [Parameter]
        public Action<Parent>? Created { get; set; }

        protected override void OnInitialized() => Created?.Invoke(this);

        public void Rerender(UiRenderContext? context = null, UiNode? node = null)
        {
            Node = node ?? Node;
            Context = context ?? Context;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<UiNodeView>(0);
            builder.AddComponentParameter(1, nameof(UiNodeView.Node), Node);
            builder.AddComponentParameter(2, nameof(UiNodeView.Context), Context);
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task Same_node_and_context_skip_the_widget_render_a_new_context_does_not()
    {
        var widgets = UiBlazorWidgets.CreateDefault().Add<CountingWidget>(new UiWidgetSpec("counting", "Counting") { Props = [new("text", UiPropType.Template)] });
        await using var services = new ServiceCollection().AddLogging().AddSingleton(widgets).BuildServiceProvider();
        var layout = UiRenderInput.Layout("""[ { "widget": "counting", "text": "n={{ data.n }}" } ]""");
        UiRenderContext Context(int n) => new(widgets.Catalog, UiRenderInput.User, UiRenderInput.Variables($$"""{ "n": {{n}} }"""), interactive: true) { Culture = CultureInfo.InvariantCulture };

        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        Parent? parent = null;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Parent>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Parent.Node)] = layout.Body[0],
                [nameof(Parent.Context)] = Context(1),
                [nameof(Parent.Created)] = (Action<Parent>)(p => parent = p),
            }));
            return output;
        });

        var before = CountingWidget.Renders;
        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender());
        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender());
        CountingWidget.Renders.Should().Be(before, "node and context are the same instances");

        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender(Context(2)));
        CountingWidget.Renders.Should().Be(before + 1);
        (await renderer.Dispatcher.InvokeAsync(html.ToHtmlString)).Should().Be("n=2");
    }

    private sealed class StubRecordDataClient(Func<string, string, CancellationToken, Task<Middt.Framework.Common.Model.Data.BaseResponseDataModel<System.Text.Json.Nodes.JsonObject>>> handler)
        : Middt.Framework.Client.Http.UiDataClient(
            new System.Net.Http.HttpClient(),
            new Middt.Framework.Tests.Client.FakeSessionState("token"),
            new Middt.Framework.Tests.Client.FakeTokenService(_ => new Middt.Framework.Model.Authentication.TokenResponseModel()),
            Microsoft.Extensions.Options.Options.Create(new Middt.Framework.Api.Configuration.Model.ApiSettings { URL = "http://localhost", Version = "1" }),
            NullLogger<Middt.Framework.Client.Http.UiDataClient>.Instance)
    {
        public override Task<Middt.Framework.Common.Model.Data.BaseResponseDataModel<System.Text.Json.Nodes.JsonObject>> GetByIdAsync(string entity, string id, CancellationToken cancellationToken = default) =>
            handler(entity, id, cancellationToken);
    }

    [Fact]
    public async Task Record_widget_reloads_when_entity_changes_with_same_id_and_ignores_stale_response()
    {
        var ct = TestContext.Current.CancellationToken;
        var slowTcs = new TaskCompletionSource<Middt.Framework.Common.Model.Data.BaseResponseDataModel<System.Text.Json.Nodes.JsonObject>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataClient = new StubRecordDataClient(async (entity, id, reqCt) =>
        {
            if (entity == "customers" && id == "slow")
            {
                await using var reg = reqCt.Register(() => slowTcs.TrySetCanceled(reqCt));
                return await slowTcs.Task;
            }

            var label = entity == "orders" ? "Order-42" : "Customer-42";
            return Middt.Framework.Common.Model.Data.BaseResponseDataModel<System.Text.Json.Nodes.JsonObject>.Success(
                new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["title"] = label });
        });

        var widgets = UiBlazorWidgets.CreateDefault();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(widgets)
            .AddSingleton<Middt.Framework.Client.Http.UiDataClient>(dataClient)
            .BuildServiceProvider();

        static UiNode RecordNode(string entity, string recordIdExpr) =>
            UiRenderInput.Layout($$"""[ { "widget": "record", "entity": "{{entity}}", "recordId": "{{recordIdExpr}}", "children": [ { "widget": "text", "text": "{{ "{{" }} record.title {{ "}}" }}" } ] } ]""").Body[0];

        UiRenderContext MakeContext(string id) =>
            new(widgets.Catalog, UiRenderInput.User, UiRenderInput.Variables($$"""{ "id": "{{id}}" }"""), interactive: true) { Culture = CultureInfo.InvariantCulture };

        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        Parent? parent = null;
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<Parent>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Parent.Node)] = RecordNode("customers", "{{ data.id }}"),
                [nameof(Parent.Context)] = MakeContext("42"),
                [nameof(Parent.Created)] = (Action<Parent>)(p => parent = p),
            })));

        (await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)).Should().Contain("Customer-42");

        // Changing entity from "customers" to "orders" with the SAME id ("42") must reload
        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender(MakeContext("42"), RecordNode("orders", "{{ data.id }}")));
        (await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)).Should().Contain("Order-42");

        // Trigger a slow request followed immediately by a fast request; the slow response must not overwrite the fast one
        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender(MakeContext("slow"), RecordNode("customers", "{{ data.id }}")));
        await renderer.Dispatcher.InvokeAsync(() => parent!.Rerender(MakeContext("42"), RecordNode("orders", "{{ data.id }}")));
        slowTcs.TrySetResult(Middt.Framework.Common.Model.Data.BaseResponseDataModel<System.Text.Json.Nodes.JsonObject>.Success(
            new System.Text.Json.Nodes.JsonObject { ["id"] = "slow", ["title"] = "Stale-Customer" }));
        await Task.Delay(20, ct);
        (await renderer.Dispatcher.InvokeAsync(root.ToHtmlString)).Should().Contain("Order-42").And.NotContain("Stale-Customer");
    }
}
