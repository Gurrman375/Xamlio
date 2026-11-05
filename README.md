# Xamlio

## Download
<a href="https://apps.microsoft.com/detail/9nk0rm6n126h?hl=en-US&gl=US" target="_self" >
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>


A native Stremio client for **Xbox Series X|S**, written as a UWP app and sideloaded in Dev Mode.

It uses the console's hardware the way the built-in media apps do:

- **Hardware video decoding** with libVLC (d3d11va). That covers H.264, HEVC 10-bit, VP9 and AV1, in MKV, MP4 and most other containers.
- **HDMI display-mode matching.** The console's video output switches to 4K, HDR10 and a refresh rate that matches the video's frame rate, then switches back when you leave the player.
- **Audio passthrough** (optional). Dolby and DTS are sent to the TV or receiver as-is.
- **Your Stremio account.** Log in to get your installed addons and your library.
- **Controller-first UI** with a TV-safe layout.

You don't need a PC. GitHub Actions builds the app, and you install it from your phone's browser.

---

## Project status

### Working

- QR-code account sign-in.
- Automatic auth-key polling after the QR code is scanned.
- Automatic addon refresh after login.
- Xbox build and GitHub Actions sideload-package publishing.
- TV-safe margin setting.
- Basic video playback with controller playback controls.
- Stream sorting by addon source and quality.
- Addon source filtering.
- Informational stream entries and external-link handling.

### Open issues and roadmap

- Verify and complete incremental stream loading, so results appear as each addon responds rather than only after all addons finish.
- Improve controller navigation and focus highlighting throughout the player and settings screens.
- Improve audio-track and subtitle-track navigation.
- Automatically play the next episode.
- Sort and organize Library results more consistently.
- Separate Search results into movies and series.
- Improve stream-list scrolling, width, background, and layout behavior.
- Verify full-screen and cropped-display behavior across Xbox televisions.
- Investigate multiplayer/watch-party support.
- Evaluate additional native player options, including MPV.
- Evaluate torrent playback without a debrid service, subject to Xbox and Microsoft platform requirements.

---

## Install on Xbox (phone + console only)

### 1. Turn on Dev Mode (one time)

1. On your phone, sign up for a **Microsoft Partner Center** developer account (individual) at
   <https://partner.microsoft.com/dashboard/registration>.
2. On the Xbox, open the Microsoft Store and install **Xbox Dev Mode** (the "Dev Mode Activation" app).
3. Open it and follow the code it shows you on your phone. The console restarts.
4. In **Dev Home**, open **Remote Access Settings** and turn on **Enable Xbox Device Portal**.
   Set a username and password, and note the address it shows (for example `https://192.168.1.50:11443`).
5. Still in Dev Home, tap **Leave developer mode** whenever you want to play retail games again.
   Switching back to Dev Mode later is quick.

### 2. Download the app on your phone

Open **Releases → latest** in this repo: <https://github.com/rsharma929/stremio-xbox/releases/tag/latest>.
Download every file listed there:

- `Xamlio_<version>_x64.msix` is the app.
- `Microsoft.VCLibs.x64.*.appx`, `Microsoft.NET.Native.Framework.*.appx` and `Microsoft.NET.Native.Runtime.*.appx` are dependencies.

### 3. Install through Device Portal

1. In your phone's browser, open the Device Portal address from step 1. Your browser will warn about the
   certificate; accept it. Then sign in.
2. Go to **Home → My games & apps → Add**.
3. Pick the `Xamlio_*.msix` file, tap **Next**, and add each `.appx` dependency file.
4. Tap **Start**. Xamlio now appears in Dev Home's app list.
5. **Recommended:** in Dev Home (or Device Portal), open the app's settings and change its **App type** from
   *App* to **Game**. Games get far more memory and CPU than apps, and get exclusive GPU access.

To update, install the newer release the same way. Every build has a higher version number.

---

## Using it

| Controller | Where | What it does |
|---|---|---|
| A | anywhere | select |
| B | anywhere | back |
| X | player | play / pause |
| LB / RB | player | back 10 s / forward 30 s |
| LT / RT | player | back / forward 5 min |
| D-pad left / right | player (controls hidden) | seek |

- **Board** shows the catalogs from your addons.
- **Search** searches across every addon that supports it. On the on-screen keyboard, press **Enter** to search.
- **Library** shows your Stremio library. It needs a login.
- **Settings** covers your account, the streaming server, subtitle language, passthrough, display matching, and adding addons by URL.

### Where streams come from

The app ships only with **Cinemeta** (catalogs) and **OpenSubtitles**. Stream sources are the addons
installed in **your Stremio account**: log in under Settings and they load automatically. You can also
paste an addon's `manifest.json` URL under Settings → Addons.

- **Direct and debrid links** (for example an addon configured with Real-Debrid, AllDebrid or Premiumize) play
  straight from the console.
- **Torrent streams** need Stremio's streaming server, which can't run on the Xbox. Run the Stremio
  desktop app (or `stremio-server`) on a PC, NAS or Android box on your network. Then enter its address under
  Settings, for example `http://192.168.1.20:11470`. Until you do, the app hides torrent streams.

Only stream content you have the rights to.

---

## Project layout

```
src/Stremio.Core/        netstandard2.0, no UI: addon protocol, account API, stream resolution, settings
src/Xamlio/         UWP app: pages, LibVLC player, HDMI display-mode service
tests/Stremio.Core.Tests xUnit tests (run on Linux in CI)
.github/workflows/       builds, signs and publishes the sideload package
```

Building locally (optional, needs Windows with Visual Studio 2022 and the *Universal Windows Platform development*
workload): open `Xamlio.sln`, pick **Release | x64**, and deploy to your console with the
**Remote Machine** target.

## Limitations

- Stremio's streaming server (torrents) runs on another device, not on the console.
- libVLC 3 doesn't report a video's HDR metadata. The app switches to HDR when the stream's name says
  HDR, DV or Dolby Vision. Dolby Vision plays as HDR10 or SDR, depending on the file.
- Dev Mode apps are for personal use. They aren't in the Microsoft Store.
- Multiplayer/watch-party functionality is not currently implemented.
- MPV support is not currently implemented.

## Development notes

This project is community-developed and is not affiliated with Stremio or Microsoft.
