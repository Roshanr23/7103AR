using System.Collections;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// One animal's minigame. Lives on the animal (beside its GroundSpawn); the
    /// HUD's MiniGameHost runs it: intro, 3-2-1, <see cref="Run"/>, results.
    /// A game drives its animal through that animal's own script (FoxWalk,
    /// SquirrelPalm.ScurryTo, HareHop.HopTo, DeerPalm, OwlFlight) and reads input
    /// from the host's tap events and choice buttons.
    /// </summary>
    public abstract class MiniGame : MonoBehaviour
    {
        /// <summary>"Mouse Hunt".</summary>
        public abstract string Title { get; }
        /// <summary>Short id for the saved best score.</summary>
        public abstract string Key { get; }
        /// <summary>One-line instruction for the intro card.</summary>
        public abstract string HowTo { get; }
        /// <summary>"5 rounds", "30 seconds".</summary>
        public abstract string Length { get; }
        /// <summary>What the score counts ("points", "acorns").</summary>
        public virtual string Unit => "points";

        /// <summary>Play the game; set host.Score as it goes. Ends when it returns.</summary>
        public abstract IEnumerator Run(MiniGameHost host);

        /// <summary>Put everything back (always called: finished, quit or left).</summary>
        public virtual void Cleanup(MiniGameHost host) { }

        /// <summary>0-3 stars for a final score.</summary>
        public abstract int Stars(int score);

        /// <summary>The results card's headline.</summary>
        public virtual string Verdict(int score, int stars) =>
            stars >= 3 ? "Brilliant!" : stars == 2 ? "Nicely done!" : stars == 1 ? "Not bad!" : "Have another go";

        /// <summary>A line under the score (round breakdown, time).</summary>
        public virtual string Detail(MiniGameHost host) => "";

        protected GroundSpawn Spawn => GetComponent<GroundSpawn>();

        /// <summary>The animal's height, in stage units.</summary>
        protected float AnimalHeight()
        {
            var s = Spawn;
            float stage = transform.parent != null ? Mathf.Max(transform.parent.lossyScale.y, 1e-5f) : 1f;
            return s != null ? s.MeshBounds().size.y / stage : 0.5f;
        }

        protected static float E(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        protected static float Bump(float x) => Mathf.Sin(Mathf.PI * Mathf.Clamp01(x));
    }
}
