using AR7103.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AR7103.UI
{
    /// <summary>
    /// "Life size / Small": scales the whole Ground Plane Stage, so the animal,
    /// its snow or soil, its walking circle, its speed and its shadows all shrink
    /// together -- a small fox still walks like a fox, just a smaller one. Handy
    /// when a life-size deer will not fit in the room.
    ///
    /// The scale eases over <see cref="transition"/> seconds and is re-applied
    /// every frame, so nothing that repositions the stage (Vuforia's anchor,
    /// tap-to-move) can undo it. The choice is remembered between visits.
    /// </summary>
    public class ARSizeToggle : MonoBehaviour
    {
        public Button button;
        [Tooltip("Segmented control: the highlight slides under the active half.")]
        public RectTransform highlight;
        public TMP_Text lifeLabel, smallLabel;
        public Color activeInk = Color.black, idleInk = Color.white;
        [Tooltip("The Ground Plane Stage. Empty = found by name.")]
        public Transform stage;
        [Range(0.1f, 1f)] public float smallScale = 0.5f;
        public float transition = 0.35f;

        const string Key = "ar7103.small";

        public bool Small { get; private set; }
        float _scale = 1f, _from = 1f, _t = 1f;

        void Start()
        {
            if (stage == null)
            {
                var go = GameObject.Find("Ground Plane Stage");
                if (go != null) stage = go.transform;
            }
            Small = PlayerPrefs.GetInt(Key, 0) == 1;
            _scale = _from = Target;
            _t = 1f;
            if (button != null) button.onClick.AddListener(Toggle);
            _hl = Small ? 1f : 0f;
            UpdateVisual();
        }

        float Target => Small ? smallScale : 1f;

        public void Toggle()
        {
            Small = !Small;
            PlayerPrefs.SetInt(Key, Small ? 1 : 0);
            _from = _scale;
            _t = 0f;
            Haptics.Light();
        }

        float _hl;

        void UpdateVisual()
        {
            if (highlight != null)
            {
                // highlight spans half the control; 0 = left (Life size), 1 = right (Small)
                highlight.anchorMin = new Vector2(0.5f * _hl, 0f);
                highlight.anchorMax = new Vector2(0.5f + 0.5f * _hl, 1f);
            }
            if (lifeLabel != null) lifeLabel.color = Color.Lerp(activeInk, idleInk, _hl);
            if (smallLabel != null) smallLabel.color = Color.Lerp(idleInk, activeInk, _hl);
        }

        void LateUpdate()
        {
            _hl = Mathf.MoveTowards(_hl, Small ? 1f : 0f, Time.unscaledDeltaTime / 0.18f);
            UpdateVisual();
            if (stage == null) return;
            if (_t < 1f)
            {
                _t = Mathf.Min(1f, _t + Time.unscaledDeltaTime / Mathf.Max(transition, 0.01f));
                float e = 1f - Mathf.Pow(1f - _t, 3f);             // ease out
                _scale = Mathf.Lerp(_from, Target, e);
            }
            else _scale = Target;
            stage.localScale = Vector3.one * _scale;
        }
    }
}
