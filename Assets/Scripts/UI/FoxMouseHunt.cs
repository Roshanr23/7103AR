using System.Collections;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Red fox minigame, "Mouse Hunt" -- the fox's party trick for real. A mouse
    /// squeaks from a hidden spot under the snow near the fox (a 3D sound, plus a
    /// little twitch of snow, since phone speakers barely place sound). Tap where
    /// you think it is: the fox turns, crouches and pounces nose-first onto that
    /// spot, and the mouse pops up to show how close it was. Five rounds.
    /// </summary>
    public class FoxMouseHunt : MiniGame
    {
        public override string Title => "Mouse Hunt";
        public override string Key => "fox";
        public override string HowTo => "A mouse is squeaking under the snow. Tap where you hear it and the fox will pounce.";
        public override string Length => "5 rounds";

        public int rounds = 5;
        public AudioClip[] squeaks;
        public AudioClip pounceSound;
        public Material snowMaterial, mouseMaterial;

        readonly int[] _round = new int[8];
        FoxWalk _walk;
        Animator _anim;
        float _animSpeed;
        GameObject _lump, _mouse;
        AudioSource _squeakSrc;
        bool _tapped;
        Vector3 _tap, _home;
        /// <summary>Where the fox stood when the game began (tests).</summary>
        public Vector3 Home => _home;

        /// <summary>A mouse is squeaking and the game is waiting for a tap (tests).</summary>
        public bool Listening { get; private set; }
        /// <summary>Where the mouse is hiding, in stage space (tests).</summary>
        public Vector3 Spot { get; private set; }

        public override int Stars(int s) => s >= 400 ? 3 : s >= 250 ? 2 : s >= 100 ? 1 : 0;
        public override string Verdict(int s, int stars) => stars >= 3 ? "Sharp ears!" : stars == 2 ? "Good hunting!" : stars == 1 ? "Getting warmer" : "The mice win this time";
        public override string Detail(MiniGameHost h) => string.Join("  ·  ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, rounds), i => _round[i].ToString()));

        public override IEnumerator Run(MiniGameHost host)
        {
            _walk = GetComponent<FoxWalk>();
            _anim = GetComponentInChildren<Animator>();
            if (_walk != null) _walk.Paused = true;
            if (_anim != null) { _animSpeed = _anim.speed; _anim.speed = 0f; }
            for (int i = 0; i < _round.Length; i++) _round[i] = 0;
            float H = AnimalHeight();
            _home = transform.localPosition;            // every mouse hides around here, so the fox never wanders off
            MakeProps(H);
            host.FloorTapped += OnFloor;

            for (int r = 0; r < rounds; r++)
            {
                host.SetRound("ROUND", $"{r + 1}/{rounds}");
                host.Hint("Listen…", "Tap the snow where you hear the mouse squeak.");
                Vector3 mouse = HidingSpot(host, H);
                _lump.transform.localPosition = mouse;
                _squeakSrc.transform.localPosition = mouse;
                _lump.SetActive(true);

                // squeak (and twitch the snow) until the player taps, or gives up waiting
                _tapped = false;
                Spot = mouse; Listening = true;
                float next = 0f;
                for (float t = 0f; !_tapped && t < 9f; t += Time.deltaTime)
                {
                    if (t >= next) { next = t + 1.3f; _squeakSrc.PlayOneShot(squeaks[Random.Range(0, squeaks.Length)], 1f); StartCoroutine(Twitch(H)); }
                    yield return null;
                }
                Listening = false;
                _lump.SetActive(false);
                if (!_tapped) { host.Popup("Too slow!", 0); yield return host.Wait(0.8f); continue; }

                yield return Pounce(_tap, H);
                float miss = Vector3.Distance(new Vector3(_tap.x, 0f, _tap.z), new Vector3(mouse.x, 0f, mouse.z)) / Mathf.Max(H, 0.05f);
                // distances in fox heights, so Small mode plays the same
                int pts = miss < 0.22f ? 100 : miss < 0.5f ? 60 : miss < 0.85f ? 25 : 0;
                _round[r] = pts;
                host.AddScore(pts);
                host.Popup(pts == 100 ? "+100 Perfect pounce!" : pts == 60 ? "+60 So close!" : pts == 25 ? "+25 Warm" : "Missed it", pts >= 100 ? 2 : pts > 0 ? 1 : 0);
                yield return ShowMouse(mouse, H, caught: pts >= 60);
                yield return host.Wait(0.4f);
            }
            host.FloorTapped -= OnFloor;
        }

        void OnFloor(Vector3 stagePoint) { if (!_tapped) { _tapped = true; _tap = stagePoint; } }

        /// <summary>Somewhere the player can see: in front of the fox, toward the phone, 0.6-1.6 fox heights away.</summary>
        Vector3 HidingSpot(MiniGameHost host, float H)
        {
            Vector3 home = _home;
            Vector3 toViewer = Vector3.forward;
            var v = host.Viewer();
            if (v != null && transform.parent != null)
            {
                toViewer = transform.parent.InverseTransformPoint(v.position) - home;
                toViewer.y = 0f;
                if (toViewer.sqrMagnitude < 1e-6f) toViewer = Vector3.forward;
                toViewer.Normalize();
            }
            float ang = Random.Range(-75f, 75f);
            Vector3 dir = Quaternion.Euler(0f, ang, 0f) * toViewer;
            Vector3 p = home + dir * Random.Range(0.6f, 1.6f) * H;
            p.y = home.y;
            return p;
        }

        IEnumerator Twitch(float H)
        {
            var t = _lump.transform;
            Vector3 rest = t.localPosition;
            for (float k = 0f; k < 0.35f; k += Time.deltaTime)
            {
                float b = Bump(k / 0.35f);
                t.localPosition = rest + Vector3.up * (0.025f * H * b);
                t.localScale = new Vector3(0.12f, 0.05f * (1f + b), 0.12f) * H;
                yield return null;
            }
            t.localPosition = rest;
        }

        /// <summary>Turn toward the spot, crouch, leap and land nose-first on it.</summary>
        IEnumerator Pounce(Vector3 spot, float H)
        {
            var spawn = Spawn;
            float faceYaw = spawn != null ? spawn.faceYaw : 0f;
            Vector3 start = transform.localPosition;
            Vector3 to = spot - start; to.y = 0f;
            float dist = to.magnitude;
            if (dist < 1e-4f) yield break;
            Vector3 dir = to / dist;
            // land with the nose, not the middle, on the spot (and never more than 2.5 heights at once)
            float body = 0.45f * H;
            Vector3 land = start + dir * Mathf.Clamp(dist - body, 0f, 2.5f * H);
            // however far away the tap, the fox keeps to its patch: never more than 2.2 heights from where it began
            Vector3 fromHome = land - _home; fromHome.y = 0f;
            if (fromHome.magnitude > 2.2f * H) land = _home + fromHome.normalized * (2.2f * H) + Vector3.up * (land.y - _home.y);
            Quaternion from = transform.localRotation;
            Quaternion face = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(0f, faceYaw, 0f);
            Vector3 right = Vector3.Cross(Vector3.up, dir);

            for (float t = 0f; t < 0.3f; t += Time.deltaTime)          // turn
            {
                transform.localRotation = Quaternion.Slerp(from, face, E(t / 0.3f));
                yield return null;
            }
            for (float t = 0f; t < 0.25f; t += Time.deltaTime)         // crouch, eyes on the spot
            {
                transform.localRotation = Quaternion.AngleAxis(-10f * E(t / 0.25f), right) * face;
                yield return null;
            }
            if (pounceSound != null) GetComponent<AudioSource>()?.PlayOneShot(pounceSound, 1f);
            float leap = 0.55f;
            for (float t = 0f; t < leap; t += Time.deltaTime)           // up, over, and down nose-first
            {
                float u = t / leap;
                Vector3 p = Vector3.Lerp(start, land, E(u)) + Vector3.up * (0.5f * H * Bump(u));
                transform.localPosition = p;
                transform.localRotation = Quaternion.AngleAxis(Mathf.Lerp(-25f, 55f, E(u)), right) * face;
                yield return null;
            }
            transform.localPosition = land;
            for (float t = 0f; t < 0.45f; t += Time.deltaTime)          // nose comes up out of the snow
            {
                transform.localRotation = Quaternion.AngleAxis(55f * (1f - E(t / 0.45f)), right) * face;
                yield return null;
            }
            transform.localRotation = face;
        }

        IEnumerator ShowMouse(Vector3 at, float H, bool caught)
        {
            _mouse.transform.localPosition = at;
            _mouse.SetActive(!caught);                 // a caught mouse stays under the fox's nose
            if (caught) { yield return null; yield break; }
            for (float t = 0f; t < 0.9f; t += Time.deltaTime)
            {
                float b = Bump(t / 0.9f);
                _mouse.transform.localPosition = at + Vector3.up * (0.03f * H * b);
                _mouse.transform.localScale = Vector3.one * (0.09f * H * Mathf.Min(1f, b * 2f));
                yield return null;
            }
            _mouse.SetActive(false);
        }

        void MakeProps(float H)
        {
            Transform stage = transform.parent;
            if (_lump == null)
            {
                _lump = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _lump.name = "MouseHunt Snow";
                Destroy(_lump.GetComponent<Collider>());
                _lump.GetComponent<Renderer>().sharedMaterial = snowMaterial;
                _lump.transform.SetParent(stage, false);
            }
            _lump.transform.localScale = new Vector3(0.12f, 0.05f, 0.12f) * H;
            _lump.SetActive(false);

            if (_mouse == null)
            {
                _mouse = new GameObject("MouseHunt Mouse");
                _mouse.transform.SetParent(stage, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(_mouse.transform, false);
                body.transform.localScale = new Vector3(0.8f, 0.6f, 1.1f);
                body.GetComponent<Renderer>().sharedMaterial = mouseMaterial;
                foreach (float x in new[] { -0.28f, 0.28f })
                {
                    var ear = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(ear.GetComponent<Collider>());
                    ear.transform.SetParent(_mouse.transform, false);
                    ear.transform.localPosition = new Vector3(x, 0.32f, 0.3f);
                    ear.transform.localScale = new Vector3(0.32f, 0.32f, 0.1f);
                    ear.GetComponent<Renderer>().sharedMaterial = mouseMaterial;
                }
            }
            _mouse.SetActive(false);

            if (_squeakSrc == null)
            {
                var go = new GameObject("MouseHunt Squeak");
                go.transform.SetParent(stage, false);
                _squeakSrc = go.AddComponent<AudioSource>();
                AnimalAudio.Configure(_squeakSrc);       // full volume within 1.5 m, like the animals' voices
            }
        }

        public override void Cleanup(MiniGameHost host)
        {
            host.FloorTapped -= OnFloor;
            StopAllCoroutines();
            if (_lump != null) Destroy(_lump);
            if (_mouse != null) Destroy(_mouse);
            if (_squeakSrc != null) Destroy(_squeakSrc.gameObject);
            _lump = _mouse = null; _squeakSrc = null;
            if (_anim != null) _anim.speed = _animSpeed > 0f ? _animSpeed : 1f;
            if (_walk != null) { _walk.Paused = false; _walk.ResumeAfterExternalMove(); }
        }
    }
}
