using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.AniBridge.Extractors;
using Jellyfin.Plugin.AniBridge.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniBridge;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    private const int HttpClientTimeoutSeconds = 50;

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("AniWorld", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("STO", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("Anikoto", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);
        serviceCollection.AddHttpClient("AnimeNexus", c => c.Timeout = TimeSpan.FromSeconds(HttpClientTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler);

        // Register each site adapter as itself (used directly by the download pipeline) and
        // again as the shared StreamingSiteService base (so the controller and download
        // service can resolve the full set via IEnumerable<StreamingSiteService> without
        // knowing about any specific site). Adding a new site adapter later only requires two
        // lines here — no controller/config changes needed.
        AddSite<AniWorldService>(serviceCollection);
        AddSite<StoService>(serviceCollection);
        AddSite<AnikotoService>(serviceCollection);
        AddSite<AnimeNexusService>(serviceCollection);

        serviceCollection.AddSingleton<DownloadHistoryService>();
        serviceCollection.AddSingleton<DownloadService>();
        serviceCollection.AddSingleton<IStreamExtractor, VoeExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidozaExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, VidmolyExtractor>();
        serviceCollection.AddSingleton<IStreamExtractor, FilemoonExtractor>();
    }

    private static void AddSite<TService>(IServiceCollection serviceCollection)
        where TService : Services.StreamingSiteService
    {
        serviceCollection.AddSingleton<TService>();
        serviceCollection.AddSingleton<Services.StreamingSiteService>(sp => sp.GetRequiredService<TService>());
    }

    private static HttpMessageHandler ConfigureHandler(IServiceProvider _)
    {
        var proxyUrl = Plugin.Instance?.Configuration?.ProxyUrl;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            var proxyUri = new Uri(proxyUrl);
            var isSocks = proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

            if (isSocks)
            {
                return new SocketsHttpHandler
                {
                    Proxy = new WebProxy(proxyUri),
                    UseProxy = true,
                };
            }

            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
            };
            return handler;
        }

        return new HttpClientHandler();
    }
}
