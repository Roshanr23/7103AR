using UnityEngine;

namespace AR7103.App
{
    /// <summary>
    /// Fits this RectTransform to the device safe area, so nothing sits under
    /// the Dynamic Island or the home indicator. Put it on a full-screen child of
    /// the canvas and parent all interactive UI under that.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaFitter : MonoBehaviour
    {
        /// <summary>Editor/capture hook: when set, used instead of Screen.safeArea.</summary>
        public static Rect? Override;

        Rect _applied;
        Vector2Int _screen;

        void OnEnable() => Apply(true);
        void Update() => Apply(false);

        public void Apply(bool force)
        {
            Rect safe = Override ?? Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (!force && safe == _applied && screen == _screen) return;
            if (screen.x <= 0 || screen.y <= 0) return;

            _applied = safe;
            _screen = screen;
            var rt = (RectTransform)transform;
            rt.anchorMin = new Vector2(safe.xMin / screen.x, safe.yMin / screen.y);
            rt.anchorMax = new Vector2(safe.xMax / screen.x, safe.yMax / screen.y);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }
}
