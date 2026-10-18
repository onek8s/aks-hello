using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateSlimBuilder(args);
// Two plain-HTTP ports (design D69): 8080 speaks HTTP/1.1 - the kubelet's probes until Kubernetes 1.38 -
// and 8081 HTTP/2 only, cleartext with prior knowledge (h2c), for the gateway. Kestrel's default,
// Http1AndHttp2, is HTTP/1.1 alone on a port without TLS (no ALPN to agree on HTTP/2), so a gateway
// speaking h2c to it fails - which 0.5.1 did behind Traefik (500 on every route, 2026-10-18).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.ListenAnyIP(8081, listen => listen.Protocols = HttpProtocols.Http2);
});
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
// Keeps the forecast fresh whether anyone looks at the page or not: the page never waits for the internet.
builder.Services.AddHostedService<WeatherRefresher>();
// The public IP the node reaches the internet from (PUBLIC_IP_URL), checked once when the pod starts and
// kept in memory: the second outbound dependency, and what tells a visitor which address the platform's
// egress (NAT gateway, egress gateway, or the node itself) gave this pod.
builder.Services.AddHttpClient("public-ip", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("aks-hello (+https://github.com/onek8s/aks-hello)");
});
builder.Services.AddSingleton<PublicIp>();
builder.Services.AddHostedService<PublicIpChecker>();

var app = builder.Build();
var started = DateTimeOffset.UtcNow;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

// The cluster the pod runs in, handed down by the deployment (CLUSTER_NAME). With the same hostname
// served by several clusters at once, it is what tells a visitor which one answered.
var cluster = Environment.GetEnvironmentVariable("CLUSTER_NAME") is { Length: > 0 } name ? name : "unknown";

app.MapGet("/", async (Weather weather, PublicIp publicIp, CancellationToken cancellationToken) => Results.Content($$"""
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
        .public-ip.error { color: #ab091e; }
      </style>
    </head>
    <body>
      <main>
        <h1>Hello, world!</h1>
        <p class="cluster">Cluster <code>{{WebUtility.HtmlEncode(cluster)}}</code></p>
        <p>Served by .NET {{WebUtility.HtmlEncode(Environment.Version.ToString())}} from pod <code>{{WebUtility.HtmlEncode(Environment.MachineName)}}</code>.</p>
        <p>Version <code>{{WebUtility.HtmlEncode(version)}}</code></p>
        {{publicIp.Current.ToHtml()}}
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

// The node's public IP as JSON: what was found when the pod started, or why nothing was.
app.MapGet("/public-ip", (PublicIp publicIp) => publicIp.Current);

// What is running where, for people and scripts: the same facts as the page, as JSON.
// Protocol is what the caller spoke to this pod - "HTTP/2" when the gateway connects over h2c as the
// platform requires (no HTTP/1.1 upstream), "HTTP/1.1" from the kubelet's probes or an old proxy.
app.MapGet("/status", (HttpContext context, IHostApplicationLifetime lifetime, PublicIp publicIp) => new Status(
    Name: "aks-hello",
    Version: version,
    Cluster: cluster,
    Node: Environment.GetEnvironmentVariable("NODE_NAME") is { Length: > 0 } node ? node : "unknown",
    NodePublicIp: publicIp.Current.Address,
    Pod: Environment.MachineName,
    Runtime: $".NET {Environment.Version}",
    Protocol: context.Request.Protocol,
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
    string? NodePublicIp,
    string Pod,
    string Runtime,
    string Protocol,
    string Architecture,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    bool Ready);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(Forecast))]
[JsonSerializable(typeof(PublicIpResult))]
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

// Refreshes the cached forecast every minute (the cache decides whether that fetches anything).
sealed class WeatherRefresher(Weather weather) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await weather.GetAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

// The public IP the node reaches the internet from, as an "echo my address" service sees it: by default
// api.ipify.org (no API key), or whatever PUBLIC_IP_URL names that answers a plain-text IP address. Found
// once when the pod starts and kept in memory - the address does not change for the life of a pod on a
// node, so nothing asks again; a pod that found nothing says so until it is restarted.
sealed class PublicIp(IHttpClientFactory httpClientFactory, ILogger<PublicIp> logger)
{
    readonly string? url = Environment.GetEnvironmentVariable("PUBLIC_IP_URL") is { Length: > 0 } value ? value
        : Environment.GetEnvironmentVariable("PUBLIC_IP_DISABLED") == "true" ? null
        : "https://api.ipify.org";

    public PublicIpResult Current { get; private set; } = new(null, null, "not checked yet", null);

    // One check: a few attempts a few seconds apart, for an egress that is not ready the moment the pod is.
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (url is null)
        {
            Current = new PublicIpResult(null, null, "the public IP check is turned off (PUBLIC_IP_DISABLED)", DateTimeOffset.UtcNow);
            return;
        }
        var source = new Uri(url).Host;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await httpClientFactory.CreateClient("public-ip").GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (!IPAddress.TryParse(text, out var address))
                    throw new FormatException($"not an IP address: \"{(text.Length > 40 ? text[..40] + "..." : text)}\"");
                Current = new PublicIpResult(address.ToString(), source, null, DateTimeOffset.UtcNow);
                logger.LogInformation("Node public IP {Address} (from {Source})", address, source);
                return;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested
                && e is HttpRequestException or TaskCanceledException or FormatException)
            {
                var reason = e is TaskCanceledException ? "timed out" : e.GetBaseException().Message;
                if (attempt < 3)
                {
                    logger.LogWarning("Node public IP from {Source} failed (attempt {Attempt} of 3): {Reason}", source, attempt, reason);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }
                logger.LogWarning("Node public IP from {Source} failed: {Reason}", source, reason);
                Current = new PublicIpResult(null, source, $"no public IP from {source}: {reason}", DateTimeOffset.UtcNow);
                return;
            }
        }
    }
}

record PublicIpResult(string? Address, string? Source, string? Error, DateTimeOffset? CheckedAt)
{
    public string ToHtml() => Error is not null
        ? $"<p class=\"public-ip error\">Node public IP: {WebUtility.HtmlEncode(Error)}</p>"
        : $"<p class=\"public-ip\">Node public IP <code>{WebUtility.HtmlEncode(Address)}</code> (from <code>{WebUtility.HtmlEncode(Source)}</code>, checked when the pod started)</p>";
}

// Runs the one public IP check when the pod starts. In the background: the app serves, and is ready,
// without waiting for it, and the page shows "not checked yet" for the moment it takes.
sealed class PublicIpChecker(PublicIp publicIp) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await publicIp.CheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
