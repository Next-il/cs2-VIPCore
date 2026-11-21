using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.DependencyInjection;
using VIPCore.Configs;
using VIPCore.Player;
using VIPCore.Services;

namespace VIPCore;

public class VipServiceCollection : IPluginServiceCollection<Plugin>
{
    public void ConfigureServices(IServiceCollection serviceCollection)
    {
        var configSystem = new ConfigSystem(serviceCollection);

        // Next-il: no LegacyConfigMigration.Run() - it rewrites vip.json and vip_core.json on load, and those are one
        // shared file per node in our Mounts repo, so a restart wave of servers would all rewrite them at once. Our
        // configs are converted in the repo instead.

        configSystem
            .AddConfig<VipConfig>("vip_core")
            .AddConfig<GroupsConfig>("vip");

        serviceCollection.AddSingleton<FeatureManager>();
        serviceCollection.AddSingleton<CookieService>();
        serviceCollection.AddSingleton<DatabaseProvider>();
        serviceCollection.AddSingleton<DatabaseService>();
        serviceCollection.AddSingleton<PlayersManager>();
        serviceCollection.AddSingleton<VipCoreApi>();
        serviceCollection.AddSingleton<MenuManager>();
        serviceCollection.AddSingleton<CommandsService>();
    }
}
