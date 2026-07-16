using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

var builder = WebApplication.CreateBuilder(args);

// Single file holding every monitored environment (QA, DEV, PROD_ACS, ...) nested under
// Monitoring:Environments — the dashboard's own environment selector picks which one to
// display, so this does NOT follow ASPNETCORE_ENVIRONMENT (that's this host's own env,
// unrelated to which target environments it monitors).
builder.Configuration.AddJsonFile("config/targets.json", optional: false, reloadOnChange: false);

builder.Services.Configure<MonitoringTargetsOptions>(
    builder.Configuration.GetSection(MonitoringTargetsOptions.SectionName));

// Serialize enums (e.g. CheckType) as their string names, not raw integers —
// the frontend's TS models expect "Api"/"Db", not 0/1.
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddHttpClient("probe", client =>
{
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue<int?>($"{MonitoringTargetsOptions.SectionName}:HttpTimeoutSeconds") ?? 15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("KLSPL-Monitoring/1.0");
});

builder.Services.AddScoped<HealthCheckerService>();
builder.Services.AddHostedService<PollingHostedService>();

// Sentinel-ported features -----------------------------------------------------
// RBAC / user management: password hashing + audit logging + merged target source.
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<ActivityLogger>();
builder.Services.AddScoped<ITargetProvider, TargetProvider>();
builder.Services.AddScoped<SettingsService>();

// Azure DevOps pipeline dashboard (scoped: reads runtime settings from the DB).
builder.Services.AddHttpClient("azure", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<AzureDevOpsService>();
builder.Services.AddSingleton<PipelineAnalysisService>();

// Ocelot API-gateway auto-importer (startup seed + runtime watcher).
builder.Services.AddSingleton<OcelotImportService>();
builder.Services.AddHostedService<OcelotWatcherHostedService>();

// History/incident log + heuristic insights (stateless — pure computation over StatusChecks).
builder.Services.AddSingleton<IncidentAnalysisService>();
// ------------------------------------------------------------------------------

builder.Services.AddDbContext<MonitoringDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("MonitoringDb")
        ?? "Host=monitoring-db;Database=monitoring;Username=monitoring;Password=monitoring";
    options.UseNpgsql(connectionString);
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardUi", policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Ensure schema exists and seed the default admin from the BasicAuth credential
// (zero-setup boot, ported from Sentinel) before serving requests.
await DbSeeder.SeedAsync(app.Services);

app.UseCors("DashboardUi");
app.UseMiddleware<BasicAuthMiddleware>();
app.MapControllers();
app.MapGet("/", () => Results.Ok(new { status = "Monitoring_API running" }));

app.Run();
