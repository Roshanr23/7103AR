using System.Collections;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// White-tailed deer minigame, "Freeze!" -- creeping up on a wary deer, for
    /// real: you walk toward it while it grazes, head down, and hold your phone
    /// still whenever it lifts its head to look at you. Moving while it watches
    /// startles it (a strike; three and it bolts). Reach it -- within about a
    /// deer's height -- inside 45 seconds to win; the faster, the more points.
    /// </summary>
    public class DeerFreeze : MiniGame
    {
        public override string Title => "Freeze!";
        public override string Key => "deer";
        public override string HowTo => "Creep up on the deer while it grazes. When it looks up, freeze! Get close to win.";
        public override string Length => "45 seconds";

        public float seconds = 45f;
        public int strikesAllowed = 3;
        [Tooltip("Phone speed (m/s) that counts as moving while the deer watches.")]
        public float stillSpeed = 0.2f;
        [Tooltip("How close (in deer heights) counts as reaching it.")]
        public float reachHeights = 0.9f;

        DeerPalm _deer;
        AnimalTricks _tricks;
        int _strikes;
        float _sniffAfter;
        float _took;
        bool _won;

        public bool Watching { get; private set; }
        /// <summary>The timed part is on (after any step-back), tests.</summary>
        public bool Playing { get; private set; }
        public int Strikes => _strikes;
        public bool Won => _won;

        public override int Stars(int s) => !_won ? 0 : _strikes == 0 ? 3 : _strikes == 1 ? 2 : 1;
        public override string Verdict(int s, int stars) => !_won ? (_strikes >= strikesAllowed ? "It bolted!" : "Out of time") : stars >= 3 ? "Silent as snow!" : "You reached it!";
        public override string Detail(MiniGameHost h) => _won ? $"Reached in {_took:0} s  ·  {_strikes} strike{(_strikes == 1 ? "" : "s")}" : $"{_strikes} strike{(_strikes == 1 ? "" : "s")}";

        public override IEnumerator Run(MiniGameHost host)
        {
            _deer = GetComponent<DeerPalm>();
            _tricks = GetComponent<AnimalTricks>();
            _strikes = 0; _won = false; _took = 0f; Playing = false;
            if (_deer != null) { _sniffAfter = _deer.sniffAfter; _deer.sniffAfter = 999f; }     // it watches; no sniffing
            var viewer = host.Viewer();
            float stageScale = transform.parent != null ? transform.parent.lossyScale.y : 1f;
            float reach = reachHeights * AnimalHeight() * stageScale;       // world metres
            float Dist() { if (viewer == null) return 99f; Vector3 d = viewer.position - transform.position; d.y = 0f; return d.magnitude; }

            // start from a little way off
            if (Dist() < reach + 0.6f * stageScale)
            {
                host.Hint("Step back a little", "Start a few steps away from the deer.");
                for (float t = 0f; t < 8f && Dist() < reach + 0.6f * stageScale; t += Time.deltaTime) yield return null;
                // no room to step back: move the finish line instead, so there is always some creeping to do
                if (Dist() < reach + 0.6f * stageScale) reach = Mathf.Max(0.3f * stageScale, Dist() - 0.6f * stageScale);
            }
            Playing = true;

            Vector3 last = viewer != null ? viewer.position : Vector3.zero;
            float speed = 0f, moving = 0f;
            float phaseEnd = 0f;
            bool look = false;
            float lookStart = 0f;
            float t0 = Time.time;
            SetGraze(host, false, ref phaseEnd);

            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                host.SetTimer(seconds - t);
                // how fast the phone is moving (smoothed), world m/s
                if (viewer != null && Time.deltaTime > 0f)
                {
                    float inst = (viewer.position - last).magnitude / Time.deltaTime;
                    speed = Mathf.Lerp(speed, inst, 1f - Mathf.Exp(-Time.deltaTime / 0.15f));
                    last = viewer.position;
                }
                float d = Dist();
                host.SetScore(Mathf.Max(0, Mathf.RoundToInt((1f - Mathf.InverseLerp(reach, reach + 3f * stageScale, d)) * 100f)));
                host.SetRound("CLOSE", $"{Mathf.RoundToInt((1f - Mathf.InverseLerp(reach, reach + 3f * stageScale, d)) * 100f)}%");

                if (d <= reach) { _won = true; _took = Time.time - t0; break; }

                if (Time.time >= phaseEnd)
                {
                    look = !look;
                    if (look) { lookStart = Time.time; phaseEnd = Time.time + Random.Range(1.8f, 2.8f); Watch(host, true); }
                    else SetGraze(host, false, ref phaseEnd);
                }
                // caught moving: after a short grace while the head comes up
                if (look && Time.time - lookStart > 0.6f)
                {
                    moving = speed > stillSpeed ? moving + Time.deltaTime : 0f;
                    if (moving > 0.25f)
                    {
                        _strikes++;
                        moving = 0f;
                        host.Popup(_strikes >= strikesAllowed ? "It bolted!" : $"Spotted! {strikesAllowed - _strikes} left", 0);
                        if (_tricks != null) { Watch(host, false); _tricks.Perform(AnimalTricks.Trick.Startle); }
                        yield return host.Wait(1.6f);                   // let the startle play out, even on the last strike
                        if (_strikes >= strikesAllowed) break;
                        look = false;
                        SetGraze(host, false, ref phaseEnd);
                    }
                }
                yield return null;
            }
            Playing = false;
            Watch(host, false);
            if (_deer != null) _deer.graze = 0f;
            host.SetScore(_won ? 100 + Mathf.RoundToInt(Mathf.Max(0f, seconds - _took) * 10f) - _strikes * 50 : 0);
            if (_won) host.Popup("You reached it!", 2);
            yield return host.Wait(0.8f);
        }

        void SetGraze(MiniGameHost host, bool _, ref float phaseEnd)
        {
            Watch(host, false);
            if (_deer != null) _deer.graze = 1f;
            phaseEnd = Time.time + Random.Range(2.2f, 4.0f);
            host.Hint("Creep closer…", "It's grazing. Walk toward it now.");
        }

        void Watch(MiniGameHost host, bool on)
        {
            Watching = on;
            if (_deer != null)
            {
                _deer.ForcedCall = on;
                if (on) _deer.graze = 0f;
            }
            if (on) { host.Hint("Freeze!", "It's watching you. Keep your phone still."); Haptics_Tick(); }
        }

        static void Haptics_Tick() => AR7103.App.Haptics.Medium();

        public override void Cleanup(MiniGameHost host)
        {
            StopAllCoroutines();
            Watching = false; Playing = false;
            if (_deer != null) { _deer.ForcedCall = null; _deer.graze = 0f; if (_sniffAfter > 0f) _deer.sniffAfter = _sniffAfter; }
        }
    }
}
