using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Goliath.Addons
{
    /// <summary>
    /// Plays "video" media items full-screen using Unity's built-in <see cref="VideoPlayer"/> (works
    /// across iOS / Android / TV / desktop for the platform's supported codecs - mp4/H.264, webm, ...).
    /// Renders to a RenderTexture shown on its own top-most overlay canvas. Tap to pause/resume, the
    /// Stop button or Esc to close.
    /// </summary>
    public class VideoPlayerView : MonoBehaviour
    {
        private static VideoPlayerView _instance;

        public static VideoPlayerView Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("VideoPlayerView");
                    _instance = go.AddComponent<VideoPlayerView>();
                }
                return _instance;
            }
        }

        public static bool Active { get; private set; }

        private Canvas _canvas;
        private GameObject _root;
        private RawImage _image;
        private AspectRatioFitter _fitter;
        private Text _status;
        private RectTransform _stopButton;
        private VideoPlayer _player;
        private AudioSource _audio;
        private RenderTexture _rt;
        private Font _font;

        [RuntimeInitializeOnLoadMethod]
        private static void Boot()
        {
            MediaRouter.Register("video", item => Instance.Play(item));
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
            _root.SetActive(true);
            _status.text = item.title;
            _player.source = VideoSource.Url;
            _player.url = item.url;
            _player.isLooping = false;
            _player.Prepare(); // OnPrepared starts playback once the first frame is ready
            Active = true;
        }

        public void Stop()
        {
            if (_player != null)
            {
                _player.Stop();
            }
            if (_root != null)
            {
                _root.SetActive(false);
            }
            Active = false;
        }

        private void TogglePause()
        {
            if (_player == null)
            {
                return;
            }
            if (_player.isPlaying)
            {
                _player.Pause();
                _status.text = "Paused - tap to resume";
            }
            else
            {
                _player.Play();
                _status.text = string.Empty;
            }
        }

        private void OnPrepared(VideoPlayer vp)
        {
            // Match the render texture to the video's native size so the aspect fit is correct.
            if (vp.width > 0 && vp.height > 0)
            {
                var old = _rt;
                _rt = new RenderTexture((int)vp.width, (int)vp.height, 0);
                vp.targetTexture = _rt;
                _image.texture = _rt;
                if (old != null)
                {
                    old.Release();
                    Destroy(old);
                }
                if (_fitter != null)
                {
                    _fitter.aspectRatio = (float)vp.width / vp.height;
                }
            }
            vp.Play();
            _status.text = string.Empty;
        }

        private void OnLoopPointReached(VideoPlayer vp) => Stop();

#if ENABLE_INPUT_SYSTEM
        private void Update()
        {
            if (!Active)
            {
                return;
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

            var canvasGo = new GameObject("VideoCanvas");
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30000; // above the reader and the hub

            var root = NewUI("VideoRoot", canvasGo.transform);
            Stretch(root);
            root.gameObject.AddComponent<Image>().color = Color.black;
            _root = root.gameObject;

            var imageRt = NewUI("Video", root);
            Stretch(imageRt);
            _image = imageRt.gameObject.AddComponent<RawImage>();
            _image.raycastTarget = false;
            _fitter = imageRt.gameObject.AddComponent<AspectRatioFitter>();
            _fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _fitter.aspectRatio = 16f / 9f;

            _stopButton = NewUI("Stop", root);
            _stopButton.anchorMin = new Vector2(0f, 1f);
            _stopButton.anchorMax = new Vector2(0f, 1f);
            _stopButton.pivot = new Vector2(0f, 1f);
            _stopButton.anchoredPosition = new Vector2(16f, -16f);
            _stopButton.sizeDelta = new Vector2(150f, 44f);
            _stopButton.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.18f);
            var stopLabel = MakeLabel("StopLabel", _stopButton, "< Stop", 20, TextAnchor.MiddleCenter, Color.white);
            Stretch(stopLabel.rectTransform);

            _status = MakeLabel("Status", root, string.Empty, 18, TextAnchor.LowerCenter, new Color(1f, 1f, 1f, 0.85f));
            _status.rectTransform.anchorMin = new Vector2(0f, 0f);
            _status.rectTransform.anchorMax = new Vector2(1f, 0f);
            _status.rectTransform.pivot = new Vector2(0.5f, 0f);
            _status.rectTransform.anchoredPosition = new Vector2(0f, 16f);
            _status.rectTransform.sizeDelta = new Vector2(-40f, 30f);

            _rt = new RenderTexture(1280, 720, 0);
            _image.texture = _rt;

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _rt;
            _player.aspectRatio = VideoAspectRatio.FitInside;

            _audio = gameObject.AddComponent<AudioSource>();
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, _audio);

            _player.prepareCompleted += OnPrepared;
            _player.loopPointReached += OnLoopPointReached;
            _player.errorReceived += (vp, message) =>
            {
                if (_status != null)
                {
                    _status.text = "Cannot play this video: " + message;
                }
            };

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
