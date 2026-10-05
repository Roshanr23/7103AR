using System;
using AR7103.Hands;
using UnityEngine;
using Vuforia;

namespace AR7103.UI
{
    /// <summary>
    /// The four gestures beyond the open palm, one meaning each for every animal:
    ///
    ///   Fist   -> Startle   (hops back and crouches; the owl puffs up)   + alarm sound
    ///   Pinch  -> Treat     (turns to you, nibbles)                      + munching
    ///   Point  -> Trick     (squirrel spins, fox pounces nose-first, deer
    ///                        springs up, hare binkies, owl turns its head)
    ///   Peace  -> Say cheese (turns to you, holds still, 3-2-1, photo)
    ///
    /// A trick is drawn as a temporary offset over whatever the animal is doing:
    /// each LateUpdate (late in the frame, after walking/hopping/flying scripts
    /// and the Animator) the offset goes on, and TrickUndo takes it off again at
    /// the very start of the next frame, so the animal's own scripts never see it.
    /// While a trick plays the routine is paused (PalmReaction.Paused, Animator
    /// held) -- except the owl, which keeps flying and tricks with its head.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class AnimalTricks : MonoBehaviour
    {
        public enum Trick { None, Startle, Treat, Special, Pose }
        public enum Special { Spin, Pounce, Leap, Binky, HeadTurn }

        public Special special = Special.Spin;
        [Tooltip("What the Point trick is called on screen (\"Pounce\").")]
        public string specialName = "Trick";
        [Tooltip("Head bone, for nibbling and head turns. Empty = the whole body nods.")]
        public Transform head;
        [Tooltip("Off for the owl: it keeps flying and tricks with its head and feathers.")]
        public bool pausesRoutine = true;
        public float cooldown = 0.8f;
        [Tooltip("Who the animal turns to. Empty = the AR camera.")]
        public Transform viewerOverride;

        public static event Action<AnimalTricks, Trick> Performed;

        public Trick Current => _trick;
        public bool Busy => _trick != Trick.None;

        GroundSpawn _spawn;
        PalmReaction _palm;
        Animator _anim;
        AnimalAudio _audio;
        ARHud _hud;
        PhotoCapture _photo;
        HandGestureManager _hands;

        Trick _trick;
        float _t, _dur, _lastEnd = -99f, _animSpeed = 1f, _faceYaw;
        int _beat;
        bool _wasPaused;

        // this frame's offset, in the Ground Plane Stage's space
        Vector3 _pos;
        Quaternion _rot = Quaternion.identity;
        Vector3 _scale = Vector3.one;
        float _headPitch, _headYaw;

        // measured once: where to rotate and squash about, and how big it is
        bool _measured;
        Vector3 _pivotLocal;      // root-local, middle of the body
        float _floorLocal;        // root-local height of the feet
        float _height;            // stage units

        // undo bookkeeping
        bool _applied;
        Vector3 _basePos, _baseScale;
        Quaternion _baseRot;

        void Awake()
        {
            var undo = gameObject.GetComponent<TrickUndo>();
            if (undo == null) undo = gameObject.AddComponent<TrickUndo>();
            undo.tricks = this;
        }

        void OnEnable()
        {
            _spawn = GetComponent<GroundSpawn>();
            _palm = GetComponent<PalmReaction>();
            _anim = GetComponentInChildren<Animator>();
            _audio = GetComponent<AnimalAudio>();
            _hud = FindFirstObjectByType<ARHud>();
            _photo = FindFirstObjectByType<PhotoCapture>();
            _hands = FindFirstObjectByType<HandGestureManager>();
            if (_hands != null) _hands.onGestureBegan.AddListener(OnGesture);
            _measured = false;
        }

        void OnDisable()
        {
            if (_hands != null) _hands.onGestureBegan.RemoveListener(OnGesture);
            if (Busy) End();
            Undo();
        }

        public void OnGesture(Gesture g)
        {
            if (MiniGameHost.Running || (_hud != null && !_hud.AnimalPlaced)) return;
            Trick t = g switch
            {
                Gesture.Fist => Trick.Startle,
                Gesture.Pinch => Trick.Treat,
                Gesture.Point => Trick.Special,
                Gesture.Peace => Trick.Pose,
                _ => Trick.None,
            };
            if (t != Trick.None) Perform(t);
        }

        /// <summary>Start a trick (public for tests and minigames). False if busy or cooling down.</summary>
        public bool Perform(Trick t)
        {
            if (t == Trick.None || Busy || Time.time - _lastEnd < cooldown || !isActiveAndEnabled) return false;
            if (!_measured) Measure();
            _trick = t;
            _t = 0f;
            _beat = 0;
            _dur = Duration(t);
            if (pausesRoutine)
            {
                // whoever else had paused the routine (a minigame) keeps it paused afterwards
                _wasPaused = _palm != null && _palm.Paused;
                if (_palm != null) _palm.Paused = true;
                if (_anim != null) { _animSpeed = _anim.speed; _anim.speed = 0f; }
            }
            _faceYaw = YawToViewer();
            Cue(t switch { Trick.Startle => "startle", Trick.Treat => "treat", Trick.Special => special == Special.HeadTurn ? null : "special", _ => null });
            if (_hud != null) _hud.ShowTrick(t, specialName);
            Performed?.Invoke(this, t);
            return true;
        }

        float Duration(Trick t) => t switch
        {
            Trick.Startle => 1.5f,
            Trick.Treat => 2.2f,
            Trick.Pose => 3.8f,
            _ => special switch { Special.Spin => 0.8f, Special.Pounce => 1.5f, Special.Leap => 1.0f, Special.Binky => 0.9f, _ => 1.8f },
        };

        void Measure()
        {
            _measured = true;
            if (_spawn == null) return;
            _spawn.Footprint(1f, out Vector3 centre, out _);
            Bounds b = _spawn.MeshBounds();
            float s = Mathf.Max(transform.lossyScale.y, 1e-5f);
            _pivotLocal = centre + Vector3.up * (b.size.y * 0.5f / s);
            _floorLocal = centre.y;
            float stage = transform.parent != null ? Mathf.Max(transform.parent.lossyScale.y, 1e-5f) : 1f;
            _height = b.size.y / stage;
        }

        void Cue(string name)
        {
            if (name != null && _audio != null) _audio.PlayCue(name, 1f, footstep: false);
        }

        // ------------------------------------------------------------ per frame

        void LateUpdate()
        {
            if (!Busy) return;
            _t += Time.deltaTime;
            Evaluate(_t);
            Apply();
            if (_t >= _dur) End();
        }

        static float E(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        static float Bump(float x) => Mathf.Sin(Mathf.PI * Mathf.Clamp01(x));

        void Evaluate(float t)
        {
            _pos = Vector3.zero; _rot = Quaternion.identity; _scale = Vector3.one;
            _headPitch = _headYaw = 0f;
            float H = _height;
            Vector3 n = Nose(), up = Vector3.up, right = Vector3.Cross(up, n);
            float pitch = 0f, yaw = 0f, roll = 0f;            // degrees; +pitch = nose down

            switch (_trick)
            {
                case Trick.Startle:
                    if (special == Special.HeadTurn)
                    {
                        float p = Bump(t / 1.2f);              // the owl puffs itself up
                        _scale = new Vector3(1f + 0.16f * p, 1f + 0.1f * p, 1f + 0.16f * p);
                        if (t > 0.1f && _beat == 0) { _beat = 1; }
                        break;
                    }
                    {
                        float back = t < 0.25f ? E(t / 0.25f) : t < 1.0f ? 1f : 1f - E((t - 1.0f) / 0.5f);
                        _pos = -n * (0.22f * H * back) + up * (0.1f * H * Bump(t / 0.25f));
                        float c = E((t - 0.15f) / 0.15f) * (1f - E((t - 0.9f) / 0.3f));
                        float flat = special == Special.Binky ? 0.2f : 0.14f;
                        _scale = new Vector3(1f + 0.05f * c, 1f - flat * c, 1f + 0.05f * c);
                        pitch = 6f * c;
                    }
                    break;

                case Trick.Treat:
                {
                    float f = E(t / 0.35f) * (1f - E((t - 1.9f) / 0.3f));
                    yaw = _faceYaw * f;
                    float b = t > 0.4f && t < 1.9f ? Mathf.Max(0f, Mathf.Sin(2f * Mathf.PI * 2f * (t - 0.4f))) : 0f;
                    if (head != null) _headPitch = 24f * b; else pitch = 7f * b;
                    if (t >= 1.0f && _beat == 0) { _beat = 1; Cue("treat"); }
                    break;
                }

                case Trick.Pose:
                {
                    if (pausesRoutine) yaw = _faceYaw * E(t / 0.4f) * (1f - E((t - 3.5f) / 0.3f));
                    int shown = t < 0.4f ? 0 : t < 1.4f ? 3 : t < 2.4f ? 2 : t < 3.3f ? 1 : -1;
                    if (shown > 0 && _beat != shown) { _beat = shown; if (_hud != null) _hud.ShowCountdown(shown); Cue("tick"); }
                    if (shown < 0 && _beat != -1)
                    {
                        _beat = -1;
                        if (_hud != null) _hud.ShowCountdown(0);
                        if (_photo != null) _photo.Shoot();
                    }
                    break;
                }

                case Trick.Special:
                    switch (special)
                    {
                        case Special.Spin:
                            yaw = 360f * E(t / 0.7f);
                            _pos = up * (0.12f * H * Bump(t / 0.7f));
                            break;
                        case Special.Pounce:
                            if (t < 0.35f)
                            {
                                float c = E(t / 0.35f);                 // crouch, nose up, eyes on the spot
                                _scale = new Vector3(1f, 1f - 0.1f * c, 1f);
                                pitch = -10f * c;
                            }
                            else if (t < 0.95f)
                            {
                                float u = (t - 0.35f) / 0.6f;           // up and over, diving nose-first
                                _pos = up * (0.55f * H * Bump(u)) + n * (0.35f * H * E(u));
                                pitch = Mathf.Lerp(-25f, 60f, E(u));
                            }
                            else
                            {
                                float v = (t - 0.95f) / 0.55f;          // nose in the snow, then up again
                                _pos = n * (0.35f * H * (1f - E(v)));
                                pitch = 60f * (1f - E(v));
                                _scale = new Vector3(1f, 1f - 0.08f * Bump(v), 1f);
                            }
                            break;
                        case Special.Leap:
                            _pos = up * (0.28f * H * Bump(t / 0.8f));
                            pitch = t < 0.4f ? -8f * Bump(t / 0.4f) : 6f * Bump((t - 0.4f) / 0.4f);
                            break;
                        case Special.Binky:
                            _pos = up * (0.45f * H * Bump(t / 0.75f));
                            yaw = 40f * Mathf.Sin(2f * Mathf.PI * Mathf.Clamp01(t / 0.75f));
                            roll = 20f * Bump(t / 0.75f);
                            if (t >= 0.75f && _beat == 0) { _beat = 1; Cue("land"); }
                            break;
                        case Special.HeadTurn:
                            _headYaw = t < 0.45f ? 140f * E(t / 0.45f)
                                     : t < 0.8f ? 140f
                                     : t < 1.35f ? Mathf.Lerp(140f, -130f, E((t - 0.8f) / 0.55f))
                                     : -130f * (1f - E((t - 1.35f) / 0.45f));
                            if (t >= 0.45f && _beat == 0) { _beat = 1; Cue("special"); }
                            break;
                    }
                    break;
            }
            _rot = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(pitch, right) * Quaternion.AngleAxis(roll, n);
        }

        /// <summary>Put this frame's offset on (rotation about the body's middle, squash about the feet).</summary>
        void Apply()
        {
            var tr = transform;
            _basePos = tr.localPosition; _baseRot = tr.localRotation; _baseScale = tr.localScale;
            Vector3 pivot = _basePos + _baseRot * Vector3.Scale(_pivotLocal, _baseScale);
            Vector3 feet = _basePos + _baseRot * Vector3.Scale(new Vector3(_pivotLocal.x, _floorLocal, _pivotLocal.z), _baseScale);

            Vector3 pos = pivot + _rot * (_basePos - pivot);
            pos = feet + Vector3.Scale(pos - feet, _scale);
            tr.localRotation = _rot * _baseRot;
            tr.localScale = Vector3.Scale(_baseScale, _scale);
            tr.localPosition = pos + _pos;
            _applied = true;

            if (head != null && (_headPitch != 0f || _headYaw != 0f))
            {
                Vector3 n = tr.parent != null ? tr.parent.TransformDirection(Nose()) : Nose();
                Vector3 right = Vector3.Cross(Vector3.up, n);
                head.rotation = Quaternion.AngleAxis(_headYaw, Vector3.up) * Quaternion.AngleAxis(_headPitch, right) * head.rotation;
            }
        }

        /// <summary>Stop a trick now, putting everything back as it was (a minigame is starting).</summary>
        public void Cancel()
        {
            if (!Busy) return;
            Undo();
            End();
        }

        /// <summary>Take this frame's offset off again (TrickUndo, first thing next frame).</summary>
        public void Undo()
        {
            if (!_applied) return;
            _applied = false;
            transform.localPosition = _basePos;
            transform.localRotation = _baseRot;
            transform.localScale = _baseScale;
        }

        void End()
        {
            _trick = Trick.None;
            _lastEnd = Time.time;
            if (pausesRoutine)
            {
                if (_palm != null) _palm.Paused = _wasPaused;
                if (_anim != null) _anim.speed = _animSpeed;
            }
            if (_hud != null) _hud.ShowCountdown(0);
        }

        // ------------------------------------------------------------ helpers

        Vector3 Nose()
        {
            float fy = _spawn != null ? _spawn.faceYaw : 0f;
            Vector3 n = transform.localRotation * Quaternion.Euler(0f, -fy, 0f) * Vector3.forward;
            n.y = 0f;
            return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.forward;
        }

        Transform Viewer()
        {
            if (viewerOverride != null) return viewerOverride;
            var vb = Application.isPlaying ? VuforiaBehaviour.Instance : null;
            var cam = vb != null ? vb.GetComponent<Camera>() : Camera.main;
            return cam != null ? cam.transform : null;
        }

        float YawToViewer()
        {
            var v = Viewer();
            if (v == null) return 0f;
            Vector3 to = transform.parent != null ? transform.parent.InverseTransformPoint(v.position) - transform.localPosition
                                                  : v.position - transform.position;
            to.y = 0f;
            return to.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(Nose(), to, Vector3.up);
        }
    }
}
