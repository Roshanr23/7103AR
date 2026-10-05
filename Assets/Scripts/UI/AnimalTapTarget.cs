using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Makes the animal tappable: an invisible box collider fitted to it once it
    /// has landed, a little larger than the animal so a small or moving one is
    /// still easy to hit. It is a child of the animal, turned to its nose, so it
    /// moves, turns and scales with it. ARTapRouter raycasts against it.
    /// </summary>
    [RequireComponent(typeof(GroundSpawn))]
    public class AnimalTapTarget : MonoBehaviour
    {
        [Tooltip("Box size relative to the animal (bigger = easier to tap).")]
        public float padding = 1.25f;

        GroundSpawn _spawn;
        BoxCollider _box;
        float _enabledAt;
        bool _measured;

        public BoxCollider Box => _box;

        void OnEnable()
        {
            _spawn = GetComponent<GroundSpawn>();
            _enabledAt = Time.time;
            _measured = false;
        }

        void Update()
        {
            if (!_measured && Time.time - _enabledAt > (_spawn != null ? _spawn.popDuration : 0.5f) + 0.1f) Measure();
        }

        /// <summary>Fit the box to the animal as it stands now. Public for editor tests.</summary>
        public void Measure()
        {
            if (_spawn == null) _spawn = GetComponent<GroundSpawn>();
            if (_box == null)
            {
                var go = new GameObject("TapTarget") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                _box = go.AddComponent<BoxCollider>();
                _box.isTrigger = true;               // only ever raycast, never collides
            }
            // the box's own axes follow the nose: x = across, z = along
            _box.transform.localPosition = Vector3.zero;
            _box.transform.localRotation = Quaternion.Euler(0f, -_spawn.faceYaw, 0f);
            _box.transform.localScale = Vector3.one;

            _spawn.Footprint(1f, out Vector3 centre, out Vector2 size);
            float height = _spawn.MeshBounds().size.y;
            float s = Mathf.Max(transform.lossyScale.x, 1e-5f);
            Vector3 c = _box.transform.InverseTransformPoint(transform.TransformPoint(centre));
            _box.center = c + Vector3.up * (height * 0.5f / s);
            _box.size = new Vector3(size.x * padding, height * padding, size.y * padding) / s;
            _measured = true;
        }
    }
}
