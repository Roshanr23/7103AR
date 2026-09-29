using UnityEngine;
using UnityEngine.UI;

namespace AR7103.UI
{
    /// <summary>
    /// Sizes an Image so it covers its parent (like CSS object-fit: cover),
    /// cropping the overflow, and keeps <see cref="focus"/> in view.
    ///
    /// AspectRatioFitter's EnvelopeParent mode can cover, but it resets the
    /// anchored position every update, so it cannot pull an off-centre subject
    /// -- the squirrel sits at the right edge of its photo -- back into frame.
    /// The parent must clip (a Mask), or the overflow will show.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Image))]
    public class CoverImage : MonoBehaviour
    {
        [Tooltip("Point of the photo to keep in view, 0..1 from bottom-left.")]
        public Vector2 focus = new Vector2(0.5f, 0.5f);

        Vector2 _lastParent;
        Sprite _lastSprite;
        Vector2 _lastFocus;

        void OnEnable() => Fit();

        void LateUpdate()
        {
            var parent = transform.parent as RectTransform;
            if (parent == null) return;
            var img = GetComponent<Image>();
            if (parent.rect.size != _lastParent || img.sprite != _lastSprite || focus != _lastFocus)
                Fit();
        }

        public void Fit()
        {
            var parent = transform.parent as RectTransform;
            var img = GetComponent<Image>();
            if (parent == null || img.sprite == null) return;

            Vector2 p = parent.rect.size;
            if (p.x <= 0f || p.y <= 0f) return;
            Rect sr = img.sprite.rect;
            float aspect = sr.width / sr.height;

            Vector2 size = (p.x / p.y > aspect)
                ? new Vector2(p.x, p.x / aspect)
                : new Vector2(p.y * aspect, p.y);

            Vector2 overflow = size - p;
            Vector2 pos = new Vector2(
                Mathf.Clamp((0.5f - focus.x) * size.x, -overflow.x * 0.5f, overflow.x * 0.5f),
                Mathf.Clamp((0.5f - focus.y) * size.y, -overflow.y * 0.5f, overflow.y * 0.5f));

            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;

            _lastParent = p;
            _lastSprite = img.sprite;
            _lastFocus = focus;
        }
    }
}
