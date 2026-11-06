using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Stremio.Core.Settings
{
    // Platform-specific key/value storage (ApplicationData.LocalSettings on Xbox, a dictionary in tests).
    public interface ISettingsStore
    {
        string Get(string key);
        void Set(string key, string value);
    }

    public class AppSettings
    {
        public static readonly string[] DefaultAddonUrls =
        {
            "https://v3-cinemeta.strem.io/manifest.json",
            "https://opensubtitles-v3.strem.io/manifest.json",
            "https://watchhub.strem.io/manifest.json",
        };

        private readonly ISettingsStore _store;

        public AppSettings(ISettingsStore store)
        {
            _store = store;
        }

        public string AuthKey
        {
            get { return _store.Get("authKey"); }
            set { _store.Set("authKey", value); }
        }

        public string Email
        {
            get { return _store.Get("email"); }
            set { _store.Set("email", value); }
        }

        // e.g. http://192.168.1.20:11470 - a Stremio streaming server on another device, used for torrent streams.
        public string StreamingServerUrl
        {
            get { return _store.Get("streamingServerUrl"); }
            set { _store.Set("streamingServerUrl", string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }
        }

        public string TmdbApiKey
        {
            get { return _store.Get("tmdbApiKey"); }
            set { _store.Set("tmdbApiKey", string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }
        }

        public bool BlurUnwatchedEpisodeImages
        {
            get { return _store.Get("blurUnwatchedEpisodeImages") == "true"; }
            set { _store.Set("blurUnwatchedEpisodeImages", value ? "true" : "false"); }
        }

        public string PreferredSubtitleLanguage
        {
            get { return _store.Get("subtitleLang") ?? "eng"; }
            set { _store.Set("subtitleLang", value); }
        }

        public string PreferredAudioLanguage
        {
            get { return _store.Get("audioLang") ?? "eng"; }
            set { _store.Set("audioLang", value); }
        }

        public bool SubtitlesEnabled
        {
            get { return _store.Get("subtitlesEnabled") == "true"; }
            set { _store.Set("subtitlesEnabled", value ? "true" : "false"); }
        }

        public bool PreferSignsAndSongs
        {
            get { return _store.Get("preferSignsAndSongs") == "true"; }
            set { _store.Set("preferSignsAndSongs", value ? "true" : "false"); }
        }

        public bool AudioPassthrough
        {
            get { return _store.Get("audioPassthrough") == "true"; }
            set { _store.Set("audioPassthrough", value ? "true" : "false"); }
        }

        public bool MatchDisplayMode
        {
            get { return _store.Get("matchDisplayMode") != "false"; }
            set { _store.Set("matchDisplayMode", value ? "true" : "false"); }
        }

        // Addons added by URL on the console, used in addition to the defaults when not logged in.
        public List<string> ExtraAddonUrls
        {
            get
            {
                var json = _store.Get("extraAddons");
                return json == null ? new List<string>() : JsonConvert.DeserializeObject<List<string>>(json);
            }
            set { _store.Set("extraAddons", JsonConvert.SerializeObject(value.Distinct().ToList())); }
        }

        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(AuthKey); }
        }

        public bool UseTvSafeMargin
        {
            get { return _store.Get("useTvSafeMargin") != "false"; }
            set { _store.Set("useTvSafeMargin", value ? "true" : "false"); }
        }
    }
}
