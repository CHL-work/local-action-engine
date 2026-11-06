using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Brainwash
{
    // Demo 4 — extraction under siege. Chanting scripture fills the Rite of
    // Return; brainwashing uses the same mouth. Word matching against the
    // displayed scripture is the one place code compares speech to text.
    public sealed class RiteOfReturnDemo : DemoScenario
    {
        public override string Id => "rite_of_return";
        public override string Title => "Rite of Return";
        public override string Tagline => "Chant to escape while monsters break in. You have one mouth.";
        public override string Description => "Extraction under siege. Stand in the circle and read the scripture aloud (hold C) to fill the Rite. Every second spent brainwashing is a second not spent chanting. Turn the Frost Golem into a bodyguard, make the Plague Nurse heal you, and revive the fallen cultist so she chants with you.";
        public override string[] Mechanics => new[]
        {
            "Hold C and read the scripture line; matched words fill the Rite",
            "One mouth: you cannot chant and brainwash at the same time",
            "Getting hit breaks a chant in progress",
            "Golem guards, Nurse heals or revives, Harpies snatch chanters"
        };
        public override bool ChantEnabled => true;
        public override string ChantLine => Lines[line % Lines.Length];
        public override float ChantProgress => progress;
        public override string Objective => progress >= 100 ? "The Rite is complete!" : "Stand in the circle and chant the scripture (hold C). Rite " + Mathf.FloorToInt(progress) + "%";
        public override string Intro => "Rite of Return: hold C and read the scripture line aloud while standing in the circle. Monsters are coming.";
        public override string WinText => "The circle flares white. You return to the church.";
        public override string WinSummary => "Returned in " + Clock(Elapsed) + " after " + chants + " chants (" + Mathf.RoundToInt(bestAccuracy * 100) + "% best accuracy). Cultist chanting: " + Mathf.RoundToInt(cultistContribution) + "% · waves survived: " + wave + ".";
        public override string LoseText => "Your voice fell silent before the Rite was complete.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -2.5f);
        public override float NavHalfExtent => 19;
        public override Color Ambient => new Color(.32f, .3f, .4f);
        public override float SunIntensity => .7f;
        public override float FogDensity => .012f;
        public override Color FogColor => new Color(.07f, .06f, .1f);
        public override float PlayerDamageScale => .4f;
        public override float PlayerRegen => 1.5f;
        public override string Controls => "Hold C chant · Hold B brainwash (not both)\nWASD move · Space jump · hold RMB or J/L/I/K look\nF staff · Q kick · G grab/throw · E use · M menu · R restart";
        public override string[] Presets => new[] { "Guard me", "Attack the goblin", "Heal me", "Revive the red cultist", "Make an acid pool in front of you", "Stop" };

        static readonly string[] Lines =
        {
            "By the eye of the faithful I return to the light",
            "Let the gate of bone open for the servant of the church",
            "Carry me home through ash and silence",
            "I give my voice to the circle and the circle gives me passage",
            "No beast may follow where the faithful walk"
        };
        static readonly Vector3 Center = Vector3.zero;
        const float Radius = 2.7f;
        float progress, nextWave, bestAccuracy, cultistContribution;
        int line, wave, chants;
        SandboxEntity cultist, circle;
        Light circleLight;
        readonly Vector3[] gates = { new Vector3(0, 0, 16.5f), new Vector3(16.5f, 0, 0), new Vector3(-16.5f, 0, 0) };

        bool InCircle(Vector3 point) => Vector3.ProjectOnPlane(point - Center, Vector3.up).magnitude <= Radius;

        public override void Build()
        {
            progress = 0; bestAccuracy = 0; cultistContribution = 0; line = 0; wave = 0; chants = 0; nextWave = Time.time + 18;
            Color stone = new Color(.3f, .29f, .33f), wall = new Color(.36f, .34f, .4f), outside = new Color(.22f, .22f, .25f);
            Floor(-14, 14, -14, 14, stone);
            Floor(-3, 3, 14, 19, outside); Floor(14, 19, -3, 3, outside); Floor(-19, -14, -3, 3, outside);
            WallAlongX(-14, -14, 14, 4, wall);
            WallAlongX(14, -14, 14, 4, wall, new Vector2(-1.6f, 1.6f));
            WallAlongZ(-14, -14, 14, 4, wall, new Vector2(-1.6f, 1.6f));
            WallAlongZ(14, -14, 14, 4, wall, new Vector2(-1.6f, 1.6f));
            WallAlongZ(-3, 14, 19, 3, outside); WallAlongZ(3, 14, 19, 3, outside); WallAlongX(19, -3, 3, 3, outside);
            WallAlongX(-3, 14, 19, 3, outside); WallAlongX(3, 14, 19, 3, outside); WallAlongZ(19, -3, 3, 3, outside);
            WallAlongX(-3, -19, -14, 3, outside); WallAlongX(3, -19, -14, 3, outside); WallAlongZ(-19, -3, 3, 3, outside);
            // Pews and broken columns for cover.
            foreach (float x in new[] { -8f, -5f, 5f, 8f })
                foreach (float z in new[] { -9f, -6f })
                    Box("Pew", new Vector3(x, .45f, z), new Vector3(2.2f, .9f, .5f), new Color(.35f, .24f, .15f));
            foreach (var p in new[] { new Vector3(-7, 0, 6), new Vector3(7, 0, 6), new Vector3(-7, 0, -2), new Vector3(7, 0, -2) })
                Box("Column", p + Vector3.up * 2, new Vector3(1, 4, 1), new Color(.45f, .43f, .48f));
            circle = World.SpawnObject("o_circle", "circle", "rite circle", Center + Vector3.up * .02f, new Vector3(Radius * 2, .04f, Radius * 2), new Color(.55f, .5f, .8f), false, false);
            circleLight = World.PointLight(Center + Vector3.up * 1.2f, new Color(.6f, .55f, 1), 6, .6f);
            cultist = World.SpawnRemains("r_cultist", "fallen red cultist", Center + new Vector3(1.5f, 0, 1), "red", new Color(.7f, .1f, .1f));
            // Neutral tools near the circle.
            var golem = World.SpawnMonster("ice_golem", "m_golem", "big blue ice golem", new Vector3(0, 0, 8), Vector3.back, "blue");
            golem.Brain.Aggro = 0; golem.Brain.GuardPost = true;
            var nurse = World.SpawnMonster("nurse", "m_nurse", "Plague Nurse", new Vector3(-4.5f, 0, 3.5f), Vector3.right, "black");
            nurse.Brain.Aggro = 0;
            World.SpawnProp(World.NextHandle("o_barrel"), "barrel", "explosive barrel", new Vector3(10, .5f, 10), new Vector3(.7f, 1, .7f), new Color(.62f, .16f, .1f), 18, 25);
            World.SpawnProp(World.NextHandle("o_barrel"), "barrel", "explosive barrel", new Vector3(-10, .5f, 10), new Vector3(.7f, 1, .7f), new Color(.62f, .16f, .1f), 18, 25);
            for (int i = 0; i < 3; i++) World.SpawnProp(World.NextHandle("o_crate"), "crate", "wooden crate", new Vector3(-4 + i * 4, .45f, -11), new Vector3(.9f, .9f, .9f), new Color(.55f, .4f, .22f), 22, 45);
            Torch(new Vector3(-13.6f, 2.6f, 8)); Torch(new Vector3(13.6f, 2.6f, 8)); Torch(new Vector3(-13.6f, 2.6f, -8)); Torch(new Vector3(13.6f, 2.6f, -8));
        }

        SandboxMonster Invader(string species, string name, Vector3 at, string color = null, Color? tint = null)
        {
            var monster = World.SpawnMonster(species, World.NextHandle("m_" + species), name, at + new Vector3(UnityEngine.Random.Range(-1f, 1f), 0, UnityEngine.Random.Range(-1f, 1f)), Center - at, color, tint);
            monster.Brain.Aggro = 3; monster.Brain.Alert(World.Player.position);
            return monster;
        }

        void SpawnWave()
        {
            wave++;
            Vector3 north = gates[0], east = gates[1], west = gates[2];
            switch (wave)
            {
                case 1: Invader("goblin", "green goblin", north, "green"); break;
                case 2: Invader("bomb_toad", "bomb toad", east); Invader("goblin", "green goblin", west, "green"); break;
                case 3: var harpy = Invader("harpy", "harpy", north, "black"); harpy.Brain.Snatcher = true; Invader("imp", "red imp", east, "red"); break;
                case 4: Invader("goblin", "green goblin", west, "green"); Invader("goblin", "scarred goblin", west, "green"); Invader("bomb_toad", "bomb toad", north); Invader("imp", "orange imp", north, "orange", new Color(1, .45f, .12f)); break;
                default:
                    var gate = gates[UnityEngine.Random.Range(0, gates.Length)];
                    Invader("goblin", "green goblin", gate, "green"); Invader("goblin", "big goblin", gate, "green");
                    if (wave % 2 == 0) Invader("bomb_toad", "bomb toad", gates[UnityEngine.Random.Range(0, gates.Length)]);
                    else Invader("imp", "red imp", gates[UnityEngine.Random.Range(0, gates.Length)], "red");
                    break;
            }
            World.Report("Wave " + wave + " breaks into the chapel!");
            GameAudio.Play2D("wave_horn", .8f);
        }

        public override void Tick(float dt)
        {
            if (Time.time >= nextWave)
            {
                if (CountAlive(m => m.Brain != null && m.Brain.Aggro >= 3) < 9) SpawnWave();
                nextWave = Time.time + (wave < 4 ? 26 : 28);
            }
            if (cultist != null && cultist.Alive && InCircle(cultist.transform.position) && progress < 100)
            {
                float gain = 1.2f * dt; progress += gain; cultistContribution += gain;
                if (progress >= 100) Complete();
            }
            if (circleLight != null) circleLight.intensity = .6f + progress / 100 * 4.5f + Mathf.Sin(Time.time * 3) * .15f;
        }

        public override void OnChant(string transcript, bool typed)
        {
            if (progress >= 100) return;
            if (!InCircle(World.Player.position)) { World.Report("You must stand inside the circle for the Rite to hear you."); return; }
            float accuracy = Match(ChantLine, transcript);
            chants++; bestAccuracy = Mathf.Max(bestAccuracy, accuracy);
            World.MakeNoise(World.Player.position, 12);
            if (accuracy < .5f)
            {
                progress += accuracy * 6;
                World.Report("The Rite falters (" + Mathf.RoundToInt(accuracy * 100) + "% of the words). Read the line exactly.");
                if (progress >= 100) Complete();
                return;
            }
            float gain = 22 * accuracy;
            progress += gain; line++;
            GameAudio.Play2D("rite_progress");
            World.Effects.Pulse(Center + Vector3.up * .3f, new Color(.7f, .65f, 1), Radius);
            World.Report("The circle answers (+" + Mathf.RoundToInt(gain) + "%, " + Mathf.RoundToInt(accuracy * 100) + "% of the words). Next line!");
            if (progress >= 100) Complete();
        }

        void Complete()
        {
            progress = 100;
            World.Effects.Flash(Center + Vector3.up, new Color(.85f, .8f, 1), 6);
            GameAudio.Play2D("rite_complete");
            World.Win();
        }

        // Ordered word overlap (longest common subsequence) between the scripture and what was heard.
        static float Match(string expected, string heard)
        {
            var a = Words(expected); var b = Words(heard);
            if (a.Count == 0 || b.Count == 0) return 0;
            var table = new int[a.Count + 1, b.Count + 1];
            for (int i = 1; i <= a.Count; i++)
                for (int j = 1; j <= b.Count; j++)
                    table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            return table[a.Count, b.Count] / (float)a.Count;
        }

        static List<string> Words(string text)
        {
            var words = new List<string>(); var current = new StringBuilder();
            foreach (char c in (text ?? "").ToLowerInvariant())
            {
                if (char.IsLetter(c) || c == '\'') current.Append(c);
                else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            if (current.Length > 0) words.Add(current.ToString());
            return words;
        }

        public override void OnDamaged(SandboxEntity victim, float amount, SandboxEntity attacker, string element)
        {
            if (victim == Player && World.App != null && World.App.Chanting) World.App.InterruptChant("Your chant was broken by the hit!");
        }

        public override void OnRevived(SandboxEntity remains, SandboxMonster nurse)
        {
            if (remains != cultist) return;
            cultist.Revive(60);
            cultist.DisplayName = "revived red cultist";
            DemoVisuals.SetRemainsStanding(cultist, true);
            World.Report("The red cultist rises and joins the chant (+1.2% per second while she stands in the circle).");
        }

        public override void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause)
        {
            if (victim == cultist)
            {
                cultist.DisplayName = "fallen red cultist";
                DemoVisuals.SetRemainsStanding(cultist, false);
                World.Report("The cultist falls silent again. A Plague Nurse could revive her.");
            }
        }

        public override IEnumerable<string> StatusLines()
        {
            int filled = Mathf.FloorToInt(progress / 5);
            yield return "RITE [" + new string('|', filled) + new string('.', 20 - filled) + "] " + Mathf.FloorToInt(progress) + "%";
            yield return "Wave " + wave + " · invaders " + CountAlive(m => m.Brain != null && m.Brain.Aggro >= 3) + " · next in " + Mathf.Max(0, Mathf.CeilToInt(nextWave - Time.time)) + " s";
            yield return "Cultist: " + (cultist != null && cultist.Alive ? "chanting with you" : "fallen (a Nurse can revive her)") + (InCircle(World.Player.position) ? " · you are IN the circle" : " · you are outside the circle");
        }
    }
}
