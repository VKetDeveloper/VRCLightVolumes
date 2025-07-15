﻿using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace VRCLightVolumes {
    public struct Atlas3D {
        public Texture3D Texture;
        public Vector3[] BoundsUvwMin;
        public Vector3[] BoundsUvwMax;
    }

    public static class Texture3DAtlasGenerator {

        const int maxAtlasSize = 2048;

        public static event Action<LightVolume[]> OnPreAtlasCreate;

        public static IEnumerator CreateAtlas(LightVolume[] volumes, Action<Atlas3D> onComplete) {

            Texture3D[] texs = null;
            const int padding = 1;

            try {

                OnPreAtlasCreate?.Invoke(volumes);

                // Stacking textures into array
                Texture3D[] textures = new Texture3D[volumes.Length * 4];
                for (int i = 0; i < volumes.Length; i++) {
                    if (volumes[i] == null) {
                        Debug.LogError("[LightVolumeSetup] One of the light volumes is not setuped!");
                        yield break;
                    }
                    if (volumes[i].Texture0 == null || volumes[i].Texture1 == null || volumes[i].Texture2 == null) {
                        Debug.LogError($"[LightVolumeSetup] Light volume \"{volumes[i].gameObject.name}\" is not baked!");
                        yield break;
                    }
                    textures[i * 4] = volumes[i].Texture0;
                    textures[i * 4 + 1] = volumes[i].Texture1;
                    textures[i * 4 + 2] = volumes[i].Texture2;
                    textures[i * 4 + 3] = volumes[i].ShadowsTexture;
                }

                // Color coccecting and Deringing SH
                texs = new Texture3D[textures.Length];
                for (int i = 0; i < textures.Length / 4; ++i) {
                    Texture3D[] bundle = { textures[i * 4], textures[i * 4 + 1], textures[i * 4 + 2] };

                    float dark = -volumes[i].Shadows * 0.5f;
                    float bright = 1 - volumes[i].Highlights * 0.5f;

                    Texture3DPostprocessResult result = new Texture3DPostprocessResult();
                    yield return PostProcessSphericalHarmonics(bundle, result, dark, bright, volumes[i].Exposure);

                    texs[i * 4] = result.data[0];
                    texs[i * 4 + 1] = result.data[1];
                    texs[i * 4 + 2] = result.data[2];
                    texs[i * 4 + 3] = volumes[i].ShadowsTexture; // Occlusion texture remains unchanged
                }

                int count = texs.Length;

                // Deduplication of same textures 3D
                var keyToUnique = new Dictionary<string, int>();
                var uniqueTexs = new List<Texture3D>();
                var origToUnique = new int[count];

                for (int i = 0; i < count; ++i) {
                    Texture3D t = texs[i];
                    if (t == null) {
                        origToUnique[i] = -1; // Missing optional texture
                        continue;
                    }
                    
                    NativeArray<byte> raw = t.GetPixelData<byte>(0);
                    Hash128 hash = Hash128.Compute(raw);
                    string key = $"{hash}_{t.width}_{t.height}_{t.depth}";

                    if (!keyToUnique.TryGetValue(key, out int uIdx)) {
                        uIdx = uniqueTexs.Count;
                        uniqueTexs.Add(t);
                        keyToUnique.Add(key, uIdx);
                    }
                    origToUnique[i] = uIdx;
                }

                int uniqueCount = uniqueTexs.Count;

                yield return null;

                // Unique islands packing
                var blocks = new List<(int index, int w, int h, int d)>();
                for (int i = 0; i < uniqueCount; ++i)
                    blocks.Add((i, uniqueTexs[i].width, uniqueTexs[i].height, uniqueTexs[i].depth));

                blocks.Sort((a, b) => (b.w * b.h * b.d).CompareTo(a.w * a.h * a.d));

                var placed = new List<(int x, int y, int z, int w, int h, int d, int index)>();
                int atlasW = 0, atlasH = 0, atlasD = 0;

                yield return null;

                foreach (var block in blocks) {
                    int bw = block.w + padding * 2;
                    int bh = block.h + padding * 2;
                    int bd = block.d + padding * 2;

                    Vector3Int bestPos = Vector3Int.zero;
                    int bestVol = int.MaxValue;

                    List<int> xCand = new List<int> { 0 };
                    List<int> yCand = new List<int> { 0 };
                    List<int> zCand = new List<int> { 0 };
                    foreach (var p in placed) {
                        xCand.Add(p.x + p.w);
                        yCand.Add(p.y + p.h);
                        zCand.Add(p.z + p.d);
                    }

                    foreach (int x in xCand) {
                        foreach (int y in yCand) {
                            foreach (int z in zCand) {
                                bool collides = false;
                                foreach (var p in placed) {
                                    if (x < p.x + p.w && x + bw > p.x && y < p.y + p.h && y + bh > p.y && z < p.z + p.d && z + bd > p.z) {
                                        collides = true; break;
                                    }
                                }
                                if (collides) continue;

                                int newW = Mathf.Max(atlasW, x + bw);
                                int newH = Mathf.Max(atlasH, y + bh);
                                int newD = Mathf.Max(atlasD, z + bd);

                                if (newW > maxAtlasSize || newH > maxAtlasSize || newD > maxAtlasSize)
                                    continue;

                                int vol = newW * newH * newD;

                                if (vol < bestVol) { bestVol = vol; bestPos = new Vector3Int(x, y, z); }
                            }
                        }
                        yield return null;
                    }

                    placed.Add((bestPos.x, bestPos.y, bestPos.z, bw, bh, bd, block.index));
                    atlasW = Mathf.Max(atlasW, bestPos.x + bw);
                    atlasH = Mathf.Max(atlasH, bestPos.y + bh);
                    atlasD = Mathf.Max(atlasD, bestPos.z + bd);
                }

                ulong vCount = (ulong)atlasW * (ulong)atlasH * (ulong)atlasD;
                if (vCount > int.MaxValue) {
                    Debug.LogError($"[LightVolumeSetup] Light Volume voxel count is too large and can't be saved!");
                    yield break;
                }

                // Copying pixels
                var atlasPixels = new Color[(int)vCount];
                var uniqueBoundsMin = new Vector3[uniqueCount];
                var uniqueBoundsMax = new Vector3[uniqueCount];

                foreach (var p in placed) {
                    Texture3D tex = uniqueTexs[p.index];
                    Color[] col = tex.GetPixels();
                    int w = tex.width;
                    int h = tex.height;
                    int d = tex.depth;

                    int coroutineStep = 0; // Each step is 10000 voxels processed

                    // Main island's pixels
                    for (int z = 0; z < d; ++z) {
                        for (int y = 0; y < h; ++y) {
                            for (int x = 0; x < w; ++x) {
                                int src = x + y * w + z * w * h;

                                // Yielding if 10000 voxels calculated
                                if (src >= coroutineStep * 10000) {
                                    coroutineStep++;
                                    yield return null;
                                }

                                int dx = p.x + x + padding;
                                int dy = p.y + y + padding;
                                int dz = p.z + z + padding;
                                int dst = dx + dy * atlasW + dz * atlasW * atlasH;
                                atlasPixels[dst] = col[src];
                            }
                        }

                    }

                    // Padding X
                    for (int z = 0; z < d; ++z) {
                        for (int y = 0; y < h; ++y) {
                            atlasPixels[(p.x + 0) + (p.y + padding + y) * atlasW + (p.z + padding + z) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding) + (p.y + padding + y) * atlasW + (p.z + padding + z) * atlasW * atlasH];

                            atlasPixels[(p.x + padding + w) + (p.y + padding + y) * atlasW + (p.z + padding + z) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding + w - 1) + (p.y + padding + y) * atlasW + (p.z + padding + z) * atlasW * atlasH];
                        }
                    }

                    yield return null;

                    // Padding Y
                    for (int z = 0; z < d; ++z) {
                        for (int x = 0; x < w; ++x) {
                            atlasPixels[(p.x + padding + x) + (p.y + 0) * atlasW + (p.z + padding + z) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding + x) + (p.y + padding) * atlasW + (p.z + padding + z) * atlasW * atlasH];

                            atlasPixels[(p.x + padding + x) + (p.y + padding + h) * atlasW + (p.z + padding + z) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding + x) + (p.y + padding + h - 1) * atlasW + (p.z + padding + z) * atlasW * atlasH];
                        }
                    }

                    yield return null;

                    // Padding Z
                    for (int y = 0; y < h; ++y) {
                        for (int x = 0; x < w; ++x) {
                            atlasPixels[(p.x + padding + x) + (p.y + padding + y) * atlasW + (p.z + 0) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding + x) + (p.y + padding + y) * atlasW + (p.z + padding) * atlasW * atlasH];

                            atlasPixels[(p.x + padding + x) + (p.y + padding + y) * atlasW + (p.z + padding + d) * atlasW * atlasH] =
                            atlasPixels[(p.x + padding + x) + (p.y + padding + y) * atlasW + (p.z + padding + d - 1) * atlasW * atlasH];
                        }
                    }

                    yield return null;

                    uniqueBoundsMin[p.index] = new Vector3(
                        (float)(p.x + padding) / atlasW,
                        (float)(p.y + padding) / atlasH,
                        (float)(p.z + padding) / atlasD);

                    uniqueBoundsMax[p.index] = new Vector3(
                        (float)(p.x + padding + w) / atlasW,
                        (float)(p.y + padding + h) / atlasH,
                        (float)(p.z + padding + d) / atlasD);
                }

                yield return null;

                // Bounds for duplicated islands
                Vector3[] boundsMin = new Vector3[count];
                Vector3[] boundsMax = new Vector3[count];
                for (int i = 0; i < count; ++i) {
                    int u = origToUnique[i];
                    if (u < 0)
                        continue; // Missing optional texture
                    
                    boundsMin[i] = uniqueBoundsMin[u];
                    boundsMax[i] = uniqueBoundsMax[u];
                }

                yield return null;

                // Final Atlas 3D
                Texture3D atlasTexture = new Texture3D(atlasW, atlasH, atlasD, TextureFormat.RGBAHalf, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
                LVUtils.Apply3DTextureData(atlasTexture, atlasPixels);

                yield return null;

                onComplete?.Invoke(new Atlas3D { Texture = atlasTexture, BoundsUvwMin = boundsMin, BoundsUvwMax = boundsMax });

            } finally {
#if UNITY_EDITOR
                // Clear temporary 3D textures
                if (texs != null) {
                    for (int i = 0; i < texs.Length; i++) {
                        if (!EditorUtility.IsPersistent(texs[i])) {
                            UnityEngine.Object.DestroyImmediate(texs[i]);
                        }
                    }
                }
#endif
            }

        }

        // Simple color correction
        private static Vector3 CorrectVector(Vector3 v, float dark, float bright, float expo) {
            return v.normalized * Mathf.Max(LVUtils.RemapTo01(v.magnitude, dark, bright) * Mathf.Pow(2, expo), 0);
        }

        private static IEnumerator PostProcessSphericalHarmonics(Texture3D[] texs, Texture3DPostprocessResult result, float dark = 0, float bright = 1, float expo = 0) {

            int x = texs[0].width;
            int y = texs[0].height;
            int z = texs[0].depth;

            TextureFormat format = TextureFormat.RGBAHalf;
            Texture3D[] t = new Texture3D[3];
            t[0] = new Texture3D(x, y, z, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            t[1] = new Texture3D(x, y, z, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            t[2] = new Texture3D(x, y, z, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            result.data = t;

            yield return null;
            Color[] colors0 = texs[0].GetPixels();
            yield return null;
            Color[] colors1 = texs[1].GetPixels();
            yield return null;
            Color[] colors2 = texs[2].GetPixels();

            int coroutineStep = 0; // Each step is 10000 voxels processed

            for (int iz = 0; iz < z; iz++) {
                for (int iy = 0; iy < y; iy++) {
                    for (int ix = 0; ix < x; ix++) {

                        int index = iz * (x * y) + iy * x + ix;

                        // Yielding if 10000 voxels calculated
                        if(index >= coroutineStep * 10000) {
                            coroutineStep++;
                            yield return null;
                        }

                        Color tex0 = colors0[index];
                        Color tex1 = colors1[index];
                        Color tex2 = colors2[index];

                        Vector3 L0 = new Vector3(tex0.r, tex0.g, tex0.b);
                        Vector3 L1r = new Vector3(tex1.r, tex2.r, tex0.a);
                        Vector3 L1g = new Vector3(tex1.g, tex2.g, tex1.a);
                        Vector3 L1b = new Vector3(tex1.b, tex2.b, tex2.a);

                        L1r = LVUtils.DeringSingleSH(L0.x, L1r);
                        L1g = LVUtils.DeringSingleSH(L0.y, L1g);
                        L1b = LVUtils.DeringSingleSH(L0.z, L1b);

                        L1r = CorrectVector(L1r, dark, bright, expo);
                        L1g = CorrectVector(L1g, dark, bright, expo);
                        L1b = CorrectVector(L1b, dark, bright, expo);
                        L0 =  CorrectVector(L0, dark, bright, expo);

                        colors0[index] = new Color(L0.x, L0.y, L0.z, L1r.z);
                        colors1[index] = new Color(L1r.x, L1g.x, L1b.x, L1g.z);
                        colors2[index] = new Color(L1r.y, L1g.y, L1b.y, L1b.z);

                    }
                }
            }

            yield return null;
            LVUtils.Apply3DTextureData(t[0], colors0);
            yield return null;
            LVUtils.Apply3DTextureData(t[1], colors1);
            yield return null;
            LVUtils.Apply3DTextureData(t[2], colors2);

        }

        // Container for PostProcessSphericalHarmonics coroutine
        private class Texture3DPostprocessResult {
            public Texture3D[] data;
        }

    }
}