using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AR7103.UI
{
    /// <summary>
    /// Carries the hare forward in step with its hops, and answers an open palm.
    ///
    /// The exported clip keeps each hop's arc and tilt but not its forward travel.
    /// <see cref="metres"/> holds how far the hare had gone on every frame of the
    /// original, so this reads the Animator's own clock and moves the hare by
    /// exactly that much (times its scale): it travels only while airborne and
    /// stays put while it sits, because that is what the original did. Where it
    /// travels to is up to the mode -- round its circle, or straight at a point.
    ///
    /// Open palm: it freezes at its next landing (the clip pauses on a sitting
    /// frame), sits up tall with its ears twitching and turns to face you. Keep the
    /// palm up for <see cref="hopOverAfter"/> seconds and it hops over, then sits up
    /// again close by. Hand down: it hops back to its circle and carries on.
    ///
    /// Everything is in the Ground Plane Stage's space, so tap-to-move carries it.
    /// </summary>
    public class HareHop : PalmReaction
    {
        [Header("Travel recorded on export (hare_travel.json)")]
        public float[] metres;
        public float total = 1.26f;

        [Header("Path")]
        public float radius = 0.3f;
        public bool counterClockwise = true;
        [Tooltip("Ground covered (m) while spiralling out onto the circle.")]
        public float rampDistance = 0.25f;
        public float turnRate = 8f;

        [Header("Coming to the viewer")]
        [Tooltip("Seconds of open palm before it hops over (it freezes and sits up first).")]
        public float hopOverAfter = 2f;
        [Tooltip("Furthest it hops toward the viewer (m).")]
        public float approachDistance = 0.9f;
        [Tooltip("Never closer to the viewer than this (m, along the floor).")]
        public float stopShort = 0.5f;
        [Tooltip("How far the front of the body rears up when it sits up (degrees).")]
        public float sitUpAngle = 24f;
        [Tooltip("Size of an ear twitch (degrees).")]
        public float earTwitch = 16f;
        [Tooltip("Degrees per second when shuffling round to face you while sitting.")]
        public float sitTurnSpeed = 90f;

        [Tooltip("Editor previews set this to drive the clip clock; -1 = read the Animator.")]
        public float previewNormalizedTime = -1f;
        /// <summary>Set (with previewNormalizedTime in use) when the hare wants the clip moved on; the preview applies it.</summary>
        [System.NonSerialized] public float previewJump = -1f;

        public enum Mode { Circle, Freeze, Approach, Return }
        public Mode State => _mode;
        public bool Airborne => _airborne;

        Animator _anim;
        GroundSpawn _spawn;
        Transform _rig, _spineFront, _head, _ear0, _ear1, _hind0, _hind1, _fore0, _fore1;
        Mode _mode;
        bool _init, _airborne, _approached;
        float _y, _lastD, _angle, _circleTravel, _sit, _twitchAt, _twitchT = 1f;
        int _twitchEar;
        float _frameDt = 1f / 30f, _grounded;
        bool _wasAirborne;
        Vector3 _target;

        public override void ResetState()
        {
            base.ResetState();
            _anim = GetComponentInChildren<Animator>();
            _spawn = GetComponent<GroundSpawn>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                switch (t.name)
                {
                    case "spine_front": _spineFront = t; break;
                    case "head": _head = t; break;
                    case "ear0": _ear0 = t; break;
                    case "ear1": _ear1 = t; break;
                    case "HareRig": _rig = t; break;
                    case "hind0": _hind0 = t; break;
                    case "hind1": _hind1 = t; break;
                    case "fore0": _fore0 = t; break;
                    case "fore1": _fore1 = t; break;
                }
            }
            _init = false;
            _mode = Mode.Circle;
            _sit = 0f;
            _approached = false;
            if (_anim != null) _anim.speed = 1f;
        }

        float FaceYaw => _spawn != null ? _spawn.faceYaw : 0f;
        Vector3 Center => new Vector3(0f, _y, 0f);

        /// <summary>Metres covered since the clip began, at normalised time <paramref name="nt"/>.</summary>
        public float DistanceAt(float nt)
        {
            if (metres == null || metres.Length < 2) return 0f;
            int loop = Mathf.FloorToInt(nt);
            float f = (nt - loop) * (metres.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, metres.Length - 2);
            return Mathf.Lerp(metres[i], metres[i + 1], f - i) + loop * total;
        }

        float NormalizedTime =>
            previewNormalizedTime >= 0f ? previewNormalizedTime
            : _anim != null && _anim.isActiveAndEnabled ? _anim.GetCurrentAnimatorStateInfo(0).normalizedTime : 0f;

        /// <summary>Decisions; movement happens after the Animator has stepped the clip.</summary>
        protected override void Advance(float dt)
        {
            if (!_init) return;
            switch (_mode)
            {
                case Mode.Circle:
                    if (Called) _mode = Mode.Freeze;
                    break;

                case Mode.Freeze:
                    if (!Called) { BeginReturn(); break; }
                    if (!_approached && HeldFor >= hopOverAfter && ViewerOnGround(_y, out Vector3 v))
                    {
                        _target = ApproachPoint(transform.localPosition, v, stopShort, approachDistance);
                        if ((_target - transform.localPosition).magnitude > 0.08f) { _mode = Mode.Approach; SkipToNextHop(); }
                        _approached = true;
                    }
                    break;

                case Mode.Approach:
                    if (!Called) BeginReturn();
                    break;

                case Mode.Return:
                    if (Called) _mode = Mode.Freeze;
                    break;
            }

            // Travelling somewhere: a short beat on landing, then straight into the next hop
            // (the clip's own sits are long, made for the circle)
            _grounded = _airborne ? 0f : _grounded + dt;
            if ((_mode == Mode.Approach || _mode == Mode.Return) && _grounded >= 0.25f) { SkipToNextHop(); _grounded = 0f; }

            // The clip only stops on a sitting frame: freezing means "stop at the next landing"
            bool hold = _mode == Mode.Freeze && !_airborne;
            if (_anim != null) _anim.speed = hold ? 0f : 1f;
            float sitWant = hold ? 1f : 0f;
            if (sitWant > 0f && _sit <= 0f) Cue("sniff");          // sits up, nose going
            _sit = Mathf.MoveTowards(_sit, sitWant, dt / (sitWant > _sit ? 0.35f : 0.22f));
        }

        /// <summary>
        /// A minigame sending the hare somewhere (under a snow mound): it hops there
        /// in its own hops and calls <paramref name="arrived"/> once it sits.
        /// Needs ForcedCall = true for the duration, so it does not hop home.
        /// </summary>
        public void HopTo(Vector3 stageLocal, Action arrived = null)
        {
            if (!_init) return;
            _target = stageLocal; _target.y = _y;
            _approached = true;
            _externalArrived = arrived;
            _mode = Mode.Approach;
            SkipToNextHop();
        }

        Action _externalArrived;

        public override void ResumeAfterExternalMove()
        {
            if (!_init) return;
            _approached = false;
            _mode = Mode.Freeze;          // from a standstill...
            BeginReturn();                // ...hop back to the circle
        }

        void BeginReturn()
        {
            if (_mode == Mode.Freeze) SkipToNextHop();
            _mode = Mode.Return;
            _approached = false;
            Vector3 out_ = transform.localPosition - Center; out_.y = 0f;
            if (out_.sqrMagnitude < 1e-6f) out_ = new Vector3(Mathf.Cos(_angle), 0f, Mathf.Sin(_angle));
            _target = Center + out_.normalized * radius;
        }

        /// <summary>
        /// The clip sits for a few seconds between hops. When it is time to go
        /// somewhere, skip on to just before the next take-off -- sitting frame to
        /// sitting frame, so the jump does not show.
        /// </summary>
        void SkipToNextHop()
        {
            if (_airborne || metres == null || metres.Length < 2) return;
            float nt = NormalizedTime, frame = 1f / (metres.Length - 1), to = nt;
            for (float x = nt; x < nt + 1f; x += frame)
                if (DistanceAt(x + frame) - DistanceAt(x) > 0.002f) { to = Mathf.Max(nt, x - 3f * frame); break; }
            if (to <= nt + frame) return;
            if (previewNormalizedTime >= 0f) previewJump = to;
            else if (_anim != null)
                _anim.Play(_anim.GetCurrentAnimatorStateInfo(0).fullPathHash, 0, Mathf.Repeat(to, 1f));
        }

        protected override void AfterAnimation(float dt)
        {
            if (_anim == null || (!_anim.isActiveAndEnabled && previewNormalizedTime < 0f)) return;
            float nt = NormalizedTime;
            _frameDt = dt;
            Step(DistanceAt(nt));
            // airborne = the recorded travel is still increasing just here
            _airborne = DistanceAt(nt) - DistanceAt(Mathf.Max(0f, nt - 0.02f)) > 0.004f;
            if (_wasAirborne && !_airborne) Cue("land", 1f, footstep: true);
            _wasAirborne = _airborne;
            Pose(dt);
        }

        /// <summary>Move the hare for a given authored distance. Public for the editor preview.</summary>
        public void Step(float d)
        {
            if (!_init)
            {
                if (_anim == null) ResetState();
                _init = true;
                _y = transform.localPosition.y;
                _lastD = d;
                _circleTravel = 0f;
                // start the circle heading the way the hare already faces
                Vector3 f = Nose(FaceYaw);
                _angle = counterClockwise ? Mathf.Atan2(-f.x, f.z) : Mathf.Atan2(f.x, -f.z);
            }

            float delta = Mathf.Max(0f, d - _lastD) * transform.localScale.x;   // stage has unit scale
            _lastD = d;
            Vector3 prev = transform.localPosition;
            Vector3 pos = prev;

            switch (_mode)
            {
                case Mode.Circle:
                case Mode.Freeze:            // until it lands, a freezing hare finishes its hop
                {
                    _circleTravel += delta;
                    float r = radius * Smooth01(_circleTravel / Mathf.Max(rampDistance, 1e-4f));
                    _angle += (counterClockwise ? 1f : -1f) * delta / Mathf.Max(radius, 0.05f);
                    pos = Center + new Vector3(Mathf.Cos(_angle) * r, 0f, Mathf.Sin(_angle) * r);
                    if (_mode == Mode.Freeze && _approached)
                        pos = prev;          // already hopped over to the viewer: stay put
                    break;
                }
                case Mode.Approach:
                case Mode.Return:
                {
                    Vector3 to = _target - prev; to.y = 0f;
                    float dist = to.magnitude;
                    pos = dist > 1e-5f ? prev + to / dist * Mathf.Min(delta, dist) : prev;
                    if (dist - delta <= 0.01f)
                    {
                        if (_mode == Mode.Approach)
                        {
                            _mode = Mode.Freeze;
                            var cb = _externalArrived; _externalArrived = null;
                            cb?.Invoke();
                        }
                        else
                        {
                            Vector3 o = pos - Center;
                            _angle = Mathf.Atan2(o.z, o.x);
                            _circleTravel = Mathf.Max(_circleTravel, rampDistance);   // already on the circle
                            _mode = Mode.Circle;
                            MarkHome();
                        }
                    }
                    break;
                }
            }
            pos.y = _y;

            // Facing: along the path while moving; toward the target or the viewer otherwise
            Vector3 step = pos - prev; step.y = 0f;
            float dt = _frameDt;
            if (_mode == Mode.Approach || _mode == Mode.Return)
            {
                Vector3 to = _target - pos; to.y = 0f;
                if (to.sqrMagnitude > 1e-6f)
                    transform.localRotation = Quaternion.Slerp(transform.localRotation, Facing(to, FaceYaw), Ease(turnRate, dt));
            }
            else if (_mode == Mode.Freeze && !_airborne && ViewerOnGround(_y, out Vector3 v))
            {
                float ang = Vector3.SignedAngle(Nose(FaceYaw), Vector3.ProjectOnPlane(v - pos, Vector3.up), Vector3.up);
                float turn = Mathf.Clamp(ang, -sitTurnSpeed * dt, sitTurnSpeed * dt);
                transform.localRotation = Quaternion.Euler(0f, turn, 0f) * transform.localRotation;
            }
            else if (step.sqrMagnitude > 1e-10f)     // sitting still: keep facing where it was
            {
                transform.localRotation = Quaternion.Slerp(transform.localRotation, Facing(step, FaceYaw), Ease(turnRate, dt));
            }
            transform.localPosition = pos;
        }

        /// <summary>
        /// Sit up tall: the whole body rocks back over the hind feet (rump settles,
        /// feet stay planted), the chest lifts, the front legs hang straight down,
        /// and the head stays level, turned to the viewer. Ears twitch.
        /// </summary>
        void Pose(float dt)
        {
            if (_sit <= 1e-3f || _spineFront == null) return;
            float w = Smooth01(_sit);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.rotation * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);       // negative angle about right = nose up

            float rock = sitUpAngle * 0.5f * w, chest = sitUpAngle * 0.6f * w;
            if (_rig != null && _hind0 != null && _hind1 != null)
            {
                Vector3 feet = (_hind0.position + _hind1.position) * 0.5f;
                feet.y = transform.position.y;
                _rig.RotateAround(feet, right, -rock);
                // hind legs keep their angle to the floor
                _hind0.rotation = Quaternion.AngleAxis(rock, right) * _hind0.rotation;
                _hind1.rotation = Quaternion.AngleAxis(rock, right) * _hind1.rotation;
            }
            else chest += rock;
            _spineFront.rotation = Quaternion.AngleAxis(-chest, right) * _spineFront.rotation;
            float lifted = rock + chest;
            if (_fore0 != null) _fore0.rotation = Quaternion.AngleAxis(lifted, right) * _fore0.rotation;
            if (_fore1 != null) _fore1.rotation = Quaternion.AngleAxis(lifted, right) * _fore1.rotation;

            if (_head != null)
            {
                _head.rotation = Quaternion.AngleAxis(lifted, right) * _head.rotation;    // level again
                var viewer = Viewer();
                if (viewer != null)
                {
                    Vector3 to = viewer.position - _head.position;
                    Vector3 flat = Vector3.ProjectOnPlane(to, Vector3.up);
                    float yaw = Mathf.Clamp(Vector3.SignedAngle(fwd, flat, Vector3.up), -50f, 50f);
                    float pitch = Mathf.Clamp(Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg, 0f, 24f) * 0.5f;
                    Vector3 lookRight = Vector3.Cross(Vector3.up, Quaternion.AngleAxis(yaw, Vector3.up) * fwd);
                    _head.rotation = Quaternion.AngleAxis(yaw * w, Vector3.up) * _head.rotation;
                    _head.rotation = Quaternion.AngleAxis(-pitch * w, lookRight) * _head.rotation;
                }
            }

            // ear twitches: one ear at a time, a quick flick back and forth
            _twitchT += dt / 0.22f;
            if (_twitchT >= 1f && Clock >= _twitchAt)
            {
                _twitchT = 0f;
                _twitchEar = (_twitchEar + 1 + (Random.value < 0.3f ? 1 : 0)) % 2;
                _twitchAt = Clock + Random.Range(0.5f, 1.3f);
            }
            if (_twitchT < 1f)
            {
                float k = Mathf.Sin(_twitchT * Mathf.PI) * earTwitch * w;
                var ear = _twitchEar == 0 ? _ear0 : _ear1;
                if (ear != null) ear.rotation = Quaternion.AngleAxis(k, right) * ear.rotation;
            }
        }
    }
}
