using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GLTFast;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WhatIf.Content;

namespace WhatIf.EditorTools
{
    /// <summary>
    /// Menu: What If > Include Shaders for Build. The app makes all its materials at runtime (cut-outs,
    /// paint, shadows, particles, and glTFast's materials for the 3D models), so a player build
    /// doesn't know which shaders and keyword variants it needs: missing ones render pink.
    /// This saves one placeholder material per kind into a Resources folder (glTFast's recommended
    /// alternative to a shader variant collection), which puts exactly those variants into the build.
    /// The 3D model materials are taken from models the server generated (server/data/assets), so
    /// run it once after the server made a few models, and again after switching the 3D provider.
    /// </summary>
    [InitializeOnLoad]
    public static class ShaderVariantMaterials
    {
        const string Folder = "Assets/WhatIf/Resources/WhatIfShaderVariants";
        const int MaxModels = 12;

        // Runs once by itself (when the folder has no 3D model materials yet), so a fresh
        // checkout builds without pink materials; the menu item refreshes it.
        static ShaderVariantMaterials()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode) return;
                if (AssetDatabase.FindAssets("Variant t:Material", new[] { Folder }).Length > 0) return;
                Create(false);
            };
        }

        [MenuItem("What If/Include Shaders for Build")]
        static void CreateFromMenu() => Create(true);

        public static async void Create(bool interactive)
        {
            var log = new StringBuilder();
            if (!AssetDatabase.IsValidFolder("Assets/WhatIf/Resources")) AssetDatabase.CreateFolder("Assets/WhatIf", "Resources");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/WhatIf/Resources", "WhatIfShaderVariants");
            var seen = new HashSet<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Folder }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m != null) seen.Add(Key(m));
            }

            // What If's own materials
            int added = 0;
            added += Save(MaterialFactory.Transparent(), "Transparent", seen);
            added += Save(MaterialFactory.AlphaClip(), "Cutout", seen);
            added += Save(MaterialFactory.Opaque(), "Opaque", seen);
            added += Save(Particles(), "Particles", seen);
            log.AppendLine($"What If materials: {added} new");

            // glTFast materials of generated 3D models
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../server/data/assets"));
            var models = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("model.glb", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxModels).ToList()
                : new List<FileInfo>();
            if (models.Count == 0) log.AppendLine("! No generated 3D models in " + dir + " - generate a few with the server, then run this again.");
            int fromModels = 0;
            for (int i = 0; i < models.Count; i++)
            {
                EditorUtility.DisplayProgressBar("What If", "Reading " + models[i].Directory.Name, (float)i / models.Count);
                var gltf = new GltfImport(deferAgent: new UninterruptedDeferAgent());
                try
                {
                    if (!await gltf.Load(new System.Uri(models[i].FullName).AbsoluteUri)) continue;
                    for (int m = 0; m < gltf.MaterialCount; m++)
                    {
                        var mat = gltf.GetMaterial(m);
                        if (mat != null) fromModels += Save(mat, "Model", seen);
                    }
                    var def = gltf.GetDefaultMaterial();
                    if (def != null) fromModels += Save(def, "ModelDefault", seen);
                }
                finally { gltf.Dispose(); }
            }
            // Every kind of glTF material foreign models (Objaverse) bring, from tiny test models: both
            // workflows and unlit (KHR_materials_unlit, common in scans) x opaque / cut-out (leaf cards) /
            // transparent x one- / double-sided x texture sets. Without them such materials rendered
            // black, flat grey or not at all on the headset.
            int fromVariants = 0, n = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "whatif_gltf_variants");
            Directory.CreateDirectory(tmp);
            foreach (string kind in new[] { "metal", "specgloss", "unlit" })
            foreach (string alpha in new[] { "OPAQUE", "MASK", "BLEND" })
            foreach (bool doubleSided in new[] { false, true })
            for (int textures = 0; textures < 4; textures++)
            {
                EditorUtility.DisplayProgressBar("What If", "glTF material variants", n++ / 72f);
                var file = Path.Combine(tmp, $"v{n}.gltf");
                File.WriteAllText(file, VariantGltf(kind, alpha, doubleSided, textures));
                var gltf = new GltfImport(deferAgent: new UninterruptedDeferAgent());
                try
                {
                    if (!await gltf.Load(new System.Uri(file).AbsoluteUri)) continue;
                    for (int m = 0; m < gltf.MaterialCount; m++)
                        fromVariants += Save(gltf.GetMaterial(m), "Variant", seen);
                }
                finally { gltf.Dispose(); }
            }
            EditorUtility.ClearProgressBar();
            log.AppendLine($"3D model materials: {fromModels} new (from {models.Count} models)");
            log.AppendLine($"glTF material variants: {fromVariants} new (72 combinations)");
            AssetDatabase.SaveAssets();

            Debug.Log("[WhatIf] Include Shaders for Build:\n" + log);
            if (interactive)
                EditorUtility.DisplayDialog("What If - shaders", log + "\nThey're in " + Folder + ". Build again.", "OK");
        }

        const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        /// <summary>A one-triangle glTF with one material of the given kind (metal, specgloss, unlit;
        /// textures: 0 none, 1 colour, 2 + normal map, 3 + metal/gloss, occlusion and emission).</summary>
        static string VariantGltf(string kind, string alpha, bool doubleSided, int textures)
        {
            bool specGloss = kind == "specgloss", unlit = kind == "unlit";
            var data = new List<byte>();
            void Floats(params float[] v) { foreach (var f in v) data.AddRange(System.BitConverter.GetBytes(f)); }
            Floats(0, 0, 0, 1, 0, 0, 0, 1, 0);   // positions
            Floats(0, 0, 1, 0, 0, 1, 0, 0, 1);   // normals
            Floats(0, 0, 1, 0, 0, 1);            // uvs
            string buffer = System.Convert.ToBase64String(data.ToArray());
            string tex(string name) => $"\"{name}\":{{\"index\":0}}";
            var mat = new List<string>();
            if (specGloss)
            {
                var sg = new List<string> { "\"diffuseFactor\":[1,1,1,1]" };
                if (textures >= 1) sg.Add(tex("diffuseTexture"));
                if (textures >= 3) sg.Add(tex("specularGlossinessTexture"));
                mat.Add("\"extensions\":{\"KHR_materials_pbrSpecularGlossiness\":{" + string.Join(",", sg) + "}}");
            }
            else
            {
                var pbr = new List<string> { "\"baseColorFactor\":[1,1,1,1]" };
                if (textures >= 1) pbr.Add(tex("baseColorTexture"));
                if (textures >= 3) pbr.Add(tex("metallicRoughnessTexture"));
                mat.Add("\"pbrMetallicRoughness\":{" + string.Join(",", pbr) + "}");
            }
            if (unlit) mat.Add("\"extensions\":{\"KHR_materials_unlit\":{}}");
            if (textures >= 2) mat.Add(tex("normalTexture"));
            if (textures >= 3) { mat.Add(tex("occlusionTexture")); mat.Add(tex("emissiveTexture")); mat.Add("\"emissiveFactor\":[1,1,1]"); }
            mat.Add($"\"alphaMode\":\"{alpha}\"");
            if (alpha == "MASK") mat.Add("\"alphaCutoff\":0.5");
            if (doubleSided) mat.Add("\"doubleSided\":true");
            return "{\"asset\":{\"version\":\"2.0\"}," +
                   (specGloss ? "\"extensionsUsed\":[\"KHR_materials_pbrSpecularGlossiness\"]," :
                    unlit ? "\"extensionsUsed\":[\"KHR_materials_unlit\"]," : "") +
                   "\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                   "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"material\":0}]}]," +
                   $"\"buffers\":[{{\"byteLength\":{data.Count},\"uri\":\"data:application/octet-stream;base64,{buffer}\"}}]," +
                   "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":72,\"byteLength\":24}]," +
                   "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}," +
                   "{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"}]," +
                   $"\"images\":[{{\"uri\":\"data:image/png;base64,{Png1x1}\"}}],\"samplers\":[{{}}],\"textures\":[{{\"source\":0,\"sampler\":0}}]," +
                   "\"materials\":[{" + string.Join(",", mat) + "}]}";
        }

        static string Key(Material m)
        {
            var kw = m.shaderKeywords.OrderBy(k => k);
            return m.shader.name + "|" + string.Join(" ", kw) + "|" + m.renderQueue;
        }

        /// <summary>Copies the material without its textures (runtime textures can't be saved).</summary>
        static int Save(Material source, string name, HashSet<string> seen)
        {
            if (source == null || source.shader == null) return 0;
            if (!seen.Add(Key(source))) return 0;
            var copy = new Material(source) { name = name };
            foreach (var p in copy.GetTexturePropertyNames()) copy.SetTexture(p, null);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{Folder}/{name}.mat");
            AssetDatabase.CreateAsset(copy, path);
            return 1;
        }

        // Same settings as LoadingEffect's material (additive transparent particles).
        static Material Particles()
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) return null;
            var m = new Material(shader);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.One);
            m.SetInt("_ZWrite", 0);
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }
    }
}
