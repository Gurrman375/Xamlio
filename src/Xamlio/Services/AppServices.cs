using System;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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

        public static async Task<string> GetTmdbBackgroundAsync(
            string imdbId, string fallback)
        {
            var apiKey = Settings.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(imdbId))
                return fallback;

            try
            {
                var url = "https://api.themoviedb.org/3/find/" +
                    Uri.EscapeDataString(imdbId) +
                    "?api_key=" + Uri.EscapeDataString(apiKey) +
                    "&external_source=imdb_id&language=en-US";
                var response = await Http.GetStringAsync(url);
                var json = JObject.Parse(response);
                var result = (json["movie_results"] as JArray)?.FirstOrDefault() ??
                    (json["tv_results"] as JArray)?.FirstOrDefault();
                var path = result?["backdrop_path"]?.Value<string>();
                return string.IsNullOrWhiteSpace(path)
                    ? fallback
                    : "https://image.tmdb.org/t/p/w1280" + path;
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("TMDB background lookup failed: " + ex.Message);
                return fallback;
            }
        }

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
