using UnityEngine;
using UnityEngine.Rendering.Universal;
using WhatIf.Api;

namespace WhatIf.Content
{
    /// <summary>
    /// Turns an effect's building blocks (colours, pattern, motion, glow, reflection, edges,
    /// particles) into a material for the WhatIf/Surface shader and a particle system. Nothing here
    /// knows "water" or "lava": the planner composes those from the blocks.
    /// </summary>
    public static class SurfaceLook
    {
        static Shader _shader;

        public static Material CreateMaterial(Effect e)
        {
            if (_shader == null) _shader = Shader.Find("WhatIf/Surface");
            if (_shader == null)
            {
                // fallback: a plain transparent colour (the shader is in Resources, so this shouldn't happen)
                var plain = MaterialFactory.Transparent();
                var c = Hex(e.TintHex, Color.gray); c.a = e.Strength;
                MaterialFactory.SetColor(plain, c);
                return plain;
            }
            var mat = new Material(_shader) { name = "WhatIf Surface" };
            Apply(mat, e);
            return mat;
        }

        public static void Apply(Material mat, Effect e)
        {
            if (mat.shader != _shader || _shader == null)
            {
                var c = Hex(e.TintHex, Color.gray); c.a = e.Strength;
                MaterialFactory.SetColor(mat, c);
                return;
            }
            var main = Hex(e.TintHex, Color.gray);
            var c2 = e.Colors.Count > 0 ? Hex(e.Colors[0], main * 0.6f) : main * 0.85f;
            var c3 = e.Colors.Count > 1 ? Hex(e.Colors[1], main) : Color.Lerp(main, Color.white, 0.25f);
            mat.SetColor("_Color1", main);
            mat.SetColor("_Color2", c2);
            mat.SetColor("_Color3", c3);
            mat.SetFloat("_Opacity", Mathf.Clamp01(e.Strength));
            mat.SetFloat("_Pattern", e.Pattern switch { "noise" => 1, "cells" => 2, "waves" => 3, "stripes" => 4, "speckle" => 5, _ => 0 });
            mat.SetFloat("_Scale", Mathf.Max(0.02f, e.PatternScaleM));
            mat.SetFloat("_Motion", e.Motion switch { "flow" => 1, "ripple" => 2, "bubble" => 3, _ => 0 });
            mat.SetFloat("_Speed", e.Speed);
            mat.SetVector("_FlowDir", new Vector4(1f, 0.3f, 0f, 0f));
            mat.SetFloat("_Glow", Mathf.Clamp01(e.Glow));
            mat.SetColor("_GlowColor", Hex(e.GlowHex, c3));
            mat.SetFloat("_Reflect", Mathf.Clamp01(e.Reflect));
            mat.SetFloat("_Edge", e.Edge switch { "foam" => 1, "glow" => 2, "frost" => 3, _ => 0 });
            mat.SetColor("_EdgeColor", Hex(e.EdgeHex, e.Edge == "glow" ? Hex(e.GlowHex, c3) : Color.white));
            if (e.Edge != null && e.Edge != "none") NeedSceneDepth();
        }

        /// <summary>Particles over an area (a box): rise from its bottom, fall from its top, or float in it.</summary>
        /// <summary>Particles an object gives off (a fire pit's sparks, a fountain's spray): small,
        /// from its top, few.</summary>
        public static GameObject CreateEmitter(GeneratedObject o, string kind, string hex)
        {
            if (string.IsNullOrEmpty(kind) || kind == "none") return null;
            var e = new Effect { Particles = kind, ParticleHex = hex, ParticleAmount = 0.12f };
            float h = Mathf.Max(0.1f, o.Height), w = Mathf.Clamp(o.Footprint * 0.5f, 0.1f, 1.5f);
            var go = CreateParticles(e, o.transform.position + Vector3.up * h * 0.7f,
                                     new Vector3(w, Mathf.Min(1f, h), w), o.transform);
            if (go != null)
            {
                var main = go.GetComponent<ParticleSystem>().main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local; // moves with the object
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            }
            return go;
        }

        public static GameObject CreateParticles(Effect e, Vector3 center, Vector3 size, Transform parent)
        {
            if (string.IsNullOrEmpty(e.Particles) || e.Particles == "none") return null;
            var go = new GameObject("WhatIf Particles");
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.startSize = new ParticleSystem.MinMaxCurve(0.01f, 0.035f);
            // no launch speed: Unity's default (5 m/s along the box's Z) shot everything sideways;
            // the motion comes from velocityOverLifetime and noise only
            main.startSpeed = 0f;
            main.startColor = Hex(e.ParticleHex, Color.white);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 600;
            float amount = Mathf.Clamp01(e.ParticleAmount);
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            var emission = ps.emission;
            emission.rateOverTime = Mathf.Lerp(10f, 160f, amount);
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            // all three axes in the same mode (two constants), or Unity logs an error every frame
            vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
            var noise = ps.noise;
            noise.enabled = true;
            switch (e.Particles)
            {
                case "rise": // from the surface upwards (embers, bubbles, steam)
                    shape.scale = new Vector3(size.x, 0.05f, size.z);
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
                    vel.y = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
                    noise.strength = 0.15f;
                    break;
                case "fall": // from above downwards (snow, ash, petals)
                    go.transform.position = center + Vector3.up * size.y;
                    shape.scale = new Vector3(size.x, 0.05f, size.z);
                    main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
                    vel.y = new ParticleSystem.MinMaxCurve(-0.5f, -0.25f);
                    noise.strength = 0.3f;
                    break;
                default: // float in the volume (fireflies, dust)
                    go.transform.position = center + Vector3.up * size.y * 0.5f;
                    shape.scale = size;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
                    vel.y = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);
                    noise.strength = 0.25f;
                    break;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = LoadingEffect.ParticleMaterial();
            ps.Play();
            return go;
        }

        static void NeedSceneDepth()
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp &&
                !urp.supportsCameraDepthTexture)
                urp.supportsCameraDepthTexture = true;
            var cam = Camera.main;
            var data = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
            if (data != null) data.requiresDepthTexture = true;
        }

        public static Color Hex(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
    }
}
