using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display.Core;

namespace Xamlio.Services
{
    // Switches the console's HDMI output to match the video (4K, HDR10, frame rate), the way
    // the built-in media apps do. Only does anything on Xbox; on PC HdmiDisplayInformation is null.
    public static class DisplayModeService
    {
        private static bool _changed;

        public static async Task MatchAsync(uint width, uint height, double fps, bool hdr)
        {
            if (!ApiInformation.IsTypePresent("Windows.Graphics.Display.Core.HdmiDisplayInformation"))
                return;

            try
            {
                var info = HdmiDisplayInformation.GetForCurrentView();
                if (info == null)
                    return;

                var wantUhd = width > 1920 || height > 1080;
                var modes = info.GetSupportedDisplayModes()
                    .Where(m => !hdr || m.IsSmpte2084Supported)
                    .Where(m => wantUhd ? m.ResolutionHeightInRawPixels >= 2160 : m.ResolutionHeightInRawPixels == 1080 || m.ResolutionHeightInRawPixels == 2160)
                    .ToList();
                if (modes.Count == 0)
                    return;

                // Prefer the refresh rate that's an exact multiple of the content (23.976 -> 23.976/47.95, 25 -> 50, ...).
                var best = modes
                    .OrderBy(m => fps > 0 ? RefreshPenalty(m.RefreshRate, fps) : 0)
                    .ThenByDescending(m => m.ResolutionHeightInRawPixels)
                    .ThenByDescending(m => m.RefreshRate)
                    .First();

                var hdrOption = hdr ? HdmiDisplayHdrOption.Eotf2084 : HdmiDisplayHdrOption.None;
                if (await info.RequestSetCurrentDisplayModeAsync(best, hdrOption))
                    _changed = true;
            }
            catch (Exception)
            {
                // Display mode switching is a nicety; never let it break playback.
            }
        }

        public static async void RestoreDefault()
        {
            if (!_changed)
                return;
            _changed = false;
            try
            {
                var info = HdmiDisplayInformation.GetForCurrentView();
                if (info != null)
                    await info.SetDefaultDisplayModeAsync();
            }
            catch (Exception)
            {
            }
        }

        private static double RefreshPenalty(double refresh, double fps)
        {
            var ratio = refresh / fps;
            var nearest = Math.Max(1, Math.Round(ratio));
            return Math.Abs(ratio - nearest) * 100 + (nearest - 1);
        }
    }
}
