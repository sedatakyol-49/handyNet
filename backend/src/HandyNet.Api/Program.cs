using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HandyNet.Migrations;
using HandyNet.Modules.Catalog;
using HandyNet.Modules.Identity;
using HandyNet.Modules.Identity.Application;
using HandyNet.Modules.Imports;
using HandyNet.Modules.Inventory;
using HandyNet.Modules.Marketplace;
using HandyNet.Modules.Notifications;
using HandyNet.Modules.Shipping;
using HandyNet.Modules.Payments;
using HandyNet.Modules.Disputes;
using HandyNet.Modules.Ankauf;
using HandyNet.Modules.Ordering;
using HandyNet.Modules.Tenancy;
using HandyNet.Persistence;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// `dotnet HandyNet.Api.dll migrate` applies database migrations with the owner connection and exits.
if (args.Contains("migrate"))
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .AddUserSecrets<Program>(optional: true)
        .AddCommandLine(args)
        .Build();
    var owner = config.GetConnectionString("HandyNetMigrations")
                ?? throw new InvalidOperationException("ConnectionStrings:HandyNetMigrations is not configured.");
    var applied = await DatabaseMigrator.MigrateAsync(owner);
    Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied: {string.Join(", ", applied)}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("HandyNet")
                       ?? throw new InvalidOperationException("ConnectionStrings:HandyNet is not configured.");

builder.Services
    .AddHandyNetPersistence(connectionString)
    .AddIdentityModule(builder.Configuration)
    .AddTenancyModule()
    .AddCatalogModule()
    .AddInventoryModule()
    .AddMarketplaceModule()
    .AddImportsModule()
    .AddOrderingModule(builder.Configuration)
    .AddNotificationsModule(builder.Configuration)
    .AddShippingModule(builder.Configuration, builder.Environment)
    .AddPaymentsModule(builder.Configuration, builder.Environment)
    .AddDisputesModule()
    .AddAnkaufModule();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.CreateKey(),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(HandyNetClaims.PlatformAdminPolicy, p => p.RequireAuthenticatedUser().RequireClaim(HandyNetClaims.PlatformAdmin, "true"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(IdentityModule.AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHealthChecks().AddDbContextCheck<HandyNetDbContext>("database", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("handynet-api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddSource("Npgsql");
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            tracing.AddOtlpExporter();
    });

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapIdentityEndpoints()
    .MapTenancyEndpoints()
    .MapCatalogEndpoints()
    .MapInventoryEndpoints()
    .MapMarketplaceEndpoints()
    .MapImportsEndpoints()
    .MapOrderingEndpoints()
    .MapShippingEndpoints()
    .MapPaymentsEndpoints()
    .MapDisputesEndpoints()
    .MapAnkaufEndpoints();

if (app.Configuration["Bootstrap:AdminEmail"] is { Length: > 0 } adminEmail)
{
    await IdentityModule.EnsurePlatformAdminAsync(app.Services, adminEmail,
        app.Configuration["Bootstrap:AdminPassword"] ?? throw new InvalidOperationException("Bootstrap:AdminPassword is required."),
        CancellationToken.None);
}

app.Run();

public partial class Program;
