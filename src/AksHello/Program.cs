using System.Net;
using System.Reflection;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapGet("/", () => Results.Content($$"""
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Hello from AKS</title>
      <style>
        body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f5f7fa; color: #1f2933; }
        main { text-align: center; padding: 2rem; }
        h1 { font-size: 2.5rem; margin-bottom: .5rem; }
        p { color: #52606d; }
        code { background: #e4e7eb; padding: .1rem .4rem; border-radius: 4px; }
      </style>
    </head>
    <body>
      <main>
        <h1>Hello, world!</h1>
        <p>Served by .NET {{WebUtility.HtmlEncode(Environment.Version.ToString())}} from pod <code>{{WebUtility.HtmlEncode(Environment.MachineName)}}</code>.</p>
        <p>Version <code>{{WebUtility.HtmlEncode(version)}}</code></p>
      </main>
    </body>
    </html>
    """, "text/html; charset=utf-8"));

// Liveness and readiness probes.
app.MapHealthChecks("/healthz");

app.Run();
