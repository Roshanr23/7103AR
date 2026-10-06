using System.Runtime.InteropServices;
using UnityEngine;

namespace AR7103.App
{
    /// <summary>
    /// Keeps the app audible with the iPhone's silent switch on (Plugins/iOS/AR7103Audio.mm):
    /// Unity's default "ambient" audio session is muted by the switch. A no-op elsewhere.
    /// </summary>
    public static class AudioSession
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void AR7103_PlayEvenWhenSilenced();
        public static void PlayEvenWhenSilenced() => AR7103_PlayEvenWhenSilenced();
#else
        public static void PlayEvenWhenSilenced() { }
#endif

        /// <summary>Applied as soon as the app starts, before the first scene's sounds.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnAppStart()
        {
            PlayEvenWhenSilenced();
            var go = new GameObject("AudioSession") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<AudioSessionKeeper>();
        }
    }
}
