using System;
using System.Collections;
using System.Collections.Generic;
using AR7103.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Vuforia;
using Image = UnityEngine.UI.Image;

namespace AR7103.UI
{
    /// <summary>
    /// Runs the animal's minigame on the HUD: intro card, 3-2-1-Go, the game
    /// (top bar with score and round/timer, a hint card, score pop-ups, two choice
    /// buttons for games that want them), then a results card with stars and the
    /// best score, Play again or Done.
    ///
    /// While a game runs (<see cref="Running"/>) gestures and the open palm leave
    /// the animal alone, the normal HUD steps aside, and taps on the AR view come
    /// here (ARTapRouter): a tap on a game piece raises <see cref="PieceTapped"/>,
    /// anywhere else on the floor <see cref="FloorTapped"/> with the spot in stage space.
    /// </summary>
    public class MiniGameHost : MonoBehaviour
    {
        public static bool Running { get; private set; }

        [Header("Top bar")]
        public CanvasGroup gameHud;
        public TMP_Text titleText, roundLabel, roundText, scoreText;
        [Header("Hint card")]
        public CanvasGroup hintGroup;
        public TMP_Text hintTitle, hintBody;
        public Button quitButton;
        [Header("Pop-up")]
        public CanvasGroup popup;
        public TMP_Text popupText;
        public Image popupBg;
        [Header("Choices")]
        public CanvasGroup choiceGroup;
        public Button choiceA, choiceB;
        public TMP_Text choiceALabel, choiceBLabel;
        [Header("Results")]
        public CanvasGroup results;
        public TMP_Text resultsGame, resultsVerdict, resultsScore, resultsUnit, resultsBest, resultsDetail;
        public Image[] stars;
        public Sprite starOn, starOff;
        public Button doneButton, againButton;
        [Header("Sounds")]
        public AudioClip tick, go, point, great, miss, win, over;
        [Header("Colours")]
        public Color good = new Color(0.37f, 0.82f, 0.55f), ok = new Color(0.91f, 0.475f, 0.227f), bad = new Color(0.6f, 0.64f, 0.66f);
        [Tooltip("Who is playing (for distances, facing). Empty = the AR camera.")]
        public Transform viewerOverride;

        public MiniGame Game { get; private set; }
        public int Score { get; set; }
        public int Best { get; private set; }

        public event Action<Vector3> FloorTapped;
        public event Action<GameTapTarget> PieceTapped;
        public event Action<int> Chose;        // 0 = A, 1 = B

        ARHud _hud;
        AudioSource _audio;
        Coroutine _run, _popupCo;
        bool _resultsUp;

        void Awake()
        {
            _hud = GetComponent<ARHud>();
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false; _audio.spatialBlend = 0f;
            foreach (var g in new[] { gameHud, hintGroup, popup, choiceGroup, results }) Show(g, false);
        }

        void Start()
        {
            if (quitButton != null) quitButton.onClick.AddListener(Quit);
            if (doneButton != null) doneButton.onClick.AddListener(Done);
            if (againButton != null) againButton.onClick.AddListener(Again);
            if (choiceA != null) choiceA.onClick.AddListener(() => Chose?.Invoke(0));
            if (choiceB != null) choiceB.onClick.AddListener(() => Chose?.Invoke(1));
        }

        void OnDisable()
        {
            if (Running && Game != null) Game.Cleanup(this);
            Running = false;
        }

        // ------------------------------------------------------------ flow

        /// <summary>The animal in this scene has a game.</summary>
        public MiniGame FindGame()
        {
            var spawn = FindFirstObjectByType<GroundSpawn>(FindObjectsInactive.Include);
            return spawn != null ? spawn.GetComponent<MiniGame>() : null;
        }

        public static string BestKey(MiniGame g) => "ar7103.best." + g.Key;

        public void StartGame()
        {
            if (Running) return;
            Game = FindGame();
            if (Game == null || !Game.isActiveAndEnabled) return;
            // a trick still playing would hand the routine back mid-game when it ended
            var tricks = Game.GetComponent<AnimalTricks>();
            if (tricks != null) tricks.Cancel();
            Running = true;
            Haptics.Light();
            _run = StartCoroutine(Flow());
        }

        IEnumerator Flow()
        {
            Score = 0;
            Best = PlayerPrefs.GetInt(BestKey(Game), 0);
            _resultsUp = false;
            Show(results, false);
            Show(choiceGroup, false);
            SetTitle(Game.Title);
            SetRound("", "");
            SetScore(0);
            Show(gameHud, true);
            Hint(Game.Title, Game.HowTo);
            yield return Wait(2.2f);
            foreach (int n in new[] { 3, 2, 1 })
            {
                if (_hud != null) _hud.ShowCountdown(n);
                Play(tick);
                yield return Wait(0.6f);
            }
            if (_hud != null) _hud.ShowCountdown(0);
            Play(go);
            yield return Game.Run(this);
            Finish();
        }

        void Finish()
        {
            Game.Cleanup(this);
            int s = Score;
            int starsWon = Game.Stars(s);
            bool newBest = s > Best && s > 0;
            if (newBest) { Best = s; PlayerPrefs.SetInt(BestKey(Game), s); }
            Show(choiceGroup, false);
            Show(hintGroup, false);
            Show(gameHud, false);

            if (resultsGame != null) resultsGame.text = Game.Title.ToUpperInvariant();
            if (resultsVerdict != null) resultsVerdict.text = Game.Verdict(s, starsWon);
            if (resultsScore != null) resultsScore.text = s.ToString();
            if (resultsUnit != null) resultsUnit.text = Game.Unit;
            if (resultsBest != null) resultsBest.text = newBest ? "New best!" : $"Best {Best}";
            if (resultsDetail != null) resultsDetail.text = Game.Detail(this);
            if (stars != null)
                for (int i = 0; i < stars.Length; i++)
                    if (stars[i] != null) stars[i].sprite = i < starsWon ? starOn : starOff;
            Show(results, true, interactive: true);
            _resultsUp = true;
            Play(starsWon > 0 ? win : over);
            if (starsWon > 0) Haptics.Success();
            _run = null;
        }

        /// <summary>Leave the game now (the Quit button).</summary>
        public void Quit()
        {
            if (!Running) return;
            if (_run != null) { StopCoroutine(_run); _run = null; }
            if (!_resultsUp && Game != null) Game.Cleanup(this);
            Close();
        }

        void Done() { Haptics.Light(); Close(); }

        void Again()
        {
            Haptics.Light();
            Show(results, false);
            _resultsUp = false;
            _run = StartCoroutine(Flow());
        }

        void Close()
        {
            foreach (var g in new[] { gameHud, hintGroup, popup, choiceGroup, results }) Show(g, false);
            if (_hud != null) _hud.ShowCountdown(0);
            _resultsUp = false;
            Running = false;
            Game = null;
        }

        /// <summary>The results card is up (tests).</summary>
        public bool ShowingResults => _resultsUp;

        // ------------------------------------------------------------ for games

        public void SetTitle(string t) { if (titleText != null) titleText.text = t; }
        public void SetScore(int s) { Score = s; if (scoreText != null) scoreText.text = s.ToString(); }
        public void AddScore(int s) => SetScore(Score + s);

        /// <summary>The middle pill: a label ("ROUND", "TIME") and its value ("3/5", "0:23").</summary>
        public void SetRound(string label, string value)
        {
            if (roundLabel != null) roundLabel.text = label;
            if (roundText != null) roundText.text = value;
        }

        public void SetTimer(float seconds) => SetRound("TIME", $"0:{Mathf.CeilToInt(Mathf.Max(0f, seconds)):00}");

        public void Hint(string title, string body)
        {
            if (hintTitle != null) hintTitle.text = title;
            if (hintBody != null) hintBody.text = body;
            Show(hintGroup, true);
        }

        /// <summary>A short floating message: kind 2 = great (green), 1 = ok (rust), 0 = miss (grey).</summary>
        public void Popup(string text, int kind)
        {
            if (popupText != null) popupText.text = text;
            if (popupBg != null) popupBg.color = kind >= 2 ? good : kind == 1 ? ok : bad;
            Play(kind >= 2 ? great : kind == 1 ? point : miss);
            if (kind >= 1) Haptics.Light();
            if (_popupCo != null) StopCoroutine(_popupCo);
            _popupCo = StartCoroutine(PopupRoutine());
        }

        IEnumerator PopupRoutine()
        {
            if (popup == null) yield break;
            var rt = (RectTransform)popup.transform;
            for (float t = 0f; t < 1.3f; t += Time.deltaTime)
            {
                popup.alpha = t < 0.15f ? t / 0.15f : t > 1.0f ? 1f - (t - 1.0f) / 0.3f : 1f;
                float s = 1f + 0.12f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.25f));
                rt.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            popup.alpha = 0f;
        }

        public void ShowChoices(string a, string b, bool on)
        {
            if (choiceALabel != null) choiceALabel.text = a;
            if (choiceBLabel != null) choiceBLabel.text = b;
            Show(choiceGroup, on, interactive: true);
        }

        public void Play(AudioClip clip, float volume = 0.8f)
        {
            if (clip != null && _audio != null) _audio.PlayOneShot(clip, volume);
        }

        public IEnumerator Wait(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        /// <summary>The player's phone, in world space.</summary>
        public Transform Viewer()
        {
            if (viewerOverride != null) return viewerOverride;
            var vb = Application.isPlaying ? VuforiaBehaviour.Instance : null;
            var cam = vb != null ? vb.GetComponent<Camera>() : Camera.main;
            return cam != null ? cam.transform : null;
        }

        /// <summary>A tap on the AR view while a game runs (ARTapRouter). True = the game took it.</summary>
        public bool HandleTap(Ray ray, Transform stage)
        {
            if (!Running || _resultsUp) return false;
            Physics.SyncTransforms();
            float best = float.MaxValue; GameTapTarget hit = null;
            foreach (var h in Physics.RaycastAll(ray, 50f, ~0, QueryTriggerInteraction.Collide))
            {
                var p = h.collider.GetComponentInParent<GameTapTarget>();
                if (p != null && h.distance < best) { best = h.distance; hit = p; }
            }
            if (hit != null) { PieceTapped?.Invoke(hit); return true; }
            if (stage != null)
            {
                var floor = new Plane(stage.up, stage.position);
                if (floor.Raycast(ray, out float d))
                {
                    FloorTapped?.Invoke(stage.InverseTransformPoint(ray.GetPoint(d)));
                    return true;
                }
            }
            return true;    // a game is on: a tap never moves the animal
        }

        // ------------------------------------------------------------ helpers

        static void Show(CanvasGroup g, bool on, bool interactive = false)
        {
            if (g == null) return;
            g.alpha = on ? 1f : 0f;
            g.blocksRaycasts = on && interactive;
            g.interactable = on && interactive;
        }

        void LateUpdate()
        {
            // the hint card and top bar never take taps; the quit button lives in the hint card's group
            if (hintGroup != null && hintGroup.alpha > 0.5f && Running) { hintGroup.blocksRaycasts = true; hintGroup.interactable = true; }
        }
    }
}
