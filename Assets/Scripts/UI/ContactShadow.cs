using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// A soft dark patch on the floor right under the animal -- the contact
    /// darkening real objects get from a room's diffuse light, which is what
    /// stops an AR animal looking pasted on. It follows the animal around the
    /// floor (it is a sibling under the Ground Plane Stage, so it stays flat on
    /// the floor while the animal moves and turns), sized to its footprint, and
    /// shrinks and fades as the animal leaves the ground (the hare's hops, the
    /// squirrel's bounds).
    /// </summary>
    [RequireComponent(typeof(GroundSpawn))]
    [DefaultExecutionOrder(1100)]          // after AnimalTricks, so a pounce lifts it too
    public class ContactShadow : MonoBehaviour
    {
        [Tooltip("Uses the Custom/AR Contact Shadow shader.")]
        public Material material;
        [Range(0f, 1f)] public float opacity = 0.55f;
        [Tooltip("Patch size relative to the footprint (it should spill a little past the feet).")]
        public float spread = 1.35f;
        [Tooltip("Share of the animal's height counted as touching the floor when measuring the footprint.")]
        [Range(0.05f, 1f)] public float lowerShare = 0.35f;
        [Tooltip("Moves when the animal leaves the ground (a body bone). Empty = the animal never lifts off.")]
        public Transform liftReference;
        [Tooltip("Height (m) at which the patch has faded out.")]
        public float fadeHeight = 0.2f;

        GroundSpawn _spawn;
        Transform _patch;
        Renderer _renderer;
        MaterialPropertyBlock _mpb;
        Vector3 _localCentre;
        Vector2 _size;
        float _liftRest, _enabledAt;
        bool _measured;
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");

        void OnEnable()
        {
            _spawn = GetComponent<GroundSpawn>();
            _measured = false;
            _enabledAt = Time.time;
            if (_patch != null) _patch.gameObject.SetActive(false);
        }

        void OnDisable()
        {
            if (_patch != null) _patch.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_patch == null) return;
            if (Application.isPlaying) Destroy(_patch.gameObject); else DestroyImmediate(_patch.gameObject);
        }

        void LateUpdate()
        {
            // measure once GroundSpawn's grow-in has finished
            if (!_measured && Time.time - _enabledAt > (_spawn != null ? _spawn.popDuration : 0.5f) + 0.1f) Measure();
            Follow();
        }

        /// <summary>Measure the footprint and create the patch. Public for editor previews.</summary>
        public void Measure()
        {
            if (_spawn == null) _spawn = GetComponent<GroundSpawn>();
            _spawn.Footprint(lowerShare, out _localCentre, out Vector2 sizeW);
            // stored in the animal's local units so it scales with it
            float s = Mathf.Max(transform.lossyScale.x, 1e-5f);
            _size = sizeW / s;
            _liftRest = LiftNow();
            EnsurePatch();
            _patch.gameObject.SetActive(true);
            _measured = true;
        }

        void EnsurePatch()
        {
            if (_patch != null) return;
            var go = new GameObject(name + " ContactShadow") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform.parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = FloorQuad();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _renderer = mr;
            _patch = go.transform;
            _mpb = new MaterialPropertyBlock();
        }

        /// <summary>Keep the patch under the animal. Public for editor previews.</summary>
        public void Follow()
        {
            if (!_measured || _patch == null) return;
            Transform stage = transform.parent;
            float s = transform.lossyScale.x;

            Vector3 centre = transform.TransformPoint(_localCentre);
            Vector3 nose = transform.rotation * Quaternion.Euler(0f, -(_spawn != null ? _spawn.faceYaw : 0f), 0f) * Vector3.forward;
            Vector3 up = stage != null ? stage.up : Vector3.up;
            nose = Vector3.ProjectOnPlane(nose, up);
            if (nose.sqrMagnitude < 1e-8f) nose = Vector3.forward;

            // flat on the floor (the stage plane), a hair above it to avoid z-fighting
            float floorY = stage != null ? stage.position.y : 0f;
            centre += up * (floorY + 0.003f - Vector3.Dot(centre, up));
            _patch.SetPositionAndRotation(centre, Quaternion.LookRotation(nose, up));

            float lift = Mathf.Max(0f, LiftNow() - _liftRest) * (stage != null ? stage.lossyScale.y : 1f);
            float k = Mathf.Clamp01(lift / Mathf.Max(fadeHeight, 1e-4f));     // both in world metres
            float grow = 1f - 0.35f * k;
            _patch.localScale = new Vector3(_size.x * s * spread * grow, 1f, _size.y * s * spread * grow) /
                                (stage != null ? stage.lossyScale.x : 1f);

            _mpb.SetFloat(OpacityId, opacity * (1f - k));
            _renderer.SetPropertyBlock(_mpb);
        }

        /// <summary>Height of the lift reference (or the animal itself) above the stage, in stage units.</summary>
        float LiftNow()
        {
            Transform r = liftReference != null ? liftReference : transform;
            return transform.parent != null ? transform.parent.InverseTransformPoint(r.position).y : r.position.y;
        }

        /// <summary>A 1x1 quad lying in the XZ plane, facing up, UVs 0..1.</summary>
        static Mesh _quad;
        static Mesh FloorQuad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "FloorQuad", hideFlags = HideFlags.DontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            _quad.RecalculateBounds();
            return _quad;
        }
    }
}
