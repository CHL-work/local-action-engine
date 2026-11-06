using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Demo 3 — a non-combat puzzle with many answers. Crossing the chasm and
    // getting a key are two problems; every monster solves one of them.
    public sealed class LedgeKeyDemo : DemoScenario
    {
        public override string Id => "ledge_key";
        public override string Title => "The Ledge Key";
        public override string Tagline => "One locked door, many ways through.";
        public override string Description => "The exit is across a spike chasm and the only key is with a goblin on a high ledge. Nobody here wants to fight. Every monster is a different tool: fly, toss, frame, burn or bribe your way across — the demo records which solutions you found.";
        public override string[] Mechanics => new[]
        {
            "Harpy: carry you, fly, fetch (carry_to, fly)",
            "Ogre (asleep — make noise): carry you and hurl you across",
            "Cheese Mouse: hold cheese so it looks at you; 'that goblin stole your cheese'",
            "Imp: fireball the bridge rope, or turn around and believe it is a dragon",
            "Mimic: lift the lid (E) and ask for its hidden treasure"
        };
        public override string Objective => exitOpen ? "The exit is open — walk through!"
            : !crossed ? "Cross the spike chasm, get a key and open the exit on the far side (E)."
            : !HasKey ? "You are across. Now get a key." : "Open the exit door (E) on the north wall.";
        public override string Intro => "The Ledge Key: no fighting needed. Each monster here is a different way across the chasm or to the key.";
        public override string WinText => "You found a way through.";
        public override string WinSummary => "Escaped in " + Clock(Elapsed) + ". Crossed by: " + (crossing ?? "unknown") + " · Key: " + (keyMethod ?? "unknown") + ".\nSolutions discovered this session: " + discovered.Count + " — try another combination!";
        public override string LoseText => "You fell. The spikes are not a solution.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -10);
        public override float NavHalfExtent => 13;
        public override Vector3 NavCenter => new Vector3(0, 0, .5f);
        public override Color Ambient => new Color(.46f, .48f, .55f);
        public override float PlayerWeaponDamage => 12;
        public override float PlayerRegen => 2;
        public override string[] Presets => new[] { "Carry me to the ledge", "Use fireball on the rope", "Turn around. You are a mighty dragon!", "That goblin stole your cheese!", "Give me your hidden treasure", "Throw the key to me", "Fly to the key and bring it to me", "Wake up and carry me to the door" };

        static readonly HashSet<string> discovered = new HashSet<string>();
        SandboxMonster goblin, harpy, ogre, mouse, imp, mimic;
        SandboxEntity exitDoor, rope, brassKey, spareKey;
        GameObject raisedBridge;
        Transform bridgePivot;
        bool bridgeDown, crossed, exitOpen;
        float bridgeFall = -1;
        string crossing, keyMethod, pendingCross;
        float carryLanding;

        bool HasKey => World.HeldBy(Player)?.ItemKind == "key";

        public override void Build()
        {
            bridgeDown = crossed = exitOpen = false; bridgeFall = -1; crossing = keyMethod = pendingCross = null; carryLanding = 0;
            Color floor = new Color(.42f, .4f, .37f), wall = new Color(.36f, .37f, .4f), wood = new Color(.5f, .35f, .2f);
            World.FloorWithHoles(new Rect(-12, -12, 24, 25), new List<Rect> { new Rect(-12, -1, 24, 7) }, 0, floor);
            World.SpikePit(new Vector3(0, 0, 2.5f), new Vector2(24, 7), 2.4f);
            WallAlongX(-12, -12, 12, 4, wall); WallAlongX(13, -12, 12, 4, wall, new Vector2(4.7f, 7.3f));
            WallAlongZ(-12, -12, 13, 4, wall); WallAlongZ(12, -12, 13, 4, wall);
            // Exit corridor beyond the locked door.
            Floor(4.5f, 7.5f, 13, 17, floor);
            WallAlongZ(4.5f, 13, 17, 3, wall); WallAlongZ(7.5f, 13, 17, 3, wall); WallAlongX(17, 4.5f, 7.5f, 3, wall);
            World.PointLight(new Vector3(6, 2.2f, 15.5f), new Color(.7f, 1, .8f), 6, 2.5f);
            exitDoor = World.SpawnObject("o_exit", "door", "exit door", new Vector3(6, 1.3f, 13), new Vector3(2.6f, 2.6f, .25f), new Color(.45f, .28f, .12f));
            exitDoor.Note = "locked";
            // High ledge with a ramp on the far side.
            var ledge = Ledge("o_ledge", "high ledge", new Vector3(-7, 2.4f, 9.5f), new Vector2(5.6f, 4.4f), new Color(.4f, .38f, .36f));
            Ramp(new Vector3(.6f, 0, 9.5f), new Vector3(-4.2f, 2.4f, 9.5f), 2, new Color(.38f, .36f, .34f));
            // Raised drawbridge held up by a rope on the far edge.
            bridgePivot = new GameObject("Drawbridge hinge").transform; bridgePivot.SetParent(World.RoomRoot, false);
            bridgePivot.position = new Vector3(0, -.15f, 6.15f);
            raisedBridge = GameObject.CreatePrimitive(PrimitiveType.Cube); raisedBridge.name = "Drawbridge";
            raisedBridge.transform.SetParent(bridgePivot, false); raisedBridge.transform.localPosition = new Vector3(0, 3.65f, 0);
            raisedBridge.transform.localScale = new Vector3(2.4f, 7.3f, .3f);
            raisedBridge.Comp<Renderer>().sharedMaterial = SurfaceTextures.Material(wood, SurfaceTextures.Kind.Wood);
            SurfaceTextures.WorldUV(raisedBridge);
            World.Decor("Rope post", PrimitiveType.Cube, new Vector3(1.75f, 1.9f, 6.75f), new Vector3(.3f, 3.8f, .3f), new Color(.3f, .22f, .14f), true);
            rope = World.SpawnObject("o_rope", "rope", "bridge rope", new Vector3(1.75f, 2.2f, 6.45f), new Vector3(.14f, 3.4f, .14f), new Color(.85f, .75f, .35f), true);
            rope.RestoreHealth(20); rope.Flammable = true;
            // The key keeper on the ledge, looking out over the chasm.
            goblin = World.SpawnMonster("goblin", "m_keeper", "goblin keeper", new Vector3(-7, 2.45f, 8.4f), Vector3.back, "green");
            goblin.Brain.Passive = true; goblin.Brain.Aggro = 0;
            brassKey = World.SpawnItem("i_key", "key", "brass key", goblin.transform.position + Vector3.up, new Color(1, .75f, .15f), true, 1);
            brassKey.Attach(goblin.Entity, goblin.Hand);
            // Player side: the tools.
            harpy = World.SpawnMonster("harpy", "m_harpy", "harpy", new Vector3(7, 0, -4), Vector3.left, "black");
            harpy.Brain.Aggro = 0; harpy.Brain.GuardPost = true;
            ogre = World.SpawnMonster("ogre", "m_ogre", "sleeping ogre", new Vector3(-8, 0, -6), Vector3.right, "grey");
            ogre.Brain.Aggro = 0; ogre.Entity.Asleep = true; ogre.Brain.GuardPost = true;
            mouse = World.SpawnMonster("mouse", "m_mouse", "cheese mouse", new Vector3(5, 0, -9), Vector3.back, "grey");
            mouse.Brain.Aggro = 0;
            World.SpawnItem("i_cheese", "cheese", "cheese", new Vector3(-9, .2f, -10.5f), new Color(1, .85f, .25f), true, 1);
            imp = World.SpawnMonster("imp", "m_imp", "red imp", new Vector3(-1.6f, 0, -1.8f), Vector3.back, "red");
            imp.Brain.Passive = true; imp.Brain.Aggro = 0;
            mimic = World.SpawnMonster("mimic", "m_mimic", "treasure chest", new Vector3(9.5f, 0, -10.3f), Vector3.left, "brown");
            mimic.Entity.Asleep = true; mimic.Brain.Aggro = 0;
            spareKey = World.SpawnItem("i_spare_key", "key", "spare key", mimic.transform.position + Vector3.up, new Color(.8f, .8f, .85f), true, 1);
            World.StoreSwallowed(mimic.Entity, spareKey);
            Torch(new Vector3(-11.6f, 2.6f, -6)); Torch(new Vector3(11.6f, 2.6f, -6)); Torch(new Vector3(-11.6f, 2.6f, 10)); Torch(new Vector3(3.5f, 2.6f, 12.6f));
        }

        public override void Tick(float dt)
        {
            if (bridgeFall >= 0)
            {
                bridgeFall += dt;
                float t = Mathf.Clamp01(bridgeFall / .9f);
                bridgePivot.localRotation = Quaternion.Euler(-90 * t * t, 0, 0);
                if (t >= 1) { bridgeFall = -1; World.MakeNoise(new Vector3(0, 0, 2.5f), 14); World.Report("CRASH! The drawbridge spans the chasm."); }
            }
            bool farSide = World.Player.position.z > 6.3f && World.Player.position.y > -.5f;
            if (!crossed && farSide && World.Carried != Player)
            {
                crossed = true;
                crossing = pendingCross ?? (bridgeDown ? "Drawbridge" : "A daring leap");
                Discover("cross:" + crossing);
                World.Report("You made it across! (" + crossing + ")");
            }
            if (exitOpen && World.Player.position.z > 13.6f && Mathf.Abs(World.Player.position.x - 6) < 1.4f) World.Win();
            if (pendingCross != null && World.Carried != Player && Time.time > carryLanding + 2.5f && !farSide) pendingCross = null;
        }

        public override void OnCarried(SandboxMonster carrier, SandboxEntity cargo, bool thrown)
        {
            if (cargo != Player) return;
            pendingCross = carrier == ogre ? "Ogre toss" : carrier == harpy ? "Harpy ferry" : carrier.Entity.DisplayName;
            carryLanding = Time.time;
        }

        public override void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause)
        {
            if (victim == rope && !bridgeDown)
            {
                bridgeDown = true; bridgeFall = 0;
                rope.gameObject.SetActive(false); if (rope.Label != null) rope.Label.gameObject.SetActive(false);
                World.Report("The rope " + (element == "fire" ? "burns through" : "snaps") + " — the drawbridge falls!");
                GameAudio.Play("rope_burn", rope.Center); GameAudio.Play("bridge_fall", rope.Center);
                if (attacker == imp.Entity) Discover("tool:imp");
            }
        }

        public override void OnItemFreed(SandboxEntity item, string how, SandboxEntity from)
        {
            if (item.ItemKind != "key") return;
            string method = how == "rat_tide" ? "Rat Tide" : how == "hidden_gift" ? "Mimic's gift" : how == "killed" ? "Brute force"
                : how == "commanded" && from == goblin.Entity ? "Brainwashed keeper" : how == "commanded" && from == harpy.Entity ? "Harpy courier" : null;
            if (method != null) item.Note = method;
        }

        public override void OnPlayerPickedUp(SandboxEntity item)
        {
            if (item.ItemKind != "key") return;
            keyMethod = item.Note ?? (item == brassKey ? "Picked up the keeper's key" : "Picked up the spare key");
            Discover("key:" + keyMethod);
            World.Report("You have a key! (" + keyMethod + ")");
        }

        public override bool OnInteract()
        {
            if (exitDoor == null || exitOpen) return false;
            if (Vector3.ProjectOnPlane(World.Player.position - exitDoor.transform.position, Vector3.up).magnitude > 2.6f) return false;
            if (!HasKey) { GameAudio.Play("door_locked", exitDoor.Center, 1, 1, .6f); World.Report("The exit door is locked. You need a key."); return true; }
            exitOpen = true; exitDoor.Note = null; exitDoor.SetOpen(true);
            World.Report("The key turns. The exit is open — walk through!");
            return true;
        }

        void Discover(string id)
        {
            if (discovered.Add(id)) World.Effects.Popup(World.Player.position + World.Player.forward * 2 + Vector3.up * 1.8f, "NEW SOLUTION", new Color(.5f, 1, .6f));
        }

        public override IEnumerable<string> StatusLines()
        {
            yield return "Crossing: " + (crossing ?? (bridgeDown ? "the drawbridge is down" : "not yet")) + " · Key: " + (keyMethod ?? (HasKey ? "in hand" : "not yet"));
            yield return "Solutions discovered this session: " + discovered.Count;
            yield return "Tip: E lifts the chest lid · noise wakes the Ogre · cheese calms the Mouse";
        }
    }
}
