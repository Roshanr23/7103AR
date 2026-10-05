using AR7103.Hands;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Makes the owl take off from where it spawned and fly up and down.
    ///
    /// Timeline, from the moment the owl appears:
    ///   perch   -- the baked clip plays untouched (head turns, blinks, breathing)
    ///   takeoff -- rises to <see cref="lowHeight"/>, flapping hard
    ///   fly     -- loops smoothly between <see cref="lowHeight"/> and
    ///              <see cref="highHeight"/>: strong flaps while climbing, a
    ///              held glide while dropping, the way a real bird spends energy.
    ///
    /// Runs in LateUpdate on purpose. The Animator writes the wing controls every
    /// frame from the baked clip; only work done after it survives. While airborne
    /// this overrides the wings, and blends in so there is no snap at takeoff.
    ///
    /// Heights are metres above the floor, in the Ground Plane Stage's space, so
    /// tap-to-move carries the whole flight along with the stage.
    ///
    /// Open palm: the owl comes to the viewer. The loop keeps running underneath
    /// and the approach is blended over it by a weight, so leaving and rejoining
    /// the loop is always smooth -- there is no second path to hand back from.
    /// </summary>
    public class OwlFlight : MonoBehaviour
    {
        [Header("Wings (filled in by the builder from the baked clip)")]
        public Transform wingNear;
        public Transform wingFar;
        [Tooltip("Wing pose with no lift, as in the clip's first frame.")]
        public Quaternion nearRest = Quaternion.identity;
        public Quaternion farRest = Quaternion.identity;
        [Tooltip("Local axis a positive flap turns about, measured from the clip's flap.")]
        public Vector3 nearAxis = Vector3.right;
        public Vector3 farAxis = Vector3.right;

        [Header("Timing")]
        public float perchSeconds = 1.5f;
        public float takeoffSeconds = 1.3f;
        [Tooltip("One full climb-and-drop.")]
        public float cycleSeconds = 3.4f;

        [Header("Height above the floor (m)")]
        public float lowHeight = 0.35f;
        public float highHeight = 0.9f;

        [Header("Flapping")]
        public float flapHz = 3.6f;
        [Tooltip("Lift at the top of a flap stroke, degrees. Wider than the perched " +
                 "flutter (40) so a beat reads from a phone's distance.")]
        public float flapTop = 66f;
        [Tooltip("Lift at the bottom of a stroke. Keep it above 0: the wing is a shell " +
                 "resting on the body, and any negative lift pushes it through the torso.")]
        public float flapBottom = 6f;
        [Tooltip("Lift held while gliding down.")]
        public float glideLift = 30f;

        [Header("Life")]
        public float swayMetres = 0.035f;
        public float pitchDegrees = 7f;

        [Header("Come to the viewer (open palm)")]
        [Tooltip("How far in front of the camera the owl stops, in metres.")]
        public float approachDistance = 1.0f;
        [Tooltip("Owl's body this far below the camera. The owl's pivot is mid-body, " +
                 "not at its feet; 0.18 at 1 m puts it ~10 deg under the centre of view.")]
        public float approachDrop = 0.18f;
        [Tooltip("Seconds to fly across; SmoothDamp time, so it eases out and in.")]
        public float travelTime = 0.9f;
        [Tooltip("Stays at least this long once called, so a brief palm is not a flicker.")]
        public float minStay = 1.5f;
        [Range(0f, 1f)] public float hoverEffort = 0.8f;
        public float hoverBob = 0.035f;

        [Header("Floor shadow")]
        public SpriteRenderer shadow;
        public float shadowSize = 0.34f;
        public float shadowAlpha = 0.45f;

        float _t0;
        float _phase;
        bool _launched;

        // approach state
        bool _called;          // open palm is being shown
        bool _pending;         // called before the owl was airborne
        float _calledAt;
        float _w, _wVel;       // 0 = on the loop, 1 = in front of the viewer
        Vector3 _comeTarget, _comeVel;
        bool _comeInit;
        Camera _viewer;
        Vector3 _base;
        Quaternion _baseRot;
        Vector3 _shadowScale;

        void OnEnable()
        {
            _t0 = Time.time;
            _launched = false;
            _phase = 0f;
            _called = _pending = false;
            _w = _wVel = 0f;
            _comeInit = false;
            if (shadow != null) { _shadowScale = shadow.transform.localScale; shadow.gameObject.SetActive(false); }
        }

        void LateUpdate()
        {
            float t = Time.time - _t0;
            if (t < perchSeconds) return;              // on the ground: the clip owns everything

            if (!_launched)
            {
                // GroundSpawn has finished placing us by now; this is the floor pose
                _base = transform.localPosition;
                _baseRot = transform.localRotation;
                _launched = true;
                if (shadow != null) shadow.gameObject.SetActive(true);
            }

            Pose p = Evaluate(t - perchSeconds);

            // Only answer once airborne; a palm shown during perch/takeoff is kept
            bool airborne = t - perchSeconds > takeoffSeconds;
            if (_pending && airborne) { _pending = false; _called = true; _calledAt = Time.time; }
            bool stay = _called || (_w > 0.01f && Time.time - _calledAt < minStay);
            _w = Mathf.SmoothDamp(_w, stay ? 1f : 0f, ref _wVel, travelTime);

            // Flapping: hard while crossing the room, steady hover once in front
            bool travelling = Mathf.Abs(_wVel) > 0.15f;
            if (travelling) p.effort = 1f;
            else if (_w > 0.5f) p.effort = Mathf.Max(p.effort * (1f - _w), hoverEffort);

            _phase += Time.deltaTime * Mathf.PI * 2f * flapHz * Mathf.Lerp(0.55f, 1f, p.effort);
            Apply(p, _phase);
        }

        // ---- gesture hooks, wired to HandGestureManager's events by the builder

        public void OnGestureBegan(Gesture g)
        {
            if (!isActiveAndEnabled) return;             // not spawned yet
            if (MiniGameHost.Running) return;            // the game is driving the owl
            if (g == Gesture.OpenPalm) ComeToViewer();
            else Release();
        }

        public void OnGestureEnded(Gesture g)
        {
            if (MiniGameHost.Running) return;
            if (g == Gesture.OpenPalm) Release();
        }

        public void OnHandLost() { if (!MiniGameHost.Running) Release(); }

        public void ComeToViewer()
        {
            if (!isActiveAndEnabled) return;
            if (!_launched || Time.time - _t0 - perchSeconds < takeoffSeconds) { _pending = true; return; }
            if (!_called) _calledAt = Time.time;
            _called = true;
        }

        public void Release()
        {
            _called = false;
            _pending = false;
        }

        Camera Viewer()
        {
            if (_viewer != null) return _viewer;
            var vb = Vuforia.VuforiaBehaviour.Instance;
            _viewer = vb != null ? vb.GetComponent<Camera>() : Camera.main;
            return _viewer;
        }

        /// <summary>
        /// Where the owl hovers in front of the viewer, in the stage's local space:
        /// on the line from the camera to the owl's home, approachDistance out, and
        /// just below eye level -- never lower than its normal flying floor.
        /// </summary>
        public Vector3 ComeTarget(Camera viewer)
        {
            Transform stage = transform.parent;
            if (viewer == null || stage == null) return _base + Vector3.up * lowHeight;
            Vector3 cam = stage.InverseTransformPoint(viewer.transform.position);
            Vector3 home = _base;
            Vector3 dir = home - cam; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
            {
                dir = stage.InverseTransformDirection(viewer.transform.forward); dir.y = 0f;
            }
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            Vector3 target = cam + dir * approachDistance;
            target.y = Mathf.Max(cam.y - approachDrop, _base.y + lowHeight);
            return target;
        }

        /// <summary>The flight state at <paramref name="ft"/> seconds after takeoff began.</summary>
        public struct Pose
        {
            public float height;     // metres above the floor
            public float effort;     // 0 = gliding, 1 = flapping hard
            public float blend;      // 0 = clip's wings, 1 = flight wings
            public float sway;       // -1..1
        }

        public Pose Evaluate(float ft)
        {
            var p = new Pose();
            p.blend = Mathf.Clamp01(ft / 0.3f);
            if (ft < takeoffSeconds)
            {
                // ease out, so the climb arrives at lowHeight with no velocity and
                // hands over to the loop (which starts at its lowest point) seamlessly
                float u = ft / takeoffSeconds;
                p.height = lowHeight * (1f - (1f - u) * (1f - u) * (1f - u));
                p.effort = 1f;
            }
            else
            {
                float c = (ft - takeoffSeconds) / cycleSeconds * Mathf.PI * 2f;
                float mid = (lowHeight + highHeight) * 0.5f;
                float amp = (highHeight - lowHeight) * 0.5f;
                p.height = mid - amp * Mathf.Cos(c);
                // climbing where sin > 0: flap; dropping: glide
                p.effort = Mathf.Clamp01(0.5f + 0.9f * Mathf.Sin(c));
                p.sway = Mathf.Sin(c * 0.5f);
            }
            return p;
        }

        public float LiftAt(Pose p, float phase)
        {
            float stroke = Mathf.Lerp(flapBottom, flapTop, 0.5f + 0.5f * Mathf.Sin(phase));
            return Mathf.Lerp(glideLift, stroke, p.effort);
        }

        /// <summary>Pose the owl. Public so the editor preview can render any moment.</summary>
        public void Apply(Pose p, float phase)
        {
            float lift = LiftAt(p, phase);
            if (wingNear != null)
                wingNear.localRotation = Quaternion.Slerp(wingNear.localRotation,
                    nearRest * Quaternion.AngleAxis(lift, nearAxis), p.blend);
            if (wingFar != null)
                wingFar.localRotation = Quaternion.Slerp(wingFar.localRotation,
                    farRest * Quaternion.AngleAxis(lift, farAxis), p.blend);

            Vector3 side = _baseRot * Vector3.right;
            Vector3 loopPos = _base + Vector3.up * p.height + side * (swayMetres * p.sway);
            // lean into the climb, level out on the glide
            Quaternion lean = Quaternion.Euler(-pitchDegrees * (p.effort - 0.4f), 0f, 0f);
            Quaternion loopRot = _baseRot * lean;

            Vector3 pos = loopPos;
            Quaternion rot = loopRot;
            if (_w > 0.001f)
            {
                // smooth the target so a shaky hand-held camera does not shake the owl
                Vector3 goal = ComeTarget(Viewer());
                if (!_comeInit) { _comeTarget = goal; _comeInit = true; }
                _comeTarget = Vector3.SmoothDamp(_comeTarget, goal, ref _comeVel, 0.35f);
                Vector3 hover = _comeTarget + Vector3.up * (hoverBob * Mathf.Sin(Time.time * 2.3f));

                float e = UIAnimEase(_w);
                pos = Vector3.LerpUnclamped(loopPos, hover, e);

                // face the viewer, turning only about the floor's up axis
                Camera v = Viewer();
                if (v != null)
                {
                    Vector3 to = transform.parent != null
                        ? transform.parent.InverseTransformPoint(v.transform.position) - pos
                        : v.transform.position - pos;
                    to.y = 0f;
                    if (to.sqrMagnitude > 1e-6f)
                        rot = Quaternion.Slerp(loopRot, Quaternion.LookRotation(to, Vector3.up) * lean, e);
                }
            }
            else _comeInit = false;

            transform.localPosition = pos;
            transform.localRotation = rot;

            if (shadow != null)
            {
                // on the floor under the owl, smaller and fainter the higher it flies
                Vector3 floor = transform.localPosition; floor.y = _base.y + 0.002f;
                shadow.transform.localPosition = floor;
                // from the owl's real height: while it hovers by the viewer that is
                // not the loop height
                float actual = transform.localPosition.y - _base.y;
                float k = Mathf.Clamp01(1f - actual / (highHeight * 1.6f));
                shadow.transform.localScale = _shadowScale * Mathf.Lerp(0.55f, 1f, k);
                Color col = shadow.color; col.a = shadowAlpha * Mathf.Lerp(0.35f, 1f, k); shadow.color = col;
            }
        }

        static float UIAnimEase(float t) => t * t * (3f - 2f * t);

        /// <summary>Editor preview: force the approach weight, and the camera to approach.</summary>
        public void PreviewApproach(float weight, Camera viewer)
        {
            _w = weight; _viewer = viewer; _comeInit = false;
        }

        /// <summary>Editor preview: fix the floor pose without waiting for the timeline.</summary>
        public void BeginPreview()
        {
            _base = transform.localPosition;
            _baseRot = transform.localRotation;
            if (shadow != null) { _shadowScale = shadow.transform.localScale; shadow.gameObject.SetActive(true); }
        }
    }
}
