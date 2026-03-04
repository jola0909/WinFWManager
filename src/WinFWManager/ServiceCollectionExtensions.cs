using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WinFWManager.Core.Services;

namespace WinFWManager;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWinFWManagerServices(this IServiceCollection services)
    {
        services.AddSingleton<IProcessResolver>(new ProcessResolver());
        services.AddSingleton<IGeoIpResolver>(sp =>
        {
            var mmdbPath = Path.Combine(AppContext.BaseDirectory, "GeoLite2-City.mmdb");
            return new GeoIpResolver(File.Exists(mmdbPath) ? mmdbPath : null);
        });
        services.AddSingleton<INetworkInterfaceService, NetworkInterfaceService>();
        services.AddSingleton<IFirewallLogParser, FirewallLogParser>();
        services.AddSingleton<IFirewallRuleService, FirewallRuleService>();
        services.AddSingleton<IEtwTrafficMonitor, EtwTrafficMonitor>();
        return services;
    }
}
