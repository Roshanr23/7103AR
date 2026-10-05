using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// The squirrel's answer to an open palm: "you've got a nut".
    ///
    /// It snaps round to face the viewer in quick jerky turns, scurries over in
    /// short bounding dashes with pauses between (about half the way, never closer
    /// than <see cref="stopShort"/>), then leans in toward your hand, tail flicking.
    /// Hand down: it turns and dashes back to its spot and settles.
    ///
    /// The squirrel is a static model -- no rig, no clip -- so all of it is done
    /// here: the root moves across the floor, the body (SquirrelRoot) rocks, bounces and
    /// leans about a pivot under its haunches, and the tail flicks about its base.
    /// Both are reset to their rest pose every frame before the offsets go on.
    /// </summary>
    public class SquirrelPalm : PalmReaction
    {
        [Header("Scurry")]
        [Tooltip("Share of the way to the viewer it comes (0..1).")]
        [Range(0.1f, 1f)] public float approachFraction = 0.5f;
        [Tooltip("Furthest it comes (m).")]
        public float maxTravel = 0.8f;
        [Tooltip("Never closer to the viewer than this (m, along the floor).")]
        public float stopShort = 0.45f;
        [Tooltip("Average speed during a dash (m/s).")]
        public float dashSpeed = 1.1f;
        [Tooltip("Length of one dash (m).")]
        public float dashLength = 0.2f;
        public Vector2 pauseRange = new Vector2(0.25f, 0.45f);
        [Tooltip("Height of each bound while dashing (m).")]
        public float bounce = 0.025f;

        [Header("Turning")]
        [Tooltip("Degrees per second during each snap of a jerky turn.")]
        public float snapSpeed = 700f;

        [Header("Waiting for the nut")]
        [Tooltip("The model already sits upright; once there it leans in toward your hand by this much (degrees).")]
        public float leanIn = 7f;
        [Tooltip("Tail flick size (degrees).")]
        public float tailFlick = 16f;

        public enum Mode { Idle, Go, Sit, Back }
        public Mode State => _mode;

        GroundSpawn _spawn;
        Transform _body, _tail;
        Vector3 _bodyRestPos, _tailRestPos, _pivotLocal, _tailRootLocal;
        Quaternion _bodyRestRot, _tailRestRot, _homeRot;
        Vector3 _homePos, _target;
        bool _ready;
        Mode _mode;
        float _burstT = -1f, _pause, _sit, _bob, _bobPitch, _flickT = 1f, _nextFlick;

        public override void ResetState()
        {
            base.ResetState();
            _spawn = GetComponent<GroundSpawn>();
            _ready = false;
            _mode = Mode.Idle;
            _sit = _bob = _bobPitch = 0f;
            _burstT = -1f; _pause = 0f;
            _flickT = 1f; _nextFlick = 2f;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "SquirrelRoot") _body = t;
                else if (t.name == "Squirrel_Tail") _tail = t;
            }
            if (_body != null) { _bodyRestPos = _body.localPosition; _bodyRestRot = _body.localRotation; }
            if (_tail != null) { _tailRestPos = _tail.localPosition; _tailRestRot = _tail.localRotation; }
        }

        float FaceYaw => _spawn != null ? _spawn.faceYaw : 0f;
        float Settle => (_spawn != null ? _spawn.popDuration : 0.5f) + 0.15f;

        /// <summary>Once GroundSpawn has put it down: remember home, find the pivots.</summary>
        void Init()
        {
            _ready = true;
            _homePos = transform.localPosition;
            _homeRot = transform.localRotation;
            PinToFloor(transform.Find("Snowfall"));

            // pivot under the haunches: bottom of the body, toward the back
            var bodyMesh = FindRenderer("Squirrel");
            Vector3 nose = Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward;
            if (bodyMesh != null)
            {
                Bounds b = LocalBounds(bodyMesh, transform);          // in the root's space, where the nose is known
                float back = Vector3.Dot(b.extents, new Vector3(Mathf.Abs(nose.x), 0f, Mathf.Abs(nose.z)));
                _pivotLocal = new Vector3(b.center.x, b.min.y, b.center.z) - nose * back * 0.55f;
            }
            // tail base: the point of the tail's box nearest the body's middle
            var tailMesh = FindRenderer("Squirrel_Tail");
            if (tailMesh != null && bodyMesh != null && _tail != null)
            {
                Vector3 p = tailMesh.bounds.ClosestPoint(bodyMesh.bounds.center);
                _tailRootLocal = _tail.parent.InverseTransformPoint(p);
            }
        }

        Renderer FindRenderer(string n)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true)) if (r.name == n) return r;
            return null;
        }

        static Bounds LocalBounds(Renderer r, Transform space)
        {
            Bounds w = r.bounds;
            var b = new Bounds(space.InverseTransformPoint(w.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = w.center + Vector3.Scale(w.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, ((i & 4) >> 1) - 1));
                b.Encapsulate(space.InverseTransformPoint(c));
            }
            return b;
        }

        public override void ResumeAfterExternalMove()
        {
            if (!_ready) return;
            _mode = Mode.Back; _burstT = -1f; _pause = 0.2f;
        }

        /// <summary>
        /// A minigame sending the squirrel somewhere (an acorn): it scurries there in
        /// its usual dashes and calls <paramref name="arrived"/>. Null target = stop.
        /// </summary>
        public void ScurryTo(Vector3? stageLocal, System.Action arrived = null)
        {
            if (!_ready) Init();
            _external = stageLocal;
            _externalArrived = arrived;
            _burstT = -1f; _pause = 0f;
        }

        Vector3? _external;
        System.Action _externalArrived;

        /// <summary>Busy with a minigame errand.</summary>
        public bool OnErrand => _external.HasValue;

        protected override void Advance(float dt)
        {
            if (_external.HasValue)
            {
                if (!_ready) Init();
                _bob = _bobPitch = 0f;
                _sit = Mathf.MoveTowards(_sit, 0f, dt / 0.15f);
                if (Scurry(_external.Value, dt))
                {
                    _external = null;
                    var cb = _externalArrived; _externalArrived = null;
                    cb?.Invoke();
                }
                return;
            }
            if (!_ready) { if (Clock >= Settle) Init(); else return; }
            _bob = _bobPitch = 0f;

            switch (_mode)
            {
                case Mode.Idle:
                    if (Called && ViewerOnGround(_homePos.y, out Vector3 v))
                    {
                        float dist = Vector3.Distance(_homePos, v);
                        _target = ApproachPoint(_homePos, v, Mathf.Max(stopShort, dist * (1f - approachFraction)), maxTravel);
                        _mode = Mode.Go;
                        _burstT = -1f; _pause = 0f;
                    }
                    break;

                case Mode.Go:
                    if (!Called) { _mode = Mode.Back; _burstT = -1f; _pause = 0.1f; break; }
                    if (Scurry(_target, dt)) { _mode = Mode.Sit; Cue("chatter"); }
                    break;

                case Mode.Sit:
                    if (!Called) { _mode = Mode.Back; _burstT = -1f; _pause = 0.15f; break; }
                    if (ViewerOnGround(_homePos.y, out Vector3 at)) SnapTurn(at - transform.localPosition, dt);
                    break;

                case Mode.Back:
                    if (Called) { _mode = Mode.Go; _burstT = -1f; break; }
                    if (Scurry(_homePos, dt) && SnapTurn(_homeRot * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward, dt))
                    {
                        _mode = Mode.Idle;
                        MarkHome();
                    }
                    break;
            }

            float sitWant = _mode == Mode.Sit ? 1f : 0f;
            _sit = Mathf.MoveTowards(_sit, sitWant, dt / (sitWant > _sit ? 0.25f : 0.15f));

            // tail flicks: often while sitting up or pausing, now and then at rest
            _flickT += dt / 0.28f;
            if (_flickT >= 1f && Clock >= _nextFlick)
            {
                _flickT = 0f;
                _nextFlick = Clock + (_mode == Mode.Sit ? Random.Range(0.8f, 1.4f)
                                    : _mode == Mode.Idle ? Random.Range(2.5f, 4.5f) : Random.Range(0.6f, 1.0f));
            }
        }

        /// <summary>
        /// Jerky turn toward <paramref name="dir"/>: quick snaps with a beat between.
        /// Returns true once facing it.
        /// </summary>
        bool SnapTurn(Vector3 dir, float dt)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) return true;
            float ang = Vector3.SignedAngle(Nose(FaceYaw), dir, Vector3.up);
            if (Mathf.Abs(ang) < 3f) return true;
            bool snapping = Mathf.Repeat(Clock, 0.17f) < 0.08f;
            if (snapping)
            {
                float turn = Mathf.Clamp(ang, -snapSpeed * dt, snapSpeed * dt);
                transform.localRotation = Quaternion.Euler(0f, turn, 0f) * transform.localRotation;
            }
            return false;
        }

        /// <summary>
        /// Scurry to <paramref name="target"/> in dashes: face it (jerky), dash a
        /// bounding stretch, pause, repeat. Returns true once there.
        /// </summary>
        bool Scurry(Vector3 target, float dt)
        {
            Vector3 pos = transform.localPosition;
            Vector3 to = target - pos; to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.01f && _burstT < 0f) return true;

            if (_pause > 0f) { _pause -= dt; return false; }

            if (_burstT < 0f)
            {
                if (!SnapTurn(to, dt)) return false;      // line up before each dash
                _burstT = 0f;
            }

            float dur = Mathf.Max(0.05f, Mathf.Min(dashLength, dist + 0.01f) / dashSpeed);
            float t0 = _burstT / dur;
            _burstT += dt;
            float t1 = Mathf.Min(1f, _burstT / dur);
            // eased progress along the dash: quick start, quick stop
            float covered = (Ease01(t1) - Ease01(t0)) * dur * dashSpeed;
            float stepLen = Mathf.Min(covered, dist);
            if (Mathf.Floor(t0 * 2f) != Mathf.Floor(t1 * 2f)) Cue("patter", 1f, footstep: true);   // paws down, each bound
            if (dist > 1e-5f) pos += to / dist * stepLen;
            transform.localPosition = pos;

            // two bounds per dash, body rocking with them
            float b = Mathf.Sin(t1 * Mathf.PI * 2f);
            _bob = Mathf.Abs(b) * bounce;
            _bobPitch = b * 6f;

            if (t1 >= 1f || stepLen >= dist - 1e-4f)
            {
                _burstT = -1f;
                _pause = (target - pos).magnitude < 0.01f ? 0f : Random.Range(pauseRange.x, pauseRange.y);
                if (_pause > 0f && _flickT >= 1f) _nextFlick = Clock;   // flick during the pause
            }
            return false;
        }

        static float Ease01(float t) => 0.5f - 0.5f * Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI);

        protected override void AfterAnimation(float dt)
        {
            if (!_ready) return;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.rotation * Quaternion.Euler(0f, -FaceYaw, 0f) * Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);

            if (_body != null)
            {
                _body.localPosition = _bodyRestPos;
                _body.localRotation = _bodyRestRot;
                _body.position += Vector3.up * _bob;
                float pitch = leanIn * Smooth01(_sit) + _bobPitch;           // positive about right = nose down/forward
                if (Mathf.Abs(pitch) > 1e-3f)
                    _body.RotateAround(transform.TransformPoint(_pivotLocal) + Vector3.up * _bob, right, pitch);
            }
            if (_tail != null)
            {
                _tail.localPosition = _tailRestPos;
                _tail.localRotation = _tailRestRot;
                if (_flickT < 1f)
                {
                    float k = Mathf.Sin(_flickT * Mathf.PI) * tailFlick;
                    _tail.RotateAround(_tail.parent.TransformPoint(_tailRootLocal), right, -k);   // tip up and back down
                }
            }
        }
    }
}
