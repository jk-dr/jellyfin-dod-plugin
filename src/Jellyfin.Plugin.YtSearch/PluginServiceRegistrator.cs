using Jellyfin.Plugin.YtSearch.Middleware;
using Jellyfin.Plugin.YtSearch.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.YtSearch;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpService>();
        serviceCollection.AddSingleton<SearchService>();
        serviceCollection.AddHostedService<YtDlpUpdater>();
        serviceCollection.AddTransient<IStartupFilter, YtStartupFilter>();
    }

    private sealed class YtStartupFilter : IStartupFilter
    {
        public System.Action<IApplicationBuilder> Configure(System.Action<IApplicationBuilder> next) => app =>
        {
            app.UseMiddleware<SearchAppendMiddleware>();
            next(app);
        };
    }
}
