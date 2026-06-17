using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Goliath.Addons
{
    /// <summary>
    /// Plays "audio" media items through an <see cref="AudioSource"/>. Files are decoded with
    /// <see cref="UnityWebRequestMultimedia"/> (ogg / wav / mp3 / aiff, platform permitting). Shows a
    /// simple "now playing" screen: tap to pause/resume, Stop or Esc to close, auto-closes at the end.
    /// <c>MediaItem.url</c> may be a local path or an http(s) URL.
    /// </summary>
    public class AudioPlayerView : MonoBehaviour
    {
        private static AudioPlayerView _instance;

        public static AudioPlayerView Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("AudioPlayerView");
                    _instance = go.AddComponent<AudioPlayerView>();
                }
                return _instance;
            }
        }

        public static bool Active { get; private set; }

        private Canvas _canvas;
        private GameObject _root;
        private Text _title;
        private Text _progress;
        private Text _status;
        private RectTransform _stopButton;
        private AudioSource _audio;
        private Font _font;
        private bool _paused;
        private bool _started;

        [RuntimeInitializeOnLoadMethod]
        private static void Boot()
        {
            MediaRouter.Register("audio", item => Instance.Play(item));
        }

        public void Play(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.url))
            {
                return;
            }
            EnsureUI();
            if (_root == null)
            {
                return;
            }
            StopAllCoroutines();
            _root.SetActive(true);
            _title.text = item.title;
            _progress.text = string.Empty;
            _paused = false;
            _started = false;
            Active = true;
            StartCoroutine(LoadAndPlay(item));
        }

        public void Stop()
        {
            StopAllCoroutines();
            if (_audio != null)
            {
                _audio.Stop();
                _audio.clip = null;
            }
            if (_root != null)
            {
                _root.SetActive(false);
            }
            _started = false;
            Active = false;
        }

        private IEnumerator LoadAndPlay(MediaItem item)
        {
            _status.text = "Loading...";
            string url = (item.url.StartsWith("http://") || item.url.StartsWith("https://"))
                ? item.url
                : new Uri(item.url).AbsoluteUri;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioTypeFor(item.url)))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _status.text = "Cannot play: " + request.error;
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _status.text = "Cannot decode this audio file.";
                    yield break;
                }
                clip.name = item.title;
                _audio.clip = clip;
                _audio.Play();
                _started = true;
                _status.text = string.Empty;
            }
        }

        private void TogglePause()
        {
            if (_audio == null || _audio.clip == null)
            {
                return;
            }
            if (_paused)
            {
                _audio.UnPause();
                _paused = false;
                _status.text = string.Empty;
            }
            else
            {
                _audio.Pause();
                _paused = true;
                _status.text = "Paused - tap to resume";
            }
        }

        private static AudioType AudioTypeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                case ".aiff":
                case ".aif": return AudioType.AIFF;
                default: return AudioType.UNKNOWN;
            }
        }

        private static string FormatTime(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0f)
            {
                seconds = 0f;
            }
            int total = Mathf.FloorToInt(seconds);
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

#if ENABLE_INPUT_SYSTEM
        private void Update()
        {
            if (!Active)
            {
                return;
            }

            if (_audio != null && _audio.clip != null)
            {
                _progress.text = FormatTime(_audio.time) + " / " + FormatTime(_audio.clip.length);
                if (_started && !_paused && !_audio.isPlaying) // reached the end
                {
                    Stop();
                    return;
                }
            }

            var kb = Keyboard.current;
            if (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame))
            {
                Stop();
                return;
            }
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
        }
#endif

        private void HandleTap(Vector2 pos)
        {
            MediaInput.MarkConsumed();
            if (HitTest(_stopButton, pos))
            {
                Stop();
                return;
            }
            TogglePause();
        }

        private void EnsureUI()
        {
            if (_root != null)
            {
                return;
            }
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "Segoe UI" }, 16);

            var canvasGo = new GameObject("AudioCanvas");
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30000;

            var root = NewUI("AudioRoot", canvasGo.transform);
            Stretch(root);
            root.gameObject.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 0.96f);
            _root = root.gameObject;

            var heading = MakeLabel("Heading", root, "Now Playing", 20, TextAnchor.MiddleCenter, new Color(0.7f, 0.75f, 0.82f));
            Place(heading.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 70f), new Vector2(700f, 30f));

            _title = MakeLabel("Title", root, string.Empty, 30, TextAnchor.MiddleCenter, Color.white);
            Place(_title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 24f), new Vector2(820f, 48f));

            _progress = MakeLabel("Progress", root, string.Empty, 22, TextAnchor.MiddleCenter, new Color(0.85f, 0.88f, 0.92f));
            Place(_progress.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -28f), new Vector2(400f, 30f));

            _status = MakeLabel("Status", root, string.Empty, 18, TextAnchor.MiddleCenter, new Color(0.8f, 0.84f, 0.9f));
            Place(_status.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(820f, 28f));

            var hint = MakeLabel("Hint", root, "Tap to pause/resume - Stop or Esc to close", 16, TextAnchor.LowerCenter, new Color(0.6f, 0.65f, 0.72f));
            hint.rectTransform.anchorMin = new Vector2(0f, 0f);
            hint.rectTransform.anchorMax = new Vector2(1f, 0f);
            hint.rectTransform.pivot = new Vector2(0.5f, 0f);
            hint.rectTransform.anchoredPosition = new Vector2(0f, 16f);
            hint.rectTransform.sizeDelta = new Vector2(-40f, 26f);

            _stopButton = NewUI("Stop", root);
            _stopButton.anchorMin = new Vector2(0f, 1f);
            _stopButton.anchorMax = new Vector2(0f, 1f);
            _stopButton.pivot = new Vector2(0f, 1f);
            _stopButton.anchoredPosition = new Vector2(16f, -16f);
            _stopButton.sizeDelta = new Vector2(150f, 44f);
            _stopButton.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.18f);
            var stopLabel = MakeLabel("StopLabel", _stopButton, "< Stop", 20, TextAnchor.MiddleCenter, Color.white);
            Stretch(stopLabel.rectTransform);

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;

            _root.SetActive(false);
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
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private static RectTransform NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }

        private static bool HitTest(RectTransform rect, Vector2 screenPosition)
        {
            return rect != null && rect.gameObject.activeInHierarchy &&
                   RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, null);
        }
    }
}
