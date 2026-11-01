using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Stremio.Core.Addons;

namespace Xamlio.Views
{
    public static class WatchStateHelper
    {
        public static List<Video> OrderEpisodes(IEnumerable<Video> videos)
        {
            return (videos ?? Enumerable.Empty<Video>())
                .Where(video => video.Season.HasValue && video.Episode.HasValue)
                .OrderBy(video => video.Season.Value)
                .ThenBy(video => video.Episode.Value)
                .ToList();
        }

        public static HashSet<string> Decode(
            string value,
            IList<Video> videos)
        {
            var watched = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(value))
                return watched;

            try
            {
                var parts = value.Split(new[] { ':' }, 5);
                if (parts.Length != 5 ||
                    !int.TryParse(parts[3], out var bitCount))
                {
                    return watched;
                }

                var compressed = Convert.FromBase64String(parts[4]);
                var deflateBytes = RemoveZlibWrapper(compressed);
                using (var input = new MemoryStream(deflateBytes))
                using (var deflate = new DeflateStream(
                    input,
                    CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    var bytes = output.ToArray();

                    for (var i = 0;
                         i < bitCount && i < videos.Count;
                         i++)
                    {
                        if (i / 8 < bytes.Length &&
                            (bytes[i / 8] & (1 << (i % 8))) != 0)
                        {
                            watched.Add(videos[i].Id);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Invalid or legacy data is treated as unwatched.
            }

            return watched;
        }

        public static string Encode(
            IList<Video> videos,
            IEnumerable<string> watchedVideoIds,
            Video selectedVideo,
            bool shouldBeWatched)
        {
            var watched = new HashSet<string>(
                watchedVideoIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (shouldBeWatched)
                watched.Add(selectedVideo.Id);
            else
                watched.Remove(selectedVideo.Id);

            var bytes = new byte[(videos.Count + 7) / 8];
            for (var i = 0; i < videos.Count; i++)
            {
                if (watched.Contains(videos[i].Id))
                    bytes[i / 8] |= (byte)(1 << (i % 8));
            }

            return selectedVideo.Id + ":" + videos.Count + ":" +
                Compress(bytes);
        }

        public static string SetVideo(
            string currentValue,
            IList<Video> videos,
            Video selectedVideo,
            bool shouldBeWatched)
        {
            var selectedIndex = -1;
            for (var i = 0; i < videos.Count; i++)
            {
                if (string.Equals(
                    videos[i].Id,
                    selectedVideo.Id,
                    StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex < 0)
                return currentValue;

            var bitCount = selectedIndex + 1;
            var bytes = new byte[Math.Max(2, (bitCount + 7) / 8)];

            if (TryDecodeBits(currentValue, out var existingBitCount,
                out var existingBytes))
            {
                // Preserve the existing bits, but do not preserve trailing
                // metadata count. The serialized byte buffer itself must be
                // preserved because Stremio keeps its existing capacity.
                bytes = new byte[Math.Max(
                    existingBytes.Length,
                    (bitCount + 7) / 8)];
                Buffer.BlockCopy(
                    existingBytes,
                    0,
                    bytes,
                    0,
                    Math.Min(existingBytes.Length, bytes.Length));
            }

            if (shouldBeWatched)
                bytes[selectedIndex / 8] |=
                    (byte)(1 << (selectedIndex % 8));
            else
                bytes[selectedIndex / 8] &=
                    (byte)~(1 << (selectedIndex % 8));

            var highestWatchedBit = -1;
            for (var i = bytes.Length * 8 - 1; i >= 0; i--)
            {
                if ((bytes[i / 8] & (1 << (i % 8))) != 0)
                {
                    highestWatchedBit = i;
                    break;
                }
            }

            bitCount = highestWatchedBit >= 0
                ? highestWatchedBit + 1
                : selectedIndex + 1;

            var anchorVideoId = selectedVideo.Id;
            if (highestWatchedBit >= 0 &&
                highestWatchedBit < videos.Count)
            {
                anchorVideoId = videos[highestWatchedBit].Id;
            }

            return anchorVideoId + ":" + bitCount + ":" +
                Compress(bytes);
        }

        private static string Compress(byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(
                    output,
                    CompressionMode.Compress,
                    true))
                {
                    deflate.Write(bytes, 0, bytes.Length);
                }

                var deflateBytes = output.ToArray();
                var zlib = new byte[deflateBytes.Length + 6];
                zlib[0] = 0x78;
                zlib[1] = 0x9C;
                Buffer.BlockCopy(deflateBytes, 0, zlib, 2,
                    deflateBytes.Length);

                var checksum = Adler32(bytes);
                zlib[zlib.Length - 4] = (byte)(checksum >> 24);
                zlib[zlib.Length - 3] = (byte)(checksum >> 16);
                zlib[zlib.Length - 2] = (byte)(checksum >> 8);
                zlib[zlib.Length - 1] = (byte)checksum;

                return Convert.ToBase64String(zlib);
            }
        }

        private static bool TryDecodeBits(
            string value,
            out int bitCount,
            out byte[] bytes)
        {
            bitCount = 0;
            bytes = null;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            try
            {
                var parts = value.Split(new[] { ':' }, 5);
                if (parts.Length != 5 ||
                    !int.TryParse(parts[3], out bitCount))
                {
                    return false;
                }

                var compressed = Convert.FromBase64String(parts[4]);
                var deflateBytes = RemoveZlibWrapper(compressed);
                using (var input = new MemoryStream(deflateBytes))
                using (var deflate = new DeflateStream(
                    input,
                    CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    bytes = output.ToArray();
                    return true;
                }
            }
            catch (Exception)
            {
                bitCount = 0;
                bytes = null;
                return false;
            }
        }

        private static byte[] RemoveZlibWrapper(byte[] bytes)
        {
            if (bytes.Length >= 6 &&
                bytes[0] == 0x78)
            {
                var raw = new byte[bytes.Length - 6];
                Buffer.BlockCopy(bytes, 2, raw, 0, raw.Length);
                return raw;
            }

            return bytes;
        }

        private static uint Adler32(byte[] bytes)
        {
            const uint Mod = 65521;
            uint a = 1;
            uint b = 0;

            foreach (var value in bytes)
            {
                a = (a + value) % Mod;
                b = (b + a) % Mod;
            }

            return (b << 16) | a;
        }
    }
}
