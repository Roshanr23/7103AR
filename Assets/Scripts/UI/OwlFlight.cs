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

        [Header("Floor shadow")]
        public SpriteRenderer shadow;
        public float shadowSize = 0.34f;
        public float shadowAlpha = 0.45f;

        float _t0;
        float _phase;
        bool _launched;
        Vector3 _base;
        Quaternion _baseRot;
        Vector3 _shadowScale;

        void OnEnable()
        {
            _t0 = Time.time;
            _launched = false;
            _phase = 0f;
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
            _phase += Time.deltaTime * Mathf.PI * 2f * flapHz * Mathf.Lerp(0.55f, 1f, p.effort);
            Apply(p, _phase);
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
            transform.localPosition = _base + Vector3.up * p.height + side * (swayMetres * p.sway);
            // lean into the climb, level out on the glide
            transform.localRotation = _baseRot * Quaternion.Euler(-pitchDegrees * (p.effort - 0.4f), 0f, 0f);

            if (shadow != null)
            {
                // on the floor under the owl, smaller and fainter the higher it flies
                Vector3 floor = transform.localPosition; floor.y = _base.y + 0.002f;
                shadow.transform.localPosition = floor;
                float k = Mathf.Clamp01(1f - p.height / (highHeight * 1.6f));
                shadow.transform.localScale = _shadowScale * Mathf.Lerp(0.55f, 1f, k);
                Color col = shadow.color; col.a = shadowAlpha * Mathf.Lerp(0.35f, 1f, k); shadow.color = col;
            }
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
