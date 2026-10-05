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
    /// Tap behaviour, tested for real in Play Mode -- the HUD runs its own Start and
    /// Update, UI raycasts go through the EventSystem -- in every animal scene:
    ///   * tapping the animal opens its fact card and does not move it;
    ///   * tapping the floor with the card open only closes the card;
    ///   * tapping the floor otherwise asks to move the animal;
    ///   * tapping any control (Back, Mute, Life size | Small, shutter, chip) is UI and moves nothing;
    ///   * the fox stays tappable after walking somewhere else.
    ///
    ///   Unity -batchmode -projectPath . -executeMethod AR7103.EditorTools.TapPlayTest.Run
    /// (no -quit: it enters Play Mode and exits Unity itself, 0 = all passed).
    /// </summary>
    [InitializeOnLoad]
    public static class TapPlayTest
    {
        const string Flag = "ar7103.tapPlayTest";
        static readonly string[] Scenes = { AppScenes.Squirrel, AppScenes.Fox, AppScenes.Deer, AppScenes.Hare, AppScenes.Owl };

        static TapPlayTest()
        {
            if (!SessionState.GetBool(Flag, false)) return;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredPlayMode) Begin();
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Flag, true);
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene($"Assets/Scenes/{Scenes[0]}.unity");
            EditorApplication.EnterPlaymode();
        }

        // ------------------------------------------------------------ runner
        static IEnumerator<float> _steps;
        static float _wakeAt;
        static int _fails, _checks;

        static void Begin()
        {
            _steps = All().GetEnumerator();
            _wakeAt = 0f;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (Time.realtimeSinceStartup < _wakeAt) return;
            bool more;
            try { more = _steps.MoveNext(); }
            catch (Exception e) { Debug.LogError("[TapPlay] crashed: " + e); _fails++; more = false; }
            if (more) { _wakeAt = Time.realtimeSinceStartup + _steps.Current; return; }
            EditorApplication.update -= Tick;
            SessionState.SetBool(Flag, false);
            Debug.Log(_fails == 0 ? $"[TapPlay] ALL {_checks} CHECKS PASSED" : $"[TapPlay] {_fails} of {_checks} CHECKS FAILED");
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        static void Check(string key, bool ok, string what)
        {
            _checks++;
            if (!ok) _fails++;
            Debug.Log($"[TapPlay] {key,-8} {(ok ? "ok  " : "FAIL")} {what}");
        }

        /// <summary>Yielded numbers are seconds to wait before the next step.</summary>
        static IEnumerable<float> All()
        {
            foreach (var scene in Scenes)
            {
                if (SceneManager.GetActiveScene().name != scene)
                {
                    SceneManager.LoadScene(scene);
                    yield return 0.5f;
                }
                foreach (var w in One(scene)) yield return w;
            }
        }

        static IEnumerable<float> One(string scene)
        {
            string key = scene.Replace("AR_", "").ToLowerInvariant();
            var stage = GameObject.Find("Ground Plane Stage").transform;
            var spawn = stage.GetComponentsInChildren<GroundSpawn>(true).First();
            var hud = UnityEngine.Object.FindFirstObjectByType<ARHud>();
            var router = hud != null ? hud.GetComponent<ARTapRouter>() : null;
            Check(key, router != null, "the HUD routes taps (ARTapRouter)");
            var listeners = UnityEngine.Object.FindObjectsByType<Vuforia.AnchorInputListenerBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Check(key, listeners.All(l => !l.enabled), "Vuforia's own tap listener is switched off");
            if (router == null) yield break;

            // a stand-in phone, and the animal placed in front of it as the scan would
            var phone = new GameObject("TestPhone").AddComponent<Camera>();
            phone.depth = -50;
            bool big = key == "deer";
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
            phone.transform.position = big ? new Vector3(0.4f, 1.5f, 2.8f) : new Vector3(0.3f, 1.25f, 1.9f);
            phone.fieldOfView = 60f;
            router.cameraOverride = phone;
            spawn.Spawn(stage, phone, instant: true);
            // hold the animal still so the taps aim at a known spot
            foreach (var b in spawn.GetComponents<MonoBehaviour>())
                if (b is PalmReaction || b is OwlFlight) b.enabled = false;
            phone.transform.LookAt(spawn.MeshBounds().center);
            hud.MarkPlacedForTests();
            yield return 1.5f;                                   // HUD fades its controls in; tap box fitted

            var target = spawn.GetComponent<AnimalTapTarget>();
            Check(key, target != null && target.Box != null, "the animal has a tap target");
            Vector3 home = spawn.transform.position;
            Vector2 Animal() => phone.WorldToScreenPoint(spawn.MeshBounds().center);
            Vector2 floorSpot = phone.WorldToScreenPoint(home + new Vector3(-0.7f, 0f, 0.15f) * (big ? 1.6f : 1f));

            // 1. the animal
            int moves = router.MoveRequests;
            var t = router.Tap(Animal());
            yield return 0.5f;
            Check(key, t == ARTapRouter.Target.Animal, $"tap on the animal hits the animal ({t})");
            Check(key, hud.FactsOpen && hud.factCard.alpha > 0.9f, "tapping the animal opens its fact card");
            Check(key, router.MoveRequests == moves && (spawn.transform.position - home).sqrMagnitude < 1e-8f, "tapping the animal does not move it");

            // 2. floor while the card is open
            t = router.Tap(floorSpot);
            yield return 0.5f;
            Check(key, t == ARTapRouter.Target.Floor, $"tap beside the animal is the floor ({t})");
            Check(key, !hud.FactsOpen && router.MoveRequests == moves, "with the card open, a floor tap only closes it");

            // 3. floor again
            t = router.Tap(floorSpot);
            yield return 0.2f;
            Check(key, t == ARTapRouter.Target.Floor && router.MoveRequests == moves + 1, "with the card closed, a floor tap moves the animal");
            moves = router.MoveRequests;

            // 4. every control
            var controls = new (string, RectTransform)[] {
                ("Back", (RectTransform)hud.backButton.transform), ("Mute", (RectTransform)hud.muteButton.transform),
                ("Life size | Small", (RectTransform)hud.sizeGroup.transform), ("Shutter", (RectTransform)hud.shutterGroup.transform),
                ("name chip", (RectTransform)hud.placedInfo.transform) };
            foreach (var (name, rt) in controls)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(rt.rect.center));
                t = router.Classify(sp);                          // classify only: pressing Back would leave the scene
                Check(key, t == ARTapRouter.Target.UI, $"tap on {name} is UI ({t})");
            }
            Check(key, router.MoveRequests == moves, "no control tap asked to move the animal");

            // 5. the animal somewhere else (the fox, mid-walk)
            if (key == "fox")
            {
                spawn.transform.localPosition = new Vector3(0.42f, spawn.transform.localPosition.y, 0f);
                spawn.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                yield return 0.1f;
                t = router.Tap(Animal());
                yield return 0.4f;
                Check(key, t == ARTapRouter.Target.Animal && hud.FactsOpen, $"the fox is still tappable after walking off ({t})");
                hud.CloseFacts();
            }
            UnityEngine.Object.Destroy(phone.gameObject);
        }
    }
}
