using UnityEngine;

namespace Brainwash
{
    // Generated prop, item and decor models (art/models/recipes_props.py). Each
    // builder returns false when its model is missing so the caller can fall
    // back to the primitive version. Gameplay colliders stay on the entity
    // roots; models never carry colliders.
    public static class PropModels
    {
        // A model centred on its parent and scaled to fill size (or to fit
        // inside it, keeping proportions, when uniform).
        public static GameObject Fit(string id, Transform parent, Vector3 size, Color tint, bool uniform = false)
        {
            var go = ModelLibrary.Spawn(id, parent, tint);
            if (go == null) return null;
            Vector3 model = ModelLibrary.Size(id, Vector3.one);
            Vector3 scale = new Vector3(Ratio(size.x, model.x), Ratio(size.y, model.y), Ratio(size.z, model.z));
            if (uniform) scale = Vector3.one * Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z));
            go.transform.localScale = scale;
            go.transform.localPosition = -Vector3.Scale(ModelLibrary.Center(id), scale);
            return go;
        }

        static float Ratio(float want, float have) => have > .0001f ? want / have : 1;

        static Renderer MainRenderer(GameObject go)
        {
            Renderer best = null; float volume = -1;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var e = r.bounds.size; float v = e.x * e.y * e.z;
                if (v > volume) { volume = v; best = r; }
            }
            return best;
        }

        public static bool Item(SandboxEntity entity, string kind, Vector3 box, Color color)
        {
            string id = kind == "key" || kind == "cheese" || kind == "relic" || kind == "scripture" || kind == "pebble" || kind == "rock" || kind == "fire_pot" ? kind : null;
            if (id == null || !ModelLibrary.Has(id)) return false;
            var go = Fit(id, entity.transform, box, color, true);
            entity.VisualRoot = go.transform; entity.Body = MainRenderer(go); entity.ModelTint = color;
            return true;
        }

        public static bool Object(SandboxEntity entity, string kind, Vector3 size, Color color)
        {
            switch (kind)
            {
                case "barrel": case "crate": case "brazier": case "pedestal":
                {
                    if (!ModelLibrary.Has(kind)) return false;
                    var go = Fit(kind, entity.transform, size, color);
                    entity.VisualRoot = go.transform; entity.Body = MainRenderer(go); entity.ModelTint = color;
                    return true;
                }
                case "door": return Door(entity, size, color);
                case "lever": return Lever(entity, size, color);
                case "circle": return Circle(entity, size, color);
            }
            return false;
        }

        // Frame plus a leaf on a hinge at the left edge; SetOpen swings the hinge.
        static bool Door(SandboxEntity entity, Vector3 size, Color color)
        {
            bool gate = entity.DisplayName != null && entity.DisplayName.Contains("cage");
            string leaf = gate ? "gate_panel" : "door_panel";
            if (!ModelLibrary.Has(leaf) || (!gate && !ModelLibrary.Has("door_frame"))) return false;
            var root = new GameObject("Door model").transform; root.SetParent(entity.transform, false);
            Vector3 design = gate ? new Vector3(4.2f, 2.8f, .25f) : new Vector3(2.6f, 2.6f, .25f);
            Vector3 scale = new Vector3(size.x / design.x, size.y / design.y, Mathf.Max(.6f, size.z / design.z));
            if (!gate)
            {
                var frame = ModelLibrary.Spawn("door_frame", root, new Color(.36f, .37f, .4f));
                frame.transform.localScale = scale;
            }
            var hinge = new GameObject("Door hinge").transform; hinge.SetParent(root, false);
            hinge.localPosition = Vector3.left * size.x * .5f;
            var panel = ModelLibrary.Spawn(leaf, hinge, color);
            panel.transform.localScale = scale;
            entity.VisualRoot = root; entity.OpenVisual = hinge; entity.OpenVisualRotation = Quaternion.Euler(0, -95, 0);
            entity.Body = MainRenderer(panel); entity.ModelTint = color;
            return true;
        }

        static bool Lever(SandboxEntity entity, Vector3 size, Color color)
        {
            if (!ModelLibrary.Has("lever_base") || !ModelLibrary.Has("lever_handle")) return false;
            var root = new GameObject("Lever model").transform; root.SetParent(entity.transform, false);
            float bottom = -size.y * .5f;
            var stand = ModelLibrary.Spawn("lever_base", root, new Color(.3f, .32f, .35f));
            stand.transform.localPosition = new Vector3(0, bottom, 0);
            var pivot = new GameObject("Lever pivot").transform; pivot.SetParent(root, false);
            pivot.localPosition = new Vector3(0, bottom + .26f, 0); pivot.localRotation = Quaternion.Euler(-20, 0, 0);
            var handle = ModelLibrary.Spawn("lever_handle", pivot, color);
            float length = Mathf.Max(.4f, size.y - .35f);
            handle.transform.localScale = new Vector3(1, length / .95f, 1);
            entity.VisualRoot = root; entity.OpenVisual = pivot; entity.OpenVisualRotation = Quaternion.Euler(45, 0, 0);
            entity.Body = MainRenderer(handle); entity.ModelTint = color;
            return true;
        }

        static bool Circle(SandboxEntity entity, Vector3 size, Color color)
        {
            if (!ModelLibrary.Has("circle")) return false;
            var root = new GameObject("Circle model").transform; root.SetParent(entity.transform, false);
            var disc = ModelLibrary.Spawn("circle", root, color);
            Vector3 model = ModelLibrary.Size("circle", Vector3.one);
            disc.transform.localScale = new Vector3(size.x / model.x, 1, size.z / model.z);
            if (ModelLibrary.Has("rune_stone"))
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.PI * 2 / 12;
                    var stone = ModelLibrary.Spawn("rune_stone", root, color);
                    stone.transform.localPosition = new Vector3(Mathf.Cos(a) * size.x * .52f, -size.y * .5f, Mathf.Sin(a) * size.z * .52f);
                    stone.transform.localRotation = Quaternion.LookRotation(-new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)));
                }
            entity.VisualRoot = root; entity.Body = MainRenderer(disc); entity.ModelTint = color;
            return true;
        }

        // A fallen cultist lies on its back; SetRemainsStanding stands it up.
        public static bool Remains(SandboxEntity entity, Color robe)
        {
            if (!ModelLibrary.Has("cultist")) return false;
            var root = new GameObject("Remains model").transform; root.SetParent(entity.transform, false);
            var body = ModelLibrary.Spawn("cultist", root, robe);
            entity.VisualRoot = root; entity.Body = MainRenderer(body); entity.ModelTint = robe;
            SetStanding(entity, false);
            return true;
        }

        public static bool SetStanding(SandboxEntity entity, bool standing)
        {
            if (entity.VisualRoot == null || entity.VisualRoot.name != "Remains model") return false;
            // Lying on its back, head toward +Z where the old capsule's head was.
            entity.VisualRoot.localRotation = standing ? Quaternion.identity : Quaternion.Euler(90, 0, 0) * Quaternion.Euler(0, 180, 0);
            entity.VisualRoot.localPosition = standing ? Vector3.zero : new Vector3(0, .25f, -.9f);
            return true;
        }

        // Decorations that have a model: the primitive keeps only its collider
        // (or goes away), and the model takes its place.
        public static GameObject Decor(GameObject primitive, string name, Transform room, Vector3 position, Vector3 scale, Quaternion rotation, bool solid)
        {
            string id; Vector3 size; Color tint = Color.gray; Vector3 offset = Vector3.zero; bool uniform = true;
            switch (name)
            {
                case "Torch": id = "torch"; size = new Vector3(.3f, .6f, .3f); tint = new Color(.4f, .28f, .16f); break;
                case "Spike": id = "spike"; size = new Vector3(.12f, scale.y * 2, .12f); tint = new Color(.4f, .3f, .2f); break;
                case "Tent": id = "tent"; size = scale; uniform = false; tint = primitive.GetComponent<Renderer>().sharedMaterial.color; break;
                case "Harmless statue": id = "gargoyle"; size = new Vector3(1.2f, 1.9f, 1.2f); offset = Vector3.down * .9f; tint = new Color(.46f, .47f, .46f); break;
                default: return primitive;
            }
            if (!ModelLibrary.Has(id)) return primitive;
            var holder = new GameObject(name + " model").transform; holder.SetParent(room, false);
            holder.SetPositionAndRotation(position + rotation * offset, rotation);
            GameObject model;
            if (id == "gargoyle")
            {
                model = ModelLibrary.Spawn(id, holder, tint);
                float s = 1.9f / ModelLibrary.Height(id, 1.9f);
                model.transform.localScale = Vector3.one * s;
            }
            else model = Fit(id, holder, size, tint, uniform);
            if (solid) primitive.GetComponent<Renderer>().enabled = false;
            else UnityEngine.Object.Destroy(primitive);
            return model;
        }
    }
}
