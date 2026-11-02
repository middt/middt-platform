using Middt.Demo.Workflows;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddHttpClient("Runtime", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHostedService<DemoFlowDeployer>();

var host = builder.Build();
await host.RunAsync();

