using Ledger.Repository;
using Ledger.Service.Health;
using Ledger.Service.Hosting;
using Ledger.Service.Security;
using Ledger.Domain.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

var opsPort = OpsEndpoint.FromConfiguration(app.Configuration);

app.UseHealthChecks("/health", opsPort, new HealthCheckOptions
{
    ResponseWriter = (context, result) => context.Response.WriteAsync(result.Status.ToString())
});
app.UseMetricServer(opsPort);
app.UseHttpMetrics();

await app.RunAsync();

/// <summary>Entry point for the ledger host, exposed as a partial class so the integration test factory can boot it in-process.</summary>
public partial class Program;
