using UnityEngine;
using Vuforia;

namespace AR7103.UI
{
    /// <summary>
    /// Matches the virtual lighting to the room. A few times a second it averages
    /// the brightness of the camera image (the grayscale frames the hand tracker
    /// already asks Vuforia for) and eases the main light, the ambient light and
    /// the floor shadows toward it: a dim room gives a dimmer animal with softer
    /// shadows, a bright one a brighter animal with firmer shadows.
    ///
    /// The camera's auto-exposure evens most rooms out, so the range is kept
    /// gentle -- the point is to stop the animal glowing in a dark room, not to
    /// track every flicker. Without camera frames (the Editor) it holds the
    /// "typical room" values.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public class CameraLightEstimator : MonoBehaviour
    {
        [Tooltip("Seconds between camera samples.")]
        public float sampleInterval = 0.3f;
        [Tooltip("Seconds to settle on a new brightness.")]
        public float settleSeconds = 1.2f;

        [Header("Mapping (camera brightness 0..1)")]
        public Vector2 brightnessRange = new Vector2(0.08f, 0.55f);
        public Vector2 lightIntensity = new Vector2(0.45f, 1.3f);
        public Vector2 ambientIntensity = new Vector2(0.22f, 0.62f);
        public Color ambientTint = new Color(0.93f, 0.96f, 1f);
        [Tooltip("Floor shadow darkness (Custom/AR Shadow Catcher materials).")]
        public Vector2 shadowStrength = new Vector2(0.18f, 0.5f);
        public Material[] shadowCatchers;

        [Tooltip("Assumed brightness until (or without) camera frames.")]
        [Range(0f, 1f)] public float typicalBrightness = 0.4f;

        public float Brightness => _lum;

        Light _light;
        float _lum, _target, _next;
        bool _formatRequested;
        static readonly int StrengthId = Shader.PropertyToID("_ShadowStrength");

        void Awake()
        {
            _light = GetComponent<Light>();
            _lum = _target = typicalBrightness;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            Apply();
        }

        void OnEnable()
        {
            if (VuforiaApplication.Instance == null) return;
            VuforiaApplication.Instance.OnVuforiaStarted += RequestFrames;
            if (VuforiaApplication.Instance.IsRunning) RequestFrames();
        }

        void OnDisable()
        {
            if (VuforiaApplication.Instance != null) VuforiaApplication.Instance.OnVuforiaStarted -= RequestFrames;
        }

        void RequestFrames()
        {
            if (_formatRequested) return;
            // Same request the hand tracker makes; asking twice is harmless
            _formatRequested = VuforiaBehaviour.Instance != null &&
                               VuforiaBehaviour.Instance.CameraDevice.SetFrameFormat(PixelFormat.GRAYSCALE, true);
        }

        void Update()
        {
            if (Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + sampleInterval;
                if (TrySample(out float l)) _target = l;
            }
            _lum = Mathf.Lerp(_lum, _target, 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(settleSeconds, 0.05f)));
            Apply();
        }

        /// <summary>Apply the "typical room" lighting now (editor previews: no Awake or camera there).</summary>
        public void ApplyTypical()
        {
            _light = GetComponent<Light>();
            _lum = _target = typicalBrightness;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            Apply();
        }

        void Apply()
        {
            float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(brightnessRange.x, brightnessRange.y, _lum));
            if (_light != null) _light.intensity = Mathf.Lerp(lightIntensity.x, lightIntensity.y, k);
            RenderSettings.ambientLight = ambientTint * Mathf.Lerp(ambientIntensity.x, ambientIntensity.y, k);
            if (shadowCatchers != null)
                foreach (var m in shadowCatchers)
                    if (m != null) m.SetFloat(StrengthId, Mathf.Lerp(shadowStrength.x, shadowStrength.y, k));
        }

        /// <summary>Mean brightness of a sparse grid over the camera frame.</summary>
        bool TrySample(out float lum)
        {
            lum = 0f;
            if (VuforiaApplication.Instance == null || !VuforiaApplication.Instance.IsRunning) return false;
            var vb = VuforiaBehaviour.Instance;
            if (vb == null) return false;
            var image = vb.CameraDevice.GetCameraImage(PixelFormat.GRAYSCALE);
            if (image == null || image.Pixels == null || image.Width <= 0 || image.Height <= 0) return false;

            byte[] px = image.Pixels;
            int w = image.Width, h = image.Height;
            int stride = image.Stride > 0 ? image.Stride : w;
            const int grid = 24;
            long sum = 0; int n = 0;
            for (int gy = 0; gy < grid; gy++)
            {
                int y = (gy * 2 + 1) * h / (grid * 2);
                for (int gx = 0; gx < grid; gx++)
                {
                    int x = (gx * 2 + 1) * w / (grid * 2);
                    int i = y * stride + x;
                    if (i < px.Length) { sum += px[i]; n++; }
                }
            }
            if (n == 0) return false;
            lum = sum / (255f * n);
            return true;
        }
    }
}
