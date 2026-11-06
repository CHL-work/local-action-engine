using System;
using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Runtime-only low-poly models. Gameplay collisions belong to entity roots;
    // no decorative primitive may participate in movement, sight or item drops.
    public sealed class SandboxVisuals : MonoBehaviour
    {
        private SandboxEntity entity;
        private SandboxMonster monster;
        private Transform model, leftArm, rightArm, leftLeg, rightLeg, rightHand, leftWing, rightWing, naturalWeapon;
        private Vector3 lastPosition;
        private float height, stride;
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();

        public static void BuildCreature(SandboxEntity entity, float width, float height, Color color, Func<Color, Material> material)
        {
            // Generated models (art/models) replace the primitive figures when present.
            if (CreatureAnimator.Build(entity, entity.Species, height, color) != null) return;
            var visuals = entity.gameObject.AddComponent<SandboxVisuals>();
            visuals.entity = entity; visuals.height = height; visuals.lastPosition = entity.transform.position;
            visuals.model = Node("Creature model", entity.transform, Vector3.zero);
            entity.VisualRoot = visuals.model;
            if (entity.Species == "ice_golem") visuals.Golem(width, height, color, material);
            else visuals.SmallCreature(width, height, color, material, entity.Species == "goblin");
        }

        public static void BuildItem(SandboxEntity entity, Color color, Func<Color, Material> material)
        {
            if (PropModels.Item(entity, entity.ItemKind, entity.ItemKind == "key" ? new Vector3(.66f, .1f, .32f) : new Vector3(.38f, .2f, .2f), color)) return;
            var visuals = entity.gameObject.AddComponent<SandboxVisuals>();
            visuals.entity = entity;
            visuals.model = Node("Item model", entity.transform, Vector3.zero); entity.VisualRoot = visuals.model;
            if (entity.ItemKind == "key")
            {
                var shape = new Shape();
                const int segments = 12;
                Vector3 center = new Vector3(-.17f, 0, 0);
                for (int i = 0; i < segments; i++)
                {
                    float a = i * Mathf.PI * 2 / segments, b = (i + 1) * Mathf.PI * 2 / segments;
                    Vector3 outerA = center + new Vector3(Mathf.Cos(a) * .16f, 0, Mathf.Sin(a) * .16f);
                    Vector3 outerB = center + new Vector3(Mathf.Cos(b) * .16f, 0, Mathf.Sin(b) * .16f);
                    Vector3 innerA = center + new Vector3(Mathf.Cos(a) * .10f, 0, Mathf.Sin(a) * .10f);
                    Vector3 innerB = center + new Vector3(Mathf.Cos(b) * .10f, 0, Mathf.Sin(b) * .10f);
                    Vector3 up = Vector3.up * .04f;
                    shape.Quad(outerA + up, innerA + up, innerB + up, outerB + up);
                    shape.Quad(outerB - up, innerB - up, innerA - up, outerA - up);
                    shape.Quad(outerA - up, outerA + up, outerB + up, outerB - up);
                    shape.Quad(innerB - up, innerB + up, innerA + up, innerA - up);
                }
                shape.Box(new Vector3(.12f, 0, 0), new Vector3(.42f, .08f, .065f));
                shape.Box(new Vector3(.19f, 0, .065f), new Vector3(.055f, .08f, .14f));
                shape.Box(new Vector3(.295f, 0, .065f), new Vector3(.055f, .08f, .14f));
                // One renderer contains the whole key, so existing drop sweeps
                // conservatively account for the ring and teeth as well as shaft.
                entity.Body = visuals.MeshPart("Brass key: ring, shaft and teeth", visuals.model, shape.Mesh("Key"), color, material);
            }
            else entity.Body = Part("Item", PrimitiveType.Cube, visuals.model, Vector3.zero, new Vector3(.38f, .2f, .2f), color, material);
        }

        public static void BuildObject(SandboxEntity entity, Vector3 size, Color color, Func<Color, Material> material)
        {
            if (PropModels.Object(entity, entity.ObjectKind, size, color)) return;
            var visuals = entity.gameObject.AddComponent<SandboxVisuals>(); visuals.entity = entity;
            visuals.model = Node("Prop model", entity.transform, Vector3.zero); entity.VisualRoot = visuals.model;
            Color dark = Color.Lerp(color, Color.black, .45f), light = Color.Lerp(color, Color.white, .23f);
            if (entity.ObjectKind == "door")
            {
                for (int side = -1; side <= 1; side += 2)
                    Part("Stone door jamb", PrimitiveType.Cube, visuals.model, new Vector3(side * (size.x * .5f + .07f), 0, 0), new Vector3(.14f, size.y + .16f, size.z * 1.5f), new Color(.33f, .35f, .38f), material);
                Part("Door lintel", PrimitiveType.Cube, visuals.model, Vector3.up * (size.y * .5f + .08f), new Vector3(size.x + .28f, .16f, size.z * 1.5f), new Color(.38f, .4f, .43f), material);
                var hinge = Node("Door hinge", visuals.model, Vector3.left * size.x * .5f);
                entity.OpenVisual = hinge; entity.OpenVisualRotation = Quaternion.Euler(0, -95, 0);
                entity.Body = Part("Wooden door panel", PrimitiveType.Cube, hinge, Vector3.right * size.x * .5f, new Vector3(size.x * .97f, size.y * .97f, size.z), color, material);
                for (int plank = 1; plank <= 4; plank++)
                    Part("Door plank seam", PrimitiveType.Cube, hinge, new Vector3(size.x * plank / 5f, 0, size.z * .52f), new Vector3(.018f, size.y * .95f, .016f), dark, material);
                for (int side = -1; side <= 1; side += 2)
                    Part("Iron door strap", PrimitiveType.Cube, hinge, new Vector3(size.x * .5f, side * size.y * .29f, size.z * .56f), new Vector3(size.x * .9f, .09f, .04f), new Color(.18f, .2f, .23f), material);
                Part("Brass door handle", PrimitiveType.Sphere, hinge, new Vector3(size.x * .84f, -.05f, size.z * .5f + .07f), Vector3.one * .14f, new Color(.95f, .68f, .17f), material);
            }
            else if (entity.ObjectKind == "lever")
            {
                float bottom = -size.y * .5f;
                Part("Lever stone base", PrimitiveType.Cube, visuals.model, new Vector3(0, bottom + .13f, 0), new Vector3(.48f, .26f, .45f), new Color(.28f, .31f, .34f), material);
                var pivot = Node("Lever pivot", visuals.model, new Vector3(0, bottom + .26f, 0));
                pivot.localRotation = Quaternion.Euler(-20, 0, 0);
                entity.OpenVisual = pivot; entity.OpenVisualRotation = Quaternion.Euler(45, 0, 0);
                float length = Mathf.Max(.4f, size.y - .35f);
                entity.Body = Part("Lever handle", PrimitiveType.Cylinder, pivot, Vector3.up * length * .5f, new Vector3(.08f, length * .5f, .08f), new Color(.55f, .58f, .62f), material);
                Part("Red lever grip", PrimitiveType.Sphere, pivot, Vector3.up * length, Vector3.one * .24f, color, material);
                Part("Lever axle", PrimitiveType.Cylinder, pivot, Vector3.zero, new Vector3(.18f, .16f, .18f), dark, material).transform.localRotation = Quaternion.Euler(0, 0, 90);
            }
            else if (entity.ObjectKind == "crate")
            {
                entity.Body = Part("Crate core", PrimitiveType.Cube, visuals.model, Vector3.zero, size, dark, material);
                for (int side = -1; side <= 1; side += 2)
                {
                    for (int plank = 0; plank < 4; plank++)
                    {
                        float coordinate = (plank - 1.5f) / 4f;
                        Part("Front back wooden plank", PrimitiveType.Cube, visuals.model, new Vector3(coordinate * size.x, 0, side * size.z * .505f), new Vector3(size.x * .235f, size.y * .96f, .025f), (plank % 2 == 0) ? color : light, material);
                        Part("Side wooden plank", PrimitiveType.Cube, visuals.model, new Vector3(side * size.x * .505f, 0, coordinate * size.z), new Vector3(.025f, size.y * .96f, size.z * .235f), (plank % 2 == 0) ? light : color, material);
                    }
                    Part("Crate cross brace", PrimitiveType.Cube, visuals.model, new Vector3(0, 0, side * (size.z * .5f + .03f)), new Vector3(size.x * .12f, size.y * 1.18f, .05f), light, material).transform.localRotation = Quaternion.Euler(0, 0, -38);
                    Part("Crate top plank", PrimitiveType.Cube, visuals.model, new Vector3(side * size.x * .24f, size.y * .51f, 0), new Vector3(size.x * .46f, .035f, size.z), color, material);
                }
            }
            else if (entity.ObjectKind == "rope")
            {
                entity.Body = Part("Rope", PrimitiveType.Cylinder, visuals.model, Vector3.zero, new Vector3(size.x, size.y * .5f, size.z), color, material);
                for (int knot = -1; knot <= 1; knot++) Part("Rope knot", PrimitiveType.Sphere, visuals.model, Vector3.up * knot * size.y * .3f, new Vector3(size.x * 1.6f, size.x, size.z * 1.6f), dark, material);
            }
            else entity.Body = Part("Object", PrimitiveType.Cube, visuals.model, Vector3.zero, size, color, material);
        }

        private void SmallCreature(float w, float h, Color color, Func<Color, Material> material, bool goblin)
        {
            Color shade = Color.Lerp(color, Color.black, .28f), leather = new Color(.26f, .16f, .10f), horn = new Color(.92f, .82f, .57f);
            entity.Body = Part("Torso", PrimitiveType.Capsule, model, new Vector3(0, h * .50f, 0), new Vector3(w * .88f, h * .25f, w * .62f), color, material);
            Part("Belt", PrimitiveType.Cube, model, new Vector3(0, h * .36f, 0), new Vector3(w * .88f, h * .07f, w * .66f), leather, material);
            Part("Belt buckle", PrimitiveType.Cube, model, new Vector3(0, h * .36f, w * .35f), new Vector3(w * .19f, h * .065f, .045f), horn, material);
            var head = Node("Head", model, Vector3.up * h * .79f);
            Part("Head", PrimitiveType.Sphere, head, Vector3.zero, new Vector3(w * .93f, h * .28f, w * .83f), color, material);
            Part("Jaw", PrimitiveType.Cube, head, new Vector3(0, -h * .07f, w * .18f), new Vector3(w * .6f, h * .1f, w * .6f), shade, material);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Eye white", PrimitiveType.Sphere, head, new Vector3(side * w * .22f, h * .015f, w * .36f), new Vector3(w * .26f, h * .078f, .09f), new Color(1, .94f, .66f), material);
                Part("Eye pupil", PrimitiveType.Sphere, head, new Vector3(side * w * .22f, h * .015f, w * .414f), new Vector3(w * .10f, h * .057f, .04f), new Color(.06f, .045f, .05f), material);
                var brow = Part("Angled eyebrow", PrimitiveType.Cube, head, new Vector3(side * w * .22f, h * .06f, w * .39f), new Vector3(w * .29f, h * .025f, .06f), shade, material);
                brow.transform.localRotation = Quaternion.Euler(0, 0, side * -14);
                Part("Tooth", PrimitiveType.Cube, head, new Vector3(side * w * .17f, -h * .085f, w * .49f), new Vector3(w * .065f, h * .045f, .045f), horn, material);
                if (goblin)
                {
                    var ear = Cone("Long pointed ear", head, new Vector3(side * w * .37f, .02f, 0), new Vector3(w * .22f, w * .57f, w * .18f), color, material);
                    ear.localRotation = Quaternion.Euler(0, 0, -side * 67);
                }
                else
                {
                    var hornPart = Cone("Imp horn", head, new Vector3(side * w * .30f, h * .09f, -.015f), new Vector3(w * .17f, h * .22f, w * .17f), horn, material);
                    hornPart.localRotation = Quaternion.Euler(-13, 0, -side * 21);
                }
                var arm = Node(side < 0 ? "Left shoulder" : "Right shoulder", model, new Vector3(side * w * .54f, h * .64f, 0));
                Part("Arm", PrimitiveType.Capsule, arm, Vector3.down * h * .105f, new Vector3(w * .26f, h * .13f, w * .27f), color, material);
                var wrist = Node("Hand socket guide", arm, new Vector3(0, -h * .235f, .01f));
                Part("Hand", PrimitiveType.Sphere, wrist, Vector3.zero, Vector3.one * w * .34f, shade, material);
                var leg = Node(side < 0 ? "Left hip" : "Right hip", model, new Vector3(side * w * .24f, h * .33f, 0));
                Part("Leg", PrimitiveType.Capsule, leg, Vector3.down * h * .12f, new Vector3(w * .31f, h * .14f, w * .32f), shade, material);
                Part("Foot", PrimitiveType.Cube, leg, new Vector3(0, -h * .285f, w * .09f), new Vector3(w * .37f, h * .08f, w * .55f), leather, material);
                if (side < 0) { leftArm = arm; leftLeg = leg; }
                else { rightArm = arm; rightLeg = leg; rightHand = wrist; }
            }
            if (goblin)
            {
                var nose = Cone("Goblin long nose", head, new Vector3(0, -.015f, w * .32f), new Vector3(w * .23f, w * .42f, w * .23f), shade, material);
                nose.localRotation = Quaternion.Euler(90, 0, 0);
                naturalWeapon = Node("Goblin club", rightHand, Vector3.zero);
                Part("Club handle", PrimitiveType.Cylinder, naturalWeapon, new Vector3(0, .16f, .04f), new Vector3(.07f, .27f, .07f), leather, material);
                Part("Heavy club head", PrimitiveType.Cube, naturalWeapon, new Vector3(0, .43f, .04f), new Vector3(.22f, .28f, .21f), new Color(.36f, .25f, .16f), material).transform.localRotation = Quaternion.Euler(0, 18, 7);
                Part("Club iron band", PrimitiveType.Cube, naturalWeapon, new Vector3(0, .46f, .04f), new Vector3(.23f, .055f, .22f), new Color(.32f, .35f, .37f), material);
            }
            else
            {
                Part("Imp nose", PrimitiveType.Sphere, head, new Vector3(0, -.03f, w * .45f), new Vector3(.12f, .10f, .15f), shade, material);
                for (int side = -1; side <= 1; side += 2)
                {
                    var wing = Node(side < 0 ? "Left bat wing" : "Right bat wing", model, new Vector3(side * w * .29f, h * .64f, -w * .25f));
                    var shape = new Shape();
                    Vector3 a = Vector3.zero, b = new Vector3(side * w * 1.02f, h * .15f, -.13f), c = new Vector3(side * w * .83f, -h * .15f, -.08f), d = new Vector3(side * w * .47f, -h * .08f, 0), e = new Vector3(side * w * .18f, -h * .21f, .03f);
                    shape.Triangle(a, b, c, true); shape.Triangle(a, c, d, true); shape.Triangle(a, d, e, true);
                    MeshPart("Bat wing membrane", wing, shape.Mesh("Bat wing"), new Color(.22f, .16f, .24f), material);
                    Segment("Wing leading bone", wing, a, b, .045f, shade, material);
                    Segment("Wing finger", wing, a, c, .035f, shade, material);
                    if (side < 0) leftWing = wing; else rightWing = wing;
                }
                Vector3 tailA = new Vector3(0, h * .36f, -w * .25f), tailB = new Vector3(w * .30f, h * .25f, -w * .85f), tailC = new Vector3(w * .48f, h * .40f, -w * 1.12f);
                Segment("Imp tail base", model, tailA, tailB, .065f, shade, material); Segment("Imp tail curl", model, tailB, tailC, .05f, shade, material);
                var tip = Cone("Tail arrow tip", model, tailC, new Vector3(.17f, .24f, .09f), shade, material); tip.localRotation = Quaternion.FromToRotation(Vector3.up, (tailC - tailB).normalized);
            }
        }

        private void Golem(float w, float h, Color color, Func<Color, Material> material)
        {
            Color pale = new Color(.66f, .92f, 1), dark = new Color(.13f, .34f, .51f);
            entity.Body = Part("Heavy ice torso", PrimitiveType.Cube, model, Vector3.up * h * .52f, new Vector3(w * 1.13f, h * .43f, w * .77f), color, material);
            Part("Ice chest plate", PrimitiveType.Cube, model, new Vector3(0, h * .57f, w * .42f), new Vector3(w * .72f, h * .22f, .10f), pale, material).transform.localRotation = Quaternion.Euler(0, 0, 14);
            Part("Ice head", PrimitiveType.Cube, model, new Vector3(0, h * .84f, .02f), new Vector3(w * .65f, h * .22f, w * .66f), pale, material).transform.localRotation = Quaternion.Euler(0, 0, -5);
            Part("Dark eye slit", PrimitiveType.Cube, model, new Vector3(0, h * .855f, w * .36f), new Vector3(w * .54f, h * .035f, .035f), dark, material);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Bright ice eye", PrimitiveType.Cube, model, new Vector3(side * w * .16f, h * .855f, w * .385f), new Vector3(w * .11f, h * .034f, .025f), Color.white, material);
                var arm = Node(side < 0 ? "Left ice shoulder" : "Right ice shoulder", model, new Vector3(side * w * .76f, h * .69f, 0));
                Part("Shoulder ice block", PrimitiveType.Cube, arm, Vector3.zero, new Vector3(w * .53f, h * .22f, w * .64f), pale, material).transform.localRotation = Quaternion.Euler(0, 0, side * 15);
                Part("Massive ice forearm", PrimitiveType.Cube, arm, Vector3.down * h * .18f, new Vector3(w * .47f, h * .28f, w * .52f), color, material);
                var wrist = Node("Ice hand guide", arm, Vector3.down * h * .34f);
                Part("Ice fist", PrimitiveType.Cube, wrist, Vector3.zero, new Vector3(w * .54f, h * .15f, w * .57f), dark, material);
                var leg = Node(side < 0 ? "Left ice hip" : "Right ice hip", model, new Vector3(side * w * .3f, h * .33f, 0));
                Part("Ice leg", PrimitiveType.Cube, leg, Vector3.down * h * .12f, new Vector3(w * .43f, h * .25f, w * .47f), color, material);
                Part("Ice foot", PrimitiveType.Cube, leg, new Vector3(0, -h * .28f, w * .13f), new Vector3(w * .55f, h * .10f, w * .8f), dark, material);
                var crystal = Cone("Shoulder crystal", arm, new Vector3(side * w * .1f, h * .09f, 0), new Vector3(w * .28f, h * .27f, w * .28f), pale, material);
                crystal.localRotation = Quaternion.Euler(0, 0, -side * 25);
                if (side < 0) { leftArm = arm; leftLeg = leg; } else { rightArm = arm; rightLeg = leg; rightHand = wrist; }
            }
            Cone("Ice crown", model, new Vector3(-w * .08f, h * .94f, 0), new Vector3(w * .3f, h * .16f, w * .28f), color, material);
        }

        private void LateUpdate()
        {
            if (entity == null || entity.Kind != "monster" || model == null) return;
            if (monster == null) monster = GetComponent<SandboxMonster>();
            float dt = Mathf.Max(.001f, Time.deltaTime);
            float speed = Vector3.ProjectOnPlane(transform.position - lastPosition, Vector3.up).magnitude / dt;
            lastPosition = transform.position;
            if (!entity.Alive)
            {
                model.localPosition = Vector3.up * height * .14f;
                model.localRotation = Quaternion.Euler(0, 0, 82);
                return;
            }
            string pose = monster != null ? monster.Pose : "idle";
            if (naturalWeapon != null) naturalWeapon.gameObject.SetActive(entity.HeldItem == null);
            stride += Mathf.Min(speed, 6) * dt * 3.2f;
            float gait = speed > .08f ? Mathf.Sin(stride) * 27 : 0;
            float t = Time.time;
            model.localPosition = Vector3.up * (speed > .08f ? Mathf.Abs(Mathf.Sin(stride)) * .025f : 0);
            model.localRotation = Quaternion.Euler(pose == "bow" ? 25 : 0, pose == "dance" ? Mathf.Sin(t * 8) * 22 : 0, pose == "dance" ? Mathf.Sin(t * 5) * 8 : 0);
            entity.Body.transform.localRotation = Quaternion.identity;
            leftLeg.localRotation = Quaternion.Euler(gait, 0, 0); rightLeg.localRotation = Quaternion.Euler(-gait, 0, 0);
            leftArm.localRotation = Quaternion.Euler(-gait * .75f, 0, 8);
            rightArm.localRotation = Quaternion.Euler(entity.HeldItem != null ? -52 : gait * .75f, 0, -8);
            if (pose == "wave") rightArm.localRotation = Quaternion.Euler(-20, 0, -145 + Mathf.Sin(t * 12) * 20);
            else if (pose == "dance")
            { leftArm.localRotation = Quaternion.Euler(Mathf.Sin(t * 9) * 35, 0, 75); rightArm.localRotation = Quaternion.Euler(-Mathf.Sin(t * 9) * 35, 0, -75); }
            else if (pose == "attacking" || pose == "fireball") rightArm.localRotation = Quaternion.Euler(-75 + Mathf.Sin(t * 17) * 32, 0, -10);
            else if (pose == "listening" || pose == "thinking") leftArm.localRotation = Quaternion.Euler(-35, 0, 20);
            // Demo-only poses: the shy lookout hides its face, sleepers slump.
            else if (pose == "cover") { leftArm.localRotation = Quaternion.Euler(-150, 0, 35); rightArm.localRotation = Quaternion.Euler(-150, 0, -35); }
            else if (pose == "sleep") model.localPosition = Vector3.down * height * .06f;
            if (leftWing != null) leftWing.localRotation = Quaternion.Euler(0, Mathf.Sin(t * 2) * 6, 0);
            if (rightWing != null) rightWing.localRotation = Quaternion.Euler(0, -Mathf.Sin(t * 2) * 6, 0);
            if (monster != null && monster.Hand != null && rightHand != null)
                monster.Hand.SetPositionAndRotation(rightHand.position, rightHand.rotation);
        }

        private static Transform Node(string name, Transform parent, Vector3 position)
        { var node = new GameObject(name).transform; node.SetParent(parent, false); node.localPosition = position; return node; }

        private static Renderer Part(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Color color, Func<Color, Material> material)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>(); if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = go.GetComponent<Renderer>(); renderer.sharedMaterial = material(color); return renderer;
        }

        private static void Segment(string name, Transform parent, Vector3 start, Vector3 end, float thickness, Color color, Func<Color, Material> material)
        {
            var segment = Part(name, PrimitiveType.Cylinder, parent, (start + end) * .5f, new Vector3(thickness, (end - start).magnitude * .5f, thickness), color, material);
            segment.transform.localRotation = Quaternion.FromToRotation(Vector3.up, end - start);
        }

        private Transform Cone(string name, Transform parent, Vector3 position, Vector3 scale, Color color, Func<Color, Material> material)
        {
            var shape = new Shape(); const int sides = 6;
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2 / sides, b = (i + 1) * Mathf.PI * 2 / sides;
                Vector3 p = new Vector3(Mathf.Cos(a) * .5f, 0, Mathf.Sin(a) * .5f), q = new Vector3(Mathf.Cos(b) * .5f, 0, Mathf.Sin(b) * .5f);
                shape.Triangle(p, Vector3.up, q); shape.Triangle(Vector3.zero, p, q);
            }
            var renderer = MeshPart(name, parent, shape.Mesh(name), color, material); renderer.transform.localPosition = position; renderer.transform.localScale = scale; return renderer.transform;
        }

        private Renderer MeshPart(string name, Transform parent, Mesh mesh, Color color, Func<Color, Material> material)
        {
            ownedMeshes.Add(mesh);
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material(color); return renderer;
        }
        private void OnDestroy() { foreach (var mesh in ownedMeshes) if (mesh != null) Destroy(mesh); }

        private sealed class Shape
        {
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int> triangles = new List<int>();
            public void Triangle(Vector3 a, Vector3 b, Vector3 c, bool doubleSided = false)
            {
                int i = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c); triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
                if (doubleSided) Triangle(c, b, a);
            }
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Triangle(a, b, c); Triangle(a, c, d); }
            public void Box(Vector3 center, Vector3 size)
            {
                Vector3 s = size * .5f;
                Vector3 a = center + new Vector3(-s.x, -s.y, -s.z), b = center + new Vector3(s.x, -s.y, -s.z), c = center + new Vector3(s.x, s.y, -s.z), d = center + new Vector3(-s.x, s.y, -s.z);
                Vector3 e = center + new Vector3(-s.x, -s.y, s.z), f = center + new Vector3(s.x, -s.y, s.z), g = center + new Vector3(s.x, s.y, s.z), h = center + new Vector3(-s.x, s.y, s.z);
                Quad(a, d, c, b); Quad(f, g, h, e); Quad(e, h, d, a); Quad(b, c, g, f); Quad(d, h, g, c); Quad(e, a, b, f);
            }
            public Mesh Mesh(string name) { var mesh = new Mesh { name = name }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh; }
        }
    }
}
