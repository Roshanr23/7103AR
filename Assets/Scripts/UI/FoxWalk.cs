using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Walks the fox in a circle around the spot where it appeared, and answers an
    /// open palm: it slows to a stop, turns, trots over to the viewer, and stands
    /// looking up at them with its head tilting. Hand down, it walks back out and
    /// picks the circle up again.
    ///
    /// The exported walk cycle is IN PLACE (the fox's travel across the Blender
    /// scene was stripped), so something has to move it. It travels at
    /// <see cref="authoredSpeed"/> times its current scale -- the speed the walk
    /// was authored at -- and the Animator's speed always follows the actual
    /// ground speed, so planted paws stay planted instead of skating.
    ///
    /// Everything is in the Ground Plane Stage's space, so tap-to-move carries the
    /// whole circle along.
    /// </summary>
    public class FoxWalk : PalmReaction
    {
        [Tooltip("Radius of the walk around the spot, in metres.")]
        public float radius = 0.42f;
        [Tooltip("Metres per second at the model's authored size (measured on export).")]
        public float authoredSpeed = 0.55f;
        [Tooltip("Pause after appearing before it sets off.")]
        public float startDelay = 0.8f;
        [Tooltip("Seconds to ease from standing to a full walk.")]
        public float rampSeconds = 2.0f;
        public bool counterClockwise = true;
        [Tooltip("How quickly it turns to face where it is going.")]
        public float turnRate = 5f;

        [Header("Coming to the viewer")]
        [Tooltip("Seconds to slow from a walk to a stop when the palm goes up.")]
        public float haltSeconds = 0.5f;
        [Tooltip("Furthest it walks toward the viewer (m).")]
        public float approachDistance = 1.0f;
        [Tooltip("Never closer to the viewer than this (m, along the floor).")]
        public float stopShort = 0.6f;
        [Tooltip("Degrees per second when turning on the spot.")]
        public float turnSpeed = 150f;
        [Tooltip("Head tilt either side while it watches you (degrees).")]
        public float headTilt = 14f;

        public enum Mode { Circle, Halt, Approach, Hold, Return }
        public Mode State => _mode;

        Animator _anim;
        GroundSpawn _spawn;
        Transform _head, _neck;
        Mode _mode;
        bool _started;
        float _angle, _y, _speed, _look, _ramp;
        Vector3 _prev, _haltAt;
        float _stepPhase, _clipLength;

        public override void ResetState()
        {
            base.ResetState();
            _started = false;
            _mode = Mode.Circle;
            _speed = _look = _ramp = 0f;
            _anim = GetComponentInChildren<Animator>();
            _spawn = GetComponent<GroundSpawn>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "head") _head = t;
                else if (t.name == "neck") _neck = t;
            }
            if (_anim != null) _anim.speed = 0f;         // stand still, mid-stride, until it sets off
        }

        float WalkSpeed => authoredSpeed * transform.localScale.x;   // stage has unit scale
        // The model's nose is not its +Z axis (it walks toward +X); GroundSpawn knows by how much.
        float FaceYaw => _spawn != null ? _spawn.faceYaw : 0f;
        Vector3 Center => new Vector3(0f, _y, 0f);

        protected override void Advance(float dt)
        {
            if (Clock < startDelay) return;
            if (!_started)
            {
                _started = true;
                Vector3 p = transform.localPosition;
                _y = p.y;
                _prev = p;
                // start the circle heading the way the fox already faces
                Vector3 f = Nose(FaceYaw);
                _angle = counterClockwise ? Mathf.Atan2(-f.x, f.z) : Mathf.Atan2(f.x, -f.z);
            }

            switch (_mode)
            {
                case Mode.Circle:
                    WalkCircle(dt, halting: false);
                    if (Called) _mode = Mode.Halt;
                    break;

                case Mode.Halt:
                    WalkCircle(dt, halting: true);
                    if (!Called) { _mode = Mode.Circle; break; }
                    if (_speed <= 1e-3f) { _mode = Mode.Approach; _haltAt = transform.localPosition; }
                    break;

                case Mode.Approach:
                case Mode.Hold:
                {
                    if (!Called) { _mode = Mode.Return; break; }
                    if (!ViewerOnGround(_y, out Vector3 viewer)) break;
                    Vector3 target = ApproachPoint(_haltAt, viewer, stopShort, approachDistance);
                    bool there = MoveTo(target, dt, faceAtEnd: viewer);
                    if (there) _mode = Mode.Hold;
                    else if (_mode == Mode.Hold && (target - transform.localPosition).magnitude > 0.25f)
                        _mode = Mode.Approach;                   // the viewer moved off; follow
                    break;
                }

                case Mode.Return:
                {
                    if (Called) { _mode = Mode.Approach; _haltAt = transform.localPosition; break; }
                    Vector3 p = transform.localPosition;
                    Vector3 out_ = p - Center; out_.y = 0f;
                    if (out_.sqrMagnitude < 1e-6f) out_ = new Vector3(Mathf.Cos(_angle), 0f, Mathf.Sin(_angle));
                    Vector3 target = Center + out_.normalized * radius;
                    if (MoveTo(target, dt, faceAtEnd: null, keepWalking: true))
                    {
                        _angle = Mathf.Atan2(out_.z, out_.x);
                        _ramp = 1f;
                        _mode = Mode.Circle;
                        MarkHome();
                    }
                    break;
                }
            }
            _prev = transform.localPosition;
            Footfalls(dt);
        }

        public override void ResumeAfterExternalMove()
        {
            _started = true;
            _y = transform.localPosition.y;
            _prev = transform.localPosition;
            _speed = 0f;
            _ramp = 1f;
            _mode = Mode.Return;
        }

        /// <summary>Four paws per walk cycle, in time with the legs, as loud as the gait is brisk.</summary>
        void Footfalls(float dt)
        {
            if (_anim == null || _anim.speed < 0.15f) return;
            if (_clipLength <= 0f)
            {
                var ctrl = _anim.runtimeAnimatorController;
                _clipLength = ctrl != null && ctrl.animationClips.Length > 0 ? ctrl.animationClips[0].length : 1.96f;
            }
            _stepPhase += _anim.speed * dt * 4f / _clipLength;
            if (_stepPhase < 1f) return;
            _stepPhase -= Mathf.Floor(_stepPhase);
            Cue("step", Mathf.Clamp01(_anim.speed), footstep: true);
        }

        /// <summary>The routine: spiral out onto the circle, then round and round.</summary>
        void WalkCircle(float dt, bool halting)
        {
            if (_ramp < 1f && !halting)
            {
                // first steps after appearing: spiral out, path speed = walk speed
                _ramp = Mathf.Min(1f, _ramp + dt / rampSeconds);
                _speed = WalkSpeed * Smooth01(_ramp);
            }
            else
            {
                float accel = WalkSpeed / Mathf.Max(halting ? haltSeconds : 0.8f, 0.05f);
                _speed = Mathf.MoveTowards(_speed, halting ? 0f : WalkSpeed, accel * dt);
            }

            float r = radius * Smooth01(_ramp);
            _angle += (counterClockwise ? 1f : -1f) * _speed / Mathf.Max(radius, 0.05f) * dt;
            Vector3 pos = Center + new Vector3(Mathf.Cos(_angle) * r, 0f, Mathf.Sin(_angle) * r);

            Vector3 step = pos - _prev; step.y = 0f;
            if (step.sqrMagnitude > 1e-10f)
                transform.localRotation = Quaternion.Slerp(transform.localRotation, Facing(step, FaceYaw), Ease(turnRate, dt));
            transform.localPosition = pos;
            if (_anim != null) _anim.speed = _speed / Mathf.Max(WalkSpeed, 1e-4f);
        }

        /// <summary>
        /// Walk to <paramref name="target"/>: turn on the spot first (legs shuffling),
        /// then walk, slowing on arrival. Returns true once standing there (and, with
        /// <paramref name="faceAtEnd"/>, turned toward that point).
        /// </summary>
        bool MoveTo(Vector3 target, float dt, Vector3? faceAtEnd, bool keepWalking = false)
        {
            Vector3 pos = transform.localPosition;
            Vector3 to = target - pos; to.y = 0f;
            float dist = to.magnitude;
            bool arrived = dist < 0.02f;

            Vector3 lookDir = arrived ? (faceAtEnd.HasValue ? faceAtEnd.Value - pos : Nose(FaceYaw)) : to;
            float ang = Vector3.SignedAngle(Nose(FaceYaw), Vector3.ProjectOnPlane(lookDir, Vector3.up), Vector3.up);
            float turn = Mathf.Clamp(ang, -turnSpeed * dt, turnSpeed * dt);
            transform.localRotation = Quaternion.Euler(0f, turn, 0f) * transform.localRotation;

            float want = 0f;
            if (!arrived)
            {
                float align = Smooth01(1f - Mathf.Abs(ang) / 70f);              // walk only once roughly facing
                float slow = keepWalking ? 1f : Mathf.Clamp01(dist / 0.25f);    // ease into the stop
                want = WalkSpeed * align * Mathf.Max(slow, 0.3f);
            }
            _speed = Mathf.MoveTowards(_speed, want, WalkSpeed * 2f * dt);
            float stepLen = Mathf.Min(_speed * dt, dist);
            if (!arrived && stepLen > 0f)
                pos += Vector3.ProjectOnPlane(Nose(FaceYaw), Vector3.up).normalized * stepLen;
            Vector3 left = target - pos; left.y = 0f;
            if (left.magnitude < 0.02f) { pos.x = target.x; pos.z = target.z; }
            pos.y = _y;
            transform.localPosition = pos;

            // legs follow the ground speed; a slow shuffle while turning on the spot
            float gait = _speed / Mathf.Max(WalkSpeed, 1e-4f);
            if (Mathf.Abs(turn) > 0.2f * turnSpeed * dt) gait = Mathf.Max(gait, 0.45f);
            if (_anim != null) _anim.speed = Mathf.MoveTowards(_anim.speed, gait, 3f * dt);

            if (keepWalking) return arrived;
            return arrived && _speed < 0.02f && (!faceAtEnd.HasValue || Mathf.Abs(ang) < 4f);
        }

        /// <summary>Head up toward the viewer, tilting side to side, while it stands with them.</summary>
        protected override void AfterAnimation(float dt)
        {
            float want = _mode == Mode.Hold ? 1f : _mode == Mode.Approach ? 0.35f : 0f;
            _look = Mathf.MoveTowards(_look, want, dt / 0.6f);
            var viewer = Viewer();
            if (_look <= 1e-3f || _head == null || viewer == null) return;

            Vector3 fwd = transform.rotation * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward;
            Vector3 to = viewer.position - _head.position;
            Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, Vector3.up).normalized;
            Vector3 flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
            float yaw = Mathf.Clamp(Vector3.SignedAngle(flatFwd, flatTo, Vector3.up), -70f, 70f);
            float pitch = Mathf.Clamp(Mathf.Atan2(to.y, flatTo.magnitude) * Mathf.Rad2Deg, -15f, 35f);
            float w = Smooth01(_look);

            Vector3 lookFwd = Quaternion.AngleAxis(yaw, Vector3.up) * flatFwd;
            Vector3 right = Vector3.Cross(Vector3.up, lookFwd);
            if (_neck != null)
            {
                _neck.rotation = Quaternion.AngleAxis(yaw * 0.4f * w, Vector3.up) * _neck.rotation;
                _neck.rotation = Quaternion.AngleAxis(-pitch * 0.45f * w, right) * _neck.rotation;
            }
            float share = _neck != null ? 0.6f : 1f;
            _head.rotation = Quaternion.AngleAxis(yaw * share * w, Vector3.up) * _head.rotation;
            _head.rotation = Quaternion.AngleAxis(-pitch * (_neck != null ? 0.55f : 1f) * w, right) * _head.rotation;
            // the curious tilt: slow, lingering at each side
            float s = Mathf.Sin(Clock * Mathf.PI * 2f / 3.4f);
            float tilt = headTilt * Mathf.Sign(s) * Mathf.Sqrt(Mathf.Abs(s));
            _head.rotation = Quaternion.AngleAxis(tilt * w, lookFwd) * _head.rotation;
        }
    }
}
