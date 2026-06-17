using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using SK.Libretro.Unity;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Goliath.Addons
{
    /// <summary>
    /// Plays "rom" / "game" media items through the SK.Libretro (RetroArch) integration: it picks a
    /// libretro core for the ROM, points a <see cref="LibretroInstance"/> at it, and starts content.
    ///
    /// Requirements (the user provides these via the SK.Libretro setup that ships with the project):
    ///   - A <see cref="LibretroInstance"/> in the scene with a render surface (Renderer + Collider).
    ///     SK.Libretro's example prefab/scene provides one. RetroPlayer finds the first in the scene.
    ///   - The needed libretro cores installed (SK.Libretro's Core Manager downloads them).
    ///   - ROM files in &lt;persistentDataPath&gt;/Libretro/roms (see <see cref="RomLibrarySource"/>).
    /// The core can be overridden per item via <c>MediaItem.mime</c>; otherwise it is chosen by extension.
    /// </summary>
    public class RetroPlayer : MonoBehaviour
    {
        private static RetroPlayer _instance;

        public static RetroPlayer Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("RetroPlayer");
                    _instance = go.AddComponent<RetroPlayer>();
                }
                return _instance;
            }
        }

        /// <summary>True while a game is running; other players pause their input then.</summary>
        public static bool Active { get; private set; }

        /// <summary>Default file-extension -> libretro core mapping (editable at runtime if needed).</summary>
        public static readonly Dictionary<string, string> ExtensionCores =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".nes", "fceumm" }, { ".fds", "fceumm" },
                { ".sfc", "snes9x" }, { ".smc", "snes9x" },
                { ".gb", "gambatte" }, { ".gbc", "gambatte" },
                { ".gba", "mgba" },
                { ".md", "genesis_plus_gx" }, { ".gen", "genesis_plus_gx" }, { ".smd", "genesis_plus_gx" },
                { ".sms", "genesis_plus_gx" }, { ".gg", "genesis_plus_gx" },
                { ".n64", "mupen64plus_next" }, { ".z64", "mupen64plus_next" }, { ".v64", "mupen64plus_next" },
                { ".pce", "mednafen_pce_fast" },
                { ".a26", "stella" },
                { ".ws", "mednafen_wswan" }, { ".wsc", "mednafen_wswan" },
                { ".col", "gearcoleco" }
            };

        private LibretroInstance _libretro;
        private Camera _runtimeCamera;       // non-null only when we built our own screen
        private GameObject _runtimeScreen;
        private readonly List<Canvas> _hiddenOverlays = new List<Canvas>();

        [RuntimeInitializeOnLoadMethod]
        private static void Boot()
        {
            // Register before the hub opens, so ROM items are routable (not shown as "soon").
            MediaRouter.Register("rom", item => Instance.Launch(item));
            MediaRouter.Register("game", item => Instance.Launch(item));
        }

        public void Launch(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.url))
            {
                return;
            }

            // The content to load and the core to use. An explicit item.mime overrides core detection
            // (and keeps the zip as-is, e.g. for arcade/MAME). Otherwise, if it's a zip we look inside
            // for the actual ROM so the core can be chosen from the inner file's extension.
            string contentPath = item.url;
            string core = item.mime;

            if (string.IsNullOrEmpty(core))
            {
                if (IsZip(contentPath))
                {
                    string innerRom = ResolveZippedRom(contentPath, out string innerCore);
                    if (innerRom != null)
                    {
                        contentPath = innerRom;
                        core = innerCore;
                    }
                }
                else
                {
                    core = CoreForExtension(contentPath);
                }
            }

            if (string.IsNullOrEmpty(core))
            {
                Debug.LogWarning($"RetroPlayer: no known core for '{Path.GetFileName(item.url)}'. " +
                                 "Set the item's 'mime' to a libretro core name.");
                return;
            }

            var instance = EnsureLibretroInstance();
            if (instance == null)
            {
                Debug.LogError("RetroPlayer: no LibretroInstance in the scene. Add SK.Libretro's render " +
                               "surface (its example prefab/scene) so games have somewhere to display.");
                return;
            }

            // Download the libretro core if it isn't installed yet, then start the game.
            StartCoroutine(EnsureCoreThenStart(instance, core,
                Path.GetDirectoryName(contentPath), Path.GetFileName(contentPath)));
        }

        private IEnumerator EnsureCoreThenStart(LibretroInstance instance, string core, string dir, string file)
        {
            string coresDir = Path.Combine(Application.persistentDataPath, "Libretro", "cores");
            string coreFile = core + "_libretro." + CoreFileExtension();
            string corePath = Path.Combine(coresDir, coreFile);

            if (!File.Exists(corePath))
            {
                string platform = BuildbotPlatform();
                if (platform == null)
                {
                    Debug.LogError($"RetroPlayer: core '{core}' is not installed, and auto-download isn't " +
                                   "supported on this platform. Install it via the Libretro > Manage Cores window.");
                    yield break;
                }

                Directory.CreateDirectory(coresDir);
                string url = $"https://buildbot.libretro.com/nightly/{platform}/x86_64/latest/{coreFile}.zip";
                string tmpZip = Path.Combine(Application.temporaryCachePath, coreFile + ".zip");
                Debug.Log($"RetroPlayer: core '{core}' missing - downloading from {url} ...");

                using (var request = UnityWebRequest.Get(url))
                {
                    request.downloadHandler = new DownloadHandlerFile(tmpZip);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"RetroPlayer: failed to download core '{core}': {request.error}. " +
                                       "Install it via the Libretro > Manage Cores window.");
                        yield break;
                    }
                }

                try
                {
                    ZipUtil.Unzip(tmpZip, coresDir);
                }
                catch (Exception e)
                {
                    Debug.LogError($"RetroPlayer: failed to unpack core '{core}': {e.Message}");
                    yield break;
                }

                if (!File.Exists(corePath))
                {
                    Debug.LogError($"RetroPlayer: core '{core}' still missing after download.");
                    yield break;
                }
                Debug.Log($"RetroPlayer: core '{core}' installed.");
            }

            StartGame(instance, core, dir, file);
        }

        private void StartGame(LibretroInstance instance, string core, string dir, string file)
        {
            try
            {
                instance.StopContent();
                instance.Initialize(core, dir, file);
                if (instance.gameObject != null)
                {
                    instance.gameObject.SetActive(true);
                }
                instance.StartContent();
                HideOverlays();
                if (_runtimeCamera != null)
                {
                    _runtimeCamera.enabled = true;
                }
                Active = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"RetroPlayer: failed to start '{file}' with core '{core}': {e.Message}");
            }
        }

        private static string CoreFileExtension()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer: return "dylib";
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.Android: return "so";
                default: return "dll"; // Windows
            }
        }

        private static string BuildbotPlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer: return "windows";
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer: return "linux";
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer: return "apple/osx";
                default: return null; // Android/iOS/etc need platform-specific core handling
            }
        }

        public void Stop()
        {
            if (_libretro != null)
            {
                _libretro.StopContent();
            }
            if (_runtimeCamera != null)
            {
                _runtimeCamera.enabled = false; // give the screen back to the reader/hub
            }
            RestoreOverlays();
            Active = false;
        }

        private LibretroInstance EnsureLibretroInstance()
        {
            if (_libretro == null)
            {
                _libretro = FindObjectOfType<LibretroInstance>(true);
                if (_libretro == null)
                {
                    _libretro = BuildRuntimeInstance();
                }
            }
            return _libretro;
        }

        /// <summary>
        /// Builds a full-screen game screen at runtime (orthographic camera + quad + unlit material) so
        /// games display in any scene without a pre-placed SK.Libretro render surface.
        /// </summary>
        private LibretroInstance BuildRuntimeInstance()
        {
            const int layer = 31; // dedicated layer so only our camera draws the game quad

            var camGo = new GameObject("RetroCamera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.position = new Vector3(0f, 0f, -5000f); // far from the scene to avoid overlap
            _runtimeCamera = camGo.AddComponent<Camera>();
            _runtimeCamera.orthographic = true;
            _runtimeCamera.orthographicSize = 1f;
            _runtimeCamera.nearClipPlane = 0.1f;
            _runtimeCamera.farClipPlane = 50f;
            _runtimeCamera.clearFlags = CameraClearFlags.SolidColor;
            _runtimeCamera.backgroundColor = Color.black;
            _runtimeCamera.cullingMask = 1 << layer;
            _runtimeCamera.depth = 100f; // draw on top of the scene's cameras
            _runtimeCamera.enabled = false;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "RetroScreen";
            quad.layer = layer;
            quad.transform.SetParent(transform, false);
            float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
            quad.transform.position = new Vector3(0f, 0f, -4998f);
            quad.transform.localScale = new Vector3(2f * aspect, 2f, 1f); // fills the orthographic view

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            string texProp = "_BaseMap";
            if (shader == null) { shader = Shader.Find("Unlit/Texture"); texProp = "_MainTex"; }
            if (shader == null) { shader = Shader.Find("Sprites/Default"); texProp = "_MainTex"; }

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(shader);

            if (quad.GetComponent<AudioSource>() == null)
            {
                quad.AddComponent<AudioSource>(); // libretro audio plays through this source
            }

            var instance = quad.AddComponent<LibretroInstance>();
            instance.Settings = new InstanceSettings { ShaderTextureName = texProp };
            instance.Renderer = renderer;
            instance.Collider = quad.GetComponent<Collider>();
            _runtimeScreen = quad;

            Debug.Log("RetroPlayer: built a runtime game screen (no LibretroInstance was in the scene).");
            return instance;
        }

        private static string CoreForExtension(string path)
        {
            string ext = Path.GetExtension(path);
            return ExtensionCores.TryGetValue(ext, out var core) ? core : null;
        }

        private static bool IsZip(string path)
        {
            return Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts a zipped ROM (once, to the cache) and returns the first contained file whose
        /// extension maps to a known core; <paramref name="core"/> is set to that core. Null if none.
        /// </summary>
        private static string ResolveZippedRom(string zipPath, out string core)
        {
            core = null;
            try
            {
                string name = Path.GetFileNameWithoutExtension(zipPath);
                string dest = Path.Combine(Application.temporaryCachePath, "roms_extracted", name);
                if (!Directory.Exists(dest) || Directory.GetFileSystemEntries(dest).Length == 0)
                {
                    Directory.CreateDirectory(dest);
                    ZipUtil.Unzip(zipPath, dest);
                }
                foreach (var f in Directory.GetFiles(dest, "*.*", SearchOption.AllDirectories))
                {
                    if (ExtensionCores.TryGetValue(Path.GetExtension(f), out var c))
                    {
                        core = c;
                        return f;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"RetroPlayer: could not read zip '{Path.GetFileName(zipPath)}': {e.Message}");
            }
            return null;
        }

        private void HideOverlays()
        {
            _hiddenOverlays.Clear();
            foreach (var canvas in FindObjectsOfType<Canvas>())
            {
                if (canvas.enabled && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    canvas.enabled = false;
                    _hiddenOverlays.Add(canvas);
                }
            }
        }

        private void RestoreOverlays()
        {
            foreach (var canvas in _hiddenOverlays)
            {
                if (canvas != null)
                {
                    canvas.enabled = true;
                }
            }
            _hiddenOverlays.Clear();
        }

#if ENABLE_INPUT_SYSTEM
        private void Update()
        {
            if (!Active)
            {
                return;
            }
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                Stop();
            }
        }
#endif
    }
}
