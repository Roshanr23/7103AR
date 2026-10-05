using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AR7103.UI
{
    /// <summary>
    /// Arctic hare minigame, "Snow Hide" -- white on white. The hare hops under
    /// one of three snow mounds, the mounds shuffle (more swaps, faster, every
    /// round), and you tap the one you think it is under. Right: it pops out with
    /// a binky. Five rounds; a streak bonus for finding it in a row.
    /// </summary>
    public class HareSnowHide : MiniGame
    {
        public override string Title => "Snow Hide";
        public override string Key => "hare";
        public override string HowTo => "Watch which snow mound the hare hides under. They'll shuffle. Then tap the right one!";
        public override string Length => "5 rounds";

        public int rounds = 5;
        public Material snowMaterial;
        public AudioClip burrowSound, poofSound;

        HareHop _hare;
        AnimalTricks _tricks;
        ContactShadow _shadow;
        Renderer[] _renderers;
        readonly List<Transform> _mounds = new List<Transform>();
        int _holder = -1, _picked = -1, _finds, _streak;
        bool _arrived;
        AudioSource _sfx;

        public int Finds => _finds;
        public int Holder => _holder;
        /// <summary>Waiting for the player to pick a mound (tests).</summary>
        public bool Choosing { get; private set; }
        public IReadOnlyList<Transform> Mounds => _mounds;

        public override int Stars(int s) => _finds >= 5 ? 3 : _finds >= 3 ? 2 : _finds >= 1 ? 1 : 0;
        public override string Verdict(int s, int stars) => stars >= 3 ? "Eagle eyes!" : stars == 2 ? "Good spotting!" : stars == 1 ? "Tricky, isn't it?" : "Perfect camouflage";
        public override string Detail(MiniGameHost h) => $"Found {_finds} of {rounds}";

        public override IEnumerator Run(MiniGameHost host)
        {
            _hare = GetComponent<HareHop>();
            _tricks = GetComponent<AnimalTricks>();
            _shadow = GetComponent<ContactShadow>();
            _sfx = GetComponent<AudioSource>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _finds = _streak = 0;
            float H = AnimalHeight();
            if (_hare != null) _hare.ForcedCall = true;            // keeps it from hopping off on its own
            BuildMounds(host, H);
            host.PieceTapped += OnPiece;

            for (int r = 0; r < rounds; r++)
            {
                host.SetRound("ROUND", $"{r + 1}/{rounds}");
                host.Hint("Watch the hare", "Which mound does it hide under?");
                yield return Grow(true, 0.35f);

                // the hare hops under one
                int k = Random.Range(0, _mounds.Count);
                _arrived = false;
                Vector3 under = _mounds[k].localPosition;
                if (_hare != null) _hare.HopTo(under, () => _arrived = true);
                for (float t = 0f; !_arrived && t < 7f; t += Time.deltaTime) yield return null;
                transform.localPosition = new Vector3(under.x, transform.localPosition.y, under.z);
                _holder = k;
                Hide(true);
                if (burrowSound != null && _sfx != null) _sfx.PlayOneShot(burrowSound, 0.9f);
                yield return Puff(_mounds[k], 0.3f);
                yield return host.Wait(0.4f);

                // shuffle
                host.Hint("Shuffling…", "Keep your eye on it!");
                int swaps = 3 + r;
                float dur = Mathf.Max(0.32f, 0.62f - 0.07f * r);
                for (int s = 0; s < swaps; s++)
                {
                    int i = Random.Range(0, _mounds.Count), j = (i + Random.Range(1, _mounds.Count)) % _mounds.Count;
                    yield return Swap(i, j, dur, H);
                }

                // pick
                host.Hint("Which one?", "Tap the mound the hare is under.");
                _picked = -1;
                Choosing = true;
                for (float t = 0f; _picked < 0 && t < 20f; t += Time.deltaTime) yield return null;
                Choosing = false;
                if (_picked < 0) _picked = (_holder + 1) % _mounds.Count;     // timed out: counts as a wrong guess

                bool right = _picked == _holder;
                yield return Puff(_mounds[_picked], 0.25f);
                if (right)
                {
                    _finds++; _streak++;
                    int pts = 100 + 20 * (_streak - 1);
                    host.AddScore(pts);
                    host.Popup(_streak > 1 ? $"+{pts} Found it! x{_streak}" : $"+{pts} Found it!", 2);
                }
                else
                {
                    _streak = 0;
                    host.Popup("Not there!", 0);
                    yield return host.Wait(0.5f);
                    yield return Puff(_mounds[_holder], 0.25f);
                }
                yield return Grow(false, 0.3f, _holder);
                Hide(false);
                if (poofSound != null && _sfx != null) _sfx.PlayOneShot(poofSound, 0.7f);
                if (_tricks != null) _tricks.Perform(AnimalTricks.Trick.Special);   // a binky out of the snow
                yield return host.Wait(1.3f);
            }
            host.PieceTapped -= OnPiece;
        }

        void OnPiece(GameTapTarget p)
        {
            if (Choosing && _picked < 0) _picked = p.id;
        }

        /// <summary>For tests: pick a mound as if tapped.</summary>
        public void Pick(int id) { if (Choosing && _picked < 0) _picked = id; }

        void Hide(bool hidden)
        {
            // forceRenderingOff, not enabled: Vuforia flips 'enabled' itself as floor tracking comes
            // and goes, which would pop a hidden hare back into view (or hold a found one invisible)
            if (_renderers != null) foreach (var r in _renderers) if (r != null) r.forceRenderingOff = hidden;
            if (_shadow != null) _shadow.enabled = !hidden;
        }

        IEnumerator Swap(int i, int j, float dur, float H)
        {
            Transform a = _mounds[i], b = _mounds[j];
            Vector3 pa = a.localPosition, pb = b.localPosition;
            Vector3 mid = (pa + pb) * 0.5f;
            Vector3 side = Vector3.Cross(Vector3.up, (pb - pa).normalized) * (0.45f * H);
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float u = E(t / dur), arc = Bump(t / dur);
                a.localPosition = Vector3.Lerp(pa, pb, u) + side * arc;
                b.localPosition = Vector3.Lerp(pb, pa, u) - side * arc;
                FollowHolder();
                yield return null;
            }
            a.localPosition = pb; b.localPosition = pa;
            _mounds[i] = b; _mounds[j] = a;                 // slots keep their order left to right
            if (_holder == i) _holder = j; else if (_holder == j) _holder = i;
            for (int m = 0; m < _mounds.Count; m++) _mounds[m].GetComponent<GameTapTarget>().id = m;
            FollowHolder();
        }

        void FollowHolder()
        {
            if (_holder < 0) return;
            Vector3 p = _mounds[_holder].localPosition;
            transform.localPosition = new Vector3(p.x, transform.localPosition.y, p.z);
        }

        IEnumerator Puff(Transform m, float dur)
        {
            Vector3 s0 = m.localScale;
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float b = Bump(t / dur);
                m.localScale = new Vector3(s0.x * (1f + 0.12f * b), s0.y * (1f + 0.25f * b), s0.z * (1f + 0.12f * b));
                yield return null;
            }
            m.localScale = s0;
        }

        /// <summary>Mounds rise out of the floor (or sink back); <paramref name="except"/> = one that stays.</summary>
        IEnumerator Grow(bool up, float dur, int except = -1)
        {
            if (!up)
            {
                // only the revealed mound sinks; the rest stay for the next round
                if (except < 0) yield break;
                var m = _mounds[except];
                Vector3 s0 = m.localScale;
                for (float t = 0f; t < dur; t += Time.deltaTime) { m.localScale = new Vector3(s0.x, s0.y * (1f - E(t / dur)), s0.z); yield return null; }
                m.localScale = new Vector3(s0.x, 0.001f, s0.z);
                _sunk = except;
                yield break;
            }
            if (_sunk >= 0)
            {
                var m = _mounds[_sunk];
                Vector3 s0 = new Vector3(m.localScale.x, _mountHeight, m.localScale.z);
                for (float t = 0f; t < dur; t += Time.deltaTime) { m.localScale = new Vector3(s0.x, s0.y * E(t / dur), s0.z); yield return null; }
                m.localScale = s0;
                _sunk = -1;
            }
        }

        int _sunk = -1;
        float _mountHeight;

        void BuildMounds(MiniGameHost host, float H)
        {
            foreach (var m in _mounds) if (m != null) Destroy(m.gameObject);
            _mounds.Clear();
            _sunk = -1;
            Vector3 home = transform.localPosition;
            Vector3 toViewer = Vector3.forward;
            var v = host.Viewer();
            if (v != null && transform.parent != null)
            {
                toViewer = transform.parent.InverseTransformPoint(v.position) - home; toViewer.y = 0f;
                toViewer = toViewer.sqrMagnitude > 1e-6f ? toViewer.normalized : Vector3.forward;
            }
            Vector3 across = Vector3.Cross(Vector3.up, toViewer);
            Vector3 centre = home + toViewer * (0.7f * H);
            var mesh = Dome();
            _mountHeight = 0.55f * H;
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject($"SnowMound{i}");
                go.transform.SetParent(transform.parent, false);
                go.transform.localPosition = centre + across * ((i - 1) * 1.25f * H) + Vector3.up * (home.y);
                go.transform.localScale = new Vector3(1.05f * H, _mountHeight, 1.05f * H);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = snowMaterial;
                var col = go.AddComponent<SphereCollider>();
                col.isTrigger = true; col.radius = 0.6f; col.center = new Vector3(0f, 0.3f, 0f);
                go.AddComponent<GameTapTarget>().id = i;
                _mounds.Add(go.transform);
            }
        }

        static Mesh _dome;
        /// <summary>A unit snow dome: radius 0.5, height 1, flat on y = 0, a little lumpy.</summary>
        static Mesh Dome()
        {
            if (_dome != null) return _dome;
            const int rings = 10, segs = 40;
            var verts = new List<Vector3>(); var tris = new List<int>();
            verts.Add(new Vector3(0f, 1f, 0f));
            for (int r = 1; r <= rings; r++)
            {
                float a = r / (float)rings * Mathf.PI * 0.5f;
                for (int s = 0; s < segs; s++)
                {
                    float ang = s / (float)segs * Mathf.PI * 2f;
                    float lump = 1f + 0.05f * Mathf.Sin(ang * 3f + r) * Mathf.Sin(a * 2f);
                    verts.Add(new Vector3(Mathf.Cos(ang) * Mathf.Sin(a) * 0.5f * lump, Mathf.Cos(a), Mathf.Sin(ang) * Mathf.Sin(a) * 0.5f * lump));
                }
            }
            for (int s = 0; s < segs; s++) { tris.Add(0); tris.Add(1 + (s + 1) % segs); tris.Add(1 + s); }
            for (int r = 1; r < rings; r++)
                for (int s = 0; s < segs; s++)
                {
                    int a0 = 1 + (r - 1) * segs + s, a1 = 1 + (r - 1) * segs + (s + 1) % segs;
                    int b0 = 1 + r * segs + s, b1 = 1 + r * segs + (s + 1) % segs;
                    tris.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
                }
            _dome = new Mesh { name = "SnowDome" };
            _dome.SetVertices(verts); _dome.SetTriangles(tris, 0);
            _dome.RecalculateNormals(); _dome.RecalculateBounds();
            return _dome;
        }

        public override void Cleanup(MiniGameHost host)
        {
            host.PieceTapped -= OnPiece;
            StopAllCoroutines();
            Choosing = false;
            foreach (var m in _mounds) if (m != null) Destroy(m.gameObject);
            _mounds.Clear();
            Hide(false);
            if (_hare != null) { _hare.ForcedCall = null; _hare.ResumeAfterExternalMove(); }
        }
    }
}
