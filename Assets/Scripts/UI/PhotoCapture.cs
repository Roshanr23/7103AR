using System.Collections;
using AR7103.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Image = UnityEngine.UI.Image;

namespace AR7103.UI
{
    /// <summary>
    /// The shutter button: takes a photo of the AR view -- camera image and
    /// animal, no buttons or labels -- and saves it to the photo library.
    ///
    /// Every UI canvas is switched off for the one frame that is captured, then
    /// back on, so the photo is clean. A shutter click, a white flash and a haptic
    /// confirm the shot; a thumbnail with "Saved to Photos" (or what went wrong)
    /// slides in and away. The save itself runs natively (PhotoLibrary), which
    /// replies to <see cref="OnPhotoSaved"/>.
    /// </summary>
    public class PhotoCapture : MonoBehaviour
    {
        public Button shutter;
        [Tooltip("White overlay flashed when the photo is taken.")]
        public CanvasGroup flash;
        [Tooltip("Thumbnail + message shown after the shot.")]
        public CanvasGroup savedToast;
        public Image thumbnail;
        public TMP_Text savedText;
        public AudioClip shutterSound;
        [Range(0f, 1f)] public float shutterVolume = 0.7f;
        public float toastSeconds = 2.4f;

        bool _busy;
        AudioSource _audio;
        Texture2D _lastShot;
        Sprite _lastSprite;
        Coroutine _toast;

        void Start()
        {
            if (shutter != null) shutter.onClick.AddListener(Shoot);
            if (flash != null) { flash.alpha = 0f; flash.blocksRaycasts = false; }
            if (savedToast != null) { savedToast.alpha = 0f; savedToast.blocksRaycasts = false; }
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
        }

        public void Shoot()
        {
            if (_busy) return;
            StartCoroutine(Capture());
        }

        IEnumerator Capture()
        {
            _busy = true;
            // hide every canvas for the captured frame (overlay canvases are drawn into the screenshot)
            var canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            var wasOn = new bool[canvases.Length];
            for (int i = 0; i < canvases.Length; i++)
            {
                wasOn[i] = canvases[i].enabled;
                if (canvases[i].isRootCanvas) canvases[i].enabled = false;
            }
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            for (int i = 0; i < canvases.Length; i++)
                if (canvases[i] != null) canvases[i].enabled = wasOn[i];

            if (shutterSound != null) _audio.PlayOneShot(shutterSound, shutterVolume);
            Haptics.Medium();
            StartCoroutine(Flash());

            // the thumbnail shows straight away; the save confirms a moment later
            ShowThumbnail(shot);
            if (savedText != null) savedText.text = "Saving…";
            if (_toast != null) StopCoroutine(_toast);
            _toast = StartCoroutine(Toast(hold: true));

            byte[] png = shot.EncodeToPNG();
            PhotoLibrary.Save(png, gameObject, nameof(OnPhotoSaved));
            _busy = false;
        }

        /// <summary>Reply from the native save: "ok", "denied" or "error".</summary>
        public void OnPhotoSaved(string result)
        {
            if (savedText != null)
                savedText.text = result == "ok" ? "Saved to Photos"
                    : result == "denied" ? "Allow photo access in Settings to save"
                    : "Couldn't save the photo";
            if (result != "ok") Haptics.Light();
            if (_toast != null) StopCoroutine(_toast);
            _toast = StartCoroutine(Toast(hold: false));
        }

        void ShowThumbnail(Texture2D shot)
        {
            if (_lastSprite != null) Destroy(_lastSprite);
            if (_lastShot != null) Destroy(_lastShot);
            _lastShot = shot;
            if (thumbnail == null) return;
            _lastSprite = Sprite.Create(shot, new Rect(0, 0, shot.width, shot.height), new Vector2(0.5f, 0.5f), 100f);
            thumbnail.sprite = _lastSprite;
            thumbnail.preserveAspect = true;
        }

        IEnumerator Flash()
        {
            if (flash == null) yield break;
            flash.alpha = 0.85f;
            yield return UIAnim.Fade(flash, 0f, 0.35f);
        }

        IEnumerator Toast(bool hold)
        {
            if (savedToast == null) yield break;
            if (savedToast.alpha < 1f) yield return UIAnim.Fade(savedToast, 1f, 0.2f);
            if (hold) yield break;                                  // waiting for the save to answer
            yield return new WaitForSecondsRealtime(toastSeconds);
            yield return UIAnim.Fade(savedToast, 0f, 0.35f);
        }

        void OnDestroy()
        {
            if (_lastSprite != null) Destroy(_lastSprite);
            if (_lastShot != null) Destroy(_lastShot);
        }
    }
}
