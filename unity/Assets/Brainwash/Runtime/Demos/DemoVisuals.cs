using System;
using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Primitive models for the demo-only species, items and props. Like the
    // classic visuals, no decorative primitive keeps a collider.
    public sealed class DemoVisuals : MonoBehaviour
    {
        SandboxEntity entity;
        SandboxMonster monster;
        string kind;
        Transform model, head, leftArm, rightArm, leftLeg, rightLeg, leftWing, rightWing, lid, hand, tail, sac;
        float height, stride, lidAngle, lidTarget;
        Vector3 lastPosition;
        readonly List<Mesh> ownedMeshes = new List<Mesh>();
        Func<Color, Material> material;

        // ---------- Builders ----------
        public static void BuildCreature(SandboxEntity entity, SpeciesProfile profile, Color color, Func<Color, Material> material)
        {
            // Generated models (art/models) replace the primitive figures when present.
            if (CreatureAnimator.Build(entity, profile.Id, profile.Height, color) != null) return;
            var v = entity.gameObject.AddComponent<DemoVisuals>();
            v.entity = entity; v.kind = profile.Id; v.height = profile.Height; v.material = material; v.lastPosition = entity.transform.position;
            v.model = Node("Creature model", entity.transform, Vector3.zero); entity.VisualRoot = v.model;
            float w = profile.Width, h = profile.Height;
            switch (profile.Id)
            {
                case "mouse": v.Mouse(w, h, color); break;
                case "ogre": v.Ogre(w, h, color); break;
                case "harpy": v.Harpy(w, h, color); break;
                case "mimic": v.Mimic(w, h, color); break;
                case "nurse": v.Nurse(w, h, color); break;
                case "gargoyle": v.Gargoyle(w, h, color); break;
                case "bomb_toad": v.Toad(w, h, color); break;
                default: entity.Body = v.Part("Body", PrimitiveType.Capsule, v.model, Vector3.up * h * .5f, new Vector3(w, h * .5f, w), color); break;
            }
        }

        void Mouse(float w, float h, Color c)
        {
            Color pink = new Color(.95f, .62f, .68f), dark = new Color(.05f, .05f, .06f);
            entity.Body = Part("Mouse body", PrimitiveType.Sphere, model, new Vector3(0, h * .42f, -.05f), new Vector3(w * .85f, h * .62f, w * 1.35f), c);
            head = Node("Mouse head", model, new Vector3(0, h * .5f, w * .6f));
            Part("Head", PrimitiveType.Sphere, head, Vector3.zero, new Vector3(w * .62f, h * .5f, w * .75f), c);
            Part("Snout", PrimitiveType.Sphere, head, new Vector3(0, -h * .05f, w * .36f), new Vector3(w * .28f, h * .22f, w * .32f), Color.Lerp(c, Color.white, .25f));
            Part("Nose", PrimitiveType.Sphere, head, new Vector3(0, -h * .03f, w * .52f), Vector3.one * w * .1f, pink);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Ear", PrimitiveType.Sphere, head, new Vector3(side * w * .28f, h * .3f, -w * .05f), new Vector3(w * .42f, w * .42f, .05f), pink);
                Part("Eye", PrimitiveType.Sphere, head, new Vector3(side * w * .17f, h * .1f, w * .3f), Vector3.one * w * .12f, dark);
                Part("Whisker", PrimitiveType.Cube, head, new Vector3(side * w * .3f, -h * .05f, w * .42f), new Vector3(w * .4f, .01f, .01f), Color.white);
                var leg = Node("Leg", model, new Vector3(side * w * .3f, h * .18f, w * .25f)); Part("Paw", PrimitiveType.Sphere, leg, Vector3.down * h * .1f, Vector3.one * w * .2f, pink);
                var back = Node("Hind leg", model, new Vector3(side * w * .3f, h * .18f, -w * .35f)); Part("Paw", PrimitiveType.Sphere, back, Vector3.down * h * .1f, Vector3.one * w * .24f, pink);
                if (side < 0) { leftLeg = leg; leftArm = back; } else { rightLeg = leg; rightArm = back; }
            }
            tail = Node("Tail", model, new Vector3(0, h * .3f, -w * .75f));
            Segment("Tail", tail, Vector3.zero, new Vector3(0, .05f, -w * 1.1f), .05f, pink);
            hand = Node("Mouth socket", head, new Vector3(0, -h * .12f, w * .45f));
        }

        void Ogre(float w, float h, Color c)
        {
            Color skin = Color.Lerp(c, new Color(.75f, .6f, .5f), .35f), dark = Color.Lerp(c, Color.black, .4f);
            entity.Body = Part("Ogre torso", PrimitiveType.Sphere, model, new Vector3(0, h * .56f, -w * .05f), new Vector3(w * 1.15f, h * .42f, w * .85f), c);
            Part("Ogre belly", PrimitiveType.Sphere, model, new Vector3(0, h * .46f, w * .18f), new Vector3(w * .8f, h * .28f, w * .55f), skin);
            head = Node("Ogre head", model, new Vector3(0, h * .8f, w * .28f));
            Part("Head", PrimitiveType.Sphere, head, Vector3.zero, new Vector3(w * .48f, h * .17f, w * .45f), skin);
            Part("Brow ridge", PrimitiveType.Cube, head, new Vector3(0, h * .035f, w * .2f), new Vector3(w * .46f, h * .04f, w * .12f), dark);
            Part("Jaw", PrimitiveType.Cube, head, new Vector3(0, -h * .055f, w * .14f), new Vector3(w * .36f, h * .07f, w * .26f), skin);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Eye", PrimitiveType.Sphere, head, new Vector3(side * w * .09f, h * .01f, w * .21f), Vector3.one * w * .07f, new Color(1, .85f, .2f));
                Part("Tusk", PrimitiveType.Cube, head, new Vector3(side * w * .1f, -h * .03f, w * .27f), new Vector3(.05f, .12f, .05f), new Color(.95f, .92f, .8f));
                var arm = Node(side < 0 ? "Left shoulder" : "Right shoulder", model, new Vector3(side * w * .62f, h * .68f, w * .05f));
                Part("Upper arm", PrimitiveType.Capsule, arm, Vector3.down * h * .16f, new Vector3(w * .32f, h * .17f, w * .32f), c);
                Part("Forearm", PrimitiveType.Capsule, arm, Vector3.down * h * .4f, new Vector3(w * .28f, h * .14f, w * .28f), skin);
                var fist = Node("Fist", arm, Vector3.down * h * .55f);
                Part("Fist", PrimitiveType.Cube, fist, Vector3.zero, new Vector3(w * .3f, w * .26f, w * .3f), dark);
                var leg = Node(side < 0 ? "Left hip" : "Right hip", model, new Vector3(side * w * .3f, h * .3f, 0));
                Part("Leg", PrimitiveType.Capsule, leg, Vector3.down * h * .14f, new Vector3(w * .38f, h * .15f, w * .38f), c);
                Part("Foot", PrimitiveType.Cube, leg, new Vector3(0, -h * .29f, w * .1f), new Vector3(w * .4f, h * .05f, w * .55f), dark);
                if (side < 0) { leftArm = arm; leftLeg = leg; } else { rightArm = arm; rightLeg = leg; hand = fist; }
            }
        }

        void Harpy(float w, float h, Color c)
        {
            Color beak = new Color(.95f, .75f, .2f), feather = Color.Lerp(c, new Color(.25f, .2f, .35f), .3f);
            entity.Body = Part("Harpy body", PrimitiveType.Sphere, model, new Vector3(0, h * .55f, 0), new Vector3(w * .55f, h * .45f, w * .8f), c);
            head = Node("Harpy head", model, new Vector3(0, h * .82f, w * .32f));
            Part("Head", PrimitiveType.Sphere, head, Vector3.zero, Vector3.one * w * .36f, c);
            var b = Cone("Beak", head, new Vector3(0, -.02f, w * .2f), new Vector3(.12f, .32f, .12f), beak); b.localRotation = Quaternion.Euler(90, 0, 0);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Eye", PrimitiveType.Sphere, head, new Vector3(side * w * .1f, .04f, w * .14f), Vector3.one * .08f, new Color(1, .2f, .15f));
                var wing = Node(side < 0 ? "Left wing" : "Right wing", model, new Vector3(side * w * .22f, h * .65f, 0));
                Part("Wing", PrimitiveType.Cube, wing, new Vector3(side * w * .55f, 0, -w * .05f), new Vector3(w * 1.1f, .04f, w * .55f), feather);
                Part("Wing tip", PrimitiveType.Cube, wing, new Vector3(side * w * 1.2f, -.05f, -w * .2f), new Vector3(w * .4f, .03f, w * .4f), c);
                var leg = Node("Talon", model, new Vector3(side * w * .12f, h * .3f, 0));
                Segment("Leg", leg, Vector3.zero, new Vector3(0, -h * .22f, .05f), .05f, beak);
                Part("Claw", PrimitiveType.Cube, leg, new Vector3(0, -h * .24f, .08f), new Vector3(.12f, .04f, .16f), beak);
                if (side < 0) { leftWing = wing; leftLeg = leg; } else { rightWing = wing; rightLeg = leg; }
            }
            tail = Node("Tail feathers", model, new Vector3(0, h * .5f, -w * .4f));
            Part("Tail", PrimitiveType.Cube, tail, new Vector3(0, 0, -w * .2f), new Vector3(w * .4f, .04f, w * .5f), feather);
            hand = Node("Talon socket", model, new Vector3(0, h * .05f, .05f));
        }

        void Mimic(float w, float h, Color c)
        {
            Color metal = new Color(.55f, .5f, .35f), dark = Color.Lerp(c, Color.black, .5f), inside = new Color(.45f, .05f, .08f);
            entity.Body = Part("Chest base", PrimitiveType.Cube, model, new Vector3(0, h * .3f, 0), new Vector3(w, h * .6f, w * .7f), c);
            for (int side = -1; side <= 1; side += 2)
                Part("Iron band", PrimitiveType.Cube, model, new Vector3(side * w * .32f, h * .3f, 0), new Vector3(w * .08f, h * .62f, w * .72f), metal);
            Part("Mouth inside", PrimitiveType.Cube, model, new Vector3(0, h * .58f, 0), new Vector3(w * .9f, .04f, w * .6f), inside);
            lid = Node("Lid hinge", model, new Vector3(0, h * .6f, -w * .35f));
            Part("Lid", PrimitiveType.Cube, lid, new Vector3(0, h * .12f, w * .35f), new Vector3(w * 1.02f, h * .25f, w * .72f), c);
            Part("Lid band", PrimitiveType.Cube, lid, new Vector3(0, h * .12f, w * .35f), new Vector3(w * .1f, h * .27f, w * .74f), metal);
            Part("Lock", PrimitiveType.Cube, lid, new Vector3(0, h * .02f, w * .72f), new Vector3(.12f, .14f, .05f), new Color(.95f, .75f, .2f));
            for (int i = 0; i < 6; i++)
            {
                float x = (i - 2.5f) / 6f * w * .85f;
                Part("Tooth", PrimitiveType.Cube, model, new Vector3(x, h * .64f, w * .3f), new Vector3(.06f, .1f, .04f), Color.white);
                Part("Lid tooth", PrimitiveType.Cube, lid, new Vector3(x, -.02f, w * .66f), new Vector3(.06f, .09f, .04f), Color.white);
            }
            head = Node("Eyes", model, new Vector3(0, h * .66f, -w * .1f));
            for (int side = -1; side <= 1; side += 2) Part("Eye", PrimitiveType.Sphere, head, new Vector3(side * w * .16f, 0, 0), Vector3.one * .15f, new Color(1, .9f, .2f));
            Part("Tongue", PrimitiveType.Cube, model, new Vector3(0, h * .6f, w * .1f), new Vector3(w * .2f, .05f, w * .45f), new Color(.85f, .25f, .35f));
            hand = Node("Mouth socket", model, new Vector3(0, h * .7f, w * .2f));
            lidAngle = lidTarget = 0;
        }

        void Nurse(float w, float h, Color c)
        {
            Color mask = new Color(.92f, .9f, .82f), sacColor = new Color(.45f, 1, .3f);
            entity.Body = Part("Robe", PrimitiveType.Capsule, model, new Vector3(0, h * .45f, 0), new Vector3(w * .8f, h * .45f, w * .7f), c);
            head = Node("Nurse head", model, new Vector3(0, h * .86f, 0));
            Part("Hood", PrimitiveType.Sphere, head, Vector3.zero, Vector3.one * w * .55f, c);
            var beak = Cone("Plague mask beak", head, new Vector3(0, -.03f, w * .3f), new Vector3(.16f, .42f, .16f), mask); beak.localRotation = Quaternion.Euler(100, 0, 0);
            Part("Hat brim", PrimitiveType.Cylinder, head, new Vector3(0, w * .25f, 0), new Vector3(w * .95f, .02f, w * .95f), c);
            Part("Hat crown", PrimitiveType.Cylinder, head, new Vector3(0, w * .38f, 0), new Vector3(w * .5f, w * .13f, w * .5f), c);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Glass eye", PrimitiveType.Sphere, head, new Vector3(side * w * .13f, .05f, w * .22f), Vector3.one * .1f, new Color(.6f, 1, .5f));
                var arm = Node(side < 0 ? "Left shoulder" : "Right shoulder", model, new Vector3(side * w * .45f, h * .7f, 0));
                Part("Sleeve", PrimitiveType.Capsule, arm, Vector3.down * h * .15f, new Vector3(w * .22f, h * .16f, w * .22f), c);
                var wrist = Node("Hand", arm, Vector3.down * h * .32f);
                Part("Glove", PrimitiveType.Sphere, wrist, Vector3.zero, Vector3.one * w * .2f, mask);
                if (side < 0) leftArm = arm; else { rightArm = arm; hand = wrist; Part("Syringe", PrimitiveType.Cylinder, wrist, new Vector3(0, -.12f, .05f), new Vector3(.05f, .14f, .05f), sacColor); }
            }
            sac = Node("Sacs", model, new Vector3(0, h * .62f, -w * .42f));
            for (int i = 0; i < 3; i++) Part("Acid sac", PrimitiveType.Sphere, sac, new Vector3((i - 1) * w * .28f, (i % 2) * .12f, 0), Vector3.one * w * .32f, sacColor);
        }

        void Gargoyle(float w, float h, Color c)
        {
            Color dark = Color.Lerp(c, Color.black, .35f), eye = new Color(.9f, .2f, .15f);
            entity.Body = Part("Stone torso", PrimitiveType.Capsule, model, new Vector3(0, h * .5f, 0), new Vector3(w * .8f, h * .3f, w * .65f), c);
            head = Node("Gargoyle head", model, new Vector3(0, h * .82f, w * .12f));
            Part("Head", PrimitiveType.Cube, head, Vector3.zero, new Vector3(w * .45f, h * .17f, w * .45f), c);
            Part("Snout", PrimitiveType.Cube, head, new Vector3(0, -h * .03f, w * .25f), new Vector3(w * .28f, h * .09f, w * .2f), dark);
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Glowing eye", PrimitiveType.Sphere, head, new Vector3(side * w * .11f, h * .02f, w * .23f), Vector3.one * .08f, eye);
                var horn = Cone("Horn", head, new Vector3(side * w * .17f, h * .08f, -w * .05f), new Vector3(.1f, .3f, .1f), dark); horn.localRotation = Quaternion.Euler(-30, 0, -side * 25);
                var wing = Node(side < 0 ? "Left wing" : "Right wing", model, new Vector3(side * w * .3f, h * .7f, -w * .3f));
                Part("Folded wing", PrimitiveType.Cube, wing, new Vector3(side * w * .2f, -h * .1f, -w * .1f), new Vector3(w * .5f, h * .45f, .06f), dark).transform.localRotation = Quaternion.Euler(0, side * 30, side * 10);
                var arm = Node(side < 0 ? "Left shoulder" : "Right shoulder", model, new Vector3(side * w * .48f, h * .66f, w * .05f));
                Part("Arm", PrimitiveType.Capsule, arm, Vector3.down * h * .15f, new Vector3(w * .22f, h * .16f, w * .22f), c);
                var claw = Node("Claw", arm, Vector3.down * h * .32f); Part("Claw", PrimitiveType.Cube, claw, Vector3.zero, new Vector3(w * .24f, w * .14f, w * .3f), dark);
                var leg = Node("Leg", model, new Vector3(side * w * .22f, h * .28f, 0));
                Part("Leg", PrimitiveType.Capsule, leg, Vector3.down * h * .12f, new Vector3(w * .26f, h * .14f, w * .3f), c);
                if (side < 0) { leftWing = wing; leftArm = arm; leftLeg = leg; } else { rightWing = wing; rightArm = arm; rightLeg = leg; hand = claw; }
            }
            Part("Plinth", PrimitiveType.Cylinder, model, new Vector3(0, .04f, 0), new Vector3(w * 1.1f, .04f, w * 1.1f), dark);
        }

        void Toad(float w, float h, Color c)
        {
            Color belly = new Color(.85f, .82f, .45f), spot = new Color(.95f, .8f, .15f);
            entity.Body = Part("Toad body", PrimitiveType.Sphere, model, new Vector3(0, h * .45f, 0), new Vector3(w, h * .8f, w * 1.05f), c);
            Part("Belly", PrimitiveType.Sphere, model, new Vector3(0, h * .32f, w * .18f), new Vector3(w * .8f, h * .5f, w * .7f), belly);
            sac = Node("Throat sac", model, new Vector3(0, h * .3f, w * .42f));
            Part("Gas sac", PrimitiveType.Sphere, sac, Vector3.zero, new Vector3(w * .5f, h * .35f, w * .3f), spot);
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60 * Mathf.Deg2Rad;
                Part("Warty spot", PrimitiveType.Sphere, model, new Vector3(Mathf.Cos(a) * w * .38f, h * .68f, Mathf.Sin(a) * w * .38f - .05f), Vector3.one * .12f, spot);
            }
            head = Node("Eyes", model, new Vector3(0, h * .82f, w * .28f));
            for (int side = -1; side <= 1; side += 2)
            {
                Part("Bulging eye", PrimitiveType.Sphere, head, new Vector3(side * w * .2f, 0, 0), Vector3.one * w * .22f, new Color(1, .95f, .5f));
                Part("Pupil", PrimitiveType.Cube, head, new Vector3(side * w * .2f, 0, w * .1f), new Vector3(w * .12f, .03f, .03f), Color.black);
                var leg = Node("Leg", model, new Vector3(side * w * .4f, h * .15f, -w * .1f));
                Part("Haunch", PrimitiveType.Sphere, leg, Vector3.zero, new Vector3(w * .35f, h * .3f, w * .5f), c);
                if (side < 0) leftLeg = leg; else rightLeg = leg;
            }
            hand = Node("Mouth socket", model, new Vector3(0, h * .5f, w * .5f));
        }

        public static void AddVisor(SandboxEntity entity, float width, float height)
        {
            var animator = entity.Comp<CreatureAnimator>();
            var head = animator != null ? animator.HeadBone : null;
            var root = head != null ? head : entity.VisualRoot != null ? entity.VisualRoot : entity.transform;
            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube); visor.name = "Ice visor";
            var collider = visor.Comp<Collider>(); collider.enabled = false; Destroy(collider);
            if (head != null)
            {
                // A generated model's head moves: ride on the head bone at the eyes.
                visor.transform.SetParent(head, true);
                visor.transform.SetPositionAndRotation(entity.transform.TransformPoint(new Vector3(0, height * .855f, width * .42f)), entity.transform.rotation);
                Vector3 world = new Vector3(width * .78f, height * .2f, .14f), parent = head.lossyScale;
                visor.transform.localScale = new Vector3(world.x / parent.x, world.y / parent.y, world.z / parent.z);
            }
            else
            {
                visor.transform.SetParent(root, false);
                visor.transform.localPosition = new Vector3(0, height * .855f, width * .42f);
                visor.transform.localScale = new Vector3(width * .78f, height * .2f, .14f);
            }
            visor.Comp<Renderer>().sharedMaterial = entity.World.Material(new Color(.78f, .95f, 1f));
            for (int i = -1; i <= 1; i++)
            {
                var spike = GameObject.CreatePrimitive(PrimitiveType.Cube); spike.name = "Visor icicle";
                var c = spike.Comp<Collider>(); c.enabled = false; Destroy(c);
                spike.transform.SetParent(visor.transform, false);
                spike.transform.localPosition = new Vector3(i * .3f, -.6f, 0); spike.transform.localScale = new Vector3(.12f, .5f, .9f);
                spike.Comp<Renderer>().sharedMaterial = entity.World.Material(new Color(.85f, .97f, 1f));
            }
        }

        public static void RemoveVisor(SandboxEntity entity)
        {
            foreach (var t in entity.GetComponentsInChildren<Transform>(true))
                if (t.name == "Ice visor") { Destroy(t.gameObject); return; }
        }

        public static void SetLid(SandboxEntity entity, bool open)
        {
            var visuals = entity.Comp<DemoVisuals>();
            if (visuals != null) visuals.lidTarget = open ? -70 : 0;
        }

        public static void BuildItem(SandboxEntity entity, string kind, Color color, Func<Color, Material> material)
        {
            var box = entity.Comp<BoxCollider>();
            if (PropModels.Item(entity, kind, box != null ? box.size : new Vector3(.24f, .14f, .24f), color)) return;
            var v = entity.gameObject.AddComponent<DemoVisuals>(); v.entity = entity; v.kind = "item"; v.material = material;
            v.model = Node("Item model", entity.transform, Vector3.zero); entity.VisualRoot = v.model;
            switch (kind)
            {
                case "cheese":
                    entity.Body = v.Part("Cheese wedge", PrimitiveType.Cube, v.model, Vector3.zero, new Vector3(.34f, .18f, .26f), new Color(1, .85f, .25f));
                    for (int i = 0; i < 3; i++) v.Part("Cheese hole", PrimitiveType.Sphere, v.model, new Vector3((i - 1) * .1f, .05f, .13f), Vector3.one * .06f, new Color(.85f, .65f, .1f));
                    break;
                case "relic":
                    entity.Body = v.Part("Relic idol", PrimitiveType.Capsule, v.model, Vector3.up * .05f, new Vector3(.2f, .16f, .2f), new Color(1, .78f, .2f));
                    v.Part("Relic eye", PrimitiveType.Sphere, v.model, new Vector3(0, .12f, .09f), Vector3.one * .09f, new Color(.2f, .9f, 1f));
                    v.Part("Relic base", PrimitiveType.Cylinder, v.model, Vector3.down * .13f, new Vector3(.26f, .04f, .26f), new Color(.6f, .45f, .15f));
                    break;
                case "scripture":
                    entity.Body = v.Part("Scripture cover", PrimitiveType.Cube, v.model, Vector3.zero, new Vector3(.3f, .08f, .38f), new Color(.45f, .08f, .1f));
                    v.Part("Pages", PrimitiveType.Cube, v.model, new Vector3(.02f, 0, 0), new Vector3(.28f, .06f, .36f), new Color(.95f, .92f, .8f));
                    break;
                default:
                    entity.Body = v.Part("Pebble", PrimitiveType.Sphere, v.model, Vector3.zero, new Vector3(.22f, .16f, .2f), color);
                    break;
            }
        }

        public static void BuildObject(SandboxEntity entity, string kind, Vector3 size, Color color, Func<Color, Material> material)
        {
            if (PropModels.Object(entity, kind, size, color)) return;
            var v = entity.gameObject.AddComponent<DemoVisuals>(); v.entity = entity; v.kind = "object"; v.material = material;
            v.model = Node("Prop model", entity.transform, Vector3.zero); entity.VisualRoot = v.model;
            Color dark = Color.Lerp(color, Color.black, .4f);
            switch (kind)
            {
                case "barrel":
                    entity.Body = v.Part("Barrel", PrimitiveType.Cylinder, v.model, Vector3.zero, new Vector3(size.x, size.y * .5f, size.z), color);
                    for (int i = -1; i <= 1; i += 2) v.Part("Iron hoop", PrimitiveType.Cylinder, v.model, Vector3.up * i * size.y * .3f, new Vector3(size.x * 1.04f, .03f, size.z * 1.04f), new Color(.2f, .2f, .22f));
                    v.Part("Warning band", PrimitiveType.Cylinder, v.model, Vector3.zero, new Vector3(size.x * 1.02f, .06f, size.z * 1.02f), new Color(1, .85f, .1f));
                    break;
                case "brazier":
                    entity.Body = v.Part("Brazier bowl", PrimitiveType.Cylinder, v.model, Vector3.up * size.y * .35f, new Vector3(size.x, size.y * .12f, size.z), new Color(.3f, .25f, .2f));
                    v.Part("Brazier stand", PrimitiveType.Cylinder, v.model, Vector3.down * size.y * .1f, new Vector3(size.x * .2f, size.y * .38f, size.z * .2f), dark);
                    v.Part("Coals", PrimitiveType.Cylinder, v.model, Vector3.up * size.y * .45f, new Vector3(size.x * .85f, .03f, size.z * .85f), new Color(1, .35f, .05f));
                    break;
                case "pedestal":
                    entity.Body = v.Part("Pedestal", PrimitiveType.Cylinder, v.model, Vector3.zero, new Vector3(size.x, size.y * .5f, size.z), color);
                    v.Part("Pedestal top", PrimitiveType.Cylinder, v.model, Vector3.up * size.y * .5f, new Vector3(size.x * 1.25f, .05f, size.z * 1.25f), dark);
                    break;
                case "circle":
                    entity.Body = v.Part("Rite circle", PrimitiveType.Cylinder, v.model, Vector3.zero, new Vector3(size.x, .02f, size.z), color);
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * Mathf.PI * 2 / 12;
                        v.Part("Rune stone", PrimitiveType.Cube, v.model, new Vector3(Mathf.Cos(a) * size.x * .52f, .1f, Mathf.Sin(a) * size.z * .52f), new Vector3(.18f, .2f, .18f), new Color(.75f, .7f, .55f));
                    }
                    break;
                case "ledge":
                    entity.Body = v.Part("Ledge lip", PrimitiveType.Cube, v.model, Vector3.zero, new Vector3(size.x, .06f, size.z), Color.Lerp(color, Color.white, .15f));
                    break;
                case "cage":
                    entity.Body = v.Part("Cage floor", PrimitiveType.Cube, v.model, Vector3.down * size.y * .5f, new Vector3(size.x, .05f, size.z), dark);
                    for (float x = -size.x * .5f; x <= size.x * .5f + .01f; x += .4f)
                        v.Part("Cage bar", PrimitiveType.Cylinder, v.model, new Vector3(x, 0, size.z * .5f), new Vector3(.06f, size.y * .5f, .06f), color);
                    v.Part("Cage top", PrimitiveType.Cube, v.model, Vector3.up * size.y * .5f, new Vector3(size.x, .08f, size.z), dark);
                    break;
                case "pit": case "fire": case "void":
                    // Hazard markers are invisible: the spikes and flames show the danger.
                    // With no body, sight and targeting use the point one metre above the
                    // marker, which no floor or rim can hide.
                    break;
                default:
                    entity.Body = v.Part(kind, PrimitiveType.Cube, v.model, Vector3.zero, new Vector3(Mathf.Max(.1f, size.x), Mathf.Max(.02f, size.y), Mathf.Max(.1f, size.z)), color);
                    break;
            }
        }

        public static void BuildRemains(SandboxEntity entity, Color robe, Func<Color, Material> material)
        {
            if (PropModels.Remains(entity, robe)) return;
            var v = entity.gameObject.AddComponent<DemoVisuals>(); v.entity = entity; v.kind = "remains"; v.material = material;
            v.model = Node("Remains model", entity.transform, Vector3.zero); entity.VisualRoot = v.model;
            entity.Body = v.Part("Fallen robe", PrimitiveType.Capsule, v.model, new Vector3(0, .2f, 0), new Vector3(.55f, .75f, .4f), robe);
            entity.Body.transform.localRotation = Quaternion.Euler(90, 0, 0);
            v.Part("Head", PrimitiveType.Sphere, v.model, new Vector3(0, .2f, .85f), Vector3.one * .32f, new Color(.85f, .72f, .6f));
            v.Part("Hood", PrimitiveType.Sphere, v.model, new Vector3(0, .26f, .8f), new Vector3(.4f, .3f, .4f), robe);
        }

        // A revived cultist stands up; a fallen one lies down again.
        public static void SetRemainsStanding(SandboxEntity entity, bool standing)
        {
            if (PropModels.SetStanding(entity, standing)) return;
            var v = entity.Comp<DemoVisuals>();
            if (v == null || v.model == null) return;
            v.model.localRotation = standing ? Quaternion.Euler(-90, 0, 0) : Quaternion.identity;
            v.model.localPosition = standing ? new Vector3(0, .75f, .25f) : Vector3.zero;
        }

        // ---------- Animation ----------
        void LateUpdate()
        {
            if (entity == null || model == null) return;
            if (kind == "remains" || kind == "item" || kind == "object") return;
            if (monster == null) monster = this.Comp<SandboxMonster>();
            float dt = Mathf.Max(.001f, Time.deltaTime), t = Time.time;
            float speed = Vector3.ProjectOnPlane(transform.position - lastPosition, Vector3.up).magnitude / dt;
            lastPosition = transform.position;
            if (lid != null)
            {
                lidTarget = entity.Asleep ? 0 : entity.Alive ? -55 - Mathf.Abs(Mathf.Sin(t * 3)) * 15 : -20;
                if (monster != null && monster.Pose == "attacking") lidTarget = -80 + Mathf.Sin(t * 20) * 25;
                lidAngle = Mathf.MoveTowards(lidAngle, lidTarget, 220 * dt);
                lid.localRotation = Quaternion.Euler(lidAngle, 0, 0);
                if (head != null) head.gameObject.SetActive(!entity.Asleep);
            }
            if (!entity.Alive)
            {
                model.localPosition = Vector3.up * height * .12f;
                model.localRotation = Quaternion.Euler(0, 0, kind == "mimic" ? 20 : 82);
                return;
            }
            string pose = monster != null ? monster.Pose : "idle";
            if (pose == "statue") { model.localPosition = Vector3.zero; return; }
            stride += Mathf.Min(speed, 7) * dt * 3.4f;
            float gait = speed > .08f ? Mathf.Sin(stride) * 30 : 0;
            model.localPosition = Vector3.up * (speed > .08f ? Mathf.Abs(Mathf.Sin(stride)) * .03f : 0);
            model.localRotation = Quaternion.identity;
            if (pose == "sleep") { model.localPosition = Vector3.down * height * .05f; model.localScale = new Vector3(1, 1 + Mathf.Sin(t * 1.6f) * .03f, 1); }
            else model.localScale = Vector3.one;
            if (pose == "confused") model.localRotation = Quaternion.Euler(0, Mathf.Sin(t * 6) * 20, Mathf.Sin(t * 4) * 6);
            else if (pose == "dance") model.localRotation = Quaternion.Euler(0, Mathf.Sin(t * 8) * 24, Mathf.Sin(t * 5) * 9);
            else if (pose == "bow") model.localRotation = Quaternion.Euler(28, 0, 0);
            if (leftLeg != null) leftLeg.localRotation = Quaternion.Euler(gait, 0, 0);
            if (rightLeg != null) rightLeg.localRotation = Quaternion.Euler(-gait, 0, 0);
            if (leftArm != null) leftArm.localRotation = Quaternion.Euler(-gait * .7f, 0, 6);
            if (rightArm != null) rightArm.localRotation = Quaternion.Euler(entity.HeldItem != null ? -50 : gait * .7f, 0, -6);
            if (pose == "attacking" || pose == "fireball")
            {
                if (rightArm != null) rightArm.localRotation = Quaternion.Euler(-80 + Mathf.Sin(t * 16) * 40, 0, -10);
                if (kind == "mouse" || kind == "bomb_toad") model.localPosition += Vector3.forward * Mathf.Abs(Mathf.Sin(t * 14)) * .12f;
            }
            else if (pose == "cover")
            {
                if (leftArm != null) leftArm.localRotation = Quaternion.Euler(-150, 0, 35);
                if (rightArm != null) rightArm.localRotation = Quaternion.Euler(-150, 0, -35);
                if (head != null) head.localRotation = Quaternion.Euler(25, 0, 0);
            }
            else if (pose == "heal" && leftArm != null && rightArm != null)
            { leftArm.localRotation = Quaternion.Euler(-160, 0, 20); rightArm.localRotation = Quaternion.Euler(-160, 0, -20); }
            else if (pose == "wave" && rightArm != null) rightArm.localRotation = Quaternion.Euler(-20, 0, -145 + Mathf.Sin(t * 12) * 20);
            if (head != null && pose != "cover") head.localRotation = pose == "listening" || pose == "thinking" ? Quaternion.Euler(-10, 0, 12) : Quaternion.identity;
            bool flying = monster != null && monster.Flying;
            float flap = flying ? Mathf.Sin(t * (speed > .3f ? 16 : 9)) * 38 : Mathf.Sin(t * 2) * 5;
            if (leftWing != null) leftWing.localRotation = Quaternion.Euler(0, 0, kind == "gargoyle" ? 0 : flap);
            if (rightWing != null) rightWing.localRotation = Quaternion.Euler(0, 0, kind == "gargoyle" ? 0 : -flap);
            if (flying) model.localPosition += Vector3.up * Mathf.Sin(t * 3) * .08f;
            if (tail != null) tail.localRotation = Quaternion.Euler(0, Mathf.Sin(t * 5) * 20, 0);
            if (sac != null && kind == "bomb_toad") sac.localScale = Vector3.one * (1 + Mathf.Abs(Mathf.Sin(t * (pose == "attacking" ? 9 : 2))) * .35f);
            if (monster != null && monster.Hand != null && hand != null) monster.Hand.SetPositionAndRotation(hand.position, hand.rotation);
        }

        // ---------- Primitive helpers ----------
        static Transform Node(string name, Transform parent, Vector3 position)
        { var node = new GameObject(name).transform; node.SetParent(parent, false); node.localPosition = position; return node; }

        Renderer Part(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            var collider = go.Comp<Collider>(); if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = go.Comp<Renderer>(); renderer.sharedMaterial = material(color); return renderer;
        }

        void Segment(string name, Transform parent, Vector3 start, Vector3 end, float thickness, Color color)
        {
            var segment = Part(name, PrimitiveType.Cylinder, parent, (start + end) * .5f, new Vector3(thickness, (end - start).magnitude * .5f, thickness), color);
            segment.transform.localRotation = Quaternion.FromToRotation(Vector3.up, end - start);
        }

        Transform Cone(string name, Transform parent, Vector3 position, Vector3 scale, Color color)
        {
            var vertices = new List<Vector3>(); var triangles = new List<int>(); const int sides = 6;
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2 / sides, b = (i + 1) * Mathf.PI * 2 / sides;
                Vector3 p = new Vector3(Mathf.Cos(a) * .5f, 0, Mathf.Sin(a) * .5f), q = new Vector3(Mathf.Cos(b) * .5f, 0, Mathf.Sin(b) * .5f);
                int n = vertices.Count; vertices.Add(p); vertices.Add(Vector3.up); vertices.Add(q); triangles.Add(n); triangles.Add(n + 1); triangles.Add(n + 2);
                n = vertices.Count; vertices.Add(Vector3.zero); vertices.Add(p); vertices.Add(q); triangles.Add(n); triangles.Add(n + 1); triangles.Add(n + 2);
            }
            var mesh = new Mesh { name = name }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            ownedMeshes.Add(mesh);
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material(color);
            go.transform.localPosition = position; go.transform.localScale = scale; return go.transform;
        }

        void OnDestroy() { foreach (var mesh in ownedMeshes) if (mesh != null) Destroy(mesh); }
    }
}
