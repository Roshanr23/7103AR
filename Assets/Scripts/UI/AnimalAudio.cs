using System;
using AR7103.Hands;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AR7103.UI
{
    /// <summary>
    /// The animal's voice: idle calls now and then once it has appeared, a
    /// different sound when it answers an open palm, and named cues the behaviour
    /// scripts trigger at moments of their own (the deer's sniffing, the hare
    /// sitting up). Plays from a 3D AudioSource on the animal, so the sound comes
    /// from where it is in the room and gets louder as it comes closer.
    ///
    /// Lives on the spawned animal, so nothing plays until it is placed. The four
    /// floor animals call <see cref="PlayGesture"/> from PalmReaction the moment
    /// they start reacting; the owl's is wired to HandGestureManager directly.
    /// </summary>
    public class AnimalAudio : MonoBehaviour
    {
        [Header("Idle calls")]
        public AudioClip[] idleCalls;
        [Tooltip("Seconds between idle calls (random in this range).")]
        public Vector2 idleGap = new Vector2(8f, 15f);
        [Tooltip("Seconds after appearing before the first call.")]
        public float firstCallDelay = 2.5f;
        [Range(0f, 1f)] public float idleVolume = 0.8f;

        [Header("Open palm")]
        public AudioClip[] gestureClips;
        [Range(0f, 1f)] public float gestureVolume = 1f;
        [Tooltip("Seconds before the gesture sound can play again.")]
        public float gestureCooldown = 2.5f;

        [Serializable]
        public struct Cue
        {
            public string name;
            public AudioClip clip;
            [Range(0f, 1f)] public float volume;
        }
        [Header("Cues the behaviour scripts trigger")]
        public Cue[] cues;

        [Header("Arrival and footsteps")]
        [Tooltip("Played as the animal appears (a soft puff of snow).")]
        public AudioClip appearClip;
        [Range(0f, 1f)] public float appearVolume = 0.5f;
        [Range(0f, 1f)] public float footstepVolume = 0.45f;

        /// <summary>Raised for every sound played (debug logging, editor previews).</summary>
        public static event Action<AnimalAudio, AudioClip> Played;

        /// <summary>Set while the animal is busy with the viewer: idle calls wait.</summary>
        public bool Engaged { get; set; }

        AudioSource _src, _fx;
        float _nextIdle, _lastGesture = -99f, _quietUntil;
        int _lastIdle = -1;

        void Awake()
        {
            _src = Source();
            // footsteps get their own source so they never cut off (or hold back) a call
            _fx = gameObject.AddComponent<AudioSource>();
            Configure(_fx);
            _fx.priority = 160;
        }

        void OnEnable()
        {
            _nextIdle = Time.time + firstCallDelay + Random.Range(0f, 1.5f);
            _quietUntil = 0f;
            if (appearClip != null && _fx != null)
            {
                _fx.PlayOneShot(appearClip, appearVolume);
                Played?.Invoke(this, appearClip);
            }
        }

        AudioSource Source()
        {
            if (_src != null) return _src;
            _src = GetComponent<AudioSource>();
            if (_src == null)
            {
                _src = gameObject.AddComponent<AudioSource>();
                Configure(_src);
            }
            return _src;
        }

        /// <summary>3D, close-range: full volume within ~1.5 m, fading gently with distance.</summary>
        public static void Configure(AudioSource s)
        {
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 1.5f;
            s.maxDistance = 25f;
            s.dopplerLevel = 0f;
            s.spread = 30f;
        }

        void Update()
        {
            if (idleCalls == null || idleCalls.Length == 0) return;
            float now = Time.time;
            // busy with the viewer, or a minigame on (a bark would bury the sounds the game is about)
            if (Engaged || MiniGameHost.Running) { _nextIdle = Mathf.Max(_nextIdle, now + 3f); return; }
            if (now < _nextIdle || now < _quietUntil || Source().isPlaying) return;

            int i = Random.Range(0, idleCalls.Length);
            if (idleCalls.Length > 1 && i == _lastIdle) i = (i + 1) % idleCalls.Length;   // no repeats back to back
            _lastIdle = i;
            Play(idleCalls[i], idleVolume, pitchJitter: 0.05f);
            _nextIdle = now + Random.Range(idleGap.x, idleGap.y);
        }

        /// <summary>The open-palm reaction sound.</summary>
        public void PlayGesture()
        {
            if (!isActiveAndEnabled || gestureClips == null || gestureClips.Length == 0) return;
            if (Time.time - _lastGesture < gestureCooldown) return;
            _lastGesture = Time.time;
            var clip = gestureClips[Random.Range(0, gestureClips.Length)];
            Source().Stop();                                   // cut off any idle call
            Play(clip, gestureVolume, pitchJitter: 0.03f);
            _quietUntil = Time.time + clip.length + 3f;
            _nextIdle = Mathf.Max(_nextIdle, _quietUntil + Random.Range(2f, 5f));
        }

        /// <summary>Wired to HandGestureManager.onGestureBegan for animals without a PalmReaction (the owl).</summary>
        public void OnGestureBegan(Gesture g)
        {
            if (g == Gesture.OpenPalm) PlayGesture();
        }

        /// <summary>
        /// Play a named cue (e.g. "sniff"); a random one if several share the name,
        /// nothing if there is none. Voice cues hold back idle calls for a moment;
        /// footsteps (<paramref name="footstep"/>) go on their own source and do not.
        /// </summary>
        public void PlayCue(string cueName, float volumeScale = 1f, bool footstep = false)
        {
            if (!isActiveAndEnabled || cues == null) return;
            int count = 0;
            foreach (var c in cues) if (c.name == cueName && c.clip != null) count++;
            if (count == 0) return;
            int pick = Random.Range(0, count);
            foreach (var c in cues)
            {
                if (c.name != cueName || c.clip == null || pick-- > 0) continue;
                float v = (c.volume > 0f ? c.volume : 1f) * volumeScale;
                if (footstep && _fx != null)
                {
                    _fx.pitch = 1f + Random.Range(-0.08f, 0.08f);
                    _fx.PlayOneShot(c.clip, v * footstepVolume);
                }
                else
                {
                    Source().PlayOneShot(c.clip, v);
                    _quietUntil = Mathf.Max(_quietUntil, Time.time + c.clip.length + 1f);
                }
                Played?.Invoke(this, c.clip);
                return;
            }
        }

        void Play(AudioClip clip, float volume, float pitchJitter)
        {
            if (clip == null) return;
            var s = Source();
            s.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);   // no two calls quite alike
            s.clip = clip;
            s.volume = volume;
            s.Play();
            Played?.Invoke(this, clip);
        }
    }
}
