using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stremio.Core.Api;
using Stremio.Core.Settings;

namespace Stremio.Core.Addons
{
    public class CatalogSource
    {
        public InstalledAddon Addon { get; set; }
        public CatalogDescriptor Catalog { get; set; }

        public string Title
        {
            get
            {
                var type = Catalog.Type == "series" ? "Series" : Catalog.Type == "movie" ? "Movies" : Catalog.Type;
                return (Catalog.Name ?? Catalog.Id) + " - " + type;
            }
        }

        public bool RequiresExtra
        {
            get
            {
                return (Catalog.Extra != null && Catalog.Extra.Any(e => e.IsRequired))
                    || (Catalog.ExtraRequired != null && Catalog.ExtraRequired.Count > 0);
            }
        }

        public bool SupportsSearch
        {
            get
            {
                return (Catalog.Extra != null && Catalog.Extra.Any(e => e.Name == "search"))
                    || (Catalog.ExtraSupported != null && Catalog.ExtraSupported.Contains("search"));
            }
        }
    }

    // Holds the installed addons and fans requests out to every addon that supports them,
    // the same way the official Stremio clients do.
    public class AddonManager
    {
        private readonly AddonClient _client;
        private readonly StremioApi _api;
        private readonly AppSettings _settings;

        public AddonManager(HttpClient http, AppSettings settings)
        {
            _client = new AddonClient(http);
            _api = new StremioApi(http);
            _settings = settings;
        }

        public AddonManager(AddonClient client, StremioApi api, AppSettings settings)
        {
            _client = client;
            _api = api;
            _settings = settings;
        }

        public IReadOnlyList<InstalledAddon> Addons { get; private set; } = new List<InstalledAddon>();

        public AddonClient Client
        {
            get { return _client; }
        }

        public async Task LoadAsync(CancellationToken ct = default(CancellationToken))
        {
            var addons = new List<InstalledAddon>();
            if (_settings.IsLoggedIn)
            {
                try
                {
                    addons.AddRange(await _api.GetAddonCollectionAsync(_settings.AuthKey, ct).ConfigureAwait(false));
                }
                catch (StremioApiException)
                {
                    // Expired session: fall back to the defaults below.
                    _settings.AuthKey = null;
                }
            }

            var urls = new List<string>();
            if (addons.Count == 0)
                urls.AddRange(AppSettings.DefaultAddonUrls);
            urls.AddRange(_settings.ExtraAddonUrls);

            var known = new HashSet<string>(addons.Select(a => a.TransportUrl), StringComparer.OrdinalIgnoreCase);
            var fetched = await Task.WhenAll(urls.Where(known.Add).Select(async url =>
            {
                try
                {
                    var manifest = await _client.GetManifestAsync(url, ct).ConfigureAwait(false);
                    return manifest == null ? null : new InstalledAddon(url, manifest);
                }
                catch (Exception)
                {
                    return null;
                }
            })).ConfigureAwait(false);
            addons.AddRange(fetched.Where(a => a != null));

            Addons = addons;
        }

        public IEnumerable<CatalogSource> GetCatalogs()
        {
            return Addons
                .Where(a => a.Manifest.Catalogs != null)
                .SelectMany(a => a.Manifest.Catalogs.Select(c => new CatalogSource { Addon = a, Catalog = c }));
        }

        public async Task<MetaDetail> GetMetaAsync(string type, string id, CancellationToken ct = default(CancellationToken))
        {
            foreach (var addon in Addons.Where(a => a.Supports("meta", type, id)))
            {
                try
                {
                    var meta = await _client.GetMetaAsync(addon, type, id, ct).ConfigureAwait(false);
                    if (meta != null)
                        return meta;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Try the next addon.
                }
            }
            return null;
        }

        public Task<List<Stream>> GetStreamsAsync(string type, string id, CancellationToken ct = default(CancellationToken))
        {
            return FanOutAsync("stream", type, id, a => _client.GetStreamsAsync(a, type, id, ct));
        }

        public async Task<List<Stream>> GetStreamsFromAddonAsync(
            string addonUrl,
            string type,
            string id,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(addonUrl))
                return new List<Stream>();

            var addon = Addons.FirstOrDefault(a =>
                string.Equals(a.TransportUrl, addonUrl, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.BaseUrl, addonUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

            if (addon == null || !addon.Supports("stream", type, id))
                return new List<Stream>();

            try
            {
                return await _client.GetStreamsAsync(addon, type, id, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new List<Stream>();
            }
        }

        public async Task<List<Stream>> GetStreamsIncrementalAsync(string type, string id, IProgress<List<Stream>> progress, CancellationToken ct = default(CancellationToken))
        {
            var allStreams = new List<Stream>();
            var sync = new object();

            var tasks = Addons
                .Where(a => a.Supports("stream", type, id))
                .Select(async addon =>
                {
                    try
                    {
                        var streams = await _client
                            .GetStreamsAsync(addon, type, id, ct)
                            .ConfigureAwait(false);

                        lock (sync)
                        {
                            allStreams.AddRange(streams);
                        }

                        progress?.Report(streams);
                    }
                    catch (Exception)
                    {
                        // One failing addon should not stop the others.
                    }
                });

            await Task.WhenAll(tasks);

            return allStreams;
        }

        public Task<List<Subtitle>> GetSubtitlesAsync(string type, string id, CancellationToken ct = default(CancellationToken))
        {
            return FanOutAsync("subtitles", type, id, a => _client.GetSubtitlesAsync(a, type, id, ct));
        }

        private async Task<List<T>> FanOutAsync<T>(string resource, string type, string id,
            Func<InstalledAddon, Task<List<T>>> fetch)
        {
            var results = await Task.WhenAll(Addons.Where(a => a.Supports(resource, type, id)).Select(async a =>
            {
                try
                {
                    return await fetch(a).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return new List<T>();
                }
            })).ConfigureAwait(false);
            return results.SelectMany(r => r).ToList();
        }
    }
}
