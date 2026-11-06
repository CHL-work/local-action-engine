using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // One playable demo. The world owns entities, physics and the brainwash
    // lease; a scenario only builds its level, sets goals and reacts to events.
    public abstract class DemoScenario
    {
        protected SandboxWorld World { get; private set; }
        protected SandboxEntity Player => World.PlayerEntity;
        public abstract string Id { get; }
        public abstract string Title { get; }
        public abstract string Tagline { get; }
        public abstract string Description { get; }
        public virtual string[] Mechanics => Array.Empty<string>();
        public virtual string Objective => "";
        public virtual string Intro => Title + " started.";
        public virtual string WinText => "Victory!";
        public virtual string LoseText => "You were defeated.";
        public virtual string WinSummary => WinText;
        public virtual float ControlSeconds => 5;
        public virtual float StartMP => 30;
        public virtual Vector3 NavCenter => Vector3.zero;
        public virtual float NavHalfExtent => 14;
        public virtual Color Ambient => new Color(.42f, .45f, .52f);
        public virtual float SunIntensity => 1.25f;
        public virtual float FogDensity => 0;
        public virtual Color FogColor => new Color(.05f, .06f, .08f);
        public virtual float PlayerWeaponDamage => 12;
        // Demo balance: share of incoming damage the player takes, and HP per
        // second recovered after four seconds without being hit.
        public virtual float PlayerDamageScale => 1;
        public virtual float PlayerRegen => 0;
        public virtual bool KickEnabled => true;
        public virtual Vector3 PlayerSpawn => new Vector3(0, 0, -8);
        public virtual float PlayerYaw => 0;
        public virtual string[] Presets => Array.Empty<string>();
        public virtual string Controls => "WASD move · Shift run · Space jump · hold RMB or J/L/I/K look\nF / LMB+RMB staff · Q kick · G grab/throw · E use\nHold B speak to brainwash · M menu · R restart";
        public virtual bool ChantEnabled => false;
        public virtual string ChantLine => null;
        public virtual float ChantProgress => 0;
        public float StartedAt { get; private set; }
        float finishedAt = -1;
        public float Elapsed => (finishedAt >= 0 ? finishedAt : Time.time) - StartedAt;

        internal void Attach(SandboxWorld world) { World = world; }
        internal void MarkStarted() { StartedAt = Time.time; finishedAt = -1; }
        internal void MarkFinished() { if (finishedAt < 0) finishedAt = Time.time; }

        public abstract void Build();
        public virtual void Tick(float dt) { }
        public virtual IEnumerable<string> StatusLines() { yield break; }

        // Extra eye-contact rule beyond species rules. Return a reason to block.
        public virtual string EyeVeto(SandboxMonster monster) => null;
        public virtual bool TriggerReady(SandboxMonster monster) => true;
        public virtual void OnBrainwashed(SandboxMonster monster, JObject compiled) { }
        public virtual void OnControlEnded(SandboxMonster monster) { }
        public virtual float AdjustDamage(SandboxEntity victim, float damage, SandboxEntity attacker, string element) => damage;
        public virtual void OnDamaged(SandboxEntity victim, float amount, SandboxEntity attacker, string element) { }
        public virtual void OnKilled(SandboxEntity victim, SandboxEntity attacker, string element, string cause) { }
        public virtual void OnKnocked(SandboxMonster monster, Vector3 velocity, SandboxEntity thrower) { }
        public virtual void OnRecovered(SandboxMonster monster) { }
        public virtual void OnImpact(SandboxEntity body, SandboxEntity other, float speed) { }
        public virtual void OnNoise(Vector3 point, float radius) { }
        public virtual void OnPlayerThrew(SandboxEntity prop) { }
        public virtual bool OnInteract() => false;
        public virtual void OnMimicOpened(SandboxMonster mimic) { }
        public virtual void OnPlayerPickedUp(SandboxEntity item) { }
        public virtual void OnEaten(SandboxMonster eater, SandboxEntity item) { }
        public virtual void OnRevived(SandboxEntity remains, SandboxMonster nurse) { }
        public virtual void OnInteracted(SandboxEntity target, string operation, SandboxMonster actor) { }
        public virtual void OnBurned(SandboxEntity entity) { }
        public virtual void OnChant(string transcript, bool typed) { }
        public virtual void OnCommandText(string transcript) { }
        public virtual void OnTriggered(string trigger, SandboxMonster actor) { }
        public virtual void OnCarried(SandboxMonster carrier, SandboxEntity cargo, bool thrown) { }
        public virtual void OnItemFreed(SandboxEntity item, string how, SandboxEntity from) { }
        public virtual void OnLost(SandboxEntity entity) { }

        // Mimic "hidden gift": by default it spits out whatever it swallowed.
        public virtual bool OnHiddenGift(SandboxMonster mimic)
        {
            var item = World.TakeSwallowed(mimic.Entity);
            if (item == null) { World.Report("The Mimic opens wide... it has nothing left to give."); return false; }
            Vector3 toward = World.Player.position + Vector3.ProjectOnPlane(mimic.transform.position - World.Player.position, Vector3.up).normalized * 1.1f;
            World.TossItem(item, mimic.Entity.Center + Vector3.up * .4f, toward);
            World.Report("The Mimic coughs up the " + item.DisplayName + "!");
            OnItemFreed(item, "hidden_gift", mimic.Entity);
            return true;
        }

        // ---------- Level-building helpers ----------
        protected GameObject Box(string name, Vector3 center, Vector3 size, Color color) => World.Solid(name, center, size, color);
        protected void Floor(float x0, float x1, float z0, float z1, Color color, float top = 0)
            => Box("Floor", new Vector3((x0 + x1) * .5f, top - .15f, (z0 + z1) * .5f), new Vector3(x1 - x0, .3f, z1 - z0), color);
        // A wall running along X at a fixed Z, with optional door gaps (x ranges).
        protected void WallAlongX(float z, float x0, float x1, float height, Color color, params Vector2[] gaps)
        {
            var cuts = new List<Vector2>(gaps); cuts.Sort((a, b) => a.x.CompareTo(b.x));
            float start = x0;
            foreach (var gap in cuts) { if (gap.x > start) Box("Wall", new Vector3((start + gap.x) * .5f, height * .5f, z), new Vector3(gap.x - start, height, .4f), color); start = Mathf.Max(start, gap.y); }
            if (x1 > start) Box("Wall", new Vector3((start + x1) * .5f, height * .5f, z), new Vector3(x1 - start, height, .4f), color);
        }
        protected void WallAlongZ(float x, float z0, float z1, float height, Color color, params Vector2[] gaps)
        {
            var cuts = new List<Vector2>(gaps); cuts.Sort((a, b) => a.x.CompareTo(b.x));
            float start = z0;
            foreach (var gap in cuts) { if (gap.x > start) Box("Wall", new Vector3(x, height * .5f, (start + gap.x) * .5f), new Vector3(.4f, height, gap.x - start), color); start = Mathf.Max(start, gap.y); }
            if (z1 > start) Box("Wall", new Vector3(x, height * .5f, (start + z1) * .5f), new Vector3(.4f, height, z1 - start), color);
        }
        protected void Torch(Vector3 position, float range = 9, float intensity = 1.6f)
        {
            World.PointLight(position, new Color(1, .7f, .4f), range, intensity);
            World.Decor("Torch", PrimitiveType.Cylinder, position + Vector3.down * .35f, new Vector3(.08f, .3f, .08f), new Color(.3f, .2f, .1f));
            World.Effects.Particles("Torch flame", World.RoomRoot, position, new Vector3(.08f, .08f, .05f), new Color(1, .85f, .3f), new Color(1, .35f, .05f), 18, .7f, .22f, .5f);
        }
        protected SandboxEntity Ledge(string handle, string name, Vector3 topCenter, Vector2 size, Color color)
        {
            Box(name + " body", new Vector3(topCenter.x, topCenter.y * .5f, topCenter.z), new Vector3(size.x, topCenter.y, size.y), color);
            var ledge = World.SpawnObject(handle, "ledge", name, topCenter + Vector3.up * .03f, new Vector3(size.x - .2f, .06f, size.y - .2f), color, false, false);
            return ledge;
        }
        // A walkable ramp from a low point up to a high point (width across).
        protected void Ramp(Vector3 low, Vector3 high, float width, Color color)
        {
            Vector3 delta = high - low; float length = delta.magnitude;
            var ramp = Box("Ramp", (low + high) * .5f + Vector3.down * .12f, new Vector3(width, .25f, length), color);
            ramp.transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        }

        // Helpers shared by the concrete demos.
        protected static string Clock(float seconds) => Mathf.FloorToInt(seconds / 60) + ":" + Mathf.FloorToInt(seconds % 60).ToString("00");
        protected int CountAlive(Func<SandboxMonster, bool> filter = null)
        {
            int count = 0;
            foreach (var monster in World.Monsters) if (monster != null && monster.Entity.Alive && (filter == null || filter(monster))) count++;
            return count;
        }
        protected bool PlayerNear(Vector3 point, float radius) => Vector3.ProjectOnPlane(World.Player.position - point, Vector3.up).magnitude <= radius;

        public static readonly Func<DemoScenario>[] Catalogue =
        {
            () => new PitArenaDemo(),
            () => new PuppetWarDemo(),
            () => new LedgeKeyDemo(),
            () => new RiteOfReturnDemo(),
            () => new EyeHeistDemo()
        };
        public static DemoScenario Create(string id)
        {
            if (id == "gallery") return new MonsterGalleryDemo();
            foreach (var factory in Catalogue) { var demo = factory(); if (demo.Id == id) return demo; }
            return null;
        }
    }
}
