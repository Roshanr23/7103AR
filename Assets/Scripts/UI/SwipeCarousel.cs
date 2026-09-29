using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AR7103.UI
{
    /// <summary>
    /// Paged, snapping card carousel. The centre card sits at full size; its
    /// neighbours shrink and dim with distance, so the peeking edges read as
    /// "more this way" without competing with the card in focus.
    ///
    /// Written instead of using ScrollRect because ScrollRect has no notion of
    /// pages -- snapping has to be bolted on by fighting its inertia, and flicks
    /// land between cards.
    ///
    /// Lives on the viewport, which needs a raycast-target Graphic (a fully
    /// transparent Image is fine) so it receives drags anywhere across its width.
    /// </summary>
    public class SwipeCarousel : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform content;
        public RectTransform[] cards;
        [Tooltip("Black overlay on each card, faded in as the card leaves centre.")]
        public Image[] dims;

        [Header("Feel")]
        public float spacing = 44f;
        [Range(0.6f, 1f)] public float sideScale = 0.88f;
        [Range(0f, 1f)] public float sideDim = 0.45f;
        [Tooltip("Drag speed (canvas units/s) above which a short swipe still turns the page.")]
        public float flickVelocity = 900f;
        [Tooltip("Resistance when dragging past the first or last card.")]
        [Range(0.05f, 1f)] public float edgeResistance = 0.35f;
        public float snapTime = 0.2f;

        public event Action<int> PageChanged;
        public int Page => _page;
        public int Count => cards?.Length ?? 0;

        float _cardWidth = 800f;
        float _offset;
        float _snapVel;
        float _dragVel;
        int _page;
        bool _dragging;
        Canvas _canvas;

        float Step => _cardWidth + spacing;
        float Scale => _canvas != null ? _canvas.scaleFactor : 1f;

        void Awake() => _canvas = GetComponentInParent<Canvas>();

        /// <summary>Size and place the cards. Safe to call again on resize.</summary>
        public void Layout(float cardWidth, float cardHeight)
        {
            _cardWidth = cardWidth;
            for (int i = 0; i < Count; i++)
            {
                RectTransform c = cards[i];
                c.anchorMin = c.anchorMax = c.pivot = new Vector2(0.5f, 0.5f);
                c.sizeDelta = new Vector2(cardWidth, cardHeight);
                c.anchoredPosition = new Vector2(i * Step, 0f);
            }
            _offset = -_page * Step;
            _snapVel = 0f;
            ApplyVisuals();
        }

        public void GoTo(int index, bool instant = false)
        {
            index = Mathf.Clamp(index, 0, Mathf.Max(0, Count - 1));
            bool changed = index != _page;
            _page = index;
            if (instant) { _offset = -_page * Step; _snapVel = 0f; ApplyVisuals(); }
            if (changed) PageChanged?.Invoke(_page);
        }

        public void OnBeginDrag(PointerEventData e)
        {
            _dragging = true;
            _dragVel = 0f;
        }

        public void OnDrag(PointerEventData e)
        {
            float dx = e.delta.x / Scale;
            float min = -(Count - 1) * Step;
            if (_offset > 0f || _offset < min) dx *= edgeResistance;   // rubber band
            _offset += dx;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            _dragVel = Mathf.Lerp(_dragVel, dx / dt, 0.35f);
            ApplyVisuals();
        }

        public void OnEndDrag(PointerEventData e)
        {
            _dragging = false;
            int target = Mathf.RoundToInt(-_offset / Step);
            // A quick flick turns the page even if it did not travel halfway
            if (target == _page && Mathf.Abs(_dragVel) > flickVelocity)
                target = _page + (_dragVel < 0f ? 1 : -1);
            GoTo(target);
        }

        void LateUpdate()
        {
            if (_dragging || Count == 0) return;
            float goal = -_page * Step;
            if (Mathf.Abs(_offset - goal) < 0.05f && Mathf.Abs(_snapVel) < 0.05f) return;
            _offset = Mathf.SmoothDamp(_offset, goal, ref _snapVel, snapTime,
                                       Mathf.Infinity, Time.unscaledDeltaTime);
            ApplyVisuals();
        }

        public void ApplyVisuals()
        {
            if (content != null) content.anchoredPosition = new Vector2(_offset, 0f);
            for (int i = 0; i < Count; i++)
            {
                float pages = Mathf.Abs((i * Step + _offset) / Step);   // 0 = centred
                float t = Mathf.Clamp01(pages);
                float s = Mathf.Lerp(1f, sideScale, t);
                cards[i].localScale = new Vector3(s, s, 1f);
                if (dims != null && i < dims.Length && dims[i] != null)
                {
                    Color c = dims[i].color;
                    c.a = Mathf.Lerp(0f, sideDim, t);
                    dims[i].color = c;
                }
            }
        }
    }
}
