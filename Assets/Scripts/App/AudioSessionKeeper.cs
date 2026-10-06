using UnityEngine;

namespace AR7103.App
{
    /// <summary>iOS can reset the audio session while the app is in the background: re-apply it on return.</summary>
    public class AudioSessionKeeper : MonoBehaviour
    {
        void OnApplicationFocus(bool focus) { if (focus) AudioSession.PlayEvenWhenSilenced(); }
        void OnApplicationPause(bool paused) { if (!paused) AudioSession.PlayEvenWhenSilenced(); }
    }
}
