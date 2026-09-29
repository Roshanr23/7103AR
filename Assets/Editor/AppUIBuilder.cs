using System;
using System.IO;
using System.Linq;
using AR7103.App;
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
            new Species("fox",      "Red Fox",           "Vulpes vulpes",           0.42f),
            new Species("deer",     "White-tailed Deer", "Odocoileus virginianus",  0.50f, AppScenes.Deer,     "ImageTargetDeer"),
            new Species("hare",     "Snowshoe Hare",     "Lepus americanus",        0.45f),
            new Species("owl",      "Snowy Owl",         "Bubo scandiacus",         0.55f, AppScenes.Owl,      "ImageTargetOwl"),
        };

        static TMP_FontAsset _font;
        static Sprite _round, _stroke, _shadow, _gradUp, _circle, _ringThin, _ringThick, _blob;

        // ================================================================ entry
        [MenuItem("7103AR/Build App UI")]
        public static void BuildAll()
        {
            Prepare();
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
        static float SpawnHeight(string file) => file switch { "squirrel" => 0.30f, "deer" => 1.0f, "owl" => 0.6f, _ => 0.5f };
        static float SpawnYaw(string file) => file switch { _ => 0f };
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
            string outDir = Environment.GetEnvironmentVariable("APPUI_CAPTURE_DIR") ?? "Temp/AppUICapture";
            Directory.CreateDirectory(outDir);
            foreach (var (file, path) in new[] {
                ("squirrel", "Assets/squirrel_export/SquirrelScene.fbx"),
                ("deer",     "Assets/deer_export/WhiteTailedDeer.fbx"),
                ("owl",      "Assets/owl_export/SnowyOwl.fbx") })
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
                float facing = Vector3.Dot(spawn.transform.forward, toCam);
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

            foreach (var a in Animals)
            {
                ConfigureSprite($"{AnimalDir}/{a.file}.png", 1024, mip: true);
                ConfigureSprite($"{AnimalDir}/Backdrops/{a.file}_blur.png", 256, mip: false);
            }
            AssetDatabase.Refresh();
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
                                ObserverBehaviour trigger = null, string pageSubtitle = null)
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
                if (original == null) throw new Exception("SampleScene has no " + a.target + " to restore");
                existing = UnityEngine.Object.Instantiate(original);
                existing.name = a.target;
                SceneManager.MoveGameObjectToScene(existing, scene);
                EditorSceneManager.CloseScene(src, true);
                Debug.Log($"[AppUI] {a.arScene}: restored {a.target} as the page trigger");
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
                if (target == null) throw new Exception($"{a.arScene}: no {a.target} and no model on the stage");
                GameObject model = target.transform.Cast<Transform>()
                    .Select(t => t.gameObject)
                    .FirstOrDefault(PrefabUtility.IsAnyPrefabInstanceRoot);
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
            // hidden until the floor locks, then GroundSpawn places and reveals it
            spawn.gameObject.SetActive(false);

            BuildScanUI(includeBack: false, spawn: spawn, returnToMenu: false,
                        lockedTitle: "Found it",
                        lockedSubtitle: $"Placing the {a.common.ToLowerInvariant()} on your floor.",
                        trigger: page,
                        pageSubtitle: $"Point your camera at the {a.common.ToLowerInvariant()} page in your book.");
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

            // shown once the animal is down: what it is, and that it can be moved
            var chip = Node("Placed", safe);
            chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(0.5f, 0f);
            chip.anchoredPosition = new Vector2(0f, 72f);
            LayoutBackground(chip, 2.2f, WithA(Night, 0.62f));
            var v = chip.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(48, 48, 26, 30); v.spacing = 4f;
            v.childAlignment = TextAnchor.MiddleCenter;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            var cf = chip.gameObject.AddComponent<ContentSizeFitter>();
            cf.horizontalFit = cf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var placedGroup = chip.gameObject.AddComponent<CanvasGroup>();
            placedGroup.blocksRaycasts = false;
            var n = Text(chip, "Name", a.common, 40, Snow, FontStyles.Bold);
            n.alignment = TextAlignmentOptions.Center;
            var h = Text(chip, "Hint", "Tap the floor to move it", 32, WithA(Snow, 0.7f), FontStyles.Normal);
            h.alignment = TextAlignmentOptions.Center;

            var fader = Stretch(Node("Fader", root));
            Img(fader, null, Night);
            var faderGroup = fader.gameObject.AddComponent<CanvasGroup>();
            faderGroup.alpha = 1f; faderGroup.blocksRaycasts = false;

            var hud = canvas.gameObject.AddComponent<ARHud>();
            hud.placedInfo = placedGroup;
            hud.fader = faderGroup;
            hud.backButton = back;
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
            foreach (var (page, scanned, label) in new[] { (0, true, "ready"), (1, true, "noAR") })
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
            Debug.Log("[AppUI] Captures written to " + Path.GetFullPath(outDir));
        }

        /// <summary>Capture an animal scene's UI over a photo standing in for the camera feed.</summary>
        static void CaptureOverlay(string scenePath, string feedPhoto, string label, bool placed, string outDir)
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

            var fake = new GameObject("FakeFeed", typeof(RectTransform)).GetComponent<RectTransform>();
            fake.SetParent(canvases[0].transform, false); fake.SetAsFirstSibling();
            Stretch(fake);
            fake.gameObject.AddComponent<Image>().sprite =
                AssetDatabase.LoadAssetAtPath<Sprite>($"{AnimalDir}/{feedPhoto}.png");
            fake.gameObject.AddComponent<CoverImage>();
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
