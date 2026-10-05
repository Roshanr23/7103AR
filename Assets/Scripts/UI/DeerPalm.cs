using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// The deer's answer to an open palm: "alert, then trusting".
    ///
    /// It freezes (its idle clip eases to a stop), holds still a beat, then lifts
    /// its head and turns it to look straight at the viewer, ears pricked forward.
    /// If the viewer is well off to one side it turns its body round slowly too.
    /// Keep the palm up for <see cref="sniffAfter"/> seconds and it stretches its
    /// nose toward you and sniffs, ears flicking. Hand down: head goes back, the
    /// idle clip picks up where it left off.
    ///
    /// The deer's legs are one rigid mesh with no rig, so it does not walk -- it
    /// stays on its spot and does all of this with neck, head and ears, layered on
    /// top of whatever pose the clip is holding (after the Animator, every frame).
    /// </summary>
    public class DeerPalm : PalmReaction
    {
        [Header("Timing")]
        [Tooltip("Seconds it stays frozen before looking up at you.")]
        public float freezeBeat = 0.5f;
        [Tooltip("Seconds of open palm before it reaches out to sniff.")]
        public float sniffAfter = 2f;

        [Header("Looking")]
        [Tooltip("Direction of the muzzle in Ctrl_Head's local space (measured by the builder).")]
        public Vector3 noseLocal = Vector3.forward;
        [Tooltip("Share of the look done by the neck; the head does the rest.")]
        [Range(0f, 1f)] public float neckShare = 0.55f;
        public float maxYaw = 75f;
        [Tooltip("Ears pricked toward you (degrees).")]
        public float earPerk = 18f;
        [Tooltip("Turn the body once the viewer is more than this far round (degrees).")]
        public float bodyTurnBeyond = 55f;
        public float bodyTurnSpeed = 30f;

        [Header("Sniffing")]
        [Tooltip("How far the nose reaches down/out toward your hand (degrees of neck).")]
        public float reach = 10f;
        public float sniffBob = 3f;

        Animator _anim;
        GroundSpawn _spawn;
        Transform _neck, _head, _ear0, _ear1;
        float _alert, _sniff, _animSpeed = 1f, _sinceCalled;
        bool _wasAlert, _turning, _pinned;
        float _nextSniffSound;

        public override void ResetState()
        {
            base.ResetState();
            _anim = GetComponentInChildren<Animator>();
            _spawn = GetComponent<GroundSpawn>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                switch (t.name)
                {
                    case "Ctrl_Neck": _neck = t; break;
                    case "Ctrl_Head": _head = t; break;
                    case "Ctrl_Ear0": _ear0 = t; break;
                    case "Ctrl_Ear1": _ear1 = t; break;
                }
            }
            _alert = _sniff = 0f;
            _animSpeed = 1f;
            _wasAlert = _turning = false;
            if (_anim != null) _anim.speed = 1f;
            _pinned = false;
        }

        float FaceYaw => _spawn != null ? _spawn.faceYaw : 0f;

        public float Alert => _alert;
        public float Sniff => _sniff;

        protected override void Advance(float dt)
        {
            // once GroundSpawn has put it down, its snow mounds stay put if it turns
            if (!_pinned && Clock >= (_spawn != null ? _spawn.popDuration : 0.5f) + 0.15f)
            {
                _pinned = true;
                PinToFloor(transform.Find("Deer_SnowMounds"));
            }
            bool called = Called;
            _sinceCalled = called ? _sinceCalled + dt : 0f;

            // freeze: the idle clip eases to a stop and holds that pose
            _animSpeed = Mathf.MoveTowards(_animSpeed, called ? 0f : 1f, dt / (called ? 0.3f : 0.6f));
            if (_anim != null) _anim.speed = _animSpeed;

            float alertWant = called && _sinceCalled >= freezeBeat ? 1f : 0f;
            _alert = Mathf.MoveTowards(_alert, alertWant, dt / (alertWant > _alert ? 0.6f : 0.9f));
            float sniffWant = called && HeldFor >= sniffAfter ? 1f : 0f;
            // a sniffing sound with each burst of sniffs, every other burst
            if (sniffWant > 0f && (_sniff <= 0f || Clock >= _nextSniffSound)) { Cue("sniff"); _nextSniffSound = Clock + 2.2f; }
            _sniff = Mathf.MoveTowards(_sniff, sniffWant, dt / 0.7f);

            if (_alert > 0.01f) _wasAlert = true;
            else if (_wasAlert && !called) { _wasAlert = false; MarkHome(); }

            // a slow turn of the whole deer when the viewer is far round to one side
            if (_alert > 0.5f && ViewerOnGround(transform.localPosition.y, out Vector3 v))
            {
                float ang = Vector3.SignedAngle(Nose(FaceYaw), v - transform.localPosition, Vector3.up);
                if (Mathf.Abs(ang) > bodyTurnBeyond) _turning = true;
                if (Mathf.Abs(ang) < 20f) _turning = false;
                if (_turning)
                {
                    float turn = Mathf.Clamp(ang, -bodyTurnSpeed * dt, bodyTurnSpeed * dt);
                    transform.localRotation = Quaternion.Euler(0f, turn, 0f) * transform.localRotation;
                }
            }
            else _turning = false;
        }

        /// <summary>0..1: head down to the snow, grazing (the Freeze! minigame). The look-up blends over it.</summary>
        [System.NonSerialized] public float graze;
        float _grazeW;

        protected override void AfterAnimation(float dt)
        {
            float w = Smooth01(_alert);
            _grazeW = Mathf.MoveTowards(_grazeW, graze, dt / 0.7f);
            if (_grazeW > 1e-3f && _neck != null)
            {
                Vector3 gf = Vector3.ProjectOnPlane(transform.rotation * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward, Vector3.up).normalized;
                Vector3 gr = Vector3.Cross(Vector3.up, gf);
                float g = Smooth01(_grazeW) * (1f - w);
                _neck.rotation = Quaternion.AngleAxis(48f * g, gr) * _neck.rotation;           // positive = nose down
                if (_head != null) _head.rotation = Quaternion.AngleAxis(18f * g, gr) * _head.rotation;
            }
            var viewer = Viewer();
            if (w <= 1e-3f || _head == null || viewer == null) return;

            Vector3 fwd = Vector3.ProjectOnPlane(transform.rotation * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward, Vector3.up).normalized;
            Vector3 to = viewer.position - _head.position;
            Vector3 flat = Vector3.ProjectOnPlane(to, Vector3.up);
            float yaw = Mathf.Clamp(Vector3.SignedAngle(fwd, flat, Vector3.up), -maxYaw, maxYaw);
            float pitch = Mathf.Clamp(Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg, -5f, 30f);

            // sniffing: nose reaches toward the hand (a little below the phone), in short bursts
            float s = Smooth01(_sniff);
            float burst = Mathf.Repeat(Clock, 1.1f) < 0.6f ? 1f : 0f;
            pitch -= reach * s;
            pitch += Mathf.Sin(Clock * Mathf.PI * 2f * 5f) * sniffBob * s * burst;

            Vector3 lookFwd = Quaternion.AngleAxis(yaw, Vector3.up) * fwd;
            Vector3 right = Vector3.Cross(Vector3.up, lookFwd);
            Vector3 want = Quaternion.AngleAxis(-pitch, right) * lookFwd;      // negative about right = up

            // neck takes its share of the turn, then the head finishes it
            if (_neck != null)
            {
                Vector3 cur = _head.rotation * noseLocal;
                Quaternion q = Quaternion.FromToRotation(cur, want);
                _neck.rotation = Quaternion.Slerp(Quaternion.identity, q, neckShare * w) * _neck.rotation;
            }
            {
                Vector3 cur = _head.rotation * noseLocal;
                Quaternion q = Quaternion.FromToRotation(cur, want);
                _head.rotation = Quaternion.Slerp(Quaternion.identity, q, w) * _head.rotation;
            }

            // ears pricked forward; while sniffing they flick in turn
            float flick0 = s * Mathf.Max(0f, Mathf.Sin(Clock * 7.3f)) * 14f;
            float flick1 = s * Mathf.Max(0f, Mathf.Sin(Clock * 6.1f + 2f)) * 14f;
            if (_ear0 != null) _ear0.rotation = Quaternion.AngleAxis((earPerk - flick0) * w, right) * _ear0.rotation;
            if (_ear1 != null) _ear1.rotation = Quaternion.AngleAxis((earPerk - flick1) * w, right) * _ear1.rotation;
        }
    }
}
