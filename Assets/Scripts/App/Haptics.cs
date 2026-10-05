using System.Runtime.InteropServices;

namespace AR7103.App
{
    /// <summary>
    /// Light Taptic Engine feedback (Plugins/iOS/AR7103Haptics.mm). A no-op in
    /// the Editor and on other platforms.
    /// </summary>
    public static class Haptics
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void AR7103_Haptic(int kind);
        static void Fire(int kind) => AR7103_Haptic(kind);
#else
        static void Fire(int kind) { }
#endif
        /// <summary>A light tap: a palm recognised, a button pressed.</summary>
        public static void Light() => Fire(0);
        /// <summary>A firmer tap: the floor has locked.</summary>
        public static void Medium() => Fire(1);
        /// <summary>The success pattern: the animal has arrived.</summary>
        public static void Success() => Fire(2);
        /// <summary>A tiny tick: moving between carousel pages.</summary>
        public static void Tick() => Fire(3);
    }
}
