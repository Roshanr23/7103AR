using System;
using System.Collections.Generic;
using System.Linq;
using AR7103.App;
using AR7103.Hands;
using AR7103.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Every minigame, every way it can go, in Play Mode. Each scenario reloads the
    /// animal's scene, places the animal in front of a stand-in phone and plays at
    /// four times speed:
    ///   perfect   - a flawless player: exact expected score
    ///   idle      - nobody plays: every timeout path
    ///   bad       - wrong answers / far taps / moving while watched / frantic re-targeting
    ///   quit      - Quit halfway
    ///   again     - finish, Play again, finish again: best score kept
    ///   small     - "Small" mode (stage at half scale), perfect play
    ///   noise     - gestures, the palm and floor taps thrown at it while it plays
    ///   midtrick  - a game started while a gesture trick is still playing
    ///   leave     - the scene left mid-game
    ///   card      - started from the field card's Play button
    /// After each: no errors from our code, no pieces left, the routine unpaused, the
    /// animal looking as before, and -- a few seconds later -- back in its routine.
    ///
    ///   Unity -batchmode -projectPath . -executeMethod AR7103.EditorTools.GameStressTest.Run
    /// STRESS_ONLY=fox and/or STRESS_CASE=quit narrow it down. Exits 0 when all pass.
    /// </summary>
    [InitializeOnLoad]
    public static class GameStressTest
    {
        const string Flag = "ar7103.gameStress";
        static readonly string[] AllScenes = { AppScenes.Squirrel, AppScenes.Fox, AppScenes.Deer, AppScenes.Hare, AppScenes.Owl };
        static readonly string[] AllCases = { "perfect", "idle", "bad", "quit", "again", "small", "noise", "midtrick", "leave", "card", "close" };
        const float Speed = 4f;

        static GameStressTest()
        {
            if (!SessionState.GetBool(Flag, false)) return;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredPlayMode) Begin(); };
        }

        public static void Run()
        {
            SessionState.SetBool(Flag, true);
            SessionState.SetString(Flag + ".only", Environment.GetEnvironmentVariable("STRESS_ONLY") ?? "");
            SessionState.SetString(Flag + ".case", Environment.GetEnvironmentVariable("STRESS_CASE") ?? "");
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene($"Assets/Scenes/{AllScenes[0]}.unity");
            EditorApplication.EnterPlaymode();
        }

        // ------------------------------------------------------------ runner
        static IEnumerator<float> _steps;
        static float _wakeAt;
        static int _fails, _checks;
        static readonly List<string> _errors = new List<string>();
        static readonly List<string> _failed = new List<string>();

        static void Begin()
        {
            Application.logMessageReceived += OnLog;
            _steps = All().GetEnumerator();
            EditorApplication.update += Tick;
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            if (msg.StartsWith("[Stress]")) return;
            // ours only: Vuforia complains in a headless editor (no webcam), that is not a game bug
            if ((stack ?? "").Contains("AR7103") || msg.Contains("AR7103")) _errors.Add(msg.Split('\n')[0]);
        }

        static void Tick()
        {
            if (Time.realtimeSinceStartup < _wakeAt) return;
            bool more;
            try { more = _steps.MoveNext(); }
            catch (Exception e) { Debug.LogError("[Stress] crashed: " + e); _fails++; more = false; }
            if (more) { _wakeAt = Time.realtimeSinceStartup + _steps.Current; return; }
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            SessionState.SetBool(Flag, false);
            Time.timeScale = 1f;
            foreach (var f in _failed) Debug.Log("[Stress] FAILED: " + f);
            Debug.Log(_fails == 0 ? $"[Stress] ALL {_checks} CHECKS PASSED" : $"[Stress] {_fails} of {_checks} CHECKS FAILED");
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        static string _label;
        static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) { _fails++; _failed.Add($"{_label}: {what}"); }
            Debug.Log($"[Stress] {_label,-18} {(ok ? "ok  " : "FAIL")} {what}");
        }

        static IEnumerable<float> All()
        {
            string only = SessionState.GetString(Flag + ".only", ""), onlyCase = SessionState.GetString(Flag + ".case", "");
            foreach (var scene in AllScenes.Where(s => only == "" || s.ToLowerInvariant().Contains(only)))
                foreach (var c in AllCases.Where(c => onlyCase == "" || onlyCase.Split(',').Contains(c)))
                    foreach (var w in Scenario(scene, c)) yield return w;
        }

        // ------------------------------------------------------------ one scenario

        class Ctx
        {
            public string key;
            public Transform stage;
            public GroundSpawn spawn;
            public ARHud hud;
            public ARTapRouter router;
            public MiniGameHost host;
            public AnimalTricks tricks;
            public MiniGame game;
            public PalmReaction palm;
            public Camera phone;
            public float H;                  // animal height, world m
            public Vector3 home;             // stage local
            public int round;
            public float clock;
            public System.Random rng = new System.Random(7);
            public Vector2 Screen(Vector3 stageLocal) => phone.WorldToScreenPoint(stage.TransformPoint(stageLocal));
        }

        static IEnumerable<float> Scenario(string scene, string kind)
        {
            Time.timeScale = 1f;
            string key = scene.Replace("AR_", "").ToLowerInvariant();
            if (kind == "close" && key != "deer") yield break;          // the deer's own edge case
            _label = $"{key}/{kind}";
            _errors.Clear();
            PlayerPrefs.DeleteKey("ar7103.best." + key);
            SceneManager.LoadScene(scene);
            yield return 0.8f;

            var c = Setup(key, kind == "small" ? 0.5f : 1f);
            if (c == null) { Check(false, "scene set up"); yield break; }
            yield return 2.2f;
            var shownBefore = c.spawn.GetComponentsInChildren<Renderer>(true).ToDictionary(r => r, r => r.enabled);
            int movesBefore = c.router.MoveRequests;

            bool reloaded = false;
            switch (kind)
            {
                case "perfect":
                case "small":
                    foreach (var w in Play(c, Perfect, 200f)) yield return w;
                    ExpectPerfect(c);
                    break;
                case "idle":
                    foreach (var w in Play(c, (x) => { }, 260f)) yield return w;
                    ExpectIdle(c);
                    break;
                case "bad":
                    foreach (var w in Play(c, Bad, 260f)) yield return w;
                    ExpectBad(c);
                    break;
                case "quit":
                {
                    c.host.StartGame();
                    Time.timeScale = Speed;
                    for (float t = 0f; t < 4f; t += 0.1f) yield return 0.1f;          // idle, so it is surely mid-game
                    Check(MiniGameHost.Running && !c.host.ShowingResults, "still playing when Quit is pressed");
                    c.host.quitButton.onClick.Invoke();
                    yield return 0.1f;
                    Check(!MiniGameHost.Running, "Quit ends the game at once");
                    Check(!c.host.ShowingResults && c.host.results.alpha < 0.01f, "Quit shows no results card");
                    break;
                }
                case "again":
                {
                    Strategy fast = c.game is OwlHootEcho ? Bad : (Strategy)Perfect;
                    foreach (var w in Play(c, fast, 200f, pressDone: false)) yield return w;
                    int first = c.host.Score;
                    Check(c.host.ShowingResults, $"first game finished ({first})");
                    c.host.againButton.onClick.Invoke();
                    yield return 0.2f;
                    Check(MiniGameHost.Running && !c.host.ShowingResults && c.host.results.alpha < 0.01f, "Play again restarts with the results card gone");
                    Check(c.host.Score == 0, "score reset for the new game");
                    c.round = 0;
                    foreach (var w in Drive(c, (x) => { }, 260f)) yield return w;   // second game: nobody plays
                    Check(c.host.ShowingResults, "second game finished");
                    Check(PlayerPrefs.GetInt("ar7103.best." + key, 0) == first && c.host.resultsBest.text.Contains(first.ToString()),
                          $"best score kept from the first game ({PlayerPrefs.GetInt("ar7103.best." + key, 0)}, card says \"{c.host.resultsBest.text}\")");
                    c.host.doneButton.onClick.Invoke();
                    yield return 0.2f;
                    break;
                }
                case "noise":
                {
                    var hands = UnityEngine.Object.FindFirstObjectByType<HandGestureManager>();
                    var stray = new List<AnimalTricks.Trick>();
                    Action<AnimalTricks, AnimalTricks.Trick> seen = (a, t) => { if (t == AnimalTricks.Trick.Treat || t == AnimalTricks.Trick.Pose) stray.Add(t); };
                    AnimalTricks.Performed += seen;
                    var gestures = new[] { Gesture.Fist, Gesture.Pinch, Gesture.Point, Gesture.Peace, Gesture.OpenPalm };
                    int n = 0;
                    Strategy noisy = (x) =>
                    {
                        Perfect(x);
                        n++;
                        if (n % 6 == 0 && hands != null) hands.onGestureBegan.Invoke(gestures[(n / 6) % gestures.Length]);
                        if (n % 9 == 0 && x.palm != null) x.palm.PalmUp();
                        if (n % 7 == 0 && !(x.game is FoxMouseHunt) && !(x.game is SquirrelNutStash))
                            x.router.Tap(x.Screen(x.home + new Vector3(0.9f, 0f, -0.9f) * x.H));     // stray floor tap
                    };
                    foreach (var w in Play(c, noisy, 220f)) yield return w;
                    AnimalTricks.Performed -= seen;
                    if (c.palm != null) c.palm.PalmDown();
                    Check(stray.Count == 0, $"gestures did not start tricks during the game ({stray.Count})");
                    Check(c.router.MoveRequests == movesBefore, "no tap during the game asked to move the animal");
                    ExpectPerfect(c, noisy: true);
                    break;
                }
                case "midtrick":
                {
                    c.tricks.Perform(AnimalTricks.Trick.Treat);
                    yield return 0.4f;
                    Check(c.tricks.Busy, "a trick is playing");
                    c.host.StartGame();
                    yield return 0.1f;
                    Check(!c.tricks.Busy, "starting the game cancelled the trick");
                    yield return 5.0f;                                     // past the trick's end and the 3-2-1
                    if (c.palm is FoxWalk) Check(c.palm.Paused, "the fox stays held by the game after the trick's end time");
                    Check(!c.tricks.Busy && MiniGameHost.Running, "the game is still running, the trick still gone");
                    Time.timeScale = Speed;
                    c.round = 0;
                    foreach (var w in Drive(c, c.game is OwlHootEcho ? Bad : (Strategy)Perfect, 200f)) yield return w;
                    Check(c.host.ShowingResults, "the game played through");
                    c.host.doneButton.onClick.Invoke();
                    yield return 0.2f;
                    break;
                }
                case "leave":
                {
                    c.host.StartGame();
                    Time.timeScale = Speed;
                    for (float t = 0f; t < 3f; t += 0.1f) { Perfect(c); yield return 0.1f; }
                    Time.timeScale = 1f;
                    SceneManager.LoadScene(scene);                         // what Back does, minus the fade
                    yield return 1.0f;
                    Check(!MiniGameHost.Running, "leaving the scene mid-game clears the running flag");
                    reloaded = true;
                    break;
                }
                case "close":
                {
                    // starts too close and never steps back: no instant win, still winnable by creeping
                    Vector3 d = c.phone.transform.position - c.spawn.transform.position; d.y = 0f;
                    c.phone.transform.position = c.spawn.transform.position + d.normalized * 1.5f + Vector3.up * c.phone.transform.position.y;
                    foreach (var w in Play(c, Perfect, 200f)) yield return w;
                    var deer = (DeerFreeze)c.game;
                    Check(deer.Won && c.host.Score < 545, $"no instant win from too close, but still won by creeping ({c.host.resultsDetail.text}, {c.host.Score})");
                    break;
                }
                case "card":
                {
                    c.hud.OpenFacts();
                    yield return 0.6f;
                    Check(c.hud.FactsOpen && c.hud.playButton.interactable, "the card is open with its Play button");
                    Check(c.hud.playTitle.text == $"Play {c.game.Title}", $"Play button names the game (\"{c.hud.playTitle.text}\")");
                    c.hud.playButton.onClick.Invoke();
                    yield return 0.3f;
                    Check(MiniGameHost.Running && !c.hud.FactsOpen, "Play starts the game and closes the card");
                    yield return 0.6f;
                    Check(c.hud.placedInfo.alpha < 0.05f && c.hud.shutterGroup.alpha < 0.05f && c.hud.sizeGroup.alpha < 0.05f,
                          "the normal HUD steps aside during the game");
                    c.host.quitButton.onClick.Invoke();
                    yield return 1.0f;
                    Check(c.hud.placedInfo.alpha > 0.5f, "the normal HUD comes back after");
                    break;
                }
            }

            Time.timeScale = 1f;
            if (!reloaded)
            {
                foreach (var w in AfterGame(c, shownBefore)) yield return w;
            }
            Check(_errors.Count == 0, _errors.Count == 0 ? "no errors from our code" : $"errors: {string.Join(" | ", _errors.Distinct().Take(4))}");
        }

        static Ctx Setup(string key, float stageScale)
        {
            var stageGo = GameObject.Find("Ground Plane Stage");
            if (stageGo == null) return null;
            var c = new Ctx { key = key, stage = stageGo.transform };
            c.spawn = c.stage.GetComponentsInChildren<GroundSpawn>(true).First();
            c.hud = UnityEngine.Object.FindFirstObjectByType<ARHud>();
            c.router = c.hud.GetComponent<ARTapRouter>();
            c.host = c.hud.GetComponent<MiniGameHost>();
            c.tricks = c.spawn.GetComponent<AnimalTricks>();
            c.game = c.spawn.GetComponent<MiniGame>();
            c.palm = c.spawn.GetComponent<PalmReaction>();
            c.stage.position = Vector3.zero; c.stage.rotation = Quaternion.identity;
            c.stage.localScale = Vector3.one * stageScale;
            var size = c.hud.GetComponent<ARSizeToggle>();
            if (size != null) size.enabled = false;                       // the test sets the stage scale itself

            bool big = key == "deer";
            c.phone = new GameObject("TestPhone").AddComponent<Camera>();
            c.phone.depth = -50;
            c.phone.transform.position = (big ? new Vector3(0.4f, 1.5f, 3.2f) : new Vector3(0.3f, 1.25f, 1.9f));
            c.phone.fieldOfView = 60f;
            c.router.cameraOverride = c.phone;
            c.host.viewerOverride = c.phone.transform;
            c.tricks.viewerOverride = c.phone.transform;
            if (c.palm != null) c.palm.viewerOverride = c.phone.transform;
            c.spawn.Spawn(c.stage, c.phone, instant: true);
            c.phone.transform.LookAt(c.spawn.MeshBounds().center);
            c.hud.MarkPlacedForTests();
            c.H = c.spawn.MeshBounds().size.y;
            c.home = c.spawn.transform.localPosition;
            return c;
        }

        delegate void Strategy(Ctx c);
        static int _blipChecked = -1;

        /// <summary>Start the game, play it with a strategy at four times speed, check it ends, then Done.</summary>
        static IEnumerable<float> Play(Ctx c, Strategy player, float timeout, bool pressDone = true)
        {
            c.host.StartGame();
            Check(MiniGameHost.Running, $"{c.game.Title} starts");
            Time.timeScale = Speed;
            c.round = 0; c.clock = 0f;
            foreach (var w in Drive(c, player, timeout)) yield return w;
            Check(c.host.ShowingResults, $"reaches its results card ({c.clock:0} s real)");
            Check(c.host.results.alpha > 0.9f && c.host.doneButton.interactable && c.host.againButton.interactable, "results card shows with working buttons");
            Debug.Log($"[Stress] {_label,-18} results: \"{c.host.resultsVerdict.text}\" {c.host.resultsScore.text} {c.host.resultsUnit.text} | {c.host.resultsBest.text} | {c.host.resultsDetail.text}");
            if (pressDone) { c.host.doneButton.onClick.Invoke(); yield return 0.2f; Check(!MiniGameHost.Running, "Done closes it"); }
        }

        static IEnumerable<float> Drive(Ctx c, Strategy player, float timeout)
        {
            c.clock = 0f;
            float farthest = 0f;
            float Hs = c.H / Mathf.Max(c.stage.lossyScale.x, 1e-5f);
            while (!c.host.ShowingResults && MiniGameHost.Running && c.clock < timeout)
            {
                // the animal must stay in its patch of floor however the game goes
                if (c.game is FoxMouseHunt || c.game is SquirrelNutStash)
                    farthest = Mathf.Max(farthest, (c.spawn.transform.localPosition - c.home).magnitude / Hs);
                player(c);
                c.clock += 0.1f;
                yield return 0.1f;
            }
            Time.timeScale = 1f;
            if (c.game is FoxMouseHunt) Check(farthest < 2.4f, $"the fox stays near its spot ({farthest:0.0} fox heights at most)");
            if (c.game is SquirrelNutStash) Check(farthest < 3.3f, $"the squirrel stays near its spot ({farthest:0.0} squirrel heights at most)");
        }

        // ------------------------------------------------------------ players

        static void Perfect(Ctx c)
        {
            switch (c.game)
            {
                case FoxMouseHunt fox:
                    if (fox.Listening) { c.round++; c.router.Tap(c.Screen(fox.Spot)); }
                    break;
                case SquirrelNutStash nuts:
                    if (!nuts.SquirrelBusy)
                    {
                        var a = nuts.LiveAcorns().OrderBy(x => (x.localPosition - c.spawn.transform.localPosition).sqrMagnitude).FirstOrDefault();
                        if (a != null) c.router.Tap(c.phone.WorldToScreenPoint(a.position));
                    }
                    break;
                case DeerFreeze deer:
                    if (deer.Playing && !deer.Watching)
                    {
                        Vector3 to = c.spawn.transform.position - c.phone.transform.position; to.y = 0f;
                        if (to.magnitude > 0.2f) c.phone.transform.position += to.normalized * 0.18f;
                    }
                    break;
                case HareSnowHide hare:
                    if (hare.Choosing && _blipChecked != c.round)
                    {
                        // a tracking blip: Vuforia switches every renderer back on; the hare must stay hidden
                        _blipChecked = c.round;
                        var rs = c.spawn.GetComponentsInChildren<Renderer>(true);
                        var was = rs.Select(r => r.enabled).ToArray();
                        foreach (var r in rs) r.enabled = true;
                        Check(rs.All(r => r.forceRenderingOff), $"round {c.round + 1}: the hidden hare stays hidden through a tracking blip");
                        for (int i = 0; i < rs.Length; i++) rs[i].enabled = was[i];         // tracking settles again
                    }
                    if (hare.Choosing) { c.round++; c.router.Tap(c.phone.WorldToScreenPoint(hare.Mounds[hare.Holder].position + Vector3.up * 0.1f * c.H)); }
                    break;
                case OwlHootEcho owl:
                    if (owl.Answering && owl.Pattern.Count > 0)
                        foreach (int h in owl.Pattern) (h == 0 ? c.host.choiceA : c.host.choiceB).onClick.Invoke();
                    break;
            }
        }

        static void Bad(Ctx c)
        {
            switch (c.game)
            {
                case FoxMouseHunt fox:
                    if (fox.Listening) c.router.Tap(c.Screen(fox.Spot + new Vector3(2.6f, 0f, 0.4f) * (c.H / c.stage.lossyScale.x)));
                    break;
                case SquirrelNutStash nuts:
                {
                    // frantic: a different acorn every poll, plus taps on bare floor
                    var live = nuts.LiveAcorns().ToList();
                    if (live.Count > 0) c.router.Tap(c.phone.WorldToScreenPoint(live[c.rng.Next(live.Count)].position));
                    else c.router.Tap(c.Screen(c.home + new Vector3(0.5f, 0f, 0.5f) * c.H));
                    break;
                }
                case DeerFreeze deer:
                {
                    if (!deer.Playing) break;
                    // never still: circle round the deer at the same distance, so it is never reached
                    Vector3 to = c.phone.transform.position - c.spawn.transform.position; to.y = 0f;
                    Vector3 side = Vector3.Cross(Vector3.up, to.normalized);
                    Vector3 p = c.spawn.transform.position + Quaternion.AngleAxis(4f, Vector3.up) * to;
                    c.phone.transform.position = new Vector3(p.x, c.phone.transform.position.y, p.z) + side * 0.001f;
                    break;
                }
                case HareSnowHide hare:
                    if (hare.Choosing) c.router.Tap(c.phone.WorldToScreenPoint(hare.Mounds[(hare.Holder + 1) % 3].position + Vector3.up * 0.1f * c.H));
                    break;
                case OwlHootEcho owl:
                    if (owl.Answering && owl.Pattern.Count > 0) (owl.Pattern[0] == 0 ? c.host.choiceB : c.host.choiceA).onClick.Invoke();
                    break;
            }
        }

        // ------------------------------------------------------------ expectations

        static void ExpectPerfect(Ctx c, bool noisy = false)
        {
            int s = c.host.Score;
            switch (c.game)
            {
                case FoxMouseHunt _: Check(s == 500, $"perfect Mouse Hunt scores 500 ({s})"); break;
                case SquirrelNutStash _: Check(s >= 6, $"keen Nut Stash buries plenty ({s})"); break;
                case DeerFreeze d: Check(d.Won && d.Strikes == 0 && s > 100, $"reached the deer, no strikes ({s}, strikes {d.Strikes})"); break;
                case HareSnowHide h: Check(h.Finds == 5 && s == 700, $"found it every round, streak bonus ({h.Finds}/5, {s})"); break;
                case OwlHootEcho _: Check(s == 10 && c.host.resultsVerdict.text.Length > 0, $"echoed the full 10-hoot pattern ({s})"); break;
            }
            Check(c.game.Stars(s) == 3, $"three stars ({c.game.Stars(s)})");
        }

        static void ExpectIdle(Ctx c)
        {
            int s = c.host.Score;
            Check(s == 0, $"nobody played: score 0 ({s})");
            Check(c.game.Stars(s) == 0, "no stars");
            if (c.game is DeerFreeze d) Check(!d.Won && c.host.resultsVerdict.text == "Out of time", $"deer: out of time (\"{c.host.resultsVerdict.text}\")");
            Check(!c.host.resultsBest.text.Contains("New best"), $"a zero is not a new best (\"{c.host.resultsBest.text}\")");
        }

        static void ExpectBad(Ctx c)
        {
            int s = c.host.Score;
            switch (c.game)
            {
                case FoxMouseHunt _: Check(s == 0, $"far pounces score nothing ({s})"); break;
                case SquirrelNutStash _: Check(s >= 0, $"frantic tapping still works ({s} buried)"); break;
                case DeerFreeze d: Check(!d.Won && d.Strikes == 3 && s == 0 && c.host.resultsVerdict.text == "It bolted!", $"moving while watched: 3 strikes, it bolts ({d.Strikes}, \"{c.host.resultsVerdict.text}\")"); break;
                case HareSnowHide h: Check(h.Finds == 0 && s == 0, $"always wrong: none found ({h.Finds}, {s})"); break;
                case OwlHootEcho _: Check(s == 0, $"wrong first hoot: 0 ({s})"); break;
            }
        }

        /// <summary>After any game: all tidy now, and back in the routine a little later.</summary>
        static IEnumerable<float> AfterGame(Ctx c, Dictionary<Renderer, bool> shownBefore)
        {
            Check(!MiniGameHost.Running, "no game running");
            bool leftovers = UnityEngine.Object.FindObjectsByType<GameTapTarget>(FindObjectsSortMode.None).Length > 0
                             || UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Any(t => t.name.StartsWith("MouseHunt") || t.name == "Acorn" || t.name.StartsWith("SnowMound"));
            Check(!leftovers, "no game pieces left");
            Check(c.palm == null || (!c.palm.Paused && c.palm.ForcedCall == null), "routine not paused or forced");
            Check(!c.tricks.Busy, "no trick stuck");
            var changed = shownBefore.Where(kv => kv.Key != null && kv.Key.enabled != kv.Value).Select(kv => $"{kv.Key.name} {(kv.Value ? "on" : "off")}->{(kv.Key.enabled ? "on" : "off")}").ToList();
            Check(changed.Count == 0, changed.Count == 0 ? "the animal looks as before" : $"renderers changed: {string.Join(", ", changed.Take(5))}");
            Check(!c.hud.CountdownShowing, "no countdown left up");
            Check(c.spawn.GetComponentsInChildren<Renderer>(true).All(r => !r.forceRenderingOff), "nothing on the animal left forced invisible");
            _blipChecked = -1;

            // give it time to make its way back
            Time.timeScale = Speed;
            yield return 3.0f;
            Time.timeScale = 1f;
            Vector3 p = c.spawn.transform.localPosition;
            switch (c.palm)
            {
                case FoxWalk fw:
                    float r = new Vector2(p.x, p.z).magnitude;
                    Check(fw.State == FoxWalk.Mode.Circle && Mathf.Abs(r - fw.radius) < 0.1f, $"fox back on its circle ({fw.State}, r {r:0.00} vs {fw.radius:0.00})");
                    Check(c.spawn.GetComponentInChildren<Animator>().speed > 0.5f, "fox legs moving again");
                    break;
                case HareHop hh:
                    float rh = new Vector2(p.x, p.z).magnitude;
                    Check(hh.State == HareHop.Mode.Circle && rh < hh.radius + 0.08f, $"hare back on its circle ({hh.State}, r {rh:0.00})");
                    break;
                case SquirrelPalm sq:
                    Check(sq.State == SquirrelPalm.Mode.Idle && (p - c.home).magnitude < 0.03f, $"squirrel back home ({sq.State}, {(p - c.home).magnitude * 100f:0.0} cm off)");
                    break;
                case DeerPalm dp:
                    Check(dp.graze <= 0f && dp.Alert < 0.05f, $"deer relaxed (graze {dp.graze:0.0}, alert {dp.Alert:0.00})");
                    break;
                default:
                    var flight = c.spawn.GetComponent<OwlFlight>();
                    Check(flight != null && flight.enabled, "owl flying its routine");
                    break;
            }
            UnityEngine.Object.Destroy(c.phone.gameObject);
        }
    }
}
