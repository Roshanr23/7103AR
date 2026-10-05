using AR7103.Hands;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Shared plumbing for an animal that reacts to an open palm.
    ///
    /// The builder wires HandGestureManager's events to <see cref="OnGestureBegan"/>,
    /// <see cref="OnGestureEnded"/> and <see cref="OnHandLost"/> (the same hooks the
    /// owl uses). Subclasses only read <see cref="Called"/> -- palm up, and the
    /// cooldown since the animal last got home has passed -- and
    /// <see cref="HeldFor"/>, how long it has been called for, and call
    /// <see cref="MarkHome"/> once they are back to their normal routine.
    ///
    /// Everything runs off <see cref="Clock"/>, advanced by <see cref="Tick"/>, not
    /// Time.time: the editor previews step an animal frame by frame with a fake
    /// palm and a fake viewer and get exactly what the phone does.
    /// </summary>
    public abstract class PalmReaction : MonoBehaviour
    {
        [Header("Open palm")]
        [Tooltip("Seconds after getting back home before the animal will react again.")]
        public float cooldown = 1.5f;
        [Tooltip("Who the animal comes to. Empty = the AR camera.")]
        public Transform viewerOverride;

        public float Clock { get; private set; }
        AnimalAudio _audio;
        bool _wasCalled;

        /// <summary>Play one of this animal's named sound cues, if it has them.</summary>
        protected void Cue(string name, float volume = 1f, bool footstep = false)
        {
            if (_audio == null) _audio = GetComponent<AnimalAudio>();
            if (_audio != null) _audio.PlayCue(name, volume, footstep);
        }
        bool _palm;
        float _palmSince, _homeAt = -999f;

        /// <summary>Palm is up and the animal is allowed to answer it (or a game says so).</summary>
        protected bool Called => ForcedCall ?? (_palm && Clock - _homeAt >= cooldown && !MiniGameHost.Running);

        /// <summary>A minigame driving the reaction directly: true/false overrides the palm; null = the palm decides.</summary>
        public bool? ForcedCall { get; set; }

        /// <summary>Set by a trick or a minigame that moves the animal itself: the routine stops in place.</summary>
        public bool Paused { get; set; }

        /// <summary>After something else moved the animal (a game, a trick), make its way back to the routine.</summary>
        public virtual void ResumeAfterExternalMove() { }

        /// <summary>Seconds the animal has been <see cref="Called"/> for (0 when not).</summary>
        protected float HeldFor => Called ? Clock - Mathf.Max(_palmSince, _homeAt + cooldown) : 0f;

        /// <summary>Back in its routine: starts the cooldown.</summary>
        protected void MarkHome() => _homeAt = Clock;

        void OnEnable() => ResetState();

        /// <summary>Back to the state it appears in. Public so editor previews (no OnEnable there) can call it.</summary>
        public virtual void ResetState()
        {
            Clock = 0f;
            _pins.Clear();
            _wasCalled = false;
            ForcedCall = null;
            Paused = false;
            _palm = false;
            _homeAt = -999f;
        }

        void Update() => Tick(Time.deltaTime);
        void LateUpdate() => LateTick(Time.deltaTime);

        /// <summary>Advance one frame (movement). Public so editor previews can drive it.</summary>
        public void Tick(float dt)
        {
            if (Paused) return;
            Clock += dt;
            Advance(dt);

            // its voice: the reaction sound the moment it starts answering, quiet otherwise
            bool called = Called;
            if (_audio == null) _audio = GetComponent<AnimalAudio>();
            if (_audio != null)
            {
                if (called && !_wasCalled) _audio.PlayGesture();
                _audio.Engaged = called;
            }
            _wasCalled = called;
        }

        /// <summary>Runs after the Animator: pose overrides (heads, ears, tails) go here.</summary>
        public void LateTick(float dt)
        {
            if (Paused) return;
            AfterAnimation(dt);
            if (Stage == null) return;
            foreach (var p in _pins)
            {
                if (p.t == null) continue;
                p.t.SetPositionAndRotation(Stage.TransformPoint(p.pos), Stage.rotation * p.rot);
            }
        }

        struct Pin { public Transform t; public Vector3 pos; public Quaternion rot; }
        readonly System.Collections.Generic.List<Pin> _pins = new System.Collections.Generic.List<Pin>();

        /// <summary>
        /// Keep a child (snowfall, snow mounds) where it is on the floor while the
        /// animal moves or turns: it is put back every frame, in stage space.
        /// </summary>
        protected void PinToFloor(Transform t)
        {
            if (t == null || Stage == null) return;
            _pins.RemoveAll(p => p.t == t);
            _pins.Add(new Pin { t = t, pos = Stage.InverseTransformPoint(t.position),
                                rot = Quaternion.Inverse(Stage.rotation) * t.rotation });
        }

        protected abstract void Advance(float dt);
        protected virtual void AfterAnimation(float dt) { }

        // ---- gesture hooks, wired to HandGestureManager's events by the builder

        public void OnGestureBegan(Gesture g)
        {
            if (g == Gesture.OpenPalm) PalmUp();
            else PalmDown();
        }

        public void OnGestureEnded(Gesture g)
        {
            if (g == Gesture.OpenPalm) PalmDown();
        }

        public void OnHandLost() => PalmDown();

        public void PalmUp()
        {
            if (!isActiveAndEnabled) return;             // not spawned yet
            if (!_palm) _palmSince = Clock;
            _palm = true;
        }

        public void PalmDown() => _palm = false;

        // ---- where the viewer is, in the stage's space

        Camera _viewer;

        protected Transform Viewer()
        {
            if (viewerOverride != null) return viewerOverride;
            if (_viewer == null)
            {
                var vb = Vuforia.VuforiaBehaviour.Instance;
                _viewer = vb != null ? vb.GetComponent<Camera>() : Camera.main;
            }
            return _viewer != null ? _viewer.transform : null;
        }

        protected Transform Stage => transform.parent;

        /// <summary>The viewer's position in stage space, dropped to height <paramref name="y"/>.</summary>
        protected bool ViewerOnGround(float y, out Vector3 p)
        {
            p = Vector3.zero;
            var v = Viewer();
            if (v == null) return false;
            p = Stage != null ? Stage.InverseTransformPoint(v.position) : v.position;
            p.y = y;
            return true;
        }

        /// <summary>The viewer's position in stage space, at its real height.</summary>
        protected bool ViewerLocal(out Vector3 p)
        {
            p = Vector3.zero;
            var v = Viewer();
            if (v == null) return false;
            p = Stage != null ? Stage.InverseTransformPoint(v.position) : v.position;
            return true;
        }

        /// <summary>
        /// Where to stop when coming to the viewer: on the line from
        /// <paramref name="from"/> toward the viewer, <paramref name="stopShort"/>
        /// metres short of them, and never more than <paramref name="maxTravel"/> away.
        /// </summary>
        protected Vector3 ApproachPoint(Vector3 from, Vector3 viewerGround, float stopShort, float maxTravel)
        {
            Vector3 d = viewerGround - from; d.y = 0f;
            float dist = d.magnitude;
            if (dist < 1e-4f) return from;
            float go = Mathf.Clamp(dist - stopShort, 0f, maxTravel);
            return from + d / dist * go;
        }

        /// <summary>Yaw (stage space) that points the model's nose along <paramref name="dir"/>.</summary>
        protected Quaternion Facing(Vector3 dir, float faceYaw)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) return transform.localRotation;
            return Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, faceYaw, 0f);
        }

        /// <summary>The model's nose direction in stage space.</summary>
        protected Vector3 Nose(float faceYaw) =>
            transform.localRotation * Quaternion.Euler(0f, -faceYaw, 0f) * Vector3.forward;

        protected static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Frame-rate independent exponential approach.</summary>
        protected static float Ease(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);
    }
}
