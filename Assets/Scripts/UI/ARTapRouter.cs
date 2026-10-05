using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Vuforia;

namespace AR7103.UI
{
    /// <summary>
    /// Decides what a tap on the AR view means, in place of Vuforia's
    /// AnchorInputListenerBehaviour (which it switches off). That listener sends
    /// EVERY tap to the Plane Finder -- so tapping Mute, the shutter, the name chip
    /// or the animal itself would move the animal to wherever you touched -- and it
    /// reads the old Input Manager, which this project has turned off.
    ///
    /// Here a tap (a quick touch that does not move: drags and swipes are ignored) is
    ///   * on the UI          -> left to the UI; nothing moves;
    ///   * on the animal      -> opens its fact card;
    ///   * on the floor       -> closes the fact card if it is open, otherwise moves
    ///                           the animal there (Plane Finder hit test, as before).
    /// Before the animal is placed, floor taps still go to the Plane Finder, so a
    /// tap can lock the floor early just as it always could.
    /// </summary>
    public class ARTapRouter : MonoBehaviour
    {
        public PlaneFinderBehaviour planeFinder;
        public ARHud hud;
        [Tooltip("A touch longer than this is not a tap.")]
        public float maxTapSeconds = 0.4f;
        [Tooltip("A touch that moves further than this (points) is a drag, not a tap.")]
        public float slopPoints = 12f;

        public enum Target { None, UI, Animal, Floor, Game }
        public MiniGameHost games;

        Transform _stage;
        Transform Stage()
        {
            if (_stage == null) { var go = GameObject.Find("Ground Plane Stage"); if (go != null) _stage = go.transform; }
            return _stage;
        }

        /// <summary>Floor taps that were sent on to move the animal (debugging, tests).</summary>
        public int MoveRequests { get; private set; }

        /// <summary>Raised for each tap with what it hit (debugging, tests).</summary>
        public event Action<Target, Vector2> Tapped;

        bool _down, _moved;
        Vector2 _start;
        float _t0;
        readonly List<RaycastResult> _hits = new List<RaycastResult>();

        void Awake()
        {
            if (planeFinder == null) planeFinder = FindFirstObjectByType<PlaneFinderBehaviour>();
            if (hud == null) hud = FindFirstObjectByType<ARHud>();
            if (games == null) games = FindFirstObjectByType<MiniGameHost>();
            // the stock listener would forward every tap, including taps on buttons
            foreach (var l in FindObjectsByType<AnchorInputListenerBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                l.enabled = false;
        }

        void Update()
        {
            var p = Pointer.current;
            if (p == null) return;
            bool pressed = p.press.isPressed;
            Vector2 pos = p.position.ReadValue();
            float slop = slopPoints * (Screen.dpi > 0f ? Screen.dpi / 160f : 2f);

            if (pressed && !_down) { _down = true; _moved = false; _start = pos; _t0 = Time.unscaledTime; }
            else if (pressed && _down) { if ((pos - _start).sqrMagnitude > slop * slop) _moved = true; }
            else if (!pressed && _down)
            {
                _down = false;
                if (!_moved && Time.unscaledTime - _t0 <= maxTapSeconds) Tap(_start);
            }
        }

        /// <summary>Act on a tap at a screen position. Public so editor tests can tap.</summary>
        public Target Tap(Vector2 screen)
        {
            Target t = Classify(screen);
            // a minigame takes every non-UI tap: game pieces, or a spot on the floor
            if (t != Target.UI && MiniGameHost.Running && games != null)
            {
                Camera cam = ViewCamera();
                if (cam != null) games.HandleTap(cam.ScreenPointToRay(screen), Stage());
                Tapped?.Invoke(Target.Game, screen);
                return Target.Game;
            }
            switch (t)
            {
                case Target.Animal:
                    if (hud != null) hud.OpenFacts();
                    break;
                case Target.Floor:
                    if (hud != null && hud.FactsOpen) { hud.CloseFacts(); break; }   // tap outside = dismiss
                    MoveRequests++;
                    if (Application.isPlaying && planeFinder != null && VuforiaApplication.Instance != null &&
                        VuforiaApplication.Instance.IsRunning)
                        planeFinder.PerformHitTest(screen);
                    break;
            }
            Tapped?.Invoke(t, screen);
            return t;
        }

        /// <summary>What is under a screen position: UI first, then the animal, else the floor.</summary>
        public Target Classify(Vector2 screen)
        {
            if (OverUI(screen)) return Target.UI;
            Camera cam = ViewCamera();
            if (cam != null && hud != null && hud.AnimalPlaced)
            {
                // the animal moves every frame; make sure physics sees where it is now
                Physics.SyncTransforms();
                Ray ray = cam.ScreenPointToRay(screen);
                foreach (var hit in Physics.RaycastAll(ray, 50f, ~0, QueryTriggerInteraction.Collide))
                    if (hit.collider.GetComponentInParent<AnimalTapTarget>() != null) return Target.Animal;
            }
            return Target.Floor;
        }

        bool OverUI(Vector2 screen)
        {
            var es = EventSystem.current != null ? EventSystem.current : FindFirstObjectByType<EventSystem>();
            if (es == null) return false;
            var data = new PointerEventData(es) { position = screen };
            _hits.Clear();
            es.RaycastAll(data, _hits);
            return _hits.Count > 0;      // only raycast targets in groups that block raycasts count
        }

        Camera _cam;
        public Camera cameraOverride;

        Camera ViewCamera()
        {
            if (cameraOverride != null) return cameraOverride;
            if (_cam == null && Application.isPlaying)
            {
                var vb = VuforiaBehaviour.Instance;
                _cam = vb != null ? vb.GetComponent<Camera>() : Camera.main;
            }
            return _cam;
        }
    }
}
