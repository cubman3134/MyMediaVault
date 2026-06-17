using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Goliath.Addons
{
    /// <summary>
    /// The app's media hub: a full-screen home that aggregates media sources (built-in + addons),
    /// recently opened media, source management, a file browser, and the in-app addon editor.
    ///
    /// It is self-contained: it builds its own UI under the scene Canvas, reads its own input, routes
    /// chosen items through <see cref="MediaRouter"/>, and knows nothing about any specific player.
    /// Any viewer (the ebook reader, a future video player, ...) just registers with MediaRouter and
    /// calls <c>MediaHub.Instance.Show()</c> to open the hub.
    /// </summary>
    public class MediaHub : MonoBehaviour
    {
        private static MediaHub _instance;

        public static MediaHub Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MediaHub");
                    _instance = go.AddComponent<MediaHub>();
                }
                return _instance;
            }
        }

        /// <summary>True while the hub (home or editor) is on screen; players pause their input then.</summary>
        public static bool Active { get; private set; }

        /// <summary>Frame on which the hub last consumed a tap, so a player can skip that same tap.</summary>
        public static int LastInputFrame { get; private set; } = -1;

        private const float RowHeight = 56f;
        private const string DefaultAddonTemplate =
@"function getCatalog(argJson) {
    log('building catalog');
    var items = [
        { id: '1', title: 'Example Book', type: 'ebook', url: 'https://host/book.epub' }
    ];
    return JSON.stringify({ title: 'My Source', items: items });
}

function search(argJson) {
    var q = JSON.parse(argJson || '{}').query || '';
    return JSON.stringify({ title: 'Search: ' + q, items: [] });
}
";

        private Canvas _canvas;
        private Font _font;
        private int _activatedFrame = -1;

        // Home / list overlay
        private GameObject _root;
        private Text _header;
        private Text _pageLabel;
        private RectTransform _prevButton;
        private RectTransform _nextButton;
        private RectTransform _closeButton;
        private readonly List<RectTransform> _rowRects = new List<RectTransform>();
        private readonly List<Text> _rowLabels = new List<Text>();
        private readonly List<Item> _items = new List<Item>();
        private int _scroll;   // index of the first visible row (scroll offset)
        private int _rowsPerPage = 1;
        private string _headerText = "Media Hub";

        // Addon editor
        private GameObject _editorRoot;
        private InputField _editorName;
        private InputField _editorId;
        private InputField _editorScript;
        private Text _editorOutput;
        private RectTransform _edSave;
        private RectTransform _edTest;
        private RectTransform _edExport;
        private RectTransform _edBack;
        private string _editingId;

        private struct Item
        {
            public string Label;
            public Action Action;
        }

        private bool EditorOpen => _editorRoot != null && _editorRoot.activeSelf;

        // ------------------------------------------------------------------------------------
        // Show / hide / input
        // ------------------------------------------------------------------------------------

        public void Show()
        {
            if (!EnsureUI())
            {
                return;
            }
            if (_editorRoot != null)
            {
                _editorRoot.SetActive(false);
            }
            ShowHome();
            _root.SetActive(true);
            Active = true;
            _activatedFrame = Time.frameCount;
        }

        public void Hide()
        {
            if (_root != null)
            {
                _root.SetActive(false);
            }
            if (_editorRoot != null)
            {
                _editorRoot.SetActive(false);
            }
            Active = false;
        }

#if ENABLE_INPUT_SYSTEM
        private void Update()
        {
            if (!Active || Time.frameCount == _activatedFrame)
            {
                return; // ignore the tap that opened the hub
            }
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    HandleTap(mouse.position.ReadValue());
                }
                float wheel = mouse.scroll.ReadValue().y;
                if (!EditorOpen && Mathf.Abs(wheel) > 0.01f)
                {
                    _scroll = Mathf.Clamp(_scroll - (int)Mathf.Sign(wheel) * 3, 0, MaxScroll());
                    Refresh();
                }
            }
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame)
            {
                HandleTap(touch.primaryTouch.position.ReadValue());
            }
        }
#endif

        private void HandleTap(Vector2 pos)
        {
            LastInputFrame = Time.frameCount;
            MediaInput.MarkConsumed();
            if (EditorOpen)
            {
                HandleEditorTap(pos);
                return;
            }
            HandleHomeTap(pos);
        }

        private void HandleHomeTap(Vector2 pos)
        {
            int start = _scroll;
            for (int i = 0; i < _rowRects.Count; i++)
            {
                int e = start + i;
                if (e < _items.Count && _rowRects[i].gameObject.activeSelf && HitTest(_rowRects[i], pos))
                {
                    _items[e].Action?.Invoke();
                    return;
                }
            }
            if (HitTest(_closeButton, pos)) { Hide(); return; }

            // Prev/Next page through the list a screenful at a time (plus mouse-wheel scrolling).
            if (HitTest(_prevButton, pos)) { _scroll = Mathf.Max(0, _scroll - _rowsPerPage); Refresh(); return; }
            if (HitTest(_nextButton, pos)) { _scroll = Mathf.Min(MaxScroll(), _scroll + _rowsPerPage); Refresh(); return; }
        }

        private int MaxScroll() => Mathf.Max(0, _items.Count - _rowsPerPage);

        // ------------------------------------------------------------------------------------
        // Home + sub-lists
        // ------------------------------------------------------------------------------------

        private void ShowHome()
        {
            _headerText = "Media Hub";
            _items.Clear();
            _items.Add(new Item { Label = "+   Add a book...", Action = () => ShowFolder(DefaultBrowseDir(), BrowseMode.Books) });
            _items.Add(new Item { Label = "#   Add a game (ROM)...", Action = () => ShowFolder(RomStartDir(), BrowseMode.Roms) });
            _items.Add(new Item { Label = "@   Add a video...", Action = () => ShowFolder(VideoStartDir(), BrowseMode.Videos) });
            _items.Add(new Item { Label = "~   Add music / audio...", Action = () => ShowFolder(AudioStartDir(), BrowseMode.Audio) });
            _items.Add(new Item { Label = "*   Create / edit addon...", Action = () => ShowAddonPicker() });
            _items.Add(new Item { Label = "=   Manage sources...", Action = () => ShowSourceManager() });

            foreach (var media in RecentsStore.Recent)
            {
                var it = media;
                _items.Add(new Item { Label = "Recent:  " + it.title, Action = () => OpenMediaItem(it) });
            }
            foreach (var source in AddonManager.Instance.GetEnabledSources())
            {
                var s = source;
                _items.Add(new Item { Label = "[Source]  " + s.DisplayName, Action = () => ShowSourceCatalog(s) });
            }
            _scroll = 0;
            Refresh();
        }

        private void ShowSourceManager()
        {
            _headerText = "Manage Sources";
            _items.Clear();
            _items.Add(new Item { Label = "<   Media Hub", Action = () => ShowHome() });
            foreach (var source in AddonManager.Instance.Sources)
            {
                var s = source;
                bool on = AddonManager.Instance.IsSourceEnabled(s.Id);
                _items.Add(new Item
                {
                    Label = (on ? "[on]   " : "[off]  ") + s.DisplayName,
                    Action = () =>
                    {
                        AddonManager.Instance.SetSourceEnabled(s.Id, !AddonManager.Instance.IsSourceEnabled(s.Id));
                        ShowSourceManager();
                    }
                });
            }
            if (AddonManager.Instance.Sources.Count == 0)
            {
                _items.Add(new Item { Label = "(no sources installed)", Action = null });
            }
            _scroll = 0;
            Refresh();
        }

        private void ShowSourceCatalog(IMediaSource source)
        {
            MediaCatalog catalog;
            try { catalog = source.GetCatalog(); }
            catch (Exception e) { Debug.LogWarning($"Source '{source.Id}' failed: {e.Message}"); catalog = null; }

            _headerText = (catalog != null && !string.IsNullOrEmpty(catalog.title)) ? catalog.title : source.DisplayName;
            _items.Clear();
            _items.Add(new Item { Label = "<   Media Hub", Action = () => ShowHome() });
            if (catalog != null && catalog.items != null)
            {
                foreach (var item in catalog.items)
                {
                    var it = item;
                    string baseLabel = string.IsNullOrEmpty(it.subtitle) ? it.title : $"{it.title}  -  {it.subtitle}";
                    string typeTag = string.IsNullOrEmpty(it.type) ? string.Empty : $"   [{it.type}]";
                    bool playable = MediaRouter.CanOpen(it.type);
                    _items.Add(new Item { Label = (playable ? string.Empty : "(soon) ") + baseLabel + typeTag, Action = () => OpenMediaItem(it) });
                }
            }
            if (_items.Count == 1)
            {
                _items.Add(new Item { Label = "(this source returned no items)", Action = null });
            }
            _scroll = 0;
            Refresh();
        }

        private void OpenMediaItem(MediaItem item)
        {
            if (item == null)
            {
                return;
            }
            if (MediaRouter.Open(item))
            {
                Hide(); // a player took it
                return;
            }
            _headerText = $"No player for '{item.type}' yet: {item.title}";
            Refresh();
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            OpenMediaItem(new MediaItem
            {
                type = "ebook",
                url = path,
                title = Path.GetFileNameWithoutExtension(path)
            });
        }

        // ------------------------------------------------------------------------------------
        // File browser (books + addon install)
        // ------------------------------------------------------------------------------------

        private enum BrowseMode { Books, Roms, Videos, Audio, Addons }

        private void ShowDrives(BrowseMode mode)
        {
            _headerText = "Choose a drive";
            _items.Clear();
            _items.Add(new Item
            {
                Label = mode == BrowseMode.Addons ? "<   Addons" : "<   Media Hub",
                Action = mode == BrowseMode.Addons ? (Action)(() => ShowAddonPicker()) : (() => ShowHome())
            });
            try
            {
                foreach (var drive in Directory.GetLogicalDrives())
                {
                    string captured = drive;
                    _items.Add(new Item { Label = "[Drive]   " + drive, Action = () => ShowFolder(captured, mode) });
                }
            }
            catch (Exception e)
            {
                _items.Add(new Item { Label = "(cannot list drives: " + e.Message + ")", Action = null });
            }
            _scroll = 0;
            Refresh();
        }

        private void ShowFolder(string dir, BrowseMode mode)
        {
            string title = mode == BrowseMode.Addons ? "Install addon: "
                : mode == BrowseMode.Roms ? "Add game: "
                : mode == BrowseMode.Videos ? "Add video: "
                : mode == BrowseMode.Audio ? "Add audio: " : string.Empty;
            _headerText = title + ShortPath(dir);
            _items.Clear();
            _items.Add(new Item
            {
                Label = mode == BrowseMode.Addons ? "<   Addons" : "<   Media Hub",
                Action = mode == BrowseMode.Addons ? (Action)(() => ShowAddonPicker()) : (() => ShowHome())
            });
            _items.Add(new Item { Label = "[Drives]   (choose a disk)", Action = () => ShowDrives(mode) });
            try
            {
                var parent = Directory.GetParent(dir);
                if (parent != null)
                {
                    string up = parent.FullName;
                    _items.Add(new Item { Label = "..   (" + parent.Name + ")", Action = () => ShowFolder(up, mode) });
                }
                foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
                {
                    string captured = d;
                    _items.Add(new Item { Label = "[Folder]   " + new DirectoryInfo(d).Name, Action = () => ShowFolder(captured, mode) });
                }
                foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
                {
                    string captured = f;
                    string fileName = Path.GetFileName(f);
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (mode == BrowseMode.Addons && ext == ".addon")
                    {
                        _items.Add(new Item { Label = "[Install]  " + fileName, Action = () => DoImportAddon(captured) });
                    }
                    else if (mode == BrowseMode.Books && ext == ".epub")
                    {
                        _items.Add(new Item { Label = fileName, Action = () => OpenPath(captured) });
                    }
                    else if (mode == BrowseMode.Roms && RomLibrarySource.IsRom(captured))
                    {
                        _items.Add(new Item { Label = fileName, Action = () => OpenMediaItem(new MediaItem { type = "rom", url = captured, title = Path.GetFileNameWithoutExtension(captured) }) });
                    }
                    else if (mode == BrowseMode.Videos && VideoLibrarySource.IsVideo(captured))
                    {
                        _items.Add(new Item { Label = fileName, Action = () => OpenMediaItem(new MediaItem { type = "video", url = captured, title = Path.GetFileNameWithoutExtension(captured) }) });
                    }
                    else if (mode == BrowseMode.Audio && AudioLibrarySource.IsAudio(captured))
                    {
                        _items.Add(new Item { Label = fileName, Action = () => OpenMediaItem(new MediaItem { type = "audio", url = captured, title = Path.GetFileNameWithoutExtension(captured) }) });
                    }
                    else if (ext == ".zip")
                    {
                        // Zips can hold any media: extract, then browse inside in the same mode.
                        // (ROM mode handles .zip directly above, since libretro loads zipped ROMs.)
                        _items.Add(new Item { Label = "[Zip]   " + fileName + "  (extract)", Action = () => ExtractAndBrowse(captured, mode) });
                    }
                }
            }
            catch (Exception e)
            {
                _items.Add(new Item { Label = "(cannot read folder: " + e.Message + ")", Action = null });
            }
            _scroll = 0;
            Refresh();
        }

        /// <summary>Extracts a zip to a cache folder (once) and browses its contents in the same mode.</summary>
        private void ExtractAndBrowse(string zipPath, BrowseMode mode)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(zipPath);
                string dest = Path.Combine(Application.temporaryCachePath, "unzipped", name);
                if (!Directory.Exists(dest) || Directory.GetFileSystemEntries(dest).Length == 0)
                {
                    Directory.CreateDirectory(dest);
                    ZipUtil.Unzip(zipPath, dest);
                }
                ShowFolder(dest, mode);
            }
            catch (Exception e)
            {
                _headerText = "Cannot extract " + Path.GetFileName(zipPath);
                _items.Clear();
                _items.Add(new Item { Label = "<   Media Hub", Action = () => ShowHome() });
                _items.Add(new Item { Label = "(" + e.Message + ")", Action = null });
                _scroll = 0;
                Refresh();
            }
        }

        private string RomStartDir()
        {
            string dir = RomLibrarySource.RomsDir();
            return Directory.Exists(dir) ? dir : DefaultBrowseDir();
        }

        private string VideoStartDir()
        {
            string dir = VideoLibrarySource.VideosDir();
            return Directory.Exists(dir) ? dir : DefaultBrowseDir();
        }

        private string AudioStartDir()
        {
            string dir = AudioLibrarySource.MusicDir();
            return Directory.Exists(dir) ? dir : DefaultBrowseDir();
        }

        private void DoImportAddon(string addonPackagePath)
        {
            bool ok = AddonManager.Instance.ImportAddon(addonPackagePath);
            ShowAddonPicker();
            _headerText = ok ? "Installed " + Path.GetFileName(addonPackagePath) : "Import failed - see Console";
            Refresh();
        }

        private string DefaultBrowseDir()
        {
            return Directory.GetCurrentDirectory();
        }

        private static string ShortPath(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return "Files";
            }
            return dir.Length > 52 ? "..." + dir.Substring(dir.Length - 49) : dir;
        }

        // ------------------------------------------------------------------------------------
        // Addon picker + editor
        // ------------------------------------------------------------------------------------

        private void ShowAddonPicker()
        {
            _headerText = "Addons";
            _items.Clear();
            _items.Add(new Item { Label = "<   Media Hub", Action = () => ShowHome() });
            _items.Add(new Item { Label = "+   New addon", Action = () => OpenEditor(null) });
            _items.Add(new Item { Label = "v   Install addon (.addon)...", Action = () => ShowFolder(DefaultBrowseDir(), BrowseMode.Addons) });
            foreach (var manifest in AddonManager.Instance.GetUserAddons())
            {
                string id = manifest.id;
                string name = string.IsNullOrEmpty(manifest.name) ? manifest.id : manifest.name;
                _items.Add(new Item { Label = "Edit: " + name, Action = () => OpenEditor(id) });
            }
            _scroll = 0;
            Refresh();
        }

        private void OpenEditor(string id)
        {
            EnsureEditorUI();
            if (_editorRoot == null)
            {
                return;
            }
            _root.SetActive(false);
            _editingId = id;
            if (id != null && AddonManager.Instance.TryReadAddon(id, out var manifest, out var script))
            {
                _editorName.text = manifest.name ?? string.Empty;
                _editorId.text = manifest.id ?? string.Empty;
                _editorScript.text = script ?? string.Empty;
            }
            else
            {
                _editingId = null;
                _editorName.text = "My Source";
                _editorId.text = "com.you.my-source";
                _editorScript.text = DefaultAddonTemplate;
            }
            _editorOutput.text = AddonManager.ScriptingAvailable
                ? "Edit, then Save (adds it as a hub source) or Export (.addon)."
                : "JS engine not installed - Save/Export work; Test needs Jint (see Addons/README).";
            _editorRoot.SetActive(true);
        }

        private void HandleEditorTap(Vector2 pos)
        {
            if (HitTest(_edBack, pos)) { _editorRoot.SetActive(false); ShowHome(); _root.SetActive(true); return; }
            if (HitTest(_edSave, pos)) { DoSaveAddon(); return; }
            if (HitTest(_edTest, pos)) { DoTestAddon(); return; }
            if (HitTest(_edExport, pos)) { DoExportAddon(); return; }
        }

        private void DoSaveAddon()
        {
            string id = (_editorId.text ?? string.Empty).Trim();
            string name = (_editorName.text ?? string.Empty).Trim();
            string script = _editorScript.text ?? string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                _editorOutput.text = "An Id is required (e.g. com.you.my-source).";
                return;
            }
            if (!string.IsNullOrEmpty(_editingId) && _editingId != id)
            {
                AddonManager.Instance.DeleteAddon(_editingId);
            }
            var manifest = new AddonManifest
            {
                id = id,
                name = name,
                version = "1.0.0",
                type = "media-source",
                entry = "main.js",
                permissions = new string[0],
                minAppVersion = "0.1.0"
            };
            AddonManager.Instance.SaveAddon(manifest, script);
            _editingId = id;
            _editorOutput.text = "Saved. It now appears as [Source] " + (string.IsNullOrEmpty(name) ? id : name) + ".";
        }

        private void DoTestAddon()
        {
            string result = AddonManager.Instance.RunScriptOnce(_editorScript.text ?? string.Empty, "getCatalog", "{}");
            _editorOutput.text = "getCatalog() => " + result;
        }

        private void DoExportAddon()
        {
            DoSaveAddon();
            string id = (_editorId.text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id))
            {
                return;
            }
            string path = AddonManager.Instance.DefaultExportPath(id);
            _editorOutput.text = AddonManager.Instance.ExportAddon(id, path)
                ? "Exported to:\n" + path
                : "Export failed (see Console).";
        }

        // ------------------------------------------------------------------------------------
        // UI construction
        // ------------------------------------------------------------------------------------

        private void Refresh()
        {
            _header.text = _headerText;
            _scroll = Mathf.Clamp(_scroll, 0, MaxScroll());
            int start = _scroll;
            for (int i = 0; i < _rowRects.Count; i++)
            {
                int e = start + i;
                bool used = e < _items.Count;
                _rowRects[i].gameObject.SetActive(used);
                if (used)
                {
                    _rowLabels[i].text = _items[e].Label;
                }
            }
            bool scrollable = MaxScroll() > 0;
            int last = Mathf.Min(start + _rowsPerPage, _items.Count);
            _pageLabel.text = scrollable ? $"{start + 1}-{last} of {_items.Count}  (scroll)" : string.Empty;
            _pageLabel.gameObject.SetActive(scrollable);
            _prevButton.gameObject.SetActive(scrollable);
            _nextButton.gameObject.SetActive(scrollable);
        }

        private bool EnsureUI()
        {
            if (_root != null)
            {
                return true;
            }
            _canvas = FindObjectOfType<Canvas>();
            if (_canvas == null)
            {
                Debug.LogError("MediaHub: no Canvas in the scene to attach to.");
                return false;
            }
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "Segoe UI" }, 16);

            var crect = _canvas.GetComponent<RectTransform>().rect;
            float w = crect.width;
            float h = crect.height;

            var root = NewUI("MediaHub", _canvas.transform);
            SetRect(root, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            root.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.11f, 0.13f, 1f);
            _root = root.gameObject;

            _header = MakeLabel("Header", root, "Media Hub", 26, TextAnchor.MiddleLeft, Color.white);
            SetRect(_header.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(28f, -14f), new Vector2(w - 200f, 46f));

            _closeButton = MakeButton("Close", root, "Close", new Vector2(1f, 1f), new Vector2(-16f, -14f), new Vector2(150f, 46f));

            const float topInset = 80f, bottomInset = 86f;
            _rowsPerPage = Mathf.Max(1, Mathf.FloorToInt((h - topInset - bottomInset) / RowHeight));
            _rowRects.Clear();
            _rowLabels.Clear();
            for (int i = 0; i < _rowsPerPage; i++)
            {
                var rt = NewUI("Row" + i, root);
                rt.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);
                SetRect(rt, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                        new Vector2(0f, -(topInset + i * RowHeight)), new Vector2(-56f, RowHeight - 8f));
                var label = MakeLabel("Label", rt, string.Empty, 20, TextAnchor.MiddleLeft, Color.white);
                SetRect(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                        new Vector2(18f, 0f), new Vector2(-36f, 0f));
                _rowRects.Add(rt);
                _rowLabels.Add(label);
            }

            _prevButton = MakeButton("Prev", root, "< Prev", new Vector2(0.5f, 0f), new Vector2(-w * 0.28f, 18f), new Vector2(150f, 46f));
            _pageLabel = MakeLabel("Page", root, string.Empty, 20, TextAnchor.MiddleCenter, Color.white);
            SetRect(_pageLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(0f, 24f), new Vector2(170f, 40f));
            _nextButton = MakeButton("Next", root, "Next >", new Vector2(0.5f, 0f), new Vector2(w * 0.28f, 18f), new Vector2(150f, 46f));

            _root.SetActive(false);
            return true;
        }

        private void EnsureEditorUI()
        {
            if (_editorRoot != null || !EnsureUI())
            {
                return;
            }
            var crect = _canvas.GetComponent<RectTransform>().rect;
            float w = crect.width;
            float h = crect.height;

            var root = NewUI("AddonEditor", _canvas.transform);
            SetRect(root, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            root.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.11f, 0.13f, 1f);
            _editorRoot = root.gameObject;

            var header = MakeLabel("Header", root, "Addon Editor", 26, TextAnchor.MiddleLeft, Color.white);
            SetRect(header.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(28f, -14f), new Vector2(w - 200f, 40f));

            _edBack = MakeButton("EdBack", root, "< Back", new Vector2(1f, 1f), new Vector2(-16f, -14f), new Vector2(140f, 44f));

            MakeFieldLabel(root, "Name", -64f);
            _editorName = MakeInput("NameInput", root, false);
            SetTopRow((RectTransform)_editorName.transform, -88f, 40f);

            MakeFieldLabel(root, "Id (unique, e.g. com.you.my-source)", -134f);
            _editorId = MakeInput("IdInput", root, false);
            SetTopRow((RectTransform)_editorId.transform, -158f, 40f);

            MakeFieldLabel(root, "Script (main.js)", -204f);
            _editorScript = MakeInput("ScriptInput", root, true);
            SetTopRow((RectTransform)_editorScript.transform, -228f, h - 228f - 160f);

            _editorOutput = MakeLabel("Output", root, string.Empty, 16, TextAnchor.UpperLeft, new Color(0.78f, 0.84f, 0.92f));
            _editorOutput.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetRect(_editorOutput.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                    new Vector2(0f, 64f), new Vector2(-40f, 84f));

            _edSave = MakeButton("EdSave", root, "Save", new Vector2(0f, 0f), new Vector2(20f, 16f), new Vector2(150f, 44f));
            _edTest = MakeButton("EdTest", root, "Test", new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(150f, 44f));
            _edExport = MakeButton("EdExport", root, "Export", new Vector2(1f, 0f), new Vector2(-20f, 16f), new Vector2(150f, 44f));

            _editorRoot.SetActive(false);
        }

        private void MakeFieldLabel(Transform parent, string text, float y)
        {
            var label = MakeLabel("FieldLabel", parent, text, 16, TextAnchor.LowerLeft, new Color(0.7f, 0.75f, 0.82f));
            SetRect(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, y), new Vector2(-40f, 22f));
        }

        private static void SetTopRow(RectTransform rt, float y, float height)
        {
            SetRect(rt, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, y), new Vector2(-40f, height));
        }

        // ------------------------------------------------------------------------------------
        // Small UI helpers (self-contained so the hub has no dependency on the reader)
        // ------------------------------------------------------------------------------------

        private InputField MakeInput(string name, Transform parent, bool multiline)
        {
            var rt = NewUI(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.97f, 0.97f, 0.98f, 1f);
            var input = rt.gameObject.AddComponent<InputField>();

            var textRt = NewUI("Text", rt);
            var text = textRt.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 18;
            text.color = new Color(0.10f, 0.10f, 0.12f);
            text.supportRichText = false;
            text.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = multiline ? VerticalWrapMode.Overflow : VerticalWrapMode.Truncate;
            SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                    new Vector2(10f, 0f), new Vector2(-20f, -8f));

            input.textComponent = text;
            input.targetGraphic = img;
            input.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            input.text = string.Empty;
            return input;
        }

        private RectTransform MakeButton(string name, Transform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rt = NewUI(name, parent);
            rt.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.16f);
            SetRect(rt, anchor, anchor, anchor, pos, size);
            var lbl = MakeLabel(name + "Label", rt, label, 20, TextAnchor.MiddleCenter, Color.white);
            SetRect(lbl.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            return rt;
        }

        private Text MakeLabel(string name, Transform parent, string text, int fontSize, TextAnchor anchor, Color color)
        {
            var rt = NewUI(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = _font;
            t.fontSize = fontSize;
            t.alignment = anchor;
            t.color = color;
            t.text = text;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        private static RectTransform NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void SetRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPos;
        }

        private static bool HitTest(RectTransform rect, Vector2 screenPosition)
        {
            return rect != null && rect.gameObject.activeInHierarchy &&
                   RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null);
        }
    }
}
