using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UEPub;
using TMPro;
using Goliath.Addons;
using UnityEngine;
using UnityEngine.UI;
using System;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Renders an EPUB book into a legacy <see cref="Text"/> component, paginating the
/// content so it fits the text box and letting the user flip through pages.
///
/// The pipeline is:
///   1. <see cref="UEPubReader"/> unzips the epub and exposes each spine document as raw XHTML.
///   2. <see cref="ConvertHtmlToRichText"/> turns a chapter's XHTML into Unity rich-text markup
///      (only the tags legacy Text understands: &lt;b&gt;, &lt;i&gt;, &lt;size&gt;).
///   3. <see cref="Paginate"/> greedily fills pages by measuring text height, keeping rich-text
///      tags balanced across page boundaries.
/// </summary>
public class EbookRenderer : MonoBehaviour
{
    [Header("Scene references")]
    public Text displayText;
    public TMP_Text pageLabel;        // optional "Chapter — Page X of Y" indicator

    [Header("Settings menu")]
    [Tooltip("Panel toggled by tapping the top of the screen. Hidden on start.")]
    public GameObject sizeMenu;
    [Header("  Font size row")]
    public RectTransform decreaseButton;        // 'A-' tap target
    public RectTransform increaseButton;        // 'A+' tap target
    public TMP_Text sizeValueLabel;
    [Header("  Line spacing row")]
    public RectTransform decreaseSpacingButton;
    public RectTransform increaseSpacingButton;
    public TMP_Text spacingValueLabel;
    [Header("  Font row")]
    public RectTransform prevFontButton;
    public RectTransform nextFontButton;
    public TMP_Text fontValueLabel;
    [Header("  Contents")]
    public RectTransform contentsButton;   // opens the chapter list overlay

    [Header("Input")]
    [Tooltip("Fraction of the screen height (from the bottom) reserved for the info bar; taps there don't turn pages.")]
    [Range(0f, 0.3f)] public float bottomDeadZone = 0.1f;
    [Tooltip("Fraction of the screen height (from the top) that opens/closes the settings menu.")]
    [Range(0f, 0.3f)] public float topZone = 0.12f;

    [Header("Text size")]
    public int minFontSize = 8;
    public int maxFontSize = 40;
    public int fontSizeStep = 2;

    [Header("Line spacing")]
    public float minLineSpacing = 0.8f;
    public float maxLineSpacing = 2.5f;
    public float lineSpacingStep = 0.1f;

    [Header("Fonts")]
    [Tooltip("Fonts the reader can cycle through. The body Text's current font is used if this is empty.")]
    public Font[] availableFonts;

    [Header("Book")]
    [Tooltip("Path to the .epub file. Absolute, or relative to the project / working directory.")]
    public string epubPath = "Assets/ebook/Books/austen-pride-and-prejudice-illustrations.epub";

    // Heading sizes are the base font size plus these deltas (index 0 == h1), so headings scale
    // with the reader's chosen text size.
    private static readonly int[] HeadingDeltas = { 12, 8, 6, 4, 2, 1 };

    // Private sentinels used while converting HTML so the "strip every remaining tag" pass
    // does not eat the rich-text tags we intentionally produce. Replaced back to '<'/'>' at the end.
    private const char TagOpenSentinel = (char)1;
    private const char TagCloseSentinel = (char)2;
    // Marks an *intentional* line break (from a block element / <br> / <hr>) so it survives the
    // pass that collapses the source document's own line-wrapping whitespace into single spaces.
    private const char LineBreakMarker = (char)3;

    // Global "last used" preference keys (the default for never-before-opened books). Per-book
    // values and reading position are stored under BookKey() + a suffix.
    private const string KeyFontSize = "ebook.fontSize";
    private const string KeyLineSpacing = "ebook.lineSpacing";
    private const string KeyFontName = "ebook.fontName";

    private UEPubReader _epub;
    private List<string> _pages = new List<string> { string.Empty };
    private List<int> _pageOffsets = new List<int> { 0 }; // visible-char offset at each page start
    private int _currentChapter;
    private int _currentPageIndex;
    private string _currentChapterTitle;
    private bool _bookFinished;
    private int _fontIndex;

    // Table of contents + chapter-list overlay (built lazily at runtime).
    private readonly List<TocItem> _toc = new List<TocItem>();
    private int _navChapterIndex = -1;
    private int _renderedChapter = -1;
    private bool _forceToc;   // TOC shown on demand (menu button), independent of the current chapter
    private GameObject _contentsRoot;
    private Text _tocTitle;
    private Text _tocPager;
    private readonly List<Text> _rowTexts = new List<Text>();
    private int _contentsPage;
    private int _rowsPerPage = 1;
    private const int RowPoolSize = 40;
    private static readonly Color TocLinkColor = new Color(0.13f, 0.42f, 0.85f);   // hyperlink blue
    private static readonly Color TocTitleColor = new Color(0.196f, 0.196f, 0.196f); // matches body text

    private struct TocItem
    {
        public string Title;
        public int Chapter;
    }

    private RectTransform _libraryButton;     // the "Library" entry on the settings menu

    private void Start()
    {
        // Wait one frame so the canvas has laid out and displayText.rect has a real size,
        // otherwise pagination measures against a zero-sized box.
        StartCoroutine(OpenBookNextFrame());
    }

    private IEnumerator OpenBookNextFrame()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        OpenBook();
    }

    private void OpenBook()
    {
        if (displayText == null)
        {
            Debug.LogError("EbookRenderer: displayText is not assigned.");
            return;
        }

        // We do our own pagination, so the box should wrap horizontally and clip vertically.
        displayText.horizontalOverflow = HorizontalWrapMode.Wrap;
        displayText.verticalOverflow = VerticalWrapMode.Truncate;
        displayText.supportRichText = true;

        if (sizeMenu != null)
        {
            sizeMenu.SetActive(false);
        }
        EnsureContentsButton();
        EnsureLibraryButton();

        // Register this reader as the player for ebook media items, so the hub can route to it.
        MediaRouter.Register("ebook", item =>
        {
            if (!string.IsNullOrEmpty(item.url))
            {
                OpenEpub(item.url);
            }
        });

        LoadCurrentBook();
    }

    /// <summary>(Re)loads the book at <see cref="epubPath"/>, restoring its saved prefs and position.</summary>
    private void LoadCurrentBook()
    {
        var resolvedPath = ResolvePath(epubPath);
        _epub = new UEPubReader(resolvedPath);

        if (_epub.chapters == null || _epub.chapters.Count == 0)
        {
            displayText.text = "Could not open book.";
            return;
        }

        // Per-book preferences need the book's identifier, so restore them after the epub is open
        // but before anything paginates.
        LoadSettings();
        if (availableFonts == null || availableFonts.Length == 0)
        {
            availableFonts = BuildDefaultFonts();
        }
        ApplySavedFont();
        UpdateMenuLabels();

        BuildTocModel();
        _renderedChapter = -1;
        _forceToc = false;

        if (!RestoreSavedPosition())
        {
            displayText.text = "No readable content in this book.";
            return;
        }
        AfterNavigate();

        // Record this open in the shared media history (the hub reads this for "Recent").
        RecentsStore.Add(new MediaItem
        {
            type = "ebook",
            url = epubPath,
            title = (_epub.metadata != null && !string.IsNullOrEmpty(_epub.metadata.title))
                ? _epub.metadata.title
                : System.IO.Path.GetFileNameWithoutExtension(epubPath)
        });
    }

    /// <summary>Opens a different epub (used by the MediaHub's "ebook" player registration).</summary>
    public void OpenEpub(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        epubPath = path;
        LoadCurrentBook();
    }

    // ----------------------------------------------------------------------------------------
    // Persistence (PlayerPrefs): global reading preferences + per-book position
    // ----------------------------------------------------------------------------------------

    private void LoadSettings()
    {
        // Per-book value if present, else the global "last used" value, else the current default.
        string bk = BookKey();
        int fontSize = PlayerPrefs.GetInt(bk + ".fontSize", PlayerPrefs.GetInt(KeyFontSize, displayText.fontSize));
        displayText.fontSize = Mathf.Clamp(fontSize, minFontSize, maxFontSize);

        float lineSpacing = PlayerPrefs.GetFloat(bk + ".lineSpacing", PlayerPrefs.GetFloat(KeyLineSpacing, displayText.lineSpacing));
        displayText.lineSpacing = Mathf.Clamp(lineSpacing, minLineSpacing, maxLineSpacing);
    }

    private void ApplySavedFont()
    {
        string bk = BookKey();
        string savedFont = PlayerPrefs.GetString(bk + ".fontName", PlayerPrefs.GetString(KeyFontName, string.Empty));
        _fontIndex = 0;
        for (int i = 0; i < availableFonts.Length; i++)
        {
            if (availableFonts[i] == null)
            {
                continue;
            }
            bool match = string.IsNullOrEmpty(savedFont)
                ? availableFonts[i] == displayText.font
                : availableFonts[i].name == savedFont;
            if (match)
            {
                _fontIndex = i;
                break;
            }
        }
        if (!string.IsNullOrEmpty(savedFont) && availableFonts.Length > 0 && availableFonts[_fontIndex] != null)
        {
            displayText.font = availableFonts[_fontIndex];
        }
    }

    private void SaveSettings()
    {
        string bk = BookKey();
        int fontSize = displayText.fontSize;
        float lineSpacing = displayText.lineSpacing;
        string fontName = displayText.font != null ? displayText.font.name : string.Empty;

        // Save per book, and also as the global "last used" default for never-before-opened books.
        PlayerPrefs.SetInt(bk + ".fontSize", fontSize);
        PlayerPrefs.SetInt(KeyFontSize, fontSize);
        PlayerPrefs.SetFloat(bk + ".lineSpacing", lineSpacing);
        PlayerPrefs.SetFloat(KeyLineSpacing, lineSpacing);
        PlayerPrefs.SetString(bk + ".fontName", fontName);
        PlayerPrefs.SetString(KeyFontName, fontName);
        PlayerPrefs.Save();
    }

    /// <summary>Stable per-book key prefix: the EPUB identifier, else the file name.</summary>
    private string BookKey()
    {
        string id = (_epub != null && _epub.metadata != null) ? _epub.metadata.identifier : null;
        if (string.IsNullOrEmpty(id))
        {
            id = System.IO.Path.GetFileNameWithoutExtension(epubPath ?? string.Empty);
        }
        return "ebook." + id;
    }

    private void SavePosition()
    {
        if (_epub == null || InTocMode)
        {
            return; // don't persist the TOC view as a reading position
        }
        int offset = (_currentPageIndex < _pageOffsets.Count) ? _pageOffsets[_currentPageIndex] : 0;
        string key = BookKey();
        PlayerPrefs.SetInt(key + ".chapter", _currentChapter);
        PlayerPrefs.SetInt(key + ".offset", offset);
        // Not flushed every page turn (avoids disk I/O); flushed on settings change / pause / quit.
    }

    /// <summary>Loads the saved chapter and lands on the page covering the saved character offset.</summary>
    private bool RestoreSavedPosition()
    {
        string key = BookKey();
        int chapter = PlayerPrefs.GetInt(key + ".chapter", 0);
        int offset = PlayerPrefs.GetInt(key + ".offset", 0);

        if (LoadChapter(chapter, goToLastPage: false))
        {
            int idx = 0;
            for (int i = 0; i < _pageOffsets.Count; i++)
            {
                if (_pageOffsets[i] <= offset)
                {
                    idx = i;
                }
                else
                {
                    break;
                }
            }
            _currentPageIndex = idx;
            return true;
        }
        return LoadChapter(0, goToLastPage: false);
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            SavePosition();
            PlayerPrefs.Save();
        }
    }

    private void OnApplicationQuit()
    {
        SavePosition();
        PlayerPrefs.Save();
    }

    /// <summary>Adds a "Library" button just below the settings menu panel (runtime, no scene wiring).</summary>
    private void EnsureLibraryButton()
    {
        if (_libraryButton != null || sizeMenu == null)
        {
            return;
        }
        var panel = sizeMenu.transform as RectTransform;
        if (panel == null)
        {
            return;
        }
        var rt = NewUI("LibraryButtonRuntime", panel);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.85f, 0.91f, 1f, 0.96f);
        // Hang just beneath the panel's bottom edge, centred.
        SetRect(rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                new Vector2(0f, -10f), new Vector2(300f, 46f));
        var label = MakeLabel("LibraryLabel", rt, "Library", 22, TextAnchor.MiddleCenter,
                              new Color(0.12f, 0.12f, 0.14f));
        SetRect(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        _libraryButton = rt;
    }

    private void BuildTocModel()
    {
        _toc.Clear();
        _navChapterIndex = -1;
        if (_epub.toc != null && _epub.chapterHrefs != null)
        {
            foreach (var entry in _epub.toc)
            {
                int idx = _epub.chapterHrefs.FindIndex(h => string.Equals(h, entry.href, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    _toc.Add(new TocItem { Title = entry.title, Chapter = idx });
                }
            }
        }
        if (!string.IsNullOrEmpty(_epub.navHref) && _epub.chapterHrefs != null)
        {
            _navChapterIndex = _epub.chapterHrefs.FindIndex(h => string.Equals(h, _epub.navHref, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback: if the nav/NCX produced nothing usable, derive a chapter list from the spine
        // so the contents feature always has something to show.
        if (_toc.Count == 0)
        {
            for (int i = 0; i < _epub.chapters.Count; i++)
            {
                if (i == _navChapterIndex)
                {
                    continue;
                }
                string title = ExtractChapterTitle(_epub.chapters[i]);
                if (string.IsNullOrEmpty(title))
                {
                    title = $"Section {i + 1}";
                }
                _toc.Add(new TocItem { Title = title, Chapter = i });
            }
        }

    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        if (System.IO.Path.IsPathRooted(path) && System.IO.File.Exists(path))
        {
            return path;
        }
        // Relative paths are resolved against the project root (the working directory in the editor).
        var combined = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), path);
        return System.IO.File.Exists(combined) ? combined : path;
    }

#if ENABLE_INPUT_SYSTEM
    private void Update()
    {
        // Tap / click: right side turns the page forward, left side goes back.
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            HandleTap(mouse.position.ReadValue());
        }

        var touch = Touchscreen.current;
        if (touch != null && touch.primaryTouch.press.wasPressedThisFrame)
        {
            HandleTap(touch.primaryTouch.position.ReadValue());
        }

        // Keyboard is still handy for desktop reading (but not while the hub or a game is on screen).
        var kb = Keyboard.current;
        if (kb != null && !MediaHub.Active && !RetroPlayer.Active && !VideoPlayerView.Active && !AudioPlayerView.Active)
        {
            if (kb.rightArrowKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.pageDownKey.wasPressedThisFrame)
            {
                NextPage();
            }
            else if (kb.leftArrowKey.wasPressedThisFrame || kb.pageUpKey.wasPressedThisFrame)
            {
                PreviousPage();
            }
        }
    }

    private void HandleTap(Vector2 screenPosition)
    {
        // While the media hub is open it owns all input (it reads taps itself). The LastInputFrame
        // check also swallows the tap that just closed the hub, regardless of Update order.
        if (MediaHub.Active || RetroPlayer.Active || VideoPlayerView.Active || AudioPlayerView.Active ||
            Time.frameCount == MediaInput.ConsumedFrame)
        {
            return;
        }

        // On the table-of-contents page, a tap on a chapter hyperlink jumps there and a tap on the
        // footer pager turns the TOC page; anything else falls through to menu handling below.
        if (InTocMode)
        {
            if (TryTapTocRow(screenPosition)) { return; }
            if (TryTapTocPager(screenPosition)) { return; }
        }

        bool menuOpen = sizeMenu != null && sizeMenu.activeSelf;

        if (menuOpen)
        {
            // While the menu is up, taps drive the menu, never page turns.
            if (HitTest(decreaseButton, screenPosition)) { ChangeFontSize(-fontSizeStep); }
            else if (HitTest(increaseButton, screenPosition)) { ChangeFontSize(fontSizeStep); }
            else if (HitTest(decreaseSpacingButton, screenPosition)) { ChangeLineSpacing(-lineSpacingStep); }
            else if (HitTest(increaseSpacingButton, screenPosition)) { ChangeLineSpacing(lineSpacingStep); }
            else if (HitTest(prevFontButton, screenPosition)) { CycleFont(-1); }
            else if (HitTest(nextFontButton, screenPosition)) { CycleFont(1); }
            else if (HitTest(contentsButton, screenPosition)) { sizeMenu.SetActive(false); ShowContents(); }
            else if (HitTest(_libraryButton, screenPosition)) { sizeMenu.SetActive(false); MediaHub.Instance.Show(); }
            else { sizeMenu.SetActive(false); } // tap elsewhere (incl. top zone) closes the menu
            return;
        }

        // Top of the screen opens the settings menu.
        if (sizeMenu != null && screenPosition.y >= Screen.height * (1f - topZone))
        {
            sizeMenu.SetActive(true);
            UpdateMenuLabels();
            return;
        }

        // Bottom strip is reserved for the info bar.
        if (screenPosition.y <= Screen.height * bottomDeadZone)
        {
            return;
        }

        if (screenPosition.x >= Screen.width * 0.5f)
        {
            NextPage();
        }
        else
        {
            PreviousPage();
        }
    }

#endif

    private static bool HitTest(RectTransform rect, Vector2 screenPosition)
    {
        // Screen-Space-Overlay canvas => null camera.
        return rect != null && rect.gameObject.activeInHierarchy &&
               RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null);
    }

    // ----------------------------------------------------------------------------------------
    // Text size
    // ----------------------------------------------------------------------------------------

    public void ChangeFontSize(int delta)
    {
        int newSize = Mathf.Clamp(displayText.fontSize + delta, minFontSize, maxFontSize);
        if (newSize == displayText.fontSize)
        {
            return;
        }
        displayText.fontSize = newSize;
        ReflowPreservingPosition();
        SaveSettings();
    }

    public void ChangeLineSpacing(float delta)
    {
        float newSpacing = Mathf.Clamp(displayText.lineSpacing + delta, minLineSpacing, maxLineSpacing);
        if (Mathf.Approximately(newSpacing, displayText.lineSpacing))
        {
            return;
        }
        displayText.lineSpacing = newSpacing;
        ReflowPreservingPosition();
        SaveSettings();
    }

    /// <summary>
    /// Default font set when none is configured in the inspector: the project's own font plus a few
    /// common OS fonts (Windows/standalone). OS fonts that aren't installed fall back gracefully.
    /// </summary>
    private Font[] BuildDefaultFonts()
    {
        var fonts = new List<Font>();
        if (displayText.font != null)
        {
            fonts.Add(displayText.font);
        }
        foreach (var name in new[] { "Arial", "Times New Roman", "Verdana", "Courier New", "Georgia" })
        {
            var f = Font.CreateDynamicFontFromOSFont(name, displayText.fontSize);
            if (f != null && !fonts.Exists(x => x != null && x.name == f.name))
            {
                fonts.Add(f);
            }
        }
        return fonts.ToArray();
    }

    public void CycleFont(int direction)
    {
        if (availableFonts == null || availableFonts.Length == 0)
        {
            return;
        }
        _fontIndex = ((_fontIndex + direction) % availableFonts.Length + availableFonts.Length) % availableFonts.Length;
        if (availableFonts[_fontIndex] != null)
        {
            displayText.font = availableFonts[_fontIndex];
        }
        ReflowPreservingPosition();
        SaveSettings();
    }

    /// <summary>
    /// Re-paginates the current chapter (font/size/spacing changed page metrics) and lands on the
    /// page that holds the passage the reader was on. Also refreshes the menu labels.
    /// </summary>
    private void ReflowPreservingPosition()
    {
        if (_epub == null)
        {
            UpdateMenuLabels();
            return;
        }

        int targetOffset = (_currentPageIndex < _pageOffsets.Count) ? _pageOffsets[_currentPageIndex] : 0;

        Repaginate(_currentChapter);

        int idx = 0;
        for (int i = 0; i < _pageOffsets.Count; i++)
        {
            if (_pageOffsets[i] <= targetOffset)
            {
                idx = i;
            }
            else
            {
                break;
            }
        }
        _currentPageIndex = idx;
        _bookFinished = false;
        RenderCurrentPage();
        UpdateMenuLabels();
    }

    private void UpdateMenuLabels()
    {
        if (displayText == null)
        {
            return;
        }
        if (sizeValueLabel != null)
        {
            sizeValueLabel.text = $"Size  {displayText.fontSize}";
        }
        if (spacingValueLabel != null)
        {
            spacingValueLabel.text = $"Spacing  {displayText.lineSpacing:0.0}";
        }
        if (fontValueLabel != null)
        {
            fontValueLabel.text = "Font  " + (displayText.font != null ? displayText.font.name : "Default");
        }
    }

    // ----------------------------------------------------------------------------------------
    // Navigation
    // ----------------------------------------------------------------------------------------

    public void NextPage()
    {
        if (_epub == null || _bookFinished)
        {
            return;
        }

        // The table-of-contents page has its own internal pages of chapter links.
        if (InTocMode)
        {
            if (_contentsPage < MaxContentsPage)
            {
                _contentsPage++;
                RenderToc();
                return;
            }
            if (_forceToc)
            {
                _forceToc = false;   // paged past the on-demand TOC -> back to reading
                RenderCurrentPage();
                return;
            }
            if (!LoadChapter(_currentChapter + 1, goToLastPage: false))
            {
                _bookFinished = true;
                HideToc();
                displayText.text = "End of book.";
                return;
            }
            AfterNavigate();
            return;
        }

        if (_currentPageIndex < _pages.Count - 1)
        {
            _currentPageIndex++;
            AfterNavigate();
            return;
        }

        if (!LoadChapter(_currentChapter + 1, goToLastPage: false))
        {
            _bookFinished = true;
            displayText.text = "End of book.";
            return;
        }
        AfterNavigate();
    }

    public void PreviousPage()
    {
        if (_epub == null)
        {
            return;
        }
        _bookFinished = false;

        if (InTocMode)
        {
            if (_contentsPage > 0)
            {
                _contentsPage--;
                RenderToc();
                return;
            }
            if (_forceToc)
            {
                _forceToc = false;   // paged back out of the on-demand TOC -> back to reading
                RenderCurrentPage();
                return;
            }
            if (!LoadChapter(_currentChapter - 1, goToLastPage: true))
            {
                return;
            }
            AfterNavigate();
            return;
        }

        if (_currentPageIndex > 0)
        {
            _currentPageIndex--;
            AfterNavigate();
            return;
        }

        if (!LoadChapter(_currentChapter - 1, goToLastPage: true))
        {
            return; // already at the very beginning
        }
        AfterNavigate();
    }

    /// <summary>
    /// Guarantees a working Contents button even if the serialized scene reference failed to bind:
    /// finds the existing "Contents" button by its label, or builds one at the bottom of the menu.
    /// </summary>
    private void EnsureContentsButton()
    {
        if (contentsButton != null || sizeMenu == null)
        {
            return;
        }

        // Look for the scene's Contents button by the label text on one of its children.
        foreach (var tmp in sizeMenu.GetComponentsInChildren<TMP_Text>(true))
        {
            if (tmp.text != null && tmp.text.Trim() == "Contents")
            {
                contentsButton = (tmp.transform.parent as RectTransform) ?? tmp.rectTransform;
                return;
            }
        }

        // None present -> build one at the bottom of the menu panel.
        var panel = sizeMenu.transform as RectTransform;
        if (panel == null)
        {
            return;
        }
        var rt = NewUI("ContentsButtonRuntime", panel);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.92f);
        SetRect(rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 12f), new Vector2(300f, 44f));
        var label = MakeLabel("ContentsLabel", rt, "Contents", 22, TextAnchor.MiddleCenter,
                              new Color(0.196f, 0.196f, 0.196f));
        SetRect(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        contentsButton = rt;
    }

    /// <summary>Shows the table of contents on demand (from the menu), over the current chapter.</summary>
    public void ShowContents()
    {
        if (_epub == null || _toc.Count == 0)
        {
            return;
        }
        _forceToc = true;
        _contentsPage = 0;
        RenderCurrentPage();
    }

    /// <summary>Jumps straight to a chapter (from a table-of-contents hyperlink or the menu).</summary>
    public void JumpToChapter(int chapterIndex)
    {
        if (_epub == null || chapterIndex < 0 || chapterIndex >= _epub.chapters.Count)
        {
            return;
        }
        _forceToc = false;   // picking a chapter leaves the on-demand TOC
        _bookFinished = false;
        if (LoadChapter(chapterIndex, goToLastPage: false))
        {
            AfterNavigate();
        }
    }

    private void AfterNavigate()
    {
        if (_currentChapter != _renderedChapter)
        {
            _renderedChapter = _currentChapter;
            _contentsPage = 0; // fresh entry into the TOC page starts at its first page
        }
        RenderCurrentPage();
    }

    // ----------------------------------------------------------------------------------------
    // Table of contents page (rendered like a normal reading page, with chapter hyperlinks)
    // ----------------------------------------------------------------------------------------

    private bool InTocMode => _toc.Count > 0 &&
        (_forceToc || (_navChapterIndex >= 0 && _currentChapter == _navChapterIndex));

    private int MaxContentsPage => Mathf.Max(0, (_toc.Count - 1) / Mathf.Max(1, _rowsPerPage));

    private void HideToc()
    {
        if (_contentsRoot != null)
        {
            _contentsRoot.SetActive(false);
        }
    }

    /// <summary>
    /// Lays out the table of contents inside the same area as the reading text: a heading plus one
    /// tappable, hyperlink-coloured row per chapter, paginated to fit. The book background and the
    /// bottom info bar are reused, so it reads as an ordinary page.
    /// </summary>
    private void RenderToc()
    {
        EnsureTocUI();
        if (_contentsRoot == null)
        {
            Debug.LogError("[Ebook] RenderToc: contents UI could not be built.");
            return;
        }
        displayText.text = string.Empty;     // the link rows stand in for the body text
        _contentsRoot.SetActive(true);

        float rowHeight = Mathf.Max(20f, displayText.fontSize * 2f);
        float titleHeight = displayText.fontSize + 6 + 16;
        float pagerHeight = displayText.fontSize * 2.4f;   // reserved footer strip for paging
        float available = displayText.rectTransform.rect.height - titleHeight - pagerHeight;
        _rowsPerPage = Mathf.Clamp(Mathf.FloorToInt(available / rowHeight), 1, _rowTexts.Count);
        _contentsPage = Mathf.Clamp(_contentsPage, 0, MaxContentsPage);

        _tocTitle.font = displayText.font;
        _tocTitle.fontSize = displayText.fontSize + 6;

        bool multiPage = MaxContentsPage > 0;
        _tocPager.gameObject.SetActive(multiPage);
        if (multiPage)
        {
            _tocPager.font = displayText.font;
            _tocPager.fontSize = displayText.fontSize;
            _tocPager.text = $"<  Prev        Page {_contentsPage + 1} of {MaxContentsPage + 1}        Next  >";
        }

        int start = _contentsPage * _rowsPerPage;
        for (int i = 0; i < _rowTexts.Count; i++)
        {
            int e = start + i;
            bool used = i < _rowsPerPage && e < _toc.Count;
            var row = _rowTexts[i];
            row.gameObject.SetActive(used);
            if (used)
            {
                row.font = displayText.font;
                row.fontSize = displayText.fontSize;
                row.text = _toc[e].Title;
                SetRect(row.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                        new Vector2(0f, -(titleHeight + i * rowHeight)), new Vector2(0f, rowHeight));
            }
        }

        if (pageLabel != null)
        {
            pageLabel.text = MaxContentsPage > 0
                ? $"Contents  —  Page {_contentsPage + 1} of {MaxContentsPage + 1}"
                : "Contents";
        }
    }

    private void EnsureTocUI()
    {
        if (_contentsRoot != null)
        {
            return;
        }
        var canvas = displayText.canvas;
        if (canvas == null)
        {
            Debug.LogError("[Ebook] EnsureTocUI: displayText has no Canvas yet.");
            return;
        }
        var src = displayText.rectTransform;

        // Match the reading-text area exactly, with no background, so it looks like a normal page.
        var root = NewUI("TableOfContents", canvas.transform);
        SetRect(root, src.anchorMin, src.anchorMax, src.pivot, src.anchoredPosition, src.sizeDelta);
        _contentsRoot = root.gameObject;

        _tocTitle = MakeLabel("TocTitle", root, "Contents", displayText.fontSize + 6, TextAnchor.UpperLeft, TocTitleColor);
        SetRect(_tocTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -2f), new Vector2(0f, displayText.fontSize + 12));

        _rowTexts.Clear();
        for (int i = 0; i < RowPoolSize; i++)
        {
            var row = MakeLabel("TocRow" + i, root, string.Empty, displayText.fontSize, TextAnchor.UpperLeft, TocLinkColor);
            SetRect(row.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 30f));
            row.gameObject.SetActive(false);
            _rowTexts.Add(row);
        }

        _tocPager = MakeLabel("TocPager", root, string.Empty, displayText.fontSize, TextAnchor.LowerCenter, TocTitleColor);
        SetRect(_tocPager.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 4f), new Vector2(0f, displayText.fontSize * 2.2f));
        _tocPager.gameObject.SetActive(false);

        _contentsRoot.SetActive(false);
    }

    private bool TryTapTocRow(Vector2 screenPosition)
    {
        int start = _contentsPage * _rowsPerPage;
        for (int i = 0; i < _rowsPerPage && i < _rowTexts.Count; i++)
        {
            int e = start + i;
            if (e < _toc.Count && _rowTexts[i].gameObject.activeSelf && HitTest(_rowTexts[i].rectTransform, screenPosition))
            {
                JumpToChapter(_toc[e].Chapter);
                return true;
            }
        }
        return false;
    }

    private bool TryTapTocPager(Vector2 screenPosition)
    {
        if (_tocPager == null || !_tocPager.gameObject.activeSelf || !HitTest(_tocPager.rectTransform, screenPosition))
        {
            return false;
        }
        if (screenPosition.x >= Screen.width * 0.5f) { NextPage(); }
        else { PreviousPage(); }
        return true;
    }

    private RectTransform MakeButton(string name, Transform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var rt = NewUI(name, parent);
        var image = rt.gameObject.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.16f);
        SetRect(rt, anchor, anchor, anchor, pos, size);
        var lbl = MakeLabel(name + "Label", rt, label, 20, TextAnchor.MiddleCenter, Color.white);
        SetRect(lbl.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        return rt;
    }

    private Text MakeLabel(string name, Transform parent, string text, int fontSize, TextAnchor anchor, Color color)
    {
        var rt = NewUI(name, parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = displayText.font;
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

    private void RenderCurrentPage()
    {
        if (InTocMode)
        {
            RenderToc();
            return;
        }

        HideToc();
        displayText.text = _pages[_currentPageIndex];

        if (pageLabel != null)
        {
            string title = string.IsNullOrEmpty(_currentChapterTitle)
                ? $"Chapter {_currentChapter + 1}"
                : _currentChapterTitle;
            pageLabel.text = $"{title}  —  Page {_currentPageIndex + 1} of {_pages.Count}";
        }

        SavePosition();
    }

    /// <summary>
    /// Loads the chapter at <paramref name="index"/> and paginates it. Chapters that contain no
    /// readable text (e.g. pure image/cover pages) are skipped in the direction of travel.
    /// Returns false if there is no readable chapter in that direction.
    /// </summary>
    private bool LoadChapter(int index, bool goToLastPage)
    {
        int step = goToLastPage ? -1 : 1;
        for (int i = index; i >= 0 && i < _epub.chapters.Count; i += step)
        {
            var pg = PaginateChapter(i);
            if (pg.Pages.Count > 0)
            {
                _currentChapter = i;
                _pages = pg.Pages;
                _pageOffsets = pg.Offsets;
                _currentChapterTitle = ExtractChapterTitle(_epub.chapters[i]);
                _currentPageIndex = goToLastPage ? pg.Pages.Count - 1 : 0;
                return true;
            }
        }
        return false;
    }

    /// <summary>Re-paginates the current chapter in place (e.g. after a font-size change).</summary>
    private void Repaginate(int chapter)
    {
        var pg = PaginateChapter(chapter);
        if (pg.Pages.Count == 0)
        {
            pg.Pages.Add(string.Empty);
            pg.Offsets.Add(0);
        }
        _pages = pg.Pages;
        _pageOffsets = pg.Offsets;
    }

    private Pagination PaginateChapter(int chapter)
    {
        var rich = ConvertHtmlToRichText(_epub.chapters[chapter], displayText.fontSize);
        return Paginate(Tokenize(rich));
    }

    // ----------------------------------------------------------------------------------------
    // HTML -> rich text
    // ----------------------------------------------------------------------------------------

    private static string ConvertHtmlToRichText(string html, int baseFontSize)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        const RegexOptions Opt = RegexOptions.Singleline | RegexOptions.IgnoreCase;
        string nl = LineBreakMarker.ToString();
        string br2 = nl + nl; // paragraph / heading separator (one blank line)

        // 1. Drop things that are never visible body text.
        html = Regex.Replace(html, @"<\?xml.*?\?>", string.Empty, Opt);
        html = Regex.Replace(html, @"<!DOCTYPE.*?>", string.Empty, Opt);
        html = Regex.Replace(html, @"<!--.*?-->", string.Empty, Opt);
        html = Regex.Replace(html, @"<head\b.*?</head>", string.Empty, Opt);
        html = Regex.Replace(html, @"<style\b.*?</style>", string.Empty, Opt);
        html = Regex.Replace(html, @"<script\b.*?</script>", string.Empty, Opt);
        html = Regex.Replace(html, @"<svg\b.*?</svg>", string.Empty, Opt);

        // 2. Void/replaced elements that carry meaning. Do these BEFORE the blanket self-closing
        //    removal below so their content/breaks are preserved.
        html = Regex.Replace(html, @"<br\s*/?>", nl, Opt);
        html = Regex.Replace(html, @"<hr\s*/?>", br2 + "* * *" + br2, Opt);
        html = Regex.Replace(html, @"<img\b[^>]*?\balt=""([^""]+)""[^>]*?>", br2 + "[$1]" + br2, Opt);

        // 3. Remove any remaining self-closing tags (e.g. <em/>, <header/>, <footer/>, leftover
        //    <img/>). These have no content, and treating <em/> as an *opening* italic would leave
        //    an unbalanced tag that bleeds formatting across the rest of the chapter.
        html = Regex.Replace(html, @"<[a-zA-Z][^>]*/>", string.Empty, Opt);

        // 4. Headings -> sized + bold (sentinel-marked so the strip pass below leaves them alone).
        //    The (?<!/) guards against any self-closing form sneaking through.
        for (int level = 1; level <= 6; level++)
        {
            int size = baseFontSize + HeadingDeltas[level - 1];
            html = Regex.Replace(html, $@"<h{level}\b[^>]*(?<!/)>",
                br2 + Mark($"size={size}") + Mark("b"), Opt);
            html = Regex.Replace(html, $@"</h{level}\s*>",
                Mark("/b") + Mark("/size") + br2, Opt);
        }

        // 5. Inline emphasis. (?<!/) so a stray self-closing variant is never treated as an open.
        html = Regex.Replace(html, @"<(b|strong)\b[^>]*(?<!/)>", Mark("b"), Opt);
        html = Regex.Replace(html, @"</(b|strong)\s*>", Mark("/b"), Opt);
        html = Regex.Replace(html, @"<(i|em|cite|dfn|var)\b[^>]*(?<!/)>", Mark("i"), Opt);
        html = Regex.Replace(html, @"</(i|em|cite|dfn|var)\s*>", Mark("/i"), Opt);

        // 6. Structural breaks -> line-break markers.
        html = Regex.Replace(html, @"<li\b[^>]*>", nl + "• ", Opt);
        html = Regex.Replace(html, @"<(p|div|blockquote|section|article|header|footer|tr|caption|figcaption|dd|dt)\b[^>]*>", nl, Opt);
        html = Regex.Replace(html, @"</(p|div|blockquote|section|article|header|footer|li|ul|ol|tr|table|figure|caption|dl|dd|dt)\s*>", nl, Opt);

        // 7. Remove every remaining tag (a, span, sup, sub, table cells, ...) keeping inner text.
        html = Regex.Replace(html, @"<[^>]+>", string.Empty, Opt);

        // 8. Decode HTML entities.
        html = System.Net.WebUtility.HtmlDecode(html);

        // 9. Collapse the document's own whitespace (incl. source line-wrapping and &nbsp;) to single
        //    spaces. The break markers are control chars, so \s leaves them untouched.
        html = Regex.Replace(html, @"\s+", " ");
        html = Regex.Replace(html, " ?" + nl + " ?", nl);   // no spaces hugging a break
        html = Regex.Replace(html, nl + "{3,}", br2);        // at most one blank line in a row
        html = html.Replace(LineBreakMarker, '\n');

        // 10. Restore our intentional rich-text tags.
        html = html.Replace(TagOpenSentinel, '<').Replace(TagCloseSentinel, '>');
        return html.Trim();
    }

    private static string Mark(string inner)
    {
        return TagOpenSentinel + inner + TagCloseSentinel;
    }

    /// <summary>
    /// Best-effort chapter title: the document's &lt;title&gt;, else its first heading, else null.
    /// </summary>
    private static string ExtractChapterTitle(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        const RegexOptions Opt = RegexOptions.Singleline | RegexOptions.IgnoreCase;
        foreach (var pattern in new[] { @"<title\b[^>]*>(.*?)</title>", @"<h[1-6]\b[^>]*>(.*?)</h[1-6]>" })
        {
            var m = Regex.Match(html, pattern, Opt);
            if (m.Success)
            {
                var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"<[^>]+>", string.Empty)).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Tokenizing + pagination
    // ----------------------------------------------------------------------------------------

    private enum TokenType { Word, Space, Newline, Tag }

    private struct Token
    {
        public TokenType Type;
        public string Raw;     // for Word/Tag
        public bool IsClose;   // for Tag
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '<')
            {
                int end = s.IndexOf('>', i);
                if (end >= 0)
                {
                    string raw = s.Substring(i, end - i + 1);
                    tokens.Add(new Token { Type = TokenType.Tag, Raw = raw, IsClose = raw.StartsWith("</") });
                    i = end + 1;
                    continue;
                }
                // Stray '<' with no closer: fall through and treat it as a normal character.
            }

            if (c == '\n')
            {
                tokens.Add(new Token { Type = TokenType.Newline });
                i++;
            }
            else if (c == ' ' || c == '\t')
            {
                while (i < n && (s[i] == ' ' || s[i] == '\t'))
                {
                    i++;
                }
                tokens.Add(new Token { Type = TokenType.Space });
            }
            else
            {
                int start = i;
                while (i < n)
                {
                    char d = s[i];
                    if (d == '<' || d == '\n' || d == ' ' || d == '\t')
                    {
                        break;
                    }
                    i++;
                }
                tokens.Add(new Token { Type = TokenType.Word, Raw = s.Substring(start, i - start) });
            }
        }
        return tokens;
    }

    private struct Pagination
    {
        public List<string> Pages;
        public List<int> Offsets; // visible-char offset of each page's start within the chapter
    }

    /// <summary>
    /// Greedily fills pages. Rich-text tags are tracked on a stack so that any tags still open at a
    /// page break are closed at the end of the page and reopened at the start of the next one. Also
    /// records, per page, how many visible characters precede it (used to keep the reader's place
    /// when the text is re-flowed at a different font size).
    /// </summary>
    private Pagination Paginate(List<Token> tokens)
    {
        var pages = new List<string>();
        var offsets = new List<int>();
        var open = new Stack<string>();   // raw open-tag strings, e.g. "<b>", "<size=20>"
        var sb = new StringBuilder();
        bool pageEmpty = true;            // no visible content on the current page yet
        int consumed = 0;                 // visible chars processed so far
        int pageStart = 0;                // 'consumed' value where the current page began

        void StartPage()
        {
            sb.Length = 0;
            foreach (var tag in open.Reverse()) // reopen oldest-first
            {
                sb.Append(tag);
            }
            pageEmpty = true;
            pageStart = consumed;
        }

        void FlushPage()
        {
            if (!pageEmpty)
            {
                pages.Add(sb.ToString() + CloseTagsFor(open));
                offsets.Add(pageStart);
            }
        }

        StartPage();

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case TokenType.Tag:
                    if (token.IsClose)
                    {
                        if (open.Count > 0)
                        {
                            open.Pop();
                        }
                    }
                    else
                    {
                        open.Push(token.Raw);
                    }
                    sb.Append(token.Raw);
                    break;

                case TokenType.Newline:
                    if (!pageEmpty)
                    {
                        sb.Append('\n');
                    }
                    consumed++;
                    break;

                case TokenType.Space:
                    if (!pageEmpty)
                    {
                        sb.Append(' ');
                    }
                    consumed++;
                    break;

                case TokenType.Word:
                    string candidate = sb.ToString() + token.Raw + CloseTagsFor(open);
                    if (!pageEmpty && MeasureHeight(candidate) > PageHeight)
                    {
                        FlushPage();
                        StartPage();   // pageStart excludes this word -> it belongs to the new page
                    }
                    // First word always goes on, even if taller than the box (can't split a word).
                    sb.Append(token.Raw);
                    pageEmpty = false;
                    consumed += token.Raw.Length;
                    break;
            }
        }

        FlushPage();
        return new Pagination { Pages = pages, Offsets = offsets };
    }

    private static string CloseTagsFor(Stack<string> open)
    {
        if (open.Count == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var raw in open) // Stack enumerates top-first => correct closing order
        {
            sb.Append("</").Append(TagName(raw)).Append('>');
        }
        return sb.ToString();
    }

    private static string TagName(string rawOpenTag)
    {
        // "<size=20>" -> "size", "<b>" -> "b"
        const int start = 1;
        int i = start;
        while (i < rawOpenTag.Length)
        {
            char c = rawOpenTag[i];
            if (c == '>' || c == ' ' || c == '=')
            {
                break;
            }
            i++;
        }
        return rawOpenTag.Substring(start, i - start);
    }

    // ----------------------------------------------------------------------------------------
    // Measurement (mirrors how UnityEngine.UI.Text computes preferredHeight)
    // ----------------------------------------------------------------------------------------

    private float PageHeight => displayText.rectTransform.rect.size.y;

    private float MeasureHeight(string text)
    {
        var extents = new Vector2(displayText.rectTransform.rect.size.x, 0f);
        var settings = displayText.GetGenerationSettings(extents);
        return displayText.cachedTextGeneratorForLayout.GetPreferredHeight(text, settings) / displayText.pixelsPerUnit;
    }
}
