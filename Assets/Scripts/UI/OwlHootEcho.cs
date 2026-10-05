using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Snowy owl minigame, "Hoot Echo" -- a call-and-answer memory game. The owl
    /// flies over and hovers in front of you, then hoots a pattern of short and
    /// long hoots (bobbing its head with each, and the pattern is drawn as dots
    /// and dashes so it plays with the sound off too). Hoot it back with the
    /// Short / Long buttons. Each round adds one; one slip and it is over.
    /// </summary>
    [DefaultExecutionOrder(1001)]          // head bob goes on after the flight and the Animator
    public class OwlHootEcho : MiniGame
    {
        public override string Title => "Hoot Echo";
        public override string Key => "owl";
        public override string HowTo => "The owl will hoot a pattern of short and long hoots. Hoot it back with the buttons!";
        public override string Length => "Until a slip";
        public override string Unit => "hoots";

        public AudioClip shortHoot, longHoot;
        public int startLength = 2, maxLength = 10;
        public Transform head;

        OwlFlight _flight;
        AudioSource _voice;
        readonly List<int> _pattern = new List<int>();
        readonly List<int> _answer = new List<int>();
        float _bobUntil, _bobLength;
        int _best;

        /// <summary>Waiting for the player's answer (tests read the pattern from here).</summary>
        public bool Answering { get; private set; }
        public IReadOnlyList<int> Pattern => _pattern;

        public override int Stars(int s) => s >= 7 ? 3 : s >= 5 ? 2 : s >= 3 ? 1 : 0;
        public override string Verdict(int s, int stars) => stars >= 3 ? "Owl whisperer!" : stars == 2 ? "Great echo!" : stars == 1 ? "Good ears" : "Hoo knew?";
        public override string Detail(MiniGameHost h) => $"Longest pattern: {_best} hoots";

        public override IEnumerator Run(MiniGameHost host)
        {
            _flight = GetComponent<OwlFlight>();
            _voice = GetComponent<AudioSource>();
            _pattern.Clear(); _best = 0;
            if (_flight != null) _flight.ComeToViewer();
            host.Hint("Here it comes", "The owl is flying over to you.");
            yield return host.Wait(2.6f);
            host.Chose += OnChoice;

            for (int len = startLength; len <= maxLength; len++)
            {
                if (_flight != null) _flight.ComeToViewer();            // stay put in front of the player
                host.SetRound("PATTERN", $"{len}");
                while (_pattern.Count < len) _pattern.Add(Random.value < 0.5f ? 0 : 1);

                // the owl hoots it
                host.ShowChoices("Short", "Long", false);
                host.Hint("Listen…", "");
                yield return host.Wait(0.5f);
                var shown = new StringBuilder();
                foreach (int h in _pattern)
                {
                    shown.Append(h == 0 ? "\u2022 " : "\u2014 ");
                    host.Hint("Listen…", shown.ToString());
                    yield return Hoot(h);
                    yield return host.Wait(0.28f);
                }

                // the player answers
                _answer.Clear();
                host.Hint("Your turn", "Hoot it back.");
                host.ShowChoices("Short", "Long", true);
                Answering = true;
                bool slipped = false;
                for (float t = 0f; _answer.Count < _pattern.Count && t < 20f; t += Time.deltaTime)
                {
                    if (_answer.Count > 0 && _answer[_answer.Count - 1] != _pattern[_answer.Count - 1]) { slipped = true; break; }
                    yield return null;
                }
                Answering = false;
                host.ShowChoices("Short", "Long", false);
                if (!slipped && _answer.Count == _pattern.Count && _answer[_answer.Count - 1] != _pattern[_answer.Count - 1]) slipped = true;
                if (slipped || _answer.Count < _pattern.Count)
                {
                    host.Popup(slipped ? "Wrong hoot!" : "Too slow!", 0);
                    yield return host.Wait(1.0f);
                    break;
                }
                _best = len;
                host.SetScore(len);
                host.Popup(len >= maxLength ? "Perfect echo!" : $"{len} in a row!", 2);
                yield return host.Wait(0.9f);
            }
            host.Chose -= OnChoice;
            host.SetScore(_best);
        }

        void OnChoice(int c)
        {
            if (!Answering) return;
            _answer.Add(c);
            StartCoroutine(Hoot(c, answer: true));
        }

        /// <summary>For tests: answer as if a button was pressed.</summary>
        public void Answer(int c) => OnChoice(c);

        IEnumerator Hoot(int kind, bool answer = false)
        {
            var clip = kind == 0 ? shortHoot : longHoot;
            if (clip != null && _voice != null) _voice.PlayOneShot(clip, answer ? 0.7f : 1f);
            _bobLength = clip != null ? clip.length : (kind == 0 ? 0.32f : 0.85f);
            _bobUntil = Time.time + _bobLength;
            if (!answer) for (float t = 0f; t < _bobLength; t += Time.deltaTime) yield return null;
        }

        void LateUpdate()
        {
            if (head == null || Time.time >= _bobUntil) return;
            // tip the head up while hooting, the way an owl throws its call
            float u = 1f - (_bobUntil - Time.time) / Mathf.Max(_bobLength, 0.05f);
            float w = Mathf.Sin(Mathf.PI * Mathf.Clamp01(u));
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            Vector3 right = Vector3.Cross(Vector3.up, f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward);
            head.rotation = Quaternion.AngleAxis(-18f * w, right) * head.rotation;
        }

        public override void Cleanup(MiniGameHost host)
        {
            host.Chose -= OnChoice;
            StopAllCoroutines();
            Answering = false;
            host.ShowChoices("Short", "Long", false);
            if (_flight != null) _flight.Release();
        }
    }
}
