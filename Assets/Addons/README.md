# Goliath Addons

A cross-platform addon system. Addons are **interpreted JavaScript** (via Jint, a pure-C# engine), so
the exact same addon runs on **iOS, Android, Android TV, Windows, Linux** and future platforms — no
native code, no JIT, nothing downloaded as executable code (which iOS forbids).

An addon **takes JSON in and returns JSON out**. The first addon type is `media-source`: it provides a
catalog of media items that appear in the app's Library.

## Enabling the JavaScript engine (one-time)

The engine library is not bundled (it has its own license). To run script addons:

1. Download **Jint** and its dependency (e.g. `Esprima.dll` for Jint v3) from
   [NuGet](https://www.nuget.org/packages/Jint) or via NuGetForUnity.
2. Drop `Jint.dll` (+ `Esprima.dll`) into `Assets/Plugins/`.
3. **Project Settings → Player → Scripting Define Symbols** → add `ADDONS_JINT`.

Until then the project still compiles and **C#-backed sources work** (see `BuiltinSampleSource`), but
JavaScript addons load to nothing.

## Addon package format

An addon is a folder (zipped into a `.addon` file) containing:

```
manifest.json     # metadata (see AddonManifest)
main.js           # entry script (name set by manifest "entry")
```

`manifest.json`:

```json
{
  "id": "com.you.my-source",
  "name": "My Source",
  "version": "1.0.0",
  "type": "media-source",
  "entry": "main.js",
  "permissions": ["network"],
  "minAppVersion": "0.1.0"
}
```

## Writing a media source (JavaScript)

```js
function getCatalog(argJson) {
    return JSON.stringify({
        title: "My Source",
        items: [
            { id: "1", title: "Some Book", type: "ebook", url: "https://host/book.epub" }
        ]
    });
}
// optional
function search(argJson) {
    var q = JSON.parse(argJson).query;
    return JSON.stringify({ title: "Results", items: [] });
}
```

A `MediaItem` has: `id, title, subtitle, type, url, thumbnailUrl, mime`.
`type` is `"ebook"` today; `"video"`, `"audio"`, etc. are reserved for when those players exist.

### Host API (globals available to scripts)

| Function | Permission | Notes |
|---|---|---|
| `log(msg)` | — | writes to the Unity console |
| `httpGet(url)` | `network` | synchronous GET, returns body string |
| `getStorage(key)` | — | addon-scoped key/value read |
| `setStorage(key, value)` | — | addon-scoped key/value write |

## Where addons live

- Built-in: `Assets/StreamingAssets/Addons/<id>/` (shipped; read directly on desktop/editor).
- User: `<persistentDataPath>/Addons/<id>/` (installed/authored at runtime, all platforms).

## Author in-app, export later

```csharp
// create / edit
AddonManager.Instance.SaveAddon(manifest, scriptText);
// package into a shareable file
AddonManager.Instance.ExportAddon(manifest.id, "C:/path/MySource.addon");
// install someone else's
AddonManager.Instance.ImportAddon("C:/path/Their.addon");
```

## Using sources from app code

```csharp
foreach (var src in AddonManager.Instance.Sources) {
    var catalog = src.GetCatalog();
    foreach (var item in catalog.items) { /* show item, open on tap */ }
}
```

## Emulators (ROMs via SK.Libretro / RetroArch)

ROMs are a media type (`type: "rom"`) routed to `RetroPlayer`, which drives the SK.Libretro
integration already in the project.

Setup:
1. **Cores**: on desktop (Windows/Linux/macOS) `RetroPlayer` **auto-downloads** the needed core from
   the libretro nightly buildbot into `<persistentDataPath>/Libretro/cores` on first launch. You can
   also pre-install them with SK.Libretro's Core Manager (Libretro > Manage Cores). On mobile/console,
   install/bundle cores manually.
2. **A render surface**: `RetroPlayer` displays games on a full-screen quad. If the scene already has a
   `LibretroInstance` (e.g. SK.Libretro's example screen), it uses that; otherwise it **builds one at
   runtime** (an orthographic camera + quad + unlit material), so games show in any scene with no
   manual wiring. Overlay UI is hidden while a game runs.
3. **Add ROMs**: drop files into `<persistentDataPath>/Libretro/roms`, or use the hub's
   **Add a game (ROM)...** to browse and launch one directly.

The core is chosen by file extension (`.nes`->fceumm, `.sfc`->snes9x, `.gba`->mgba, ...; see
`RetroPlayer.ExtensionCores`). Override per item by setting `MediaItem.mime` to a core name.
While a game runs, overlay UI is hidden and **Esc** stops it and returns.

A media-source addon can list games too: return items with `type: "rom"`, `url` = the ROM path,
and optionally `mime` = the core name.

## Video

Videos are a media type (`type: "video"`) played by `VideoPlayerView` using Unity's built-in
`VideoPlayer` (cross-platform; codecs depend on the platform - mp4/H.264 and webm are the safest).
It renders full-screen on its own top-most canvas: **tap** to pause/resume, **Stop**/**Esc** to close.

- Drop files in `<persistentDataPath>/Videos` (they appear under the **Videos** source), or use the
  hub's **Add a video...** to browse and play one.
- `MediaItem.url` may be a local file path or an http(s) URL.
- A media-source addon can list videos: return items with `type: "video"` and `url` = the path/URL.

(libretro also has an `ffmpeg` movie-player core, but Unity's `VideoPlayer` is more reliable and
portable, so it is the default.)

## Audio / music

Audio is a media type (`type: "audio"`) played by `AudioPlayerView` via an `AudioSource`. Files are
decoded with `UnityWebRequestMultimedia` (ogg / wav / mp3 / aiff, platform permitting). It shows a
simple "now playing" screen with elapsed/total time: **tap** to pause/resume, **Stop**/**Esc** to close.

- Drop files in `<persistentDataPath>/Music` (they appear under the **Music** source), or use the
  hub's **Add music / audio...** to browse and play one.
- `MediaItem.url` may be a local path or an http(s) URL.

## Zip archives

In the hub's file browser, `.zip` files show as `[Zip] name (extract)`. Tapping one extracts it (once)
to `<temporaryCachePath>/unzipped/<name>` and browses inside in the same mode, so you can open the
media within. (In **ROM** mode `.zip` is launched directly, since libretro loads zipped ROMs itself.)

## Known limitations / next steps

- **StreamingAssets on Android** is inside the APK and is not a normal file path; built-in addons there
  need `UnityWebRequest` reads (user-folder addons work everywhere). Desktop/editor read directly.
- **`httpGet` is synchronous** — keep addon network use light; script execution will move to a worker
  thread / async API later.
- Only `type: "ebook"` items open today (they route to the ebook reader). Other media types are
  reserved for when their players are added.
