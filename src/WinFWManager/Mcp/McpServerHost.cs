using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinFWManager.Core.Services;
using WinFWManager.ViewModels;

namespace WinFWManager.Mcp;

/// <summary>
/// Hosts the MCP endpoint inside the WPF process, so tools can read live UI state.
///
/// WinFW Manager runs elevated, which makes an in-process HTTP listener a real
/// privilege-escalation surface. It is therefore off until the user starts it, bound to
/// the loopback address only, and every request must carry a bearer token generated for
/// this run and shown in the GUI. The exposed tools are read plus filter/tab control —
/// there is no firewall-write surface.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int DefaultPort = 7337;

    private readonly IServiceProvider _appServices;
    private WebApplication? _app;

    public McpServerHost(IServiceProvider appServices) => _appServices = appServices;

    public bool IsRunning => _app != null;
    public McpEndpointInfo? Endpoint { get; private set; }

    public async Task<McpEndpointInfo> StartAsync(int port = DefaultPort)
    {
        if (_app != null && Endpoint != null)
            return Endpoint;

        var token = McpEndpointInfo.NewToken();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        // Loopback only — never expose this on a routable address.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        // Bridge the WPF container: the tools operate on the very same ViewModel
        // instances the windows are bound to, which is what makes "what the user sees"
        // answerable rather than a re-query.
        builder.Services.AddSingleton(_appServices.GetRequiredService<MainViewModel>());
        builder.Services.AddSingleton(_appServices.GetRequiredService<TrafficMonitorViewModel>());
        builder.Services.AddSingleton(_appServices.GetRequiredService<DashboardViewModel>());
        builder.Services.AddSingleton(_appServices.GetRequiredService<RulesManagerViewModel>());
        builder.Services.AddSingleton(_appServices.GetRequiredService<INetworkInterfaceService>());

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "winfw-manager", Version = "1.1.0" };
            })
            .WithHttpTransport()
            .WithTools<WinFwTools>();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!IsAuthorised(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing or invalid bearer token.");
                return;
            }

            await next();
        });

        app.MapMcp("/mcp");

        await app.StartAsync();

        _app = app;
        Endpoint = new McpEndpointInfo(port, token);
        return Endpoint;
    }

    private static bool IsAuthorised(HttpContext context, string token)
    {
        // Loopback-only binding already excludes remote callers; the token stops other
        // local processes from driving an elevated app.
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var presented = header.AsSpan(prefix.Length).Trim();
        return CryptographicEquals(presented, token);
    }

    private static bool CryptographicEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length) return false;

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];

        return diff == 0;
    }

    public async Task StopAsync()
    {
        if (_app == null) return;

        var app = _app;
        _app = null;
        Endpoint = null;

        await app.StopAsync();
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
