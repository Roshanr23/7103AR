using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AR7103.App;
using AR7103.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Is the sound really working? In Play Mode, in every animal scene:
    ///   * exactly one enabled AudioListener, on the AR camera, and not muted;
    ///   * every clip any component points at exists, loads, and is not silent;
    ///   * the ambience is playing (looping, audible volume);
    ///   * the animal's arrival puff, an idle call, the open-palm sound, a gesture
    ///     trick's sound, footsteps where it has them, and the minigame's sounds all
    ///     actually start an AudioSource playing;
    ///   * those sources are loud enough at the phone's distance (3D rolloff);
    ///   * and the mixer's output is not silent while they play.
    ///   Unity -batchmode -projectPath . -executeMethod AR7103.EditorTools.AudioPlayTest.Run
    /// </summary>
    [InitializeOnLoad]
    public static class AudioPlayTest
    {
        const string Flag = "ar7103.audioTest";
        static readonly string[] Scenes = { AppScenes.Squirrel, AppScenes.Fox, AppScenes.Deer, AppScenes.Hare, AppScenes.Owl };

        static AudioPlayTest()
        {
            if (!SessionState.GetBool(Flag, false)) return;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredPlayMode) Begin(); };
        }

        public static void Run()
        {
            SessionState.SetBool(Flag, true);
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene($"Assets/Scenes/{Scenes[0]}.unity");
            EditorApplication.EnterPlaymode();
        }

        static IEnumerator<float> _steps;
        static float _wakeAt;
        static int _fails, _checks;
        static string _key;
        static readonly List<string> _failed = new List<string>();

        static void Begin() { _steps = All().GetEnumerator(); EditorApplication.update += Tick; }

        static void Tick()
        {
            if (Time.realtimeSinceStartup < _wakeAt) return;
            bool more;
            try { more = _steps.MoveNext(); }
            catch (Exception e) { Debug.LogError("[Audio] crashed: " + e); _fails++; more = false; }
            if (more) { _wakeAt = Time.realtimeSinceStartup + _steps.Current; return; }
            EditorApplication.update -= Tick;
            SessionState.SetBool(Flag, false);
            foreach (var f in _failed) Debug.Log("[Audio] FAILED: " + f);
            Debug.Log(_fails == 0 ? $"[Audio] ALL {_checks} CHECKS PASSED" : $"[Audio] {_fails} of {_checks} CHECKS FAILED");
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) { _fails++; _failed.Add($"{_key}: {what}"); }
            Debug.Log($"[Audio] {_key,-8} {(ok ? "ok  " : "FAIL")} {what}");
        }

        static IEnumerable<float> All()
        {
            foreach (var scene in Scenes)
            {
                if (SceneManager.GetActiveScene().name != scene) { SceneManager.LoadScene(scene); yield return 0.8f; }
                foreach (var w in One(scene)) yield return w;
            }
        }

        /// <summary>RMS of a clip's samples (0 = silent). Null when the samples cannot be read.</summary>
        static float? Rms(AudioClip c)
        {
            if (c == null) return null;
            if (c.loadState != AudioDataLoadState.Loaded) c.LoadAudioData();
            if (c.loadType != AudioClipLoadType.DecompressOnLoad) return null;      // compressed-in-memory: not readable
            var data = new float[Math.Min(c.samples * c.channels, 44100 * 4)];
            if (!c.GetData(data, 0)) return null;
            double sum = 0; foreach (var v in data) sum += v * v;
            return (float)Math.Sqrt(sum / Math.Max(1, data.Length));
        }

        /// <summary>Volume factor of a 3D source heard at a distance (Unity's logarithmic / linear rolloff).</summary>
        static float Rolloff(AudioSource s, float d)
        {
            if (s.spatialBlend < 0.01f) return 1f;
            float g = s.rolloffMode == AudioRolloffMode.Linear
                ? Mathf.Clamp01(1f - (d - s.minDistance) / Mathf.Max(0.01f, s.maxDistance - s.minDistance))
                : (d <= s.minDistance ? 1f : s.minDistance / d);
            return Mathf.Lerp(1f, g, s.spatialBlend);
        }

        static float OutputLevel()
        {
            var buf = new float[1024];
            AudioListener.GetOutputData(buf, 0);
            double sum = 0; foreach (var v in buf) sum += v * v;
            return (float)Math.Sqrt(sum / buf.Length);
        }

        static IEnumerable<float> One(string scene)
        {
            _key = scene.Replace("AR_", "").ToLowerInvariant();
            yield return 0.5f;

            // ---- listener
            var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Where(l => l.isActiveAndEnabled).ToArray();
            Check(listeners.Length == 1, $"exactly one active AudioListener ({listeners.Length}: {string.Join(", ", listeners.Select(l => l.name))})");
            var arCam = Vuforia.VuforiaBehaviour.Instance != null ? Vuforia.VuforiaBehaviour.Instance.gameObject : GameObject.Find("ARCamera");
            Check(listeners.Length == 1 && arCam != null && listeners[0].gameObject == arCam, $"the listener is on the AR camera ({(listeners.Length > 0 ? listeners[0].name : "-")})");
            Check(GameObject.Find("AudioSession") != null || UnityEngine.Object.FindFirstObjectByType<AudioSessionKeeper>() != null,
                  "the silent-switch fix is running (AudioSession keeper)");
            Check(AudioListener.volume > 0.99f && !AudioListener.pause, $"not muted (volume {AudioListener.volume}, paused {AudioListener.pause}, saved mute {PlayerPrefs.GetInt("ar7103.muted", 0)})");
            var ear = listeners.Length > 0 ? listeners[0].transform : null;

            // ---- every clip that anything points at
            var clips = new Dictionary<string, AudioClip>();
            void Add(string where, AudioClip c) { clips[where] = c; }
            var stage = GameObject.Find("Ground Plane Stage").transform;
            var spawn = stage.GetComponentsInChildren<GroundSpawn>(true).First();
            var voice = spawn.GetComponent<AnimalAudio>();
            Check(voice != null, "the animal has AnimalAudio");
            if (voice == null) yield break;
            foreach (var (c, i) in (voice.idleCalls ?? new AudioClip[0]).Select((c, i) => (c, i))) Add($"idle[{i}]", c);
            foreach (var (c, i) in (voice.gestureClips ?? new AudioClip[0]).Select((c, i) => (c, i))) Add($"gesture[{i}]", c);
            foreach (var cue in voice.cues ?? new AnimalAudio.Cue[0]) Add($"cue '{cue.name}' {cue.clip?.name}", cue.clip);
            Add("appear", voice.appearClip);
            var amb = UnityEngine.Object.FindFirstObjectByType<SceneAmbience>();
            Check(amb != null, "the scene has an ambience");
            var ambSrc = amb != null ? amb.GetComponent<AudioSource>() : null;
            if (ambSrc != null) Add("ambience", ambSrc.clip);
            var hud = UnityEngine.Object.FindFirstObjectByType<ARHud>();
            var host = hud.GetComponent<MiniGameHost>();
            foreach (var f in typeof(MiniGameHost).GetFields().Where(f => f.FieldType == typeof(AudioClip))) Add($"game host {f.Name}", (AudioClip)f.GetValue(host));
            var photo = hud.GetComponent<PhotoCapture>();
            if (photo != null) Add("shutter", photo.shutterSound);
            var game = spawn.GetComponent<MiniGame>();
            foreach (var f in game.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.FieldType == typeof(AudioClip)) Add($"{game.Title} {f.Name}", (AudioClip)f.GetValue(game));
                if (f.FieldType == typeof(AudioClip[])) foreach (var (c, i) in ((AudioClip[])f.GetValue(game) ?? new AudioClip[0]).Select((c, i) => (c, i))) Add($"{game.Title} {f.Name}[{i}]", c);
            }
            var missing = clips.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
            Check(missing.Count == 0, missing.Count == 0 ? $"all {clips.Count} clips are assigned" : $"unassigned: {string.Join(", ", missing)}");
            var silent = new List<string>();
            foreach (var kv in clips.Where(kv => kv.Value != null))
            {
                var c = kv.Value;
                c.LoadAudioData();
                float? r = Rms(c);
                if (c.length <= 0.01f || (r.HasValue && r.Value < 0.003f)) silent.Add($"{kv.Key} ({c.name}, rms {r:0.0000})");
            }
            Check(silent.Count == 0, silent.Count == 0 ? "every clip has sound in it" : $"silent or empty: {string.Join(", ", silent)}");

            // ---- the ambience plays as the scene opens
            yield return 3.2f;
            Check(ambSrc != null && ambSrc.isPlaying && ambSrc.loop, $"ambience playing and looping ({ambSrc?.clip?.name})");
            Check(ambSrc != null && ambSrc.volume > 0.3f, $"ambience faded up to an audible level ({ambSrc?.volume:0.00})");
            // measured over a second: the very first scene can take a moment to start the mixer
            float ambOut = 0f;
            for (int i = 0; i < 10; i++) { yield return 0.1f; ambOut = Mathf.Max(ambOut, OutputLevel()); }
            Check(ambOut > 1e-5f, $"the mixer is putting out sound (level {ambOut:0.00000})");

            // ---- the animal arrives in front of the phone
            var phone = Camera.main != null ? Camera.main.transform : ear;
            if (ear != null) { ear.position = _key == "deer" ? new Vector3(0.4f, 1.5f, 3.2f) : new Vector3(0.3f, 1.25f, 1.9f); }
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
            var cam = ear != null ? ear.GetComponent<Camera>() : null;
            var palm = spawn.GetComponent<PalmReaction>();
            var tricks = spawn.GetComponent<AnimalTricks>();
            if (palm != null) palm.viewerOverride = ear;
            if (tricks != null) tricks.viewerOverride = ear;
            host.viewerOverride = ear;
            var heard = new List<string>();
            Action<AnimalAudio, AudioClip> onPlayed = (a, c) => heard.Add(c.name);
            AnimalAudio.Played += onPlayed;
            spawn.Spawn(stage, cam != null ? cam : Camera.main, instant: true);
            hud.MarkPlacedForTests();
            yield return 0.15f;
            Check(heard.Contains(voice.appearClip.name), $"the arrival puff plays as it appears ({string.Join(", ", heard)})");

            var sources = spawn.GetComponents<AudioSource>();
            Check(sources.Length >= 2, $"the animal has its voice and footstep sources ({sources.Length})");
            var main = sources.FirstOrDefault();
            float dist = ear != null ? Vector3.Distance(ear.position, spawn.transform.position) : 2f;
            foreach (var s in sources)
                Check(s.enabled && !s.mute && s.volume >= 0f && Rolloff(s, dist) > 0.35f && s.maxDistance > dist,
                      $"source audible at the phone ({dist:0.0} m: rolloff x{Rolloff(s, dist):0.00}, {s.rolloffMode} {s.minDistance}-{s.maxDistance} m)");

            // ---- an idle call, by itself, within its gap
            heard.Clear();
            float waitIdle = voice.firstCallDelay + 1.6f + 0.5f;
            for (float t = 0f; t < waitIdle && !heard.Any(n => voice.idleCalls.Any(c => c.name == n)); t += 0.2f) yield return 0.2f;
            bool idle = heard.Any(n => voice.idleCalls.Any(c => c.name == n));
            Check(idle && main.isPlaying, $"an idle call plays on its own ({string.Join(", ", heard)}, playing {main.isPlaying})");
            if (idle) { yield return 0.1f; float lvl = OutputLevel(); Check(lvl > ambOut * 0.5f, $"it comes out of the mixer (level {lvl:0.0000} vs ambience {ambOut:0.0000})"); }

            // ---- the open-palm sound
            yield return 1.5f;
            heard.Clear();
            if (palm != null) { palm.PalmUp(); yield return 0.3f; palm.PalmDown(); }
            else { voice.OnGestureBegan(AR7103.Hands.Gesture.OpenPalm); yield return 0.3f; }
            Check(heard.Any(n => voice.gestureClips.Any(c => c.name == n)) && main.isPlaying, $"open palm plays its sound ({string.Join(", ", heard)})");

            // ---- a gesture trick's sound
            yield return 3.0f;
            heard.Clear();
            bool started = tricks != null && tricks.Perform(AnimalTricks.Trick.Startle);
            yield return 0.3f;
            string startleClip = voice.cues.FirstOrDefault(c => c.name == "startle").clip?.name;
            Check(started && heard.Contains(startleClip), $"the fist (startle) sound plays ({startleClip}; heard {string.Join(", ", heard)})");
            yield return 2.0f;

            // ---- footsteps, where the animal has them
            var steps = voice.cues.Where(c => c.name == "step" || c.name == "land" || c.name == "patter").Select(c => c.clip.name).ToList();
            if (steps.Count > 0)
            {
                heard.Clear();
                if (_key == "squirrel" && palm != null) { palm.PalmUp(); }          // it only patters when it dashes
                for (float t = 0f; t < 8f && !heard.Any(steps.Contains); t += 0.2f) yield return 0.2f;
                if (palm != null) palm.PalmDown();
                // footsteps are one-shots on the second source (isPlaying does not report one-shots)
                var fx = sources.FirstOrDefault(s => s != main);
                Check(heard.Any(steps.Contains) && fx != null && fx.enabled && !fx.mute && voice.footstepVolume > 0.2f,
                      $"footsteps play while it moves ({string.Join(", ", heard.Where(steps.Contains).Distinct())}, step volume {voice.footstepVolume:0.00})");
                yield return 4f;
            }

            // ---- the minigame's own sounds
            var gameSounds = new List<string>();
            var hostSrc = host.GetComponents<AudioSource>();
            float ambBefore = ambSrc.volume;
            heard.Clear();
            host.StartGame();
            for (float t = 0f; t < 3.4f; t += 0.1f)
            {
                foreach (var s in hostSrc) if (s.isPlaying) gameSounds.Add("host");
                yield return 0.1f;
            }
            Check(gameSounds.Count > 0, "the game's countdown ticks are audible (the host's source plays)");
            Check(ambSrc.volume < ambBefore * 0.6f, $"the ambience dips during the game ({ambBefore:0.00} -> {ambSrc.volume:0.00})");
            // a stretch of play with no idle calls over it
            for (float t = 0f; t < voice.idleGap.y + 1f; t += 0.25f) yield return 0.25f;
            // a clip that is also one of the animal's cues (the hare sniffing as it sits up) is not an idle call
            var cueClips = new HashSet<string>(voice.cues.Where(c => c.clip != null).Select(c => c.clip.name));
            var idleDuring = heard.Where(n => voice.idleCalls.Any(c => c.name == n) && !cueClips.Contains(n)).ToList();
            Check(idleDuring.Count == 0, $"no idle calls over the game ({string.Join(", ", idleDuring)})");
            if (game is FoxMouseHunt)
            {
                var sq = GameObject.Find("MouseHunt Squeak")?.GetComponent<AudioSource>();
                float d = sq != null && ear != null ? Vector3.Distance(ear.position, sq.transform.position) : 99f;
                Check(sq != null && Rolloff(sq, d) > 0.5f, $"the mouse squeak is loud enough at the phone ({d:0.0} m: x{(sq != null ? Rolloff(sq, d) : 0f):0.00})");
            }
            host.Quit();
            yield return 1.2f;
            Check(ambSrc.volume > ambBefore * 0.95f, $"the ambience comes back up after ({ambSrc.volume:0.00})");

            AnimalAudio.Played -= onPlayed;
            Debug.Log($"[Audio] {_key,-8} mixer level with ambience only: {ambOut:0.00000}");
        }
    }
}
