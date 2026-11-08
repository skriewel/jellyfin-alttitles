using Jellyfin.Plugin.AltTitles.Injection;
using Jellyfin.Plugin.AltTitles.Titles;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AltTitles;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        serviceCollection.AddSingleton<AltTitleStore>();
        serviceCollection.AddSingleton<AniDbTitles>();
        serviceCollection.AddSingleton<TitleCollector>();

        // AltTitleSearchProvider is found by Jellyfin itself (it implements ISearchProvider) and gets the store injected
    }
}
