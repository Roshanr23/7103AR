using System.Collections;
using AR7103.App;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Puts an animal on the scanned floor when the ground locks.
    ///
    /// Placement is measured from the mesh at spawn time rather than stored as a
    /// pose, because the models were authored for 1 m image targets and each one
    /// had been rotated to suit its target. Here each is:
    ///   * scaled so its height is <see cref="targetHeight"/> metres,
    ///   * turned to face the camera (yaw only, so it stays upright),
    ///   * lowered until its lowest point touches the floor.
    ///
    /// Sits on the model root, which must be a child of the Ground Plane Stage.
    /// Tap-to-move on the Plane Finder repositions the stage, and the animal
    /// follows because it is parented to it.
    /// </summary>
    public class GroundSpawn : MonoBehaviour
    {
        [Tooltip("Height of the animal on the floor, in metres.")]
        public float targetHeight = 0.6f;

        [Tooltip("Extra turn (degrees) if the model's face is not its +Z axis.")]
        public float faceYaw = 0f;

        [Tooltip("Seconds for the grow-in when the animal appears.")]
        public float popDuration = 0.5f;

        [Tooltip("Shown together with the animal (e.g. the fox's snow patch and snowfall).")]
        public GameObject[] revealWith;

        Vector3 _baseScale;
        bool _hasBase;

        public void Spawn(Transform stage, Camera viewer, bool instant = false)
        {
            if (!_hasBase) { _baseScale = transform.localScale; _hasBase = true; }
            gameObject.SetActive(true);
            if (revealWith != null)
                foreach (var go in revealWith) if (go != null) go.SetActive(true);

            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = _baseScale;

            // 1. size: measured height at the base scale -> target metres, in the stage's
            //    own units, so the Life size / Small toggle (which scales the stage)
            //    shrinks the animal rather than being measured away
            float stageScale = transform.parent != null ? Mathf.Max(transform.parent.lossyScale.y, 1e-5f) : 1f;
            float h = MeshBounds().size.y / stageScale;
            if (h > 1e-5f) transform.localScale = _baseScale * (targetHeight / h);

            // 2. face the viewer, turning only about the floor's up axis
            Vector3 up = stage != null ? stage.up : Vector3.up;
            if (viewer != null)
            {
                Vector3 toCam = Vector3.ProjectOnPlane(viewer.transform.position - transform.position, up);
                if (toCam.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(toCam, up) * Quaternion.Euler(0f, faceYaw, 0f);
            }

            // 3. drop onto the floor: lowest point of the mesh sits on the stage plane
            Bounds b = MeshBounds();
            float floorY = stage != null ? stage.position.y : transform.position.y;
            transform.position += up * (floorY - b.min.y);

            if (!instant && popDuration > 0f && isActiveAndEnabled) StartCoroutine(Pop());
        }

        IEnumerator Pop()
        {
            Vector3 full = transform.localScale;
            // pivot is not at the feet, so grow from the floor contact point instead
            Bounds b = MeshBounds();
            Vector3 feet = new Vector3(b.center.x, b.min.y, b.center.z);
            Vector3 fromFeet = transform.position - feet;
            yield return UIAnim.Tween(popDuration, t =>
            {
                // ease-out-back: a small overshoot reads as "arriving", not scaling
                const float c = 1.6f;
                float e = 1f + (c + 1f) * Mathf.Pow(t - 1f, 3f) + c * Mathf.Pow(t - 1f, 2f);
                float s = Mathf.Max(0.001f, e);
                transform.localScale = full * s;
                transform.position = feet + fromFeet * s;
            }, x => x);
            transform.localScale = full;
            transform.position = feet + fromFeet;
        }

        /// <summary>
        /// World-space bounds from mesh data, not Renderer.bounds: Vuforia's observer
        /// handler toggles renderers on and off with tracking, and a disabled
        /// renderer is not a trustworthy source of bounds. Particle systems (the
        /// snowfall) are skipped so they cannot stretch the measurement.
        /// </summary>
        public Bounds MeshBounds()
        {
            bool any = false;
            Bounds world = new Bounds(transform.position, Vector3.zero);
            ForEachPoint(w =>
            {
                if (!any) { world = new Bounds(w, Vector3.zero); any = true; }
                else world.Encapsulate(w);
            });
            return world;
        }

        /// <summary>
        /// Where the animal meets the floor, in its own frame: the extent of the
        /// points in the bottom <paramref name="lowerShare"/> of its height (legs
        /// and belly, not a raised tail or head), measured along its nose and across.
        /// Returned as a centre (root-local, on the floor) and a size in metres
        /// (x = across, y = along the nose).
        /// </summary>
        public void Footprint(float lowerShare, out Vector3 localCentre, out Vector2 size)
        {
            Bounds b = MeshBounds();
            float cut = b.min.y + b.size.y * lowerShare;
            Vector3 nose = transform.rotation * Quaternion.Euler(0f, -faceYaw, 0f) * Vector3.forward;
            nose = Vector3.ProjectOnPlane(nose, Vector3.up).normalized;
            Vector3 across = Vector3.Cross(Vector3.up, nose);
            float a0 = float.MaxValue, a1 = float.MinValue, c0 = float.MaxValue, c1 = float.MinValue;
            Vector3 o = transform.position;
            ForEachPoint(w =>
            {
                if (w.y > cut) return;
                Vector3 d = w - o;
                float a = Vector3.Dot(d, nose), c = Vector3.Dot(d, across);
                a0 = Mathf.Min(a0, a); a1 = Mathf.Max(a1, a);
                c0 = Mathf.Min(c0, c); c1 = Mathf.Max(c1, c);
            });
            if (a0 > a1) { localCentre = Vector3.zero; size = new Vector2(b.size.x, b.size.z); return; }
            Vector3 centreW = o + nose * ((a0 + a1) * 0.5f) + across * ((c0 + c1) * 0.5f);
            localCentre = transform.InverseTransformPoint(centreW);
            localCentre.y = transform.InverseTransformPoint(new Vector3(o.x, b.min.y, o.z)).y;
            size = new Vector2(c1 - c0, a1 - a0);
        }

        /// <summary>Every world-space point the bounds are measured from.</summary>
        void ForEachPoint(System.Action<Vector3> Add)
        {

            // Skinned meshes (the rigged fox) have no MeshFilter. Bake the CURRENT
            // pose so the measurement is of this stride, not the bind pose.
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr.sharedMesh == null) continue;
                if ((smr.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) continue;   // fur shells
                // useScale FALSE on purpose: a skinned mesh follows its bones and ignores
                // its own object scale, and merged meshes can carry a junk one (the
                // fox's face ended up at 0.01, 0.01, ~0). With useScale true the bake
                // comes back 100x too big in that space; without it the vertices are
                // true size, needing only position and rotation to reach world space.
                var baked = new Mesh();
                smr.BakeMesh(baked, false);
                Matrix4x4 m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (var v in baked.vertices) Add(m.MultiplyPoint3x4(v));
                if (Application.isPlaying) Destroy(baked); else DestroyImmediate(baked);
            }
            foreach (var mf in GetComponentsInChildren<MeshFilter>(false))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<ParticleSystemRenderer>() != null) continue;
                // shell-fur layers are hidden helper objects duplicating the base mesh
                if ((mf.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
                Bounds lb = mf.sharedMesh.bounds;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = lb.center + Vector3.Scale(lb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Add(m.MultiplyPoint3x4(corner));
                }
            }
        }
    }
}
