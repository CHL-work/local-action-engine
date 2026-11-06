using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Demo 1 — physics melee. The staff is weak; the boot, the pits, the fire
    // and the barrels are strong. Brainwash turns monsters into projectiles.
    public sealed class PitArenaDemo : DemoScenario
    {
        public override string Id => "pit_arena";
        public override string Title => "Pit Fighter";
        public override string Tagline => "Melee + physics: every pit, flame and barrel is a weapon.";
        public override string Description => "A ruined gladiator pit with two spike pits, a fire trench and an open edge over the abyss. Your staff barely scratches. Your boot launches light bodies — and a brainwashed Ogre is a wrecking ball.";
        public override string[] Mechanics => new[]
        {
            "Q kicks: light monsters fly, heavy ones barely budge",
            "Bodies and props deal impact damage and knock others down",
            "Spikes, fire, explosive barrels and the abyss edge",
            "Environmental kills score ×3 across three waves"
        };
        public override string Objective => wave <= 3
            ? "Survive three waves. Kick, throw and command monsters into the environment. Wave " + Mathf.Min(wave, 3) + "/3 · " + CountAlive() + " left"
            : "Arena cleared!";
        public override string Intro => "Pit Fighter: kick (Q) light monsters into the spikes, fire or the abyss. G grabs barrels and crates.";
        public override string WinText => "The pit is yours.";
        public override string WinSummary => "Arena cleared in " + Clock(Elapsed) + ". Score " + score + " · environmental kills " + environmentKills + "/" + kills + (bestCombo > 1 ? " · best multi-kill ×" + bestCombo : "") + ".";
        public override string LoseText => "You were overwhelmed in wave " + wave + ". Score " + score + ". Use the pits: a kick is worth more than ten staff swings.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -9);
        public override float NavHalfExtent => 14;
        public override Color Ambient => new Color(.36f, .33f, .32f);
        public override float SunIntensity => 1.1f;
        public override float PlayerWeaponDamage => 10;
        public override float PlayerDamageScale => .7f;
        public override float PlayerRegen => 1;
        public override string[] Presets => new[] { "Attack the goblin", "Carry the crate to the imp", "Jump into the pit", "Use your ultimate", "Carry the goblin to the pit", "Run forward" };

        int wave, kills, environmentKills, score, combo, bestCombo;
        float waveAt, comboUntil;
        readonly List<Vector3> pits = new List<Vector3>();

        public override void Build()
        {
            wave = 0; kills = environmentKills = score = combo = bestCombo = 0; waveAt = -1;
            Color stone = new Color(.33f, .3f, .28f), sand = new Color(.52f, .45f, .34f), wall = new Color(.4f, .36f, .33f);
            pits.Clear(); pits.Add(new Vector3(-5, 0, 2)); pits.Add(new Vector3(5, 0, -2.5f));
            var holes = new List<Rect>();
            foreach (var pit in pits) holes.Add(new Rect(pit.x - 1.6f, pit.z - 1.6f, 3.2f, 3.2f));
            World.FloorWithHoles(new Rect(-13, -13, 26, 26), holes, 0, sand);
            foreach (var pit in pits) World.SpikePit(pit, new Vector2(3.2f, 3.2f), 2.2f);
            // Low walls except the east gap that drops into the abyss.
            // Walls too tall to jump; the east gap stays open over the abyss.
            WallAlongX(-13, -13, 13, 2.2f, wall);
            WallAlongX(13, -13, 13, 2.6f, wall);
            WallAlongZ(-13, -13, 13, 2.2f, wall);
            WallAlongZ(13, -13, 13, 2.2f, wall, new Vector2(-6, 6));
            World.Decor("Abyss lip", PrimitiveType.Cube, new Vector3(13.2f, -.6f, 0), new Vector3(.5f, 1, 12), new Color(.15f, .13f, .12f));
            var abyss = World.SpawnObject(World.NextHandle("o_edge"), "void", "abyss edge", new Vector3(15, -.2f, 0), new Vector3(3, .1f, 12), new Color(.05f, .05f, .07f), false, false);
            abyss.Note = "hazard";
            World.AddHazard(HazardKind.Void, new Bounds(new Vector3(17, -4, 0), new Vector3(8, 8, 14)), 0, "abyss");
            // Fire trench along the north wall.
            World.FireZone(new Vector3(0, 0, 10.6f), new Vector3(11, .2f, 1.6f), 20, "fire trench");
            World.Decor("Trench lip", PrimitiveType.Cube, new Vector3(0, .05f, 9.6f), new Vector3(11.4f, .1f, .25f), new Color(.2f, .12f, .08f));
            // Pillars for impacts.
            foreach (var p in new[] { new Vector3(-9, 0, -8), new Vector3(9, 0, -8), new Vector3(-9, 0, 7), new Vector3(9, 0, 7) })
                Box("Pillar", p + Vector3.up * 1.5f, new Vector3(1, 3, 1), stone);
            // Physics props.
            Color barrel = new Color(.62f, .16f, .1f), crate = new Color(.55f, .4f, .22f);
            foreach (var p in new[] { new Vector3(-2, .5f, 6), new Vector3(2.5f, .5f, 6.5f), new Vector3(-10, .5f, -3), new Vector3(10, .5f, 4) })
                World.SpawnProp(World.NextHandle("o_barrel"), "barrel", "explosive barrel", p, new Vector3(.7f, 1, .7f), barrel, 18, 25);
            foreach (var p in new[] { new Vector3(-3, .45f, -6), new Vector3(4, .45f, -7), new Vector3(-10, .45f, 9), new Vector3(10.5f, .45f, -9), new Vector3(1, .45f, 2.5f) })
                World.SpawnProp(World.NextHandle("o_crate"), "crate", "wooden crate", p, new Vector3(.9f, .9f, .9f), crate, 22, 45);
            for (int i = 0; i < 3; i++)
                World.SpawnItem(World.NextHandle("i_rock"), "rock", "rock", new Vector3(-1.5f + i * 1.5f, .2f, -7), new Color(.5f, .5f, .52f), true, 4);
            Torch(new Vector3(-12.6f, 2.2f, 0)); Torch(new Vector3(0, 3.2f, 12.6f)); Torch(new Vector3(-12.6f, 2.2f, -10)); Torch(new Vector3(12.6f, 2.2f, 10));
            SpawnWave(1);
        }

        void SpawnWave(int number)
        {
            wave = number;
            GameAudio.Play2D("wave_horn", .8f);
            Vector3 west = new Vector3(-11, 0, 11), east = new Vector3(11, 0, 11);
            if (number == 1)
            {
                Goblin(new Vector3(-6, 0, 7)); Goblin(new Vector3(0, 0, 7.5f)); Goblin(new Vector3(6, 0, 7));
                Spawn("bomb_toad", "bomb toad", new Vector3(-8, 0, 3));
            }
            else if (number == 2)
            {
                Goblin(west); Goblin(east + Vector3.left * 2);
                Spawn("imp", "red imp", east, "red"); Spawn("imp", "orange imp", west + Vector3.right * 2, "orange", new Color(1, .45f, .12f));
            }
            else
            {
                var ogre = Spawn("ogre", "ogre", new Vector3(0, 0, 8));
                ogre.Brain.Aggro = 1.6f;
                Goblin(west); Goblin(east); Spawn("bomb_toad", "bomb toad", east + Vector3.left * 3);
                Spawn("imp", "red imp", west + Vector3.right * 3, "red");
            }
            World.Report("WAVE " + number + "! " + (number == 3 ? "An Ogre joins the fight — it hates goblins too." : "Monsters pour into the pit.")
                + " They size you up for a moment: brainwash one now.");
            graceUntil = Time.time + (number == 1 ? 6 : 3);
        }

        float graceUntil = -1;

        SandboxMonster Goblin(Vector3 at) => Spawn("goblin", "green goblin", at, "green");
        SandboxMonster Spawn(string species, string name, Vector3 at, string color = null, Color? tint = null)
        {
            var monster = World.SpawnMonster(species, World.NextHandle("m_" + species), name, at, Vector3.back, color, tint);
            monster.Brain.Aggro = 2.2f;
            // Facing the player and holding still until the wave's grace period ends.
            monster.Brain.Passive = true;
            return monster;
        }

        public override void Tick(float dt)
        {
            if (graceUntil > 0 && Time.time >= graceUntil)
            {
                graceUntil = -1;
                foreach (var m in World.Monsters)
                    if (m != null && m.Entity.Alive && m.Brain != null && m.Brain.Passive) { m.Brain.Passive = false; m.Brain.Alert(World.Player.position); }
            }
            if (wave >= 1 && wave <= 3 && CountAlive() == 0)
            {
                // A short breather between waves.
                if (waveAt < 0) waveAt = Time.time + 3;
                else if (Time.time >= waveAt)
                {
                    waveAt = -1;
                    if (wave == 3) { wave = 4; World.Win(); }
                    else SpawnWave(wave + 1);
                }
            }
            if (combo > 0 && Time.time > comboUntil) combo = 0;
        }

        public override void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause)
        {
            if (victim.Kind != "monster") return;
            kills++;
            int points = cause == "environment" ? 300 : cause == "monster" ? 200 : 100;
            if (cause == "environment") environmentKills++;
            combo = Time.time < comboUntil ? combo + 1 : 1; comboUntil = Time.time + 2.5f;
            bestCombo = Mathf.Max(bestCombo, combo);
            points *= combo;
            score += points;
            string how = element == "spikes" ? "impaled" : element == "fall" ? "lost to the abyss" : element == "explosion" ? "blown up" : element == "fire" ? "burned" : element == "impact" ? "smashed" : cause == "monster" ? "killed by a monster" : "struck down";
            World.Report(victim.DisplayName + " " + how + "! +" + points + (combo > 1 ? " (multi-kill ×" + combo + ")" : ""));
            World.Effects.Popup(victim.Center + Vector3.up, "+" + points, cause == "environment" ? new Color(1, .85f, .2f) : Color.white);
        }

        public override IEnumerable<string> StatusLines()
        {
            yield return "SCORE " + score + "   kills " + kills + " (environmental " + environmentKills + ")";
            if (combo > 1) yield return "MULTI-KILL ×" + combo;
            yield return "Kick Q · Grab/throw G · Staff F · Jump Space";
        }
    }
}
