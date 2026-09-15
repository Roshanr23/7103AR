using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// One-click fixes for the imported Blender squirrel scene in an AR project.
/// Menu: Tools > Squirrel Scene > Fix Snow And Background
/// </summary>
public static class SquirrelSceneSetup
{
    const string MaterialPath = "Assets/unity_export/M_SnowParticle.mat";
    static readonly string[] BackgroundObjects = { "Backdrop", "BackgroundTrunks", "BackgroundBoughs", "BackgroundSnow", "Environment" };

    [MenuItem("Tools/Squirrel Scene/Fix Snow And Background")]
    public static void FixSnowAndBackground()
    {
        var root = FindSceneRoot();
        if (root == null)
        {
            EditorUtility.DisplayDialog("Squirrel Scene", "Could not find the SquirrelScene object in the open scene. Drag SquirrelScene.fbx into the scene first.", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Fix squirrel snow and background");
        int group = Undo.GetCurrentGroup();

        // 1. hide the painted backdrop: the AR camera feed is the background now
        foreach (var name in BackgroundObjects)
        {
            var t = FindDeep(root.transform, name);
            if (t != null && t.gameObject.activeSelf)
            {
                Undo.RecordObject(t.gameObject, "Hide background");
                t.gameObject.SetActive(false);
            }
        }

        // 2. hide the static snow mesh from Blender
        var staticSnow = FindDeep(root.transform, "FallingSnow");
        if (staticSnow != null && staticSnow.gameObject.activeSelf)
        {
            Undo.RecordObject(staticSnow.gameObject, "Hide static snow");
            staticSnow.gameObject.SetActive(false);
        }

        // 3. add an animated snowfall particle system
        var existing = FindDeep(root.transform, "Snowfall");
        if (existing == null)
        {
            var go = new GameObject("Snowfall");
            Undo.RegisterCreatedObjectUndo(go, "Create snowfall");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.AddComponent<ParticleSystem>();
            var snow = go.AddComponent<Snowfall>();
            snow.material = GetOrCreateSnowMaterial();
            snow.flakeMesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            snow.Configure();
            Selection.activeGameObject = go;
        }

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("Squirrel Scene: backdrop hidden, static snow replaced with a Snowfall particle system. Save the scene to keep it.");
    }

    /// <summary>Headless variant: opens the scene, applies the fix and saves. Used from the command line.</summary>
    public static void FixSnowAndBackgroundHeadless()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
        FixSnowAndBackground();
        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        Debug.Log("Squirrel Scene: headless fix applied and scene saved.");
    }

    [MenuItem("Tools/Squirrel Scene/Show Background Again")]
    public static void ShowBackground()
    {
        var root = FindSceneRoot();
        if (root == null) return;
        foreach (var name in BackgroundObjects)
        {
            var t = FindDeep(root.transform, name);
            if (t != null) { Undo.RecordObject(t.gameObject, "Show background"); t.gameObject.SetActive(true); }
        }
        EditorSceneManager.MarkSceneDirty(root.scene);
    }

    static Material GetOrCreateSnowMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat != null) return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        mat = new Material(shader);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.95f, 0.96f, 1f, 1f));
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(0.95f, 0.96f, 1f, 1f));
        AssetDatabase.CreateAsset(mat, MaterialPath);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static GameObject FindSceneRoot()
    {
        // the FBX instance: any scene object that has a "Squirrel" mesh child somewhere below it
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (go.name == "SquirrelScene" && go.scene.IsValid()) return go;
        }
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (go.name == "Squirrel" && go.scene.IsValid())
            {
                var t = go.transform;
                while (t.parent != null && t.parent.name != "ImageTarget") t = t.parent;
                return t.gameObject;
            }
        }
        return null;
    }

    static Transform FindDeep(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            var r = FindDeep(parent.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }
}
