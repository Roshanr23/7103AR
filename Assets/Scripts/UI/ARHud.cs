using System.Collections;
using AR7103.App;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AR7103.UI
{
    /// <summary>
    /// Always-on layer for an animal AR scene: a Back button to the menu, and,
    /// once the animal is on the floor, a chip naming it and saying it can be
    /// moved. Sits above the scan overlay, which only covers the scanning phase.
    /// </summary>
    public class ARHud : MonoBehaviour
    {
        public CanvasGroup placedInfo;
        public CanvasGroup fader;
        public Button backButton;
        [Tooltip("Delay after the ground locks before the chip appears, so it follows the animal's arrival.")]
        public float infoDelay = 1.2f;

        bool _leaving;
        float _placedAt = -1f;

        void OnEnable() => GroundAnchorStore.GroundStored += OnPlaced;
        void OnDisable() => GroundAnchorStore.GroundStored -= OnPlaced;

        void Start()
        {
            if (backButton != null) backButton.onClick.AddListener(OnBack);
            if (placedInfo != null) placedInfo.alpha = 0f;
            StartCoroutine(UIAnim.Fade(fader, 0f, 0.6f));   // covers camera start-up
        }

        void OnPlaced(Transform anchor) => _placedAt = Time.unscaledTime;

        void Update()
        {
            if (placedInfo == null) return;
            bool show = _placedAt >= 0f && Time.unscaledTime - _placedAt > infoDelay;
            placedInfo.alpha = Mathf.MoveTowards(placedInfo.alpha, show ? 1f : 0f, Time.unscaledDeltaTime * 2.5f);
        }

        void OnBack()
        {
            if (_leaving) return;
            _leaving = true;
            StartCoroutine(Back());
        }

        IEnumerator Back()
        {
            if (fader != null) fader.blocksRaycasts = true;
            yield return UIAnim.Fade(fader, 1f, 0.3f);
            SceneManager.LoadSceneAsync(AppScenes.Entry);
        }
    }
}
