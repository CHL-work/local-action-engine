using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Primitive-and-particle effects for the demos: flames, blasts, pools,
    // floating damage numbers and the brainwash halo. Everything is unlit and
    // owned by the world's room root, so a reset clears it.
    public sealed class DemoEffects
    {
        readonly SandboxWorld world;
        readonly Dictionary<Color, Material> unlit = new Dictionary<Color, Material>();
        static Texture2D softDot;
        Material particleMaterial;

        public DemoEffects(SandboxWorld world) { this.world = world; }

        public void Clear() { }
        public void Tick() { }

        public Material Unlit(Color color)
        {
            if (!unlit.TryGetValue(color, out var material))
            {
                material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit")) { color = color };
                unlit[color] = material;
            }
            return material;
        }

        Material ParticleMaterial()
        {
            if (particleMaterial != null) return particleMaterial;
            if (softDot == null)
            {
                softDot = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15.5f;
                        float a = Mathf.Clamp01(1 - d); a *= a;
                        softDot.SetPixel(x, y, new Color(1, 1, 1, a));
                    }
                softDot.Apply();
            }
            particleMaterial = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit")) { mainTexture = softDot };
            return particleMaterial;
        }

        GameObject Primitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Color color, Transform parent = null, bool ownMaterial = false)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name;
            var collider = go.Comp<Collider>(); if (collider != null) { collider.enabled = false; Object.Destroy(collider); }
            go.transform.SetParent(parent != null ? parent : world.RoomRoot, true);
            go.transform.position = position; go.transform.localScale = scale;
            var renderer = go.Comp<Renderer>();
            renderer.sharedMaterial = ownMaterial ? new Material(Unlit(color)) : Unlit(color);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        public ParticleSystem Particles(string name, Transform parent, Vector3 position, Vector3 boxSize, Color a, Color b, float rate, float speed, float size, float lifetime, bool world = true)
        {
            var go = new GameObject(name); go.SetActive(false);
            go.transform.SetParent(parent != null ? parent : this.world.RoomRoot, false);
            go.transform.position = position;
            var system = go.AddComponent<ParticleSystem>();
            var main = system.main;
            main.loop = true; main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * .6f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * .5f, speed); main.startSize = new ParticleSystem.MinMaxCurve(size * .6f, size);
            main.startColor = new ParticleSystem.MinMaxGradient(a, b); main.maxParticles = 400;
            main.simulationSpace = world ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            main.gravityModifier = -.05f;
            var emission = system.emission; emission.rateOverTime = rate;
            var shape = system.shape; shape.shapeType = ParticleSystemShapeType.Box; shape.scale = boxSize;
            shape.rotation = new Vector3(-90, 0, 0);
            var colors = system.colorOverLifetime; colors.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(.9f, 0), new GradientAlphaKey(.6f, .5f), new GradientAlphaKey(0, 1) });
            colors.color = gradient;
            var sizes = system.sizeOverLifetime; sizes.enabled = true; sizes.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 1, 1, .2f));
            var renderer = go.Comp<ParticleSystemRenderer>();
            renderer.sharedMaterial = ParticleMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(true);
            return system;
        }

        // Persistent fire field (fire pits, burning bridge ropes, braziers).
        public GameObject Flames(Vector3 center, Vector3 size, Transform parent)
        {
            var system = Particles("Flames", parent, center + Vector3.up * .1f, new Vector3(Mathf.Max(.2f, size.x), Mathf.Max(.2f, size.z), .1f),
                new Color(1, .82f, .25f), new Color(1, .3f, .04f), 25 + size.x * size.z * 12, 1.6f, .55f, .9f);
            GameAudio.Attach("fire_loop", system.transform, Mathf.Clamp(.4f + size.x * size.z * .05f, .4f, 1));
            return system.gameObject;
        }

        public GameObject AttachFlame(SandboxEntity entity)
        {
            float height = entity.Body != null ? entity.Body.bounds.size.y : 1;
            var system = Particles("Burning", entity.transform, entity.Center, new Vector3(.4f, .4f, height * .5f),
                new Color(1, .85f, .3f), new Color(1, .35f, .05f), 30, 1.4f, .45f, .7f);
            GameAudio.Play("fire_ignite", entity.Center, .8f);
            GameAudio.Attach("fire_loop", system.transform, .6f);
            return system.gameObject;
        }

        public void Pulse(Vector3 position, Color color, float radius)
        {
            var go = Primitive("Pulse", PrimitiveType.Sphere, position, Vector3.one * .3f, new Color(color.r, color.g, color.b, .55f), null, true);
            var effect = go.AddComponent<TimedEffect>(); effect.Lifetime = .55f; effect.Grow = true; effect.GrowTo = radius * 2 / .3f;
        }

        public void Flash(Vector3 position, Color color, float radius)
        {
            var go = Primitive("Blast", PrimitiveType.Sphere, position, Vector3.one * .5f, new Color(color.r, color.g, color.b, .8f), null, true);
            var effect = go.AddComponent<TimedEffect>(); effect.Lifetime = .45f; effect.Grow = true; effect.GrowTo = radius * 2 / .5f;
            var lightObject = new GameObject("Blast light"); lightObject.transform.SetParent(world.RoomRoot, false); lightObject.transform.position = position + Vector3.up * .5f;
            var light = lightObject.AddComponent<Light>(); light.type = LightType.Point; light.color = color; light.range = radius * 3; light.intensity = 6;
            lightObject.AddComponent<TimedEffect>().Lifetime = .35f;
            Burst(position, new Color(.35f, .3f, .28f), 10, 7);
        }

        public void Burst(Vector3 position, Color color, int pieces = 8, float speed = 4.5f)
        {
            for (int i = 0; i < pieces; i++)
            {
                var go = Primitive("Debris", PrimitiveType.Cube, position + Random.insideUnitSphere * .2f, Vector3.one * Random.Range(.08f, .2f), color, null, true);
                var debris = go.AddComponent<Debris>();
                debris.Velocity = (Random.insideUnitSphere + Vector3.up * .9f).normalized * Random.Range(speed * .4f, speed);
                debris.Spin = Random.insideUnitSphere * 540;
            }
        }

        public void Pool(Vector3 point, float radius, Color color, float seconds)
        {
            var go = Primitive("Pool", PrimitiveType.Cylinder, point + Vector3.up * .03f, new Vector3(radius * 2, .015f, radius * 2), color, null, true);
            GameAudio.Play("acid_sizzle", point);
            var effect = go.AddComponent<TimedEffect>(); effect.Lifetime = seconds; effect.FadeFrom = .75f;
            var bubbles = Particles("Pool bubbles", go.transform, point + Vector3.up * .05f, new Vector3(radius * 1.6f, radius * 1.6f, .05f),
                new Color(color.r, color.g, color.b, .8f), new Color(color.r * .6f, color.g, color.b * .5f, .6f), 10 * radius, .5f, .22f, .8f);
            bubbles.transform.SetParent(go.transform, true);
        }

        public void Popup(Vector3 position, string text, Color color)
        {
            var go = new GameObject("Damage popup"); go.transform.SetParent(world.RoomRoot, false);
            go.transform.position = position + Random.insideUnitSphere * .2f;
            go.transform.localScale = Vector3.one * .03f;
            var mesh = go.AddComponent<TextMesh>(); mesh.text = text; mesh.fontSize = 56; mesh.anchor = TextAnchor.MiddleCenter;
            mesh.color = color; mesh.fontStyle = FontStyle.Bold;
            var popup = go.AddComponent<FloatingText>(); popup.Camera = world.View;
        }

        public GameObject Halo()
        {
            var root = new GameObject("Brainwash halo"); root.transform.SetParent(world.RoomRoot, false);
            for (int i = 0; i < 14; i++)
            {
                float angle = i * Mathf.PI * 2 / 14;
                var bead = Primitive("Halo bead", PrimitiveType.Sphere, Vector3.zero, Vector3.one * .1f, Color.white, root.transform, true);
                bead.transform.localPosition = new Vector3(Mathf.Cos(angle) * .42f, 0, Mathf.Sin(angle) * .42f);
            }
            return root;
        }

        public void Tint(GameObject go, Color color)
        {
            foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                if (renderer.sharedMaterial != null) renderer.sharedMaterial.color = color;
        }

        // A puff of floor dust where something heavy steps, lands or falls.
        // One shared emitter per room; amount 0-1.
        ParticleSystem dust;
        public void Dust(Vector3 point, float amount)
        {
            if (dust == null)
            {
                dust = Particles("Dust", null, point, Vector3.one * .1f, new Color(.66f, .6f, .52f, .5f), new Color(.5f, .46f, .41f, .38f), 0, 1, .4f, .8f);
                var main = dust.main; main.gravityModifier = -.015f; main.maxParticles = 300;
                var sizes = dust.sizeOverLifetime; sizes.size = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, .55f, 1, 1.6f));
                var drag = dust.limitVelocityOverLifetime; drag.enabled = true; drag.limit = 100; drag.drag = 3.5f;
            }
            amount = Mathf.Clamp01(amount);
            int count = 3 + Mathf.RoundToInt(amount * 11);
            var emit = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 d = Random.insideUnitCircle.normalized;
                emit.position = point + new Vector3(d.x * .18f, .06f, d.y * .18f);
                emit.velocity = new Vector3(d.x, Random.Range(.12f, .45f), d.y) * Random.Range(.9f, 2.1f) * (.5f + amount);
                emit.startSize = Random.Range(.22f, .42f) * (.6f + amount * .8f);
                emit.startLifetime = Random.Range(.55f, .95f);
                emit.startColor = Color.Lerp(new Color(.66f, .6f, .52f, .5f), new Color(.5f, .46f, .41f, .38f), Random.value);
                dust.Emit(emit, 1);
            }
        }

        // A loud thin ring on the floor that shows how far a noise carried.
        public void NoiseRing(Vector3 point, float radius, Color color)
        {
            var go = Primitive("Noise ring", PrimitiveType.Cylinder, point + Vector3.up * .05f, new Vector3(.4f, .01f, .4f), new Color(color.r, color.g, color.b, .35f), null, true);
            var effect = go.AddComponent<TimedEffect>(); effect.Lifetime = .6f; effect.Grow = true; effect.GrowTo = radius * 2 / .4f; effect.FlatGrow = true;
        }
    }

    public sealed class Debris : MonoBehaviour
    {
        public Vector3 Velocity, Spin;
        float born;
        Renderer cached;
        void Start() { born = Time.time; cached = GetComponent<Renderer>(); }
        void Update()
        {
            Velocity += Physics.gravity * Time.deltaTime;
            transform.position += Velocity * Time.deltaTime;
            transform.Rotate(Spin * Time.deltaTime);
            float t = (Time.time - born) / 1.4f;
            if (cached != null) { var c = cached.sharedMaterial.color; c.a = Mathf.Clamp01(1 - t); cached.sharedMaterial.color = c; }
            if (t >= 1) Destroy(gameObject);
        }
        void OnDestroy() { if (cached != null && cached.sharedMaterial != null) Destroy(cached.sharedMaterial); }
    }

    public sealed class FloatingText : MonoBehaviour
    {
        public Camera Camera;
        float born;
        TextMesh mesh;
        void Start() { born = Time.time; mesh = GetComponent<TextMesh>(); }
        void Update()
        {
            float t = (Time.time - born) / .9f;
            transform.position += Vector3.up * Time.deltaTime * 1.3f;
            if (Camera != null) transform.rotation = Quaternion.LookRotation(transform.position - Camera.transform.position);
            if (mesh != null) { var c = mesh.color; c.a = Mathf.Clamp01(1.4f - t * 1.4f); mesh.color = c; }
            if (t >= 1) Destroy(gameObject);
        }
    }
}
