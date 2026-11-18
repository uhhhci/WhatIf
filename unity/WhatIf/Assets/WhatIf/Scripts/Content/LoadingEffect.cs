using UnityEngine;
using UnityEngine.Rendering;

namespace WhatIf.Content
{
    /// <summary>
    /// Shows that something is coming at a placed position:
    ///   waiting for the cut-out -> a column of rising particles over the object's footprint,
    ///   cut-out shown, 3D coming -> a slow shimmer around it,
    ///   each arrival            -> a short burst.
    /// Bright, additive colours: dark particles are invisible on the Magic Leap 2's additive display.
    /// Built in code with Unity's particle system, so it needs no assets.
    /// </summary>
    public class LoadingEffect : MonoBehaviour
    {
        public enum Phase { None, Column, Shimmer }

        static readonly Color ColumnA = new Color(0.45f, 0.95f, 1f);   // cyan
        static readonly Color ColumnB = new Color(0.85f, 1f, 1f);
        static readonly Color ShimmerA = new Color(1f, 0.92f, 0.65f);  // warm white
        static readonly Color ShimmerB = new Color(1f, 1f, 1f);

        static Material _material;

        float _height, _footprint;
        ParticleSystem _loop;
        Phase _phase;

        public static LoadingEffect Attach(Transform parent, float height, float footprint)
        {
            var go = new GameObject("loading fx");
            go.transform.SetParent(parent, false);
            var fx = go.AddComponent<LoadingEffect>();
            fx._height = Mathf.Max(0.2f, height);
            fx._footprint = Mathf.Clamp(footprint, 0.2f, 6f);
            return fx;
        }

        public void SetPhase(Phase phase)
        {
            if (phase == _phase) return;
            _phase = phase;
            if (_loop != null)
            {
                _loop.Stop(true, ParticleSystemStopBehavior.StopEmitting); // let the last particles fade
                Destroy(_loop.gameObject, 3f);
                _loop = null;
            }
            if (phase == Phase.Column) _loop = MakeColumn();
            else if (phase == Phase.Shimmer) _loop = MakeShimmer();
        }

        /// <summary>A short burst, e.g. when the cut-out or the 3D model appears.</summary>
        public void Burst(bool big)
        {
            var ps = NewSystem("burst", maxParticles: 120);
            var main = ps.main;
            main.loop = false;
            main.duration = 0.2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, big ? 1.6f : 1.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
            main.startColor = new ParticleSystem.MinMaxGradient(ShimmerA, ColumnA);
            main.gravityModifier = 0.15f;
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(big ? 70 : 40)) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.15f, Mathf.Min(_footprint, _height) * 0.4f);
            shape.position = new Vector3(0f, _height * 0.5f, 0f);
            FadeOut(ps);
            ps.Play();
            Destroy(ps.gameObject, 2f);
        }

        public void Stop()
        {
            SetPhase(Phase.None);
            Destroy(gameObject, 3f);
        }

        // ------------------------------------------------------------------ systems

        ParticleSystem MakeColumn()
        {
            var ps = NewSystem("column", maxParticles: 250);
            var main = ps.main;
            float life = 1.6f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.8f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_height / life * 0.6f, _height / life);
            main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.045f);
            main.startColor = new ParticleSystem.MinMaxGradient(ColumnA, ColumnB);
            var em = ps.emission;
            em.rateOverTime = Mathf.Clamp(15f * _footprint, 10f, 50f);
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone; // a disc emitting upwards
            shape.angle = 2f;
            shape.radius = _footprint * 0.5f;
            shape.radiusThickness = 0.3f;                   // mostly from the rim: reads as an outline
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.08f;
            noise.frequency = 0.8f;
            FadeOut(ps);
            ps.Play();
            return ps;
        }

        ParticleSystem MakeShimmer()
        {
            var ps = NewSystem("shimmer", maxParticles: 120);
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.01f, 0.03f);
            main.startColor = new ParticleSystem.MinMaxGradient(ShimmerA, ShimmerB);
            main.gravityModifier = -0.03f; // drift upwards
            var em = ps.emission;
            em.rateOverTime = Mathf.Clamp(3f * _footprint * _height, 3f, 12f); // a hint, not a cloud
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(_footprint * 0.9f, _height, _footprint * 0.9f);
            shape.position = new Vector3(0f, _height * 0.5f, 0f);
            FadeOut(ps);
            ps.Play();
            return ps;
        }

        ParticleSystem NewSystem(string name, int maxParticles)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // configure before playing
            var main = ps.main;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Material();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        static void FadeOut(ParticleSystem ps)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        /// <summary>Additive soft-dot material, shared by all effects.</summary>
        /// <summary>The soft-dot particle material, also used by surface effects.</summary>
        public static Material ParticleMaterial() => Material();

        static Material Material()
        {
            if (_material != null) return _material;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { name = "WhatIfParticles" };
            var tex = SoftDot();
            m.mainTexture = tex;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f); // transparent
                m.SetFloat("_Blend", 2f);   // additive
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)BlendMode.One);
            // Alpha as coverage ("over"), not replaced: on ML2 the frame's alpha drives segmented
            // dimming, and URP's default (One, Zero) made each particle quad reset the alpha of what
            // is behind it - a see-through square in front of a cut-out.
            if (m.HasProperty("_SrcBlendAlpha")) m.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha")) m.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            m.renderQueue = (int)RenderQueue.Transparent;
            _material = m;
            return m;
        }

        static Texture2D SoftDot()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: true)
            {
                name = "WhatIfDot", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color(1f, 1f, 1f, a * a);
                }
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }
    }
}
