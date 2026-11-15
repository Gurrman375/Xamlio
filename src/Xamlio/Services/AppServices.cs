using System;
using System.Collections.Generic;
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

        public static async Task<List<string>> GetTmdbBackgroundsAsync(
            string imdbId, string fallback)
        {
            var backgrounds = new List<string>();
            var apiKey = Settings.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(imdbId))
            {
                if (!string.IsNullOrWhiteSpace(fallback))
                    backgrounds.Add(fallback);
                return backgrounds;
            }

            try
            {
                var findUrl = "https://api.themoviedb.org/3/find/" +
                    Uri.EscapeDataString(imdbId) +
                    "?api_key=" + Uri.EscapeDataString(apiKey) +
                    "&external_source=imdb_id&language=en-US";
                var findJson = JObject.Parse(await Http.GetStringAsync(findUrl));
                var movie = (findJson["movie_results"] as JArray)?.FirstOrDefault();
                var tv = (findJson["tv_results"] as JArray)?.FirstOrDefault();
                var result = movie ?? tv;
                var mediaType = movie != null ? "movie" : "tv";
                var tmdbId = result?["id"]?.Value<int?>();

                if (tmdbId.HasValue)
                {
                    var imagesUrl = "https://api.themoviedb.org/3/" +
                        mediaType + "/" + tmdbId.Value + "/images?api_key=" +
                        Uri.EscapeDataString(apiKey);
                    var imagesJson = JObject.Parse(await Http.GetStringAsync(imagesUrl));
                    var backdrops = imagesJson["backdrops"] as JArray;
                    if (backdrops != null)
                    {
                        backgrounds.AddRange(backdrops
                            .OrderByDescending(item =>
                                (item["width"]?.Value<long>() ?? 0) *
                                (item["height"]?.Value<long>() ?? 0))
                            .Select(item => item["file_path"]?.Value<string>())
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .Select(path => "https://image.tmdb.org/t/p/original" + path)
                            .Take(12));
                    }
                }

                if (backgrounds.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
                    backgrounds.Add(fallback);
                return backgrounds;
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("TMDB background lookup failed: " + ex.Message);
                if (!string.IsNullOrWhiteSpace(fallback))
                    backgrounds.Add(fallback);
                return backgrounds;
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
