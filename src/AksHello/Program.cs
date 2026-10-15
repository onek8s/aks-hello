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
// The weather forecast, fetched from the internet (WEATHER_URL) - an outbound dependency the platform's
// egress control has to let through. Never a health check: no forecast is not a reason to stop serving.
builder.Services.AddHttpClient("weather", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("aks-hello (+https://github.com/onek8s/aks-hello)");
});
builder.Services.AddSingleton<Weather>();

var app = builder.Build();
var started = DateTimeOffset.UtcNow;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

// The cluster the pod runs in, handed down by the deployment (CLUSTER_NAME). With the same hostname
// served by several clusters at once, it is what tells a visitor which one answered.
var cluster = Environment.GetEnvironmentVariable("CLUSTER_NAME") is { Length: > 0 } name ? name : "unknown";

app.MapGet("/", async (Weather weather, CancellationToken cancellationToken) => Results.Content($$"""
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
        .weather { margin-top: 1.5rem; }
        .weather.error { color: #ab091e; }
      </style>
    </head>
    <body>
      <main>
        <h1>Hello, world!</h1>
        <p class="cluster">Cluster <code>{{WebUtility.HtmlEncode(cluster)}}</code></p>
        <p>Served by .NET {{WebUtility.HtmlEncode(Environment.Version.ToString())}} from pod <code>{{WebUtility.HtmlEncode(Environment.MachineName)}}</code>.</p>
        <p>Version <code>{{WebUtility.HtmlEncode(version)}}</code></p>
        {{(await weather.GetAsync(cancellationToken)).ToHtml()}}
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

// The forecast as JSON: what the page shows, or why there is none.
app.MapGet("/weather", async (Weather weather, CancellationToken cancellationToken) => await weather.GetAsync(cancellationToken));

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
[JsonSerializable(typeof(Forecast))]
partial class AppJsonContext : JsonSerializerContext;

sealed class LifetimeCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            lifetime.ApplicationStopping.IsCancellationRequested ? HealthCheckResult.Unhealthy("stopping")
            : !lifetime.ApplicationStarted.IsCancellationRequested ? HealthCheckResult.Unhealthy("starting")
            : HealthCheckResult.Healthy());
}

// Today's forecast for one place, from Open-Meteo (no API key) or whatever WEATHER_URL names with the
// same response shape. Cached for WEATHER_CACHE_SECONDS (default 600) - a failure for 60 seconds, so a
// blocked or broken egress shows up as one attempt a minute per pod in the firewall's log, not one per
// page view.
sealed class Weather(IHttpClientFactory httpClientFactory, ILogger<Weather> logger)
{
    readonly string? url = Environment.GetEnvironmentVariable("WEATHER_URL") is { Length: > 0 } value ? value
        : Environment.GetEnvironmentVariable("WEATHER_DISABLED") == "true" ? null
        : "https://api.open-meteo.com/v1/forecast?latitude=60.17&longitude=24.94&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto&forecast_days=3";
    readonly string place = Environment.GetEnvironmentVariable("WEATHER_PLACE") is { Length: > 0 } value ? value : "Helsinki";
    readonly TimeSpan cacheFor = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("WEATHER_CACHE_SECONDS"), out var seconds) ? seconds : 600);
    readonly SemaphoreSlim gate = new(1, 1);
    Forecast? cached;
    DateTimeOffset expires;

    public async Task<Forecast> GetAsync(CancellationToken cancellationToken)
    {
        if (url is null)
            return new Forecast(place, null, [], "the weather forecast is turned off (WEATHER_DISABLED)", DateTimeOffset.UtcNow);
        if (cached is not null && DateTimeOffset.UtcNow < expires)
            return cached;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow < expires)
                return cached;
            cached = await FetchAsync(url, cancellationToken);
            expires = DateTimeOffset.UtcNow + (cached.Error is null ? cacheFor : TimeSpan.FromSeconds(60));
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<Forecast> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var source = new Uri(url).Host;
        try
        {
            using var response = await httpClientFactory.CreateClient("weather").GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var daily = document.RootElement.GetProperty("daily");
            var dates = daily.GetProperty("time").EnumerateArray().Select(d => d.GetString() ?? "").ToArray();
            var max = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(t => t.GetDouble()).ToArray();
            var min = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(t => t.GetDouble()).ToArray();
            var rain = daily.TryGetProperty("precipitation_sum", out var p) ? p.EnumerateArray().Select(r => r.GetDouble()).ToArray() : new double[dates.Length];
            var days = dates.Select((date, i) => new ForecastDay(date, min[i], max[i], rain[i])).ToArray();
            return new Forecast(place, source, days, null, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            var reason = e is TaskCanceledException ? "timed out" : e.GetBaseException().Message;
            logger.LogWarning("Weather forecast from {Source} failed: {Reason}", source, reason);
            return new Forecast(place, source, [], $"no forecast from {source}: {reason}", DateTimeOffset.UtcNow);
        }
    }
}

record ForecastDay(string Date, double MinCelsius, double MaxCelsius, double PrecipitationMm);

record Forecast(string Place, string? Source, ForecastDay[] Days, string? Error, DateTimeOffset FetchedAt)
{
    public string ToHtml() => Error is not null
        ? $"<p class=\"weather error\">Weather in {WebUtility.HtmlEncode(Place)}: {WebUtility.HtmlEncode(Error)}</p>"
        : $"<p class=\"weather\">Weather in {WebUtility.HtmlEncode(Place)} (from <code>{WebUtility.HtmlEncode(Source)}</code>): "
          + string.Join(" &middot; ", Days.Select(d => $"{WebUtility.HtmlEncode(d.Date)} {d.MinCelsius:0.#}&hellip;{d.MaxCelsius:0.#} &deg;C, {d.PrecipitationMm:0.#} mm"))
          + "</p>";
}
