using System.Linq;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEditor;
#if UNITY_EDITOR
using System.IO;
using UnityEngine.SceneManagement;
#endif

namespace VRCLightVolumes {
    [ExecuteAlways]
    public class LightVolume : MonoBehaviour {

        [Header("Volume Setup")]
        [Tooltip("Defines whether this volume can be moved in runtime. Disabling this option slightly improves performance.")]
        public bool Dynamic;
        [Tooltip("Additive volumes apply their light on top of others as an overlay. Useful for movable and togglable lights. They can also project light onto static lightmapped objects if the surface shader supports it.")]
        public bool Additive;
        [Tooltip("Multiplies the volume’s color by this value.")]
        [ColorUsage(showAlpha: false)]
        public Color Color = Color.white;
        [Tooltip("Brightness of the volume.")]
        public float Intensity = 1f;
        [Tooltip("Size in meters of this Light Volume's overlapping regions for smooth blending with other volumes.")]
        [Range(0, 1)] public float SmoothBlending = 0.25f;

        [Header("Baked Data")]
        [Tooltip("Texture3D with baked SH data required for future atlas packing. It won't be uploaded to VRChat. (L0r, L0g, L0b, L1r.z)")]
        public Texture3D Texture0;
        [Tooltip("Texture3D with baked SH data required for future atlas packing. It won't be uploaded to VRChat. (L1r.x, L1g.x, L1b.x, L1g.z)")]
        public Texture3D Texture1;
        [Tooltip("Texture3D with baked SH data required for future atlas packing. It won't be uploaded to VRChat. (L1r.y, L1g.y, L1b.y, L1b.z)")]
        public Texture3D Texture2;
        [Tooltip("Optional Texture3D with baked shadows data for future atlas packing. It won't be uploaded to VRChat. Stores occlusion for up to 4 nearby point light volumes.")]
        public Texture3D ShadowsTexture;

        [Header("Color Correction")]
        [Tooltip("Makes volume brighter or darker.\nUpdates volume color after atlas packing only!")]
        public float Exposure = 0;
        [Tooltip("Makes dark volume colors brighter or darker.\nUpdates volume color after atlas packing only!")]
        [Range(-1, 1)] public float Shadows = 0;
        [Tooltip("Makes bright volume colors brighter or darker.\nUpdates volume color after atlas packing only!")]
        [Range(-1, 1)] public float Highlights = 0;

        [Header("Baking Setup")]
        [Tooltip("Uncheck it if you don't want to rebake this volume's textures.")]
        public bool Bake = true;
        [Tooltip("Uncheck it if you don't want to rebake occlusion data required for baked point light volumes shadows.")]
        public bool PointLightShadows = true;
        [Tooltip("Post-processes the baked occlusion texture with a softening blur. This can help mitigate 'blocky' shadows caused by aliasing, but also makes shadows less crispy.")]
        public bool BlurShadows = true;
        [Tooltip("Automatically sets the resolution based on the Voxels Per Unit value.")]
        public bool AdaptiveResolution = true;
        [Tooltip("Number of voxels used per meter, linearly. This value increases the Light Volume file size cubically.")]
        public float VoxelsPerUnit = 3;
        [Tooltip("Manual Light Volume resolution in voxel count.")]
        public Vector3Int Resolution = new Vector3Int(16, 16, 16);

        public bool PreviewVoxels;
#if BAKERY_INCLUDED
        public BakeryVolume BakeryVolume;
#endif

        public LightVolumeInstance LightVolumeInstance;
        public LightVolumeSetup LightVolumeSetup;

        // Light probes world positions
        private Vector3[] _probesPositions = new Vector3[0];

        // To check if object was edited this frame
        private Vector3 _prevPos = Vector3.zero;
        private Quaternion _prevRot = Quaternion.identity;
        private Vector3 _prevScl = Vector3.one;

        private float _prevExposure = 0;
        private float _prevShadows = 0;
        private float _prevHighlights = 0;
        private float _lastTimeColorCorrection = 0; // Last time color correction values changed

        // Preview
        private Material _previewMaterial;
        private Mesh _previewMesh;
        private ComputeBuffer _posBuf;
        private ComputeBuffer _argsBuf;
        static readonly int _previewPosID = Shader.PropertyToID("_Positions");
        static readonly int _previewScaleID = Shader.PropertyToID("_Scale");

        // Was it changed on Validate?
        private bool _isValidated = false;

        // Auto-initialize with a reflection probe bounds
        public void Reset() {
            if (transform.parent != null && transform.parent.gameObject.TryGetComponent(out ReflectionProbe probe)) {
                transform.SetPositionAndRotation(probe.bounds.center, Quaternion.identity);
                LVUtils.SetLossyScale(transform, probe.bounds.size);
            }
        }

        // Position, Rotation and Scale of the final light volume, depending on the current setup
        public Vector3 GetPosition() {
            return transform.position;
        }
        public Vector3 GetScale() {
            return transform.lossyScale;
        }
        public Quaternion GetRotation() {
            SetupDependencies();
            if (LightVolumeSetup.IsBakeryMode && !Application.isPlaying && Bake) {
#if BAKERY_INCLUDED
                if (typeof(BakeryVolume).GetField("rotateAroundY") != null) { // Some Bakery versions does not support rotateAroundY, so we'll check it
                    return Quaternion.Euler(0, transform.rotation.eulerAngles.y, 0);
                } else {
                    return Quaternion.identity;
                }
#else
                return Quaternion.identity;
#endif
            } else {
                return transform.rotation;
            }
        }
        public Matrix4x4 GetMatrixTRS() {
            return Matrix4x4.TRS(GetPosition(), GetRotation(), GetScale());
        }

        // Returns volume voxel count. Returns -1 if a wrong voxels count
        public int GetVoxelCount(int padding = 0) {
            ulong voxels = (ulong)(Resolution.x + padding * 2) * (ulong)(Resolution.y + padding * 2) * (ulong)(Resolution.z + padding * 2);
            if (voxels > int.MaxValue || voxels < 0) {
                return -1;
            } else {
                return (int)voxels;
            }
        }

        // Looks for LightVolumeSetup and LightVolumeInstance udon script and setups them if needed
        public void SetupDependencies() {
            if (LightVolumeInstance == null && !TryGetComponent(out LightVolumeInstance)) {
                LightVolumeInstance = gameObject.AddComponent<LightVolumeInstance>();
            }
            if (LightVolumeSetup == null) {
                LightVolumeSetup = FindObjectOfType<LightVolumeSetup>();
                if (LightVolumeSetup == null) {
                    var go = new GameObject("Light Volume Manager");
                    LightVolumeSetup = go.AddComponent<LightVolumeSetup>();
                    LightVolumeSetup.SyncUdonScript();
                }
            }
        }

        // Sets Additional Probes to bake with Unity Lightmapper
#if UNITY_EDITOR
        public void SetAdditionalProbes(int id) {
            RecalculateProbesPositions();
            UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(id, _probesPositions);
        }

        public void RemoveAdditionalProbes(int id) {
            UnityEditor.Experimental.Lightmapping.SetAdditionalBakedProbes(id, new Vector3[0]);
        }

        public void BakeOcclusionTexture() {
            // Occlusion data is optional, check if requested and needed
            // Additive volumes don't get occlusion, because adding occlusion values doesn't make any sense
            bool needOcclusion = PointLightShadows && !Additive && LightVolumeSetup.PointLightVolumes.Any(l => l.BakedShadows);
            if (!needOcclusion) {
                if (ShadowsTexture != null)
                    LVUtils.MarkDirty(this);
                if (LightVolumeInstance.BakeOcclusion)
                    LVUtils.MarkDirty(LightVolumeInstance);
                ShadowsTexture = null;
                LightVolumeInstance.BakeOcclusion = false;
                return;
            }
            
            // Precompute some properties of each shadow casting light
            LightVolumeOcclusionBaker.ComputeLightProperties(
                LightVolumeSetup.PointLightVolumes,
                Resolution,
                transform.lossyScale, 
                LightVolumeSetup.AreaLightBrightnessCutoff + 0.05f,
                out float[] shadowLightInfluenceRadii,
                out float[] shadowLightRadii,
                out Vector2[] shadowLightArea);
            
            // Compute shadowmask indices and apply them to lights
            sbyte[] shadowmaskIndices = LightVolumeOcclusionBaker.ComputeShadowmaskIndices(LightVolumeSetup.PointLightVolumes, shadowLightInfluenceRadii);
            for (int lightIdx = 0; lightIdx < LightVolumeSetup.PointLightVolumes.Count; lightIdx++) {
                var instance = LightVolumeSetup.PointLightVolumes[lightIdx].PointLightVolumeInstance;
                if (instance != null && instance.ShadowmaskIndex == shadowmaskIndices[lightIdx])
                    continue;
                instance.ShadowmaskIndex = shadowmaskIndices[lightIdx];
                LVUtils.MarkDirty(instance);
            }
                
            // Bake occlusion
            Texture3D occ = LightVolumeOcclusionBaker.ComputeOcclusionTexture(
                Resolution,
                _probesPositions,
                LightVolumeSetup.PointLightVolumes,
                shadowLightInfluenceRadii,
                shadowLightRadii,
                shadowLightArea,
                BlurShadows);
            
            string path = $"{Path.GetDirectoryName(SceneManager.GetActiveScene().path)}/{SceneManager.GetActiveScene().name}/VRCLightVolumes/Temp";
            if (occ != null)
                LVUtils.SaveAsAsset(occ, $"{path}/{gameObject.name}_shadows.asset");
            
            ShadowsTexture = occ;
            LVUtils.MarkDirty(this);
            
            LightVolumeInstance.BakeOcclusion = occ != null;
            LVUtils.MarkDirty(LightVolumeInstance);
        }
#endif

        // Recalculates probes world positions
        public void RecalculateProbesPositions() {
            _probesPositions = new Vector3[GetVoxelCount()];
            Vector3 offset = new Vector3(0.5f, 0.5f, 0.5f);
            var pos = GetPosition();
            var rot = GetRotation();
            var scl = GetScale();
            int id = 0;
            Vector3 localPos;
            for (int z = 0; z < Resolution.z; z++) {
                for (int y = 0; y < Resolution.y; y++) {
                    for (int x = 0; x < Resolution.x; x++) {
                        localPos = new Vector3((float)(x + 0.5f) / Resolution.x, (float)(y + 0.5f) / Resolution.y, (float)(z + 0.5f) / Resolution.z) - offset;
                        _probesPositions[id] = LVUtils.TransformPoint(localPos, pos, rot, scl);
                        id++;
                    }
                }
            }
        }

        // Recalculates resolution based on Adaptive Resolution
        public void RecalculateAdaptiveResolution() {
            Vector3 count = Vector3.Scale(Vector3.one, GetScale()) * VoxelsPerUnit;
            int x = Mathf.Max((int)Mathf.Round(count.x), 1);
            int y = Mathf.Max((int)Mathf.Round(count.y), 1);
            int z = Mathf.Max((int)Mathf.Round(count.z), 1);
            Resolution = new Vector3Int(x, y, z);
        }

        // Recalculates adaptive resolution and local positions if required
        public void Recalculate() {
            if (AdaptiveResolution)
                RecalculateAdaptiveResolution();
            if (PreviewVoxels && Bake)
                RecalculateProbesPositions();
        }
#if UNITY_EDITOR
        // Saves additional probes data baked with Progressive Lightmapper
        public void Save3DTexturesProgressive(int id) {

            int vCount = GetVoxelCount();
            if (vCount < 0) {
                Debug.LogError($"[LightVolume] Can't save light volume {gameObject.name} 3D texture. Voxels count is too large!");
                return;
            }

            SetupDependencies();

            // Atlas Sizes
            int w = Resolution.x;
            int h = Resolution.y;
            int d = Resolution.z;

            // SH data output
            using (NativeArray<SphericalHarmonicsL2> probes = new NativeArray<SphericalHarmonicsL2>(vCount, Allocator.Temp))
            using (NativeArray<float> probesValidity = new NativeArray<float>(vCount, Allocator.Temp)) {

                // Checking data available
#pragma warning disable CS0618
                if (!UnityEditor.Experimental.Lightmapping.GetAdditionalBakedProbes(id, probes, probesValidity)) {
                    Debug.LogError("[LightVolume] Can't grab light volume data. No additional baked probes found!");
                    return;
                }
#pragma warning restore CS0618

                // Creating Texture3D with specified format and dimensions
                TextureFormat format = TextureFormat.RGBAHalf;
                Texture3D tex0 = new Texture3D(w, h, d, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                Texture3D tex1 = new Texture3D(w, h, d, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                Texture3D tex2 = new Texture3D(w, h, d, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

                // Quick shortcuts to SH L1 components
                const int r = 0;
                const int g = 1;
                const int b = 2;
                const int a = 0;
                const int x = 3;
                const int y = 1;
                const int z = 2;
                const float coeff = 1.65f; // To transform to bakery non-linear data format. Should be 1.7699115f actually

                // Separating data for dilation and denoising
                Vector3[] L0 = new Vector3[vCount];
                Vector3[] L1r = new Vector3[vCount];
                Vector3[] L1g = new Vector3[vCount];
                Vector3[] L1b = new Vector3[vCount];
                for (int i = 0; i < vCount; i++) {
                    L0[i] = new Vector3(probes[i][r, a], probes[i][g, a], probes[i][b, a]);
                    L1r[i] = new Vector3(probes[i][r, x], probes[i][r, y], probes[i][r, z]);
                    L1g[i] = new Vector3(probes[i][g, x], probes[i][g, y], probes[i][g, z]);
                    L1b[i] = new Vector3(probes[i][b, x], probes[i][b, y], probes[i][b, z]);
                }
                
                // Dilation
                if (LightVolumeSetup.DilateInvalidProbes)
                {
                    float[] validity = probesValidity.ToArray();
                    
                    // Outputs
                    float[] validityDilated = new float[vCount];
                    Vector3[] L0Dilated = new Vector3[vCount];
                    Vector3[] L1rDilated = new Vector3[vCount];
                    Vector3[] L1gDilated = new Vector3[vCount];
                    Vector3[] L1bDilated = new Vector3[vCount];
                    
                    // Initialize outputs with source data
                    System.Array.Copy(validity, validityDilated, vCount);
                    System.Array.Copy(L0, L0Dilated, vCount);
                    System.Array.Copy(L1r, L1rDilated, vCount);
                    System.Array.Copy(L1g, L1gDilated, vCount);
                    System.Array.Copy(L1b, L1bDilated, vCount);
                    
                    for (int iter = 0; iter < LightVolumeSetup.DilationIterations; iter++) {
                        for (int voxelZ = 0; voxelZ < d; voxelZ++)
                            for (int voxelY = 0; voxelY < h; voxelY++)
                                for (int voxelX = 0; voxelX < w; voxelX++) {
                                    int centerIdx = voxelX + voxelY * w + voxelZ * w * h;

                                    if (validity[centerIdx] < LightVolumeSetup.DilationBackfaceBias) continue;
                                    
                                    Vector3 L0Sum = Vector3.zero;
                                    Vector3 L1rSum = Vector3.zero;
                                    Vector3 L1gSum = Vector3.zero;
                                    Vector3 L1bSum = Vector3.zero;
                                    int validCount = 0;
                                    for (int dz = -1; dz <= 1; dz++)
                                        for (int dy = -1; dy <= 1; dy++)
                                            for (int dx = -1; dx <= 1; dx++) {
                                                int xx = voxelX + dx;
                                                int yy = voxelY + dy;
                                                int zz = voxelZ + dz;
                                                if (xx < 0 || yy < 0 || zz < 0 || xx >= w || yy >= h || zz >= d) continue;

                                                int nIdx = xx + yy * w + zz * w * h;
                                                float neighborValidity = validity[nIdx];
                                                if (neighborValidity < LightVolumeSetup.DilationBackfaceBias) {
                                                    validCount++;
                                                    L0Sum += L0[nIdx];
                                                    L1rSum += L1r[nIdx];
                                                    L1gSum += L1g[nIdx];
                                                    L1bSum += L1b[nIdx];
                                                }
                                            }

                                    if (validCount > 0) {
                                        L0Dilated[centerIdx] = L0Sum / validCount;
                                        L1rDilated[centerIdx] = L1rSum / validCount;
                                        L1gDilated[centerIdx] = L1gSum / validCount;
                                        L1bDilated[centerIdx] = L1bSum / validCount;
                                        validityDilated[centerIdx] = 0.0f;
                                    }
                                }
                        
                        // Copy outputs back to source data after each iteration
                        System.Array.Copy(validityDilated, validity, vCount);
                        System.Array.Copy(L0Dilated, L0, vCount);
                        System.Array.Copy(L1rDilated, L1r, vCount);
                        System.Array.Copy(L1gDilated, L1g, vCount);
                        System.Array.Copy(L1bDilated, L1b, vCount);
                    }
                }

                // Denoising
                if (LightVolumeSetup.Denoise) {
                    L0 = LVUtils.BilateralDenoise3D(L0, w, h, d, 1, 0.05f);
                    L1r = LVUtils.BilateralDenoise3D(L1r, w, h, d, 1, 0.05f);
                    L1g = LVUtils.BilateralDenoise3D(L1g, w, h, d, 1, 0.05f);
                    L1b = LVUtils.BilateralDenoise3D(L1b, w, h, d, 1, 0.05f);
                }

                // Setting voxel data
                Color[] c0 = new Color[vCount];
                Color[] c1 = new Color[vCount];
                Color[] c2 = new Color[vCount];
                for (int i = 0; i < vCount; i++) {
                    c0[i] = new Color(L0[i].x, L0[i].y, L0[i].z, L1r[i].z * coeff);
                    c1[i] = new Color(L1r[i].x * coeff, L1g[i].x * coeff, L1b[i].x * coeff, L1g[i].z * coeff);
                    c2[i] = new Color(L1r[i].y * coeff, L1g[i].y * coeff, L1b[i].y * coeff, L1b[i].z * coeff);
                }

                // Apply Pixel Data to Texture
                LVUtils.Apply3DTextureData(tex0, c0);
                LVUtils.Apply3DTextureData(tex1, c1);
                LVUtils.Apply3DTextureData(tex2, c2);

                // Saving 3D Texture assets
                string path = $"{Path.GetDirectoryName(SceneManager.GetActiveScene().path)}/{SceneManager.GetActiveScene().name}/VRCLightVolumes/Temp";
                LVUtils.SaveAsAsset(tex0, $"{path}/{gameObject.name}_0.asset");
                LVUtils.SaveAsAsset(tex1, $"{path}/{gameObject.name}_1.asset");
                LVUtils.SaveAsAsset(tex2, $"{path}/{gameObject.name}_2.asset");

                // Applying textures to volume
                Texture0 = tex0;
                Texture1 = tex1;
                Texture2 = tex2;

            }

            LVUtils.MarkDirty(this);
        }
#endif
        // Setups required game objects and components
        public void SetupBakeryDependencies() {

#if BAKERY_INCLUDED

            SetupDependencies();

            // Create or destroy Bakery Volume
            if (LightVolumeSetup.IsBakeryMode && Bake && BakeryVolume == null) {
                GameObject obj = new GameObject($"Bakery Volume - {gameObject.name}");
                obj.tag = "EditorOnly";
                obj.transform.parent = transform;
                BakeryVolume = obj.AddComponent<BakeryVolume>();
                LVUtils.MarkDirty(this);
            } else if ((!LightVolumeSetup.IsBakeryMode || !Bake) && BakeryVolume != null) {
                if (Application.isPlaying) {
                    Destroy(BakeryVolume.gameObject);
                } else {
#if UNITY_EDITOR
                    // Do not destroy game object if it is part of prefab instance since it may disconnects/breaks the prefab
                    DestroyImmediate(PrefabUtility.IsPartOfPrefabInstance(BakeryVolume.gameObject) ? BakeryVolume : BakeryVolume.gameObject);
#else
                    Destroy(BakeryVolume.gameObject);
#endif
                }
                BakeryVolume = null;
                LVUtils.MarkDirty(this);
            }

            if (LightVolumeSetup.IsBakeryMode && BakeryVolume != null) {
                // Sync bakery volume with light volume
                BakeryVolume.gameObject.name = $"Bakery Volume - {gameObject.name}";
                BakeryVolume.gameObject.tag = "EditorOnly";
                if (BakeryVolume.transform.parent != transform) BakeryVolume.transform.parent = transform;
                BakeryVolume.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                BakeryVolume.transform.localScale = Vector3.one;
                BakeryVolume.bounds = new Bounds(GetPosition(), GetScale());
                BakeryVolume.enableBaking = true;
                BakeryVolume.denoise = LightVolumeSetup.Denoise;
                BakeryVolume.adaptiveRes = false;
                BakeryVolume.resolutionX = Resolution.x;
                BakeryVolume.resolutionY = Resolution.y;
                BakeryVolume.resolutionZ = Resolution.z;
                BakeryVolume.encoding = BakeryVolume.Encoding.Half4;

                // Even some latest Bakery versions does not support Rotate Around Y
                var bakeryRotationYfield = typeof(BakeryVolume).GetField("rotateAroundY");
                if (bakeryRotationYfield != null) bakeryRotationYfield.SetValue(BakeryVolume, true);

                LVUtils.MarkDirty(BakeryVolume);
            }

            SyncUdonScript();

#endif

        }

        // Syncs udon LightVolumeInstance script with this script
        private void SyncUdonScript() {
            SetupDependencies();
            LightVolumeInstance.IsDynamic = Dynamic;
            LightVolumeInstance.IsAdditive = Additive;
            LightVolumeInstance.Color = Color;
            LightVolumeInstance.ColorIntensity = Intensity;
            LightVolumeInstance.SetSmoothBlending(SmoothBlending);
        }

#if UNITY_EDITOR

        private void Update() {

            SetupDependencies();

#if BAKERY_INCLUDED
            if (Bake && (Texture0 == null || Texture1 == null || Texture2 == null) && LightVolumeSetup.IsBakeryMode && BakeryVolume != null && BakeryVolume.bakedTexture0 != null) {
                Texture0 = BakeryVolume.bakedTexture0;
                Texture1 = BakeryVolume.bakedTexture1;
                Texture2 = BakeryVolume.bakedTexture2;
                LVUtils.MarkDirty(this);
            }
#endif

            // Update udon Behaviour if Volume changed transform
            if (_prevPos != transform.position || _prevRot != transform.rotation || _prevScl != transform.localScale || _isValidated) {
                SetupBakeryDependencies();
                Recalculate();
                if (PreviewVoxels) ReleasePreviewBuffers();
                _prevPos = transform.position;
                _prevRot = transform.rotation;
                _prevScl = transform.localScale;
                _isValidated = false;
            }

            // Regenerating atlas if color correction values were changed and 0.5 seconds delay passed
            if (_prevExposure != Exposure || _prevHighlights != Highlights || _prevShadows != Shadows) {
                
                _prevExposure = Exposure;
                _prevHighlights = Highlights;
                _prevShadows = Shadows;
                _lastTimeColorCorrection = Time.time;
            }
            if(_lastTimeColorCorrection > 0 && Time.time > _lastTimeColorCorrection + 0.5f) {
                _lastTimeColorCorrection = 0;
                LightVolumeSetup.GenerateAtlas();
            }

            SyncUdonScript();
            LightVolumeSetup.SyncUdonScript();

            // If voxels preview disabled
            if (!PreviewVoxels || _probesPositions.Length == 0 || Selection.activeGameObject != gameObject) return;

            Vector3[] pPositions; // Draw only 1000 000 first probes!
            if(_probesPositions.Length > 1000000) {
                pPositions = new Vector3[1000000];
                System.Array.Copy(_probesPositions, pPositions, 1000000);
            } else {
                pPositions = _probesPositions;
            }

            // Initialize Buffers
            if (_posBuf == null || _posBuf.count != pPositions.Length) {
                ReleasePreviewBuffers();
                _posBuf = new ComputeBuffer(pPositions.Length, sizeof(float) * 3);
                _argsBuf = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            }

            // Generate Sphere mesh
            if (_previewMesh == null) {
                _previewMesh = LVUtils.GenerateIcoSphere(0.5f, 0);
            }

            // Create Material
            if (_previewMaterial == null) {
                _previewMaterial = new Material(Shader.Find("Hidden/LightVolumesPreview"));
            }

            // Calculating radius
            Vector3 scale = GetScale();
            Vector3 res = Resolution;
            float radius = Mathf.Min(scale.z / res.z, Mathf.Min(scale.x / res.x, scale.y / res.y)) / 3;

            // Setting data to buffers
            _posBuf.SetData(pPositions);
            _previewMaterial.SetBuffer(_previewPosID, _posBuf);
            _previewMaterial.SetFloat(_previewScaleID, radius);
            _argsBuf.SetData(new uint[] { _previewMesh.GetIndexCount(0), (uint)pPositions.Length, _previewMesh.GetIndexStart(0), (uint)_previewMesh.GetBaseVertex(0), 0 });

            Bounds bounds = LVUtils.BoundsFromTRS(GetMatrixTRS());
            Graphics.DrawMeshInstancedIndirect(_previewMesh, 0, _previewMaterial, bounds, _argsBuf, 0, null, ShadowCastingMode.Off, false, gameObject.layer);

        }

        // Releases compute buffer
        void ReleasePreviewBuffers() {
            if (_posBuf != null) { _posBuf.Release(); _posBuf = null; }
            if (_argsBuf != null) { _argsBuf.Release(); _argsBuf = null; }
        }

        private void Awake() {
            _prevPos = transform.position;
            _prevRot = transform.rotation;
            _prevScl = transform.localScale;
            _isValidated = false;
            _prevExposure = Exposure;
            _prevHighlights = Highlights;
            _prevShadows = Shadows;
            _lastTimeColorCorrection = 0;
        }

        private void OnEnable() {
            SetupDependencies();
            SetupBakeryDependencies();
            LightVolumeSetup.RefreshVolumesList();
            LightVolumeSetup.SyncUdonScript();
        }

        private void OnDisable() {
            if (LightVolumeSetup != null) {
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
            if (PreviewVoxels)
                ReleasePreviewBuffers();
        }

        private void OnDestroy() {
            if (LightVolumeSetup != null) {
                LightVolumeSetup.RefreshVolumesList();
                LightVolumeSetup.SyncUdonScript();
            }
            if (PreviewVoxels)
                ReleasePreviewBuffers();
        }

        private void OnValidate() {
            _isValidated = true;
            Recalculate();
        }
#endif

    }
}