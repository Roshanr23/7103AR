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

        Vector3 _baseScale;
        bool _hasBase;

        public void Spawn(Transform stage, Camera viewer, bool instant = false)
        {
            if (!_hasBase) { _baseScale = transform.localScale; _hasBase = true; }
            gameObject.SetActive(true);

            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = _baseScale;

            // 1. size: measured height at the base scale -> target metres
            float h = MeshBounds().size.y;
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
                    Vector3 w = m.MultiplyPoint3x4(corner);
                    if (!any) { world = new Bounds(w, Vector3.zero); any = true; }
                    else world.Encapsulate(w);
                }
            }
            return world;
        }
    }
}
