using System.Net.Http;
using System.Net.Http.Headers;
using ScreenCord.Local;
using ScreenCord.Security;
using ScreenCord.Services;
using ScreenCord.ViewModels;
using ScreenCord.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScreenCord(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DiscordOptions>(configuration.GetSection(DiscordOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.AddTransient<RateLimitHandler>(_ => new RateLimitHandler());
        services.AddHttpClient(DiscordHttp.ClientName, client =>
            {
                client.BaseAddress = DiscordHttp.BaseAddress;
                client.Timeout = TimeSpan.FromSeconds(20);
                // Formato exigido pela referência da API: "DiscordBot ($url, $versionNumber)".
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://localhost/screencord, 1.0.0)");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            // Tokens trafegam somente para discord.com: redirecionamentos não são seguidos.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<RateLimitHandler>();

        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<IAuthorizationCallbackListener, LoopbackCallbackListener>();
        services.AddSingleton<ITokenStore>(_ => new DpapiTokenStore());
        services.AddSingleton<IDiscordOAuthService, DiscordOAuthService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IDiscordApiClient, DiscordApiClient>();
        services.AddSingleton<IProfileService, ProfileService>();

        // API central do ScreenCord: usuários do sistema, amizades, grupos e a conexão em tempo real.
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
        services.AddHttpClient(CentralApiClient.ClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.AddSingleton<CentralApiClient>();
        services.AddSingleton<IFriendsService, CentralFriendsService>();
        services.AddSingleton<IGroupsService, CentralGroupsService>();
        services.AddSingleton<GroupsRealtimeClient>();
        services.AddSingleton(sp => new ApiRealtime(sp.GetRequiredService<GroupsRealtimeClient>()));
        services.AddSingleton<IServerLimits, CentralServerLimits>();
        services.AddSingleton<IApiHealthCheck, CentralApiHealthCheck>();

        // Salas locais: sem API, com a mídia direto entre os computadores. A tela de grupos usa a conexão que junta as duas.
        services.AddSingleton<ILocalIdentityStore, LocalIdentityStore>();
        services.AddSingleton(sp => new LocalRooms(sp.GetRequiredService<ILocalIdentityStore>(), sp.GetRequiredService<ILogger<LocalRooms>>()));
        services.AddSingleton<ILocalRooms>(sp => sp.GetRequiredService<LocalRooms>());
        services.AddSingleton<IFirewallRule, FirewallRule>();
        services.AddSingleton<IGroupsRealtime, RoutedGroupsRealtime>();

        // Atualização automática pelas GitHub Releases (o download segue o redirecionamento do GitHub para o CDN).
        services.AddHttpClient(AppUpdater.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ScreenCord/{AppVersion.Current}");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        });
        services.AddSingleton<IAppUpdater, AppUpdater>();
        services.AddSingleton<UpdateFlow>();
        services.AddSingleton<IUpdateFlow>(sp => sp.GetRequiredService<UpdateFlow>());

        // Compartilhamento de tela: captura, encoder e canal de mídia; as janelas ficam na camada de interface.
        services.AddSingleton<IScreenShareService, ScreenShareService>();
        services.AddSingleton<IScreenShareUi, ScreenShareUi>();

        // Os amigos vêm só da API: a conexão dela, sem as salas locais.
        services.AddSingleton(sp => new FriendsViewModel(sp.GetRequiredService<IFriendsService>(), sp.GetRequiredService<ApiRealtime>().Inner));
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton(sp => new AboutViewModel(
            sp.GetRequiredService<IApiHealthCheck>(), sp.GetRequiredService<ApiRealtime>().Inner, sp.GetService<IBrowserLauncher>(), sp.GetService<IUpdateFlow>()));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
