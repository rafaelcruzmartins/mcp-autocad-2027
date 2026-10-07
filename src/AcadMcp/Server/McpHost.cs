using System.Net;
using AcadMcp.Infrastructure;
using AcadMcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace AcadMcp.Server;

/// <summary>
/// Kestrel + official MCP SDK (Streamable HTTP) hosted inside acad.exe, bound to loopback only.
/// </summary>
internal static class McpHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static WebApplication? _app;

    public static int? Port { get; private set; }
    public static DateTime? StartedAt { get; private set; }
    public static bool IsRunning => _app is not null;
    public static string? Url => Port is { } p ? $"http://127.0.0.1:{p}/mcp" : null;

    public static async Task StartAsync(int port)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null) return;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = "AcadMcp",
                // Plugin folder, not acad.exe's: keeps config file watchers out of Program Files.
                ContentRootPath = Path.GetDirectoryName(typeof(McpHost).Assembly.Location),
            });

            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

            // We live inside acad.exe: no console lifetime (Ctrl+C / ProcessExit hooks), no console logging.
            builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider());
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new() { Name = "acad-mcp", Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
                    o.ServerInstructions =
                        "Controla o AutoCAD 2027 aberto nesta máquina. Coordenadas em unidades do desenho (WCS); " +
                        "consulte get_drawing_info para saber as unidades. Cada chamada de escrita é um grupo de undo.";
                })
                .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
                .WithToolsFromAssembly(typeof(McpHost).Assembly, Fmt.Options);

            var app = builder.Build();
            app.Use(LoopbackGuard);
            app.MapGet("/health", () => Results.Json(new
            {
                ok = true,
                server = "acad-mcp",
                endpoint = $"http://127.0.0.1:{port}/mcp",
                calls = ToolInvoker.Calls,
                errors = ToolInvoker.Errors,
            }));
            app.MapMcp("/mcp");

            await app.StartAsync().ConfigureAwait(false);

            _app = app;
            Port = port;
            StartedAt = DateTime.Now;
            Logger.Info($"Servidor MCP ouvindo em http://127.0.0.1:{port}/mcp");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is null) return;
            var app = _app;
            _app = null;
            Port = null;
            StartedAt = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            Logger.Info("Servidor MCP parado.");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Rejects requests whose Host or Origin is not loopback. Binding to 127.0.0.1 alone does not
    /// stop a web page in the user's browser from reaching us (DNS rebinding / cross-site POST),
    /// and execute_csharp is arbitrary code execution.
    /// </summary>
    private static async Task LoopbackGuard(HttpContext ctx, RequestDelegate next)
    {
        var host = ctx.Request.Host.Host;
        if (!IsLoopbackName(host))
        {
            Logger.Warn($"Requisição recusada: Host '{host}'");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !IsLoopbackName(o.Host)))
        {
            Logger.Warn($"Requisição recusada: Origin '{origin}'");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(ctx);
    }

    private static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "[::1]" or "::1";

    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
        public void Dispose() { }

        private sealed class FileLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var msg = $"[{category}] {formatter(state, exception)}";
                if (logLevel >= LogLevel.Error) Logger.Error(msg, exception);
                else Logger.Warn(exception is null ? msg : $"{msg} {exception.Message}");
            }
        }
    }
}
