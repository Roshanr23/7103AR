using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AR7103.Hands
{
    /// <summary>21 joints, in the order the native plugin writes them.</summary>
    public enum HandJoint
    {
        Wrist = 0,
        ThumbCMC, ThumbMP, ThumbIP, ThumbTip,
        IndexMCP, IndexPIP, IndexDIP, IndexTip,
        MiddleMCP, MiddlePIP, MiddleDIP, MiddleTip,
        RingMCP, RingPIP, RingDIP, RingTip,
        LittleMCP, LittlePIP, LittleDIP, LittleTip,
    }

    public enum Chirality { Unknown = 0, Left = 1, Right = 2 }

    /// <summary>One detected hand: normalised joint positions plus per-joint confidence.</summary>
    public struct HandObservation
    {
        public Chirality Chirality;
        public Vector2[] Points;      // normalised 0..1, origin bottom-left
        public float[] Confidence;    // 0 means the joint was not found

        public Vector2 Get(HandJoint j) => Points[(int)j];
        public float Conf(HandJoint j) => Confidence[(int)j];

        /// <summary>True when enough joints were seen for the pose to be worth reading.</summary>
        public bool IsReliable(float minConfidence, int minJoints)
        {
            int good = 0;
            for (int i = 0; i < Confidence.Length; i++)
                if (Confidence[i] >= minConfidence) good++;
            return good >= minJoints;
        }
    }

    /// <summary>
    /// Thin wrapper over the native Apple Vision plugin.
    ///
    /// Only does anything on a real iOS device. Vision needs a camera, so in the
    /// Editor every call is a no-op returning zero hands -- that is deliberate, so
    /// the rest of the pipeline can be wired up and stepped through without a
    /// device build. <see cref="IsSupported"/> tells you which case you are in.
    /// </summary>
    public static class HandPoseProvider
    {
        public const int JointsPerHand = 21;
        const int FloatsPerHand = JointsPerHand * 3;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int HandPose_IsSupported();
        [DllImport("__Internal")] static extern int HandPose_Detect(
            byte[] bytes, int width, int height, int channels,
            int orientation, int maxHands, float[] outJoints, int[] outChirality);
#else
        static int HandPose_IsSupported() => 0;
        static int HandPose_Detect(byte[] b, int w, int h, int c,
                                   int o, int m, float[] j, int[] ch) => 0;
#endif

        static float[] _jointBuf;
        static int[] _chiralityBuf;

        public static bool IsSupported => HandPose_IsSupported() == 1;

        /// <summary>
        /// Run detection on one frame. Returns the number of hands written into
        /// <paramref name="results"/>; negative values are native errors.
        /// </summary>
        /// <param name="orientation">
        /// CGImagePropertyOrientation. 1 = up, 3 = down, 6 = right, 8 = left.
        /// The camera buffer is not in screen orientation, so this has to be set
        /// correctly or hands read as rotated and every gesture misfires.
        /// </param>
        public static int Detect(byte[] pixels, int width, int height, int channels,
                                 int orientation, int maxHands, HandObservation[] results)
        {
            if (pixels == null || results == null || maxHands <= 0) return -1;
            if (results.Length < maxHands)
                throw new ArgumentException("results array is smaller than maxHands", nameof(results));

            int needed = maxHands * FloatsPerHand;
            if (_jointBuf == null || _jointBuf.Length < needed) _jointBuf = new float[needed];
            if (_chiralityBuf == null || _chiralityBuf.Length < maxHands) _chiralityBuf = new int[maxHands];

            int count = HandPose_Detect(pixels, width, height, channels,
                                        orientation, maxHands, _jointBuf, _chiralityBuf);
            if (count <= 0) return count;

            for (int h = 0; h < count; h++)
            {
                if (results[h].Points == null)
                {
                    results[h].Points = new Vector2[JointsPerHand];
                    results[h].Confidence = new float[JointsPerHand];
                }
                results[h].Chirality = (Chirality)_chiralityBuf[h];
                int b = h * FloatsPerHand;
                for (int j = 0; j < JointsPerHand; j++)
                {
                    int o = b + j * 3;
                    results[h].Points[j] = new Vector2(_jointBuf[o], _jointBuf[o + 1]);
                    results[h].Confidence[j] = _jointBuf[o + 2];
                }
            }
            return count;
        }

        public static HandObservation[] AllocResults(int maxHands)
        {
            var arr = new HandObservation[maxHands];
            for (int i = 0; i < maxHands; i++)
            {
                arr[i].Points = new Vector2[JointsPerHand];
                arr[i].Confidence = new float[JointsPerHand];
            }
            return arr;
        }
    }
}
