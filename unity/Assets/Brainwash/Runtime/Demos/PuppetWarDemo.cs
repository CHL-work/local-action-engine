using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Demo 2 — indirect combat. The player can barely hurt anything; victory
    // comes from TDD §10 grudges, faction rules, elements and a caged Ogre.
    public sealed class PuppetWarDemo : DemoScenario
    {
        public override string Id => "puppet_war";
        public override string Title => "Puppet War";
        public override string Tagline => "You are weak. Make them fight each other.";
        public override string Description => "A goblin troop and a bioweapon outpost share a ruined courtyard, guarded by a Frost Golem Warden. Your staff tickles. Turn grudges into a war, let the Plague Nurse's healing backfire, free the caged Ogre — and let them kill the Warden for you.";
        public override string[] Mechanics => new[]
        {
            "Grudges: hit once → confused, again → one strike back, third → sworn enemy",
            "Brainwashing clears only the puppet's own grudges, never its victims'",
            "Frost Golem takes ×2 fire; imps cast fireballs",
            "Tithe of Chaos: +6 MP each time a monster kills a monster"
        };
        public override string Objective => warden != null && warden.Entity.Alive
            ? "Kill the Frost Golem Warden — your staff only does 3 damage. Warden HP " + Mathf.CeilToInt(warden.Entity.Health) + "/" + Mathf.CeilToInt(warden.Entity.MaximumHealth)
            : "The Warden has fallen!";
        public override string Intro => "Puppet War: you are too weak to win a fight. Brainwash an imp to fireball a goblin and watch the grudges spread.";
        public override string WinText => "The Warden is dead and you never landed a real blow.";
        public override string WinSummary => "The Warden fell in " + Clock(Elapsed) + ". Monster-on-monster kills: " + infighting + " · MP gained from chaos: " + tithe + " · your damage dealt: " + Mathf.RoundToInt(playerDamage) + ".";
        public override string LoseText => "You were caught in the crossfire. Stay out of sight and let them fight.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -12);
        public override float NavHalfExtent => 16;
        public override Color Ambient => new Color(.42f, .44f, .5f);
        public override float PlayerWeaponDamage => 3;
        public override float PlayerDamageScale => .8f;
        public override float PlayerRegen => .6f;
        public override string[] Presets => new[] { "Use fireball on the goblin", "Attack the ice golem", "Pull the lever", "Call your allies", "Heal the goblin", "Run away from the imp" };

        SandboxMonster warden, nurse, ogre;
        SandboxEntity cageGate;
        int infighting, tithe;
        float playerDamage;
        bool ogreFree;

        public override void Build()
        {
            infighting = tithe = 0; playerDamage = 0; ogreFree = false;
            Color ground = new Color(.36f, .38f, .3f), wall = new Color(.42f, .42f, .44f), ruin = new Color(.5f, .48f, .45f);
            Floor(-16, 16, -14, 14, ground);
            WallAlongX(-14, -16, 16, 3, wall); WallAlongX(14, -16, 16, 3, wall);
            WallAlongZ(-16, -14, 14, 3, wall); WallAlongZ(16, -14, 14, 3, wall);
            // Broken central wall: cover and choke points between the camps.
            WallAlongZ(0, -6, 9, 1.4f, ruin, new Vector2(-3, -1), new Vector2(3, 5));
            Box("Rubble", new Vector3(-6, .4f, -5), new Vector3(2, .8f, 1.2f), ruin);
            Box("Rubble", new Vector3(6, .4f, -6), new Vector3(1.4f, .8f, 2), ruin);
            // Goblin camp (west).
            Color tent = new Color(.45f, .3f, .18f);
            World.Decor("Tent", PrimitiveType.Cube, new Vector3(-12, 1, 8), new Vector3(3, 2, 3), tent, true, Quaternion.Euler(0, 45, 0));
            World.Decor("Tent", PrimitiveType.Cube, new Vector3(-12, 1, 1), new Vector3(2.5f, 2, 2.5f), tent, true, Quaternion.Euler(0, 20, 0));
            World.FireZone(new Vector3(-9, 0, 4.5f), new Vector3(1.2f, .2f, 1.2f), 16, "camp fire");
            string[] names = { "green goblin", "dark green goblin", "big goblin", "small goblin", "scarred goblin" };
            Vector3[] goblinSpots = { new Vector3(-10, 0, 6), new Vector3(-7, 0, 3), new Vector3(-10.6f, 0, 3.7f), new Vector3(-8, 0, 8), new Vector3(-6, 0, 6) };
            for (int i = 0; i < goblinSpots.Length; i++)
            {
                var goblin = World.SpawnMonster("goblin", World.NextHandle("m_goblin"), names[i], goblinSpots[i], Vector3.right, "green", i == 1 ? new Color(.3f, .55f, .16f) : (Color?)null);
                goblin.Brain.Aggro = 1.2f; goblin.Brain.Leash = 12;
            }
            // Bioweapon outpost (east).
            Box("Outpost wall", new Vector3(8, .9f, -1), new Vector3(6, 1.8f, .5f), wall);
            warden = World.SpawnMonster("ice_golem", "m_warden", "Frost Golem Warden", new Vector3(11, 0, 7), Vector3.left, "blue");
            warden.Entity.RestoreHealth(240);
            warden.Brain.GuardPost = true; warden.Brain.Leash = 6; warden.Brain.Aggro = 1.1f;
            var redImp = World.SpawnMonster("imp", "m_imp_red", "red imp", new Vector3(9, 0, 3), Vector3.left, "red");
            var orangeImp = World.SpawnMonster("imp", "m_imp_orange", "orange imp", new Vector3(13, 0, 10), Vector3.left, "orange", new Color(1, .45f, .12f));
            redImp.Brain.Leash = 10; orangeImp.Brain.Leash = 10;
            nurse = World.SpawnMonster("nurse", "m_nurse", "Plague Nurse", new Vector3(12, 0, 4), Vector3.left, "black");
            nurse.Brain.Leash = 9;
            World.SpawnProp(World.NextHandle("o_brazier"), "barrel", "oil barrel", new Vector3(7, .5f, 6), new Vector3(.7f, 1, .7f), new Color(.6f, .2f, .1f), 18, 25);
            // The caged Ogre (north) and the lever that opens its gate.
            Color iron = new Color(.25f, .25f, .27f);
            Box("Cage back", new Vector3(0, 1.6f, 13.6f), new Vector3(4.6f, 3.2f, .3f), iron);
            Box("Cage side", new Vector3(-2.3f, 1.6f, 12), new Vector3(.3f, 3.2f, 3.4f), iron);
            Box("Cage side", new Vector3(2.3f, 1.6f, 12), new Vector3(.3f, 3.2f, 3.4f), iron);
            Box("Cage roof", new Vector3(0, 3.25f, 12), new Vector3(4.9f, .2f, 3.6f), iron);
            cageGate = World.SpawnObject("o_cage_gate", "door", "cage gate", new Vector3(0, 1.4f, 10.3f), new Vector3(4.2f, 2.8f, .25f), new Color(.35f, .33f, .3f));
            World.SpawnObject("o_lever", "lever", "cage lever", new Vector3(-4, .65f, 9), new Vector3(.25f, 1.3f, .25f), Color.red);
            ogre = World.SpawnMonster("ogre", "m_ogre", "caged ogre", new Vector3(0, 0, 12.2f), Vector3.back, "grey");
            ogre.Brain.Aggro = 1.8f;
            Torch(new Vector3(-15.6f, 2.4f, 5)); Torch(new Vector3(15.6f, 2.4f, 5)); Torch(new Vector3(0, 2.4f, -13.6f)); Torch(new Vector3(-3, 2.4f, 13.6f));
        }

        public override float AdjustDamage(SandboxEntity victim, float damage, SandboxEntity attacker, string element)
        {
            if (attacker == Player && victim.Kind == "monster") playerDamage += damage;
            return damage;
        }

        public override void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause)
        {
            if (victim.Kind != "monster") return;
            if (attacker != null && attacker.Kind == "monster" && attacker != victim)
            {
                infighting++;
                float before = World.MP;
                World.GainMP(6);
                tithe += Mathf.RoundToInt(World.MP - before);
                World.Report("Tithe of Chaos: " + attacker.DisplayName + " killed " + victim.DisplayName + ". +6 MP");
                World.Effects.Popup(World.Player.position + World.Player.forward * 2 + Vector3.up * 2, "+6 MP", new Color(.4f, .7f, 1));
            }
            if (warden != null && victim == warden.Entity) World.Win();
        }

        public override void OnInteracted(SandboxEntity target, string operation, SandboxMonster actor)
        {
            if (target.ObjectKind == "lever") OpenCage(actor != null ? actor.Entity.DisplayName : "You");
        }

        public override bool OnInteract()
        {
            var lever = World.Find("o_lever");
            if (lever != null && Vector3.Distance(World.Player.position, lever.transform.position) < 2.2f)
            {
                lever.SetOpen(!lever.Open);
                OpenCage("You");
                return true;
            }
            return false;
        }

        void OpenCage(string who)
        {
            if (ogreFree || cageGate == null) return;
            ogreFree = true; cageGate.SetOpen(true);
            World.Report(who + " pulled the lever — the cage swings open and the Ogre is FREE. It hates everything that isn't an ogre.");
            World.MakeNoise(cageGate.transform.position, 20);
        }

        public override void Tick(float dt)
        {
            // Until freed the Ogre paces inside its cage.
            if (ogre != null && ogre.Brain != null) ogre.Brain.Passive = !ogreFree;
        }

        public override IEnumerable<string> StatusLines()
        {
            int goblins = CountAlive(m => m.Entity.Species == "goblin"), bio = CountAlive(m => m.Entity.Species == "imp" || m.Entity.Species == "nurse");
            yield return "Goblins " + goblins + " · imps/nurse " + bio + " · Ogre " + (ogre != null && ogre.Entity.Alive ? ogreFree ? "rampaging" : "caged" : "dead");
            yield return "Infighting kills " + infighting + " · Tithe +" + tithe + " MP";
            int angry = 0;
            foreach (var m in World.Monsters) if (m != null && m.Brain != null) foreach (var g in m.Brain.Grudges.Values) if (g.Stage >= 3) angry++;
            yield return "Active feuds: " + angry + "  (labels show ! !! and ANGRY)";
        }
    }
}
