### Playback and stability

- Added safer chapter seeking with queued/latest-wins seek handling.
- Prevented failed MKV seeks from being mistaken for episode completion.
- Prevented false `EndReached` events after failed chapter seeks from starting the next episode.
- Added visible seek status and error messages below the loading indicator.
- Added explicit player teardown when leaving playback, including preload cancellation and LibVLC disposal.
- Added next-episode stream preparation without creating a second media player or starting playback.
- Disabled hardware decoding by default for improved Xbox stability.
- Added memory diagnostics using the UWP app memory report.

### Episode browsing and artwork

- Added episode thumbnails and descriptions to the episode selector.
- Added automatic selection of the first season that is not fully watched.
- Added optional blurring for unwatched episode artwork in the episode selector and stream selector.
- Added episode artwork and descriptions to the stream-selection sidebar.
- Enlarged and repositioned the stream selector sidebar below the watched controls.
- Aligned the source filter with the full stream list.
- Added optional TMDB backgrounds using the existing IMDb ID.
- Added TMDB API-key configuration in Settings.
- Added fallback to metadata artwork when TMDB is unavailable.

### Audio and subtitles

- Added preferred audio language settings.
- Added preferred subtitle language settings.
- Added an option to enable preferred subtitles by default.
- Added optional Signs and Songs subtitle preference.
- Reduced subtitle loading to the preferred track by default.
- Added on-demand loading for additional subtitle tracks.
- Added subtitle track refresh after selection so the current cue appears immediately.
- Added audio and subtitle diagnostics while switching tracks.

### Xbox controls and player interface

- D-pad left/right now remains available for player UI focus navigation.
- Bumpers navigate backward and forward by chapter.
- Chapter Back now targets the previous chapter boundary instead of the current chapter start.
- Added chapter diagnostics with current and target chapter information.
- Updated player controls with Segoe MDL2 Assets icons.
- Updated the audio control to use the globe icon.
- Added a next-episode button when another episode is available.

### Diagnostics and privacy

- Replaced direct debug output with rotating app log files in the app LocalState folder.
- Redacted authentication keys and sensitive extension URL data from API diagnostics.
- API response logs now record status and byte counts instead of full response bodies.
- Added LibVLC decoder, seek, subtitle, preload, lifecycle, and memory diagnostics.
- Added memory budget warnings at 80% and critical warnings at 90% of the reported limit.

### Build and release improvements

- GitHub releases now use unique version tags and preserve previous builds.
- Added Microsoft Store download link and badge to every release.
- Moved release notes into `.github/workflows/CHANGELOG.md` so future releases can update the changelog independently of the workflow.
