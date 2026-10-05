using System.Collections;
using System.Collections.Generic;
using AR7103.App;
using AR7103.Hands;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Vuforia;
using Image = UnityEngine.UI.Image;

namespace AR7103.UI
{
    /// <summary>
    /// Always-on layer for an animal AR scene. Sits above the scan overlay, which
    /// only covers the scanning phase.
    ///
    ///  * Back to the menu, and a mute toggle (remembered between visits).
    ///  * Once placed: a Life size / Small switch (ARSizeToggle) and a photo
    ///    shutter (PhotoCapture).
    ///  * Once the animal is on the floor: a chip naming it. Tapping the chip
    ///    opens a field-guide card with a few facts; tapping again closes it.
    ///  * A coach card that teaches the open-palm gesture, and live feedback while
    ///    a hand is in view -- "Calling the fox..." when the palm is recognised, or
    ///    "Open your hand wide" when a hand is seen but not yet read as a palm.
    ///    The coach stops appearing once the gesture has been used a few times.
    ///  * Tracking tips when the camera struggles (moving too fast, too dark, a
    ///    floor with no texture, relocalising), shown only if the problem persists.
    /// </summary>
    public class ARHud : MonoBehaviour
    {
        public CanvasGroup placedInfo;
        public CanvasGroup fader;
        public Button backButton;
        [Tooltip("Delay after the ground locks before the chip appears, so it follows the animal's arrival.")]
        public float infoDelay = 1.2f;

        [Header("Animal")]
        public string animalName = "animal";

        [Header("Facts")]
        public Button infoButton;
        public CanvasGroup factCard;
        public Button factClose;

        [Header("Open-palm coach")]
        public CanvasGroup coach;
        public CanvasGroup palmPill;
        public TMP_Text palmText;
        [Tooltip("Seconds after the animal appears before the coach card shows.")]
        public float coachDelay = 3.5f;
        [Tooltip("The coach hides itself after this long if ignored, and returns once later.")]
        public float coachShowFor = 12f;
        public float coachReminderAfter = 40f;

        [Header("Tracking tips")]
        public CanvasGroup toast;
        public TMP_Text toastText;
        [Tooltip("A tracking problem must last this long before the tip shows.")]
        public float toastAfter = 0.8f;

        [Header("Gestures and tricks")]
        [Tooltip("The icon in the live-feedback pill, swapped per gesture.")]
        public Image palmIconImage;
        public Sprite iconPalm, iconFist, iconPinch, iconPoint, iconPeace;
        [Tooltip("Big 3-2-1 in the middle of the screen (Say cheese, game starts).")]
        public CanvasGroup countdown;
        public TMP_Text countdownText;

        [Header("Minigame")]
        public Button playButton;
        public TMP_Text playTitle, playBest;
        public MiniGameHost games;

        [Header("Placed-only controls")]
        [Tooltip("The Life size / Small control.")]
        public CanvasGroup sizeGroup;
        [Tooltip("The photo shutter button.")]
        public CanvasGroup shutterGroup;

        [Header("Sound")]
        public Button muteButton;
        public Image muteIcon;
        public Sprite soundOn, soundOff;

        const string MutedKey = "ar7103.muted";
        const string PalmUsesKey = "ar7103.palmUses";
        const int PalmLearnedAfter = 3;

        bool _leaving, _factsOpen, _palmUsedHere, _palmActive, _coachDone;
        float _placedAt = -1f, _palmEndedAt = -99f, _handNoPalmSince = -1f, _problemSince = -1f, _okSince = -1f;
        string _problem;
        HandGestureManager _hands;
        readonly Dictionary<CanvasGroup, float> _restY = new Dictionary<CanvasGroup, float>();

        void OnEnable() => GroundAnchorStore.GroundStored += OnPlaced;

        void OnDisable()
        {
            GroundAnchorStore.GroundStored -= OnPlaced;
            if (_hands != null)
            {
                _hands.onGestureBegan.RemoveListener(OnGestureBegan);
                _hands.onGestureEnded.RemoveListener(OnGestureEnded);
                _hands.onHandLost.RemoveListener(OnHandLost);
            }
        }

        void Start()
        {
            if (backButton != null) backButton.onClick.AddListener(OnBack);
            if (infoButton != null) infoButton.onClick.AddListener(ToggleFacts);
            if (factClose != null) factClose.onClick.AddListener(ToggleFacts);
            if (muteButton != null) muteButton.onClick.AddListener(ToggleMute);
            if (playButton != null) playButton.onClick.AddListener(PlayGame);
            if (countdown != null) { countdown.alpha = 0f; countdown.blocksRaycasts = false; }
            foreach (var g in new[] { placedInfo, factCard, coach, palmPill, toast, sizeGroup, shutterGroup }) Hide(g);
            ApplyMute(PlayerPrefs.GetInt(MutedKey, 0) == 1);
            _coachDone = PlayerPrefs.GetInt(PalmUsesKey, 0) >= PalmLearnedAfter;

            _hands = FindFirstObjectByType<HandGestureManager>();
            if (_hands != null)
            {
                _hands.onGestureBegan.AddListener(OnGestureBegan);
                _hands.onGestureEnded.AddListener(OnGestureEnded);
                _hands.onHandLost.AddListener(OnHandLost);
            }
            StartCoroutine(UIAnim.Fade(fader, 0f, 0.6f));   // covers camera start-up
        }

        void OnPlaced(Transform anchor) { _placedAt = Time.unscaledTime; _hasPlaced = true; }

        bool _hasPlaced;
        bool Placed => _hasPlaced && Time.unscaledTime - _placedAt > infoDelay;

        /// <summary>Tests: behave as if the animal landed <paramref name="secondsAgo"/> seconds ago.</summary>
        public void MarkPlacedForTests(float secondsAgo = 10f)
        {
            _hasPlaced = true;
            _placedAt = Time.unscaledTime - secondsAgo;
        }

        void Update()
        {
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;

            // name chip, or the fact card in its place; all of it steps aside for a minigame
            bool gaming = MiniGameHost.Running;
            if (gaming) _factsOpen = false;
            Blend(placedInfo, Placed && !_factsOpen && !gaming, dt, 2.5f, interactive: true);
            Blend(factCard, _factsOpen, dt, 5f, interactive: true, slide: 40f);
            Blend(sizeGroup, Placed && !gaming, dt, 2.5f, interactive: true);
            Blend(shutterGroup, Placed && !_factsOpen && !gaming, dt, 3f, interactive: true);
            if (countdown != null)
            {
                countdown.alpha = Mathf.MoveTowards(countdown.alpha, _countdownOn ? 1f : 0f, dt * 8f);
                float k = Time.unscaledTime - _countdownAt;
                float s = 1f + 0.35f * Mathf.Exp(-k * 9f);             // each number lands with a little punch
                ((RectTransform)countdown.transform).localScale = new Vector3(s, s, 1f);
            }

            UpdatePalm(now, dt);
            UpdateCoach(now, dt);
            UpdateTracking(now, dt);
        }

        // ------------------------------------------------------------ open palm

        void OnGestureBegan(Gesture g)
        {
            if (g != Gesture.OpenPalm || !Placed) return;
            _palmActive = true;
            Haptics.Light();
            if (!_palmUsedHere)
            {
                _palmUsedHere = true;
                PlayerPrefs.SetInt(PalmUsesKey, PlayerPrefs.GetInt(PalmUsesKey, 0) + 1);
            }
        }

        void OnGestureEnded(Gesture g)
        {
            if (g == Gesture.OpenPalm && _palmActive) { _palmActive = false; _palmEndedAt = Time.unscaledTime; }
        }

        void OnHandLost()
        {
            if (_palmActive) { _palmActive = false; _palmEndedAt = Time.unscaledTime; }
            _handNoPalmSince = -1f;
        }

        void UpdatePalm(float now, float dt)
        {
            if (palmPill == null) return;
            // a hand in view that is not (yet) an open palm, for a moment: coach the pose
            bool handNoPalm = Placed && _hands != null && _hands.HandVisible && !_palmActive &&
                              _hands.CurrentGesture != Gesture.OpenPalm;
            if (handNoPalm) { if (_handNoPalmSince < 0f) _handNoPalmSince = now; }
            else _handNoPalmSince = -1f;
            bool coachPose = handNoPalm && now - _handNoPalmSince > 1.0f;

            bool calling = _palmActive || now - _palmEndedAt < 0.6f;
            bool trick = now < _trickUntil;
            if (palmText != null && (trick || calling || coachPose))
                palmText.text = trick ? _trickText : calling ? $"Calling the {animalName}…" : "Open your hand wide";
            if (palmIconImage != null && (trick || calling || coachPose))
                palmIconImage.sprite = trick && _trickIcon != null ? _trickIcon : iconPalm != null ? iconPalm : palmIconImage.sprite;
            Blend(palmPill, (trick || calling || coachPose) && !_factsOpen && !MiniGameHost.Running, dt, 6f);
        }

        void UpdateCoach(float now, float dt)
        {
            if (coach == null) return;
            bool show = false;
            if (Placed && !_coachDone && !_palmUsedHere && !_factsOpen && !_palmActive)
            {
                float since = now - _placedAt - infoDelay;
                bool first = since > coachDelay && since < coachDelay + coachShowFor;
                bool again = since > coachReminderAfter && since < coachReminderAfter + coachShowFor * 0.75f;
                show = first || again;
            }
            if (palmPill != null && palmPill.alpha > 0.05f) show = false;    // live feedback wins
            Blend(coach, show, dt, 3f, slide: 24f);
        }

        // ------------------------------------------------------------ tracking

        void UpdateTracking(float now, float dt)
        {
            if (toast == null) return;
            string problem = TrackingProblem();
            if (problem != null)
            {
                if (problem != _problem) { _problem = problem; _problemSince = now; }
                _okSince = -1f;
            }
            else if (_okSince < 0f) _okSince = now;

            // a tip needs the problem to persist; it lingers a moment after things recover
            bool recovering = problem == null && _okSince >= 0f && now - _okSince < 0.6f;
            bool show = _problem != null && now - _problemSince > toastAfter && (problem != null || recovering);
            if (problem == null && !recovering) _problem = null;
            if (toastText != null && _problem != null) toastText.text = _problem;
            Blend(toast, show, dt, 5f);
        }

        static string TrackingProblem()
        {
            var vb = VuforiaBehaviour.Instance;
            if (vb == null || vb.DevicePoseBehaviour == null || VuforiaApplication.Instance == null ||
                !VuforiaApplication.Instance.IsRunning) return null;
            switch (vb.DevicePoseBehaviour.TargetStatus.StatusInfo)
            {
                case StatusInfo.EXCESSIVE_MOTION: return "Move your phone a little slower";
                case StatusInfo.INSUFFICIENT_FEATURES: return "Point at a floor with more detail";
                case StatusInfo.INSUFFICIENT_LIGHT: return "It's too dark here – try more light";
                case StatusInfo.RELOCALIZING: return "Finding your floor again…";
                case StatusInfo.INITIALIZING:
                    return Time.timeSinceLevelLoad > 5f ? "Getting ready – move your phone slowly" : null;
                default: return null;
            }
        }

        // ------------------------------------------------------------ facts, sound

        void ToggleFacts()
        {
            if (!Placed || MiniGameHost.Running) return;
            _factsOpen = !_factsOpen;
            if (_factsOpen) RefreshPlay();
            Haptics.Light();
        }

        float _trickUntil = -1f, _countdownAt;
        string _trickText;
        Sprite _trickIcon;
        bool _countdownOn;

        /// <summary>A gesture trick started: say which, with its gesture's icon (AnimalTricks).</summary>
        public void ShowTrick(AnimalTricks.Trick t, string specialName)
        {
            if (MiniGameHost.Running) return;
            (_trickText, _trickIcon) = t switch
            {
                AnimalTricks.Trick.Startle => ("Startled!", iconFist),
                AnimalTricks.Trick.Treat => ("Treat time!", iconPinch),
                AnimalTricks.Trick.Special => ($"{specialName}!", iconPoint),
                AnimalTricks.Trick.Pose => ("Say cheese!", iconPeace),
                _ => ("", null),
            };
            _trickUntil = Time.unscaledTime + (t == AnimalTricks.Trick.Pose ? 3.6f : 1.6f);
            Haptics.Light();
        }

        /// <summary>The big centre number; 0 hides it.</summary>
        public void ShowCountdown(int n)
        {
            _countdownOn = n > 0;
            if (n > 0)
            {
                if (countdownText != null) countdownText.text = n.ToString();
                _countdownAt = Time.unscaledTime;
                Haptics.Tick();
            }
        }

        public bool CountdownShowing => _countdownOn;

        void PlayGame()
        {
            if (games == null) return;
            _factsOpen = false;
            games.StartGame();
        }

        void RefreshPlay()
        {
            var g = games != null ? games.FindGame() : null;
            if (g == null) return;
            if (playTitle != null) playTitle.text = $"Play {g.Title}";
            int best = PlayerPrefs.GetInt(MiniGameHost.BestKey(g), 0);
            if (playBest != null) playBest.text = best > 0 ? $"Best {best} \u00b7 {g.Length}" : g.Length;
        }

        /// <summary>The fact card is showing (or on its way in).</summary>
        public bool FactsOpen => _factsOpen;

        /// <summary>Tapping the animal opens its fact card (ARTapRouter).</summary>
        public void OpenFacts()
        {
            if (!Placed || _factsOpen || MiniGameHost.Running) return;
            _factsOpen = true;
            RefreshPlay();
            Haptics.Light();
        }

        /// <summary>Tapping the floor while the card is open just closes it (ARTapRouter).</summary>
        public void CloseFacts()
        {
            if (!_factsOpen) return;
            _factsOpen = false;
            Haptics.Light();
        }

        /// <summary>The animal is on the floor and the HUD is showing its controls.</summary>
        public bool AnimalPlaced => Placed;

        void ToggleMute()
        {
            bool muted = PlayerPrefs.GetInt(MutedKey, 0) != 1;
            PlayerPrefs.SetInt(MutedKey, muted ? 1 : 0);
            ApplyMute(muted);
            Haptics.Light();
        }

        void ApplyMute(bool muted)
        {
            AudioListener.volume = muted ? 0f : 1f;
            if (muteIcon != null && soundOn != null && soundOff != null) muteIcon.sprite = muted ? soundOff : soundOn;
        }

        // ------------------------------------------------------------ helpers

        static void Hide(CanvasGroup g)
        {
            if (g == null) return;
            g.alpha = 0f; g.blocksRaycasts = false; g.interactable = false;
        }

        /// <summary>Ease a group in or out; optionally rise into place as it appears.</summary>
        void Blend(CanvasGroup g, bool show, float dt, float speed, bool interactive = false, float slide = 0f)
        {
            if (g == null) return;
            g.alpha = Mathf.MoveTowards(g.alpha, show ? 1f : 0f, dt * speed);
            bool live = interactive && show && g.alpha > 0.5f;
            g.blocksRaycasts = live; g.interactable = live;
            if (slide == 0f) return;
            var rt = (RectTransform)g.transform;
            if (!_restY.TryGetValue(g, out float y)) { y = rt.anchoredPosition.y; _restY[g] = y; }
            float e = 1f - (1f - g.alpha) * (1f - g.alpha);
            rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y - slide * (1f - e));
        }

        void OnBack()
        {
            if (_leaving) return;
            _leaving = true;
            Haptics.Light();
            StartCoroutine(Back());
        }

        IEnumerator Back()
        {
            if (fader != null) fader.blocksRaycasts = true;
            yield return UIAnim.Fade(fader, 1f, 0.3f);
            SceneManager.LoadSceneAsync(AppScenes.Entry);
        }
    }
}
