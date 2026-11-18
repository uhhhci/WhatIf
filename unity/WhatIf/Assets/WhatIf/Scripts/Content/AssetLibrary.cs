using System.Collections.Generic;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using WhatIf.Api;

namespace WhatIf.Content
{
    /// <summary>
    /// Downloads each generated asset once (texture / GLB) and hands out instances.
    /// </summary>
    public class AssetLibrary
    {
        readonly WhatIfClient _client;
        readonly Dictionary<string, Task<Texture2D>> _textures = new Dictionary<string, Task<Texture2D>>();
        readonly Dictionary<string, Task<GltfImport>> _models = new Dictionary<string, Task<GltfImport>>();

        public AssetLibrary(WhatIfClient client) => _client = client;

        public Task<Texture2D> Texture(AssetInfo info)
        {
            if (!_textures.TryGetValue(info.Id, out var t))
                _textures[info.Id] = t = _client.DownloadTexture(info.BillboardUrl);
            return t;
        }

        public async Task<GameObject> InstantiateModel(AssetInfo info)
        {
            if (!_models.TryGetValue(info.Id, out var load))
                _models[info.Id] = load = LoadGltf(_client.Absolute(info.ModelUrl));
            var gltf = await load;
            if (gltf == null) return null;
            var root = new GameObject("model");
            bool ok = await gltf.InstantiateMainSceneAsync(root.transform);
            if (!ok) { Object.Destroy(root); return null; }
            return root;
        }

        static async Task<GltfImport> LoadGltf(string url)
        {
            var gltf = new GltfImport();
            bool ok = await gltf.Load(url);
            if (!ok)
            {
                Debug.LogWarning($"[WhatIf] failed to load {url}");
                gltf.Dispose();
                return null;
            }
            return gltf;
        }

        public void Clear()
        {
            foreach (var t in _textures.Values)
                if (t.IsCompletedSuccessfully && t.Result != null) Object.Destroy(t.Result);
            foreach (var m in _models.Values)
                if (m.IsCompletedSuccessfully && m.Result != null) m.Result.Dispose();
            _textures.Clear();
            _models.Clear();
        }
    }
}
