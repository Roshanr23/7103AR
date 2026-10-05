using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Red squirrel minigame, "Nut Stash" (red squirrels cache food for the
    /// winter). For 30 seconds acorns drop onto the floor around the squirrel;
    /// tap one and the squirrel scurries to it and buries it. Each acorn sinks
    /// into the snow after a few seconds, so pick the ones it can reach in time.
    /// </summary>
    public class SquirrelNutStash : MiniGame
    {
        public override string Title => "Nut Stash";
        public override string Key => "squirrel";
        public override string HowTo => "Acorns are dropping! Tap one to send the squirrel to bury it before it sinks into the snow.";
        public override string Length => "30 seconds";
        public override string Unit => "acorns";

        public float seconds = 30f;
        public float acornLife = 4.5f;
        public int maxOnFloor = 4;
        public Material acornMaterial, capMaterial;
        public AudioClip dropSound, digSound, sinkSound;

        class Acorn { public GameObject go; public float born; public Vector3 at; public bool taken; }
        readonly List<Acorn> _acorns = new List<Acorn>();
        SquirrelPalm _squirrel;
        Acorn _target;
        int _dropped, _sunk;
        AudioSource _sfx;
        Vector3 _home;
        /// <summary>Where the squirrel sat when the game began (tests).</summary>
        public Vector3 Home => _home;

        /// <summary>Acorns still on the floor, not yet buried or sunk (tests).</summary>
        public System.Collections.Generic.IEnumerable<Transform> LiveAcorns()
        {
            foreach (var a in _acorns) if (!a.taken && a.go != null) yield return a.go.transform;
        }
        public bool SquirrelBusy => _squirrel != null && _squirrel.OnErrand;

        public override int Stars(int s) => s >= 8 ? 3 : s >= 5 ? 2 : s >= 2 ? 1 : 0;
        public override string Verdict(int s, int stars) => stars >= 3 ? "Winter sorted!" : stars == 2 ? "A fine stash" : stars == 1 ? "A start" : "The snow got them";
        public override string Detail(MiniGameHost h) => $"{h.Score} buried  ·  {_sunk} sank";

        public override IEnumerator Run(MiniGameHost host)
        {
            _squirrel = GetComponent<SquirrelPalm>();
            _sfx = GetComponent<AudioSource>();
            _acorns.Clear(); _target = null; _dropped = _sunk = 0;
            float H = AnimalHeight();
            _home = transform.localPosition;            // acorns land around here, so the squirrel never wanders off
            host.PieceTapped += OnPiece;
            host.Hint("Tap an acorn", "The squirrel buries each one you send it to.");

            float nextDrop = 0f;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                host.SetTimer(seconds - t);
                if (t >= nextDrop && Live() < maxOnFloor)
                {
                    nextDrop = t + Random.Range(0.9f, 1.6f);
                    StartCoroutine(Drop(H, host));
                }
                // acorns left too long sink away
                for (int i = _acorns.Count - 1; i >= 0; i--)
                {
                    var a = _acorns[i];
                    if (a.taken || a.go == null) continue;
                    if (Time.time - a.born > acornLife) { a.taken = true; _sunk++; StartCoroutine(Sink(a, H, buried: false)); if (_target == a) _target = null; }
                }
                yield return null;
            }
            host.PieceTapped -= OnPiece;
            if (_squirrel != null) _squirrel.ScurryTo(null);
        }

        int Live() { int n = 0; foreach (var a in _acorns) if (!a.taken && a.go != null) n++; return n; }

        void OnPiece(GameTapTarget piece)
        {
            var a = _acorns.Find(x => x.go == piece.gameObject);
            if (a == null || a.taken || _squirrel == null) return;
            _target = a;
            var host = FindFirstObjectByType<MiniGameHost>();
            _squirrel.ScurryTo(a.at, () =>
            {
                if (a.taken || a.go == null) return;          // it sank on the way
                a.taken = true;
                if (host != null) { host.AddScore(1); host.Popup("+1 Stashed!", 2); }
                if (digSound != null && _sfx != null) _sfx.PlayOneShot(digSound, 0.9f);
                StartCoroutine(Sink(a, AnimalHeight(), buried: true));
                if (_target == a) _target = null;
            });
        }

        IEnumerator Drop(float H, MiniGameHost host)
        {
            Vector3 home = _home;
            Vector3 toViewer = Vector3.forward;
            var v = host.Viewer();
            if (v != null && transform.parent != null)
            {
                toViewer = transform.parent.InverseTransformPoint(v.position) - home; toViewer.y = 0f;
                toViewer = toViewer.sqrMagnitude > 1e-6f ? toViewer.normalized : Vector3.forward;
            }
            // in the half of the floor between the squirrel and the player, 1-3 squirrel heights out
            Vector3 dir = Quaternion.Euler(0f, Random.Range(-80f, 80f), 0f) * toViewer;
            Vector3 at = home + dir * Random.Range(1.0f, 3.0f) * H;
            at.y = home.y;
            var a = new Acorn { go = MakeAcorn(H), born = Time.time, at = at };
            _acorns.Add(a);
            _dropped++;
            var tr = a.go.transform;
            float size = 0.16f * H;
            for (float t = 0f; t < 0.45f; t += Time.deltaTime)              // fall and bounce once
            {
                float u = t / 0.45f;
                float y = u < 0.7f ? (1f - (u / 0.7f) * (u / 0.7f)) * 1.2f * H : 0.15f * H * Bump((u - 0.7f) / 0.3f);
                if (tr == null) yield break;
                tr.localPosition = at + Vector3.up * (y + size * 0.5f);
                tr.localRotation = Quaternion.Euler(0f, u * 200f, 0f);
                yield return null;
            }
            if (tr == null) yield break;
            tr.localPosition = at + Vector3.up * (size * 0.5f);
            if (dropSound != null && _sfx != null) _sfx.PlayOneShot(dropSound, 0.8f);
        }

        IEnumerator Sink(Acorn a, float H, bool buried)
        {
            if (!buried && sinkSound != null && _sfx != null) _sfx.PlayOneShot(sinkSound, 0.6f);
            var tr = a.go != null ? a.go.transform : null;
            if (tr == null) yield break;
            Vector3 s0 = tr.localScale, p0 = tr.localPosition;
            for (float t = 0f; t < 0.4f; t += Time.deltaTime)
            {
                if (tr == null) yield break;
                float k = 1f - E(t / 0.4f);
                tr.localScale = s0 * k;
                tr.localPosition = p0 - Vector3.up * (0.08f * H * (1f - k));
                yield return null;
            }
            if (a.go != null) Destroy(a.go);
        }

        GameObject MakeAcorn(float H)
        {
            var root = new GameObject("Acorn");
            root.transform.SetParent(transform.parent, false);
            float size = 0.16f * H;                                 // ~5 cm for a 30 cm squirrel
            root.transform.localScale = Vector3.one * size;
            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            body.GetComponent<Renderer>().sharedMaterial = acornMaterial;
            var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(cap.GetComponent<Collider>());
            cap.transform.SetParent(root.transform, false);
            cap.transform.localPosition = new Vector3(0f, 0.32f, 0f);
            cap.transform.localScale = new Vector3(0.9f, 0.45f, 0.9f);
            cap.GetComponent<Renderer>().sharedMaterial = capMaterial;
            // a generous invisible target: acorns are small and fingers are not
            var hit = root.AddComponent<SphereCollider>();
            hit.isTrigger = true;
            hit.radius = 1.6f;
            root.AddComponent<GameTapTarget>();
            return root;
        }

        public override void Cleanup(MiniGameHost host)
        {
            host.PieceTapped -= OnPiece;
            StopAllCoroutines();
            foreach (var a in _acorns) if (a.go != null) Destroy(a.go);
            _acorns.Clear();
            if (_squirrel != null) { _squirrel.ScurryTo(null); _squirrel.ResumeAfterExternalMove(); }
        }
    }
}
