using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// The scene's background: a seamless winter loop (forest wind and birds,
    /// open snowfield, or a still night), non-positional, fading in when the
    /// scene opens -- so it is already there while you scan the floor.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SceneAmbience : MonoBehaviour
    {
        [Range(0f, 1f)] public float volume = 0.45f;
        public float fadeInSeconds = 2.5f;

        AudioSource _src;
        float _t;

        public static void Configure(AudioSource s, AudioClip clip)
        {
            s.clip = clip;
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.volume = 0f;
            s.priority = 200;            // the animals' own sounds win if voices run short
        }

        void Start()
        {
            _src = GetComponent<AudioSource>();
            if (_src.clip == null) { enabled = false; return; }
            _src.volume = 0f;
            _src.time = Random.Range(0f, _src.clip.length * 0.9f);   // not the same start every visit
            _src.Play();
        }

        [Tooltip("Share of the volume kept while a minigame runs, so its sounds come through.")]
        [Range(0f, 1f)] public float duckDuringGames = 0.45f;
        float _duck = 1f;

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            float fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t / Mathf.Max(fadeInSeconds, 0.01f)));
            _duck = Mathf.MoveTowards(_duck, MiniGameHost.Running ? duckDuringGames : 1f, Time.unscaledDeltaTime / 0.6f);
            _src.volume = volume * fade * _duck;
        }
    }
}
