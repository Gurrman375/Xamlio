using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Stremio.Core.Addons
{
    public class AddonClient
    {
        private readonly HttpClient _http;

        public AddonClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<Manifest> GetManifestAsync(string transportUrl, CancellationToken ct = default(CancellationToken))
        {
            var addon = new InstalledAddon(transportUrl, null);
            return await GetJsonAsync<Manifest>(addon.BaseUrl + "/manifest.json", ct).ConfigureAwait(false);
        }

        public async Task<List<MetaPreview>> GetCatalogAsync(InstalledAddon addon, string type, string id,
            IDictionary<string, string> extra = null, CancellationToken ct = default(CancellationToken))
        {
            var response = await GetJsonAsync<CatalogResponse>(
                BuildUrl(addon, "catalog", type, id, extra), ct).ConfigureAwait(false);
            return response?.Metas ?? new List<MetaPreview>();
        }

        public async Task<MetaDetail> GetMetaAsync(InstalledAddon addon, string type, string id,
            CancellationToken ct = default(CancellationToken))
        {
            var response = await GetJsonAsync<MetaResponse>(
                BuildUrl(addon, "meta", type, id, null), ct).ConfigureAwait(false);
            return response?.Meta;
        }

        public async Task<List<Stream>> GetStreamsAsync(
            InstalledAddon addon,
            string type,
            string id,
            CancellationToken ct = default(CancellationToken))
        {
            var response = await GetJsonAsync<StreamsResponse>(
                BuildUrl(addon, "stream", type, id, null),
                ct).ConfigureAwait(false);

            var streams = response?.Streams ?? new List<Stream>();

            foreach (var stream in streams)
            {
                stream.AddonName = addon.Manifest?.Name;
                stream.AddonUrl = addon.TransportUrl;
            }

            return streams;
        }

        public async Task<List<Subtitle>> GetSubtitlesAsync(InstalledAddon addon, string type, string id,
            CancellationToken ct = default(CancellationToken))
        {
            var response = await GetJsonAsync<SubtitlesResponse>(
                BuildUrl(addon, "subtitles", type, id, null), ct).ConfigureAwait(false);
            return response?.Subtitles ?? new List<Subtitle>();
        }

        // {base}/{resource}/{type}/{id}[/{k=v&k=v}].json
        public static string BuildUrl(InstalledAddon addon, string resource, string type, string id,
            IDictionary<string, string> extra)
        {
            var url = addon.BaseUrl + "/" + resource + "/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id);
            if (extra != null && extra.Count > 0)
                url += "/" + string.Join("&", extra.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
            return url + ".json";
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
        {
            using (var response = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    return null;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<T>(body);
            }
        }
    }
}
