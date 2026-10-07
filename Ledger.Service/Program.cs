using System.Net;
using System.Reflection;
using Ledger.Repository;
using Ledger.Service.Auth;
using Ledger.Service.Cli;
using Ledger.Service.Endpoints;
using Ledger.Service.Health;
using Ledger.Service.Hosting;
using Ledger.Service.Ingestion;
using Ledger.Service.Mcp;
using Ledger.Service.OAuth;
using Ledger.Service.Security;
using Ledger.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;
using LedgerMetrics = Ledger.Service.Metrics.LedgerMetrics;
using HealthCheckMetricsPublisher = Ledger.Service.Metrics.HealthCheckMetricsPublisher;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
{
    ProductionConfigurationValidator.ThrowIfInvalid(builder.Configuration);
}

if (args.Length > 0 && args[0] == "apikey")
{
    return await ApiKeyCommand.RunAsync(args[1..], builder.Configuration);
}

if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddSystemdConsole();
}

builder.Services.Configure<KestrelServerOptions>(options => options.AddServerHeader = false);

builder.Services.AddProblemDetails();

builder.Services.AddLedgerRepository(builder.Configuration);

builder.Services.AddLedgerIngestion(builder.Configuration);

builder.Services.AddLedgerDataProtection(builder.Configuration, builder.Environment);

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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    foreach (var proxy in knownProxies)
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }
});

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddLedgerOAuth(builder.Configuration);
builder.Services.AddLedgerMcp(builder.Configuration);

var app = builder.Build();

var opsPort = OpsEndpoint.FromConfiguration(app.Configuration);

LedgerMetrics.RecordBuildInfo(Assembly.GetExecutingAssembly());
McpMetrics.InitialiseCounters();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
}

app.UseHealthChecks("/health", opsPort, new HealthCheckOptions
{
    ResponseWriter = (context, result) => context.Response.WriteAsync(result.Status.ToString())
});
app.UseMetricServer(opsPort);
app.UseHttpMetrics();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStatusEndpoints();
app.MapBankEndpoints();
app.MapLedgerOAuth();
app.MapLedgerMcp();

await app.RunAsync();

return 0;

/// <summary>Entry point for the ledger host, exposed as a partial class so the integration test factory can boot it in-process.</summary>
public partial class Program;
