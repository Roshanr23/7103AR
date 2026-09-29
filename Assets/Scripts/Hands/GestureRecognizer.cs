using UnityEngine;

namespace AR7103.Hands
{
    public enum Gesture { None = 0, OpenPalm, Fist, Pinch, Point, Peace }

    /// <summary>
    /// Classifies a <see cref="HandObservation"/> into a <see cref="Gesture"/>.
    ///
    /// Deliberately geometric rather than learned: Vision already did the hard
    /// part by giving us joints, and simple finger-extension tests are debuggable
    /// on-device in a way a second model would not be.
    ///
    /// All tests work in Vision's normalised coordinate space, so they are
    /// resolution-independent. Distances are expressed relative to hand size
    /// (wrist -> middle MCP) so they hold whether the hand is near or far.
    /// </summary>
    public static class GestureRecognizer
    {
        // A finger counts as extended when its tip is further from the wrist than
        // its PIP joint by this factor. Curled fingers fold the tip back inward,
        // which makes this ratio flip cleanly without needing angles.
        const float ExtendRatio = 1.12f;

        // Thumb sits at a different angle to the palm, so it needs a looser test.
        const float ThumbExtendRatio = 1.05f;

        // Pinch: thumb tip to index tip, as a fraction of hand size.
        const float PinchDistance = 0.32f;

        // A finger whose joints are not confident is UNKNOWN, not curled. Collapsing
        // those two cases makes an unreadable hand classify as a confident Fist,
        // which fires on every motion blur, occlusion and frame-edge exit.
        const int MinFingersToJudge = 3;   // below this we admit we cannot tell
        const int MinFingersForFist = 4;   // "all curled" needs near-full visibility

        struct Fingers
        {
            public bool Thumb, Index, Middle, Ring, Little;
            public int Evaluated;          // fingers we actually had data for
            public int ExtendedCount =>
                (Thumb ? 1 : 0) + (Index ? 1 : 0) + (Middle ? 1 : 0) +
                (Ring ? 1 : 0) + (Little ? 1 : 0);
        }

        /// <summary>null = not enough confidence to judge this finger.</summary>
        static bool? TryExtended(in HandObservation h, HandJoint pip, HandJoint tip,
                                 float minConf, float ratio)
        {
            if (h.Conf(pip) < minConf || h.Conf(tip) < minConf) return null;
            Vector2 wrist = h.Get(HandJoint.Wrist);
            float dPip = Vector2.Distance(wrist, h.Get(pip));
            float dTip = Vector2.Distance(wrist, h.Get(tip));
            if (dPip <= Mathf.Epsilon) return null;
            return (dTip / dPip) >= ratio;
        }

        static Fingers ReadFingers(in HandObservation h, float minConf)
        {
            var f = new Fingers();
            void Apply(bool? v, ref bool slot)
            {
                if (!v.HasValue) return;
                f.Evaluated++;
                slot = v.Value;
            }
            Apply(TryExtended(h, HandJoint.ThumbIP,   HandJoint.ThumbTip,  minConf, ThumbExtendRatio), ref f.Thumb);
            Apply(TryExtended(h, HandJoint.IndexPIP,  HandJoint.IndexTip,  minConf, ExtendRatio),      ref f.Index);
            Apply(TryExtended(h, HandJoint.MiddlePIP, HandJoint.MiddleTip, minConf, ExtendRatio),      ref f.Middle);
            Apply(TryExtended(h, HandJoint.RingPIP,   HandJoint.RingTip,   minConf, ExtendRatio),      ref f.Ring);
            Apply(TryExtended(h, HandJoint.LittlePIP, HandJoint.LittleTip, minConf, ExtendRatio),      ref f.Little);
            return f;
        }

        /// <summary>Wrist to middle-finger MCP: a stable proxy for apparent hand size.</summary>
        static float HandSize(in HandObservation h)
        {
            float d = Vector2.Distance(h.Get(HandJoint.Wrist), h.Get(HandJoint.MiddleMCP));
            return d > Mathf.Epsilon ? d : 1f;
        }

        public static Gesture Classify(in HandObservation h, float minConfidence = 0.4f)
        {
            // Every test below measures from the wrist and scales by hand size, so
            // if either of those anchors is unreliable the whole frame is garbage.
            if (h.Conf(HandJoint.Wrist) < minConfidence ||
                h.Conf(HandJoint.MiddleMCP) < minConfidence)
                return Gesture.None;

            // Pinch is checked first: a pinching hand often still reads as having
            // several fingers extended, so an extension-count test would shadow it.
            if (h.Conf(HandJoint.ThumbTip) >= minConfidence &&
                h.Conf(HandJoint.IndexTip) >= minConfidence)
            {
                float pinch = Vector2.Distance(h.Get(HandJoint.ThumbTip), h.Get(HandJoint.IndexTip));
                if (pinch / HandSize(h) < PinchDistance) return Gesture.Pinch;
            }

            Fingers f = ReadFingers(h, minConfidence);
            if (f.Evaluated < MinFingersToJudge) return Gesture.None;

            if (f.ExtendedCount >= 4) return Gesture.OpenPalm;
            if (f.ExtendedCount == 0)
                return f.Evaluated >= MinFingersForFist ? Gesture.Fist : Gesture.None;
            if (f.Index && f.Middle && !f.Ring && !f.Little) return Gesture.Peace;
            if (f.Index && !f.Middle && !f.Ring && !f.Little) return Gesture.Point;

            return Gesture.None;
        }

        /// <summary>Palm centroid in normalised space - handy for aiming at a target.</summary>
        public static Vector2 PalmCenter(in HandObservation h)
        {
            Vector2 sum = h.Get(HandJoint.Wrist) + h.Get(HandJoint.IndexMCP) +
                          h.Get(HandJoint.MiddleMCP) + h.Get(HandJoint.RingMCP) +
                          h.Get(HandJoint.LittleMCP);
            return sum / 5f;
        }
    }
}
