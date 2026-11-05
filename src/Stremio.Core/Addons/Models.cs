using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Stremio.Core.Addons
{
    // Models follow the Stremio addon protocol:
    // https://github.com/Stremio/stremio-addon-sdk/tree/master/docs/api

    public class Manifest
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("logo")] public string Logo { get; set; }
        [JsonProperty("types")] public List<string> Types { get; set; } = new List<string>();
        [JsonProperty("idPrefixes")] public List<string> IdPrefixes { get; set; }
        [JsonProperty("catalogs")] public List<CatalogDescriptor> Catalogs { get; set; } = new List<CatalogDescriptor>();

        // Each entry is either a plain string ("stream") or an object with its own types/idPrefixes.
        [JsonProperty("resources")] public List<JToken> Resources { get; set; } = new List<JToken>();
    }

    public class CatalogDescriptor
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("extra")] public List<ExtraProperty> Extra { get; set; } = new List<ExtraProperty>();

        // Legacy manifests list required extras separately.
        [JsonProperty("extraRequired")] public List<string> ExtraRequired { get; set; }
        [JsonProperty("extraSupported")] public List<string> ExtraSupported { get; set; }
    }

    public class ExtraProperty
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("isRequired")] public bool IsRequired { get; set; }
        [JsonProperty("options")] public List<string> Options { get; set; }
    }

    public class MetaPreview
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("poster")] public string Poster { get; set; }
        [JsonProperty("background")] public string Background { get; set; }
        [JsonProperty("logo")] public string Logo { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("releaseInfo")] public string ReleaseInfo { get; set; }
        [JsonProperty("imdbRating")] public string ImdbRating { get; set; }
    }

    public class MetaDetail : MetaPreview
    {
        [JsonProperty("genres")] public List<string> Genres { get; set; } = new List<string>();
        [JsonProperty("runtime")] public string Runtime { get; set; }
        [JsonProperty("videos")] public List<Video> Videos { get; set; } = new List<Video>();
    }

    public class Video
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("season")] public int? Season { get; set; }
        [JsonProperty("episode")] public int? Episode { get; set; }
        [JsonProperty("released")] public string Released { get; set; }
        [JsonProperty("thumbnail")] public string Thumbnail { get; set; }
        [JsonProperty("overview")] public string Overview { get; set; }

        [JsonIgnore]
        public string DisplayTitle
        {
            get
            {
                var title = Title ?? Name ?? Id;
                return Episode.HasValue ? Episode.Value + ". " + title : title;
            }
        }
    }

    public class Stream
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("ytId")] public string YtId { get; set; }
        [JsonProperty("infoHash")] public string InfoHash { get; set; }
        [JsonProperty("fileIdx")] public int? FileIdx { get; set; }
        [JsonProperty("externalUrl")] public string ExternalUrl { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("sources")] public List<string> Sources { get; set; }
        [JsonProperty("subtitles")] public List<Subtitle> Subtitles { get; set; }
        [JsonProperty("behaviorHints")] public StreamBehaviorHints BehaviorHints { get; set; }

        // Filled in by the client, not part of the protocol.
        [JsonIgnore] public string AddonName { get; set; }
        [JsonIgnore] public string AddonUrl { get; set; }

        [JsonIgnore]
        public string Label
        {
            get { return Description ?? Title ?? Name ?? Url ?? InfoHash; }
        }
    }

    public class StreamBehaviorHints
    {
        [JsonProperty("notWebReady")] public bool NotWebReady { get; set; }
        [JsonProperty("bingeGroup")] public string BingeGroup { get; set; }
        [JsonProperty("filename")] public string Filename { get; set; }
        [JsonProperty("proxyHeaders")] public ProxyHeaders ProxyHeaders { get; set; }
    }

    public class ProxyHeaders
    {
        [JsonProperty("request")] public Dictionary<string, string> Request { get; set; }
    }

    public class Subtitle
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("lang")] public string Lang { get; set; }
    }

    internal class CatalogResponse
    {
        [JsonProperty("metas")] public List<MetaPreview> Metas { get; set; } = new List<MetaPreview>();
    }

    internal class MetaResponse
    {
        [JsonProperty("meta")] public MetaDetail Meta { get; set; }
    }

    internal class StreamsResponse
    {
        [JsonProperty("streams")] public List<Stream> Streams { get; set; } = new List<Stream>();
    }

    internal class SubtitlesResponse
    {
        [JsonProperty("subtitles")] public List<Subtitle> Subtitles { get; set; } = new List<Subtitle>();
    }
}
