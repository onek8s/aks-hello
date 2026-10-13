using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
// Readiness: the pod takes traffic from the moment the app has started until it is told to stop.
builder.Services.AddHealthChecks()
    .AddCheck<LifetimeCheck>("lifetime", tags: ["ready"]);

var app = builder.Build();
var started = DateTimeOffset.UtcNow;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

// The cluster the pod runs in, handed down by the deployment (CLUSTER_NAME). With the same hostname
// served by several clusters at once, it is what tells a visitor which one answered.
var cluster = Environment.GetEnvironmentVariable("CLUSTER_NAME") is { Length: > 0 } name ? name : "unknown";

app.MapGet("/", () => Results.Content($$"""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Hello from OneK8s</title>
      <style>
        body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f5f7fa; color: #1f2933; }
        main { text-align: center; padding: 2rem; }
        h1 { font-size: 2.5rem; margin-bottom: .5rem; }
        p { color: #52606d; }
        code { background: #e4e7eb; padding: .1rem .4rem; border-radius: 4px; }
        .cluster { font-size: 1.5rem; color: #1f2933; }
      </style>
    </head>
    <body>
      <main>
        <h1>Hello, world!</h1>
        <p class="cluster">Cluster <code>{{WebUtility.HtmlEncode(cluster)}}</code></p>
        <p>Served by .NET {{WebUtility.HtmlEncode(Environment.Version.ToString())}} from pod <code>{{WebUtility.HtmlEncode(Environment.MachineName)}}</code>.</p>
        <p>Version <code>{{WebUtility.HtmlEncode(version)}}</code></p>
      </main>
    </body>
    </html>
    """, "text/html; charset=utf-8"));

// Liveness: the process answers HTTP. No checks of anything else - a dependency that is down is
// not a reason to restart this container.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: the checks tagged "ready".
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Every check, as before 0.3.0.
app.MapHealthChecks("/healthz");

// What is running where, for people and scripts: the same facts as the page, as JSON.
app.MapGet("/status", (IHostApplicationLifetime lifetime) => new Status(
    Name: "aks-hello",
    Version: version,
    Cluster: cluster,
    Node: Environment.GetEnvironmentVariable("NODE_NAME") is { Length: > 0 } node ? node : "unknown",
    Pod: Environment.MachineName,
    Runtime: $".NET {Environment.Version}",
    Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    StartedAt: started,
    UptimeSeconds: (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
    Ready: !lifetime.ApplicationStopping.IsCancellationRequested));

app.Run();

record Status(
    string Name,
    string Version,
    string Cluster,
    string Node,
    string Pod,
    string Runtime,
    string Architecture,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    bool Ready);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Status))]
partial class AppJsonContext : JsonSerializerContext;

sealed class LifetimeCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            lifetime.ApplicationStopping.IsCancellationRequested ? HealthCheckResult.Unhealthy("stopping")
            : !lifetime.ApplicationStarted.IsCancellationRequested ? HealthCheckResult.Unhealthy("starting")
            : HealthCheckResult.Healthy());
}
