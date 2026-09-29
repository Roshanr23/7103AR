using System.Collections;
using AR7103.App;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AR7103.UI
{
    /// <summary>
    /// Drives the entry scene: the animal carousel, the backdrop that follows
    /// it, page dots, and the hand-off into the AR scene.
    ///
    /// No AR runs here. The camera only starts once the user taps Scan, so iOS
    /// asks for camera permission at the moment it is obviously needed rather
    /// than the instant the app opens.
    /// </summary>
    public class EntryScreen : MonoBehaviour
    {
        [Header("Carousel")]
        public SwipeCarousel carousel;
        [Tooltip("Region the carousel may occupy, between header and footer.")]
        public RectTransform carouselArea;
        [Tooltip("Card width / height.")]
        public float cardAspect = 0.8f;
        [Tooltip("Cards never exceed this fraction of the area width, so neighbours peek.")]
        [Range(0.5f, 0.95f)] public float maxCardWidthFraction = 0.74f;
        public float maxCardHeight = 1120f;

        [Header("Page dots")]
        public RectTransform[] dots;
        public Image[] dotImages;
        public float dotIdleWidth = 16f;
        public float dotActiveWidth = 52f;
        public Color dotActive = new Color(0.91f, 0.475f, 0.227f);
        public Color dotIdle = new Color(1f, 1f, 1f, 0.28f);

        [Header("Backdrop")]
        public Image backdropA;
        public Image backdropB;
        public Sprite[] backdrops;
        public float backdropFade = 0.6f;

        [Header("Species")]
        [Tooltip("Scene to open for each card, in carousel order. Empty = no AR model yet.")]
        public string[] arScenes;
        public string[] commonNames;

        [Header("Action button")]
        public TMP_Text actionLabel;
        public TMP_Text helper;
        public Image actionFill;
        [Tooltip("Accent glow under the button; hidden while the button is disabled.")]
        public Image actionGlow;
        public Color fillReady = new Color(0.91f, 0.475f, 0.227f);
        public Color fillDisabled = new Color(1f, 1f, 1f, 0.12f);
        public Color inkReady = new Color(0.118f, 0.063f, 0.031f);
        public Color inkDisabled = new Color(1f, 1f, 1f, 0.45f);

        [Tooltip("Show 'Scan the ground' until a scan has happened. Off now that each " +
                 "animal scene scans its own floor.")]
        public bool requireScanFirst = false;

        /// <summary>Card to show on return, so Back lands where the user left off.</summary>
        public static int LastPage;

        [Header("Flow")]
        public Button scanButton;
        public CanvasGroup content;
        public RectTransform contentMotion;
        public CanvasGroup fader;

        Image _front, _back;
        Coroutine _bgRoutine;
        Vector2Int _laidOutFor;
        bool _leaving;
        string _target;

        void Start()
        {
            _front = backdropA;
            _back = backdropB;
            SetAlpha(_back, 0f);

            ApplyLayout();
            carousel.PageChanged += OnPageChanged;
            int start = Mathf.Clamp(LastPage, 0, Mathf.Max(0, carousel.Count - 1));
            carousel.GoTo(start, instant: true);
            ShowBackdrop(start, instant: true);
            UpdateDots(instant: true);
            RefreshAction(Scanned, instant: true);

            scanButton.onClick.AddListener(OnScan);
            StartCoroutine(Intro());
        }

        void OnDestroy()
        {
            if (carousel != null) carousel.PageChanged -= OnPageChanged;
        }

        void Update()
        {
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (screen != _laidOutFor) ApplyLayout();
            UpdateDots(instant: false);
            // ease the button between its ready and disabled looks
            float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            if (actionFill != null) actionFill.color = Color.Lerp(actionFill.color, _fillTarget, k);
            if (actionLabel != null) actionLabel.color = Color.Lerp(actionLabel.color, _inkTarget, k);
            if (actionGlow != null)
            {
                Color g = actionGlow.color;
                g.a = Mathf.Lerp(g.a, _glowTarget, k);
                actionGlow.color = g;
            }
        }

        float _glowTarget = 0.4f;

        Color _fillTarget, _inkTarget;

        bool Scanned => !requireScanFirst || GroundAnchorStore.HasGround;

        string SceneFor(int page) =>
            arScenes != null && page >= 0 && page < arScenes.Length ? arScenes[page] : null;

        /// <summary>
        /// Before the scan the button always scans. After it, the button follows the
        /// carousel: it opens the centred animal, or greys out for one with no model.
        /// </summary>
        public void RefreshAction(bool scanned, bool instant = false)
        {
            int page = carousel != null ? carousel.Page : 0;
            string name = commonNames != null && page < commonNames.Length ? commonNames[page] : "";
            bool hasAR = !string.IsNullOrEmpty(SceneFor(page));
            bool canAct = !scanned || hasAR;

            if (actionLabel != null)
                actionLabel.text = !scanned ? "Scan the ground" : hasAR ? "View in AR" : "Not in AR yet";
            if (helper != null)
                helper.text = !scanned
                    ? "First, scan your floor so the app knows where the ground is."
                    : hasAR
                        ? $"{name} is ready. Scan your floor, then its page in the book."
                        : $"{name} isn't in AR yet. Try the squirrel, deer or owl.";

            scanButton.interactable = canAct && !_leaving;
            _fillTarget = canAct ? fillReady : fillDisabled;
            _inkTarget = canAct ? inkReady : inkDisabled;
            _glowTarget = canAct ? 0.4f : 0f;
            if (instant)
            {
                if (actionFill != null) actionFill.color = _fillTarget;
                if (actionLabel != null) actionLabel.color = _inkTarget;
            }
        }

        /// <summary>Fit the cards to whatever height this device leaves between header and footer.</summary>
        public void ApplyLayout()
        {
            Canvas.ForceUpdateCanvases();
            Rect area = carouselArea.rect;
            if (area.height <= 1f || area.width <= 1f) return;

            float h = Mathf.Min(area.height, maxCardHeight);
            float w = h * cardAspect;
            float maxW = area.width * maxCardWidthFraction;
            if (w > maxW) { w = maxW; h = w / cardAspect; }

            carousel.Layout(w, h);
            _laidOutFor = new Vector2Int(Screen.width, Screen.height);
        }

        void OnPageChanged(int page)
        {
            ShowBackdrop(page, instant: false);
            RefreshAction(Scanned);
        }

        void UpdateDots(bool instant)
        {
            if (dots == null) return;
            float k = instant ? 1f : 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            for (int i = 0; i < dots.Length; i++)
            {
                bool on = i == carousel.Page;
                Vector2 size = dots[i].sizeDelta;
                size.x = Mathf.Lerp(size.x, on ? dotActiveWidth : dotIdleWidth, k);
                dots[i].sizeDelta = size;
                if (dotImages != null && i < dotImages.Length)
                    dotImages[i].color = Color.Lerp(dotImages[i].color, on ? dotActive : dotIdle, k);
            }
        }

        void ShowBackdrop(int page, bool instant)
        {
            if (backdrops == null || page < 0 || page >= backdrops.Length) return;
            if (instant)
            {
                _front.sprite = backdrops[page];
                SetAlpha(_front, 1f);
                SetAlpha(_back, 0f);
                return;
            }
            if (_bgRoutine != null) StopCoroutine(_bgRoutine);
            _bgRoutine = StartCoroutine(CrossFade(backdrops[page]));
        }

        IEnumerator CrossFade(Sprite next)
        {
            _back.sprite = next;
            _back.transform.SetAsLastSibling();
            float from = _back.color.a;
            yield return UIAnim.Tween(backdropFade, t => SetAlpha(_back, Mathf.Lerp(from, 1f, t)),
                                      UIAnim.EaseInOutCubic);
            SetAlpha(_front, 0f);
            (_front, _back) = (_back, _front);
            _bgRoutine = null;
        }

        IEnumerator Intro()
        {
            if (content != null) content.alpha = 0f;
            Vector2 rest = contentMotion != null ? contentMotion.anchoredPosition : Vector2.zero;
            if (contentMotion != null) contentMotion.anchoredPosition = rest + new Vector2(0f, -36f);

            StartCoroutine(UIAnim.Fade(fader, 0f, 0.5f));
            yield return UIAnim.Tween(0.7f, t =>
            {
                if (content != null) content.alpha = t;
                if (contentMotion != null)
                    contentMotion.anchoredPosition = Vector2.Lerp(rest + new Vector2(0f, -36f), rest, t);
            });
            if (fader != null) fader.blocksRaycasts = false;
        }

        void OnScan()
        {
            if (_leaving) return;
            string target = Scanned ? SceneFor(carousel.Page) : AppScenes.GroundScan;
            if (string.IsNullOrEmpty(target)) return;
            _target = target;
            LastPage = carousel.Page;
            _leaving = true;
            scanButton.interactable = false;
            StartCoroutine(Leave());
        }

        IEnumerator Leave()
        {
            if (fader != null) fader.blocksRaycasts = true;
            yield return UIAnim.Fade(fader, 1f, 0.35f);
            SceneManager.LoadSceneAsync(_target);
        }

        static void SetAlpha(Image img, float a)
        {
            if (img == null) return;
            Color c = img.color;
            c.a = a;
            img.color = c;
        }
    }
}
