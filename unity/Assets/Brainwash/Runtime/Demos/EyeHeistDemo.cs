using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Demo 5 — stealth heist built on eye contact. Each guard breaks the
    // "look it in the eye" rule a different way; noise is the other currency.
    public sealed class EyeHeistDemo : DemoScenario
    {
        public override string Id => "eye_heist";
        public override string Title => "Eye Contact Heist";
        public override string Tagline => "Every guard has a different problem with eyes.";
        public override string Description => "Steal the relic from the vault and bring it back to the entrance. Gargoyles move only when nobody watches them. A shy goblin lookout hides its face. The Frost Golem wears an ice visor. The Mimic keeps its eyes inside a closed lid. And your voice carries — every command is a noise.";
        public override string[] Mechanics => new[]
        {
            "Gargoyles move only when unwatched; a brainwashed one acts only when you look away",
            "The shy lookout covers its face when you look at it — a thrown pebble (G) makes it look",
            "Melt the Golem's ice visor with fire before you can meet its eyes",
            "Lift the Mimic's lid with E; loud commands draw the guards"
        };
        public override string Objective => stage == 0 ? "Get into the vault: the lookout carries the vault key, and heavy monsters can smash the door."
            : stage == 1 ? "The vault is open. Take the relic (E)."
            : "Escape! Bring the relic back to the entrance circle.";
        public override string Intro => "Eye Contact Heist: walk through the gallery while watching the Gargoyles. Throw pebbles (G) to make guards look away — or look at you.";
        public override string WinText => "You walked out with the relic.";
        public override string WinSummary => "Heist complete in " + Clock(Elapsed) + ". Alarms raised: " + alarms + " · commands spoken: " + commands + " · vault opened by: " + (vaultMethod ?? "unknown") + ".";
        public override string LoseText => "Caught. Next time keep your eyes on the statues and your voice down.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -18);
        public override Vector3 NavCenter => new Vector3(0, 0, .5f);
        public override float NavHalfExtent => 21;
        public override Color Ambient => new Color(.22f, .24f, .32f);
        public override float SunIntensity => .35f;
        public override float FogDensity => .02f;
        public override Color FogColor => new Color(.04f, .05f, .08f);
        public override float PlayerWeaponDamage => 10;
        public override float PlayerRegen => .5f;
        public override string[] Presets => new[] { "Give me the key", "Stop", "Attack the door", "Attack the other gargoyle", "Give me your hidden treasure", "Move away from the door" };

        int stage, alarms, commands;
        float alarmUntil, nextAlarmPulse, lookoutCooldown;
        bool galleryAwake;
        string vaultMethod;
        SandboxMonster lookout, golem, imp, mimic;
        readonly List<SandboxMonster> gargoyles = new List<SandboxMonster>();
        SandboxEntity vaultDoor, relic;
        static readonly Vector3 Extraction = new Vector3(0, 0, -17.5f);

        public override void Build()
        {
            stage = 0; alarms = commands = 0; alarmUntil = 0; galleryAwake = false; vaultMethod = null; lookoutCooldown = 0; gargoyles.Clear();
            Color floor = new Color(.27f, .27f, .3f), wall = new Color(.3f, .31f, .36f), gold = new Color(1, .8f, .3f);
            // Entrance hall.
            Floor(-6, 6, -20, -10, floor);
            WallAlongX(-20, -6, 6, 4, wall); WallAlongX(-10, -6, 6, 4, wall, new Vector2(-1.3f, 1.3f));
            WallAlongZ(-6, -20, -10, 4, wall); WallAlongZ(6, -20, -10, 4, wall);
            World.SpawnObject("o_extract", "circle", "entrance circle", Extraction + Vector3.up * .02f, new Vector3(3.6f, .04f, 3.6f), new Color(.4f, .8f, .6f), false, false);
            World.PointLight(Extraction + Vector3.up, new Color(.5f, 1, .7f), 5, 1.2f);
            Box("Brazier stand", new Vector3(-4.6f, .45f, -12.2f), new Vector3(.8f, .9f, .8f), new Color(.25f, .2f, .18f));
            World.FireZone(new Vector3(-4.6f, .9f, -12.2f), new Vector3(.7f, .2f, .7f), 12, "brazier");
            World.SpawnProp(World.NextHandle("o_crate"), "crate", "wooden crate", new Vector3(-3, .45f, -14.5f), new Vector3(.8f, .8f, .8f), new Color(.55f, .4f, .22f), 14, 30);
            for (int i = 0; i < 4; i++)
                World.SpawnItem(World.NextHandle("i_pebble"), "pebble", "pebble", new Vector3(-1.2f + i * .8f, .15f, -15.8f), new Color(.6f, .6f, .62f), true, 1.5f);
            mimic = World.SpawnMonster("mimic", "m_mimic", "treasure chest", new Vector3(4.6f, 0, -12.5f), Vector3.left, "brown");
            mimic.Entity.Asleep = true; mimic.Brain.Aggro = 0;
            var firePot = World.SpawnItem("i_fire_pot", "fire_pot", "fire pot", mimic.transform.position + Vector3.up, new Color(.9f, .35f, .1f), true, 2);
            World.StoreSwallowed(mimic.Entity, firePot);
            // Gallery of statues.
            Floor(-5, 5, -10, 4, floor);
            WallAlongZ(-5, -10, 4, 4, wall); WallAlongZ(5, -10, 4, 4, wall);
            WallAlongX(4, -5, 5, 4, wall, new Vector2(-1.3f, 1.3f));
            foreach (var p in new[] { new Vector3(-3.8f, 0, -8), new Vector3(3.8f, 0, -3), new Vector3(-3.8f, 0, 2) })
            {
                World.Decor("Statue plinth", PrimitiveType.Cylinder, p + Vector3.up * .1f, new Vector3(1, .1f, 1), new Color(.25f, .25f, .27f));
                World.Decor("Harmless statue", PrimitiveType.Capsule, p + Vector3.up * 1.1f, new Vector3(.7f, .9f, .6f), new Color(.46f, .47f, .46f), true);
            }
            var g1 = World.SpawnMonster("gargoyle", "m_gargoyle_west", "west gargoyle", new Vector3(-3.4f, 0, -4.5f), Vector3.right, "grey");
            var g2 = World.SpawnMonster("gargoyle", "m_gargoyle_east", "east gargoyle", new Vector3(3.4f, 0, -.5f), Vector3.left, "grey");
            foreach (var g in new[] { g1, g2 }) { g.Brain.Passive = true; g.Brain.Aggro = 3; gargoyles.Add(g); }
            World.PointLight(new Vector3(0, 3, -3), new Color(.6f, .65f, .9f), 9, .9f);
            // Guard room.
            Floor(-9, 9, 4, 17, floor);
            WallAlongZ(-9, 4, 17, 4, wall); WallAlongZ(9, 4, 17, 4, wall);
            WallAlongX(4, -9, -5, 4, wall); WallAlongX(4, 5, 9, 4, wall);
            WallAlongX(17, -9, 9, 4, wall, new Vector2(-1.3f, 1.3f));
            Box("Guard table", new Vector3(5.5f, .45f, 10), new Vector3(2, .9f, 1.2f), new Color(.35f, .25f, .16f));
            Box("Crates", new Vector3(-7.5f, .6f, 15.5f), new Vector3(1.4f, 1.2f, 1.4f), new Color(.45f, .33f, .2f));
            lookout = World.SpawnMonster("goblin", "m_lookout", "shy goblin lookout", new Vector3(-5.5f, 0, 11), Vector3.right, "green");
            lookout.Brain.Eyes = EyeRule.Shy; lookout.Brain.GuardPost = true; lookout.Brain.Aggro = 1; lookout.Brain.Leash = 5;
            var key = World.SpawnItem("i_vault_key", "key", "vault key", lookout.transform.position + Vector3.up, new Color(.85f, .85f, .9f), true, 1);
            key.Attach(lookout.Entity, lookout.Hand);
            golem = World.SpawnMonster("ice_golem", "m_golem", "big blue ice golem", new Vector3(0, 0, 14.6f), Vector3.back, "blue");
            golem.Brain.GuardPost = true; golem.Brain.Leash = 4; golem.Brain.Aggro = 1;
            golem.Entity.VisorIntact = true; DemoVisuals.AddVisor(golem.Entity, golem.Entity.Profile.Width, golem.Entity.Profile.Height);
            imp = World.SpawnMonster("imp", "m_imp", "red imp", new Vector3(4, 0, 8), Vector3.left, "red");
            imp.Brain.Leash = 8;
            Torch(new Vector3(-8.6f, 2.6f, 8)); Torch(new Vector3(8.6f, 2.6f, 13));
            // Vault.
            vaultDoor = World.SpawnObject("o_vault_door", "door", "vault door", new Vector3(0, 1.3f, 17), new Vector3(2.6f, 2.6f, .3f), new Color(.4f, .38f, .35f), true);
            vaultDoor.Note = "locked"; vaultDoor.RestoreHealth(60);
            Floor(-4, 4, 17, 22, floor);
            WallAlongZ(-4, 17, 22, 4, wall); WallAlongZ(4, 17, 22, 4, wall); WallAlongX(22, -4, 4, 4, wall);
            World.SpawnObject("o_pedestal", "pedestal", "relic pedestal", new Vector3(0, .5f, 20), new Vector3(.8f, 1, .8f), new Color(.35f, .3f, .3f));
            relic = World.SpawnItem("i_relic", "relic", "golden relic", new Vector3(0, 1.3f, 20), gold, false, 2);
            World.PointLight(new Vector3(0, 2.5f, 20), gold, 6, 2.2f);
        }

        public override void Tick(float dt)
        {
            if (!galleryAwake && World.Player.position.z > -9.5f)
            {
                galleryAwake = true;
                foreach (var g in gargoyles) if (g != null) g.Brain.Passive = false;
                World.Report("Stone grinds behind you. The gargoyles move whenever you look away...");
            }
            // The lookout raises the alarm when it can see you and is not hiding its face.
            if (lookout != null && lookout.Entity.Alive && !World.IsControlled(lookout) && Time.time >= lookoutCooldown && lookout.Brain != null
                && !lookout.Brain.CoveringFace && lookout.Brain.State != "Distracted" && Sees(lookout, World.PlayerEntity, 11))
                RaiseAlarm("The lookout spots you and SHRIEKS!", 12);
            if (Time.time < alarmUntil && Time.time >= nextAlarmPulse)
            {
                nextAlarmPulse = Time.time + 1;
                foreach (var m in new[] { lookout, golem, imp })
                    if (m != null && m.Entity.Alive && m.Brain != null && !World.IsControlled(m)) m.Brain.Alert(World.Player.position);
            }
            if (stage == 0 && vaultDoor != null && (vaultDoor.Open || !vaultDoor.Alive)) stage = 1;
            if (stage == 2 && World.HeldBy(Player) == relic && PlayerNear(Extraction, 1.9f)) World.Win();
        }

        static bool Sees(SandboxMonster watcher, SandboxEntity target, float range)
        {
            float distance = Vector3.Distance(watcher.transform.position, target.transform.position);
            if (distance > range) return false;
            return (distance <= SandboxPerception.NearRadius || SandboxPerception.InCone(watcher.Eye, watcher.transform.forward, target.Center, 60, true))
                && SandboxPerception.HasLineOfSight(watcher.Eye, target, watcher.transform);
        }

        void RaiseAlarm(string message, float seconds)
        {
            if (Time.time >= alarmUntil) { alarms++; World.Report(message); World.Effects.NoiseRing(World.Player.position, 14, new Color(1, .3f, .2f)); GameAudio.Play2D("alarm_bell"); }
            alarmUntil = Mathf.Max(alarmUntil, Time.time + seconds);
            lookoutCooldown = Time.time + 4;
        }

        public override float AdjustDamage(SandboxEntity victim, float damage, SandboxEntity attacker, string element)
        {
            // Only something heavy can break the vault door.
            if (victim == vaultDoor && (attacker == null || attacker.Profile == null || attacker.Profile.Mass < 200)) return 0;
            return damage;
        }

        public override void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause)
        {
            if (victim == vaultDoor)
            {
                vaultMethod = attacker != null ? attacker.DisplayName + " smashed it" : "smashed";
                Vector3 doorCenter = vaultDoor.Center;
                World.Effects.Burst(doorCenter, new Color(.4f, .38f, .35f), 14, 6);
                vaultDoor.gameObject.SetActive(false);
                if (vaultDoor.Label != null) vaultDoor.Label.gameObject.SetActive(false);
                World.MakeNoise(doorCenter, 18);
                RaiseAlarm("The vault door shatters! Every guard heard that.", 10);
            }
        }

        public override bool OnInteract()
        {
            if (vaultDoor == null || !vaultDoor.Alive || vaultDoor.Open) return false;
            if (Vector3.ProjectOnPlane(World.Player.position - vaultDoor.transform.position, Vector3.up).magnitude > 2.6f) return false;
            if (World.HeldBy(Player)?.ItemKind != "key") { GameAudio.Play("door_locked", vaultDoor.Center, 1, 1, .6f); World.Report("The vault door is locked. The lookout carries the key — or something heavy could break it."); return true; }
            vaultDoor.Note = null; vaultDoor.SetOpen(true); vaultMethod = "the lookout's key";
            World.Report("The vault key turns. The vault is open.");
            return true;
        }

        public override void OnPlayerPickedUp(SandboxEntity item)
        {
            if (item != relic || stage == 2) return;
            stage = 2;
            RaiseAlarm("You lift the relic — a bell tolls through the vault! RUN.", 25);
            foreach (var g in gargoyles) if (g != null && g.Brain != null) { g.Brain.Passive = false; g.Brain.Aggro = 4; }
        }

        public override void OnCommandText(string transcript) { commands++; }
        public override void OnBrainwashed(SandboxMonster monster, Newtonsoft.Json.Linq.JObject compiled) { if (monster == lookout) lookoutCooldown = Time.time + 8; }

        public override IEnumerable<string> StatusLines()
        {
            if (Time.time < alarmUntil) yield return "ALARM! Guards are hunting you (" + Mathf.CeilToInt(alarmUntil - Time.time) + " s)";
            else yield return "Undetected · alarms so far: " + alarms;
            yield return "Golem visor: " + (golem != null && golem.Entity.VisorIntact ? "intact (needs fire)" : "melted") + " · Mimic: " + (mimic != null && mimic.Entity.Asleep ? "closed" : "open");
            yield return "Pebbles: G to pick up, G to throw — noise turns heads";
        }
    }
}
