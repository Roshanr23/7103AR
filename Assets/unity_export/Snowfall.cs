using UnityEngine;

/// <summary>
/// Configures a ParticleSystem as gently falling snow inside the model's local space,
/// so it scales and moves with the AR target. Tweak the fields in the Inspector; the
/// particle system is reconfigured on every change.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(ParticleSystem))]
public class Snowfall : MonoBehaviour
{
    [Header("Volume (local units of the model)")]
    [Tooltip("Width and depth of the area snow falls over, centred on this object.")]
    public Vector2 area = new Vector2(6f, 4f);
    [Tooltip("Height above this object where flakes spawn.")]
    public float spawnHeight = 4.8f;
    [Tooltip("How far below this object flakes fall before disappearing.")]
    public float fallDepth = 3.5f;

    [Header("Flakes")]
    public float flakesPerSecond = 60f;
    public float fallSpeed = 0.7f;
    public Vector2 flakeSize = new Vector2(0.015f, 0.035f);
    [Tooltip("Sideways drift strength.")]
    public float drift = 0.25f;
    public Color color = new Color(0.95f, 0.96f, 1f, 1f);

    [Header("Rendering")]
    public Material material;
    public Mesh flakeMesh;

    void OnEnable() { Configure(); }
    void OnValidate() { Configure(); }

    public void Configure()
    {
        var ps = GetComponent<ParticleSystem>();
        if (ps == null) return;

        float travel = spawnHeight + fallDepth;
        float lifetime = Mathf.Max(0.1f, travel / Mathf.Max(0.01f, fallSpeed));

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.startLifetime = lifetime;
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(flakeSize.x, flakeSize.y);
        main.startColor = color;
        main.gravityModifier = 0f;
        main.maxParticles = Mathf.CeilToInt(flakesPerSecond * lifetime) + 50;
        main.startRotation3D = false;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = flakesPerSecond;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.position = new Vector3(0f, spawnHeight, 0f);
        shape.scale = new Vector3(area.x, 0.05f, area.y);

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(-drift * 0.3f, drift * 0.3f);
        vel.y = new ParticleSystem.MinMaxCurve(-fallSpeed * 1.25f, -fallSpeed * 0.8f);
        vel.z = new ParticleSystem.MinMaxCurve(-drift * 0.3f, drift * 0.3f);

        var noise = ps.noise;
        noise.enabled = drift > 0f;
        noise.strength = drift;
        noise.frequency = 0.4f;
        noise.scrollSpeed = 0.3f;
        noise.damping = true;

        var renderer = GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            if (flakeMesh != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = flakeMesh;
            }
            else
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
            }
            if (material != null) renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        if (Application.isPlaying && !ps.isPlaying) ps.Play();
    }
}
