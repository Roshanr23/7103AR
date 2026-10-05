using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AR7103.App;
using AR7103.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Headless check of the open-palm reactions: opens each animal scene, spawns
    /// the animal in front of a stand-in phone, and steps it frame by frame (the
    /// clip sampled by hand, as the Animator would play it) through: routine,
    /// palm up at <see cref="PalmUpAt"/>, palm down at <see cref="PalmDownAt"/>,
    /// back to routine. Logs where it goes and renders the phone's view and a side view.
    ///
    ///   Unity -batchmode -quit -projectPath . -executeMethod AR7103.EditorTools.PalmPreview.Run
    /// APPUI_CAPTURE_DIR picks the output folder; PALM_ONLY=fox (etc.) runs one.
    /// </summary>
    public static class PalmPreview
    {
        const float Dt = 1f / 30f, PalmUpAt = 4f, PalmDownAt = 12f, End = 19f;
        static readonly float[] Shots = { 3.9f, 4.7f, 5.6f, 6.6f, 8.5f, 11.8f, 13.2f, 15f, 18.9f };

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            string only = Environment.GetEnvironmentVariable("PALM_ONLY");
            Directory.CreateDirectory(outDir);
            foreach (var (scene, key) in new[] { (AppScenes.Squirrel, "squirrel"), (AppScenes.Fox, "fox"),
                                                 (AppScenes.Deer, "deer"), (AppScenes.Hare, "hare") })
            {
                if (!string.IsNullOrEmpty(only) && only != key) continue;
                try { RunOne(scene, key, outDir); }
                catch (Exception e) { Debug.LogError($"[Palm] {key}: {e}"); }
            }
        }

        static void RunOne(string sceneName, string key, string outDir)
        {
            EditorSceneManager.OpenScene($"Assets/Scenes/{sceneName}.unity", OpenSceneMode.Single);
            var stage = GameObject.Find("Ground Plane Stage").transform;
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
            // PALM_STAGE_SCALE=0.5 runs the whole thing in "Small" mode
            float stageScale = float.TryParse(Environment.GetEnvironmentVariable("PALM_STAGE_SCALE"), out float ss) ? ss : 1f;
            stage.localScale = Vector3.one * stageScale;
            var spawn = stage.GetComponentInChildren<GroundSpawn>(true);
            var palm = spawn.GetComponent<PalmReaction>();
            if (palm == null) throw new Exception("no PalmReaction on " + spawn.name);

            var light = new GameObject("CapSun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -40f, 0f);
            RenderSettings.ambientLight = new Color(0.6f, 0.62f, 0.66f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(0f, -0.002f, 0f);
            floor.GetComponent<Renderer>().sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.62f, 0.6f, 0.57f) };

            bool big = key == "deer";
            // the phone, held at eye height in front of the spot
            var phone = NewCam("Phone", 60f);
            phone.transform.position = big ? new Vector3(0.4f, 1.5f, 2.6f) : new Vector3(0.3f, 1.25f, 1.9f);
            var side = NewCam("Side", 45f);
            side.transform.position = big ? new Vector3(3.6f, 1.5f, 1.3f) : new Vector3(2.5f, 1.0f, 0.95f);
            side.transform.LookAt(big ? new Vector3(0f, 0.6f, 0.9f) : new Vector3(0f, 0.15f, 0.75f));
            // the viewer is drawn as a small marker so the side view shows where the phone is
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.transform.localScale = new Vector3(0.08f, 0.15f, 0.01f);
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());

            spawn.Spawn(stage, phone, instant: true);
            spawn.enabled = false;
            Debug.Log($"[Palm] {key} stage x{stageScale}: animal {spawn.MeshBounds().size.y:F2} m tall in the world");
            if (big) phone.transform.position = new Vector3(2.0f, 1.5f, 1.4f);   // walk round to its side: the deer should turn
            palm.viewerOverride = phone.transform;
            palm.ResetState();

            var anim = spawn.GetComponentInChildren<Animator>();
            AnimationClip clip = anim != null && anim.runtimeAnimatorController != null
                ? anim.runtimeAnimatorController.animationClips.FirstOrDefault() : null;
            var hop = palm as HareHop;
            var fur = spawn.GetComponentsInChildren<ShellFur>(true);

            float clipT = 0f, minDist = float.MaxValue, nextLog = 0f;
            Vector3 home = spawn.transform.localPosition;
            int shot = 0;
            var rt = new RenderTexture(480, 480, 24);
            var frames = new System.Collections.Generic.List<string>();
            float now = 0f;
            System.Action<AnimalAudio, AudioClip> heard = (src, c) => Debug.Log($"[Palm] {key} t={now,5:F1} SOUND {c.name}");
            AnimalAudio.Played += heard;
            for (float t = 0f; t <= End + 1e-4f; t += Dt)
            {
                if (Mathf.Abs(t - PalmUpAt) < Dt * 0.5f) palm.PalmUp();
                if (Mathf.Abs(t - PalmDownAt) < Dt * 0.5f) palm.PalmDown();

                now = t;
                palm.Tick(Dt);
                if (hop != null && hop.previewJump >= 0f) { clipT = hop.previewJump * clip.length; hop.previewJump = -1f; }
                if (clip != null)
                {
                    clipT += Dt * anim.speed;
                    clip.SampleAnimation(spawn.gameObject, Mathf.Repeat(clipT, clip.length));
                    if (hop != null) hop.previewNormalizedTime = clipT / clip.length;
                }
                palm.LateTick(Dt);

                Vector3 p = spawn.transform.localPosition;
                Vector3 ph = phone.transform.position; ph.y = p.y;
                float dist = Vector3.Distance(p, ph);
                if (t > PalmUpAt) minDist = Mathf.Min(minDist, dist);
                if (t >= nextLog)
                {
                    nextLog += 0.5f;
                    Debug.Log($"[Palm] {key} t={t,5:F1} {StateOf(palm),-9} pos=({p.x:F2},{p.z:F2}) to phone {dist:F2} m" +
                              (anim != null ? $" anim x{anim.speed:F2}" : ""));
                }

                if (shot < Shots.Length && t >= Shots[shot] - 1e-4f)
                {
                    foreach (var f in fur) f.Rebuild();
                    marker.transform.position = phone.transform.position;
                    marker.transform.rotation = phone.transform.rotation;
                    var b = spawn.MeshBounds();
                    phone.transform.LookAt(b.center);
                    // zoom both cameras onto the animal (the phone stays where it is)
                    float size = Mathf.Max(b.size.x, b.size.y, b.size.z);
                    phone.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(size * 0.9f / Vector3.Distance(phone.transform.position, b.center)) * Mathf.Rad2Deg, 8f, 60f);
                    side.transform.LookAt(b.center);
                    side.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(size * 1.3f / Vector3.Distance(side.transform.position, b.center)) * Mathf.Rad2Deg, 8f, 60f);
                    marker.SetActive(false);
                    string a = Path.Combine(outDir, $"palm_{key}_{shot}_phone.png");
                    Render(phone, rt, a);
                    marker.SetActive(true);
                    string s = Path.Combine(outDir, $"palm_{key}_{shot}_side.png");
                    Render(side, rt, s);
                    frames.Add($"{t:F1}s {StateOf(palm)}");
                    shot++;
                }
            }
            AnimalAudio.Played -= heard;
            Vector3 end = spawn.transform.localPosition;
            Debug.Log($"[Palm] {key} SUMMARY closest to phone {minDist:F2} m; ended {Vector3.Distance(end, home):F2} m from where it appeared, " +
                      $"{(new Vector2(end.x, end.z)).magnitude:F2} m from the spot; final state {StateOf(palm)}");
            File.WriteAllLines(Path.Combine(outDir, $"palm_{key}_frames.txt"), frames);
        }

        static string StateOf(PalmReaction p) => p switch
        {
            FoxWalk f => f.State.ToString(),
            HareHop h => h.State + (h.Airborne ? "^" : ""),
            SquirrelPalm s => s.State.ToString(),
            DeerPalm d => $"a{d.Alert:F1}/s{d.Sniff:F1}",
            _ => "?"
        };

        static Camera NewCam(string name, float fov)
        {
            var c = new GameObject(name).AddComponent<Camera>();
            c.fieldOfView = fov;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0.8f, 0.82f, 0.85f);
            c.enabled = false;
            return c;
        }

        static void Render(Camera cam, RenderTexture rt, string path)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            cam.targetTexture = null;
        }
    }
}

namespace AR7103.EditorTools
{
    /// <summary>
    /// Each animal on a flat unlit "camera feed" floor, grounding off vs on (shadow
    /// catcher + contact shadow), seen from a phone held at eye height.
    ///   Unity -batchmode -quit -projectPath . -executeMethod AR7103.EditorTools.GroundingPreview.Run
    /// </summary>
    public static class GroundingPreview
    {
        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            System.IO.Directory.CreateDirectory(outDir);
            foreach (var (scene, key) in new[] { (AppScenes.Squirrel, "squirrel"), (AppScenes.Fox, "fox"), (AppScenes.Deer, "deer"),
                                                 (AppScenes.Hare, "hare"), (AppScenes.Owl, "owl") })
            {
                EditorSceneManager.OpenScene($"Assets/Scenes/{scene}.unity", OpenSceneMode.Single);
                var stage = GameObject.Find("Ground Plane Stage").transform;
                stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
                var spawn = stage.GetComponentInChildren<GroundSpawn>(true);

                // the "camera image" of a wooden floor: unlit, so only the catcher can darken it
                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.transform.position = new Vector3(0f, -0.004f, 0f);
                var fm = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                fm.SetColor("_BaseColor", new Color(0.58f, 0.47f, 0.38f));
                floor.GetComponent<Renderer>().sharedMaterial = fm;

                bool big = key == "deer";
                var cam = new GameObject("Phone").AddComponent<Camera>();
                cam.transform.position = big ? new Vector3(0.6f, 1.45f, 3.0f) : new Vector3(0.35f, 1.2f, 1.7f);
                cam.fieldOfView = 42f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.58f, 0.47f, 0.38f);
                cam.enabled = false;
                spawn.Spawn(stage, cam, instant: true);
                spawn.enabled = false;
                foreach (var est in UnityEngine.Object.FindObjectsByType<CameraLightEstimator>(FindObjectsSortMode.None)) est.ApplyTypical();
                foreach (var f in spawn.GetComponentsInChildren<ShellFur>(true)) f.Rebuild();

                var contact = spawn.GetComponent<ContactShadow>();
                contact.Measure();
                contact.Follow();
                var b = spawn.MeshBounds();
                cam.transform.LookAt(b.center - Vector3.up * b.size.y * 0.15f);
                float size = Mathf.Max(b.size.x, b.size.y, b.size.z);
                cam.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(size * 1.1f / Vector3.Distance(cam.transform.position, b.center)) * Mathf.Rad2Deg, 10f, 60f);

                var catcher = stage.Find("ShadowCatcher").gameObject;
                var patch = GameObject.Find(spawn.name + " ContactShadow");
                Debug.Log(patch == null ? $"[Ground] {key}: NO PATCH" :
                    $"[Ground] {key}: patch at {patch.transform.position} scale {patch.transform.lossyScale} active {patch.activeInHierarchy} " +
                    $"renderer {patch.GetComponent<Renderer>().enabled} mat {patch.GetComponent<Renderer>().sharedMaterial?.shader.name} " +
                    $"animal min.y {b.min.y:F3} centre {b.center}");
                var rt = new RenderTexture(560, 560, 24);
                foreach (var (label, on, contactOn) in new[] { ("off", false, false), ("contact", false, true), ("on", true, true) })
                {
                    catcher.SetActive(on);
                    if (patch != null) patch.SetActive(contactOn);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(560, 560, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 560, 560), 0, 0); tex.Apply();
                    RenderTexture.active = null;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"ground_{key}_{label}.png"), tex.EncodeToPNG());
                }
                Debug.Log($"[Ground] {key}: rendered; light {UnityEngine.Object.FindFirstObjectByType<Light>().intensity:F2}, ambient {RenderSettings.ambientLight}");
            }
        }
    }
}
