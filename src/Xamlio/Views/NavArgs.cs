using System.Collections.Generic;
using Stremio.Core.Addons;

namespace Xamlio.Views
{
    public class MetaArgs
    {
        public string Type { get; set; }
        public string Id { get; set; }
    }

    public class StreamsArgs
    {
        public string Type { get; set; }
        public string VideoId { get; set; }
        public string MetaId { get; set; }
        public string Title { get; set; }
        public string Background { get; set; }
        public string WatchStatus { get; set; }
    }

    public class PlayerArgs
    {
        public string Url { get; set; }
        public string VideoId { get; set; }
        public string Type { get; set; }
        public string MetaId { get; set; }
        public string Title { get; set; }
        public string Background { get; set; }
        public IDictionary<string, string> Headers { get; set; }
        public IList<Subtitle> Subtitles { get; set; }

        // Taken from the stream's name/title; libVLC 3 doesn't report the transfer function.
        public bool LooksHdr { get; set; }
    }
}
