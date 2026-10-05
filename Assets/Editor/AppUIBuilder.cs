using System;
using System.IO;
using System.Linq;
using AR7103.App;
using AR7103.Hands;
using UnityEditor.Events;
using UnityEditor.Animations;
using AR7103.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Vuforia;
// Vuforia also defines an Image (its camera-frame type); the UI one is meant here.
using Image = UnityEngine.UI.Image;

namespace AR7103.EditorTools
{
    /// <summary>
    /// Builds the entry scene and the ground-scan overlay from code.
    ///
    ///   Menu:  7103AR > Build App UI
    ///   CLI:   Unity -batchmode -projectPath . -executeMethod AR7103.EditorTools.AppUIBuilder.BuildAll -quit
    ///
    /// Everything is regenerated on each run: the sprites, EntryScene, and the
    /// ScanUI inside SampleScene. Nothing else in SampleScene is touched.
    /// </summary>
    public static class AppUIBuilder
    {
        // ---------------------------------------------------------------- tokens
        // Winter field guide: a cool conifer-night ground, snow-white text, and a
        // single warm accent taken from the fox and squirrel fur.
        static readonly Color Ground   = Hex("0C1215");
        static readonly Color Snow     = Hex("F1F5F6");
        static readonly Color Lichen   = Hex("9DB0B6");
        static readonly Color Rust     = Hex("E8793A");
        static readonly Color RustInk  = Hex("1E1008");   // text on the accent
        static readonly Color Night    = Hex("06090A");
        static readonly Color Success  = Hex("5FD18B");

        const float RefW = 1179f, RefH = 2556f;          // iPhone 15 Pro, portrait
        const float Margin = 64f;

        const string GenDir    = "Assets/UI/Generated";
        const string AnimalDir = "Assets/UI/Animals";
        const string EntryPath = "Assets/Scenes/EntryScene.unity";
        // SampleScene is the SOURCE the per-animal scenes are split from. It stays
        // on disk but is no longer in the build.
        const string ARPath    = "Assets/Scenes/SampleScene.unity";
        const string ScanPath  = "Assets/Scenes/GroundScan.unity";
        static string ScenePath(string sceneName) => $"Assets/Scenes/{sceneName}.unity";
        const string FontPath  = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        struct Species
        {
            public string file, common, latin, arScene, target; public float focusX;
            public bool inAR => !string.IsNullOrEmpty(arScene);
            public Species(string f, string c, string l, float fx, string scene = "", string tgt = "")
            { file = f; common = c; latin = l; focusX = fx; arScene = scene; target = tgt; }
        }

        // Order matches the photos supplied. inAR marks the three that have 3D
        // models on image targets in SampleScene.
        static readonly Species[] Animals =
        {
            new Species("squirrel", "Red Squirrel",      "Tamiasciurus hudsonicus", 0.72f, AppScenes.Squirrel, "ImageTargetSquirrel"),
            new Species("fox",      "Red Fox",           "Vulpes vulpes",           0.42f, AppScenes.Fox,      "ImageTargetFox"),
            new Species("deer",     "White-tailed Deer", "Odocoileus virginianus",  0.50f, AppScenes.Deer,     "ImageTargetDeer"),
            new Species("hare",     "Arctic Hare",       "Lepus arcticus",          0.45f, AppScenes.Hare,     "ImageTargetHare"),
            new Species("owl",      "Snowy Owl",         "Bubo scandiacus",         0.55f, AppScenes.Owl,      "ImageTargetOwl"),
        };

        static TMP_FontAsset _font;
        static Sprite _round, _stroke, _shadow, _gradUp, _circle, _ringThin, _ringThick, _blob;
        static Sprite _palmIcon, _soundOn, _soundOff, _closeIcon;
        static Sprite _fistIcon, _pinchIcon, _pointIcon, _peaceIcon, _starOn, _starOff, _playIcon;

        // ================================================================ entry
        [MenuItem("7103AR/Build App UI")]
        public static void BuildAll()
        {
            Prepare();
            ConfigureAudioImports();
            SplitIntoScenes();
            foreach (var a in Animals.Where(a => a.inAR)) BuildAnimalScene(a);
            BuildEntryScene();
            SetBuildScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[AppUI] Built EntryScene and the per-animal scenes; set build order.");
        }

        // --------------------------------------------------------- diagnostics
        /// <summary>Log how each animal sits on its image target: sizes in metres.</summary>
        public static void ReportPlacement()
        {
            foreach (var a in Animals.Where(a => a.inAR))
            {
                EditorSceneManager.OpenScene(ScenePath(a.arScene), OpenSceneMode.Single);
                var tgt = GameObject.Find(a.target);
                if (tgt == null) { Debug.Log($"[Report] {a.arScene}: no {a.target}"); continue; }
                var t = tgt.transform;
                Debug.Log($"[Report] {a.arScene} target pos={t.position} rot={t.eulerAngles} scale={t.localScale} lossy={t.lossyScale}");
                var ib = tgt.GetComponent<ImageTargetBehaviour>();
                if (ib != null) Debug.Log($"[Report]   target size (m) = {ib.GetSize()}");
                foreach (Transform child in t)
                {
                    var rs = child.GetComponentsInChildren<Renderer>(true);
                    Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds(child.position, Vector3.zero);
                    foreach (var r in rs) b.Encapsulate(r.bounds);
                    Debug.Log($"[Report]   child '{child.name}' localPos={child.localPosition} localRot={child.localEulerAngles} " +
                              $"localScale={child.localScale} lossy={child.lossyScale} worldBounds size={b.size} min.y={b.min.y:F4} active={child.gameObject.activeSelf}");
                }
            }
            EditorSceneManager.OpenScene(ARPath, OpenSceneMode.Single);
            var stage = GameObject.Find("Ground Plane Stage");
            if (stage != null)
            {
                var comps = string.Join(", ", stage.GetComponents<Component>().Select(c => c.GetType().Name));
                Debug.Log($"[Report] SampleScene Ground Plane Stage pos={stage.transform.position} rot={stage.transform.eulerAngles} scale={stage.transform.localScale} comps=[{comps}]");
            }
            var pf = GameObject.Find("Plane Finder");
            if (pf != null)
                Debug.Log($"[Report] Plane Finder children: {string.Join(", ", pf.transform.Cast<Transform>().Select(c => c.name))}");
        }

        /// <summary>Instantiate each model at identity and log its children and native bounds.</summary>
        public static void ReportModels()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var (label, path) in new[] {
                ("squirrel", "Assets/squirrel_export/SquirrelScene.fbx"),
                ("deer",     "Assets/deer_export/WhiteTailedDeer.fbx"),
                ("owl",      "Assets/owl_export/SnowyOwl.fbx") })
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) { Debug.Log($"[Model] {label}: missing {path}"); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                go.transform.localScale = Vector3.one;
                Debug.Log($"[Model] {label}: root rot={asset.transform.localEulerAngles} scale={asset.transform.localScale}  total={BoundsOf(go.transform).size}");
                foreach (Transform c in go.transform)
                {
                    var b = BoundsOf(c);
                    Debug.Log($"[Model]   {c.name,-18} size={b.size} min={b.min} renderers={c.GetComponentsInChildren<Renderer>(true).Length}");
                }
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static Bounds BoundsOf(Transform t)
        {
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(t.position, Vector3.zero);
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        // Metres tall on the floor, and the turn needed so each model's face points
        // at the camera (found by rendering, not assumed from the FBX axes).
        // Life size: a red squirrel sitting up with its tail raised, a red fox to the ear
        // tips, a young white-tailed doe (about 0.9 m at the shoulder), an arctic hare
        // sitting up, a standing snowy owl.
        static float SpawnHeight(string file) => file switch { "squirrel" => 0.30f, "deer" => 1.4f, "owl" => 0.6f, "fox" => 0.62f, "hare" => 0.45f, _ => 0.5f };
        // The fox walks toward -X in Blender, which the FBX conversion turns into +X,
        // so its nose is 90 deg off the +Z the others face. (Confirmed by render.)
        // The hare's nose sits ~33 deg off +Z (its Blender heading was -56.9 deg). Confirmed by render.
        static float SpawnYaw(string file) => file switch { "fox" => -90f, "hare" => 33f, _ => 0f };

        // Models that are not in SampleScene, and the Vuforia trackable their page uses
        const string FoxFbx = "Assets/fox_export/RedFox.fbx";
        const string FoxController = "Assets/fox_export/RedFox.controller";
        const string HareFbx = "Assets/hare_export/ArcticHare.fbx";
        const string HareController = "Assets/hare_export/ArcticHare.controller";
        const string HareGroundFbx = "Assets/hare_export/HareGround.fbx";
        const string HareTravel = "Assets/hare_export/hare_travel.json";
        static string ModelPath(string file) => file switch { "fox" => FoxFbx, "hare" => HareFbx, _ => null };
        static string Trackable(string file) => file switch { "fox" => "fox", "hare" => "hare", _ => null };
        static readonly string[] HiddenOnFloor = { "Environment", "Tree" };

        const float SquirrelTailLift = 155f;

        /// <summary>
        /// Adapt a model posed for its image target to standing on the floor.
        ///
        /// Hides the scenery it was built with (the squirrel's 70 m environment and
        /// 9 m tree, the owl's 28 m backdrop), which would engulf a room. The
        /// squirrel also needs its tail swung up: it was modelled on a branch with
        /// the tail hanging below, so on the floor the tail tip was the lowest point
        /// and the squirrel balanced on it. Rotating about the tail's base puts the
        /// feet on the floor and gives the classic tail-over-the-back pose.
        /// Call with the model at identity rotation and unit scale.
        /// </summary>
        static void AdaptForFloor(GameObject model, string file)
        {
            foreach (Transform c in model.transform)
                if (HiddenOnFloor.Contains(c.name)) c.gameObject.SetActive(false);

            if (file != "squirrel") return;
            var tail = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Squirrel_Tail");
            if (tail == null) { Debug.LogWarning("[AppUI] squirrel has no Squirrel_Tail to lift"); return; }
            if (tail.GetComponent<TailLifted>() != null) return;          // already done
            Bounds b = BoundsOf(tail);
            Vector3 root = new Vector3(b.center.x, b.max.y, b.center.z);  // hangs down, so its base is the top
            tail.RotateAround(root, model.transform.right, SquirrelTailLift);
            tail.gameObject.AddComponent<TailLifted>();
        }

        /// <summary>Render each animal spawned on a test floor, viewed from the front.</summary>
        public static void PreviewSpawn()
        {
            Prepare();       // imports/configures the fox and hare before they are instantiated
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            foreach (var (file, path) in new[] {
                ("squirrel", "Assets/squirrel_export/SquirrelScene.fbx"),
                ("deer",     "Assets/deer_export/WhiteTailedDeer.fbx"),
                ("owl",      "Assets/owl_export/SnowyOwl.fbx"),
                ("fox",      FoxFbx),
                ("hare",     HareFbx) })
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var light = new GameObject("Sun").AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.3f;
                light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
                RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);

                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.transform.localScale = Vector3.one * 0.5f;       // 5 m x 5 m
                floor.GetComponent<Renderer>().sharedMaterial.color = new Color(0.62f, 0.6f, 0.57f);

                var stage = new GameObject("Ground Plane Stage").transform;
                var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                model.transform.SetParent(stage, false);
                AdaptForFloor(model, file);
                var spawn = model.AddComponent<GroundSpawn>();
                spawn.targetHeight = SpawnHeight(file);
                spawn.faceYaw = SpawnYaw(file);

                float h = spawn.targetHeight;
                var cam = new GameObject("Cam").AddComponent<Camera>();
                cam.transform.position = new Vector3(0f, h * 0.75f, h * 2.6f);   // in front, on +Z
                cam.transform.LookAt(new Vector3(0f, h * 0.45f, 0f));
                cam.fieldOfView = 40f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.8f, 0.84f, 0.88f);

                spawn.Spawn(stage, cam, instant: true);
                var b = spawn.MeshBounds();
                Debug.Log($"[Spawn] {file}: height={b.size.y:F3}m min.y={b.min.y:F4} footprint={b.size.x:F2}x{b.size.z:F2}m");

                var rt = new RenderTexture(700, 700, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(700, 700, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0); tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, $"spawn_{file}.png"), tex.EncodeToPNG());
            }
        }

        /// <summary>Check wiring in each animal scene and run its spawn for real. Saves nothing.</summary>
        public static void VerifyAnimalScenes()
        {
            foreach (var a in Animals.Where(a => a.inAR))
            {
                var scene = EditorSceneManager.OpenScene(ScenePath(a.arScene), OpenSceneMode.Single);
                string tag = $"[Verify] {a.arScene}:";
                var pf = GameObject.Find("Plane Finder");
                var stage = GameObject.Find("Ground Plane Stage");
                var pos = pf.GetComponent<ContentPositioningBehaviour>();
                var finder = pf.GetComponent<PlaneFinderBehaviour>();
                var flow = UnityEngine.Object.FindFirstObjectByType<GroundScanFlow>();
                var spawn = stage.GetComponentInChildren<GroundSpawn>(true);

                bool anchorLocal = pos.AnchorStage != null && pos.AnchorStage.gameObject.scene == scene
                                   && pos.AnchorStage.gameObject == stage;
                int tapListeners = finder.OnInteractiveHitTest.GetPersistentEventCount();
                var tapTarget = tapListeners > 0 ? finder.OnInteractiveHitTest.GetPersistentTarget(0) : null;
                bool tapLocal = tapTarget is Component tc && tc.gameObject.scene == scene;
                Debug.Log($"{tag} stage ref is this scene's own = {anchorLocal}; tap-to-place listeners = {tapListeners}, target in-scene = {tapLocal}");
                var tgtGo = GameObject.Find(a.target);
                bool trigOk = flow.trigger != null && tgtGo != null && flow.trigger.gameObject == tgtGo
                              && tgtGo.scene == scene;
                Debug.Log($"{tag} page trigger is this scene's own {a.target} = {trigOk}; " +
                          $"nothing under the page = {tgtGo != null && tgtGo.transform.childCount == 0}; " +
                          $"model under the stage, not the page = {spawn != null && spawn.transform.parent == stage.transform}");
                Debug.Log($"{tag} spawn wired = {flow.spawn == spawn && spawn != null}; " +
                          $"model inactive until lock = {!spawn.gameObject.activeSelf}; ScanUI back = {flow.backButton != null}");

                // Spawn for real against a tilted, rotated stage and an off-axis viewer
                stage.transform.SetPositionAndRotation(new Vector3(0.4f, -1.2f, 2.1f), Quaternion.Euler(0f, 137f, 0f));
                var cam = new GameObject("TestCam").AddComponent<Camera>();
                cam.transform.position = new Vector3(-0.6f, 0.3f, -0.4f);
                spawn.Spawn(stage.transform, cam, instant: true);
                var b = spawn.MeshBounds();
                Vector3 toCam = Vector3.ProjectOnPlane(cam.transform.position - spawn.transform.position, Vector3.up).normalized;
                // the model's nose, not its +Z: the fox's nose is +X, turned by faceYaw
                Vector3 nose = spawn.transform.rotation * Quaternion.Euler(0f, -spawn.faceYaw, 0f) * Vector3.forward;
                float facing = Vector3.Dot(nose, toCam);
                Debug.Log($"{tag} height = {b.size.y:F3} m (want {spawn.targetHeight}); feet gap to floor = {(b.min.y - stage.transform.position.y) * 1000f:F1} mm; " +
                          $"faces camera = {facing:F3} (1 = straight at it)");
            }
        }

        /// <summary>Report the owl's animation setup in AR_Owl and the wing controls' motion in its clip.</summary>
        public static void ReportOwlAnimation()
        {
            EditorSceneManager.OpenScene(ScenePath(AppScenes.Owl), OpenSceneMode.Single);
            var owl = UnityEngine.Object.FindFirstObjectByType<GroundSpawn>(FindObjectsInactive.Include);
            var anim = owl.GetComponentInChildren<Animator>(true);
            Debug.Log($"[Owl] Animator present = {anim != null}; controller = {(anim != null && anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "NONE")}; " +
                      $"legacy Animation = {owl.GetComponentInChildren<Animation>(true) != null}");
            var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/owl_export/SnowyOwl.fbx").OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview")).ToArray();
            foreach (var c in clips) Debug.Log($"[Owl] clip '{c.name}' length={c.length:F2}s frameRate={c.frameRate} loop={c.isLooping}");
            var ctrls = AssetDatabase.FindAssets("t:AnimatorController").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            Debug.Log($"[Owl] AnimatorControllers in project: {(ctrls.Length == 0 ? "none" : string.Join(", ", ctrls))}");
            foreach (Transform t in owl.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("Ctrl_")) Debug.Log($"[Owl]   {t.name,-16} parent={t.parent.name,-14} localRot={t.localEulerAngles}");
        }

        /// <summary>Debug the fox's skinned-mesh measurement at identity.</summary>
        public static void ReportFoxScale()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var fox = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FoxFbx));
            var mi = (ModelImporter)AssetImporter.GetAtPath(FoxFbx);
            Debug.Log($"[FoxScale] importer globalScale={mi.globalScale} useFileScale={mi.useFileScale} fileScale={mi.fileScale}");
            foreach (var t in fox.GetComponentsInChildren<Transform>(true).Take(6))
                Debug.Log($"[FoxScale]   {t.name,-14} localScale={t.localScale} lossy={t.lossyScale} localPos={t.localPosition}");
            foreach (var smr in fox.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m1 = new Mesh(); smr.BakeMesh(m1, true); m1.RecalculateBounds();
                var m0 = new Mesh(); smr.BakeMesh(m0, false); m0.RecalculateBounds();
                Debug.Log($"[FoxScale] {smr.name}: sharedMesh bounds={smr.sharedMesh.bounds.size} renderer.bounds={smr.bounds.size} " +
                          $"bake(useScale)={m1.bounds.size} bake(noScale)={m0.bounds.size} smrLossy={smr.transform.lossyScale} rootBone={(smr.rootBone ? smr.rootBone.name + " lossy " + smr.rootBone.lossyScale : "none")}");
            }
        }

        /// <summary>Render AR_Fox for real: fur shells, snow patch, snowfall, walk poses.</summary>
        public static void CaptureFox()
        {
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            var scene = EditorSceneManager.OpenScene(ScenePath(AppScenes.Fox), OpenSceneMode.Single);
            var stage = GameObject.Find("Ground Plane Stage").transform;
            var spawn = stage.GetComponentInChildren<GroundSpawn>(true);
            var walk = spawn.GetComponent<FoxWalk>();
            walk.enabled = false;                                     // posed by hand below
            var clip = AssetDatabase.LoadAllAssetsAtPath(FoxFbx).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview"));

            var light = new GameObject("CapSun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.25f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            RenderSettings.ambientLight = new Color(0.58f, 0.6f, 0.64f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(0f, -0.001f, 0f);
            var floorMat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.45f, 0.4f, 0.36f) };
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;     // a wooden floor for the snow to sit on
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;

            var cam = new GameObject("CapCam").AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.78f, 0.8f, 0.83f);
            cam.transform.position = new Vector3(0.9f, 1.05f, 1.45f);
            cam.transform.LookAt(new Vector3(0f, 0.18f, 0f));
            spawn.Spawn(stage, cam, instant: true);

            var fall = stage.Find("FoxSnowfall")?.GetComponent<ParticleSystem>();
            if (fall != null) { fall.Simulate(6f, true, true); }

            var rt = new RenderTexture(900, 900, 24);
            cam.targetTexture = rt;
            float y0 = spawn.transform.localPosition.y;
            foreach (var (label, frac, ang) in new[] { ("side_a", 0.0f, 90f), ("side_b", 0.5f, 90f), ("away", 0.25f, 20f) })
            {
                // put the fox on its circle, heading along it, at this point of the stride
                float a = ang * Mathf.Deg2Rad;
                spawn.transform.localPosition = new Vector3(Mathf.Cos(a) * walk.radius, y0, Mathf.Sin(a) * walk.radius);
                Vector3 tangent = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                spawn.transform.localRotation = Quaternion.LookRotation(tangent) * Quaternion.Euler(0f, spawn.faceYaw, 0f);
                clip.SampleAnimation(spawn.gameObject, clip.length * frac);
                foreach (var f in spawn.GetComponentsInChildren<ShellFur>(true)) f.Rebuild();
                var b = spawn.MeshBounds();
                Debug.Log($"[Fox] {label}: fox height {b.size.y:F2} m, lowest paw {b.min.y * 1000f:F1} mm vs floor");
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(900, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 900, 900), 0, 0); tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, $"fox_{label}.png"), tex.EncodeToPNG());
            }
        }

        /// <summary>Render AR_Hare for real: fur, soil, bark chips, and moments from its hop.</summary>
        public static void CaptureHare()
        {
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            EditorSceneManager.OpenScene(ScenePath(AppScenes.Hare), OpenSceneMode.Single);
            var stage = GameObject.Find("Ground Plane Stage").transform;
            var spawn = stage.GetComponentInChildren<GroundSpawn>(true);
            var hop = spawn.GetComponent<HareHop>();
            var clip = AssetDatabase.LoadAllAssetsAtPath(HareFbx).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview"));

            var light = new GameObject("CapSun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -40f, 0f);
            RenderSettings.ambientLight = new Color(0.6f, 0.62f, 0.66f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(0f, -0.002f, 0f);
            floor.GetComponent<Renderer>().sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(0.62f, 0.6f, 0.57f) };
            stage.position = Vector3.zero; stage.rotation = Quaternion.identity;

            var cam = new GameObject("CapCam").AddComponent<Camera>();
            cam.fieldOfView = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.8f, 0.82f, 0.85f);
            cam.transform.position = new Vector3(0.7f, 0.75f, 1.05f);
            cam.transform.LookAt(new Vector3(0f, 0.12f, 0f));
            spawn.Spawn(stage, cam, instant: true);
            spawn.enabled = false;

            var rt = new RenderTexture(900, 900, 24);
            cam.targetTexture = rt;
            int frames = hop.metres.Length;
            hop.Step(hop.DistanceAt(0f));                                   // initialise on the spot
            // a sitting moment, mid-air in the first hop, and landing after the third
            foreach (var (label, frame) in new[] { ("sit", 10), ("airborne", 36), ("landed", 112) })
            {
                float nt = frame / (float)(frames - 1);
                clip.SampleAnimation(spawn.gameObject, clip.length * nt);
                hop.Step(hop.DistanceAt(nt));
                foreach (var f in spawn.GetComponentsInChildren<ShellFur>(true)) f.Rebuild();
                var b = spawn.MeshBounds();
                Vector3 lp = spawn.transform.localPosition;
                Debug.Log($"[Hare] {label,-9} frame {frame}: travelled {hop.DistanceAt(nt) * spawn.transform.localScale.x:F2} m, " +
                          $"{Mathf.Sqrt(lp.x * lp.x + lp.z * lp.z):F2} m from the spot, lowest point {b.min.y * 1000f:F0} mm off the floor");
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(900, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 900, 900), 0, 0); tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, $"hare_{label}.png"), tex.EncodeToPNG());
            }
        }

        /// <summary>Face close-ups of the fox and hare with the fur-length mask off, then on.</summary>
        public static void CaptureFaceFur()
        {
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            foreach (var (sceneName, fbx, eye) in new[] { (AppScenes.Fox, FoxFbx, 0.42f), (AppScenes.Hare, HareFbx, 0.30f) })
            {
                EditorSceneManager.OpenScene(ScenePath(sceneName), OpenSceneMode.Single);
                var stage = GameObject.Find("Ground Plane Stage").transform;
                var spawn = stage.GetComponentInChildren<GroundSpawn>(true);
                var light = new GameObject("CapSun").AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.25f;
                light.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
                RenderSettings.ambientLight = new Color(0.6f, 0.62f, 0.66f);
                stage.position = Vector3.zero; stage.rotation = Quaternion.identity;
                var cam = new GameObject("CapCam").AddComponent<Camera>();
                cam.fieldOfView = 34f;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.78f, 0.8f, 0.83f);
                cam.transform.position = new Vector3(0.25f, eye + 0.05f, 0.9f);
                spawn.Spawn(stage, cam, instant: true);
                var b = spawn.MeshBounds();
                // aim at the face: the top-front of the animal, which faces the camera
                Vector3 face = new Vector3(b.center.x, b.min.y + b.size.y * 0.68f, b.center.z) +
                               (cam.transform.position - b.center).normalized * b.size.z * 0.3f;
                cam.transform.LookAt(face);

                var furComp = spawn.GetComponentsInChildren<ShellFur>(true).First();
                var mesh = furComp.GetComponent<SkinnedMeshRenderer>().sharedMesh;
                var cols = mesh.colors;
                Debug.Log($"[Face] {sceneName}: mesh vertex colours {cols.Length}/{mesh.vertexCount}, " +
                          $"mask red {(cols.Length > 0 ? cols.Min(c => c.r) : -1):F2}..{(cols.Length > 0 ? cols.Max(c => c.r) : -1):F2}");

                var rt = new RenderTexture(700, 700, 24);
                cam.targetTexture = rt;
                foreach (var (label, on) in new[] { ("before", false), ("after", true) })
                {
                    furComp.lengthFromVertexColor = on;
                    furComp.Rebuild();
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(700, 700, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 700, 700), 0, 0); tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(outDir, $"face_{sceneName}_{label}.png"), tex.EncodeToPNG());
                }
            }
        }

        // ------------------------------------------------------- scene split
        /// <summary>
        /// Derive GroundScan and one scene per animal from SampleScene.
        ///
        /// Only creates scenes that do not exist yet. Once AR_Deer exists it is the
        /// user's to edit, and rerunning the builder must never clobber that -- only
        /// the generated ScanUI / ARHud objects are replaced on later runs.
        /// </summary>
        static void SplitIntoScenes()
        {
            var targets = Animals.Where(a => a.inAR).Select(a => a.target).ToArray();

            // Each animal scene keeps its own target (BuildAnimalScene then moves the
            // model off it onto the ground) and keeps the Plane Finder for the scan.
            foreach (var a in Animals.Where(a => a.inAR))
                MakeFromSample(ScenePath(a.arScene), targets.Where(t => t != a.target).Append("ScanUI"));
        }

        static void MakeFromSample(string path, System.Collections.Generic.IEnumerable<string> drop)
        {
            if (File.Exists(path))
            {
                Debug.Log("[AppUI] " + path + " exists, leaving its content alone");
                return;
            }
            if (!AssetDatabase.CopyAsset(ARPath, path))
                throw new Exception("Could not copy SampleScene to " + path);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var names = new System.Collections.Generic.HashSet<string>(drop);
            foreach (var go in scene.GetRootGameObjects())
                if (names.Contains(go.name))
                {
                    Debug.Log("[AppUI]   " + System.IO.Path.GetFileNameWithoutExtension(path) + ": removed " + go.name);
                    UnityEngine.Object.DestroyImmediate(go);
                }
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[AppUI] created " + path);
        }

        static void Prepare()
        {
            _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (_font == null)
                throw new Exception("TMP Essential Resources are not imported: " + FontPath);

            Directory.CreateDirectory(GenDir);
            _round     = RoundRect("round",  256, 96f, 96);
            _stroke    = RoundRectStroke("stroke", 256, 96f, 3f, 96);
            _shadow    = Shadow("shadow", 320, 64f, 64f);
            _gradUp    = Gradient("gradient");
            _circle    = Circle("circle", 128);
            _ringThin  = Ring("ring_thin", 512, 7f);
            _ringThick = Ring("ring_thick", 512, 30f);
            _blob      = Bake("blob", 128, 128, (x, y) =>
            {
                // soft contact shadow: dense centre, long feathered edge
                float dx = (x + .5f) / 64f - 1f, dy = (y + .5f) / 64f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float t = Sat(1f - d);
                return t * t * (3f - 2f * t);
            }, Vector4.zero);

            BakeIcons();

            foreach (var a in Animals)
            {
                ConfigureSprite($"{AnimalDir}/{a.file}.png", 1024, mip: true);
                ConfigureSprite($"{AnimalDir}/Backdrops/{a.file}_blur.png", 256, mip: false);
            }
            AssetDatabase.Refresh();
            ConfigureRiggedImport(FoxFbx, FoxController, "Walk",
                ("FoxFace", 0.3f), ("FoxFur", 0.08f), ("FoxWhiskers", 0.2f));
            ConfigureRiggedImport(HareFbx, HareController, "Hop",
                ("HareFace", 0.35f), ("HareFur", 0.08f), ("HareWhiskers", 0.2f));
            ConfigureStaticImport(HareGroundFbx, ("HareGround", 0.05f));
        }

        /// <summary>
        /// Generic rig, the one gait clip set to loop, external materials; plus an
        /// AnimatorController that just plays it. The clip is a single in-place
        /// 48-frame loop cut on export at its cleanest seam.
        /// </summary>
        static void ConfigureRiggedImport(string fbx, string controller, string clipName,
                                          params (string mat, float smooth)[] materials)
        {
            var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (mi == null) { Debug.LogWarning("[AppUI] model not found at " + fbx); return; }
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = true;
            mi.materialLocation = ModelImporterMaterialLocation.External;
            mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            mi.materialSearch = ModelImporterMaterialSearch.Local;
            var clips = mi.defaultClipAnimations;
            foreach (var c in clips) { c.name = clipName; c.loopTime = true; c.loopPose = false; }
            mi.clipAnimations = clips;
            mi.SaveAndReimport();

            var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) { Debug.LogWarning("[AppUI] no animation clip in " + fbx); return; }
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(controller) == null)
                AnimatorController.CreateAnimatorControllerAtPathWithClip(controller, clip);
            SetSmoothness(fbx, materials);
            Debug.Log($"[AppUI] {Path.GetFileNameWithoutExtension(fbx)} import: Generic rig, clip '{clip.name}' {clip.length:F2}s looping={clip.isLooping}");
        }

        static void ConfigureStaticImport(string fbx, params (string mat, float smooth)[] materials)
        {
            var mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (mi == null) { Debug.LogWarning("[AppUI] model not found at " + fbx); return; }
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.materialLocation = ModelImporterMaterialLocation.External;
            mi.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            mi.materialSearch = ModelImporterMaterialSearch.Local;
            mi.SaveAndReimport();
            SetSmoothness(fbx, materials);
        }

        static void SetSmoothness(string fbx, (string mat, float smooth)[] materials)
        {
            string dir = Path.GetDirectoryName(fbx).Replace('\\', '/');
            foreach (var (mat, smooth) in materials)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"{dir}/Materials/{mat}.mat");
                if (m == null) { Debug.LogWarning("[AppUI] material not found: " + mat); continue; }
                m.SetFloat("_Smoothness", smooth);
                m.SetFloat("_Glossiness", smooth);
                EditorUtility.SetDirty(m);
            }
        }



        // ------------------------------------------------------------ EntryScene
        static void BuildEntryScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Ground;
            cam.cullingMask = 0;                     // UI is Screen Space Overlay
            cam.orthographic = true;

            AddEventSystem();
            Canvas canvas = MakeCanvas("Canvas", 0);
            Transform root = canvas.transform;

            // --- backdrop: the current animal, blurred, under a dark scrim
            var backdrop = Stretch(Node("Backdrop", root));
            var sprites = Animals.Select(a =>
                AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/Backdrops/{a.file}_blur.png")).ToArray();
            Image bgA = CoverFill(backdrop, "BackdropA", sprites[0], new Vector2(0.5f, 0.5f));
            Image bgB = CoverFill(backdrop, "BackdropB", sprites[0], new Vector2(0.5f, 0.5f));
            Img(Stretch(Node("Tint", backdrop)), null, WithA(Ground, 0.4f));
            var bottomScrim = Img(Node("BottomScrim", backdrop), _gradUp, WithA(Ground, 0.95f));
            Anchor(bottomScrim.rectTransform, 0, 0, 1, 0.55f);
            var topScrim = Img(Node("TopScrim", backdrop), _gradUp, WithA(Ground, 0.8f));
            Anchor(topScrim.rectTransform, 0, 0.72f, 1, 1);
            topScrim.rectTransform.localScale = new Vector3(1, -1, 1);   // fade downward

            // --- safe content
            var safe = Stretch(Node("SafeArea", root));
            safe.gameObject.AddComponent<SafeAreaFitter>();
            var content = Stretch(Node("Content", safe));
            var contentGroup = content.gameObject.AddComponent<CanvasGroup>();

            // header, top-left aligned
            var header = Node("Header", content);
            AnchorTop(header, 440f);
            Text(header, "Eyebrow", "FIELD GUIDE   ·   5 WINTER SPECIES", 30, Rust,
                 FontStyles.Bold, spacing: 16f).rectTransform.Also(r => PlaceTopLeft(r, Margin, 56f, 900f, 40f));
            Text(header, "Title", "Winter Wild", 128, Snow, FontStyles.Bold, spacing: -2f)
                .rectTransform.Also(r => PlaceTopLeft(r, Margin - 4f, 108f, 1000f, 150f));
            var sub = Text(header, "Subtitle",
                 "Meet five animals of the northern winter, from the forest floor to the treetops.",
                 42, WithA(Snow, 0.72f), FontStyles.Normal, lineSpacing: 10f);
            sub.textWrappingMode = TextWrappingModes.Normal;
            PlaceTopLeft(sub.rectTransform, Margin, 272f, RefW - Margin * 2f - 40f, 130f);

            // footer, bottom
            var footer = Node("Footer", content);
            AnchorBottom(footer, 470f);

            var dotsRow = Node("Dots", footer);
            dotsRow.anchorMin = dotsRow.anchorMax = new Vector2(0.5f, 0f);
            dotsRow.pivot = new Vector2(0.5f, 0f);
            dotsRow.sizeDelta = new Vector2(600f, 16f);
            dotsRow.anchoredPosition = new Vector2(0f, 420f);
            var hlg = dotsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 14f; hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = hlg.childControlHeight = false;
            hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
            var dots = new RectTransform[Animals.Length];
            var dotImgs = new Image[Animals.Length];
            for (int i = 0; i < Animals.Length; i++)
            {
                var d = Img(Node($"Dot{i}", dotsRow), _round, WithA(Snow, 0.28f), sliced: true, ppu: 12f);
                d.rectTransform.sizeDelta = new Vector2(16f, 16f);
                dots[i] = d.rectTransform; dotImgs[i] = d;
            }

            var helper = Text(footer, "Helper",
                "First, scan your floor so the app knows where the ground is.",
                36, WithA(Snow, 0.62f), FontStyles.Normal, lineSpacing: 8f);
            helper.textWrappingMode = TextWrappingModes.Normal;
            PlaceBottomLeft(helper.rectTransform, Margin, 250f, RefW - Margin * 2f, 110f);

            Button scan = PillButton(footer, "ScanButton", "Scan the ground", Rust, RustInk,
                                     height: 168f, glow: true);
            PlaceBottomStretch((RectTransform)scan.transform, Margin, 40f, 168f);

            // carousel between header and footer; bleeds to the screen edges
            var area = Node("CarouselArea", content);
            area.anchorMin = new Vector2(0f, 0f); area.anchorMax = new Vector2(1f, 1f);
            area.offsetMin = new Vector2(0f, 500f); area.offsetMax = new Vector2(0f, -470f);

            var viewport = Stretch(Node("Viewport", area));
            Img(viewport, null, new Color(0, 0, 0, 0));           // raycast target for drags
            var carousel = viewport.gameObject.AddComponent<SwipeCarousel>();
            var track = Node("Track", viewport);
            track.anchorMin = track.anchorMax = track.pivot = new Vector2(0.5f, 0.5f);
            track.sizeDelta = Vector2.zero;

            var cards = new RectTransform[Animals.Length];
            var dims = new Image[Animals.Length];
            for (int i = 0; i < Animals.Length; i++)
                cards[i] = BuildCard(track, Animals[i], out dims[i]);

            carousel.content = track;
            carousel.cards = cards;
            carousel.dims = dims;

            // fader sits over everything, for scene transitions
            var fader = Stretch(Node("Fader", root));
            Img(fader, null, Night);
            var faderGroup = fader.gameObject.AddComponent<CanvasGroup>();
            faderGroup.alpha = 1f; faderGroup.blocksRaycasts = false;

            var entry = canvas.gameObject.AddComponent<EntryScreen>();
            entry.carousel = carousel;
            entry.carouselArea = area;
            entry.maxCardWidthFraction = 0.76f;
            entry.maxCardHeight = 1140f;
            entry.dots = dots;
            entry.dotImages = dotImgs;
            entry.dotActive = Rust;
            entry.dotIdle = WithA(Snow, 0.28f);
            entry.backdropA = bgA;
            entry.backdropB = bgB;
            entry.backdrops = sprites;
            entry.scanButton = scan;
            // disabled look comes from the explicit colours below, not a tint on top
            var scb = scan.colors; scb.disabledColor = Color.white; scan.colors = scb;
            entry.actionLabel = scan.transform.Find("Label").GetComponent<TMP_Text>();
            entry.actionFill = scan.transform.Find("Fill").GetComponent<Image>();
            entry.actionGlow = scan.transform.Find("Glow").GetComponent<Image>();
            entry.helper = helper;
            entry.arScenes = Animals.Select(a => a.arScene).ToArray();
            entry.commonNames = Animals.Select(a => a.common).ToArray();
            entry.fillReady = Rust;
            entry.inkReady = RustInk;
            entry.fillDisabled = WithA(Snow, 0.12f);
            entry.inkDisabled = WithA(Snow, 0.45f);
            entry.requireScanFirst = false;
            entry.content = contentGroup;
            entry.contentMotion = content;
            entry.fader = faderGroup;

            EditorSceneManager.SaveScene(scene, EntryPath);
        }

        static RectTransform BuildCard(Transform parent, Species s, out Image dim)
        {
            var card = Node($"Card_{s.file}", parent);
            card.sizeDelta = new Vector2(880f, 1100f);

            // soft shadow, extending past the card on every side
            var shadow = Img(Node("Shadow", card), _shadow, WithA(Night, 0.6f), sliced: true);
            Anchor(shadow.rectTransform, 0, 0, 1, 1);
            shadow.rectTransform.offsetMin = new Vector2(-64f, -92f);
            shadow.rectTransform.offsetMax = new Vector2(64f, 44f);
            shadow.raycastTarget = false;

            var maskImg = Img(Stretch(Node("Mask", card)), _round, Color.white, sliced: true, ppu: 1.6f);
            maskImg.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            Transform m = maskImg.transform;

            var photo = Img(Node("Photo", m),
                AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/{s.file}.png"), Color.white);
            photo.raycastTarget = false;
            photo.gameObject.AddComponent<CoverImage>().focus = new Vector2(s.focusX, 0.5f);

            var scrim = Img(Node("Scrim", m), _gradUp, WithA(Night, 0.88f));
            Anchor(scrim.rectTransform, 0, 0, 1, 0.52f);
            scrim.raycastTarget = false;

            if (s.inAR)
            {
                var tag = Node("Tag", m);
                tag.anchorMin = tag.anchorMax = tag.pivot = new Vector2(0f, 1f);
                tag.anchoredPosition = new Vector2(44f, -44f);
                LayoutBackground(tag, 3.2f, WithA(Night, 0.55f));
                var h = tag.gameObject.AddComponent<HorizontalLayoutGroup>();
                h.padding = new RectOffset(24, 26, 12, 12); h.spacing = 12f;
                h.childAlignment = TextAnchor.MiddleLeft;
                h.childControlWidth = h.childControlHeight = true;
                h.childForceExpandWidth = h.childForceExpandHeight = false;
                var fit = tag.gameObject.AddComponent<ContentSizeFitter>();
                fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var dot = Img(Node("Dot", tag), _circle, Rust);
                var le = dot.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = le.preferredHeight = 14f; le.minWidth = le.minHeight = 14f;
                var t = Text(tag, "Label", "IN AR", 26, Snow, FontStyles.Bold, spacing: 14f);
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }

            var latin = Text(m, "Latin", s.latin, 36, WithA(Snow, 0.74f), FontStyles.Italic);
            PlaceBottomLeft(latin.rectTransform, 52f, 54f, 780f, 46f);
            var name = Text(m, "Name", s.common, 68, Snow, FontStyles.Bold, spacing: -1f);
            name.textWrappingMode = TextWrappingModes.Normal;
            name.verticalAlignment = VerticalAlignmentOptions.Bottom;
            PlaceBottomLeft(name.rectTransform, 50f, 108f, 780f, 170f);

            // hairline so light photos keep an edge against the backdrop
            var edge = Img(Stretch(Node("Edge", card)), _stroke, WithA(Snow, 0.14f), sliced: true, ppu: 1.6f);
            edge.raycastTarget = false;

            dim = Img(Stretch(Node("Dim", m)), null, new Color(Night.r, Night.g, Night.b, 0f));
            dim.raycastTarget = false;
            return card;
        }

        // ------------------------------------------------------------- ScanUI
        /// <summary>Standalone GroundScan scene. No longer in the build; kept buildable.</summary>
        static void BuildScanOverlay()
        {
            var scene = EditorSceneManager.OpenScene(ScanPath, OpenSceneMode.Single);
            BuildScanUI(includeBack: true, spawn: null, returnToMenu: true,
                        lockedTitle: "Ground saved", lockedSubtitle: "The app knows where your floor is.");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        /// <summary>Build ScanUI into the active scene, replacing any previous one.</summary>
        static void BuildScanUI(bool includeBack, GroundSpawn spawn, bool returnToMenu,
                                string lockedTitle, string lockedSubtitle,
                                ObserverBehaviour trigger = null, string pageSubtitle = null,
                                Sprite pagePhoto = null, string pageCaption = null)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == "ScanUI"))
                UnityEngine.Object.DestroyImmediate(old);
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null) AddEventSystem();

            var finderGo = GameObject.Find("Plane Finder");
            if (finderGo == null) throw new Exception("SampleScene has no 'Plane Finder'.");
            var finder = finderGo.GetComponent<PlaneFinderBehaviour>();
            var positioning = finderGo.GetComponent<ContentPositioningBehaviour>();
            if (finder == null || positioning == null)
                throw new Exception("Plane Finder is missing PlaneFinderBehaviour or ContentPositioningBehaviour.");

            Canvas canvas = MakeCanvas("ScanUI", 100);
            Transform root = canvas.transform;

            var overlay = Stretch(Node("Overlay", root));
            var overlayGroup = overlay.gameObject.AddComponent<CanvasGroup>();

            // scrims keep text legible over an unpredictable camera feed
            var top = Img(Node("TopScrim", overlay), _gradUp, WithA(Night, 0.78f));
            Anchor(top.rectTransform, 0, 0.66f, 1, 1);
            top.rectTransform.localScale = new Vector3(1, -1, 1);
            top.raycastTarget = false;
            var bottom = Img(Node("BottomScrim", overlay), _gradUp, WithA(Night, 0.6f));
            Anchor(bottom.rectTransform, 0, 0, 1, 0.24f);
            bottom.raycastTarget = false;

            var safe = Stretch(Node("SafeArea", overlay));
            safe.gameObject.AddComponent<SafeAreaFitter>();

            // In an animal scene the ARHud above owns Back; two stacked would double-fire
            Button back = includeBack ? BackButton(safe) : null;

            // Copy sits on a panel that sizes to its text. A gradient alone cannot
            // guarantee contrast: the camera feed can be a white wall or snow.
            var panel = Node("Panel", safe);
            PlaceTopLeft(panel, Margin - 20f, 150f, RefW - (Margin - 20f) * 2f, 300f);
            LayoutBackground(panel, 1.9f, WithA(Night, 0.62f));
            var vlg = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(44, 44, 38, 44); vlg.spacing = 14f;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var title = Text(panel, "Title", "Find the floor", 72, Snow, FontStyles.Bold, spacing: -1f);
            var sub = Text(panel, "Subtitle",
                "Point your camera at the ground and move your phone slowly.",
                40, WithA(Snow, 0.86f), FontStyles.Normal, lineSpacing: 10f);
            sub.textWrappingMode = TextWrappingModes.Normal;

            // reticle: thin guide ring, thick progress arc, centre point
            var reticle = Node("Reticle", safe);
            reticle.anchorMin = reticle.anchorMax = reticle.pivot = new Vector2(0.5f, 0.5f);
            reticle.sizeDelta = new Vector2(300f, 300f);
            var halo = Img(Node("Halo", reticle), _shadow, WithA(Night, 0.35f), sliced: true);
            Anchor(halo.rectTransform, 0, 0, 1, 1);
            halo.rectTransform.offsetMin = new Vector2(-70f, -70f);
            halo.rectTransform.offsetMax = new Vector2(70f, 70f);
            var ring = Img(Stretch(Node("Ring", reticle)), _ringThin, WithA(Snow, 0.55f));
            var fill = Img(Stretch(Node("Progress", reticle)), _ringThick, Rust);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Radial360;
            fill.fillOrigin = (int)Image.Origin360.Top;
            fill.fillClockwise = true;
            fill.fillAmount = 0f;
            var centre = Img(Node("Centre", reticle), _circle, Snow);
            centre.rectTransform.anchorMin = centre.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            centre.rectTransform.sizeDelta = new Vector2(18f, 18f);
            foreach (var g in reticle.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
            var reticleGroup = reticle.gameObject.AddComponent<CanvasGroup>();
            reticleGroup.blocksRaycasts = false;

            // the page step: where the reticle was, a picture of what to look for
            CanvasGroup pageHintGroup = null;
            if (pagePhoto != null)
            {
                var hint = Node("PageHint", safe);
                hint.anchorMin = hint.anchorMax = hint.pivot = new Vector2(0.5f, 0.5f);
                hint.sizeDelta = new Vector2(640f, 470f);
                var hs = Img(Node("Shadow", hint), _shadow, WithA(Night, 0.55f), sliced: true);
                Anchor(hs.rectTransform, 0, 0, 1, 1);
                hs.rectTransform.offsetMin = new Vector2(-56f, -80f);
                hs.rectTransform.offsetMax = new Vector2(56f, 40f);
                var hm = Img(Stretch(Node("Mask", hint)), _round, Color.white, sliced: true, ppu: 1.8f);
                hm.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                var hp = Img(Node("Photo", hm.rectTransform), pagePhoto, Color.white);
                hp.gameObject.AddComponent<CoverImage>().focus = new Vector2(0.5f, 0.5f);
                var hsc = Img(Node("Scrim", hm.rectTransform), _gradUp, WithA(Night, 0.85f));
                Anchor(hsc.rectTransform, 0, 0, 1, 0.55f);
                var words = Node("Words", hm.rectTransform);
                words.anchorMin = new Vector2(0f, 0f); words.anchorMax = new Vector2(1f, 0f); words.pivot = new Vector2(0f, 0f);
                words.offsetMin = new Vector2(40f, 30f); words.offsetMax = new Vector2(-40f, 150f);
                var wv = words.gameObject.AddComponent<VerticalLayoutGroup>();
                wv.childAlignment = TextAnchor.LowerLeft; wv.spacing = 2f;
                wv.childControlWidth = wv.childControlHeight = true;
                wv.childForceExpandWidth = true; wv.childForceExpandHeight = false;
                Text(words, "Name", pageCaption ?? "", 46, Snow, FontStyles.Bold);
                Text(words, "Hint", "Find this page in your book", 30, WithA(Snow, 0.8f), FontStyles.Normal);
                var rim = Img(Stretch(Node("Rim", hint)), _stroke, WithA(Snow, 0.85f), sliced: true, ppu: 1.8f);
                foreach (var g in hint.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
                pageHintGroup = hint.gameObject.AddComponent<CanvasGroup>();
                pageHintGroup.alpha = 0f; pageHintGroup.blocksRaycasts = false;
            }

            // status chip
            var chip = Node("Status", safe);
            chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(0.5f, 0f);
            chip.anchoredPosition = new Vector2(0f, 72f);
            LayoutBackground(chip, 2.2f, WithA(Night, 0.6f));
            var ch = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
            ch.padding = new RectOffset(36, 42, 24, 24); ch.spacing = 18f;
            ch.childAlignment = TextAnchor.MiddleCenter;
            ch.childControlWidth = ch.childControlHeight = true;
            ch.childForceExpandWidth = ch.childForceExpandHeight = false;
            var cf = chip.gameObject.AddComponent<ContentSizeFitter>();
            cf.horizontalFit = cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sdot = Img(Node("Dot", chip), _circle, WithA(Snow, 0.55f));
            var sle = sdot.gameObject.AddComponent<LayoutElement>();
            sle.preferredWidth = sle.preferredHeight = sle.minWidth = sle.minHeight = 18f;
            var status = Text(chip, "Label", "Searching", 34, Snow, FontStyles.Bold, spacing: 4f);
            status.textWrappingMode = TextWrappingModes.NoWrap;

            var fader = Stretch(Node("Fader", root));
            Img(fader, null, Night);
            var faderGroup = fader.gameObject.AddComponent<CanvasGroup>();
            faderGroup.alpha = 1f; faderGroup.blocksRaycasts = false;

            var flow = canvas.gameObject.AddComponent<GroundScanFlow>();
            flow.planeFinder = finder;
            flow.contentPositioning = positioning;
            flow.overlay = overlayGroup;
            flow.fader = faderGroup;
            flow.title = title;
            flow.subtitle = sub;
            flow.statusLabel = status;
            flow.statusDot = sdot;
            flow.reticle = reticle;
            flow.reticleRing = ring;
            flow.reticleFill = fill;
            flow.backButton = back;
            flow.accent = Rust;
            flow.idle = WithA(Snow, 0.55f);
            flow.success = Success;
            flow.spawn = spawn;
            flow.returnToMenu = returnToMenu;
            flow.lockedTitle = lockedTitle;
            flow.lockedSubtitle = lockedSubtitle;
            flow.reticleGroup = reticleGroup;
            flow.trigger = trigger;
            flow.pageHint = pageHintGroup;
            if (pageSubtitle != null) flow.pageSubtitle = pageSubtitle;
        }

        /// <summary>
        /// Make sure the scene has its book-page image target, as a trigger only.
        /// Scenes converted before the page became the trigger had theirs removed,
        /// so it is cloned back from SampleScene -- with the model stripped off,
        /// since the animal now stands on the floor, not the page. SampleScene is
        /// opened additively and closed unsaved.
        /// </summary>
        static ObserverBehaviour EnsureImageTarget(Scene scene, Species a)
        {
            var existing = GameObject.Find(a.target);
            if (existing == null)
            {
                var src = EditorSceneManager.OpenScene(ARPath, OpenSceneMode.Additive);
                var original = src.GetRootGameObjects().FirstOrDefault(g => g.name == a.target);
                string retarget = null;
                if (original == null && Trackable(a.file) != null)
                {
                    // No such target in SampleScene (the fox): clone another page and
                    // point it at this animal's trackable in the same database.
                    original = src.GetRootGameObjects().First(g => g.name == "ImageTargetDeer");
                    retarget = Trackable(a.file);
                }
                if (original == null) throw new Exception("SampleScene has no " + a.target + " to restore");
                existing = UnityEngine.Object.Instantiate(original);
                existing.name = a.target;
                SceneManager.MoveGameObjectToScene(existing, scene);
                EditorSceneManager.CloseScene(src, true);
                if (retarget != null)
                {
                    var so = new SerializedObject(existing.GetComponent<ImageTargetBehaviour>());
                    var name = so.FindProperty("mTrackableName");
                    if (name == null) throw new Exception("ImageTargetBehaviour has no mTrackableName field");
                    name.stringValue = retarget;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log($"[AppUI] {a.arScene}: new page trigger {a.target} -> trackable '{retarget}'");
                }
                else Debug.Log($"[AppUI] {a.arScene}: restored {a.target} as the page trigger");
            }
            // a trigger carries nothing: anything left under it would appear on the page
            for (int i = existing.transform.childCount - 1; i >= 0; i--)
            {
                var child = existing.transform.GetChild(i).gameObject;
                Debug.Log($"[AppUI] {a.arScene}: removed '{child.name}' from under {a.target}");
                UnityEngine.Object.DestroyImmediate(child);
            }
            var observer = existing.GetComponent<ObserverBehaviour>();
            if (observer == null) throw new Exception($"{a.arScene}: {a.target} has no ObserverBehaviour");
            return observer;
        }

        static Button BackButton(RectTransform parent)
        {
            Button back = PillButton(parent, "Back", "Back", WithA(Night, 0.5f), Snow, height: 92f, glow: false,
                                     labelSize: 34f, ppu: 2.1f);
            var rt = (RectTransform)back.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(176f, 92f);
            rt.anchoredPosition = new Vector2(Margin - 16f, -28f);
            return back;
        }

        // ----------------------------------------------------- animal scenes
        /// <summary>
        /// Turn an animal scene from "model on an image target" into "model spawned
        /// on the scanned floor", then (re)build its scan overlay and HUD.
        ///
        /// Structural changes happen once: if the model is already under the
        /// Ground Plane Stage it is left exactly as it is, so tweaks made to it in
        /// the scene (height, position) survive a rebuild.
        /// </summary>
        static void BuildAnimalScene(Species a)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath(a.arScene), OpenSceneMode.Single);
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null) AddEventSystem();

            EnsurePlaneFinder(scene);
            var stage = GameObject.Find("Ground Plane Stage").transform;

            GroundSpawn spawn = stage.GetComponentInChildren<GroundSpawn>(true);
            if (spawn == null)
            {
                var target = GameObject.Find(a.target);
                GameObject model = target == null ? null : target.transform.Cast<Transform>()
                    .Select(t => t.gameObject)
                    .FirstOrDefault(PrefabUtility.IsAnyPrefabInstanceRoot);
                if (model == null && ModelPath(a.file) != null)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath(a.file));
                    if (asset == null) throw new Exception($"{a.arScene}: missing model {ModelPath(a.file)}");
                    model = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                }
                if (model == null) throw new Exception($"{a.arScene}: no model prefab under {a.target}");

                // off the image target, onto the ground, back to its natural upright pose
                model.transform.SetParent(stage, false);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one;
                AdaptForFloor(model, a.file);

                spawn = model.AddComponent<GroundSpawn>();
                spawn.targetHeight = SpawnHeight(a.file);
                spawn.faceYaw = SpawnYaw(a.file);
                Debug.Log($"[AppUI] {a.arScene}: moved {model.name} from {a.target} onto the ground " +
                          $"({spawn.targetHeight} m tall); {a.target} stays as the trigger");
            }
            ObserverBehaviour page = EnsureImageTarget(scene, a);
            if (a.file == "owl") SetupOwlFlight(spawn.gameObject, stage);
            if (a.file == "fox") SetupFox(spawn, stage, scene);
            if (a.file == "hare") SetupHare(spawn, stage, scene);
            if (a.file == "squirrel" && spawn.GetComponent<SquirrelPalm>() == null) spawn.gameObject.AddComponent<SquirrelPalm>();
            if (a.file == "deer") SetupDeerPalm(spawn);
            var palm = spawn.GetComponent<PalmReaction>();
            if (palm != null) WirePalm(palm, a.arScene);
            SetupAudio(a, spawn);
            // the deer was first placed fawn-sized; bring an untouched one up to life size
            if (a.file == "deer" && Mathf.Approximately(spawn.targetHeight, 1.0f)) spawn.targetHeight = SpawnHeight("deer");
            SetupGrounding(a, spawn, stage);
            SetupTricksAndGame(a, spawn);
            // hidden until the floor locks, then GroundSpawn places and reveals it
            spawn.gameObject.SetActive(false);

            BuildScanUI(includeBack: false, spawn: spawn, returnToMenu: false,
                        lockedTitle: "Found it",
                        lockedSubtitle: $"Placing the {a.common.ToLowerInvariant()} on your floor.",
                        trigger: page,
                        pageSubtitle: $"Point your camera at the {a.common.ToLowerInvariant()} page in your book.",
                        pagePhoto: AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/{a.file}.png"),
                        pageCaption: a.common);
            BuildARHud(a);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        const string OwlFbx = "Assets/owl_export/SnowyOwl.fbx";
        const float OwlFlapPeakTime = (26f - 1f) / 24f;   // clip frame 26: first wing beat at full height

        /// <summary>
        /// Measure the owl's wing hinge from its own baked clip: sample the rest
        /// frame and the first full wing beat, and take the rotation between them.
        /// The Blender -> FBX -> Unity axis conversion makes the hinge axis easy to
        /// get wrong by hand; reading it back out of the clip cannot be.
        /// </summary>
        static bool MeasureOwlWings(out Quaternion nearRest, out Vector3 nearAxis,
                                    out Quaternion farRest, out Vector3 farAxis)
        {
            nearRest = farRest = Quaternion.identity; nearAxis = farAxis = Vector3.right;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(OwlFbx);
            var clip = AssetDatabase.LoadAllAssetsAtPath(OwlFbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (asset == null || clip == null) { Debug.LogWarning("[AppUI] owl clip not found"); return false; }

            var tmp = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            Transform Find(string n) => tmp.GetComponentsInChildren<Transform>(true).First(t => t.name == n);
            var near = Find("Ctrl_Wing_Near"); var far = Find("Ctrl_Wing_Far");

            clip.SampleAnimation(tmp, 0f);
            nearRest = near.localRotation; farRest = far.localRotation;
            clip.SampleAnimation(tmp, OwlFlapPeakTime);
            (Quaternion.Inverse(nearRest) * near.localRotation).ToAngleAxis(out float na, out nearAxis);
            (Quaternion.Inverse(farRest) * far.localRotation).ToAngleAxis(out float fa, out farAxis);
            // ToAngleAxis reports 0..360; fold so the axis is the one a POSITIVE lift turns about
            if (na > 180f) { na = 360f - na; nearAxis = -nearAxis; }
            if (fa > 180f) { fa = 360f - fa; farAxis = -farAxis; }
            Debug.Log($"[AppUI] owl wing hinge from clip: near {na:F1} deg about {nearAxis}, far {fa:F1} deg about {farAxis} (keyed at 40)");
            UnityEngine.Object.DestroyImmediate(tmp);
            return true;
        }

        static void SetupOwlFlight(GameObject owl, Transform stage)
        {
            var flight = owl.GetComponent<OwlFlight>();
            if (flight == null)
            {
                flight = owl.AddComponent<OwlFlight>();
                if (MeasureOwlWings(out var nr, out var nx, out var fr, out var fx))
                {
                    flight.nearRest = nr; flight.nearAxis = nx;
                    flight.farRest = fr;  flight.farAxis = fx;
                }
                Debug.Log("[AppUI] AR_Owl: added OwlFlight");
            }
            Transform T(string n) => owl.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
            flight.wingNear = T("Ctrl_Wing_Near");
            flight.wingFar = T("Ctrl_Wing_Far");

            // Contact shadow, a sibling on the stage so it stays on the floor while
            // the owl climbs. A floating object with no shadow reads as pasted on.
            var old = stage.Find("OwlShadow");
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var sh = new GameObject("OwlShadow");
            sh.transform.SetParent(stage, false);
            sh.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);    // lie flat on the floor
            float spriteMetres = _blob.rect.width / _blob.pixelsPerUnit;
            sh.transform.localScale = Vector3.one * (flight.shadowSize / spriteMetres);
            var sr = sh.AddComponent<SpriteRenderer>();
            sr.sprite = _blob;
            sr.color = new Color(0f, 0f, 0f, flight.shadowAlpha);
            sh.SetActive(false);
            flight.shadow = sr;
            WireOwlGestures(flight);
        }

        /// <summary>
        /// Open palm calls the owl over. Wired as persistent listeners on the
        /// HandGestureManager's events, so the link shows in its Inspector
        /// ("On Gesture Began -> OwlFlight.OnGestureBegan") and can be rewired there.
        /// Idempotent: listeners pointing at this OwlFlight are removed first.
        /// </summary>
        static void WireOwlGestures(OwlFlight flight)
        {
            var gm = UnityEngine.Object.FindFirstObjectByType<HandGestureManager>(FindObjectsInactive.Include);
            if (gm == null) { Debug.LogWarning("[AppUI] AR_Owl has no HandGestureManager; open palm will do nothing"); return; }

            void Clear(UnityEngine.Events.UnityEventBase ev)
            {
                for (int i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
                    if (ev.GetPersistentTarget(i) == flight) UnityEventTools.RemovePersistentListener(ev, i);
            }
            Clear(gm.onGestureBegan); Clear(gm.onGestureEnded); Clear(gm.onHandLost);

            UnityEventTools.AddPersistentListener(gm.onGestureBegan, flight.OnGestureBegan);
            UnityEventTools.AddPersistentListener(gm.onGestureEnded, flight.OnGestureEnded);
            UnityEventTools.AddPersistentListener(gm.onHandLost, flight.OnHandLost);
            EditorUtility.SetDirty(gm);
            Debug.Log("[AppUI] AR_Owl: open palm -> owl comes to you (wired on HandGestureManager)");
        }

        /// <summary>
        /// Open palm -> the animal's PalmReaction (squirrel, fox, deer, hare), wired
        /// like the owl: persistent listeners on HandGestureManager, visible and
        /// re-wirable in its Inspector. Idempotent.
        /// </summary>
        static void WirePalm(PalmReaction palm, string sceneName)
        {
            var gm = UnityEngine.Object.FindFirstObjectByType<HandGestureManager>(FindObjectsInactive.Include);
            if (gm == null) { Debug.LogWarning($"[AppUI] {sceneName} has no HandGestureManager; open palm will do nothing"); return; }

            void Clear(UnityEngine.Events.UnityEventBase ev)
            {
                for (int i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
                    if (ev.GetPersistentTarget(i) is PalmReaction) UnityEventTools.RemovePersistentListener(ev, i);
            }
            Clear(gm.onGestureBegan); Clear(gm.onGestureEnded); Clear(gm.onHandLost);

            UnityEventTools.AddPersistentListener(gm.onGestureBegan, palm.OnGestureBegan);
            UnityEventTools.AddPersistentListener(gm.onGestureEnded, palm.OnGestureEnded);
            UnityEventTools.AddPersistentListener(gm.onHandLost, palm.OnHandLost);
            EditorUtility.SetDirty(gm);
            Debug.Log($"[AppUI] {sceneName}: open palm -> {palm.GetType().Name} (wired on HandGestureManager)");
        }

        // ------------------------------------------------- tricks and minigames
        static Material LitMaterial(string name, Color c, float smooth)
        {
            string path = $"{GenDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, path); }
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static T[] Clips<T>(params string[] names) where T : UnityEngine.Object => names.Select(n => Clip(n) as T).ToArray();

        /// <summary>
        /// The gesture tricks (AnimalTricks) and the animal's minigame. Re-run safe:
        /// components are reused and their settings rewritten.
        /// </summary>
        static void SetupTricksAndGame(Species a, GroundSpawn spawn)
        {
            var go = spawn.gameObject;
            Transform Bone(string n) => go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
            var tricks = GetOrAdd<AnimalTricks>(go);
            tricks.specialName = SpecialName(a.file);
            tricks.pausesRoutine = a.file != "owl";
            (tricks.special, tricks.head) = a.file switch
            {
                "squirrel" => (AnimalTricks.Special.Spin, (Transform)null),
                "fox" => (AnimalTricks.Special.Pounce, Bone("head")),
                "deer" => (AnimalTricks.Special.Leap, Bone("Ctrl_Head")),
                "hare" => (AnimalTricks.Special.Binky, Bone("head")),
                _ => (AnimalTricks.Special.HeadTurn, Bone("Ctrl_Head")),
            };
            EditorUtility.SetDirty(tricks);

            var snow = LitMaterial("GameSnow", new Color(0.95f, 0.97f, 1f), 0.25f);
            foreach (var old in go.GetComponents<MiniGame>()) UnityEngine.Object.DestroyImmediate(old);
            MiniGame game;
            switch (a.file)
            {
                case "fox":
                {
                    var g = go.AddComponent<FoxMouseHunt>();
                    g.squeaks = Clips<AudioClip>("mouse_squeak_1", "mouse_squeak_2", "mouse_squeak_3");
                    g.pounceSound = Clip("pounce");
                    g.snowMaterial = snow;
                    g.mouseMaterial = LitMaterial("GameMouse", new Color(0.42f, 0.33f, 0.27f), 0.2f);
                    game = g; break;
                }
                case "squirrel":
                {
                    var g = go.AddComponent<SquirrelNutStash>();
                    g.acornMaterial = LitMaterial("GameAcorn", new Color(0.55f, 0.33f, 0.14f), 0.45f);
                    g.capMaterial = LitMaterial("GameAcornCap", new Color(0.33f, 0.23f, 0.14f), 0.1f);
                    g.dropSound = Clip("acorn_drop"); g.digSound = Clip("acorn_dig"); g.sinkSound = Clip("acorn_sink");
                    game = g; break;
                }
                case "deer":
                    game = go.AddComponent<DeerFreeze>(); break;
                case "hare":
                {
                    var g = go.AddComponent<HareSnowHide>();
                    g.snowMaterial = snow;
                    g.burrowSound = Clip("acorn_dig"); g.poofSound = Clip("snow_puff");
                    game = g; break;
                }
                default:
                {
                    var g = go.AddComponent<OwlHootEcho>();
                    g.shortHoot = Clip("hoot_short"); g.longHoot = Clip("hoot_long");
                    g.head = Bone("Ctrl_Head");
                    game = g; break;
                }
            }
            EditorUtility.SetDirty(game);
            Debug.Log($"[AppUI] {a.arScene}: tricks (point = {tricks.specialName}, head {(tricks.head != null ? tricks.head.name : "none")}), game '{game.Title}'");
        }

        // ---------------------------------------------------------- grounding
        const string ShadowCatcherMat = GenDir + "/ShadowCatcher.mat";
        const string ContactShadowMat = GenDir + "/ContactShadow.mat";

        static Material ShaderMaterial(string path, string shader)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var sh = Shader.Find(shader);
            if (sh == null) throw new Exception("missing shader " + shader);
            if (mat == null) { mat = new Material(sh); AssetDatabase.CreateAsset(mat, path); }
            else mat.shader = sh;
            return mat;
        }

        /// <summary>
        /// What makes an animal sit IN the room rather than on top of the video:
        ///  * an invisible shadow catcher on the floor, so its real-time shadow falls
        ///    on the camera image of your floor (revealed with the animal);
        ///  * a soft contact shadow under it that follows it about and fades as it
        ///    leaves the ground;
        ///  * the scene light turned to come from overhead, as indoor light does,
        ///    with its brightness matched to the room by CameraLightEstimator.
        /// Re-run safe.
        /// </summary>
        static void SetupGrounding(Species a, GroundSpawn spawn, Transform stage)
        {
            var catcherMat = ShaderMaterial(ShadowCatcherMat, "Custom/AR Shadow Catcher");
            catcherMat.SetFloat("_ShadowStrength", 0.45f);
            catcherMat.SetFloat("_EdgeFade", 0.4f);
            var contactMat = ShaderMaterial(ContactShadowMat, "Custom/AR Contact Shadow");
            contactMat.SetFloat("_Softness", 1.1f);       // a broad dark core under the body, then a soft edge

            var old = stage.Find("ShadowCatcher");
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var catcher = GameObject.CreatePrimitive(PrimitiveType.Quad);
            catcher.name = "ShadowCatcher";
            UnityEngine.Object.DestroyImmediate(catcher.GetComponent<Collider>());
            catcher.transform.SetParent(stage, false);
            catcher.transform.localPosition = new Vector3(0f, 0.001f, 0f);
            catcher.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            catcher.transform.localScale = new Vector3(5f, 5f, 1f);      // covers every animal's wanderings
            var cr = catcher.GetComponent<MeshRenderer>();
            cr.sharedMaterial = catcherMat;
            cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cr.receiveShadows = true;
            catcher.SetActive(false);
            spawn.revealWith = (spawn.revealWith ?? new GameObject[0])
                .Where(g => g != null && g.name != "ShadowCatcher").Append(catcher).ToArray();

            // snowfall must shrink with the stage in Small mode: particle systems
            // ignore their parents' scale unless told otherwise
            foreach (var ps in stage.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }

            GetOrAdd<AnimalTapTarget>(spawn.gameObject);         // tap the animal for its facts

            var cs = GetOrAdd<ContactShadow>(spawn.gameObject);
            cs.material = contactMat;
            Transform Bone(string n) => spawn.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
            switch (a.file)
            {
                case "squirrel": cs.opacity = 0.62f; cs.spread = 1.7f; cs.lowerShare = 0.4f; cs.liftReference = Bone("SquirrelRoot"); cs.fadeHeight = 0.05f; break;
                case "fox":      cs.opacity = 0.6f; cs.spread = 1.5f; cs.lowerShare = 0.3f; cs.liftReference = null; break;
                case "deer":     cs.opacity = 0.55f; cs.spread = 1.45f; cs.lowerShare = 0.22f; cs.liftReference = null; break;
                case "hare":     cs.opacity = 0.62f; cs.spread = 1.5f; cs.lowerShare = 0.35f; cs.liftReference = Bone("spine_back"); cs.fadeHeight = 0.12f; break;
                case "owl":      cs.opacity = 0.58f; cs.spread = 1.5f; cs.lowerShare = 0.3f; cs.liftReference = spawn.transform; cs.fadeHeight = 0.5f; break;
            }
            EditorUtility.SetDirty(cs);

            var sun = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null)
            {
                sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.transform.rotation = Quaternion.Euler(64f, -32f, 0f);     // high, like a ceiling light
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;                                       // the catcher sets how dark
            var est = GetOrAdd<CameraLightEstimator>(sun.gameObject);
            est.shadowCatchers = new[] { catcherMat };
            EditorUtility.SetDirty(est);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.93f, 0.96f, 1f) * 0.53f;
            Debug.Log($"[AppUI] {a.arScene}: shadow catcher, contact shadow ({cs.opacity:F2}), room-matched light");
        }

        // -------------------------------------------------------------- audio
        const string AudioDir = "Assets/Audio";

        // GetComponent's missing-component "null" is not C# null in the editor, so ?? cannot be used here
        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static AudioClip Clip(string name)
        {
            var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioDir}/{name}.wav");
            if (c == null) Debug.LogWarning($"[AppUI] missing sound {AudioDir}/{name}.wav (run Tools/Audio/synth_animals.py)");
            return c;
        }

        /// <summary>
        /// Import settings for the generated sounds: mono, Vorbis. Short calls are
        /// decompressed on load (instant, no hitch when they fire); the 30 s
        /// ambience loops stay compressed in memory.
        /// </summary>
        static void ConfigureAudioImports()
        {
            if (!Directory.Exists(AudioDir)) { Debug.LogWarning("[AppUI] no " + AudioDir); return; }
            foreach (var path in Directory.GetFiles(AudioDir, "*.wav"))
            {
                var imp = AssetImporter.GetAtPath(path.Replace('\\', '/')) as AudioImporter;
                if (imp == null) continue;
                bool amb = Path.GetFileName(path).StartsWith("amb_");
                var st = imp.defaultSampleSettings;
                st.loadType = amb ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                st.compressionFormat = AudioCompressionFormat.Vorbis;
                st.quality = amb ? 0.5f : 0.7f;
                bool changed = !imp.forceToMono || imp.defaultSampleSettings.loadType != st.loadType ||
                               imp.defaultSampleSettings.compressionFormat != st.compressionFormat ||
                               !Mathf.Approximately(imp.defaultSampleSettings.quality, st.quality) || imp.loadInBackground != amb;
                if (!changed) continue;
                imp.forceToMono = true;
                imp.loadInBackground = amb;
                imp.defaultSampleSettings = st;
                imp.SaveAndReimport();
            }
        }

        /// <summary>
        /// Each animal's voice (idle calls, its open-palm sound, extra cues) on a 3D
        /// AudioSource on the animal, and the scene's ambience loop on an
        /// "Ambience" object. Re-run safe: settings are overwritten, nothing doubles up.
        /// </summary>
        static void SetupAudio(Species a, GroundSpawn spawn)
        {
            var go = spawn.gameObject;
            var src = GetOrAdd<AudioSource>(go);
            AnimalAudio.Configure(src);
            var voice = GetOrAdd<AnimalAudio>(go);
            AnimalAudio.Cue C(string n, string clip, float v) => new AnimalAudio.Cue { name = n, clip = Clip(clip), volume = v };
            string amb;
            switch (a.file)
            {
                case "squirrel":
                    voice.idleCalls = new[] { Clip("squirrel_chatter_1"), Clip("squirrel_chatter_2") };
                    voice.idleGap = new Vector2(7f, 14f); voice.idleVolume = 0.7f;
                    voice.gestureClips = new[] { Clip("squirrel_gesture") };
                    voice.cues = new[] { C("chatter", "squirrel_chatter_2", 0.6f),
                                         C("patter", "squirrel_patter_1", 0.8f), C("patter", "squirrel_patter_2", 0.8f), C("patter", "squirrel_patter_3", 0.8f) };
                    amb = "amb_forest"; break;
                case "fox":
                    voice.idleCalls = new[] { Clip("fox_bark_1"), Clip("fox_bark_2") };
                    voice.idleGap = new Vector2(12f, 22f); voice.idleVolume = 0.6f;     // barks carry; keep them rare
                    voice.gestureClips = new[] { Clip("fox_gesture") };
                    voice.cues = Enumerable.Range(1, 4).Select(i => C("step", $"snow_step_{i}", 0.9f)).ToArray();
                    amb = "amb_snowfield"; break;
                case "deer":
                    voice.idleCalls = new[] { Clip("deer_snort") };
                    voice.idleGap = new Vector2(10f, 18f); voice.idleVolume = 0.7f;
                    voice.gestureClips = new[] { Clip("deer_gesture") };
                    voice.cues = new[] { C("sniff", "deer_sniff", 0.8f) };
                    amb = "amb_forest"; break;
                case "hare":
                    voice.idleCalls = new[] { Clip("hare_sniff") };
                    voice.idleGap = new Vector2(6f, 12f); voice.idleVolume = 0.5f;       // hares are quiet
                    voice.gestureClips = new[] { Clip("hare_gesture") };
                    voice.cues = new[] { C("sniff", "hare_sniff", 0.6f), C("land", "hare_land_1", 1f), C("land", "hare_land_2", 1f) };
                    amb = "amb_snowfield"; break;
                case "owl":
                    voice.idleCalls = new[] { Clip("owl_hoot_1"), Clip("owl_hoot_2") };
                    voice.idleGap = new Vector2(9f, 16f); voice.idleVolume = 0.85f;
                    voice.gestureClips = new[] { Clip("owl_gesture") };
                    voice.cues = new AnimalAudio.Cue[0];
                    amb = "amb_night"; break;
                default: return;
            }
            // gesture-trick sounds: the startle call, munching, the trick, and the Say-cheese ticks
            var (startle, treat, specialClip) = a.file switch
            {
                "squirrel" => ("squirrel_alarm", "nibble_1", "squirrel_squeak"),
                "fox" => ("fox_yelp", "fox_snap", "pounce"),
                "deer" => ("deer_alarm", "nibble_1", "leap"),
                "hare" => ("hare_gesture", "nibble_2", "leap"),
                _ => ("owl_clack", "owl_gulp", "hoot_short"),
            };
            voice.cues = voice.cues.Where(c => c.name != "startle" && c.name != "treat" && c.name != "special" && c.name != "tick")
                .Concat(new[] { C("startle", startle, 1f), C("treat", treat, 0.9f), C("special", specialClip, 1f), C("tick", "game_tick", 0.8f) })
                .ToArray();
            voice.appearClip = Clip("snow_puff");
            voice.appearVolume = a.file == "deer" ? 0.6f : 0.45f;
            EditorUtility.SetDirty(voice);

            // The owl has no PalmReaction to trigger its sound: listen to the gestures directly
            var gm = UnityEngine.Object.FindFirstObjectByType<HandGestureManager>(FindObjectsInactive.Include);
            if (gm != null)
            {
                for (int i = gm.onGestureBegan.GetPersistentEventCount() - 1; i >= 0; i--)
                    if (gm.onGestureBegan.GetPersistentTarget(i) is AnimalAudio)
                        UnityEventTools.RemovePersistentListener(gm.onGestureBegan, i);
                if (spawn.GetComponent<PalmReaction>() == null)
                    UnityEventTools.AddPersistentListener(gm.onGestureBegan, voice.OnGestureBegan);
                EditorUtility.SetDirty(gm);
            }

            var ambGo = GameObject.Find("Ambience") ?? new GameObject("Ambience");
            ambGo.transform.SetParent(null, false);
            var asrc = GetOrAdd<AudioSource>(ambGo);
            SceneAmbience.Configure(asrc, Clip(amb));
            var sa = GetOrAdd<SceneAmbience>(ambGo);
            sa.volume = a.file == "owl" ? 0.5f : 0.4f;
            EditorUtility.SetDirty(sa);
            Debug.Log($"[AppUI] {a.arScene}: sound -- {voice.idleCalls.Length} idle call(s), gesture '{voice.gestureClips[0]?.name}', " +
                      $"{voice.cues.Length} cue(s), ambience '{amb}'");
        }

        /// <summary>
        /// The deer looks at you with its muzzle, so it needs to know which way the
        /// muzzle points inside Ctrl_Head: the head-mesh vertex furthest from the
        /// head's pivot, in the rest pose, measured here once.
        /// </summary>
        static void SetupDeerPalm(GroundSpawn spawn)
        {
            var palm = GetOrAdd<DeerPalm>(spawn.gameObject);
            var all = spawn.GetComponentsInChildren<Transform>(true);
            var head = all.FirstOrDefault(t => t.name == "Ctrl_Head");
            var mf = all.Where(t => t.name == "Deer_Head").Select(t => t.GetComponent<MeshFilter>()).FirstOrDefault(m => m != null);
            if (head == null || mf == null || mf.sharedMesh == null) { Debug.LogWarning("[AppUI] deer: no Ctrl_Head / Deer_Head to measure the muzzle"); return; }
            Vector3 best = Vector3.zero; float far = -1f;
            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 w = mf.transform.TransformPoint(v);
                float d = (w - head.position).sqrMagnitude;
                if (d > far) { far = d; best = w; }
            }
            palm.noseLocal = head.InverseTransformDirection(best - head.position).normalized;
            Vector3 model = spawn.transform.InverseTransformDirection(best - head.position).normalized;
            Debug.Log($"[AppUI] AR_Deer: DeerPalm, muzzle {Mathf.Sqrt(far):F3} from the head pivot, toward {model} in model space");
            EditorUtility.SetDirty(palm);
        }

        /// <summary>Render the owl at chosen moments of its flight, on a test floor.</summary>
        public static void PreviewFlight()
        {
            Prepare();
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.3f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.GetComponent<Renderer>().sharedMaterial.color = new Color(0.66f, 0.64f, 0.6f);

            var stage = new GameObject("Ground Plane Stage").transform;
            var owl = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(OwlFbx));
            owl.transform.SetParent(stage, false);
            AdaptForFloor(owl, "owl");
            var spawn = owl.AddComponent<GroundSpawn>();
            spawn.targetHeight = SpawnHeight("owl");
            SetupOwlFlight(owl, stage);
            var flight = owl.GetComponent<OwlFlight>();

            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.transform.position = new Vector3(0.9f, 1.0f, 3.1f);
            cam.transform.LookAt(new Vector3(0f, 0.72f, 0f));
            cam.fieldOfView = 38f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.8f, 0.84f, 0.88f);
            spawn.Spawn(stage, cam, instant: true);
            flight.BeginPreview();

            float T0 = flight.takeoffSeconds, C = flight.cycleSeconds;
            var moments = new[] {
                ("climb_up",   T0 + C * 0.25f, Mathf.PI * 0.5f),   // climbing, wings at top of stroke
                ("climb_down", T0 + C * 0.25f, Mathf.PI * 1.5f),   // climbing, bottom of stroke
                ("top",        T0 + C * 0.50f, Mathf.PI * 0.5f),   // highest point
                ("glide",      T0 + C * 0.75f, 0f),                // dropping: wings held
            };
            var rt = new RenderTexture(560, 700, 24);
            cam.targetTexture = rt;

            // The viewer: a phone held at eye height 2.2 m from the owl's spot
            var viewer = new GameObject("Viewer").AddComponent<Camera>();
            viewer.transform.position = new Vector3(-0.8f, 1.45f, 2.1f);
            viewer.transform.LookAt(new Vector3(0f, 0.7f, 0f));
            viewer.fieldOfView = 60f;                               // roughly a phone's view
            viewer.clearFlags = CameraClearFlags.SolidColor;
            viewer.backgroundColor = new Color(0.8f, 0.84f, 0.88f);
            viewer.targetTexture = rt;
            viewer.enabled = false;
            {
                var p = flight.Evaluate(T0 + C * 0.25f);
                flight.PreviewApproach(1f, viewer);
                p.effort = flight.hoverEffort;
                flight.Apply(p, Mathf.PI * 0.5f);
                // point the phone at the middle of the owl, as a person would
                viewer.transform.LookAt(spawn.MeshBounds().center);
                float dist = Vector3.Distance(viewer.transform.position,
                                              new Vector3(owl.transform.position.x, viewer.transform.position.y, owl.transform.position.z));
                var b = spawn.MeshBounds();
                Debug.Log($"[Flight] come_to_you horizontal distance from phone={dist:F2} m (want {flight.approachDistance}); " +
                          $"owl centre {(viewer.transform.position.y - b.center.y):F2} m below eye; feet off floor={b.min.y:F2} m");
                viewer.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(560, 700, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 560, 700), 0, 0); tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, "flight_come_to_you.png"), tex.EncodeToPNG());
                flight.PreviewApproach(0f, viewer);
            }

            foreach (var (label, ft, phase) in moments)
            {
                var p = flight.Evaluate(ft);
                flight.Apply(p, phase);
                var b = spawn.MeshBounds();
                Debug.Log($"[Flight] {label,-10} height={p.height:F2} m  effort={p.effort:F2}  wing lift={flight.LiftAt(p, phase):F1} deg  " +
                          $"feet off floor={b.min.y:F2} m");
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(560, 700, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 560, 700), 0, 0); tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, $"flight_{label}.png"), tex.EncodeToPNG());
            }
        }

        // --------------------------------------------------------------- fox
        /// <summary>
        /// Fox extras: the walk loop, fur on the fur mesh only, the circle walk, and
        /// its snow -- a soft-edged patch under the circle plus local snowfall, both
        /// revealed with the fox. Re-run safe: generated pieces are replaced, the
        /// FoxWalk/ShellFur components are only added if missing.
        /// </summary>
        static void SetupFox(GroundSpawn spawn, Transform stage, Scene scene)
        {
            GameObject fox = spawn.gameObject;
            var anim = fox.GetComponentInChildren<Animator>(true) ?? fox.AddComponent<Animator>();
            anim.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>(FoxController);
            anim.applyRootMotion = false;                 // FoxWalk moves it, not the clip

            var walk = fox.GetComponent<FoxWalk>() ?? fox.AddComponent<FoxWalk>();

            var furMesh = fox.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "Fox_Fur");
            if (furMesh != null && furMesh.GetComponent<ShellFur>() == null)
            {
                var fur = furMesh.gameObject.AddComponent<ShellFur>();
                fur.shellCount = 16;          // each shell is a skinned mesh; 16 keeps phones smooth
                fur.furLength = 0.035f;       // local units of the (unscaled) model
                fur.density = 260f;
                fur.bareBelow = 0.02f;        // the legs are near-black; the default 0.07 skips them
            }
            // short fur on the face and ears, from the skin-weight mask baked on export
            var furComp = furMesh != null ? furMesh.GetComponent<ShellFur>() : null;
            if (furComp != null) furComp.lengthFromVertexColor = true;

            // snow: patch + snowfall, hidden until the fox appears
            foreach (var n in new[] { "SnowPatch", "FoxSnowfall" })
            {
                var old = stage.Find(n);
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            }
            var patch = MakeSnowPatch(stage, walk.radius + 0.45f);
            var fall = MakeSnowfall(stage, scene, walk.radius + 0.5f);
            patch.SetActive(false);
            if (fall != null) fall.SetActive(false);
            spawn.revealWith = fall != null ? new[] { patch, fall } : new[] { patch };
            Debug.Log("[AppUI] AR_Fox: walk loop, fur, circle walk and snow set up");
        }

        // -------------------------------------------------------------- hare
        /// <summary>
        /// Hare extras: the hop clip, fur on the fur mesh, the travel curve from
        /// export, and its ground -- the bark chips cut from the Blender scene over a
        /// soft dark-soil patch, like the book photo. Re-run safe.
        /// </summary>
        static void SetupHare(GroundSpawn spawn, Transform stage, Scene scene)
        {
            GameObject hare = spawn.gameObject;
            var anim = hare.GetComponentInChildren<Animator>(true) ?? hare.AddComponent<Animator>();
            anim.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>(HareController);
            anim.applyRootMotion = false;                 // HareHop moves it, from the recorded travel

            var hop = hare.GetComponent<HareHop>() ?? hare.AddComponent<HareHop>();
            var travel = AssetDatabase.LoadAssetAtPath<TextAsset>(HareTravel);
            if (travel == null) throw new Exception("missing " + HareTravel);
            var data = JsonUtility.FromJson<TravelData>(travel.text);
            hop.metres = data.metres;
            hop.total = data.total;

            var furMesh = hare.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "Hare_Fur");
            if (furMesh != null && furMesh.GetComponent<ShellFur>() == null)
            {
                var fur = furMesh.gameObject.AddComponent<ShellFur>();
                fur.shellCount = 16;
                fur.furLength = 0.04f;        // a dense winter coat
                fur.density = 300f;
            }
            // short fur on the face and ears, from the skin-weight mask baked on export
            var furComp = furMesh != null ? furMesh.GetComponent<ShellFur>() : null;
            if (furComp != null) furComp.lengthFromVertexColor = true;

            foreach (var n in new[] { "HareSoil", "HareGround" })
            {
                var old = stage.Find(n);
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
            }
            // chips were cut at the hare's authored size; scale them with the hare
            float scale = spawn.targetHeight / NativeHeight(HareFbx);
            var soil = MakeGroundPatch(stage, 0.62f, "HareSoil", new Color(0.33f, 0.18f, 0.13f, 1f), 0.05f,
                                       GenDir + "/hare_soil.png", GenDir + "/HareSoil.mat", GenDir + "/HareSoil.asset", warm: true);
            var chips = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HareGroundFbx), scene);
            chips.name = "HareGround";
            chips.transform.SetParent(stage, false);
            chips.transform.localScale = Vector3.one * scale;
            soil.SetActive(false); chips.SetActive(false);
            spawn.revealWith = new[] { soil, chips };
            Debug.Log($"[AppUI] AR_Hare: hop clip, fur, travel ({hop.total:F2} m/loop), bark chips at x{scale:F2}");
        }

        [Serializable] class TravelData { public float[] metres; public float total; }

        /// <summary>Height of a model at import scale, measured from its posed mesh.</summary>
        static float NativeHeight(string fbx)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(fbx));
            var probe = go.AddComponent<GroundSpawn>();
            float h = probe.MeshBounds().size.y;
            UnityEngine.Object.DestroyImmediate(go);
            return h > 1e-4f ? h : 1f;
        }

        const string SnowTexPath = GenDir + "/snow_patch.png";
        const string SnowMatPath = GenDir + "/SnowPatch.mat";
        const string SnowMeshPath = GenDir + "/SnowPatch.asset";

        /// <summary>
        /// A soft-edged disc of snow on the floor. The edge fades out through an
        /// irregular, noisy alpha so it reads as snow lying on the ground rather
        /// than a white plate. Transparent URP Lit, so it still takes the fox's shadow.
        /// </summary>
        static GameObject MakeSnowPatch(Transform stage, float radius) =>
            MakeGroundPatch(stage, radius, "SnowPatch", new Color(0.97f, 0.98f, 1f, 1f), 0.35f,
                            SnowTexPath, SnowMatPath, SnowMeshPath, warm: false);

        static GameObject MakeGroundPatch(Transform stage, float radius, string name, Color tint, float smoothness,
                                          string texPath, string matPath, string meshPath, bool warm)
        {
            const int N = 512;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color32[N * N];
            var rng = new System.Random(7);
            float[] wobble = Enumerable.Range(0, 16).Select(_ => (float)rng.NextDouble()).ToArray();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + .5f) / N * 2f - 1f, v = (y + .5f) / N * 2f - 1f;
                    float d = Mathf.Sqrt(u * u + v * v);
                    float ang = Mathf.Atan2(v, u);
                    // lumpy outline: a few low-frequency waves on the radius
                    float edge = 0.78f;
                    for (int k = 0; k < 4; k++)
                        edge += 0.045f * Mathf.Sin(ang * (2 + k) + wobble[k] * 6.28f) * (0.6f + wobble[k + 4]);
                    float a = Mathf.Clamp01((edge - d) / 0.2f);
                    a = a * a * (3f - 2f * a);
                    float grain = 0.93f + 0.07f * Mathf.PerlinNoise(x * 0.06f, y * 0.06f);
                    byte c = (byte)Mathf.RoundToInt(255f * grain);
                    byte blue = warm ? c : (byte)Mathf.Min(255, c + 4);   // snow skews cool, soil does not
                    px[y * N + x] = new Color32(c, c, blue, (byte)Mathf.RoundToInt(a * 255f));
                }
            tex.SetPixels32(px);
            File.WriteAllBytes(texPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.textureType = TextureImporterType.Default;
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = true;
            ti.SaveAndReimport();
            var snowTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            // gently domed disc, a few millimetres high, so paws sink in a touch
            var mesh = new Mesh { name = name };
            const int rings = 10, segs = 72;
            var verts = new System.Collections.Generic.List<Vector3> { new Vector3(0f, 0.008f, 0f) };
            var uvs = new System.Collections.Generic.List<Vector2> { new Vector2(0.5f, 0.5f) };
            for (int r = 1; r <= rings; r++)
                for (int sgi = 0; sgi < segs; sgi++)
                {
                    float t = r / (float)rings, ang = sgi / (float)segs * Mathf.PI * 2f;
                    float h = 0.008f * (1f - t * t) + 0.002f * Mathf.PerlinNoise(r * 0.7f, sgi * 0.3f);
                    verts.Add(new Vector3(Mathf.Cos(ang) * radius * t, h, Mathf.Sin(ang) * radius * t));
                    uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(ang) * t, 0.5f + 0.5f * Mathf.Sin(ang) * t));
                }
            var tris = new System.Collections.Generic.List<int>();
            for (int sgi = 0; sgi < segs; sgi++) { tris.Add(0); tris.Add(1 + (sgi + 1) % segs); tris.Add(1 + sgi); }
            for (int r = 1; r < rings; r++)
                for (int sgi = 0; sgi < segs; sgi++)
                {
                    int a0 = 1 + (r - 1) * segs + sgi, a1 = 1 + (r - 1) * segs + (sgi + 1) % segs;
                    int b0 = 1 + r * segs + sgi, b1 = 1 + r * segs + (sgi + 1) % segs;
                    tris.AddRange(new[] { a0, a1, b1, a0, b1, b0 });
                }
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetTexture("_BaseMap", snowTex);
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Surface", 1f);                                  // transparent
            mat.SetFloat("_Blend", 0f);                                    // alpha blend
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // URP keeps specular at zero alpha by default ("Preserve Specular Lighting"),
            // which lit the whole invisible disc as a pale halo past the snow's edge.
            mat.SetFloat("_BlendModePreserveSpecular", 0f);
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_ALPHAMODULATE_ON");
            mat.SetFloat("_SpecularHighlights", 0f);
            mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            mat.SetFloat("_EnvironmentReflections", 0f);
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);

            var go = new GameObject(name);
            go.transform.SetParent(stage, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            return go;
        }

        /// <summary>
        /// Local snowfall over the patch, reusing the Snowfall component and flake
        /// material the squirrel already ships with in SampleScene.
        /// </summary>
        static GameObject MakeSnowfall(Transform stage, Scene scene, float halfWidth)
        {
            var src = EditorSceneManager.OpenScene(ARPath, OpenSceneMode.Additive);
            var tmpl = src.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Snowfall>(true)).FirstOrDefault();
            Material flakeMat = tmpl != null ? tmpl.material : null;
            Mesh flakeMesh = tmpl != null ? tmpl.flakeMesh : null;
            EditorSceneManager.CloseScene(src, true);
            if (flakeMat == null) { Debug.LogWarning("[AppUI] no Snowfall material to reuse; fox gets no snowfall"); return null; }

            var go = new GameObject("FoxSnowfall");
            go.transform.SetParent(stage, false);
            go.AddComponent<ParticleSystem>();
            var sf = go.AddComponent<Snowfall>();
            sf.area = new Vector2(halfWidth * 2f, halfWidth * 2f);
            sf.spawnHeight = 1.5f;
            sf.fallDepth = 0.02f;
            sf.flakesPerSecond = 40f;
            sf.fallSpeed = 0.3f;
            sf.flakeSize = new Vector2(0.006f, 0.014f);
            sf.drift = 0.08f;
            sf.material = flakeMat;
            sf.flakeMesh = flakeMesh;
            sf.Configure();
            return go;
        }

        /// <summary>
        /// Copy the Plane Finder and Ground Plane Stage in from SampleScene if this
        /// scene lacks them. SampleScene is opened additively and closed without
        /// saving, so it is never modified.
        /// </summary>
        static void EnsurePlaneFinder(Scene scene)
        {
            if (GameObject.Find("Plane Finder") != null && GameObject.Find("Ground Plane Stage") != null) return;

            var src = EditorSceneManager.OpenScene(ARPath, OpenSceneMode.Additive);
            GameObject Root(string n) => src.GetRootGameObjects().First(g => g.name == n);
            var pf = UnityEngine.Object.Instantiate(Root("Plane Finder"));
            var st = UnityEngine.Object.Instantiate(Root("Ground Plane Stage"));
            pf.name = "Plane Finder"; st.name = "Ground Plane Stage";
            SceneManager.MoveGameObjectToScene(pf, scene);
            SceneManager.MoveGameObjectToScene(st, scene);
            EditorSceneManager.CloseScene(src, true);

            // The clone's placement still points at SampleScene's stage; repoint it.
            // (Its tap-to-place listener targets its own component, which cloning remaps.)
            var positioning = pf.GetComponent<ContentPositioningBehaviour>();
            positioning.AnchorStage = st.GetComponent<AnchorBehaviour>();
            Debug.Log("[AppUI] " + scene.name + ": added Plane Finder + Ground Plane Stage");
        }

        /// <summary>Three short field-guide facts per animal, for the HUD's fact card.</summary>
        static string[] Facts(string file) => file switch
        {
            "squirrel" => new[] {
                "It doesn't hibernate. All winter it lives on cones it stored in a midden, a pile that can hold thousands.",
                "It weighs only about 200 g, roughly as much as an apple.",
                "It chatters and stamps its feet to warn other squirrels off its patch." },
            "fox" => new[] {
                "It can hear a mouse moving under a metre of snow, then pounces nose-first to catch it.",
                "It sleeps with its bushy tail wrapped over its nose, like a scarf.",
                "It looks big in its winter coat, but usually weighs just 4\u20137 kg." },
            "deer" => new[] {
                "It raises its white tail like a flag to warn other deer of danger.",
                "Its winter coat is made of hollow hairs that trap warm air.",
                "In deep snow, deer gather in sheltered \u2018yards\u2019 and share packed-down trails." },
            "hare" => new[] {
                "In the far north it stays white all year round.",
                "It can sprint at up to 60 km/h, sometimes hopping upright on its hind legs.",
                "Thick fur even covers the soles of its feet, like built-in snowshoes." },
            "owl" => new[] {
                "It hunts in daylight, handy in the Arctic summer when the sun never sets.",
                "Feathers cover its legs and toes, like a pair of warm slippers.",
                "A single snowy owl may eat more than 1,600 lemmings in a year." },
            _ => new string[0],
        };

        /// <summary>The field card's three quick stats.</summary>
        static (string, string)[] Stats(string file)
        {
            float h = SpawnHeight(file);
            string height = h >= 1f ? $"{h:0.#} m" : $"{Mathf.RoundToInt(h * 100f)} cm";
            var (weight, eats) = file switch
            {
                "squirrel" => ("200 g", "Cones, seeds"),
                "fox" => ("4\u20137 kg", "Mice, voles"),
                "deer" => ("40\u201390 kg", "Twigs, buds"),
                "hare" => ("3\u20135 kg", "Willow, moss"),
                "owl" => ("1.6\u20133 kg", "Lemmings"),
                _ => ("", ""),
            };
            return new[] { ("HEIGHT", height), ("WEIGHT", weight), ("EATS", eats) };
        }

        /// <summary>What the Point gesture makes each animal do.</summary>
        static string SpecialName(string file) => file switch
        {
            "squirrel" => "Spin", "fox" => "Pounce", "deer" => "Leap", "hare" => "Binky", "owl" => "Head turn", _ => "Trick",
        };

        static string GameTitle(string file) => file switch
        {
            "squirrel" => "Nut Stash", "fox" => "Mouse Hunt", "deer" => "Freeze!", "hare" => "Snow Hide", "owl" => "Hoot Echo", _ => "",
        };

        static string GameLength(string file) => file switch
        {
            "squirrel" => "30 seconds", "fox" => "5 rounds", "deer" => "45 seconds", "hare" => "5 rounds", "owl" => "Until a slip", _ => "",
        };

        static string SizeNote(string file)
        {
            float h = SpawnHeight(file);
            string size = h >= 1f ? $"{h:0.#} m" : $"{Mathf.RoundToInt(h * 100f)} cm";
            return $"Shown at life size \u00b7 about {size} tall";
        }

        /// <summary>
        /// The minigame layer on the HUD: top bar (game, round/timer, score), hint
        /// card with Quit, a score pop-up, two choice buttons, and the results card.
        /// </summary>
        static MiniGameHost BuildGameUI(Canvas canvas, RectTransform safe)
        {
            var host = canvas.gameObject.AddComponent<MiniGameHost>();

            // top bar, under Back / Mute
            var bar = Node("GameBar", safe);
            bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
            bar.offsetMin = new Vector2(Margin - 16f, -282f); bar.offsetMax = new Vector2(-(Margin - 16f), -150f);
            var bh = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            bh.spacing = 24f; bh.childAlignment = TextAnchor.MiddleLeft;
            bh.childControlWidth = bh.childControlHeight = true;
            bh.childForceExpandWidth = false; bh.childForceExpandHeight = true;
            var barGroup = bar.gameObject.AddComponent<CanvasGroup>();
            barGroup.blocksRaycasts = false;
            RectTransform BarPill(string name, Color bg, out HorizontalLayoutGroup h)
            {
                var r = Node(name, bar);
                LayoutBackground(r, 2.2f, bg);
                h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
                h.padding = new RectOffset(42, 42, 0, 0); h.spacing = 18f; h.childAlignment = TextAnchor.MiddleCenter;
                h.childControlWidth = h.childControlHeight = true;
                h.childForceExpandWidth = false; h.childForceExpandHeight = false;
                r.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                return r;
            }
            var tp = BarPill("Game", WithA(Night, 0.62f), out _);
            FixedIcon(tp, "Dot", _circle, Rust, 24f);
            var titleText = Text(tp, "Title", "Mouse Hunt", 42, Snow, FontStyles.Bold);
            var spacer = Node("Spacer", bar); spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var rp = BarPill("Round", WithA(Night, 0.62f), out _);
            var roundLabel = Text(rp, "Label", "ROUND", 31, Lichen, FontStyles.Bold, spacing: 3f);
            var roundText = Text(rp, "Value", "1/5", 45, Snow, FontStyles.Bold);
            var sp = BarPill("Score", Snow, out _);
            Text(sp, "Label", "SCORE", 31, Hex("4A5A60"), FontStyles.Bold, spacing: 3f);
            var scoreText = Text(sp, "Value", "0", 45, Night, FontStyles.Bold);

            // hint card + Quit, bottom
            var hint = Node("GameHint", safe);
            hint.anchorMin = new Vector2(0f, 0f); hint.anchorMax = new Vector2(1f, 0f); hint.pivot = new Vector2(0.5f, 0f);
            hint.offsetMin = new Vector2(Margin, 60f); hint.offsetMax = new Vector2(-Margin, 60f);
            var hv = hint.gameObject.AddComponent<VerticalLayoutGroup>();
            hv.spacing = 34f; hv.childAlignment = TextAnchor.LowerCenter;
            hv.childControlWidth = hv.childControlHeight = true;
            hv.childForceExpandWidth = false; hv.childForceExpandHeight = false;
            hint.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var hintGroup = hint.gameObject.AddComponent<CanvasGroup>();
            var hcard = Node("Card", hint);
            LayoutBackground(hcard, 1.9f, WithA(Night, 0.72f));
            var hcv = hcard.gameObject.AddComponent<VerticalLayoutGroup>();
            hcv.padding = new RectOffset(54, 54, 40, 44); hcv.spacing = 8f;
            hcv.childControlWidth = hcv.childControlHeight = true;
            hcv.childForceExpandWidth = true; hcv.childForceExpandHeight = false;
            hcard.gameObject.AddComponent<LayoutElement>().preferredWidth = 900f;
            var hintTitle = Text(hcard, "Title", "Listen\u2026", 45, Snow, FontStyles.Bold);
            var hintBody = Text(hcard, "Body", "Tap the snow where you hear the mouse squeak.", 39, WithA(Snow, 0.82f), FontStyles.Normal, lineSpacing: 6f);
            hintBody.textWrappingMode = TextWrappingModes.Normal;
            hintTitle.raycastTarget = hintBody.raycastTarget = false;
            var quit = PillButton(hint, "Quit", "Quit game", WithA(Night, 0.55f), Snow, height: 120f, glow: false, labelSize: 39f, ppu: 2.1f);
            var qle = quit.gameObject.AddComponent<LayoutElement>();
            qle.preferredWidth = 330f; qle.preferredHeight = 120f;

            // choices (Hoot Echo), above the hint card
            var choices = Node("GameChoices", safe);
            choices.anchorMin = new Vector2(0f, 0f); choices.anchorMax = new Vector2(1f, 0f); choices.pivot = new Vector2(0.5f, 0f);
            choices.offsetMin = new Vector2(Margin, 560f); choices.offsetMax = new Vector2(-Margin, 740f);
            var chh = choices.gameObject.AddComponent<HorizontalLayoutGroup>();
            chh.spacing = 36f; chh.childControlWidth = chh.childControlHeight = true;
            chh.childForceExpandWidth = true; chh.childForceExpandHeight = true;
            var choiceGroup = choices.gameObject.AddComponent<CanvasGroup>();
            var cA = PillButton(choices, "A", "Short", Snow, Night, height: 180f, glow: false, labelSize: 54f, ppu: 1.4f);
            var cB = PillButton(choices, "B", "Long", Rust, RustInk, height: 180f, glow: true, labelSize: 54f, ppu: 1.4f);

            // score pop-up
            var pop = Node("GamePopup", safe);
            pop.anchorMin = pop.anchorMax = pop.pivot = new Vector2(0.5f, 0.5f);
            pop.anchoredPosition = new Vector2(0f, 330f);
            var popBg = LayoutBackground(pop, 2.4f, Success);
            var ph = pop.gameObject.AddComponent<HorizontalLayoutGroup>();
            ph.padding = new RectOffset(48, 48, 26, 28); ph.childAlignment = TextAnchor.MiddleCenter;
            ph.childControlWidth = ph.childControlHeight = true;
            var pcf = pop.gameObject.AddComponent<ContentSizeFitter>();
            pcf.horizontalFit = pcf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var popText = Text(pop, "Text", "+100 Perfect pounce!", 46, Hex("06210F"), FontStyles.Bold);
            popText.textWrappingMode = TextWrappingModes.NoWrap;
            var popGroup = pop.gameObject.AddComponent<CanvasGroup>();
            popGroup.blocksRaycasts = false;

            // results
            var res = Node("GameResults", safe);
            res.anchorMin = new Vector2(0f, 0.5f); res.anchorMax = new Vector2(1f, 0.5f); res.pivot = new Vector2(0.5f, 0.5f);
            res.offsetMin = new Vector2(60f, 0f); res.offsetMax = new Vector2(-60f, 0f);
            LayoutBackground(res, 1.4f, WithA(Hex("080C0D"), 0.94f));
            var rv = res.gameObject.AddComponent<VerticalLayoutGroup>();
            rv.padding = new RectOffset(72, 72, 84, 66); rv.spacing = 40f; rv.childAlignment = TextAnchor.UpperCenter;
            rv.childControlWidth = rv.childControlHeight = true;
            rv.childForceExpandWidth = false; rv.childForceExpandHeight = false;
            res.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var resGroup = res.gameObject.AddComponent<CanvasGroup>();
            var rGame = Text(res, "Game", "MOUSE HUNT", 33, Lichen, FontStyles.Bold, spacing: 6f);
            var starRow = Node("Stars", res);
            var srh = starRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            srh.spacing = 18f; srh.childAlignment = TextAnchor.MiddleCenter;
            srh.childControlWidth = srh.childControlHeight = true;
            srh.childForceExpandWidth = srh.childForceExpandHeight = false;
            var starImgs = new[] { FixedIcon(starRow, "S1", _starOn, Rust, 102f), FixedIcon(starRow, "S2", _starOn, Rust, 120f), FixedIcon(starRow, "S3", _starOff, Rust, 102f) };
            var rVerdict = Text(res, "Verdict", "Sharp ears!", 84, Snow, FontStyles.Bold, spacing: -1f);
            rVerdict.alignment = TextAlignmentOptions.Center;
            var scoreRow = Node("ScoreRow", res);
            var srow = scoreRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            srow.spacing = 18f; srow.childAlignment = TextAnchor.LowerCenter;
            srow.childControlWidth = srow.childControlHeight = true;
            srow.childForceExpandWidth = srow.childForceExpandHeight = false;
            var rScore = Text(scoreRow, "Score", "340", 168, Snow, FontStyles.Bold, spacing: -2f);
            var rUnit = Text(scoreRow, "Unit", "points", 42, Lichen, FontStyles.Bold);
            var bestChip = Node("Best", res);
            LayoutBackground(bestChip, 3f, WithA(Success, 0.16f));
            var bch = bestChip.gameObject.AddComponent<HorizontalLayoutGroup>();
            bch.padding = new RectOffset(42, 42, 18, 20);
            bch.childControlWidth = bch.childControlHeight = true;
            bestChip.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            var rBest = Text(bestChip, "Text", "New best!", 36, Success, FontStyles.Bold);
            var rDetail = Text(res, "Detail", "100 \u00b7 50 \u00b7 100 \u00b7 0 \u00b7 90", 38, Lichen, FontStyles.Normal);
            rDetail.alignment = TextAlignmentOptions.Center;
            var btns = Node("Buttons", res);
            btns.gameObject.AddComponent<LayoutElement>().preferredWidth = RefW - 120f - 144f;
            var bh2 = btns.gameObject.AddComponent<HorizontalLayoutGroup>();
            bh2.spacing = 30f; bh2.childControlWidth = bh2.childControlHeight = true;
            bh2.childForceExpandWidth = true; bh2.childForceExpandHeight = true;
            var done = PillButton(btns, "Done", "Done", WithA(Snow, 0.1f), Snow, height: 156f, glow: false, labelSize: 45f, ppu: 1.4f);
            done.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var again = PillButton(btns, "Again", "Play again", Rust, RustInk, height: 156f, glow: false, labelSize: 45f, ppu: 1.4f);
            again.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.4f;
            foreach (var b in new[] { done, again }) b.gameObject.GetComponent<LayoutElement>().preferredHeight = 156f;

            foreach (var g in new[] { barGroup, hintGroup, choiceGroup, popGroup, resGroup }) { g.alpha = 0f; g.blocksRaycasts = false; g.interactable = false; }

            host.gameHud = barGroup;
            host.titleText = titleText; host.roundLabel = roundLabel; host.roundText = roundText; host.scoreText = scoreText;
            host.hintGroup = hintGroup; host.hintTitle = hintTitle; host.hintBody = hintBody; host.quitButton = quit;
            host.popup = popGroup; host.popupText = popText; host.popupBg = popBg;
            host.choiceGroup = choiceGroup; host.choiceA = cA; host.choiceB = cB;
            host.choiceALabel = cA.GetComponentInChildren<TMP_Text>(); host.choiceBLabel = cB.GetComponentInChildren<TMP_Text>();
            host.results = resGroup; host.resultsGame = rGame; host.resultsVerdict = rVerdict; host.resultsScore = rScore;
            host.resultsUnit = rUnit; host.resultsBest = rBest; host.resultsDetail = rDetail;
            host.stars = starImgs; host.starOn = _starOn; host.starOff = _starOff;
            host.doneButton = done; host.againButton = again;
            host.good = Success; host.ok = Rust; host.bad = Hex("8E989C");
            host.tick = Clip("game_tick"); host.go = Clip("game_go"); host.point = Clip("game_point"); host.great = Clip("game_great");
            host.miss = Clip("game_miss"); host.win = Clip("game_win"); host.over = Clip("game_over");
            return host;
        }

        static Button IconButton(RectTransform parent, string name, Sprite icon, float size, float iconSize, out Image iconImg)
        {
            Button b = PillButton(parent, name, "", WithA(Night, 0.5f), Snow, height: size, glow: false, labelSize: 10f, ppu: 2.1f);
            var rt = (RectTransform)b.transform;
            rt.sizeDelta = new Vector2(size, size);
            iconImg = Img(Node("Icon", rt), icon, Snow);
            iconImg.rectTransform.anchorMin = iconImg.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            iconImg.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
            iconImg.raycastTarget = false;
            return b;
        }

        static RectTransform Pill(RectTransform parent, string name, Vector2 anchor, float y, float alpha, out CanvasGroup group,
                                  int padX = 40, int padY = 22, float spacing = 16f)
        {
            var rt = Node(name, parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = new Vector2(0f, y);
            LayoutBackground(rt, 2.2f, WithA(Night, alpha));
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(padX, padX + 4, padY, padY); h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var cf = rt.gameObject.AddComponent<ContentSizeFitter>();
            cf.horizontalFit = cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            group = rt.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = false;
            return rt;
        }

        static Image FixedIcon(RectTransform parent, string name, Sprite sprite, Color color, float size)
        {
            var img = Img(Node(name, parent), sprite, color);
            var le = img.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = le.minWidth = le.minHeight = size;
            img.raycastTarget = false;
            return img;
        }

        static void BuildARHud(Species a)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == "ARHud"))
                UnityEngine.Object.DestroyImmediate(old);

            // Above ScanUI (100), so Back stays reachable through every phase
            Canvas canvas = MakeCanvas("ARHud", 110);
            Transform root = canvas.transform;
            var safe = Stretch(Node("SafeArea", root));
            safe.gameObject.AddComponent<SafeAreaFitter>();
            Button back = BackButton(safe);

            // sound on/off, top right, level with Back
            Button mute = IconButton(safe, "Mute", _soundOn, 92f, 54f, out Image muteIcon);
            var mrt = (RectTransform)mute.transform;
            mrt.anchorMin = mrt.anchorMax = mrt.pivot = new Vector2(1f, 1f);
            mrt.anchoredPosition = new Vector2(-(Margin - 16f), -28f);

            // Life size | Small, top right next to Mute
            var size = Node("SizeToggle", safe);
            size.anchorMin = size.anchorMax = size.pivot = new Vector2(1f, 1f);
            size.sizeDelta = new Vector2(400f, 92f);
            size.anchoredPosition = new Vector2(-(Margin - 16f) - 92f - 20f, -28f);
            var sizeBg = Img(Stretch(Node("Fill", size)), _round, WithA(Night, 0.5f), sliced: true, ppu: 2.1f);
            var sizeBtn = size.gameObject.AddComponent<Button>();
            sizeBtn.targetGraphic = sizeBg;
            var hlHolder = Stretch(Node("Track", size));
            hlHolder.offsetMin = new Vector2(6f, 6f); hlHolder.offsetMax = new Vector2(-6f, -6f);
            var hl = Img(Node("Highlight", hlHolder), _round, Snow, sliced: true, ppu: 2.4f);
            hl.raycastTarget = false;
            hl.rectTransform.anchorMin = new Vector2(0f, 0f); hl.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            hl.rectTransform.offsetMin = hl.rectTransform.offsetMax = Vector2.zero;
            TMP_Text Half(string nm, string label, float x0, float x1)
            {
                var t = Text(size, nm, label, 32, Snow, FontStyles.Bold);
                t.rectTransform.anchorMin = new Vector2(x0, 0f); t.rectTransform.anchorMax = new Vector2(x1, 1f);
                t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
                t.alignment = TextAlignmentOptions.Center;
                t.raycastTarget = false;
                return t;
            }
            var lifeLabel = Half("Life", "Life size", 0f, 0.5f);
            lifeLabel.color = Night;                       // selected by default
            var smallLabel = Half("Small", "Small", 0.5f, 1f);
            var sizeGroup = size.gameObject.AddComponent<CanvasGroup>();

            // the shutter, bottom right: white ring round a white disc
            var shutter = Node("Shutter", safe);
            shutter.anchorMin = shutter.anchorMax = shutter.pivot = new Vector2(1f, 0f);
            shutter.sizeDelta = new Vector2(136f, 136f);
            shutter.anchoredPosition = new Vector2(-(Margin - 16f), 64f);
            var sHalo = Img(Node("Halo", shutter), _shadow, WithA(Night, 0.45f), sliced: true);
            Anchor(sHalo.rectTransform, 0, 0, 1, 1);
            sHalo.rectTransform.offsetMin = new Vector2(-34f, -40f); sHalo.rectTransform.offsetMax = new Vector2(34f, 28f);
            sHalo.raycastTarget = false;
            var ring = Img(Stretch(Node("Ring", shutter)), _ringThick, Snow);
            var disc = Img(Node("Disc", shutter), _circle, WithA(Snow, 0.92f));
            disc.rectTransform.anchorMin = disc.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            disc.rectTransform.sizeDelta = new Vector2(100f, 100f);
            var shutterBtn = shutter.gameObject.AddComponent<Button>();
            shutterBtn.targetGraphic = disc;
            ring.raycastTarget = false;
            var shutterGroup = shutter.gameObject.AddComponent<CanvasGroup>();

            // after a shot: thumbnail + result, top centre under the tips row
            var shot = Pill(safe, "PhotoSaved", new Vector2(0.5f, 1f), -262f, 0.72f, out CanvasGroup shotGroup, 22, 18, 22f);
            var thumbFrame = Node("Thumb", shot);
            var tle = thumbFrame.gameObject.AddComponent<LayoutElement>();
            tle.preferredWidth = tle.minWidth = 84f; tle.preferredHeight = tle.minHeight = 150f;
            var tMask = Img(Stretch(Node("Mask", thumbFrame)), _round, Color.white, sliced: true, ppu: 6f);
            tMask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var thumb = Img(Stretch(Node("Image", tMask.rectTransform)), null, Color.white);
            thumb.raycastTarget = false;
            var shotText = Text(shot, "Label", "Saved to Photos", 34, Snow, FontStyles.Bold);
            shotText.textWrappingMode = TextWrappingModes.NoWrap;
            shotGroup.alpha = 0f;

            // tracking tips, under the top row
            var tip = Pill(safe, "TrackingTip", new Vector2(0.5f, 1f), -150f, 0.7f, out CanvasGroup tipGroup);
            var tipText = Text(tip, "Label", "Move your phone a little slower", 34, Snow, FontStyles.Bold);
            tipText.textWrappingMode = TextWrappingModes.NoWrap;

            // shown once the animal is down: what it is; tap for the fact card
            var chip = Node("Placed", safe);
            chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(0.5f, 0f);
            chip.anchoredPosition = new Vector2(0f, 72f);
            var chipBg = LayoutBackground(chip, 2.2f, WithA(Night, 0.62f));
            chipBg.raycastTarget = true;
            var chipBtn = chip.gameObject.AddComponent<Button>();
            chipBtn.targetGraphic = chipBg;
            var v = chip.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(52, 52, 26, 30); v.spacing = 4f;
            v.childAlignment = TextAnchor.MiddleCenter;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            var cf = chip.gameObject.AddComponent<ContentSizeFitter>();
            cf.horizontalFit = cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var placedGroup = chip.gameObject.AddComponent<CanvasGroup>();
            placedGroup.blocksRaycasts = false;
            var n = Text(chip, "Name", a.common, 40, Snow, FontStyles.Bold);
            n.alignment = TextAlignmentOptions.Center;
            var h = Text(chip, "Hint", "Tap for facts \u00b7 tap the floor to move it", 30, WithA(Snow, 0.7f), FontStyles.Normal);
            h.alignment = TextAlignmentOptions.Center;
            h.raycastTarget = false; n.raycastTarget = false;

            // teaching the gesture: a card above the chip
            var coach = Node("PalmCoach", safe);
            coach.anchorMin = coach.anchorMax = coach.pivot = new Vector2(0.5f, 0f);
            coach.anchoredPosition = new Vector2(0f, 262f);
            LayoutBackground(coach, 1.9f, WithA(Night, 0.66f));
            var ch = coach.gameObject.AddComponent<HorizontalLayoutGroup>();
            ch.padding = new RectOffset(40, 48, 32, 34); ch.spacing = 30f;
            ch.childAlignment = TextAnchor.MiddleLeft;
            ch.childControlWidth = ch.childControlHeight = true;
            ch.childForceExpandWidth = ch.childForceExpandHeight = false;
            var ccf = coach.gameObject.AddComponent<ContentSizeFitter>();
            ccf.horizontalFit = ccf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var coachGroup = coach.gameObject.AddComponent<CanvasGroup>();
            coachGroup.blocksRaycasts = false; coachGroup.interactable = false;
            FixedIcon(coach, "Palm", _palmIcon, Rust, 96f);
            var words = Node("Words", coach);
            var wv = words.gameObject.AddComponent<VerticalLayoutGroup>();
            wv.spacing = 6f; wv.childControlWidth = wv.childControlHeight = true;
            wv.childForceExpandWidth = wv.childForceExpandHeight = false;
            words.gameObject.AddComponent<LayoutElement>().preferredWidth = 720f;
            Text(words, "Title", "Say hello", 40, Snow, FontStyles.Bold);
            var cs = Text(words, "Body", $"Hold up an open palm to call the {a.common.ToLowerInvariant()} over.", 33,
                          WithA(Snow, 0.78f), FontStyles.Normal, lineSpacing: 6f);
            cs.textWrappingMode = TextWrappingModes.Normal;

            // live feedback while a hand is in view (same spot as the coach)
            var palm = Pill(safe, "PalmFeedback", new Vector2(0.5f, 0f), 262f, 0.7f, out CanvasGroup palmGroup, 36, 20, 18f);
            FixedIcon(palm, "Palm", _palmIcon, Rust, 54f);
            var palmText = Text(palm, "Label", $"Calling the {a.common.ToLowerInvariant()}\u2026", 34, Snow, FontStyles.Bold);
            palmText.textWrappingMode = TextWrappingModes.NoWrap;

            // the field card, in the chip's place when open: who it is, a few facts,
            // the gestures to try, and its minigame
            var card = Node("FactCard", safe);
            card.anchorMin = new Vector2(0f, 0f); card.anchorMax = new Vector2(1f, 0f); card.pivot = new Vector2(0.5f, 0f);
            card.offsetMin = new Vector2(24f, 24f); card.offsetMax = new Vector2(-24f, 24f);
            LayoutBackground(card, 1.5f, WithA(Hex("080C0D"), 0.92f));
            var cv = card.gameObject.AddComponent<VerticalLayoutGroup>();
            cv.padding = new RectOffset(54, 54, 30, 54); cv.spacing = 40f;
            cv.childControlWidth = cv.childControlHeight = true;
            cv.childForceExpandWidth = true; cv.childForceExpandHeight = false;
            card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var cardGroup = card.gameObject.AddComponent<CanvasGroup>();
            cardGroup.blocksRaycasts = false; cardGroup.interactable = false;

            // grab handle
            var handleRow = Node("Handle", card);
            handleRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 12f;
            var handle = Img(Node("Bar", handleRow), _round, WithA(Snow, 0.25f), sliced: true, ppu: 12f);
            handle.rectTransform.anchorMin = handle.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            handle.rectTransform.sizeDelta = new Vector2(108f, 12f);
            handle.raycastTarget = false;

            // header: photo, names, close
            var head = Node("Head", card);
            var hh = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hh.childAlignment = TextAnchor.MiddleLeft; hh.spacing = 36f;
            hh.childControlWidth = hh.childControlHeight = true;
            hh.childForceExpandWidth = false; hh.childForceExpandHeight = false;
            var portrait = Node("Portrait", head);
            var ple = portrait.gameObject.AddComponent<LayoutElement>();
            ple.preferredWidth = ple.minWidth = ple.preferredHeight = ple.minHeight = 156f;
            var pring = Img(Stretch(Node("Ring", portrait)), _circle, Rust);
            pring.raycastTarget = false;
            var pmask = Img(Stretch(Node("Mask", portrait)), _circle, Color.white);
            pmask.rectTransform.offsetMin = new Vector2(6f, 6f); pmask.rectTransform.offsetMax = new Vector2(-6f, -6f);
            pmask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var pphoto = Img(Node("Photo", pmask.rectTransform), AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/{a.file}.png"), Color.white);
            pphoto.gameObject.AddComponent<CoverImage>().focus = new Vector2(a.focusX, 0.5f);
            pphoto.raycastTarget = false;
            var titles = Node("Titles", head);
            var tv = titles.gameObject.AddComponent<VerticalLayoutGroup>();
            tv.spacing = 4f; tv.childControlWidth = tv.childControlHeight = true;
            tv.childForceExpandWidth = tv.childForceExpandHeight = false;
            titles.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Text(titles, "Name", a.common, 66, Snow, FontStyles.Bold, spacing: -0.5f);
            Text(titles, "Latin", a.latin, 39, Lichen, FontStyles.Italic);
            Button close = IconButton(head, "Close", _closeIcon, 108f, 40f, out _);
            ((Image)close.targetGraphic).color = WithA(Snow, 0.1f);
            var cle = close.gameObject.AddComponent<LayoutElement>();
            cle.preferredWidth = cle.preferredHeight = cle.minWidth = cle.minHeight = 108f;

            // three quick stats
            var stats = Node("Stats", card);
            var sh = stats.gameObject.AddComponent<HorizontalLayoutGroup>();
            sh.spacing = 24f; sh.childControlWidth = sh.childControlHeight = true;
            sh.childForceExpandWidth = true; sh.childForceExpandHeight = true;
            foreach (var (label, value) in Stats(a.file))
            {
                var cell = Node(label, stats);
                cell.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                LayoutBackground(cell, 3f, WithA(Snow, 0.07f));
                var cvl = cell.gameObject.AddComponent<VerticalLayoutGroup>();
                cvl.padding = new RectOffset(30, 24, 26, 28); cvl.spacing = 6f;
                cvl.childControlWidth = cvl.childControlHeight = true;
                cvl.childForceExpandWidth = true; cvl.childForceExpandHeight = false;
                Text(cell, "Label", label, 29, Lichen, FontStyles.Bold, spacing: 4f);
                var vt = Text(cell, "Value", value, 44, Snow, FontStyles.Bold);
                vt.textWrappingMode = TextWrappingModes.NoWrap;
                vt.overflowMode = TextOverflowModes.Ellipsis;
            }

            // facts, numbered
            var facts = Node("Facts", card);
            var fvl = facts.gameObject.AddComponent<VerticalLayoutGroup>();
            fvl.spacing = 24f; fvl.childControlWidth = fvl.childControlHeight = true;
            fvl.childForceExpandWidth = true; fvl.childForceExpandHeight = false;
            foreach (var (fact, i) in Facts(a.file).Select((f, i) => (f, i)))
            {
                var row = Node($"Fact{i}", facts);
                var rh = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rh.spacing = 30f; rh.childAlignment = TextAnchor.UpperLeft;
                rh.childControlWidth = rh.childControlHeight = true;
                rh.childForceExpandWidth = false; rh.childForceExpandHeight = false;
                var badge = Node("Badge", row);
                var ble = badge.gameObject.AddComponent<LayoutElement>();
                ble.preferredWidth = ble.minWidth = ble.preferredHeight = ble.minHeight = 60f;
                Img(Stretch(Node("Fill", badge)), _circle, i == 0 ? Rust : WithA(Rust, 0.2f)).raycastTarget = false;
                var num = Text(badge, "N", (i + 1).ToString(), 33, i == 0 ? RustInk : Rust, FontStyles.Bold);
                Stretch(num.rectTransform); num.alignment = TextAlignmentOptions.Center;
                var ft = Text(row, "Text", fact, 40, WithA(Snow, 0.92f), FontStyles.Normal, lineSpacing: 4f);
                ft.textWrappingMode = TextWrappingModes.Normal;
                ft.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }

            // gestures to try
            var tryRow = Node("TryHead", card);
            var trh = tryRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            trh.childControlWidth = trh.childControlHeight = true;
            trh.childForceExpandWidth = false; trh.childForceExpandHeight = false;
            trh.childAlignment = TextAnchor.LowerLeft;
            var tl = Text(tryRow, "Label", "TRY A GESTURE", 29, Lichen, FontStyles.Bold, spacing: 5f);
            tl.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            Text(tryRow, "Hint", "Hold it up to the camera", 32, Lichen, FontStyles.Normal);
            var tiles = Node("Gestures", card);
            var tgh = tiles.gameObject.AddComponent<HorizontalLayoutGroup>();
            tgh.spacing = 18f; tgh.childControlWidth = tgh.childControlHeight = true;
            tgh.childForceExpandWidth = true; tgh.childForceExpandHeight = true;
            foreach (var (icon, label, action, special) in new[] {
                (_palmIcon, "Palm", "Come here", false), (_fistIcon, "Fist", "Startle", false),
                (_pinchIcon, "Pinch", "Treat", false), (_pointIcon, "Point", SpecialName(a.file), true),
                (_peaceIcon, "Peace", "Say cheese", false) })
            {
                var tile = Node(label, tiles);
                tile.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                LayoutBackground(tile, 3f, special ? WithA(Rust, 0.16f) : WithA(Snow, 0.07f));
                if (special)
                {
                    var rim = Img(Stretch(Node("Rim", tile)), _stroke, Rust, sliced: true, ppu: 3f);
                    rim.raycastTarget = false;
                    rim.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                }
                var tvl = tile.gameObject.AddComponent<VerticalLayoutGroup>();
                tvl.padding = new RectOffset(8, 8, 28, 24); tvl.spacing = 10f;
                tvl.childAlignment = TextAnchor.UpperCenter;
                tvl.childControlWidth = tvl.childControlHeight = true;
                tvl.childForceExpandWidth = false; tvl.childForceExpandHeight = false;
                FixedIcon(tile, "Icon", icon, Rust, 72f);
                var lt = Text(tile, "Label", label, 33, Snow, FontStyles.Bold);
                lt.alignment = TextAlignmentOptions.Center;
                var at = Text(tile, "Does", action, 29, special ? Rust : Lichen, FontStyles.Normal);
                at.alignment = TextAlignmentOptions.Center;
                at.textWrappingMode = TextWrappingModes.NoWrap;
            }

            // the minigame
            var play = Node("Play", card);
            play.gameObject.AddComponent<LayoutElement>().preferredHeight = 162f;
            var playBg = Img(Stretch(Node("Fill", play)), _round, Rust, sliced: true, ppu: 1.1f);
            var playBtn = play.gameObject.AddComponent<Button>();
            playBtn.targetGraphic = playBg;
            var pwords = Node("Words", play);
            pwords.anchorMin = new Vector2(0f, 0f); pwords.anchorMax = new Vector2(1f, 1f);
            pwords.offsetMin = new Vector2(66f, 0f); pwords.offsetMax = new Vector2(-170f, 0f);
            var pwv = pwords.gameObject.AddComponent<VerticalLayoutGroup>();
            pwv.childAlignment = TextAnchor.MiddleLeft; pwv.spacing = 2f;
            pwv.childControlWidth = pwv.childControlHeight = true;
            pwv.childForceExpandWidth = true; pwv.childForceExpandHeight = false;
            var playTitle = Text(pwords, "Title", $"Play {GameTitle(a.file)}", 48, RustInk, FontStyles.Bold);
            var playBest = Text(pwords, "Best", GameLength(a.file), 33, WithA(RustInk, 0.7f), FontStyles.Bold);
            playTitle.raycastTarget = playBest.raycastTarget = false;
            var pcirc = Img(Node("Go", play), _circle, RustInk);
            pcirc.rectTransform.anchorMin = pcirc.rectTransform.anchorMax = pcirc.rectTransform.pivot = new Vector2(1f, 0.5f);
            pcirc.rectTransform.anchoredPosition = new Vector2(-21f, 0f);
            pcirc.rectTransform.sizeDelta = new Vector2(120f, 120f);
            pcirc.raycastTarget = false;
            var ptri = Img(Node("Icon", pcirc.rectTransform), _playIcon, Rust);
            ptri.rectTransform.anchorMin = ptri.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            ptri.rectTransform.sizeDelta = new Vector2(48f, 48f);
            ptri.rectTransform.anchoredPosition = new Vector2(4f, 0f);
            ptri.raycastTarget = false;

            // the big 3-2-1
            var cd = Node("Countdown", safe);
            cd.anchorMin = cd.anchorMax = cd.pivot = new Vector2(0.5f, 0.5f);
            cd.sizeDelta = new Vector2(330f, 330f);
            cd.anchoredPosition = new Vector2(0f, 120f);
            Img(Stretch(Node("Disc", cd)), _circle, WithA(Night, 0.55f)).raycastTarget = false;
            var cdText = Text(cd, "N", "3", 210, Snow, FontStyles.Bold);
            Stretch(cdText.rectTransform); cdText.alignment = TextAlignmentOptions.Center;
            cdText.raycastTarget = false;
            var cdGroup = cd.gameObject.AddComponent<CanvasGroup>();
            cdGroup.alpha = 0f; cdGroup.blocksRaycasts = false;

            MiniGameHost host = BuildGameUI(canvas, safe);

            var flash = Stretch(Node("Flash", root));
            Img(flash, null, Color.white).raycastTarget = false;
            var flashGroup = flash.gameObject.AddComponent<CanvasGroup>();
            flashGroup.alpha = 0f; flashGroup.blocksRaycasts = false;

            var fader = Stretch(Node("Fader", root));
            Img(fader, null, Night);
            var faderGroup = fader.gameObject.AddComponent<CanvasGroup>();
            faderGroup.alpha = 1f; faderGroup.blocksRaycasts = false;

            var sizer = canvas.gameObject.AddComponent<ARSizeToggle>();
            sizer.button = sizeBtn;
            sizer.highlight = hl.rectTransform;
            sizer.lifeLabel = lifeLabel;
            sizer.smallLabel = smallLabel;
            sizer.activeInk = Night;
            sizer.idleInk = Snow;
            sizer.stage = GameObject.Find("Ground Plane Stage")?.transform;

            // taps: UI stays UI, the animal opens its facts, only the floor moves it
            var router = canvas.gameObject.AddComponent<ARTapRouter>();
            router.planeFinder = UnityEngine.Object.FindFirstObjectByType<PlaneFinderBehaviour>(FindObjectsInactive.Include);
            foreach (var l in UnityEngine.Object.FindObjectsByType<AnchorInputListenerBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                l.enabled = false;               // it forwards every tap, buttons included
                EditorUtility.SetDirty(l);
            }

            var photo = canvas.gameObject.AddComponent<PhotoCapture>();
            photo.shutter = shutterBtn;
            photo.flash = flashGroup;
            photo.savedToast = shotGroup;
            photo.thumbnail = thumb;
            photo.savedText = shotText;
            photo.shutterSound = Clip("ui_shutter");

            // everything but Back and Mute starts hidden; ARHud brings them in
            sizeGroup.alpha = 0f; shutterGroup.alpha = 0f;
            foreach (var g in new[] { placedGroup, cardGroup, coachGroup, palmGroup, tipGroup }) g.alpha = 0f;

            var hud = canvas.gameObject.AddComponent<ARHud>();
            hud.placedInfo = placedGroup;
            hud.fader = faderGroup;
            hud.backButton = back;
            hud.animalName = a.common.ToLowerInvariant();
            hud.infoButton = chipBtn;
            hud.factCard = cardGroup;
            hud.factClose = close;
            hud.coach = coachGroup;
            hud.palmPill = palmGroup;
            hud.palmText = palmText;
            hud.toast = tipGroup;
            hud.toastText = tipText;
            hud.muteButton = mute;
            hud.muteIcon = muteIcon;
            hud.soundOn = _soundOn;
            hud.soundOff = _soundOff;
            hud.sizeGroup = sizeGroup;
            hud.shutterGroup = shutterGroup;
            router.hud = hud;
            router.games = host;
            hud.games = host;
            hud.playButton = playBtn;
            hud.playTitle = playTitle;
            hud.playBest = playBest;
            hud.countdown = cdGroup;
            hud.countdownText = cdText;
            hud.palmIconImage = palm.Find("Palm")?.GetComponent<Image>();
            hud.iconPalm = _palmIcon; hud.iconFist = _fistIcon; hud.iconPinch = _pinchIcon;
            hud.iconPoint = _pointIcon; hud.iconPeace = _peaceIcon;
        }

        static void SetBuildScenes()
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(EntryPath, true),
            };
            foreach (var a in Animals.Where(a => a.inAR))
                list.Add(new EditorBuildSettingsScene(ScenePath(a.arScene), true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        // ============================================================ capture
        /// <summary>Renders both screens to PNG, with a simulated iPhone safe area.</summary>
        public static void CaptureAll()
        {
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);

            EditorSceneManager.OpenScene(EntryPath, OpenSceneMode.Single);
            var entry = UnityEngine.Object.FindFirstObjectByType<EntryScreen>();
            PrepareForCapture(entry.GetComponent<Canvas>(), out Camera cam, out RenderTexture rt);
            entry.fader.alpha = 0f;
            SetImageAlpha(entry.backdropB, 0f);
            foreach (var (page, scanned, label) in new[] { (0, true, "ready"), (1, true, "noAR"), (3, true, "hare") })
            {
                entry.backdropA.sprite = entry.backdrops[page];
                FitAll();
                entry.ApplyLayout();
                entry.carousel.GoTo(page, instant: true);
                SnapDots(entry, page);
                entry.RefreshAction(scanned, instant: true);
                if (entry.actionGlow != null)
                    SetImageAlpha(entry.actionGlow, scanned && page == 1 ? 0f : 0.4f);
                Shoot(cam, rt, Path.Combine(outDir, $"entry_{label}_p{page}.png"));
            }

            CaptureOverlay(ScenePath(AppScenes.Deer), "hare", "deer_scanning", placed: false, outDir);
            CaptureOverlay(ScenePath(AppScenes.Deer), "hare", "deer_placed", placed: true, outDir);
            CaptureHudStates(outDir);
            Debug.Log("[AppUI] Captures written to " + Path.GetFullPath(outDir));
        }

        /// <summary>The HUD's newer states, staged by hand: page card, palm coach and feedback, facts, a tracking tip.</summary>
        public static void CaptureHudStates() =>
            CaptureHudStates(Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture");

        static void CaptureHudStates(string outDir)
        {
            Directory.CreateDirectory(outDir);
            string fox = ScenePath(AppScenes.Fox);
            CaptureOverlay(fox, "hare", "hud_page", false, outDir, (flow, hud) =>
            {
                flow.title.text = flow.pageTitle; flow.subtitle.text = flow.pageSubtitle;
                flow.statusLabel.text = "Waiting for the page";
                if (flow.reticleGroup != null) flow.reticleGroup.alpha = 0f;
                if (flow.pageHint != null) flow.pageHint.alpha = 1f;
            });
            CaptureOverlay(fox, "hare", "hud_coach", true, outDir, (flow, hud) => hud.coach.alpha = 1f);
            CaptureOverlay(fox, "hare", "hud_palm", true, outDir, (flow, hud) => { hud.palmPill.alpha = 1f; });
            CaptureOverlay(fox, "hare", "hud_facts", true, outDir, (flow, hud) => { hud.placedInfo.alpha = 0f; hud.factCard.alpha = 1f; hud.shutterGroup.alpha = 0f; });
            CaptureOverlay(fox, "hare", "hud_tip", true, outDir, (flow, hud) => { hud.toast.alpha = 1f; hud.muteIcon.sprite = hud.soundOff; });
            CaptureOverlay(fox, "hare", "hud_photo", true, outDir, (flow, hud) =>
            {
                var pc = hud.GetComponent<PhotoCapture>();
                pc.savedToast.alpha = 1f;
                pc.thumbnail.sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/hare.png");
                pc.thumbnail.preserveAspect = false;
                pc.thumbnail.gameObject.AddComponent<CoverImage>();
                // show the "Small" half selected
                var sz = hud.GetComponent<ARSizeToggle>();
                sz.highlight.anchorMin = new Vector2(0.5f, 0f); sz.highlight.anchorMax = new Vector2(1f, 1f);
                sz.lifeLabel.color = sz.idleInk; sz.smallLabel.color = sz.activeInk;
            });
        }

        /// <summary>Capture an animal scene's UI over a photo standing in for the camera feed.</summary>
        static void CaptureOverlay(string scenePath, string feedPhoto, string label, bool placed, string outDir,
                                   Action<GroundScanFlow, ARHud> stage = null)
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c.isRootCanvas).OrderBy(c => c.sortingOrder).ToArray();
            PrepareForCapture(canvases[0], out Camera cam, out RenderTexture rt);
            for (int i = 1; i < canvases.Length; i++)
            {
                canvases[i].renderMode = RenderMode.ScreenSpaceCamera;
                canvases[i].worldCamera = cam;
                canvases[i].planeDistance = 10f - i;          // later canvases in front
            }
            foreach (var c in canvases)
            {
                var f = c.transform.Find("Fader")?.GetComponent<CanvasGroup>();
                if (f != null) f.alpha = 0f;
            }
            var flow = UnityEngine.Object.FindFirstObjectByType<GroundScanFlow>();
            var hud = UnityEngine.Object.FindFirstObjectByType<ARHud>();
            if (flow != null && flow.overlay != null) flow.overlay.alpha = placed ? 0f : 1f;
            if (hud != null && hud.placedInfo != null) hud.placedInfo.alpha = placed ? 1f : 0f;
            if (hud != null && hud.sizeGroup != null) hud.sizeGroup.alpha = placed ? 1f : 0f;
            if (hud != null && hud.shutterGroup != null) hud.shutterGroup.alpha = placed ? 1f : 0f;

            var fake = new GameObject("FakeFeed", typeof(RectTransform)).GetComponent<RectTransform>();
            fake.SetParent(canvases[0].transform, false); fake.SetAsFirstSibling();
            Stretch(fake);
            fake.gameObject.AddComponent<Image>().sprite =
                AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/{feedPhoto}.png");
            fake.gameObject.AddComponent<CoverImage>();
            if (hud != null)
                foreach (var g in new[] { hud.coach, hud.palmPill, hud.factCard, hud.toast })
                    if (g != null) g.alpha = 0f;
            stage?.Invoke(flow, hud);
            Canvas.ForceUpdateCanvases();
            foreach (var c in canvases)
                foreach (var lg in c.GetComponentsInChildren<LayoutGroup>(true).Reverse())
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)lg.transform);
            Shoot(cam, rt, Path.Combine(outDir, label + ".png"));
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);   // discard capture edits
        }

        static void PrepareForCapture(Canvas canvas, out Camera cam, out RenderTexture rt)
        {
            rt = new RenderTexture((int)RefW, (int)RefH, 24, RenderTextureFormat.ARGB32);
            var go = new GameObject("CaptureCam");
            cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Ground;
            cam.orthographic = true;
            cam.targetTexture = rt;
            cam.cullingMask = 1 << LayerMask.NameToLayer("UI");   // UI only, no 3D scene
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 10f;

            // iPhone 15 Pro insets at 3x: 59pt Dynamic Island area, 34pt home indicator
            foreach (var s in UnityEngine.Object.FindObjectsByType<SafeAreaFitter>(FindObjectsSortMode.None))
            {
                var r = (RectTransform)s.transform;
                s.enabled = false;
                r.anchorMin = new Vector2(0f, 102f / RefH);
                r.anchorMax = new Vector2(1f, 1f - 177f / RefH);
                r.offsetMin = r.offsetMax = Vector2.zero;
            }
            Canvas.ForceUpdateCanvases();
        }

        static void FitAll()
        {
            Canvas.ForceUpdateCanvases();
            // Layout groups and size fitters normally resolve on the first frame;
            // in an editor capture nothing ticks, so force the pass explicitly.
            foreach (var lg in UnityEngine.Object.FindObjectsByType<LayoutGroup>(FindObjectsSortMode.None))
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)lg.transform);
            foreach (var c in UnityEngine.Object.FindObjectsByType<CoverImage>(FindObjectsSortMode.None)) c.Fit();
            Canvas.ForceUpdateCanvases();
        }

        static void SnapDots(EntryScreen e, int page)
        {
            for (int i = 0; i < e.dots.Length; i++)
            {
                var sz = e.dots[i].sizeDelta;
                sz.x = i == page ? e.dotActiveWidth : e.dotIdleWidth;
                e.dots[i].sizeDelta = sz;
                e.dotImages[i].color = i == page ? e.dotActive : e.dotIdle;
            }
            Canvas.ForceUpdateCanvases();
        }

        static void Shoot(Camera cam, RenderTexture rt, string path)
        {
            FitAll();
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[AppUI] captured " + path);
        }

        // ============================================================ helpers
        const string ActionsPath = "Assets/InputSystem_Actions.inputactions";

        static void AddEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();

            // This project runs the new Input System only; StandaloneInputModule
            // would throw every frame. Types are resolved by name so the editor
            // assembly needs no hard reference to Unity.InputSystem.
            var moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (moduleType == null) { go.AddComponent<StandaloneInputModule>(); return; }
            var module = go.AddComponent(moduleType);

            // AssignDefaultActions() builds actions that belong to no asset, and a
            // scene cannot serialise references to those. Instead point the module
            // at the project's own actions and use the InputActionReference
            // sub-assets its importer generates -- exactly what the Inspector
            // does when you drag an actions asset in.
            var asset = AssetDatabase.LoadMainAssetAtPath(ActionsPath);
            if (asset == null)
            {
                Debug.LogWarning("[AppUI] " + ActionsPath + " not found; EventSystem has no UI actions.");
                return;
            }
            moduleType.GetProperty("actionsAsset")?.SetValue(module, asset);

            // Reference names are not unique inside this asset (Player/Interact
            // appears twice), so keep the first per name rather than ToDictionary.
            var refs = new System.Collections.Generic.Dictionary<string, UnityEngine.Object>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ActionsPath))
                if (o != null && o.GetType().Name == "InputActionReference" && !refs.ContainsKey(o.name))
                    refs.Add(o.name, o);

            void Bind(string property, string action)
            {
                if (!refs.TryGetValue("UI/" + action, out var r))
                {
                    Debug.LogWarning("[AppUI] no UI/" + action + " action to bind");
                    return;
                }
                moduleType.GetProperty(property)?.SetValue(module, r);
            }
            Bind("point", "Point");
            Bind("leftClick", "Click");
            Bind("rightClick", "RightClick");
            Bind("middleClick", "MiddleClick");
            Bind("scrollWheel", "ScrollWheel");
            Bind("move", "Navigate");
            Bind("submit", "Submit");
            Bind("cancel", "Cancel");
            Bind("trackedDevicePosition", "TrackedDevicePosition");
            Bind("trackedDeviceOrientation", "TrackedDeviceOrientation");
        }

        static Canvas MakeCanvas(string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(RefW, RefH);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return c;
        }

        static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static RectTransform Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        static void Anchor(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0); rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void AnchorTop(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.sizeDelta = new Vector2(0, height); rt.anchoredPosition = Vector2.zero;
        }

        static void AnchorBottom(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(0.5f, 0);
            rt.sizeDelta = new Vector2(0, height); rt.anchoredPosition = Vector2.zero;
        }

        static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        static void PlaceBottomLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
        }

        static void PlaceBottomStretch(RectTransform rt, float margin, float y, float h)
        {
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0); rt.pivot = new Vector2(0.5f, 0);
            rt.offsetMin = new Vector2(margin, y); rt.offsetMax = new Vector2(-margin, y + h);
        }

        static Image Img(RectTransform rt, Sprite sprite, Color color, bool sliced = false, float ppu = 1f)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            if (sliced) { img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = ppu; }
            return img;
        }

        /// <summary>
        /// Rounded background for a self-sizing pill or panel, on its own child.
        ///
        /// It must NOT sit on the same GameObject as the layout group: a sliced
        /// Image reports its preferred size as the sum of its sprite borders
        /// (96+96 = 192 here) without applying pixelsPerUnitMultiplier, and a
        /// ContentSizeFitter takes the larger of that and the group's real size.
        /// That is what turned the "IN AR" tag into a 192px square.
        /// </summary>
        static Image LayoutBackground(RectTransform parent, float ppu, Color color)
        {
            var bg = Img(Stretch(Node("Bg", parent)), _round, color, sliced: true, ppu: ppu);
            bg.raycastTarget = false;
            bg.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            bg.transform.SetAsFirstSibling();
            return bg;
        }

        static Image CoverFill(RectTransform parent, string name, Sprite sprite, Vector2 focus)
        {
            var img = Img(Node(name, parent), sprite, Color.white);
            img.raycastTarget = false;
            img.gameObject.AddComponent<CoverImage>().focus = focus;
            return img;
        }

        static TMP_Text Text(Transform parent, string name, string text, float size, Color color,
                             FontStyles style, float spacing = 0f, float lineSpacing = 0f)
        {
            var rt = Node(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.characterSpacing = spacing;
            t.lineSpacing = lineSpacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.raycastTarget = false;
            return t;
        }

        static Button PillButton(Transform parent, string name, string label, Color fill, Color ink,
                                 float height, bool glow, float labelSize = 46f, float ppu = 1.14f)
        {
            var rt = Node(name, parent);
            if (glow)
            {
                var g = Img(Node("Glow", rt), _shadow, WithA(fill, 0.4f), sliced: true);
                Anchor(g.rectTransform, 0, 0, 1, 1);
                g.rectTransform.offsetMin = new Vector2(-40f, -58f);
                g.rectTransform.offsetMax = new Vector2(40f, 22f);
                g.raycastTarget = false;
            }
            var bg = Img(Stretch(Node("Fill", rt)), _round, fill, sliced: true, ppu: ppu);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            var cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = Color.white;
            cb.pressedColor = new Color(0.82f, 0.82f, 0.82f);
            cb.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.6f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;

            var t = Text(rt, "Label", label, labelSize, ink, FontStyles.Bold, spacing: 1f);
            Stretch(t.rectTransform);
            t.alignment = TextAlignmentOptions.Center;
            return btn;
        }

        // ------------------------------------------------------ sprite factory
        static float Sat(float v) => Mathf.Clamp01(v);

        static float RoundRectSD(float px, float py, float w, float h, float r)
        {
            float qx = Mathf.Abs(px - w * 0.5f) - (w * 0.5f - r);
            float qy = Mathf.Abs(py - h * 0.5f) - (h * 0.5f - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        // ---- icons, as signed distances (negative inside), anti-aliased over ~1.2 px

        static float Capsule(float px, float py, float ax, float ay, float bx, float by, float r)
        {
            float pax = px - ax, pay = py - ay, bax = bx - ax, bay = by - ay;
            float h = Mathf.Clamp01((pax * bax + pay * bay) / (bax * bax + bay * bay));
            float dx = pax - bax * h, dy = pay - bay * h;
            return Mathf.Sqrt(dx * dx + dy * dy) - r;
        }

        static float Arc(float px, float py, float cx, float cy, float radius, float halfWidth, float maxAngleDeg)
        {
            float dx = px - cx, dy = py - cy;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            float ang = Mathf.Abs(Mathf.Atan2(dy, dx)) * Mathf.Rad2Deg;
            float d = Mathf.Abs(len - radius) - halfWidth;
            if (ang > maxAngleDeg) d = Mathf.Max(d, (ang - maxAngleDeg) * Mathf.Deg2Rad * len);
            return d;
        }

        static float Speaker(float x, float y)
        {
            float body = RoundRectSD(x - 23f, y - 46f, 22f, 36f, 4f);
            float hh = 17f + (x - 44f) * (23f / 26f);
            float cone = Mathf.Max(Mathf.Max(43f - x, x - 70f), Mathf.Abs(y - 64f) - hh);
            return Mathf.Min(body, cone);
        }

        static void BakeIcons()
        {
            Func<int, int, float> aa(Func<float, float, float> sd) => (x, y) => Sat(0.5f - sd(x + .5f, y + .5f) / 1.2f);

            // an open hand, palm toward you: palm, four fingers, thumb out to the side
            _palmIcon = Bake("icon_palm", 128, 128, aa((x, y) =>
            {
                float d = RoundRectSD(x - 36f, y - 14f, 58f, 56f, 20f);
                float[] fx = { 44f, 58f, 72f, 86f }, top = { 98f, 110f, 107f, 94f };
                for (int i = 0; i < 4; i++) d = Mathf.Min(d, Capsule(x, y, fx[i], 52f, fx[i], top[i], 7.5f));
                d = Mathf.Min(d, Capsule(x, y, 40f, 36f, 17f, 64f, 8f));
                return d;
            }), Vector4.zero);

            _soundOn = Bake("icon_sound_on", 128, 128, aa((x, y) =>
                Mathf.Min(Speaker(x, y), Mathf.Min(Arc(x, y, 70f, 64f, 20f, 4f, 50f), Arc(x, y, 70f, 64f, 36f, 4f, 50f)))),
                Vector4.zero);

            _soundOff = Bake("icon_sound_off", 128, 128, aa((x, y) =>
                Mathf.Min(Speaker(x, y), Mathf.Min(Capsule(x, y, 84f, 50f, 110f, 78f, 4.5f), Capsule(x, y, 84f, 78f, 110f, 50f, 4.5f)))),
                Vector4.zero);

            // the other four hand signs, in the same silhouette style as the palm
            _fistIcon = Bake("icon_fist", 128, 128, aa((x, y) =>
            {
                float d = RoundRectSD(x - 30f, y - 14f, 66f, 62f, 22f);                    // the fist
                for (int i = 0; i < 4; i++)                                                 // knuckles
                    d = Mathf.Min(d, Capsule(x, y, 38f + i * 15f, 70f, 38f + i * 15f, 82f, 8.5f));
                d = Mathf.Max(d, -Capsule(x, y, 30f, 52f, 70f, 52f, 2.2f));                // finger crease
                d = Mathf.Min(d, Capsule(x, y, 26f, 48f, 62f, 40f, 9f));                    // thumb across
                return d;
            }), Vector4.zero);
            _pinchIcon = Bake("icon_pinch", 128, 128, aa((x, y) =>
            {
                float d = RoundRectSD(x - 36f, y - 12f, 56f, 50f, 20f);
                float ring = Mathf.Abs(Mathf.Sqrt((x - 46f) * (x - 46f) + (y - 76f) * (y - 76f)) - 15f) - 6.5f;   // thumb + index O
                d = Mathf.Min(d, ring);
                d = Mathf.Min(d, Capsule(x, y, 40f, 54f, 36f, 70f, 7f));
                foreach (var (fx, top) in new[] { (64f, 104f), (77f, 100f), (89f, 90f) })   // three fingers up
                    d = Mathf.Min(d, Capsule(x, y, fx, 52f, fx, top, 7f));
                return d;
            }), Vector4.zero);
            _pointIcon = Bake("icon_point", 128, 128, aa((x, y) =>
            {
                float d = RoundRectSD(x - 34f, y - 12f, 58f, 54f, 20f);
                d = Mathf.Min(d, Capsule(x, y, 46f, 50f, 46f, 112f, 8f));                    // index, up
                foreach (var fx in new[] { 60f, 74f, 87f })                                   // curled fingers
                    d = Mathf.Min(d, Capsule(x, y, fx, 56f, fx, 66f, 8f));
                d = Mathf.Min(d, Capsule(x, y, 38f, 36f, 20f, 58f, 8f));                      // thumb
                return d;
            }), Vector4.zero);
            _peaceIcon = Bake("icon_peace", 128, 128, aa((x, y) =>
            {
                float d = RoundRectSD(x - 34f, y - 12f, 58f, 54f, 20f);
                d = Mathf.Min(d, Capsule(x, y, 48f, 54f, 34f, 110f, 8f));                    // index, out
                d = Mathf.Min(d, Capsule(x, y, 62f, 54f, 74f, 112f, 8f));                    // middle, out
                foreach (var fx in new[] { 76f, 88f })
                    d = Mathf.Min(d, Capsule(x, y, fx, 56f, fx, 66f, 7.5f));
                d = Mathf.Min(d, Capsule(x, y, 38f, 36f, 22f, 56f, 8f));
                return d;
            }), Vector4.zero);

            float Star(float x, float y, float cx, float cy, float R, float r)
            {
                // a five-point star as the max of its edge half-planes (good enough for an icon)
                float ang = Mathf.Atan2(y - cy, x - cx) + Mathf.PI * 0.5f;
                float seg = Mathf.PI * 2f / 5f;
                float a = Mathf.Repeat(ang, seg) - seg * 0.5f;
                float len = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                float px = Mathf.Cos(a) * len, py = Mathf.Abs(Mathf.Sin(a)) * len;
                // edge from the outer point (R, 0) to the inner point at half a segment (r)
                float ix = Mathf.Cos(seg * 0.5f) * r, iy = Mathf.Sin(seg * 0.5f) * r;
                float ex = ix - R, ey = iy;
                float nx = ey, ny = -ex;
                float nl = Mathf.Sqrt(nx * nx + ny * ny);
                return ((px - R) * nx + py * ny) / nl * -1f;
            }
            _starOn = Bake("icon_star_on", 128, 128, aa((x, y) => Star(x, y, 64f, 62f, 58f, 25f)), Vector4.zero);
            _starOff = Bake("icon_star_off", 128, 128, aa((x, y) =>
            {
                float outer = Star(x, y, 64f, 62f, 58f, 25f);
                return Mathf.Max(outer, -(outer + 7f));                                       // an outline
            }), Vector4.zero);
            _playIcon = Bake("icon_play", 64, 64, aa((x, y) =>
                Mathf.Max(Mathf.Max(20f - x, (x - 20f) * 0.5f + Mathf.Abs(y - 32f) - 18f), -1f)), Vector4.zero);

            _closeIcon = Bake("icon_close", 64, 64, aa((x, y) =>
                Mathf.Min(Capsule(x, y, 18f, 18f, 46f, 46f, 3.5f), Capsule(x, y, 18f, 46f, 46f, 18f, 3.5f))), Vector4.zero);
        }

        static Sprite RoundRect(string name, int size, float r, int border) =>
            Bake(name, size, size, (x, y) => Sat(0.5f - RoundRectSD(x + .5f, y + .5f, size, size, r)),
                 new Vector4(border, border, border, border));

        static Sprite RoundRectStroke(string name, int size, float r, float width, int border) =>
            Bake(name, size, size, (x, y) =>
            {
                float d = RoundRectSD(x + .5f, y + .5f, size - 2f, size - 2f, r - 1f);
                return Sat(0.5f - (Mathf.Abs(d + width * 0.5f) - width * 0.5f));
            }, new Vector4(border, border, border, border));

        static Sprite Shadow(string name, int size, float blur, float r)
        {
            float inner = size - blur * 2f;
            return Bake(name, size, size, (x, y) =>
            {
                float d = RoundRectSD(x + .5f - blur, y + .5f - blur, inner, inner, r);
                if (d <= 0f) return 1f;
                float t = Sat(1f - d / blur);
                return t * t * (3f - 2f * t) * t;          // long, soft tail
            }, new Vector4(blur + r, blur + r, blur + r, blur + r));
        }

        static Sprite Gradient(string name) =>
            Bake(name, 4, 256, (x, y) =>
            {
                float t = 1f - y / 255f;                    // opaque at the bottom
                return t * t * (3f - 2f * t);
            }, Vector4.zero);

        static Sprite Circle(string name, int size) =>
            Bake(name, size, size, (x, y) =>
            {
                float dx = x + .5f - size * .5f, dy = y + .5f - size * .5f;
                return Sat(size * .5f - 1f - Mathf.Sqrt(dx * dx + dy * dy) + .5f);
            }, Vector4.zero);

        static Sprite Ring(string name, int size, float width) =>
            Bake(name, size, size, (x, y) =>
            {
                float dx = x + .5f - size * .5f, dy = y + .5f - size * .5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float mid = size * .5f - 2f - width * .5f;
                return Sat(0.5f - (Mathf.Abs(d - mid) - width * .5f));
            }, Vector4.zero);

        static Sprite Bake(string name, int w, int h, Func<int, int, float> alpha, Vector4 border)
        {
            string path = $"{GenDir}/{name}.png";
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha(x, y) * 255f));
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.spriteBorder = border;
            ti.spritePixelsPerUnit = 100f;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static void ConfigureSprite(string path, int maxSize, bool mip)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) throw new Exception("Missing image: " + path);
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = mip;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.maxTextureSize = maxSize;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
        }

        static Color Hex(string h)
        {
            ColorUtility.TryParseHtmlString("#" + h, out Color c);
            return c;
        }

        static Color WithA(Color c, float a) { c.a = a; return c; }

        static void SetImageAlpha(Image img, float a)
        {
            var c = img.color; c.a = a; img.color = c;
        }

        static T Also<T>(this T self, Action<T> f) { f(self); return self; }
    }
}
