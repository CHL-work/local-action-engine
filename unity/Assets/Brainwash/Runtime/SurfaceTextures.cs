using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Procedural tiling surfaces for level geometry: flagstone floors, stone
    // block walls and wooden planks. The textures are light grey patterns that
    // multiply each room's own colour, and boxes get world-space UVs so the
    // pattern keeps its size on any box and continues across neighbours.
    public static class SurfaceTextures
    {
        public enum Kind { None, Floor, Wall, Wood }

        const float TileMetres = 2f;
        static readonly Dictionary<Kind, Texture2D> textures = new Dictionary<Kind, Texture2D>();
        static readonly Dictionary<(Color, Kind), Material> materials = new Dictionary<(Color, Kind), Material>();

        public static Kind Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return Kind.None;
            string n = name.ToLowerInvariant();
            if (n.Contains("floor") || n.Contains("ramp") || n.Contains("slab")) return Kind.Floor;
            if (n.Contains("wall") || n.Contains("pillar") || n.Contains("column") || n.Contains("rubble") || n.EndsWith(" body")) return Kind.Wall;
            if (n.Contains("pew") || n.Contains("table") || n.Contains("crates") || n.Contains("bridge") || n.Contains("post")) return Kind.Wood;
            if (n.EndsWith(" lip")) return Kind.Wall;
            return Kind.None;
        }

        public static Material Material(Color color, Kind kind)
        {
            if (materials.TryGetValue((color, kind), out var material) && material != null) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = kind + " surface" };
            var texture = Texture(kind);
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            // The pattern averages a little below white; lift the colour to compensate.
            Color lifted = new Color(Mathf.Min(1, color.r * 1.12f), Mathf.Min(1, color.g * 1.12f), Mathf.Min(1, color.b * 1.12f), color.a);
            material.color = lifted;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", lifted);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", kind == Kind.Floor ? .22f : .08f);
            materials[(color, kind)] = material;
            return material;
        }

        // Rewrites a box's UVs from world coordinates (planar per face).
        public static void WorldUV(GameObject go)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            var mesh = Object.Instantiate(filter.sharedMesh);
            mesh.name = go.name + " surface";
            var vertices = mesh.vertices; var normals = mesh.normals; var uv = new Vector2[vertices.Length];
            var t = go.transform;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = t.TransformPoint(vertices[i]), n = t.TransformDirection(normals[i]);
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                Vector2 q = ax >= ay && ax >= az ? new Vector2(p.z, p.y) : ay >= az ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);
                uv[i] = q / TileMetres;
            }
            mesh.uv = uv;
            filter.sharedMesh = mesh;
            go.AddComponent<OwnedMesh>().Mesh = mesh;
        }

        static Texture2D Texture(Kind kind)
        {
            if (textures.TryGetValue(kind, out var texture) && texture != null) return texture;
            texture = kind == Kind.Floor ? Flagstones() : kind == Kind.Wall ? Blocks() : Planks();
            textures[kind] = texture;
            return texture;
        }

        const int Size = 256;

        static Texture2D NewTexture(string name) => new Texture2D(Size, Size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 4 };

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 2147483647;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        // Smooth value noise that tiles every `period` pixels.
        static float Noise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int Wrap(int v) => ((v % period) + period) % period;
            float a = Hash(Wrap(x0), Wrap(y0), seed), b = Hash(Wrap(x0 + 1), Wrap(y0), seed);
            float c = Hash(Wrap(x0), Wrap(y0 + 1), seed), d = Hash(Wrap(x0 + 1), Wrap(y0 + 1), seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float Grain(int x, int y, int seed)
            => Noise(x / 32f, y / 32f, Size / 32, seed) * .5f + Noise(x / 8f, y / 8f, Size / 8, seed + 1) * .3f + Noise(x / 2f, y / 2f, Size / 2, seed + 2) * .2f;

        // Irregular flagstones: a jittered grid of cells (Worley), grout between them.
        static Texture2D Flagstones()
        {
            const int cells = 4; float cell = Size / (float)cells;
            var points = new Vector2[cells, cells];
            for (int i = 0; i < cells; i++) for (int j = 0; j < cells; j++)
                points[i, j] = new Vector2((i + .2f + .6f * Hash(i, j, 3)) * cell, (j + .2f + .6f * Hash(i, j, 4)) * cell);
            var texture = NewTexture("Flagstones");
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float d1 = float.MaxValue, d2 = float.MaxValue; int owner = 0;
                    int ci = Mathf.FloorToInt(x / cell), cj = Mathf.FloorToInt(y / cell);
                    for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                    {
                        int i = ci + di, j = cj + dj, wi = (i + cells) % cells, wj = (j + cells) % cells;
                        Vector2 p = points[wi, wj] + new Vector2((i - wi) * cell, (j - wj) * cell);
                        float d = Vector2.Distance(new Vector2(x, y), p);
                        if (d < d1) { d2 = d1; d1 = d; owner = wi * 31 + wj; } else if (d < d2) d2 = d;
                    }
                    float edge = (d2 - d1) * .5f;
                    float stone = .8f + .16f * Hash(owner, 7, 5) + (Grain(x, y, 9) - .5f) * .14f;
                    float bevel = Mathf.Clamp01(edge / 6f);
                    float v = edge < 2.2f ? .42f + Grain(x, y, 11) * .1f : stone * (.82f + .18f * bevel);
                    byte b = (byte)(Mathf.Clamp01(v) * 255);
                    pixels[y * Size + x] = new Color32(b, b, b, 255);
                }
            texture.SetPixels32(pixels); texture.Apply(true, true);
            return texture;
        }

        // Staggered stone blocks with mortar.
        static Texture2D Blocks()
        {
            const int rows = 8, perRow = 4; float h = Size / (float)rows, w = Size / (float)perRow;
            var texture = NewTexture("Stone blocks");
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int row = Mathf.FloorToInt(y / h);
                    float shift = (row % 2) * w * .5f;
                    float bx = (x + shift) % Size;
                    int col = Mathf.FloorToInt(bx / w);
                    float lx = bx - col * w, ly = y - row * h;
                    float mortar = Mathf.Min(Mathf.Min(lx, w - lx), Mathf.Min(ly, h - ly));
                    float block = .78f + .18f * Hash(col, row, 21) + (Grain(x, y, 23) - .5f) * .16f;
                    float bevel = Mathf.Clamp01(mortar / 4f);
                    float v = mortar < 1.6f ? .38f + Grain(x, y, 25) * .1f : block * (.84f + .16f * bevel) - (ly < 3 ? .05f : 0);
                    byte b = (byte)(Mathf.Clamp01(v) * 255);
                    pixels[y * Size + x] = new Color32(b, b, b, 255);
                }
            texture.SetPixels32(pixels); texture.Apply(true, true);
            return texture;
        }

        // Long wooden planks with grain lines.
        static Texture2D Planks()
        {
            const int boards = 6; float bw = Size / (float)boards;
            var texture = NewTexture("Planks");
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int board = Mathf.FloorToInt(y / bw);
                    float ly = y - board * bw;
                    float seam = Mathf.Min(ly, bw - ly);
                    float cut = (x + Hash(board, 0, 31) * Size) % Size;
                    float grain = Mathf.Sin((y + Noise(x / 24f, y / 6f, Size / 24, 33) * 12) * .9f) * .05f;
                    float v = seam < 1.5f || cut < 2 ? .4f : .82f + .12f * Hash(board, 1, 35) + grain + (Grain(x, y, 37) - .5f) * .1f;
                    byte b = (byte)(Mathf.Clamp01(v) * 255);
                    pixels[y * Size + x] = new Color32(b, b, b, 255);
                }
            texture.SetPixels32(pixels); texture.Apply(true, true);
            return texture;
        }
    }

    // Destroys a per-object mesh copy together with its object.
    public sealed class OwnedMesh : MonoBehaviour
    {
        public Mesh Mesh;
        void OnDestroy() { if (Mesh != null) Destroy(Mesh); }
    }
}
