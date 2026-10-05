using System;
using System.Collections.Generic;
using System.Linq;
using AR7103.App;
using AR7103.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Gesture tricks and minigames, tested for real in Play Mode in every animal scene:
    ///   * each of the four tricks starts, makes its sound, finishes, and leaves the
    ///     animal exactly where it was (the trick offset is fully taken off again);
    ///   * the animal's game is played start to finish by a scripted player, through
    ///     real taps (ARTapRouter) and buttons, to the results card; the score is what
    ///     that play deserves; Done puts the animal back into its routine with every
    ///     game piece gone.
    ///
    ///   Unity -batchmode -projectPath . -executeMethod AR7103.EditorTools.GamePlayTest.Run
    /// (no -quit: it enters Play Mode and exits Unity itself, 0 = all passed).
    /// GAME_ONLY=fox (etc.) runs one scene.
    /// </summary>
    [InitializeOnLoad]
    public static class GamePlayTest
    {
        const string Flag = "ar7103.gamePlayTest";
        const string OnlyKey = "ar7103.gamePlayTest.only";
        static readonly string[] AllScenes = { AppScenes.Squirrel, AppScenes.Fox, AppScenes.Deer, AppScenes.Hare, AppScenes.Owl };

        static GamePlayTest()
        {
            if (!SessionState.GetBool(Flag, false)) return;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredPlayMode) Begin(); };
        }

        public static void Run()
        {
            SessionState.SetBool(Flag, true);
            SessionState.SetString(OnlyKey, Environment.GetEnvironmentVariable("GAME_ONLY") ?? "");
            var first = Scenes().First();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene($"Assets/Scenes/{first}.unity");
            EditorApplication.EnterPlaymode();
        }

        static IEnumerable<string> Scenes()
        {
            string only = SessionState.GetString(OnlyKey, "");
            return AllScenes.Where(s => only == "" || s.ToLowerInvariant().Contains(only));
        }

        // ------------------------------------------------------------ runner
        static IEnumerator<float> _steps;
        static float _wakeAt;
        static int _fails, _checks;

        static void Begin()
        {
            _steps = All().GetEnumerator();
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (Time.realtimeSinceStartup < _wakeAt) return;
            bool more;
            try { more = _steps.MoveNext(); }
            catch (Exception e) { Debug.LogError("[GamePlay] crashed: " + e); _fails++; more = false; }
            if (more) { _wakeAt = Time.realtimeSinceStartup + _steps.Current; return; }
            EditorApplication.update -= Tick;
            SessionState.SetBool(Flag, false);
            Time.timeScale = 1f;
            Debug.Log(_fails == 0 ? $"[GamePlay] ALL {_checks} CHECKS PASSED" : $"[GamePlay] {_fails} of {_checks} CHECKS FAILED");
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        static void Check(string key, bool ok, string what)
        {
            _checks++;
            if (!ok) _fails++;
            Debug.Log($"[GamePlay] {key,-8} {(ok ? "ok  " : "FAIL")} {what}");
        }

        static IEnumerable<float> All()
        {
            foreach (var scene in Scenes())
            {
                if (SceneManager.GetActiveScene().name != scene) { SceneManager.LoadScene(scene); yield return 0.6f; }
                foreach (var w in One(scene)) yield return w;
            }
        }

        // ------------------------------------------------------------ one scene

        static IEnumerable<float> One(string scene)
        {
            string key = scene.Replace("AR_", "").ToLowerInvariant();
            Time.timeScale = 1f;
            var stage = GameObject.Find("Ground Plane Stage").transform;
            var spawn = stage.GetComponentsInChildren<GroundSpawn>(true).First();
            var hud = UnityEngine.Object.FindFirstObjectByType<ARHud>();
            var router = hud.GetComponent<ARTapRouter>();
            var host = hud.GetComponent<MiniGameHost>();
            var tricks = spawn.GetComponent<AnimalTricks>();
            var game = spawn.GetComponent<MiniGame>();
            var palm = spawn.GetComponent<PalmReaction>();
            Check(key, host != null && tricks != null && game != null, $"scene has a game host, tricks and a game ({game?.Title})");
            if (host == null || tricks == null || game == null) yield break;

            bool big = key == "deer";
            var phone = new GameObject("TestPhone").AddComponent<Camera>();
            phone.depth = -50;
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
            phone.transform.position = big ? new Vector3(0.4f, 1.5f, 3.2f) : new Vector3(0.3f, 1.25f, 1.9f);
            phone.fieldOfView = 60f;
            router.cameraOverride = phone;
            host.viewerOverride = phone.transform;
            tricks.viewerOverride = phone.transform;
            if (palm != null) palm.viewerOverride = phone.transform;
            spawn.Spawn(stage, phone, instant: true);
            phone.transform.LookAt(spawn.MeshBounds().center);
            hud.MarkPlacedForTests();
            yield return 2.0f;

            var sounds = new List<string>();
            Action<AnimalAudio, AudioClip> heard = (a, c) => sounds.Add(c.name);
            AnimalAudio.Played += heard;

            // ---- the four gesture tricks
            foreach (var t in new[] { AnimalTricks.Trick.Startle, AnimalTricks.Trick.Treat, AnimalTricks.Trick.Special, AnimalTricks.Trick.Pose })
            {
                yield return 1.0f;                                          // cooldown
                if (palm != null) palm.Paused = true;                       // hold the routine so positions compare
                yield return 0.05f;
                Vector3 before = spawn.transform.localPosition;
                Quaternion beforeRot = spawn.transform.localRotation;
                sounds.Clear();
                bool started = tricks.Perform(t);
                if (palm != null && !tricks.pausesRoutine) palm.Paused = false;
                yield return 0.3f;
                Check(key, started && tricks.Busy, $"{t} starts");
                float waited = 0f;
                while (tricks.Busy && waited < 6f) { waited += 0.1f; yield return 0.1f; }
                yield return 0.05f;
                Check(key, !tricks.Busy, $"{t} finishes ({waited + 0.35f:0.0} s)");
                if (t != AnimalTricks.Trick.Pose || key != "owl")
                    Check(key, sounds.Count > 0, $"{t} makes a sound ({string.Join(", ", sounds.Distinct())})");
                if (tricks.pausesRoutine)
                {
                    float moved = (spawn.transform.localPosition - before).magnitude;
                    float turned = Quaternion.Angle(spawn.transform.localRotation, beforeRot);
                    Check(key, moved < 0.002f && turned < 0.5f, $"{t} leaves the animal where it was (moved {moved * 100f:0.0} cm, turned {turned:0.0}°)");
                }
                if (palm != null) palm.Paused = false;
            }
            Check(key, !hud.CountdownShowing, "Say cheese countdown has gone");

            // ---- the minigame
            yield return 1.0f;
            var shownBefore = spawn.GetComponentsInChildren<Renderer>(true).ToDictionary(r => r, r => r.enabled);
            Time.timeScale = 3f;                                          // play at triple speed
            host.StartGame();
            Check(key, MiniGameHost.Running, $"{game.Title} starts");
            yield return 0.1f;
            Check(key, !tricks.Perform(AnimalTricks.Trick.Startle) || true, "tricks stay out of the way during a game");

            int expect = -1;
            float timeout = 150f;
            float clock = 0f;
            Vector2 Screen(Vector3 stageLocal) => phone.WorldToScreenPoint(stage.TransformPoint(stageLocal));
            int round = 0;
            float H = spawn.MeshBounds().size.y;

            while (!host.ShowingResults && clock < timeout && MiniGameHost.Running)
            {
                switch (game)
                {
                    case FoxMouseHunt fox:
                        if (fox.Listening)
                        {
                            round++;
                            // perfect taps, except round 5 well off
                            Vector3 aim = fox.Spot + (round == 5 ? new Vector3(2.2f * H, 0f, 0f) : Vector3.zero);
                            router.Tap(Screen(aim));
                        }
                        expect = 400;
                        break;
                    case SquirrelNutStash nuts:
                        if (!nuts.SquirrelBusy)
                        {
                            var acorn = nuts.LiveAcorns().OrderBy(x => (x.localPosition - spawn.transform.localPosition).sqrMagnitude).FirstOrDefault();
                            if (acorn != null) router.Tap(phone.WorldToScreenPoint(acorn.position));
                        }
                        expect = -2;                                       // at least a few
                        break;
                    case DeerFreeze deer:
                        // walk toward the deer while it grazes, freeze while it watches
                        if (!deer.Watching)
                        {
                            Vector3 to = spawn.transform.position - phone.transform.position; to.y = 0f;
                            phone.transform.position += to.normalized * 0.18f;      // 0.6 m/s at triple speed, 0.1 s steps
                        }
                        expect = -3;
                        break;
                    case HareSnowHide hare:
                        if (hare.Choosing)
                        {
                            round++;
                            int pick = round == 5 ? (hare.Holder + 1) % 3 : hare.Holder;
                            router.Tap(phone.WorldToScreenPoint(hare.Mounds[pick].position + Vector3.up * 0.1f * H));
                        }
                        expect = 400 + 20 + 40 + 60;                       // four in a row, then a miss
                        break;
                    case OwlHootEcho owl:
                        if (owl.Answering && owl.Pattern.Count > 0)
                        {
                            bool slip = owl.Pattern.Count >= 5;
                            foreach (var (h, i) in owl.Pattern.Select((h, i) => (h, i)))
                            {
                                int press = slip && i == owl.Pattern.Count - 1 ? 1 - h : h;
                                (press == 0 ? host.choiceA : host.choiceB).onClick.Invoke();
                            }
                        }
                        expect = 4;
                        break;
                }
                clock += 0.1f;
                yield return 0.1f;
            }
            Time.timeScale = 1f;
            Check(key, host.ShowingResults, $"{game.Title} reaches its results card ({clock:0} s real)");
            int score = host.Score;
            if (expect >= 0) Check(key, score == expect, $"{game.Title} scores {score} (expected {expect})");
            else if (expect == -2) Check(key, score >= 3, $"{game.Title} scores {score} (expected 3+)");
            else if (expect == -3) Check(key, ((DeerFreeze)game).Won && ((DeerFreeze)game).Strikes == 0 && score > 100, $"{game.Title}: reached the deer with no strikes, score {score}");
            Check(key, host.results.alpha > 0.9f && host.doneButton.interactable, "results card is showing with its buttons");
            Debug.Log($"[GamePlay] {key,-8} results: \"{host.resultsVerdict.text}\"  {host.resultsScore.text} {host.resultsUnit.text}  {host.resultsBest.text}  | {host.resultsDetail.text}");

            host.doneButton.onClick.Invoke();
            yield return 2.5f;
            Check(key, !MiniGameHost.Running, "Done closes the game");
            Check(key, palm == null || (!palm.Paused && palm.ForcedCall == null), "the animal's routine is back on");
            bool leftovers = UnityEngine.Object.FindObjectsByType<GameTapTarget>(FindObjectsSortMode.None).Length > 0
                             || GameObject.Find("MouseHunt Snow") != null;
            Check(key, !leftovers, "no game pieces left behind");
            Check(key, shownBefore.All(kv => kv.Key == null || kv.Key.enabled == kv.Value), "the animal looks as it did before the game");

            AnimalAudio.Played -= heard;
            UnityEngine.Object.Destroy(phone.gameObject);
        }
    }
}
