using Microsoft.AspNetCore.DataProtection;
using Middt.Framework.Blazor.DependencyInjection;
using Middt.Framework.Blazor.Ui;
using Middt.Framework.Client.Http;
using Middt.Demo.Blazor.Service;
using Middt.Framework.Client.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.AddMiddtBlazor();

// With Redis (Docker Compose) sessions and Data Protection keys are shared and survive restarts.
if (builder.Configuration.GetConnectionString("redis") is { Length: > 0 } redis)
{
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redis;
        o.InstanceName = "middt-sample-blazor:";
    });
    builder.Services.AddDataProtection()
        .PersistKeysToStackExchangeRedis(StackExchange.Redis.ConnectionMultiplexer.Connect(redis), "middt-sample-blazor:dataprotection-keys");
}

// Typed API clients (IHttpClientFactory + standard resilience pipeline).
builder.Services.AddMiddtApiClient<WorkflowApiClient>();
builder.Services.AddMiddtUi(); // server driven UI: JSON layouts (/ui/{key}, workflow state layouts, /ui-admin editor)

builder.Services.AddScoped<MenuService>();

var app = builder.Build();

app.UseMiddtBlazor();

app.Run();
