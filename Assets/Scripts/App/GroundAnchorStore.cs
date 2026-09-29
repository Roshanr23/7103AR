using System;
using UnityEngine;

namespace AR7103.App
{
    /// <summary>
    /// Where the scanned ground is, for anything in the AR scene that needs it.
    ///
    /// The Vuforia anchor itself is the source of truth -- Vuforia keeps nudging
    /// its transform as tracking refines -- so read <see cref="Anchor"/> for a
    /// live pose. <see cref="Position"/> / <see cref="Rotation"/> are the pose at
    /// the moment the ground was locked, kept for logging and comparisons.
    ///
    /// <see cref="HasGround"/> and the stored pose last for the whole app run,
    /// so the menu knows the scan is done. <see cref="Anchor"/> does NOT: the
    /// scan happens in its own scene, and a Vuforia anchor cannot outlive the
    /// ARCamera that created it. Once the app leaves GroundScan, Anchor is null
    /// and the pose is in a world frame the next scene's session does not share.
    /// </summary>
    public static class GroundAnchorStore
    {
        public static bool HasGround { get; private set; }
        public static Transform Anchor { get; private set; }
        public static Vector3 Position { get; private set; }
        public static Quaternion Rotation { get; private set; }

        /// <summary>Raised once, when the ground is first locked.</summary>
        public static event Action<Transform> GroundStored;

        public static void Store(Transform anchor)
        {
            if (anchor == null) return;
            Anchor = anchor;
            Position = anchor.position;
            Rotation = anchor.rotation;
            bool first = !HasGround;
            HasGround = true;
            Debug.Log($"[Ground] Stored ground at {Position} (first={first})");
            if (first) GroundStored?.Invoke(anchor);
        }

        public static void Clear()
        {
            HasGround = false;
            Anchor = null;
        }
    }
}
