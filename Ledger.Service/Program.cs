using System.Reflection;
using Ledger.Repository;
using Ledger.Service.Cli;
using Ledger.Service.Health;
using Ledger.Service.Hosting;
using Ledger.Service.Security;
using Ledger.Domain.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using LedgerMetrics = Ledger.Service.Metrics.LedgerMetrics;
using HealthCheckMetricsPublisher = Ledger.Service.Metrics.HealthCheckMetricsPublisher;

var builder = WebApplication.CreateBuilder(args);

if (args.Length > 0 && args[0] == "apikey")
{
    return await ApiKeyCommand.RunAsync(args[1..], builder.Configuration);
}

builder.Services.Configure<KestrelServerOptions>(options => options.AddServerHeader = false);

builder.Services.AddLedgerRepository(builder.Configuration);

builder.Services
    .AddDataProtection()
    .SetApplicationName("HouseholdLedger")
    .PersistKeysToLedgerDatabase();

builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddHostedService<DataProtectionCanaryInitializer>();

builder.Services
    .AddHealthChecks()
    .AddLedgerDatabaseCheck()
    .AddCheck<DataProtectionCanaryHealthCheck>("data_protection_canary");

builder.Services.AddSingleton<IHealthCheckPublisher, HealthCheckMetricsPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.FromSeconds(5);
    options.Period = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

var opsPort = OpsEndpoint.FromConfiguration(app.Configuration);

LedgerMetrics.RecordBuildInfo(Assembly.GetExecutingAssembly());

app.UseHealthChecks("/health", opsPort, new HealthCheckOptions
{
    ResponseWriter = (context, result) => context.Response.WriteAsync(result.Status.ToString())
});
app.UseMetricServer(opsPort);
app.UseHttpMetrics();

await app.RunAsync();

return 0;

/// <summary>Entry point for the ledger host, exposed as a partial class so the integration test factory can boot it in-process.</summary>
public partial class Program;
