using System;
using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // How a species allows or resists eye contact (concept §9, monsters §2).
    public enum EyeRule { Normal, Shy, Helmeted, Sleeper, Gargoyle, Eyeless }

    // Monsters §2: aggressive mutated animals attack anything except their own
    // kind; bioweapons proactively attack only believers and humans.
    public enum Faction { Mutant, Bioweapon, Machine }

    // Gameplay facts for one species. Classifier-facing values (species id,
    // skills, capabilities and trigger) use the protocol-3 catalogue names.
    public sealed class SpeciesProfile
    {
        public string Id;
        public string Title;
        public float Width = .7f, Height = 1.8f, Mass = 50, Health = 80, Speed = 2.2f, Reach = 1f, Damage = 8, AttackCooldown = 1.6f;
        public float SightRange = 9;
        public string[] Skills = Array.Empty<string>();
        public bool CanFly, CanCarry, CanEat, CanCallAllies;
        public string Trigger;
        public EyeRule Eyes = EyeRule.Normal;
        public Faction Faction = Faction.Bioweapon;
        public bool Aggressive, Timid;
        public Color Color = Color.gray;
        readonly Dictionary<string, float> weakness = new Dictionary<string, float>();

        public SpeciesProfile Weak(string element, float multiplier) { weakness[element] = multiplier; return this; }
        public float Multiplier(string element) => element != null && weakness.TryGetValue(element, out var value) ? value : 1;
        public bool Has(string skill) => Array.IndexOf(Skills, skill) >= 0;
        public string WeaknessText
        {
            get
            {
                var parts = new List<string>();
                foreach (var pair in weakness) if (pair.Value > 1) parts.Add(pair.Key);
                return parts.Count == 0 ? "none known" : string.Join(", ", parts);
            }
        }
    }

    public static class SpeciesCatalog
    {
        static readonly Dictionary<string, SpeciesProfile> profiles = new Dictionary<string, SpeciesProfile>();

        static SpeciesCatalog()
        {
            Add(new SpeciesProfile { Id = "imp", Title = "Ember Imp", Width = .7f, Height = 1.8f, Mass = 40, Health = 70, Speed = 2.1f, Reach = .95f, Damage = 6, AttackCooldown = 1.8f,
                Skills = new[] { "fireball" }, Trigger = "dragon_delusion", Faction = Faction.Bioweapon, Color = new Color(.85f, .25f, .18f) }).Weak("ice", 2).Weak("water", 2);
            Add(new SpeciesProfile { Id = "goblin", Title = "Goblin Thief", Width = .7f, Height = 1.5f, Mass = 45, Health = 90, Speed = 2.5f, Reach = .95f, Damage = 8, AttackCooldown = 1.5f,
                CanCallAllies = true, Faction = Faction.Mutant, Color = new Color(.55f, .75f, .2f) }).Weak("electric", 2);
            Add(new SpeciesProfile { Id = "ice_golem", Title = "Frost Golem", Width = 1.1f, Height = 2.4f, Mass = 300, Health = 160, Speed = 1.4f, Reach = 1.3f, Damage = 14, AttackCooldown = 2.4f,
                Skills = new[] { "ice_bolt" }, Faction = Faction.Bioweapon, Color = new Color(.25f, .65f, .95f) }).Weak("fire", 2);
            Add(new SpeciesProfile { Id = "mouse", Title = "Cheese Mouse", Width = .55f, Height = .6f, Mass = 8, Health = 30, Speed = 3.6f, Reach = .6f, Damage = 3, AttackCooldown = 1.2f,
                Skills = new[] { "ultimate" }, CanEat = true, Trigger = "cheese_theft", Timid = true, Faction = Faction.Mutant, Color = new Color(.55f, .55f, .58f), SightRange = 7 }).Weak("fire", 2);
            Add(new SpeciesProfile { Id = "ogre", Title = "Ogre", Width = 1.3f, Height = 3f, Mass = 420, Health = 280, Speed = 1.9f, Reach = 1.7f, Damage = 22, AttackCooldown = 2.3f,
                Skills = new[] { "ultimate" }, CanCarry = true, Aggressive = true, Faction = Faction.Mutant, Color = new Color(.52f, .5f, .5f) }).Weak("acid", 1.5f);
            Add(new SpeciesProfile { Id = "harpy", Title = "Harpy", Width = 1.1f, Height = 1.5f, Mass = 22, Health = 60, Speed = 5f, Reach = 1.1f, Damage = 7, AttackCooldown = 1.4f,
                CanFly = true, CanCarry = true, Aggressive = true, Faction = Faction.Mutant, Color = new Color(.12f, .11f, .14f), SightRange = 12 }).Weak("electric", 2);
            Add(new SpeciesProfile { Id = "mimic", Title = "Mimic", Width = 1f, Height = .9f, Mass = 90, Health = 120, Speed = 1.3f, Reach = 1.1f, Damage = 18, AttackCooldown = 2,
                CanEat = true, Trigger = "hidden_gift", Eyes = EyeRule.Sleeper, Faction = Faction.Bioweapon, Color = new Color(.48f, .3f, .14f), SightRange = 4 });
            Add(new SpeciesProfile { Id = "nurse", Title = "Plague Nurse", Width = .75f, Height = 2.2f, Mass = 55, Health = 90, Speed = 2f, Reach = 1.1f, Damage = 6, AttackCooldown = 2,
                Skills = new[] { "heal", "revive", "acid_pool" }, Faction = Faction.Bioweapon, Color = new Color(.12f, .13f, .12f) }).Weak("fire", 2);
            Add(new SpeciesProfile { Id = "gargoyle", Title = "Gargoyle", Width = .9f, Height = 1.9f, Mass = 220, Health = 150, Speed = 5f, Reach = 1.2f, Damage = 16, AttackCooldown = 1.6f,
                Eyes = EyeRule.Gargoyle, Faction = Faction.Bioweapon, Color = new Color(.46f, .47f, .46f) });
            Add(new SpeciesProfile { Id = "bomb_toad", Title = "Bomb Toad", Width = .95f, Height = .85f, Mass = 60, Health = 40, Speed = 1.5f, Reach = 1.1f, Damage = 0, AttackCooldown = 3,
                Faction = Faction.Mutant, Eyes = EyeRule.Normal, Color = new Color(.36f, .55f, .22f), SightRange = 6 }).Weak("fire", 5);
        }

        static SpeciesProfile Add(SpeciesProfile profile) { profiles[profile.Id] = profile; return profile; }
        public static SpeciesProfile Get(string id) => id != null && profiles.TryGetValue(id, out var profile) ? profile : null;
        public static IEnumerable<SpeciesProfile> All => profiles.Values;
    }
}
