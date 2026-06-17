using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Discovers, loads and manages addons, and exposes their media sources to the rest of the app.
    ///
    /// Addons live in two places:
    ///   - Built-in:  &lt;StreamingAssets&gt;/Addons/&lt;id&gt;/      (shipped with the app; desktop/editor read directly)
    ///   - User:      &lt;persistentDataPath&gt;/Addons/&lt;id&gt;/  (installed/authored at runtime, all platforms)
    /// Each addon folder has a manifest.json plus its entry script (default main.js).
    ///
    /// Access via <see cref="Instance"/>; it self-creates a persistent GameObject on first use.
    /// </summary>
    public class AddonManager : MonoBehaviour
    {
        private static AddonManager _instance;

        public static AddonManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("AddonManager");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<AddonManager>();
                    _instance.Initialize();
                }
                return _instance;
            }
        }

        private readonly List<IMediaSource> _sources = new List<IMediaSource>();
        private readonly List<AddonManifest> _loaded = new List<AddonManifest>();
        private IScriptEngine _engine;
        private string _userAddonsDir;

        public IReadOnlyList<IMediaSource> Sources => _sources;
        public IReadOnlyList<AddonManifest> LoadedAddons => _loaded;

        private const string SourceEnabledPrefix = "ebook.source.enabled.";

        /// <summary>Whether a source is shown in the hub. Sources are enabled by default.</summary>
        public bool IsSourceEnabled(string sourceId)
        {
            return PlayerPrefs.GetInt(SourceEnabledPrefix + sourceId, 1) == 1;
        }

        public void SetSourceEnabled(string sourceId, bool enabled)
        {
            PlayerPrefs.SetInt(SourceEnabledPrefix + sourceId, enabled ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>The sources the user has left enabled (what the hub home should list).</summary>
        public List<IMediaSource> GetEnabledSources()
        {
            var list = new List<IMediaSource>();
            foreach (var source in _sources)
            {
                if (IsSourceEnabled(source.Id))
                {
                    list.Add(source);
                }
            }
            return list;
        }

        private void Initialize()
        {
            _engine = ScriptEngineFactory.Create();
            _userAddonsDir = Path.Combine(Application.persistentDataPath, "Addons");
            try { Directory.CreateDirectory(_userAddonsDir); } catch { /* best effort */ }
            ReloadAddons();
        }

        /// <summary>Re-scans both addon roots and rebuilds the source list. Call after install/author.</summary>
        public void ReloadAddons()
        {
            _sources.Clear();
            _loaded.Clear();

            // C#-backed sources compiled into the app.
            _sources.Add(new BuiltinSampleSource());
            _sources.Add(new RomLibrarySource());
            _sources.Add(new VideoLibrarySource());
            _sources.Add(new AudioLibrarySource());

            LoadFromRoot(Path.Combine(Application.streamingAssetsPath, "Addons"));
            LoadFromRoot(_userAddonsDir);
        }

        private void LoadFromRoot(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }
            foreach (var dir in Directory.GetDirectories(root))
            {
                try { LoadAddonFolder(dir); }
                catch (Exception e) { Debug.LogWarning($"Goliath.Addons: failed to load '{dir}': {e.Message}"); }
            }
        }

        private void LoadAddonFolder(string dir)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return;
            }
            var manifest = JsonUtility.FromJson<AddonManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || string.IsNullOrEmpty(manifest.id))
            {
                Debug.LogWarning($"Goliath.Addons: '{dir}' has an invalid manifest.json");
                return;
            }

            string entry = string.IsNullOrEmpty(manifest.entry) ? "main.js" : manifest.entry;
            string scriptPath = Path.Combine(dir, entry);
            string source = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : string.Empty;

            string storageDir = Path.Combine(_userAddonsDir, "_storage", manifest.id);
            var context = new AddonContext(manifest, storageDir);
            object handle = string.IsNullOrEmpty(source) ? null : _engine.Load(source, context);

            _loaded.Add(manifest);
            if (manifest.type == "media-source" && handle != null)
            {
                _sources.Add(new ScriptMediaSource(_engine, handle, manifest));
            }
        }

        // ------------------------------------------------------------------------------------
        // Authoring / packaging: create in-app, export and import .addon (zip) packages.
        // ------------------------------------------------------------------------------------

        /// <summary>Writes (or overwrites) an addon under the user folder so it can be edited and exported.</summary>
        public void SaveAddon(AddonManifest manifest, string scriptSource)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.id))
            {
                Debug.LogError("Goliath.Addons: SaveAddon needs a manifest with an id.");
                return;
            }
            string dir = Path.Combine(_userAddonsDir, manifest.id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonUtility.ToJson(manifest, true));
            string entry = string.IsNullOrEmpty(manifest.entry) ? "main.js" : manifest.entry;
            File.WriteAllText(Path.Combine(dir, entry), scriptSource ?? string.Empty);
            ReloadAddons();
        }

        /// <summary>Zips an authored/installed addon folder into a shareable .addon package.</summary>
        public bool ExportAddon(string addonId, string outputAddonPath)
        {
            string dir = Path.Combine(_userAddonsDir, addonId);
            if (!Directory.Exists(dir))
            {
                Debug.LogError($"Goliath.Addons: no addon '{addonId}' to export.");
                return false;
            }
            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
                ZipUtil.Zip(outputAddonPath, files);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Goliath.Addons: export failed: {e.Message}");
                return false;
            }
        }

        /// <summary>True when a JavaScript engine is compiled in (the ADDONS_JINT define is set).</summary>
        public static bool ScriptingAvailable =>
#if ADDONS_JINT
            true;
#else
            false;
#endif

        /// <summary>Manifests of addons in the user (editable) folder.</summary>
        public List<AddonManifest> GetUserAddons()
        {
            var list = new List<AddonManifest>();
            if (string.IsNullOrEmpty(_userAddonsDir) || !Directory.Exists(_userAddonsDir))
            {
                return list;
            }
            foreach (var dir in Directory.GetDirectories(_userAddonsDir))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }
                try
                {
                    var manifest = JsonUtility.FromJson<AddonManifest>(File.ReadAllText(manifestPath));
                    if (manifest != null && !string.IsNullOrEmpty(manifest.id))
                    {
                        list.Add(manifest);
                    }
                }
                catch { /* skip invalid */ }
            }
            return list;
        }

        /// <summary>Reads a user addon's manifest and script for editing.</summary>
        public bool TryReadAddon(string id, out AddonManifest manifest, out string script)
        {
            manifest = null;
            script = null;
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            var dir = Path.Combine(_userAddonsDir, id);
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }
            manifest = JsonUtility.FromJson<AddonManifest>(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                return false;
            }
            var entry = string.IsNullOrEmpty(manifest.entry) ? "main.js" : manifest.entry;
            var scriptPath = Path.Combine(dir, entry);
            script = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : string.Empty;
            return true;
        }

        /// <summary>Deletes a user addon folder.</summary>
        public bool DeleteAddon(string id)
        {
            var dir = Path.Combine(_userAddonsDir, id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
                ReloadAddons();
                return true;
            }
            return false;
        }

        /// <summary>Runs a script once (e.g. from the editor's Test button) and returns its JSON result.</summary>
        public string RunScriptOnce(string source, string function, string argJson)
        {
            if (string.IsNullOrEmpty(source))
            {
                return "(empty script)";
            }
            var manifest = new AddonManifest { id = "_test", name = "test", permissions = new[] { "network" } };
            var context = new AddonContext(manifest, Path.Combine(_userAddonsDir, "_storage", "_test"));
            var handle = _engine.Load(source, context);
            if (handle == null)
            {
                return ScriptingAvailable
                    ? "(script failed to load - see the Console for the error)"
                    : "(no JavaScript engine - install Jint and set ADDONS_JINT to Test)";
            }
            return _engine.Invoke(handle, function, argJson ?? "{}");
        }

        /// <summary>A writable path to export an addon package to.</summary>
        public string DefaultExportPath(string id)
        {
            var dir = Path.Combine(Application.persistentDataPath, "Exports");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, id + ".addon");
        }

        /// <summary>Installs a .addon package (zip with manifest.json + script) into the user folder.</summary>
        public bool ImportAddon(string addonPackagePath)
        {
            try
            {
                string tmp = Path.Combine(Application.temporaryCachePath, "addon_import");
                if (Directory.Exists(tmp))
                {
                    Directory.Delete(tmp, true);
                }
                ZipUtil.Unzip(addonPackagePath, tmp);

                string manifestPath = Path.Combine(tmp, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    Debug.LogError("Goliath.Addons: package has no manifest.json");
                    return false;
                }
                var manifest = JsonUtility.FromJson<AddonManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || string.IsNullOrEmpty(manifest.id))
                {
                    Debug.LogError("Goliath.Addons: package manifest is invalid");
                    return false;
                }

                string dest = Path.Combine(_userAddonsDir, manifest.id);
                if (Directory.Exists(dest))
                {
                    Directory.Delete(dest, true);
                }
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.GetFiles(tmp, "*", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
                }

                ReloadAddons();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Goliath.Addons: import failed: {e.Message}");
                return false;
            }
        }
    }
}
