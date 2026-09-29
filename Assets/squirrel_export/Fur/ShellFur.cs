using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Shell fur: renders the mesh N extra times, each copy pushed a little further out along
/// its normals, while the fur shader keeps only the pixels that belong to a strand tall
/// enough to reach that shell. Stacked together the shells read as dense short fur.
///
/// Add to any MeshRenderer object (the Squirrel and Squirrel_Tail meshes from the FBX).
/// Shells are generated as hidden children and are never saved into the scene.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ShellFur : MonoBehaviour
{
    [Header("Shape")]
    [Range(1, 64)] public int shellCount = 24;
    [Tooltip("Fur length in the object's local units (the squirrel body is ~0.6 units tall).")]
    public float furLength = 0.045f;
    [Tooltip("How far tips sag toward world-down, as a fraction of fur length.")]
    [Range(0f, 2f)] public float gravity = 0.5f;

    [Header("Strands")]
    [Tooltip("Strands per local unit. Higher = finer fur.")]
    public float density = 220f;
    [Range(0.05f, 1f)] public float thickness = 0.85f;

    [Header("Look")]
    [Range(0f, 1f)] public float rootShade = 0.45f;
    [Tooltip("Skip fur where the albedo is darker than this (keeps eyes and nose bare).")]
    [Range(0f, 0.5f)] public float bareBelow = 0.07f;
    public Color tint = Color.white;
    [Tooltip("Leave empty to reuse the renderer's own albedo texture.")]
    public Texture albedoOverride;
    [Tooltip("Leave empty to auto-pick the Built-in or URP fur shader.")]
    public Shader shaderOverride;

    const string ShellPrefix = "__FurShell_";
    Material[] _materials;

    void OnEnable() { Rebuild(); }
    void OnDisable() { Clear(); }

    void OnValidate()
    {
        if (!isActiveAndEnabled) return;
#if UNITY_EDITOR
        // Creating objects inside OnValidate is not allowed; defer one editor tick.
        UnityEditor.EditorApplication.delayCall += () => { if (this != null && isActiveAndEnabled) Rebuild(); };
#else
        Rebuild();
#endif
    }

    [ContextMenu("Rebuild Fur")]
    public void Rebuild()
    {
        Clear();

        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();
        if (mf.sharedMesh == null) return;

        Shader shader = shaderOverride != null ? shaderOverride : FindFurShader();
        if (shader == null)
        {
            Debug.LogWarning("ShellFur: no fur shader found. Keep ShellFurBuiltIn.shader or ShellFurURP.shader in the project.", this);
            return;
        }

        Texture albedo = albedoOverride;
        if (albedo == null && mr.sharedMaterial != null) albedo = mr.sharedMaterial.mainTexture;

        _materials = new Material[shellCount];
        for (int i = 0; i < shellCount; i++)
        {
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (albedo != null) mat.SetTexture("_MainTex", albedo);
            mat.SetColor("_Color", tint);
            mat.SetFloat("_FurLength", furLength);
            mat.SetFloat("_Density", density);
            mat.SetFloat("_Thickness", thickness);
            mat.SetFloat("_Gravity", gravity);
            mat.SetFloat("_RootShade", rootShade);
            mat.SetFloat("_BareBelow", bareBelow);
            mat.SetFloat("_ShellIndex", i);
            mat.SetFloat("_ShellCount", shellCount);
            _materials[i] = mat;

            var go = new GameObject(ShellPrefix + i) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;   // the base mesh already casts the shadow
            r.receiveShadows = true;
        }
    }

    void Clear()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.name.StartsWith(ShellPrefix)) DestroyObj(child.gameObject);
        }
        if (_materials != null)
        {
            foreach (var m in _materials) if (m != null) DestroyObj(m);
            _materials = null;
        }
    }

    static void DestroyObj(Object o)
    {
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    static Shader FindFurShader()
    {
        bool srp = GraphicsSettings.currentRenderPipeline != null;
        var s = Shader.Find(srp ? "Custom/ShellFur URP" : "Custom/ShellFur BuiltIn");
        if (s == null) s = Shader.Find(srp ? "Custom/ShellFur BuiltIn" : "Custom/ShellFur URP");
        return s;
    }
}
