using System.Collections;
using AR7103.App;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Vuforia;
// Vuforia also defines an Image (its camera-frame type); the UI one is meant here.
using Image = UnityEngine.UI.Image;

namespace AR7103.UI
{
    /// <summary>
    /// Scan overlay for an animal AR scene. Two conditions, in either order, put
    /// the animal on the floor:
    ///
    ///   1. The FLOOR locks: the Plane Finder's automatic hit test has seen a
    ///      surface steadily for <see cref="holdSeconds"/>. A single hit is often a
    ///      transient misread while tracking warms up, so one hit is not enough.
    ///   2. The book PAGE is seen: the image target <see cref="trigger"/> reports
    ///      TRACKED. The page is only the trigger -- the animal stands on the floor
    ///      spot, not on the page.
    ///
    /// If the page shows up while the floor is still being scanned, the animal
    /// appears the moment the floor locks, with no extra step.
    ///
    /// Locking positions the scene's Ground Plane Stage through its
    /// ContentPositioningBehaviour -- the same path the Plane Finder's own
    /// tap-to-place uses -- and records it in <see cref="GroundAnchorStore"/>.
    /// </summary>
    public class GroundScanFlow : MonoBehaviour
    {
        [Header("Vuforia")]
        public PlaneFinderBehaviour planeFinder;
        public ContentPositioningBehaviour contentPositioning;
        [Tooltip("Book page that makes the animal appear. Empty = appear as soon as the floor locks.")]
        public ObserverBehaviour trigger;

        [Header("UI")]
        public CanvasGroup overlay;
        public CanvasGroup fader;
        public TMP_Text title;
        public TMP_Text subtitle;
        public TMP_Text statusLabel;
        public Image statusDot;
        public RectTransform reticle;
        public CanvasGroup reticleGroup;
        public Image reticleRing;
        public Image reticleFill;
        public Button backButton;
        [Tooltip("Card showing the animal's photo, shown while waiting for its book page.")]
        public CanvasGroup pageHint;

        [Header("After locking")]
        [Tooltip("Animal to place on the locked ground. Leave empty to only save the ground.")]
        public GroundSpawn spawn;
        [Tooltip("Go back to the menu once locked (used by the standalone scan scene).")]
        public bool returnToMenu = false;
        public string lockedTitle = "Ground saved";
        public string lockedSubtitle = "The app knows where your floor is.";
        public string pageTitle = "Now find the page";
        public string pageSubtitle = "Point your camera at this animal's page in your book.";

        [Header("Behaviour")]
        [Tooltip("Seconds a surface must stay in view before it is locked.")]
        public float holdSeconds = 0.9f;
        [Tooltip("A gap shorter than this between hits does not reset progress.")]
        public float lostGrace = 0.3f;
        [Tooltip("After this long with no surface, show the tracking tips.")]
        public float hintAfter = 8f;

        [Header("Colours")]
        public Color accent = new Color(0.91f, 0.475f, 0.227f);
        public Color idle = new Color(1f, 1f, 1f, 0.55f);
        public Color success = new Color(0.37f, 0.82f, 0.55f);

        enum State { Searching, Holding, AwaitingPage, Placed }

        State _state = State.Searching;
        HitTestResult _lastHit;
        float _lastHitTime = -10f;
        float _held;
        float _searchStarted;
        bool _hinted;
        bool _pageSeen;

        bool FloorLocked => _state == State.AwaitingPage || _state == State.Placed;

        void OnEnable()
        {
            if (planeFinder != null)
            {
                planeFinder.OnAutomaticHitTest.AddListener(OnAutomaticHit);
                planeFinder.OnInteractiveHitTest.AddListener(OnInteractiveHit);
            }
            if (trigger != null) trigger.OnTargetStatusChanged += OnTriggerStatus;
        }

        void OnDisable()
        {
            if (planeFinder != null)
            {
                planeFinder.OnAutomaticHitTest.RemoveListener(OnAutomaticHit);
                planeFinder.OnInteractiveHitTest.RemoveListener(OnInteractiveHit);
            }
            if (trigger != null) trigger.OnTargetStatusChanged -= OnTriggerStatus;
        }

        void Start()
        {
            GroundAnchorStore.Clear();
            _searchStarted = Time.unscaledTime;
            if (backButton != null) backButton.onClick.AddListener(OnBack);
            SetCopy("Find the floor",
                    "Point your camera at the ground and move your phone slowly.",
                    "Searching", idle);
            if (reticleFill != null) reticleFill.fillAmount = 0f;
            if (pageHint != null) { pageHint.alpha = 0f; pageHint.blocksRaycasts = false; }
            StartCoroutine(UIAnim.Fade(fader, 0f, 0.6f));    // covers camera start-up
        }

        // One-shot: the first real sighting counts. LIMITED is excluded, since it
        // can be reported for a page that is only half-recognised.
        void OnTriggerStatus(ObserverBehaviour b, TargetStatus s)
        {
            if (s.Status == Status.TRACKED || s.Status == Status.EXTENDED_TRACKED)
                _pageSeen = true;
        }

        void OnAutomaticHit(HitTestResult hit)
        {
            if (hit == null || FloorLocked) return;
            _lastHit = hit;
            _lastHitTime = Time.unscaledTime;
        }

        void OnInteractiveHit(HitTestResult hit)
        {
            if (hit == null || FloorLocked) return;
            _lastHit = hit;
            LockFloor();
        }

        void Update()
        {
            if (_state == State.Placed) return;
            if (_state == State.AwaitingPage)
            {
                if (_pageSeen) Place();
                return;
            }

            float dt = Time.unscaledDeltaTime;
            bool seeing = Time.unscaledTime - _lastHitTime < lostGrace;

            // Decays faster than it fills, so a flickering surface cannot creep to a lock
            _held = seeing ? _held + dt : Mathf.Max(0f, _held - dt * 2f);
            if (reticleFill != null)
                reticleFill.fillAmount = Mathf.Lerp(reticleFill.fillAmount, _held / holdSeconds,
                                                    1f - Mathf.Exp(-16f * dt));

            if (seeing && _state == State.Searching)
            {
                _state = State.Holding;
                SetCopy("Hold steady", "Found a surface. Keep it in view.", "Surface found", accent);
            }
            else if (!seeing && _state == State.Holding && _held <= 0f)
            {
                _state = State.Searching;
                SetCopy("Find the floor",
                        "Point your camera at the ground and move your phone slowly.",
                        "Searching", idle);
            }

            if (!_hinted && _state == State.Searching && Time.unscaledTime - _searchStarted > hintAfter)
            {
                _hinted = true;
                subtitle.text = "Try a well-lit floor with some texture. " +
                                "Plain, glossy or very dark surfaces are hard to track.";
            }

            if (_held >= holdSeconds && _lastHit != null) LockFloor();

            // Idle breathing on the reticle while searching. The guide ring stays
            // neutral on purpose: if it also turned accent, the accent progress arc
            // drawn over it would be indistinguishable from it.
            if (reticle != null)
            {
                float pulse = _state == State.Searching
                    ? 1f + 0.035f * Mathf.Sin(Time.unscaledTime * 3.2f)
                    : 1f;
                reticle.localScale = Vector3.Lerp(reticle.localScale, Vector3.one * pulse,
                                                  1f - Mathf.Exp(-10f * dt));
            }
        }

        void LockFloor()
        {
            if (FloorLocked) return;

            // DuplicateStage is off on this Plane Finder, so this moves the one stage
            // rather than spawning a copy -- safe even when a tap already placed it.
            contentPositioning.PositionContentAtPlaneAnchor(_lastHit);
            Transform anchor = contentPositioning.AnchorStage != null
                ? contentPositioning.AnchorStage.transform
                : null;
            GroundAnchorStore.Store(anchor);
            Haptics.Medium();

            if (trigger == null || _pageSeen) { Place(); return; }

            _state = State.AwaitingPage;
            StartCoroutine(PromptForPage());
        }

        IEnumerator PromptForPage()
        {
            // Acknowledge the floor first, so the switch to the page reads as step two
            SetCopy("Floor saved", "Nice. One more step.", "Floor saved", success);
            if (reticleFill != null) { reticleFill.fillAmount = 1f; reticleFill.color = success; }
            yield return PopReticle();
            yield return new WaitForSecondsRealtime(0.6f);
            if (_state != State.AwaitingPage) yield break;        // page already found
            SetCopy(pageTitle, pageSubtitle, "Waiting for the page", accent);
            // The floor reticle means nothing now that the camera should be on the book;
            // in its place, a picture of what to look for
            yield return UIAnim.Fade(reticleGroup, 0f, 0.35f);
            if (_state != State.AwaitingPage || pageHint == null) yield break;
            yield return UIAnim.Fade(pageHint, 1f, 0.35f);
            var rt = (RectTransform)pageHint.transform;
            while (_state == State.AwaitingPage)
            {
                // a slow breathe, so it reads as "waiting for this"
                float s = 1f + 0.018f * Mathf.Sin(Time.unscaledTime * 2.4f);
                rt.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
        }

        void Place()
        {
            if (_state == State.Placed) return;
            _state = State.Placed;

            Transform anchor = GroundAnchorStore.Anchor;
            if (spawn != null && anchor != null)
            {
                // Face the AR camera itself: Camera.main is not guaranteed to be it
                Camera viewer = VuforiaBehaviour.Instance != null
                    ? VuforiaBehaviour.Instance.GetComponent<Camera>()
                    : null;
                spawn.Spawn(anchor, viewer != null ? viewer : Camera.main);
            }

            SetCopy(lockedTitle, lockedSubtitle, "Placed", success);
            Haptics.Success();
            if (pageHint != null && pageHint.alpha > 0f) StartCoroutine(UIAnim.Fade(pageHint, 0f, 0.25f));
            StartCoroutine(Finish());
        }

        IEnumerator PopReticle()
        {
            if (reticleRing != null) reticleRing.color = success;
            yield return UIAnim.Tween(0.28f, t =>
            {
                if (reticle != null)
                    reticle.localScale = Vector3.one * Mathf.Lerp(1f, 1.14f, Mathf.Sin(t * Mathf.PI));
            });
        }

        IEnumerator Finish()
        {
            if (reticleFill != null) { reticleFill.fillAmount = 1f; reticleFill.color = success; }
            if (reticleGroup == null || reticleGroup.alpha > 0.5f) yield return PopReticle();
            yield return new WaitForSecondsRealtime(0.9f);
            if (returnToMenu)
            {
                if (fader != null) fader.blocksRaycasts = true;
                yield return UIAnim.Fade(fader, 1f, 0.35f);
                enabled = false;
                SceneManager.LoadSceneAsync(AppScenes.Entry);
                yield break;
            }
            // Stay in AR: clear the scan guidance away so the animal is unobstructed
            yield return UIAnim.Fade(overlay, 0f, 0.45f);
            if (overlay != null) { overlay.blocksRaycasts = false; overlay.interactable = false; }
            enabled = false;
        }

        void OnBack() => StartCoroutine(BackRoutine());

        IEnumerator BackRoutine()
        {
            if (fader != null) fader.blocksRaycasts = true;
            yield return UIAnim.Fade(fader, 1f, 0.3f);
            SceneManager.LoadSceneAsync(AppScenes.Entry);
        }

        void SetCopy(string t, string sub, string status, Color statusColor)
        {
            if (title != null) title.text = t;
            if (subtitle != null) subtitle.text = sub;
            if (statusLabel != null) statusLabel.text = status;
            if (statusDot != null) statusDot.color = statusColor;
        }
    }
}
