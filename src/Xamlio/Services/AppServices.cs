using System;
using System.Net.Http;
using System.Threading.Tasks;
using Stremio.Core.Addons;
using Stremio.Core.Api;
using Stremio.Core.Settings;

namespace Xamlio.Services
{
    // App-wide singletons. Small enough that a DI container isn't worth it.
    public static class AppServices
    {
        private static Task _loadTask;

        static AppServices()
        {
            StremioApi.LogWriter = AppLog.WriteLine;
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Xamlio/1.0");
            Settings = new AppSettings(new LocalSettingsStore());
            Api = new StremioApi(Http);
            Addons = new AddonManager(new AddonClient(Http), Api, Settings);
        }

        public static HttpClient Http { get; }
        public static AppSettings Settings { get; }
        public static StremioApi Api { get; }
        public static AddonManager Addons { get; }

        // Loads addons once; pages await this before querying.
        public static Task EnsureAddonsLoadedAsync()
        {
            if (_loadTask == null || _loadTask.IsFaulted)
                _loadTask = Addons.LoadAsync();
            return _loadTask;
        }

        // Call after login/logout or adding an addon.
        public static Task ReloadAddonsAsync()
        {
            _loadTask = Addons.LoadAsync();
            return _loadTask;
        }
    }
}
