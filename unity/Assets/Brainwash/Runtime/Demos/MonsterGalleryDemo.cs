using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Not one of the five demos: a lineup of every monster model that cycles
    // through its motions and sounds (idle, walk, listening, thinking, strike,
    // cast, hit, dance, obeying). Opened from the menu ("Monster gallery") and
    // photographed by the demo smoke.
    public sealed class MonsterGalleryDemo : DemoScenario
    {
        public override string Id => "gallery";
        public override string Title => "Monster Gallery";
        public override string Tagline => "Every monster, its motion and its sounds.";
        public override string Description => "A lineup of the demo monsters. They cycle through their motions and sounds every few seconds; nobody attacks you.";
        public override string Objective => "Showing: " + Showcase[step % Showcase.Length] + "  (next in " + Mathf.CeilToInt(Mathf.Max(0, nextStep - Time.time)) + " s · Space skips)";
        public override string Intro => "Monster gallery: watch each motion; Space skips to the next.";
        public override Vector3 PlayerSpawn => new Vector3(0, 0, -7.5f);
        public override float NavHalfExtent => 16;
        public override Color Ambient => new Color(.42f, .42f, .48f);
        public override float SunIntensity => 1.2f;
        public override bool KickEnabled => false;
        public override string[] Presets => new[] { "Dance", "Wave your hand", "Attack the goblin" };

        public static readonly string[] Species = { "goblin", "imp", "ice_golem", "mouse", "ogre", "harpy", "mimic", "nurse", "gargoyle", "bomb_toad" };
        public static readonly string[] Showcase = { "idle", "walk", "listening", "thinking", "strike", "skill", "hit", "dance", "obeying" };
        public readonly List<SandboxMonster> Lineup = new List<SandboxMonster>();
        readonly List<Vector3> homes = new List<Vector3>();
        int step;
        float nextStep, stepStarted, nextCue;
        public string Current => Showcase[step % Showcase.Length];

        public override void Build()
        {
            Lineup.Clear(); homes.Clear(); step = 0; stepStarted = Time.time; nextStep = Time.time + StepSeconds(0);
            Color stone = new Color(.42f, .4f, .4f), wall = new Color(.38f, .36f, .36f);
            Floor(-17, 17, -12, 8, stone);
            WallAlongX(7.5f, -17, 17, 4, wall);
            WallAlongZ(-17, -12, 8, 4, wall);
            WallAlongZ(17, -12, 8, 4, wall);
            Torch(new Vector3(-8, 3, 7.2f)); Torch(new Vector3(0, 3, 7.2f)); Torch(new Vector3(8, 3, 7.2f));
            for (int i = 0; i < Species.Length; i++)
            {
                float x = -13.5f + i * 3f;
                var home = new Vector3(x, 0, i % 2 == 0 ? 1.5f : 3.5f);
                var monster = World.SpawnMonster(Species[i], "m_gallery_" + i, SpeciesCatalog.Get(Species[i]).Title, home, Vector3.back);
                monster.Brain.Passive = true;
                Lineup.Add(monster); homes.Add(home);
            }
        }

        static string Skill(string species)
        {
            switch (species)
            {
                case "imp": case "ice_golem": return "cast";
                case "ogre": return "slam";
                case "bomb_toad": return "swell";
                case "nurse": return "heal";
                case "harpy": case "goblin": return "throw";
                case "mimic": return "bite";
                default: return "strike";
            }
        }

        static float StepSeconds(int s) => Showcase[s % Showcase.Length] == "walk" ? 5 : 4;

        public void Next()
        {
            step++; stepStarted = Time.time; nextStep = Time.time + StepSeconds(step); nextCue = 0;
            foreach (var m in Lineup) if (m != null && m.Entity.Alive && m.Entity.Health < m.Entity.MaximumHealth * .5f) m.Entity.RestoreHealth(m.Entity.MaximumHealth);
        }

        public override void Tick(float dt)
        {
            if (Time.time >= nextStep) Next();
            if (Input.GetKeyDown(KeyCode.Space)) Next();
            string show = Current;
            float since = Time.time - stepStarted;
            bool cue = Time.time >= nextCue;
            if (cue) nextCue = Time.time + 1.6f;
            for (int i = 0; i < Lineup.Count; i++)
            {
                var m = Lineup[i];
                if (m == null || !m.Entity.Alive) continue;
                Vector3 home = homes[i];
                switch (show)
                {
                    case "walk":
                        // Pace left and right of home.
                        Vector3 goal = home + Vector3.right * (Mathf.Sin(since * .9f + i) > 0 ? 1.1f : -1.1f);
                        float speed = m.Entity.Profile != null ? Mathf.Min(m.Entity.Profile.Speed, 2.6f) : 2;
                        m.MoveToward(goal, speed, dt, .05f, out _);
                        break;
                    case "listening": m.SetPose("listening"); Face(m, World.Player.position, dt); break;
                    case "thinking": m.SetPose("thinking"); break;
                    case "strike": m.SetPose("attacking"); if (cue) m.Cue(m.Entity.Species == "mimic" ? "bite" : "strike"); break;
                    case "skill": m.SetPose("idle"); if (cue) m.Cue(Skill(m.Entity.Species)); break;
                    case "hit": m.SetPose("idle"); if (cue) m.Entity.TakeDamage(1, null, "physical"); break;
                    case "dance": m.SetPose("dance"); break;
                    case "obeying": m.SetPose("brainwashed"); break;
                    default: m.SetPose("idle"); Face(m, World.Player.position, dt); break;
                }
                if (show != "walk" && Vector3.ProjectOnPlane(m.transform.position - home, Vector3.up).magnitude > .3f) m.MoveToward(home, 1.5f, dt, .05f, out _);
            }
        }

        static void Face(SandboxMonster m, Vector3 point, float dt)
        {
            Vector3 toward = Vector3.ProjectOnPlane(point - m.transform.position, Vector3.up);
            if (toward.sqrMagnitude > .01f) m.transform.rotation = Quaternion.RotateTowards(m.transform.rotation, Quaternion.LookRotation(toward), 240 * dt);
        }

        public override IEnumerable<string> StatusLines()
        {
            yield return "Motions: " + string.Join(" · ", Showcase);
            yield return "N mutes or unmutes the sound.";
        }
    }
}
