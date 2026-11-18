using UnityEngine;

namespace WhatIf.Content
{
    /// <summary>
    /// One placed instance. Starts as a 2D cut-out (billboard) and can be upgraded to a
    /// 3D model later. The root's pivot is the object's base; world height = Height.
    /// </summary>
    public class GeneratedObject : MonoBehaviour
    {
        public string AssetId;
        public float Height = 1f;
        public bool OnWall;
        public bool Hanging;               // hangs from the ceiling
        public float ShadowStrength = 0.5f; // 0 = no contact shadow
        public float Footprint = 1f;        // largest horizontal size, for the loading effect
        // Identity for editing by voice
        public string InstanceId;           // e.g. "chair#2"
        public string Group;                // item id shared by all instances of one kind
        public string Label;
        public string AssetPrompt;
        public Api.AssetInfo Asset;         // what it currently shows (for undo)
        bool _replacing;                    // next ShowBillboard replaces the current look entirely
        public bool Expect3D;               // a 3D model will replace the cut-out
        public bool Mirror;                 // shown mirrored (variation among many copies of a kind)
        public int ModelTriangles { get; private set; } // of the 3D model shown (0: none)

        LoadingEffect _fx;

        Transform _billboard;
        Transform _model;
        Transform _shadow;
        Transform _cam;

        static Mesh _quad;
        static Mesh _flatQuad;
        static Material _shadowMaterial;
        static float _shadowMaterialStrength = -1f;

        /// <summary>Particles at this spot until the cut-out (and the 3D model, if any) arrive.</summary>
        /// <summary>One of many small things: appears without loading particles (dozens of them
        /// made the whole room fizz).</summary>
        public bool Filler;
        /// <summary>A piece of a coat or drape (moss, ivy): never an obstacle for other objects.</summary>
        public bool Coat;
        /// <summary>A coat piece: its cut-out (a top-down picture) lies flat on the surface, Footprint
        /// wide, instead of standing up and turning to the viewer.</summary>
        public bool LieFlat;

        public void BeginLoading()
        {
            if (_fx != null || Filler) return;
            _fx = LoadingEffect.Attach(transform, Height, Footprint);
            _fx.SetPhase(LoadingEffect.Phase.Column);
        }

        /// <summary>Ends the loading effect, e.g. when generation finished, failed or timed out.</summary>
        public void EndLoading()
        {
            if (_fx == null) return;
            _fx.Stop();
            _fx = null;
        }

        /// <summary>World-space bounds of what it shows (falls back to its planned size).</summary>
        public Bounds WorldBounds()
        {
            Bounds? b = null;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is ParticleSystemRenderer || r.gameObject.name == "contact shadow") continue;
                if (b == null) b = r.bounds;
                else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
            }
            if (b.HasValue) return b.Value;
            float s = transform.lossyScale.y;
            return new Bounds(transform.position + Vector3.up * Height * s * 0.5f, new Vector3(Footprint, Height, Footprint) * s);
        }

        /// <summary>
        /// Restyle / undo: keep showing the current look until the new cut-out arrives, then replace
        /// the cut-out and drop the old 3D model.
        /// </summary>
        public void BeginReplace(bool showEffect)
        {
            _replacing = true;
            if (showEffect && _fx == null)
            {
                _fx = LoadingEffect.Attach(transform, Height, Footprint);
                _fx.SetPhase(LoadingEffect.Phase.Shimmer);
            }
        }

        public void ShowBillboard(Texture2D tex, float aspect, Material template)
        {
            if (_replacing)
            {
                // A 3D model stays until the new 3D model replaces it ("give the giraffe a hat" must not
                // fall back to a flat cut-out in between). The new cut-out is kept hidden, in case the
                // new model fails (FinishReplace). Without a model or a model to come: swap now.
                if (_model != null && Expect3D) { }
                else
                {
                    _replacing = false;
                    if (_model != null) { Destroy(_model.gameObject); _model = null; }
                }
            }
            if (_billboard != null) Destroy(_billboard.gameObject);
            var go = new GameObject("billboard", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = LieFlat ? FlatQuadMesh() : QuadMesh();
            var mat = new Material(template);
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (LieFlat)
            {
                // a patch on the surface: Footprint across its longer side; a few millimetres up, with a
                // little random offset so overlapping patches don't flicker against each other
                float w = Footprint > 0f ? Footprint : 0.3f;
                go.transform.localScale = aspect >= 1f ? new Vector3(w, 1f, w / aspect) : new Vector3(w * aspect, 1f, w);
                go.transform.localPosition = new Vector3(0f, 0.004f + UnityEngine.Random.value * 0.004f, 0f);
                _billboard = go.transform;
                if (_fx != null && _model == null) EndLoading();
                return;
            }
            Height = FitHeight(aspect);
            go.transform.localScale = new Vector3(Height * aspect, Height, 1f);
            if (OnWall)
            {
                // Root forward points out of the wall; the quad's visible side is -Z, so turn it around.
                go.transform.localPosition = new Vector3(0, 0, 0.01f); // avoid z-fighting
                go.transform.localRotation = Quaternion.Euler(0, 180f, 0);
            }
            _billboard = go.transform;
            if (_fx != null && _model == null)
            {
                _fx.Burst(big: false);
                if (Expect3D) _fx.SetPhase(LoadingEffect.Phase.Shimmer);
                else EndLoading();
            }
            if (_model != null) go.SetActive(false);
            else
            {
                // The billboard turns to face the viewer, so a round shadow fits from every side.
                float d = 0.8f * Height * aspect;
                SetShadow(d, d);
            }
        }

        /// <summary>
        /// End of a restyle without a new 3D model (failed or timed out): show the new cut-out
        /// instead of keeping the old model, so the change is still visible.
        /// </summary>
        public void FinishReplace()
        {
            if (!_replacing) return;
            _replacing = false;
            if (_billboard == null) return; // nothing new arrived: keep the old look
            if (_model != null) { Destroy(_model.gameObject); _model = null; }
            _billboard.gameObject.SetActive(true);
            float d = 0.8f * _billboard.localScale.x;
            SetShadow(d, d);
        }

        public static int CountTriangles(GameObject go)
        {
            long n = 0;
            foreach (var f in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = f.sharedMesh;
                if (m == null) continue;
                for (int i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i) / 3;
            }
            return (int)Mathf.Min(n, int.MaxValue);
        }

        /// <summary>Generated models are matte: their glossy maps made highlights sparkle with every
        /// small head movement on the headset (seen on a statue-like figure).</summary>
        /// <summary>
        /// Models arrive 1 m tall - measured on the server without their skeleton. Rigged characters
        /// often have a skeleton scaled by 100 (centimetres), so the shown model was hundreds of metres
        /// tall (seen: a giant Santa Claus, and "make him two metres tall" changed nothing). The model
        /// as shown is measured here, skinned meshes in their actual pose, and fitted to the planned
        /// height, standing on the ground and centred.
        /// </summary>
        System.Collections.IEnumerator FitWhenDrawn(GameObject model)
        {
            // one frame later: skinned meshes have then been posed by Unity, so their bounds are what is
            // drawn (baking them right after loading gave wrong sizes - seen: a dog fitted from 0.86 m and
            // then hovering above the floor)
            yield return null;
            if (model == null || _model == null || model.transform != _model) yield break;
            FitShown(model);
        }

        void FitShown(GameObject model)
        {
            var b = ShownBounds(model);
            if (!b.HasValue || b.Value.size.y < 1e-4f) return;
            float want = Height * transform.lossyScale.y;
            float have = b.Value.size.y;
            if (have > want * 1.25f || have < want * 0.75f)
            {
                model.transform.localScale *= want / have;
                Debug.Log($"[WhatIf] {name}: model was {have:F2} m tall as shown (rig or units), fitted to {want:F2} m");
                b = ShownBounds(model);
                if (!b.HasValue) return;
            }
            // on the ground and centred: the server centred it without its skeleton too
            var shift = new Vector3(transform.position.x - b.Value.center.x, transform.position.y - b.Value.min.y,
                                    transform.position.z - b.Value.center.z);
            if (shift.sqrMagnitude > 1e-6f) model.transform.position += shift;
        }

        /// <summary>World bounds of what a model draws (Unity's own bounds; skinned meshes as posed),
        /// without its shadow copies.</summary>
        static Bounds? ShownBounds(GameObject model)
        {
            Bounds? b = null;
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r is ParticleSystemRenderer || r.gameObject.name == "shadow") continue;
                if (b == null) b = r.bounds; else { var c = b.Value; c.Encapsulate(r.bounds); b = c; }
            }
            return b;
        }

        /// <summary>Shadows of 3D models on the floor (set from the settings at start).</summary>
        public static bool CastShadows;
        static Material _planarShadow;

        /// <summary>
        /// The model's shadow on the floor: a copy of each mesh drawn with WhatIf/PlanarShadow, which
        /// flattens it onto the floor along the room's light direction (PlanarShadows). No Unity shadow
        /// maps: the objects keep their standard lighting and get no shadows on themselves.
        /// </summary>
        static void AddShadowCasters(GameObject model)
        {
            foreach (var any in model.GetComponentsInChildren<Renderer>(true))
            {
                any.receiveShadows = false;
                any.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (!CastShadows) return;
            if (_planarShadow == null)
            {
                var shader = Shader.Find("WhatIf/PlanarShadow");
                if (shader == null) { CastShadows = false; Debug.LogWarning("[WhatIf] shadows: planar shadow shader missing"); return; }
                _planarShadow = new Material(shader) { name = "WhatIf Planar Shadow", enableInstancing = true };
            }
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var go = new GameObject("shadow");
                go.transform.SetParent(mf.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var sr = go.AddComponent<MeshRenderer>();
                var mats = new Material[Mathf.Max(1, mf.sharedMesh.subMeshCount)];
                for (int i = 0; i < mats.Length; i++) mats[i] = _planarShadow;
                sr.sharedMaterials = mats;
                sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sr.receiveShadows = false;
            }
        }

        static void Matte(GameObject model)
        {
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.materials)
                {
                    if (m.HasProperty("metallicRoughnessTexture")) m.SetTexture("metallicRoughnessTexture", null);
                    if (m.HasProperty("metallicFactor")) m.SetFloat("metallicFactor", 0f);
                    if (m.HasProperty("roughnessFactor")) m.SetFloat("roughnessFactor", Mathf.Max(0.85f, m.GetFloat("roughnessFactor")));
                    // no normal maps: low-poly downloaded models without tangents got dark triangles all over
                    // (seen: an elephant); the detail they add hardly shows in AR
                    if (m.HasProperty("normalTexture")) m.SetTexture("normalTexture", null);
                    if (m.HasProperty("normalTexture_scale")) m.SetFloat("normalTexture_scale", 0f);
                    if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
                    m.DisableKeyword("_NORMALMAP");
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", Mathf.Min(0.15f, m.GetFloat("_Smoothness")));
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                }
        }

        /// <summary>
        /// Objects are scaled by their height; flat, wide things (a stack of mats, a carpet, a pool)
        /// then came out tiny when the picture is wider than tall. If the planned footprint asks for
        /// more, it wins (at most 3x the planned height).
        /// </summary>
        float FitHeight(float widthPerHeight)
        {
            if (Footprint <= 0f || widthPerHeight <= 0.01f || Height <= 0f) return Height;
            float width = Height * widthPerHeight;
            return width < Footprint * 0.7f ? Mathf.Min(Footprint / widthPerHeight, Height * 3f) : Height;
        }

        public void ShowModel(GameObject model)
        {
            _replacing = false;
            if (_model != null) Destroy(_model.gameObject);
            model.transform.SetParent(transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = new Vector3(Mirror ? -Height : Height, Height, Height); // models are 1 m
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
            StartCoroutine(FitWhenDrawn(model));
            ModelTriangles = CountTriangles(model);
            Matte(model);
            AddShadowCasters(model);
            bool sideView = AssetPrompt != null && AssetPrompt.ToLowerInvariant().Contains("facing right");
            PartMotion.Attach(model, sideView); // parts the server cut out (tail, wheels, wings) move
            _model = model.transform;
            if (_billboard != null) _billboard.gameObject.SetActive(false);
            if (_fx != null) { _fx.Burst(big: true); EndLoading(); }

            // Footprint in the object's own axes: measure with the root unrotated, then restore.
            var rot = transform.rotation;
            transform.rotation = Quaternion.identity;
            Bounds? b = null;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (b == null) b = r.bounds;
                else { var bb = b.Value; bb.Encapsulate(r.bounds); b = bb; }
            }
            transform.rotation = rot;
            if (b.HasValue) SetShadow(1.1f * b.Value.size.x, 1.1f * b.Value.size.z);
        }

        /// <summary>
        /// Soft dark ellipse on the ground under the object: makes it look like it stands there.
        /// On Magic Leap 2 it is only visible with segmented dimming (dark = transparent on an
        /// additive display; the shadow's alpha drives the dimmer instead).
        /// </summary>
        void SetShadow(float sizeX, float sizeZ)
        {
            if (OnWall || Hanging || ShadowStrength <= 0f || sizeX <= 0f || sizeZ <= 0f) return;
            // a shadow where it touches the ground, not under a whole canopy: a tree's 4 m crown made a
            // floor-wide grey patch (dimming turns the shadow's alpha into grey on the headset)
            float cap = Mathf.Min(1.5f, Mathf.Max(0.2f, Footprint * 1.1f));
            sizeX = Mathf.Min(sizeX, cap);
            sizeZ = Mathf.Min(sizeZ, cap);
            if (_shadow == null)
            {
                var go = new GameObject("contact shadow", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.005f, 0f); // just above the ground
                go.GetComponent<MeshFilter>().sharedMesh = FlatQuadMesh();
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = ShadowMaterial(ShadowStrength);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _shadow = go.transform;
            }
            _shadow.localScale = new Vector3(sizeX, 1f, sizeZ);
        }

        void Start()
        {
            var cam = Camera.main;
            if (cam != null) _cam = cam.transform;
        }

        void LateUpdate()
        {
            // Cylindrical billboarding: rotate around the up axis to face the viewer.
            // Wall items keep facing out of the wall.
            if (_billboard == null || !_billboard.gameObject.activeSelf || OnWall || LieFlat || _cam == null) return;
            var to = _cam.position - _billboard.position;
            to.y = 0;
            if (to.sqrMagnitude > 1e-4f)
                _billboard.rotation = Quaternion.LookRotation(-to.normalized, Vector3.up);
        }

        /// <summary>Unit quad lying in the XZ plane, centred, facing up.</summary>
        static Mesh FlatQuadMesh()
        {
            if (_flatQuad != null) return _flatQuad;
            _flatQuad = new Mesh { name = "WhatIfShadow" };
            _flatQuad.vertices = new[]
            {
                new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f),
            };
            _flatQuad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _flatQuad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _flatQuad.RecalculateNormals();
            _flatQuad.RecalculateBounds();
            return _flatQuad;
        }

        /// <summary>One shared material + radial-gradient texture for all shadows.</summary>
        static Material ShadowMaterial(float strength)
        {
            if (_shadowMaterial != null && Mathf.Approximately(_shadowMaterialStrength, strength)) return _shadowMaterial;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: true)
            {
                name = "WhatIfShadow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color(0f, 0f, 0f, (1f - r) * (1f - r) * strength);
                }
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);

            _shadowMaterial = MaterialFactory.Transparent();
            MaterialFactory.SetTexture(_shadowMaterial, tex, Vector2.one);
            MaterialFactory.SetColor(_shadowMaterial, Color.white); // colour/alpha come from the texture
            _shadowMaterialStrength = strength;
            return _shadowMaterial;
        }

        /// <summary>Unit quad with its pivot at the bottom centre, facing -Z (towards a viewer at -Z).</summary>
        static Mesh QuadMesh()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "WhatIfBillboard" };
            _quad.vertices = new[]
            {
                new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0.5f, 1, 0), new Vector3(-0.5f, 1, 0),
            };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _quad.RecalculateNormals();
            _quad.RecalculateBounds();
            return _quad;
        }
    }
}
