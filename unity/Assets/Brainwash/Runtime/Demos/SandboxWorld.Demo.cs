using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Demo-scenario half of the world. The classic test room and arena never
    // enter these paths: every hook checks Scenario first.
    public sealed partial class SandboxWorld
    {
        public DemoScenario Scenario { get; private set; }
        public SandboxBootstrap App;
        public Light Sun;
        public float ControlSeconds => Scenario != null ? Scenario.ControlSeconds : 5;
        public DemoEffects Effects { get; private set; }
        public SandboxMonster Carrier { get; private set; }
        public SandboxEntity Carried { get; private set; }
        public SandboxEntity PlayerProp { get; private set; }
        readonly Dictionary<SandboxEntity, float> hazardTicks = new Dictionary<SandboxEntity, float>();
        readonly Dictionary<SandboxEntity, float> burnTicks = new Dictionary<SandboxEntity, float>();
        readonly Dictionary<SandboxEntity, GameObject> flames = new Dictionary<SandboxEntity, GameObject>();
        readonly Dictionary<SandboxEntity, List<SandboxEntity>> swallowed = new Dictionary<SandboxEntity, List<SandboxEntity>>();
        readonly HashSet<SandboxEntity> exploding = new HashSet<SandboxEntity>();
        readonly List<GameObject> scenarioOnly = new List<GameObject>();
        // One ring of beads above every monster you are brainwashing.
        readonly Dictionary<SandboxMonster, GameObject> halos = new Dictionary<SandboxMonster, GameObject>();
        float nextKick, nextGrab;
        // A flight that was under way when control ended finishes on its own,
        // like a committed fireball; the carrier sets its passenger down.
        // Only flights a command started are finished this way; a Harpy's own
        // snatch is not.
        bool committedCarry, carryCommanded;
        float committedUntil;
        public Vector3 CarryGoal, CarryOrigin;
        public bool CarryCommitted => committedCarry;
        int spawnCounter;

        public void StartScenario(DemoScenario scenario)
        {
            Scenario = scenario; scenario.Attach(this);
            PlayMode = true; DebugCones = false; ResetSandbox();
        }

        void ResetDemoState()
        {
            hazardTicks.Clear(); burnTicks.Clear(); exploding.Clear(); swallowed.Clear();
            foreach (var flame in flames.Values) if (flame != null) Destroy(flame);
            flames.Clear();
            Carrier = null; Carried = null; PlayerProp = null; committedCarry = carryCommanded = false; CarryGoal = CarryOrigin = Vector3.zero;
            App?.ReleaseCarried(Vector3.zero);
            DemoPhysics.Hazards.Clear();
            foreach (var go in scenarioOnly) if (go != null) Destroy(go);
            scenarioOnly.Clear();
            foreach (var ring in halos.Values) if (ring != null) Destroy(ring);
            halos.Clear();
            if (Effects == null) Effects = new DemoEffects(this);
            Effects.Clear();
        }

        void BuildScenario()
        {
            var s = Scenario;
            SandboxNavigation.Configure(s.NavCenter, s.NavHalfExtent, true);
            RenderSettings.ambientLight = s.Ambient;
            RenderSettings.fog = s.FogDensity > 0; RenderSettings.fogColor = s.FogColor; RenderSettings.fogDensity = s.FogDensity; RenderSettings.fogMode = FogMode.ExponentialSquared;
            if (View != null) View.backgroundColor = s.FogDensity > 0 ? s.FogColor : new Color(.075f, .095f, .13f);
            if (Sun != null) Sun.intensity = s.SunIntensity;
            spawnCounter = 0;
            App?.Teleport(s.PlayerSpawn, s.PlayerYaw);
            s.Build();
            Monster = Monsters.Count > 0 ? Monsters[0] : null;
            MP = Mathf.Min(MaxMP, s.StartMP);
            s.MarkStarted();
            Physics.SyncTransforms();
            Report(s.Intro);
        }

        void RestoreClassicLighting()
        {
            RenderSettings.ambientLight = new Color(.55f, .6f, .68f); RenderSettings.fog = false;
            if (View != null) View.backgroundColor = new Color(.075f, .095f, .13f);
            if (Sun != null) Sun.intensity = 1.4f;
        }

        public void Win() => FinishRun(true);
        public void GainMP(float amount) { if (amount > 0) MP = Mathf.Min(MaxMP, MP + amount); }
        public void Lose() => FinishRun(false);

        // ---------- Spawning ----------
        public string NextHandle(string prefix) => prefix + (++spawnCounter);

        public SandboxMonster SpawnMonster(string species, string handle, string name, Vector3 position, Vector3 facing, string color = null, Color? tint = null)
        {
            var profile = SpeciesCatalog.Get(species) ?? throw new ArgumentException("Unknown species " + species);
            var root = new GameObject(name); root.transform.SetParent(RoomRoot, false); root.transform.position = position;
            var entity = root.AddComponent<SandboxEntity>();
            entity.Handle = handle; entity.Kind = "monster"; entity.Species = species; entity.DisplayName = name; entity.World = this; entity.Profile = profile;
            entity.ColorName = color ?? (species == "ice_golem" ? "blue" : species == "goblin" ? "green" : species == "imp" ? "red" : "grey");
            Color body = tint ?? profile.Color;
            if (species == "imp" || species == "goblin" || species == "ice_golem") SandboxVisuals.BuildCreature(entity, profile.Width, profile.Height, body, Material);
            else DemoVisuals.BuildCreature(entity, profile, body, Material);
            var actor = root.AddComponent<SandboxMonster>(); actor.Initialize(entity, profile.Width, profile.Height);
            actor.NavigationEnabled = true;
            if (profile.CanFly) { actor.Flying = true; position.y = Mathf.Max(position.y, SandboxMonster.GroundHeight(position, null) + actor.HoverHeight); }
            actor.Teleport(position, facing.sqrMagnitude > .01f ? facing : Vector3.back);
            actor.Brain = new DemoAI(actor, this);
            if (species == "bomb_toad") { entity.Explosive = true; entity.Flammable = true; }
            entity.RestoreHealth(profile.Health);
            Monsters.Add(actor); Entities.Add(entity); Label(entity, profile.Height + .5f);
            return actor;
        }

        public SandboxEntity SpawnItem(string handle, string kind, string name, Vector3 position, Color color, bool physics = false, float mass = 1)
        {
            var root = new GameObject(name); root.transform.SetParent(RoomRoot, false); root.transform.position = position;
            var entity = root.AddComponent<SandboxEntity>(); entity.Handle = handle; entity.Kind = "item"; entity.ItemKind = kind; entity.DisplayName = name; entity.World = this;
            var box = root.AddComponent<BoxCollider>();
            box.size = kind == "key" ? new Vector3(.66f, .1f, .32f) : kind == "relic" ? new Vector3(.36f, .42f, .36f) : kind == "cheese" ? new Vector3(.36f, .2f, .3f) : new Vector3(.24f, .14f, .24f);
            if (kind == "key") SandboxVisuals.BuildItem(entity, color, Material);
            else DemoVisuals.BuildItem(entity, kind, color, Material);
            if (physics) PhysicsProp.Make(entity, mass);
            Entities.Add(entity); Label(entity, .8f); return entity;
        }

        public SandboxEntity SpawnObject(string handle, string kind, string name, Vector3 position, Vector3 size, Color color, bool destructible = false, bool collider = true)
        {
            var go = new GameObject(name); go.transform.SetParent(RoomRoot, false); go.transform.position = position;
            var entity = go.AddComponent<SandboxEntity>(); entity.Handle = handle; entity.Kind = "object"; entity.ObjectKind = kind; entity.DisplayName = name; entity.World = this;
            entity.Destructible = destructible;
            if (collider) go.AddComponent<BoxCollider>().size = size;
            if (kind == "door" || kind == "lever" || kind == "crate" || kind == "rope") SandboxVisuals.BuildObject(entity, size, color, Material);
            else DemoVisuals.BuildObject(entity, kind, size, color, Material);
            Entities.Add(entity); Label(entity, .7f + size.y * .5f); return entity;
        }

        public SandboxEntity SpawnProp(string handle, string kind, string name, Vector3 position, Vector3 size, Color color, float mass, float health = 40)
        {
            var entity = SpawnObject(handle, kind, name, position, size, color, true);
            entity.RestoreHealth(health);
            PhysicsProp.Make(entity, mass);
            if (kind == "barrel") { entity.Explosive = true; entity.Flammable = true; }
            if (kind == "crate") entity.Flammable = true;
            return entity;
        }

        public SandboxEntity SpawnRemains(string handle, string name, Vector3 position, string color, Color tint)
        {
            var root = new GameObject(name); root.transform.SetParent(RoomRoot, false); root.transform.position = position;
            var entity = root.AddComponent<SandboxEntity>(); entity.Handle = handle; entity.Kind = "remains"; entity.DisplayName = name; entity.World = this; entity.ColorName = color;
            DemoVisuals.BuildRemains(entity, tint, Material);
            entity.RestoreHealth(100); entity.Health = 0;
            Entities.Add(entity); Label(entity, .9f); return entity;
        }

        public GameObject Decor(string name, PrimitiveType type, Vector3 position, Vector3 scale, Color color, bool solid = false, Quaternion? rotation = null)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(RoomRoot, false);
            go.transform.position = position; go.transform.localScale = scale; if (rotation.HasValue) go.transform.rotation = rotation.Value;
            var surface = type == PrimitiveType.Cube ? SurfaceTextures.Classify(name) : SurfaceTextures.Kind.None;
            go.Comp<Renderer>().sharedMaterial = surface == SurfaceTextures.Kind.None ? Material(color) : SurfaceTextures.Material(color, surface);
            if (surface != SurfaceTextures.Kind.None) SurfaceTextures.WorldUV(go);
            var c = go.Comp<Collider>(); if (!solid && c != null) { c.enabled = false; Destroy(c); }
            return PropModels.Decor(go, name, RoomRoot, position, scale, rotation ?? Quaternion.identity, solid);
        }

        public Light PointLight(Vector3 position, Color color, float range, float intensity)
        {
            var go = new GameObject("Demo light"); go.transform.SetParent(RoomRoot, false); go.transform.position = position;
            var light = go.AddComponent<Light>(); light.type = LightType.Point; light.color = color; light.range = range; light.intensity = intensity; light.shadows = LightShadows.None;
            return light;
        }

        // Floor slabs covering an area while leaving rectangular holes (pits).
        public void FloorWithHoles(Rect area, IList<Rect> holes, float topY, Color color, float thickness = .3f)
        {
            var xs = new List<float> { area.xMin, area.xMax }; var zs = new List<float> { area.yMin, area.yMax };
            foreach (var h in holes) { xs.Add(Mathf.Clamp(h.xMin, area.xMin, area.xMax)); xs.Add(Mathf.Clamp(h.xMax, area.xMin, area.xMax)); zs.Add(Mathf.Clamp(h.yMin, area.yMin, area.yMax)); zs.Add(Mathf.Clamp(h.yMax, area.yMin, area.yMax)); }
            xs.Sort(); zs.Sort();
            for (int j = 0; j < zs.Count - 1; j++)
            {
                float z0 = zs[j], z1 = zs[j + 1]; if (z1 - z0 < .01f) continue;
                float runStart = float.NaN;
                for (int i = 0; i < xs.Count - 1; i++)
                {
                    float x0 = xs[i], x1 = xs[i + 1]; if (x1 - x0 < .01f) continue;
                    Vector2 c = new Vector2((x0 + x1) * .5f, (z0 + z1) * .5f);
                    bool hole = false; foreach (var h in holes) if (h.Contains(c)) hole = true;
                    if (!hole && float.IsNaN(runStart)) runStart = x0;
                    if (hole && !float.IsNaN(runStart)) { Slab(runStart, x0, z0, z1, topY, color, thickness); runStart = float.NaN; }
                }
                if (!float.IsNaN(runStart)) Slab(runStart, xs[xs.Count - 1], z0, z1, topY, color, thickness);
            }
        }
        void Slab(float x0, float x1, float z0, float z1, float topY, Color color, float thickness)
        {
            Solid("Floor slab", new Vector3((x0 + x1) * .5f, topY - thickness * .5f, (z0 + z1) * .5f), new Vector3(x1 - x0, thickness, z1 - z0), color);
        }

        public Hazard AddHazard(HazardKind kind, Bounds volume, float dps, string label, SandboxEntity source = null, float lifetime = float.PositiveInfinity)
        {
            var go = new GameObject("Hazard: " + label); go.transform.SetParent(RoomRoot, false); go.transform.position = volume.center;
            var hazard = go.AddComponent<Hazard>(); hazard.Kind = kind; hazard.Volume = volume; hazard.DamagePerSecond = dps; hazard.Label = label; hazard.Source = source;
            hazard.Expires = Time.time + lifetime;
            DemoPhysics.Hazards.Add(hazard);
            return hazard;
        }

        // A spike pit: a hole with walls, a floor below and spikes. Floor around it is built by the scenario.
        public void SpikePit(Vector3 center, Vector2 size, float depth = 2.2f)
        {
            float w = size.x, d = size.y;
            Color wall = new Color(.22f, .2f, .19f), spike = new Color(.62f, .62f, .66f);
            Solid("Pit floor", center + Vector3.down * (depth + .15f), new Vector3(w, .3f, d), new Color(.12f, .1f, .1f));
            Solid("Pit wall", center + new Vector3(-w * .5f - .15f, -depth * .5f, 0), new Vector3(.3f, depth, d), wall);
            Solid("Pit wall", center + new Vector3(w * .5f + .15f, -depth * .5f, 0), new Vector3(.3f, depth, d), wall);
            Solid("Pit wall", center + new Vector3(0, -depth * .5f, -d * .5f - .15f), new Vector3(w + .6f, depth, .3f), wall);
            Solid("Pit wall", center + new Vector3(0, -depth * .5f, d * .5f + .15f), new Vector3(w + .6f, depth, .3f), wall);
            for (float x = -w * .5f + .3f; x < w * .5f; x += .6f)
                for (float z = -d * .5f + .3f; z < d * .5f; z += .6f)
                    Decor("Spike", PrimitiveType.Cylinder, center + new Vector3(x, -depth + .35f, z), new Vector3(.08f, .35f, .08f), spike);
            // The spike volume lies inside the pit only, so a bridge across it stays walkable.
            float bottom = center.y - depth - .3f, top = center.y - .5f;
            AddHazard(HazardKind.Spikes, new Bounds(new Vector3(center.x, (bottom + top) * .5f, center.z), new Vector3(w, top - bottom, d)), 0, "spike pit");
            var marker = SpawnObject(NextHandle("o_pit"), "pit", "spike pit", center, new Vector3(w, .1f, d), new Color(.3f, .1f, .1f), false, false);
            marker.Note = "hazard";
        }

        public void FireZone(Vector3 center, Vector3 size, float dps = 18, string label = "fire pit")
        {
            AddHazard(HazardKind.Fire, new Bounds(center + Vector3.up * .4f, size + Vector3.up * .8f), dps, label);
            Effects.Flames(center, size, RoomRoot);
            PointLight(center + Vector3.up * 1.2f, new Color(1, .55f, .2f), 7, 2.2f);
            var marker = SpawnObject(NextHandle("o_fire"), "fire", label, center, new Vector3(size.x, .1f, size.z), new Color(.9f, .35f, .05f), false, false);
            marker.Note = "hazard";
        }

        public void SpawnAcidPool(Vector3 point, float radius, float dps, float seconds, SandboxEntity source)
        {
            if (Physics.Raycast(point + Vector3.up * 1.5f, Vector3.down, out var hit, 4, ~0, QueryTriggerInteraction.Ignore)) point.y = hit.point.y;
            AddHazard(HazardKind.Acid, new Bounds(point + Vector3.up * .3f, new Vector3(radius * 2, .9f, radius * 2)), dps, "acid pool", source, seconds).Walkable = true;
            Effects.Pool(point, radius, new Color(.45f, .95f, .25f, .75f), seconds);
        }

        // ---------- Eye contact & snapshot ----------
        bool DemoEyeContact(SandboxMonster monster, out string reason)
        {
            var e = monster.Entity; var p = e.Profile; reason = null;
            if (p == null) return true;
            var eyes = monster.Brain != null ? monster.Brain.Eyes : p.Eyes;
            if (eyes == EyeRule.Eyeless) { reason = "It has no eyes to meet."; return false; }
            if (e.Asleep) { reason = p.Id == "mimic" ? "Its eyes are inside the closed lid. Lift the lid with E (it may bite)." : "Its eyes are closed. Wake it first."; return false; }
            if (e.VisorIntact) { reason = "Its eyes are behind an ice visor. Melt the visor with fire first."; return false; }
            if (monster.Brain != null && monster.Brain.CoveringFace) { reason = "It hides its face from you. Make a noise or throw something so it looks."; return false; }
            var physics = monster.Comp<PhysicsBody>();
            if (physics != null && physics.Knocked) { reason = "It is tumbling through the air."; return false; }
            var veto = Scenario.EyeVeto(monster);
            if (veto != null) { reason = veto; return false; }
            return true;
        }

        public bool PlayerWatching(SandboxMonster monster)
        {
            if (View == null || monster == null) return false;
            Vector3 delta = monster.Entity.Center - View.transform.position;
            return delta.magnitude < 24 && SandboxPerception.InCone(View.transform.position, View.transform.forward, monster.Entity.Center, 38)
                && SandboxPerception.HasLineOfSight(View.transform.position, monster.Entity, Player);
        }

        public string Describe(SandboxEntity entity)
        {
            string name = entity.DisplayName;
            if (entity.Kind == "monster" && !entity.Alive) name = "dead " + name;
            var held = HeldBy(entity);
            if (held != null && entity.Kind != "player") name += " holding the " + held.ItemKind.Replace('_', ' ');
            if (entity.Kind == "monster" && entity.Alive && entity.Asleep) name += ", asleep";
            return name;
        }

        JObject BuildDemoSnapshot(SandboxMonster listener)
        {
            Physics.SyncTransforms();
            var all = new JArray(); var perceived = new JArray();
            var me = listener.Entity; var profile = me.Profile;
            var candidates = new List<SandboxEntity>();
            foreach (var entity in Entities)
            {
                if (entity == null || entity == me || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                if (entity == Carried && Carrier == listener) continue;
                if (entity == PlayerProp) continue; // in your hands, not lying around
                candidates.Add(entity);
            }
            // Nearest first, so the bounded lists keep what matters to this listener.
            candidates.Sort((a, b) => Vector3.SqrMagnitude(a.transform.position - listener.transform.position).CompareTo(Vector3.SqrMagnitude(b.transform.position - listener.transform.position)));
            // The classifier's reference hierarchy groups observed entities by kind
            // and needs at most eight options per kind.
            var perKind = new Dictionary<string, int>();
            foreach (var entity in candidates)
            {
                if (all.Count >= 63) break;
                var delta = entity.transform.position - listener.transform.position;
                float forward = Vector3.Dot(delta, listener.transform.forward), right = Vector3.Dot(delta, listener.transform.right);
                string axis = Mathf.Abs(forward) >= Mathf.Abs(right) ? (forward >= 0 ? "forward" : "back") : (right >= 0 ? "right" : "left");
                string direction = axis == "forward" ? "in front of you" : axis == "back" ? "behind you" : "on your " + axis;
                if (delta.y > 1.6f) direction += ", above you";
                else if (delta.y < -1.6f) direction += ", below you";
                bool los = SandboxPerception.HasLineOfSight(listener.Eye, entity, listener.transform);
                bool visible = SandboxPerception.InCone(listener.Eye, listener.transform.forward, entity.Center, 60, true);
                float widths = Vector3.ProjectOnPlane(delta, Vector3.up).magnitude / SandboxPerception.PlayerWidth;
                string label = Describe(entity) + " " + direction;
                if (label.Length > 190) label = label.Substring(0, 190);
                var record = new JObject { ["handle"] = entity.Handle, ["kind"] = entity.Kind,
                    ["label"] = label, ["visible"] = visible, ["los"] = los,
                    ["distanceWidths"] = Math.Round(widths, 2), ["axis"] = axis, ["alive"] = entity.Kind == "object" || entity.Kind == "item" || entity.Alive,
                    ["health"] = entity.MaximumHealth > 0 ? Math.Round(entity.Health / entity.MaximumHealth, 2) : 1, ["size"] = entity.Profile != null ? entity.Profile.Mass / 100f : entity.Size, ["destructible"] = entity.Destructible };
                if (!string.IsNullOrEmpty(entity.Species)) record["species"] = entity.Species;
                if (!string.IsNullOrEmpty(entity.ItemKind)) record["itemKind"] = entity.ItemKind;
                if (!string.IsNullOrEmpty(entity.ObjectKind)) record["objectKind"] = entity.ObjectKind;
                if (!string.IsNullOrEmpty(entity.ColorName)) record["color"] = entity.ColorName;
                var held = HeldBy(entity); if (held != null) record["held"] = held.ItemKind;
                all.Add(record);
                perKind.TryGetValue(entity.Kind, out int count);
                if (los && (visible || widths <= 4) && count < 8) { perceived.Add(record.DeepClone()); perKind[entity.Kind] = count + 1; }
            }
            var ownItem = HeldBy(me); var inventory = new JArray();
            if (ownItem != null) inventory.Add(new JObject { ["handle"] = ownItem.Handle, ["itemKind"] = ownItem.ItemKind, ["transferable"] = true, ["usable"] = ownItem.ItemKind == "fire_pot", ["throwable"] = true });
            var skills = new JArray(); var cooldown = new JArray();
            foreach (var skill in profile.Skills) skills.Add(skill);
            if (profile.Has("fireball") && Time.time < listener.FireballReadyAt) cooldown.Add("fireball");
            return new JObject {
                ["id"] = "demo-" + Scenario.Id, ["actor"] = new JObject { ["handle"] = me.Handle, ["species"] = me.Species,
                    ["skills"] = skills, ["canFly"] = profile.CanFly, ["canCarry"] = profile.CanCarry, ["canEat"] = profile.CanEat,
                    ["held"] = ownItem == null ? JValue.CreateNull() : new JValue(ownItem.ItemKind), ["inventory"] = inventory,
                    ["cooldown"] = cooldown, ["triggerReady"] = profile.Trigger != null && Scenario.TriggerReady(listener) },
                ["speaker"] = new JObject { ["color"] = "white", ["label"] = "white player speaking" },
                ["world"] = all, ["perceived"] = perceived, ["controlSeconds"] = 5, ["status"] = "Thinking", ["leaseValid"] = true };
        }

        void OnDemoBrainwashed(SandboxMonster monster, JObject compiled)
        {
            monster.Brain?.ClearGrudges();
            Scenario.OnBrainwashed(monster, compiled);
        }

        void DemoEndControl(SandboxMonster actor)
        {
            if (Carrier == actor)
            {
                if (actor.Flying && actor.Entity.Alive && Carried != null && carryCommanded) { committedCarry = true; committedUntil = Time.time + 4; }
                else ReleaseCarry(Vector3.down * .5f);
            }
            actor.Reckless = false;
            Scenario.OnControlEnded(actor);
        }

        // ---------- Damage routing ----------
        public float AdjustDamage(SandboxEntity victim, float damage, SandboxEntity attacker, string element)
        {
            if (Scenario == null) return damage;
            if (victim.Profile != null) damage *= victim.Profile.Multiplier(element == "explosion" ? "fire" : element);
            var monster = victim.Comp<SandboxMonster>();
            if (monster != null && monster.Brain != null && monster.Brain.Petrified && (element == "physical" || element == "impact")) return 0;
            return Scenario.AdjustDamage(victim, damage, attacker, element);
        }

        public void EntityDamaged(SandboxEntity victim, float amount, SandboxEntity attacker, string element)
        {
            GameAudio.Hurt(victim, amount, element);
            if (Scenario == null) return;
            var monster = victim.Comp<SandboxMonster>();
            if (monster != null && victim.Alive)
            {
                if (victim.Asleep && victim.Profile?.Id != "mimic") { victim.Asleep = false; Report(victim.DisplayName + " wakes up!"); }
                if (attacker != null && attacker.Kind == "monster" && !IsControlled(monster)) monster.Brain?.OnHitByMonster(attacker);
                if (attacker == PlayerEntity) monster.Brain?.Alert(Player.position);
            }
            if (element == "fire" || element == "explosion")
            {
                if (victim.VisorIntact) { victim.VisorIntact = false; DemoVisuals.RemoveVisor(victim); Report("The ice visor melts — " + victim.DisplayName + "'s eyes are exposed!"); Effects.Pulse(victim.Center, new Color(.6f, .9f, 1), 1.4f); }
                if (victim.Flammable && victim.Alive) Ignite(victim, attacker);
            }
            if (victim.Explosive && victim.Alive && (element == "fire" || element == "explosion" || amount >= 18)) Explode(victim.Center, 3.6f, 60, attacker ?? victim, victim);
            if (amount >= 1 && victim != PlayerEntity) Effects.Popup(victim.Center + Vector3.up * .6f, Mathf.RoundToInt(amount).ToString(), element == "fire" ? new Color(1, .55f, .2f) : element == "ice" ? new Color(.6f, .9f, 1) : Color.white);
            Scenario.OnDamaged(victim, amount, attacker, element);
        }

        public void EntityKilled(SandboxEntity victim, SandboxEntity attacker, string element)
        {
            GameAudio.Died(victim, element);
            if (Scenario == null) return;
            if (victim.Kind == "monster")
                Log?.Invoke(victim.DisplayName + " dies (" + element + (attacker != null && attacker != victim ? ", " + attacker.DisplayName : "") + ").");
            if (flames.TryGetValue(victim, out var flame)) { if (flame != null) Destroy(flame); flames.Remove(victim); }
            victim.BurningUntil = 0;
            if (victim.Explosive && !exploding.Contains(victim)) Explode(victim.Center, 3.6f, 60, attacker ?? victim, victim);
            if (victim.Kind == "object" && victim.Grabbable)
            {
                if (PlayerProp == victim) ReleasePlayerProp();
                Effects.Burst(victim.Center, victim.ModelTint ?? (victim.Body != null ? victim.Body.sharedMaterial.color : Color.gray));
                victim.gameObject.SetActive(false);
                if (victim.Label != null) victim.Label.gameObject.SetActive(false);
            }
            if (victim == Carried) ReleaseCarry(Vector3.zero);
            var monster = victim.Comp<SandboxMonster>();
            if (monster != null && Carrier == monster) ReleaseCarry(Vector3.zero);
            if (monster != null)
            {
                // A creature that dies in the air (a hovering Harpy, a falling body)
                // drops as a rigid body instead of freezing where it was.
                var physics = monster.Comp<PhysicsBody>();
                bool airborne = monster.Flying || monster.transform.position.y > SandboxMonster.GroundHeight(monster.transform.position, monster.transform) + .35f;
                if (airborne && (physics == null || !physics.Knocked)) PhysicsBody.For(monster).Knock(Vector3.down * .8f, attacker);
            }
            if (monster != null)
            {
                var held = HeldBy(victim);
                if (held != null) { var prop = held.Comp<PhysicsProp>(); held.Drop(RoomRoot, victim.Center); if (prop != null) prop.Throw(Vector3.up * 2, null); Scenario.OnItemFreed(held, "killed", victim); }
                for (var item = TakeSwallowed(victim); item != null; item = TakeSwallowed(victim))
                {
                    Vector2 spread = UnityEngine.Random.insideUnitCircle.normalized * 1.4f;
                    TossItem(item, victim.Center + Vector3.up * .4f, victim.transform.position + new Vector3(spread.x, 0, spread.y), victim);
                    Report("The " + item.DisplayName + " tumbles out of the " + victim.DisplayName + ".");
                    Scenario.OnItemFreed(item, "killed", victim);
                }
            }
            Scenario.OnKilled(victim, attacker, element, Cause(victim, attacker, element));
        }

        // How an entity died, for environment-kill scoring.
        static string Cause(SandboxEntity victim, SandboxEntity attacker, string element)
        {
            if (element == "spikes" || element == "fall" || element == "impact" || element == "explosion") return "environment";
            if (attacker != null && attacker.Kind == "object") return "environment";
            if ((element == "fire" || element == "acid") && (attacker == null || attacker.Explosive)) return "environment";
            if (attacker != null && attacker.Kind == "monster" && attacker != victim) return "monster";
            if (attacker != null && attacker.Kind == "player") return element == "physical" ? "weapon" : "player";
            return element;
        }

        public void MonsterKnocked(SandboxMonster monster, Vector3 velocity, SandboxEntity thrower)
        {
            if (Carrier == monster) ReleaseCarry(velocity * .5f);
            else if (Carried == monster.Entity) { Carried = null; Carrier = null; committedCarry = carryCommanded = false; }
            Scenario?.OnKnocked(monster, velocity, thrower);
        }
        public void MonsterRecovered(SandboxMonster monster) => Scenario?.OnRecovered(monster);
        public void Impact(SandboxEntity body, SandboxEntity other, float speed)
        {
            GameAudio.Play(body != null && body.Kind == "object" ? "wood_impact" : "body_impact", body != null ? body.Center : Player.position, Mathf.Clamp01(speed / 12f), 1, .06f);
            Scenario?.OnImpact(body, other, speed);
        }

        public void PropImpact(SandboxEntity prop, SandboxEntity hit, float speed, SandboxEntity thrower, float mass)
        {
            if (Scenario == null || prop == null) return;
            // The prop's own landing sound: stones clack, barrels and crates thump, other items drop.
            string landing = prop.ItemKind == "pebble" || prop.ItemKind == "rock" ? "pebble_land" : prop.Kind == "object" ? "wood_impact" : "item_drop";
            GameAudio.Play(landing, prop.Center, Mathf.Clamp01(speed / 10f), prop.ObjectKind == "barrel" ? .85f : 1, .05f);
            if (speed > 3.5f && Time.time - prop.LastDamageTime > .2f) MakeNoise(prop.Center, mass < 3 ? 7 : 9);
            if (prop.Explosive && speed > 8.5f) { Explode(prop.Center, 3.6f, 60, thrower ?? prop, prop); return; }
            if (prop.ItemKind == "fire_pot" && speed > 3 && thrower != null) { FireBurst(prop.Center, 1.9f, 12, thrower); prop.gameObject.SetActive(false); if (prop.Label != null) prop.Label.gameObject.SetActive(false); return; }
            if (prop.BurningUntil > Time.time && hit != null && hit != prop && hit.Alive && hit != PlayerEntity) { hit.TakeDamage(8, thrower, "fire"); Ignite(hit, thrower); }
            if (hit == null || hit == prop || !hit.Alive || speed < 4.5f) return;
            float damage = Mathf.Clamp((speed - 3.5f) * Mathf.Max(1, mass) * .7f, 4, 45);
            if (hit == PlayerEntity) { DamagePlayer(Mathf.Round(damage * .5f), prop.DisplayName); return; }
            hit.TakeDamage(damage, thrower, "impact");
            var monster = hit.Comp<SandboxMonster>();
            if (monster != null && hit.Alive)
            {
                monster.Brain?.Stagger(.5f);
                if (mass >= 8) PhysicsBody.For(monster).Knock((hit.transform.position - prop.transform.position).normalized * 4 * DemoPhysics.MassScale(hit, 40) + Vector3.up * 1.5f, thrower);
            }
        }

        public void KnockPlayer(Vector3 velocity) => App?.AddExternalVelocity(velocity);

        // Launch velocity that lands the player one step past the nearest rim of a
        // spike pit, along its short axis (never into a side wall of a long chasm).
        // A rim whose flight path is blocked (a raised drawbridge) is skipped.
        Vector3 SpikeEscape(Bounds pit, Vector3 feet)
        {
            Vector3 c = pit.center, e = pit.extents;
            float rimY = pit.max.y + .5f;
            var rims = new List<Vector3>(); var along = new List<Vector3>();
            if (e.x <= e.z + .01f) { rims.Add(new Vector3(c.x - e.x - 1.1f, rimY, feet.z)); rims.Add(new Vector3(c.x + e.x + 1.1f, rimY, feet.z)); along.Add(Vector3.forward); along.Add(Vector3.forward); }
            if (e.z <= e.x + .01f) { rims.Add(new Vector3(feet.x, rimY, c.z - e.z - 1.1f)); rims.Add(new Vector3(feet.x, rimY, c.z + e.z + 1.1f)); along.Add(Vector3.right); along.Add(Vector3.right); }
            var order = new List<int>(); for (int i = 0; i < rims.Count; i++) order.Add(i);
            order.Sort((a, b) => Vector3.ProjectOnPlane(rims[a] - feet, Vector3.up).sqrMagnitude.CompareTo(Vector3.ProjectOnPlane(rims[b] - feet, Vector3.up).sqrMagnitude));
            Vector3 start = feet + Vector3.up * .1f;
            foreach (int i in order)
                foreach (float shift in new[] { 0f, 1.5f, -1.5f, 3f, -3f })
                {
                    Vector3 rim = rims[i] + along[i] * shift;
                    Vector3 velocity = DemoPhysics.Ballistic(start, rim, 3.2f);
                    if (!DemoPhysics.ArcBlocked(start + Vector3.up * .8f, velocity, rim + Vector3.up * .8f, .3f, Player, null)) return velocity;
                }
            return DemoPhysics.Ballistic(start, rims[order[0]], 3.2f);
        }

        // A shattered fire pot: a small burst of flame, enough to melt ice.
        public void FireBurst(Vector3 center, float radius, float damage, SandboxEntity source)
        {
            Effects.Flash(center, new Color(1, .5f, .15f), radius);
            Report("The fire pot bursts into flames!");
            foreach (var entity in new List<SandboxEntity>(Entities))
            {
                if (entity == null || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                if (Reach(entity, center) > radius) continue;
                if (entity == PlayerEntity) { DamagePlayer(6, "The flames"); continue; }
                entity.TakeDamage(damage, source, "fire");
                if (entity.Alive) Ignite(entity, source);
            }
            MakeNoise(center, 8);
        }

        // Distance from a point to an entity's body, so flames reach the edge of a
        // wide creature instead of only its middle.
        static float Reach(SandboxEntity entity, Vector3 point)
        {
            var body = entity.Comp<Collider>();
            if (body != null && body.enabled) return Vector3.Distance(body.bounds.ClosestPoint(point), point);
            return Mathf.Max(0, Vector3.Distance(entity.Center, point) - .6f);
        }

        public void Ignite(SandboxEntity entity, SandboxEntity source)
        {
            if (entity == null || !entity.Alive) return;
            bool fresh = entity.BurningUntil < Time.time;
            entity.BurningUntil = Mathf.Max(entity.BurningUntil, Time.time + (entity.Kind == "object" ? 6 : 3));
            entity.BurnSource = source;
            if (fresh && !flames.ContainsKey(entity)) flames[entity] = Effects.AttachFlame(entity);
        }

        public void Explode(Vector3 center, float radius, float damage, SandboxEntity source, SandboxEntity exploder = null)
        {
            if (exploder != null) { if (exploding.Contains(exploder)) return; exploding.Add(exploder); }
            Effects.Flash(center, new Color(1, .6f, .2f), radius);
            SandboxBootstrap.ShakeAt(center, 1, radius * 5);
            GameAudio.Play("explosion", center);
            Report("BOOM! An explosion rips through the area.");
            foreach (var entity in new List<SandboxEntity>(Entities))
            {
                if (entity == null || entity == exploder || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                Vector3 delta = entity.Center - center; float distance = delta.magnitude;
                if (distance > radius) continue;
                if (entity == PlayerProp) ReleasePlayerProp();
                float falloff = 1 - distance / radius;
                Vector3 push = (delta.normalized + Vector3.up * .7f).normalized * (12 * falloff + 3);
                if (entity == PlayerEntity) { DamagePlayer(Mathf.Round(damage * .45f * falloff), "The explosion"); KnockPlayer(push * .7f); continue; }
                var monster = entity.Comp<SandboxMonster>();
                if (monster != null) PhysicsBody.For(monster).Knock(push * DemoPhysics.MassScale(entity, 70, .1f, 1.3f), source);
                var prop = entity.Comp<PhysicsProp>();
                if (prop != null) prop.Throw(push * .8f, source);
                entity.TakeDamage(damage * falloff, source, "explosion");
                if (entity.Alive && (entity.Flammable || entity.Kind == "monster") && falloff > .4f) Ignite(entity, source);
            }
            if (exploder != null && exploder.Alive) exploder.TakeDamage(9999, source, "explosion");
            MakeNoise(center, 14);
        }

        // Speaking is loud: the louder the voice, the farther monsters hear it.
        public void VoiceNoise(float radius)
        {
            if (Scenario == null || PlayerEntity == null || !PlayerEntity.Alive) return;
            foreach (var actor in Monsters)
                if (actor != null && !IsControlled(actor) && actor.Entity.Alive && actor.Brain != null && Vector3.Distance(actor.transform.position, Player.position) <= radius)
                    actor.Brain.HearNoise(Player.position, 2);
            Effects.NoiseRing(Player.position, radius, new Color(.4f, .9f, 1));
            Scenario.OnNoise(Player.position, radius);
        }

        public void MakeNoise(Vector3 point, float radius)
        {
            foreach (var actor in Monsters)
                if (actor != null && actor.Entity.Alive && actor.Brain != null && Vector3.Distance(actor.transform.position, point) <= radius)
                    actor.Brain.HearNoise(point, 2.6f);
            Scenario?.OnNoise(point, radius);
        }

        // ---------- Per-frame demo systems ----------
        void DemoUpdate(float dt)
        {
            float now = Time.time;
            if (!GameOver && !Victory) Scenario.Tick(dt);
            for (int i = DemoPhysics.Hazards.Count - 1; i >= 0; i--)
            {
                var hazard = DemoPhysics.Hazards[i];
                if (hazard == null || now >= hazard.Expires) { if (hazard != null) Destroy(hazard.gameObject); DemoPhysics.Hazards.RemoveAt(i); }
            }
            foreach (var entity in new List<SandboxEntity>(Entities))
            {
                if (entity == null || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                Vector3 feet = entity.transform.position;
                if (feet.y < -9)
                {
                    if (entity == PlayerEntity) { PlayerEntity.TakeDamage(9999, null, "fall"); FinishRun(false); }
                    else if (entity.Kind == "monster") entity.TakeDamage(9999, entity.LastAttacker, "fall");
                    else
                    {
                        // Items and props lost over the edge are gone for good.
                        entity.gameObject.SetActive(false);
                        if (entity.Label != null) entity.Label.gameObject.SetActive(false);
                        Scenario.OnLost(entity);
                    }
                    continue;
                }
                if (entity.Kind == "item" || entity.Note == "hazard") continue;
                var flyer = entity.Comp<SandboxMonster>();
                bool hovering = flyer != null && flyer.Flying;
                foreach (var hazard in DemoPhysics.Hazards.ToArray())
                {
                    if (hazard == null || !hazard.Contains(feet + Vector3.up * .2f)) continue;
                    if (hazard.Kind == HazardKind.Spikes)
                    {
                        if (hovering) continue;
                        if (entity == PlayerEntity)
                        {
                            // The player is flung back over the nearest rim; monsters are not so lucky.
                            hazardTicks.TryGetValue(entity, out float ready);
                            if (now < ready) continue;
                            hazardTicks[entity] = now + 1;
                            DamagePlayer(35, "The spikes");
                            if (PlayerEntity.Alive) KnockPlayer(SpikeEscape(hazard.Volume, feet));
                        }
                        else entity.TakeDamage(140, entity.LastAttacker, "spikes");
                        continue;
                    }
                    if (hovering && hazard.Kind != HazardKind.Fire) continue;
                    if (hazard.DamagePerSecond <= 0) continue;
                    hazardTicks.TryGetValue(entity, out float next);
                    if (now < next) continue;
                    hazardTicks[entity] = now + .5f;
                    float amount = hazard.DamagePerSecond * .5f;
                    string element = hazard.Kind == HazardKind.Fire ? "fire" : hazard.Kind == HazardKind.Acid ? "acid" : "physical";
                    if (entity == PlayerEntity) DamagePlayer(Mathf.Round(amount), hazard.Label);
                    else entity.TakeDamage(amount, hazard.Source, element);
                    if (hazard.Kind == HazardKind.Fire && entity != PlayerEntity && (entity.Kind == "monster" || entity.Flammable)) Ignite(entity, hazard.Source);
                }
                if (entity.BurningUntil > now)
                {
                    burnTicks.TryGetValue(entity, out float burn);
                    if (now >= burn)
                    {
                        burnTicks[entity] = now + .5f;
                        entity.TakeDamage(entity.Kind == "object" ? 9 : 4, entity.BurnSource, "fire");
                        foreach (var other in Entities)
                            if (other != null && other != entity && other.Flammable && other.Alive && other.BurningUntil < now
                                && Vector3.Distance(other.Center, entity.Center) < 1.4f) Ignite(other, entity.BurnSource);
                    }
                }
                else if (flames.TryGetValue(entity, out var flame)) { if (flame != null) Destroy(flame); flames.Remove(entity); }
            }
            foreach (var actor in Monsters)
            {
                if (actor == null || actor.LandingSpeed <= 0) continue;
                float landing = actor.LandingSpeed; actor.LandingSpeed = 0;
                if (actor.Entity.Alive) actor.Entity.TakeDamage((landing - 9) * 7, actor.Entity.LastAttacker, "fall");
            }
            if (Scenario.PlayerRegen > 0 && PlayerEntity.Alive && !GameOver && !Victory && Time.time - PlayerEntity.LastDamageTime > 4)
                PlayerEntity.Heal(Scenario.PlayerRegen * dt);
            if (committedCarry)
            {
                if (Carrier == null || !Carrier.Entity.Alive || Time.time > committedUntil) ReleaseCarry(Vector3.down * .5f);
                else if (Carrier.FlyToward(CarryGoal, (Carrier.Entity.Profile != null ? Carrier.Entity.Profile.Speed : 4) * 1.5f, dt, .45f, out _))
                { Report(Carrier.Entity.DisplayName + " finishes the flight and lets go."); ReleaseCarry(Vector3.down * .5f); }
            }
            UpdateCarry();
            UpdateHalo();
            Effects.Tick();
        }

        void UpdateHalo()
        {
            foreach (var pair in halos)
                if (pair.Value != null && (pair.Key == null || !pair.Key.Entity.Alive || !IsControlled(pair.Key))) pair.Value.SetActive(false);
            foreach (var lease in leases)
            {
                var monster = lease.Monster;
                if (monster == null || !monster.Entity.Alive) continue;
                if (!halos.TryGetValue(monster, out var ring) || ring == null) halos[monster] = ring = Effects.Halo();
                ring.SetActive(true);
                ring.transform.position = monster.transform.position + Vector3.up * (monster.Motor.height + .35f);
                ring.transform.Rotate(0, 140 * Time.deltaTime, 0);
                Effects.Tint(ring, lease.Status == "Listening" ? new Color(.35f, .9f, 1) : lease.Status == "Thinking" ? new Color(1, .85f, .3f) : new Color(.85f, .35f, 1));
            }
        }

        string DemoLabel(SandboxEntity entity, SandboxMonster actor)
        {
            if (actor == null) return entity.Kind == "remains" ? entity.DisplayName : entity.DisplayName;
            if (!entity.Alive) return entity.DisplayName + " (dead)";
            string state = IsControlled(actor) ? StatusOf(actor).ToUpperInvariant() : actor.Brain?.State ?? "Normal";
            string marks = "";
            if (actor.Brain != null)
                foreach (var pair in actor.Brain.Grudges)
                    if (pair.Key != null) marks += "\n" + (pair.Value.Stage >= 3 ? "ANGRY at " : new string('!', pair.Value.Stage) + " ") + pair.Key.DisplayName;
            return entity.DisplayName + " · " + state + "\nHP " + Mathf.CeilToInt(entity.Health) + marks;
        }

        // ---------- Player verbs ----------
        bool DemoPlayerAttack()
        {
            if (GameOver || Victory || !PlayerEntity.Alive || Time.time < nextPlayerAttack) return false;
            nextPlayerAttack = Time.time + .45f; Physics.SyncTransforms();
            SandboxEntity target = null; float closest = float.PositiveInfinity;
            foreach (var entity in Entities)
            {
                if (entity == null || entity == PlayerEntity || entity == PlayerProp || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                bool monster = entity.Kind == "monster", prop = entity.Kind == "object" && entity.Destructible;
                if (!monster && !prop) continue;
                float radius = entity.Comp<SandboxMonster>()?.Motor.radius ?? .4f;
                float distance = Vector3.Distance(Player.position, entity.transform.position);
                if (distance > 2.2f + radius || distance >= closest
                    || !SandboxPerception.InCone(View.transform.position, View.transform.forward, entity.Center, 42)
                    || !SandboxPerception.HasLineOfSight(View.transform.position, entity, Player)) continue;
                target = entity; closest = distance;
            }
            if (target == null) { Report("Staff swing missed."); return false; }
            GameAudio.Play(target.Kind == "monster" && target.Species != "ice_golem" && target.Species != "gargoyle" && target.Species != "mimic" ? "staff_hit_flesh" : "staff_hit_hard", target.Center);
            SandboxBootstrap.Shake(.12f);
            Effects.Pulse(Vector3.Lerp(target.Center, View.transform.position, .25f), new Color(1, .95f, .75f), .35f);
            float damage = Scenario.PlayerWeaponDamage;
            target.TakeDamage(damage, PlayerEntity, "physical");
            var actor = target.Comp<SandboxMonster>();
            Vector3 push = Vector3.ProjectOnPlane(View.transform.forward, Vector3.up).normalized;
            if (actor != null)
            {
                actor.Brain?.Stagger(.55f);
                float scale = DemoPhysics.MassScale(target, 30, 0, 1.2f);
                if (scale > .35f && target.Alive) PhysicsBody.For(actor).Knock((push * 3.2f + Vector3.up * 1.4f) * scale, PlayerEntity);
            }
            else target.Comp<PhysicsProp>()?.Throw(push * 3 + Vector3.up, PlayerEntity);
            Report("Staff hit " + target.DisplayName + " for " + damage + ".");
            return true;
        }

        // Dark Messiah-style kick: low damage, huge displacement for light bodies.
        public bool PlayerKick()
        {
            if (Scenario == null || GameOver || Victory || !PlayerEntity.Alive || Time.time < nextKick) return false;
            nextKick = Time.time + .75f; Physics.SyncTransforms();
            Vector3 forward = Vector3.ProjectOnPlane(View.transform.forward, Vector3.up).normalized;
            SandboxEntity target = null; float closest = float.PositiveInfinity;
            foreach (var entity in Entities)
            {
                if (entity == null || entity == PlayerEntity || entity == PlayerProp || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                if (entity.Kind != "monster" && entity.Comp<PhysicsProp>() == null) continue;
                float radius = entity.Comp<SandboxMonster>()?.Motor.radius ?? .4f;
                float distance = Vector3.ProjectOnPlane(entity.transform.position - Player.position, Vector3.up).magnitude;
                if (distance > 1.9f + radius || distance >= closest || !SandboxPerception.InCone(Player.position + Vector3.up, forward, entity.Center, 55, true)) continue;
                target = entity; closest = distance;
            }
            App?.KickAnimation();
            GameAudio.Play2D("kick_whoosh");
            if (target == null) { Report("Your kick hits nothing."); return false; }
            GameAudio.Play("kick_impact", target.Center);
            Vector3 velocity = forward * 8.8f + Vector3.up * 3.4f;
            var actor = target.Comp<SandboxMonster>();
            if (actor != null)
            {
                float scale = DemoPhysics.MassScale(target, 55, .06f, 1.45f);
                target.TakeDamage(4, PlayerEntity, "physical");
                if (target.Alive) PhysicsBody.For(actor).Knock(velocity * scale, PlayerEntity);
                Report(scale < .3f ? target.DisplayName + " is too heavy to kick far." : "You kick " + target.DisplayName + " away!");
            }
            else { target.Comp<PhysicsProp>().Throw(velocity * .9f, PlayerEntity); Report("You kick the " + target.DisplayName + "."); }
            MakeNoise(Player.position, 6);
            return true;
        }

        // Lets go of the prop held with G where it is, without a throw (a blast
        // knocks it out of your hands, or it was destroyed).
        public void ReleasePlayerProp()
        {
            var prop = PlayerProp; if (prop == null) return;
            PlayerProp = null;
            prop.transform.SetParent(RoomRoot, true);
            prop.SetPhysicalCollidersEnabled(true);
            if (prop.Label != null) prop.Label.gameObject.SetActive(prop.gameObject.activeSelf);
            prop.Comp<PhysicsProp>()?.SetHeld(false);
        }

        // Solid, level floor under a point: not a pit, a hazard or the abyss.
        public bool SafeGround(Vector3 point)
        {
            if (DemoPhysics.HazardAt(point)) return false;
            if (!Physics.Raycast(point + Vector3.up * 1.2f, Vector3.down, out var hit, 2.4f, ~0, QueryTriggerInteraction.Ignore)) return false;
            var entity = hit.collider.GetComponentInParent<SandboxEntity>();
            if (entity != null && (entity.Kind == "monster" || entity.Kind == "player")) return false;
            return hit.normal.y > .6f && !DemoPhysics.HazardAt(hit.point);
        }

        // Where to set something down at an entity: beside a creature, in front of
        // a door or another solid object (on the carrier's side), else on it.
        public Vector3 LandingNear(SandboxEntity entity, Vector3 from)
        {
            Vector3 at = entity.transform.position;
            Vector3 away = Vector3.ProjectOnPlane(from - at, Vector3.up);
            if (away.sqrMagnitude < .01f) away = Vector3.back;
            away.Normalize();
            if (entity.Kind == "monster" || entity.Kind == "player")
            {
                Vector3 beside = at + away * 1.3f;
                return SafeGround(beside) ? beside : at;
            }
            var box = entity.Comp<BoxCollider>();
            if (entity.Kind != "object" || box == null || !box.enabled) return at;
            Bounds b = box.bounds;
            bool alongZ = b.extents.z <= b.extents.x;
            float reach = (alongZ ? b.extents.z : b.extents.x) + .9f;
            float sign = (alongZ ? away.z : away.x) >= 0 ? 1 : -1;
            for (int side = 0; side < 2; side++, sign = -sign)
            {
                Vector3 spot = new Vector3(b.center.x, b.min.y, b.center.z) + (alongZ ? Vector3.forward : Vector3.right) * sign * reach;
                if (SafeGround(spot)) return spot;
            }
            return at;
        }

        // The nearest spot where a standing capsule fits: the desired feet
        // position, lifted onto the floor if it sank in, else stepping out,
        // first toward a preferred point.
        public Vector3 FreeStandingSpot(Vector3 feet, Vector3 toward, float radius, float height)
        {
            if (Fits(feet, radius, height)) return feet;
            Vector3 lifted = feet;
            if (Physics.Raycast(feet + Vector3.up * 1.4f, Vector3.down, out var floor, 2f, ~0, QueryTriggerInteraction.Ignore) && floor.normal.y > .6f && floor.point.y > feet.y)
            { lifted.y = floor.point.y + .03f; if (Fits(lifted, radius, height)) return lifted; }
            Vector3 back = Vector3.ProjectOnPlane(toward - lifted, Vector3.up);
            Vector3 first = back.sqrMagnitude > .01f ? back.normalized : Vector3.back;
            float[] turns = { 0, 45, -45, 90, -90, 135, -135, 180 };
            for (float distance = .5f; distance <= 3.01f; distance += .5f)
                foreach (float turn in turns)
                {
                    Vector3 candidate = lifted + Quaternion.AngleAxis(turn, Vector3.up) * first * distance;
                    if (!Physics.Raycast(candidate + Vector3.up * 1.4f, Vector3.down, out var ground, 2.6f, ~0, QueryTriggerInteraction.Ignore) || ground.normal.y < .6f) continue;
                    if (Mathf.Abs(ground.point.y - lifted.y) > 1f) continue;
                    candidate.y = ground.point.y + .03f;
                    if (Fits(candidate, radius, height)) return candidate;
                }
            return lifted;
        }

        static bool Fits(Vector3 feet, float radius, float height)
        {
            Vector3 bottom = feet + Vector3.up * (radius + .05f), top = feet + Vector3.up * Mathf.Max(radius + .05f, height - radius);
            foreach (var collider in Physics.OverlapCapsule(bottom, top, radius * .95f, ~0, QueryTriggerInteraction.Ignore))
            {
                var entity = collider.GetComponentInParent<SandboxEntity>();
                if (entity != null && (entity.Kind == "monster" || entity.Kind == "player" || entity.Kind == "item")) continue;
                return false;
            }
            return true;
        }

        // Grab a physics prop in front of you, or throw the one you hold.
        public bool PlayerGrabOrThrow(bool gentle)
        {
            if (Scenario == null || GameOver || Victory || Time.time < nextGrab) return false;
            nextGrab = Time.time + .25f;
            if (PlayerProp != null)
            {
                var prop = PlayerProp; PlayerProp = null;
                var physics = prop.Comp<PhysicsProp>();
                prop.transform.SetParent(RoomRoot, true);
                prop.SetPhysicalCollidersEnabled(true);
                if (prop.Label != null) prop.Label.gameObject.SetActive(true);
                float mass = physics?.Body != null ? physics.Body.mass : 5;
                Vector3 velocity = gentle ? View.transform.forward * 1.5f : View.transform.forward * Mathf.Lerp(14, 6.5f, Mathf.InverseLerp(1, 30, mass)) + Vector3.up * 1.6f;
                physics?.Throw(velocity, gentle ? null : PlayerEntity, PlayerEntity);
                Report(gentle ? "You set down the " + prop.DisplayName + "." : "You throw the " + prop.DisplayName + "!");
                GameAudio.Play2D(gentle ? "item_drop" : "throw_whoosh");
                if (!gentle) Scenario.OnPlayerThrew(prop);
                return true;
            }
            SandboxEntity best = null; float closest = 2.5f;
            foreach (var entity in Entities)
            {
                if (entity == null || entity.Owner != null || !entity.Grabbable || !entity.gameObject.activeInHierarchy || entity == Carried) continue;
                if (entity.Kind == "object" && !entity.Alive) continue;
                if (entity.ItemKind == "key" || entity.ItemKind == "relic") continue;
                float distance = Vector3.Distance(View.transform.position, entity.Center);
                if (distance > closest || !SandboxPerception.InCone(View.transform.position, View.transform.forward, entity.Center, 45)) continue;
                best = entity; closest = distance;
            }
            if (best == null) { Report("Nothing to grab. Face a crate, barrel, pebble or piece of cheese within reach."); return false; }
            var grabbed = best.Comp<PhysicsProp>();
            grabbed?.SetHeld(true);
            best.SetPhysicalCollidersEnabled(false);
            best.transform.SetParent(App.HoldPoint, true);
            best.transform.localPosition = Vector3.zero;
            if (best.Label != null) best.Label.gameObject.SetActive(false);
            PlayerProp = best;
            GameAudio.Play2D("grab");
            Report("Holding the " + best.DisplayName + ". G to throw, Shift+G to set down.");
            return true;
        }

        bool DemoPlayerInteract()
        {
            if (GameOver || Victory) return false;
            if (Scenario.OnInteract()) return true;
            foreach (var actor in Monsters)
            {
                if (actor == null || !actor.Entity.Alive || actor.Entity.Profile?.Id != "mimic") continue;
                if (Vector3.Distance(Player.position, actor.transform.position) > 2.1f || !SandboxPerception.InCone(View.transform.position, View.transform.forward, actor.Entity.Center, 50)) continue;
                if (actor.Entity.Asleep)
                {
                    actor.Entity.Asleep = false; DemoVisuals.SetLid(actor.Entity, true);
                    GameAudio.Play("mimic_lid_open", actor.Entity.Center);
                    Report("You lift the lid. A pair of eyes stares back at you!");
                    if (UnityEngine.Random.value < .35f) DamagePlayer(10, "The Mimic's teeth");
                    Scenario.OnMimicOpened(actor);
                    return true;
                }
            }
            var held = HeldBy(PlayerEntity);
            SandboxEntity nearest = null; float closest = 1.9f;
            foreach (var entity in Entities)
            {
                if (entity == null || entity.Kind != "item" || entity.Owner != null || !entity.gameObject.activeInHierarchy || entity == PlayerProp) continue;
                // Measured from the chest so items on pedestals and ledges are reachable.
                float distance = Vector3.Distance(Player.position + Vector3.up * .9f, entity.transform.position);
                if (distance > closest || !SandboxPerception.InCone(View.transform.position, View.transform.forward, entity.Center, 65)
                    || !SandboxPerception.HasLineOfSight(View.transform.position, entity, Player)) continue;
                nearest = entity; closest = distance;
            }
            if (nearest != null && held != null)
            {
                held.Drop(RoomRoot, Player.position + Vector3.up * .5f + Vector3.ProjectOnPlane(-View.transform.right, Vector3.up).normalized * .6f);
                Report("Swapped the " + held.DisplayName + " for the " + nearest.DisplayName + ".");
                held = null;
            }
            if (nearest != null && held == null)
            {
                nearest.Comp<PhysicsProp>()?.SetHeld(true);
                nearest.Attach(PlayerEntity, PlayerEntity.HandSocket != null ? PlayerEntity.HandSocket : Player);
                GameAudio.Play2D(nearest.ItemKind == "key" ? "key_pickup" : nearest.ItemKind == "relic" ? "relic_pickup" : "item_pickup");
                Report("Picked up the " + nearest.DisplayName + ".");
                Scenario.OnPlayerPickedUp(nearest);
                return true;
            }
            if (held != null)
            {
                Vector3 drop = Player.position + Vector3.ProjectOnPlane(View.transform.forward, Vector3.up).normalized * .8f + Vector3.up * .5f;
                held.Drop(RoomRoot, drop); held.Comp<PhysicsProp>()?.SetHeld(false);
                GameAudio.Play2D("item_drop");
                Report("Dropped the " + held.DisplayName + ".");
                return true;
            }
            Report("Nothing to interact with here.");
            return false;
        }

        public bool PlayerHoldsKind(string kind) => HeldBy(PlayerEntity)?.ItemKind == kind || PlayerProp?.ItemKind == kind;

        // ---------- Queries & world verbs for AI, executor and triggers ----------
        public SandboxEntity NearestItem(Vector3 point, string kind, float range, SandboxMonster perceiver)
        {
            SandboxEntity best = null; float closest = range;
            foreach (var entity in Entities)
            {
                if (entity == null || entity.Kind != "item" || entity.ItemKind != kind || entity.Owner != null || !entity.gameObject.activeInHierarchy || entity == PlayerProp) continue;
                float distance = Vector3.Distance(point, entity.transform.position);
                if (distance > closest || perceiver != null && !SandboxPerception.HasLineOfSight(perceiver.Eye, entity, perceiver.transform)) continue;
                best = entity; closest = distance;
            }
            return best;
        }

        public SandboxEntity NearestGrabbable(Vector3 point, float range)
        {
            SandboxEntity best = null; float closest = range;
            foreach (var entity in Entities)
            {
                if (entity == null || !entity.Grabbable || entity.Owner != null || entity == PlayerProp || !entity.gameObject.activeInHierarchy || entity.Kind == "item") continue;
                if (!entity.Alive) continue;
                float distance = Vector3.Distance(point, entity.transform.position);
                if (distance < closest) { best = entity; closest = distance; }
            }
            return best;
        }

        public void MonsterThrowProp(SandboxMonster thrower, SandboxEntity prop, Vector3 target)
        {
            var physics = prop.Comp<PhysicsProp>(); if (physics == null) return;
            thrower.Cue("throw");
            physics.Teleport(thrower.Hand.position + Vector3.up * .3f);
            physics.Throw(DemoPhysics.Ballistic(prop.transform.position, target, 1.2f), thrower.Entity);
            Report(thrower.Entity.DisplayName + " hurls the " + prop.DisplayName + "!");
        }

        public void EatItem(SandboxMonster eater, SandboxEntity item)
        {
            if (eater == null || item == null || !item.gameObject.activeInHierarchy) return;
            if (item.Owner != null) { if (item.Owner.HeldItem == item) item.Drop(RoomRoot, item.transform.position); }
            if (eater.Entity.Profile?.Id == "mimic")
            {
                if (!swallowed.TryGetValue(eater.Entity, out var list)) swallowed[eater.Entity] = list = new List<SandboxEntity>();
                list.Add(item); item.gameObject.SetActive(false);
                Report("The Mimic swallows the " + item.DisplayName + ".");
            }
            else
            {
                item.gameObject.SetActive(false); Entities.Remove(item);
                eater.Entity.Heal(15); Report(eater.Entity.DisplayName + " devours the " + item.DisplayName + ".");
            }
            if (item.Label != null) item.Label.gameObject.SetActive(false);
            Scenario?.OnEaten(eater, item);
        }

        public SandboxEntity TakeSwallowed(SandboxEntity mimic)
        {
            if (!swallowed.TryGetValue(mimic, out var list) || list.Count == 0) return null;
            var item = list[list.Count - 1]; list.RemoveAt(list.Count - 1); return item;
        }
        public void StoreSwallowed(SandboxEntity mimic, SandboxEntity item)
        {
            if (!swallowed.TryGetValue(mimic, out var list)) swallowed[mimic] = list = new List<SandboxEntity>();
            list.Add(item); item.gameObject.SetActive(false);
            if (item.Label != null) item.Label.gameObject.SetActive(false);
        }

        // Throw an item out of a creature toward a point (mimic gift, rat tide drop).
        public void TossItem(SandboxEntity item, Vector3 from, Vector3 target, SandboxEntity source = null)
        {
            if (item == null) return;
            if (item.Owner != null && item.Owner.HeldItem == item) item.Drop(RoomRoot, from);
            item.gameObject.SetActive(true);
            if (item.Label != null) item.Label.gameObject.SetActive(true);
            item.transform.SetParent(RoomRoot, true);
            item.SetPhysicalCollidersEnabled(true);
            var physics = item.Comp<PhysicsProp>() ?? PhysicsProp.Make(item, 1);
            physics.SetHeld(false);
            physics.Teleport(from);
            physics.Throw(DemoPhysics.Ballistic(from, target, 1.2f), null, source);
        }

        // ---------- Carrying (Harpy flights, Ogre tosses) ----------
        public bool BeginCarry(SandboxMonster carrier, SandboxEntity cargo, bool commanded = false)
        {
            if (cargo == null || cargo == PlayerProp) return false;
            // Already holding it (a Harpy brainwashed mid-snatch): the order takes over.
            if (Carrier == carrier && Carried == cargo) { carryCommanded |= commanded; return true; }
            if (Carrier != null) return false;
            // A tumbling body cannot be gripped until it lands.
            var tumbling = cargo.Comp<PhysicsBody>();
            if (tumbling != null && tumbling.Knocked) return false;
            Carrier = carrier; Carried = cargo; carryCommanded = commanded; CarryOrigin = cargo.transform.position;
            if (cargo == PlayerEntity) App?.SetCarried(carrier);
            else
            {
                var monster = cargo.Comp<SandboxMonster>();
                if (monster != null) { monster.Motor.enabled = false; }
                cargo.Comp<PhysicsProp>()?.SetHeld(true);
                cargo.SetPhysicalCollidersEnabled(false);
            }
            Report(carrier.Entity.DisplayName + " grabs " + (cargo == PlayerEntity ? "you" : "the " + cargo.DisplayName) + "!");
            Scenario?.OnCarried(carrier, cargo, false);
            return true;
        }

        void UpdateCarry()
        {
            if (Carrier == null || Carried == null) return;
            if (!Carrier.Entity.Alive) { ReleaseCarry(Vector3.zero); return; }
            if (Carried == PlayerEntity) return; // the bootstrap follows the carrier itself
            Vector3 hold = Carrier.Flying ? Carrier.transform.position + Vector3.down * .9f : Carrier.Hand.position + Vector3.up * .2f;
            Carried.transform.position = hold;
        }

        public void ReleaseCarry(Vector3 velocity, SandboxEntity thrower = null)
        {
            committedCarry = false; carryCommanded = false;
            if (Carrier == null || Carried == null) { Carrier = null; Carried = null; return; }
            var cargo = Carried; var carrier = Carrier; Carried = null; Carrier = null;
            if (cargo == PlayerEntity) { App?.ReleaseCarried(velocity); return; }
            cargo.SetPhysicalCollidersEnabled(true);
            var monster = cargo.Comp<SandboxMonster>();
            if (monster != null && cargo.Alive)
            {
                var knocked = monster.Comp<PhysicsBody>();
                if (knocked == null || !knocked.Knocked)
                {
                    monster.Motor.enabled = true;
                    if (velocity.sqrMagnitude > .1f) PhysicsBody.For(monster).Knock(velocity, thrower);
                    else monster.Teleport(cargo.transform.position, monster.transform.forward);
                }
                else monster.Motor.enabled = false; // the rigid body keeps it until it settles
            }
            var prop = cargo.Comp<PhysicsProp>();
            if (prop != null) prop.Throw(velocity, thrower, carrier.Entity);
            else if (cargo.Kind == "item") { var made = PhysicsProp.Make(cargo, 1); made.Throw(velocity, thrower, carrier.Entity); }
        }

        // Ogre toss: whatever it holds flies on a ballistic arc to the goal.
        // exact: the goal is itself the point (a pit), so the landing never shifts.
        public void TossCarried(SandboxMonster thrower, Vector3 goal, bool exact = false)
        {
            if (Carrier != thrower || Carried == null) return;
            thrower.Cue("throw");
            var cargo = Carried;
            Vector3 from = cargo == PlayerEntity ? Player.position + Vector3.up * .9f : cargo.transform.position;
            Vector3 velocity = TossVelocity(from, goal + Vector3.up * .4f, thrower.transform, cargo == PlayerEntity ? Player : cargo.transform, exact);
            Report(thrower.Entity.DisplayName + " hurls " + (cargo == PlayerEntity ? "you" : "the " + cargo.DisplayName) + "!");
            ReleaseCarry(velocity, thrower.Entity);
            Effects.NoiseRing(thrower.transform.position, 5, new Color(.9f, .8f, .6f));
            Scenario?.OnCarried(thrower, cargo, true);
        }

        // The lowest clear arc: first the plain throw, then the landing shifted
        // sideways, then higher arcs. Falls back to the plain throw.
        Vector3 TossVelocity(Vector3 from, Vector3 target, Transform thrower, Transform cargo, bool exact)
        {
            Vector3 flat = Vector3.ProjectOnPlane(target - from, Vector3.up);
            Vector3 side = flat.sqrMagnitude > .01f ? Vector3.Cross(Vector3.up, flat.normalized) : Vector3.right;
            float[] offsets = exact ? new[] { 0f } : new[] { 0f, 1.5f, -1.5f, 3f, -3f };
            foreach (float apex in new[] { 2.2f, 3.4f, 5f, 7f })
                foreach (float offset in offsets)
                {
                    if (offset != 0 && (apex > 3.4f || !SafeGround(target + side * offset + Vector3.down * .4f))) continue;
                    Vector3 aim = target + side * offset;
                    Vector3 velocity = DemoPhysics.Ballistic(from, aim, apex);
                    if (!DemoPhysics.ArcBlocked(from, velocity, aim, .35f, thrower, cargo)) return velocity;
                }
            return DemoPhysics.Ballistic(from, target, 2.2f);
        }

        // A melee blow from any monster: commanded or natural. Heavy hitters shove.
        public void MeleeHit(SandboxMonster attacker, SandboxEntity target, bool commanded, float multiplier = 1)
        {
            var p = attacker.Entity.Profile;
            if (p == null || target == null || !target.Alive) return;
            attacker.Cue(p.Id == "mimic" ? "bite" : "strike");
            if (p.Id == "bomb_toad") { Explode(attacker.Entity.Center, 3.4f, 55, attacker.Entity, attacker.Entity); return; }
            float damage = Mathf.Max(8, p.Damage * (commanded ? 1.25f : 1)) * multiplier;
            string element = p.Id == "ice_golem" ? "ice" : "physical";
            if (target == PlayerEntity) DamagePlayer(Mathf.Round(damage), attacker.Entity.DisplayName);
            else target.TakeDamage(damage, attacker.Entity, element);
            Vector3 push = Vector3.ProjectOnPlane(target.transform.position - attacker.transform.position, Vector3.up).normalized;
            float force = p.Mass >= 250 ? 6.5f : p.Mass >= 80 ? 3.5f : 0;
            var victim = target.Comp<SandboxMonster>();
            if (force > 0 && target.Alive)
            {
                if (victim != null) PhysicsBody.For(victim).Knock((push * force + Vector3.up * force * .4f) * DemoPhysics.MassScale(target, 80, .1f, 1.4f), attacker.Entity);
                else if (target == PlayerEntity) KnockPlayer(push * force * .8f + Vector3.up * 2);
                else { var prop = target.Comp<PhysicsProp>(); if (prop != null) prop.Throw(push * force + Vector3.up * 2, attacker.Entity); }
            }
            else if (victim != null && victim.Brain != null) victim.Brain.Stagger(.3f);
            MakeNoise(target.Center, 5);
        }

        public int CallAllies(SandboxMonster caller)
        {
            int count = 0;
            foreach (var other in Monsters)
            {
                if (other == null || other == caller || !other.Entity.Alive || other.Entity.Species != caller.Entity.Species || other.Brain == null) continue;
                if (Vector3.Distance(other.transform.position, caller.transform.position) > 30) continue;
                other.Brain.Rally(caller.transform.position, 7);
                count++;
            }
            MakeNoise(caller.transform.position, 12);
            Effects.NoiseRing(caller.transform.position, 12, new Color(.6f, 1, .3f));
            return count;
        }

        public void ProjectileHit(SandboxProjectile projectile, SandboxEntity entity, SandboxEntity source, string element, float damage, Vector3 point)
        {
            Color color = element == "fire" ? new Color(1, .55f, .2f) : element == "ice" ? new Color(.6f, .9f, 1) : new Color(.5f, 1, .3f);
            Effects.Flash(point, color, .9f);
            GameAudio.Play(element == "ice" ? "ice_impact" : element == "acid" ? "acid_sizzle" : "fireball_impact", point);
            string name = element == "fire" ? "Fireball" : element == "ice" ? "Ice bolt" : "Projectile";
            if (entity == PlayerEntity) { DamagePlayer(Mathf.Round(damage * .6f), source != null ? source.DisplayName + "'s " + name.ToLowerInvariant() : name); }
            else if (entity != null && entity.Alive)
            {
                entity.TakeDamage(damage, source, element);
                var victim = entity.Comp<SandboxMonster>();
                if (element == "ice" && victim != null && victim.Brain != null) victim.Brain.Stagger(1.2f);
                if (element == "fire" && entity.Flammable) Ignite(entity, source);
                Report(name + " hit " + entity.DisplayName + ".");
            }
            if (element == "fire")
                foreach (var other in new List<SandboxEntity>(Entities))
                    if (other != null && other != entity && other.Flammable && other.Alive && other.Owner == null && Vector3.Distance(other.Center, point) < 1.3f)
                    { other.TakeDamage(6, source, "fire"); Ignite(other, source); }
            MakeNoise(point, 6);
        }

        // ---------- Special abilities used by commands and triggers ----------
        public void RatTide(SandboxEntity target, SandboxMonster mouse)
        {
            if (target == null || !target.Alive) return;
            var tide = new GameObject("Rat tide").AddComponent<RatTideEffect>();
            tide.transform.SetParent(RoomRoot, false);
            tide.Begin(this, target, mouse.Entity);
            Report("RAT TIDE! The colony pours out of the walls at " + target.DisplayName + "!");
            var held = HeldBy(target);
            if (held != null && target != PlayerEntity)
            {
                // In its panic the accused flings what it holds at the accuser, onto
                // floor (never into a chasm beside it).
                Vector3 flat = Vector3.ProjectOnPlane(target.transform.position - mouse.transform.position, Vector3.up);
                Vector3 direction = flat.sqrMagnitude > .01f ? flat.normalized : mouse.transform.forward;
                Vector3 toward = mouse.transform.position + direction * 1.2f;
                if (!SafeGround(toward)) toward = mouse.transform.position - direction * 1.2f;
                if (!SafeGround(toward)) toward = mouse.transform.position;
                TossItem(held, target.Center + Vector3.up * .4f, toward, target);
                Report(target.DisplayName + " flings the " + held.DisplayName + " away in panic!");
                Scenario?.OnItemFreed(held, "rat_tide", target);
            }
            var victim = target.Comp<SandboxMonster>();
            if (victim != null) victim.Brain?.Flee(mouse.Entity, 4.5f);
        }

        public void DragonBreath(SandboxMonster imp)
        {
            var breath = imp.gameObject.AddComponent<DragonBreathEffect>();
            breath.Begin(this, imp);
            Report(imp.Entity.DisplayName + " believes it is a DRAGON and unleashes a torrent of fire!");
        }

        public void GroundSlam(SandboxMonster slammer)
        {
            slammer.Cue("slam");
            SandboxBootstrap.ShakeAt(slammer.transform.position, .8f, 14);
            Effects.Flash(slammer.transform.position + Vector3.up * .2f, new Color(.8f, .7f, .55f), 3.4f);
            foreach (var entity in new List<SandboxEntity>(Entities))
            {
                if (entity == null || entity == slammer.Entity || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                Vector3 delta = entity.transform.position - slammer.transform.position; float d = Vector3.ProjectOnPlane(delta, Vector3.up).magnitude;
                if (d > 3.4f) continue;
                if (entity == PlayerProp) ReleasePlayerProp();
                Vector3 push = (Vector3.ProjectOnPlane(delta, Vector3.up).normalized + Vector3.up * .8f) * 7.5f;
                if (entity == PlayerEntity) { DamagePlayer(14, slammer.Entity.DisplayName); KnockPlayer(push); continue; }
                entity.TakeDamage(22, slammer.Entity, "impact");
                var monster = entity.Comp<SandboxMonster>();
                if (monster != null && entity.Alive) PhysicsBody.For(monster).Knock(push * DemoPhysics.MassScale(entity, 90, .1f, 1.3f), slammer.Entity);
                entity.Comp<PhysicsProp>()?.Throw(push, slammer.Entity);
            }
            Report(slammer.Entity.DisplayName + " slams the ground!");
        }

        // Nurse revive: remains stand up again; the scenario decides what they become.
        public bool ReviveRemains(SandboxEntity remains, SandboxMonster nurse)
        {
            if (remains == null || remains.Kind != "remains" || remains.Alive) return false;
            Effects.Pulse(remains.Center, new Color(.4f, 1, .55f), 2);
            GameAudio.Play("revive", remains.Center);
            Report(nurse.Entity.DisplayName + " revives the " + remains.DisplayName + "!");
            Scenario?.OnRevived(remains, nurse);
            return true;
        }

        public void TrackScenarioObject(GameObject go) { if (go != null) scenarioOnly.Add(go); }

        // ---------- Test hooks used by DemoSmoke ----------
        public JObject DebugSnapshot(SandboxMonster listener) => BuildDemoSnapshot(listener);

        // Commits an already-compiled host directive to a monster, skipping eye
        // contact and the model. Only the acceptance harness calls this. Unless
        // keepOthers is set, every other cast and lease is cancelled first.
        public bool DebugCommand(SandboxMonster target, JObject compiled, bool keepOthers = false)
        {
            if (!keepOthers) Cancel();
            else { var old = LeaseOf(target); if (old != null) { leases.Remove(old); EndControl(target, old.Status == "Brainwashed"); } }
            if (target == null || !target.Entity.Alive) return false;
            var lease = new BrainwashLease(target, CastNumber, 0);
            leases.Add(lease); Monster = target;
            Seal(new List<BrainwashLease> { lease });
            return ApplyOutcome(lease.Id, compiled);
        }

        public void DebugSetMP(float value) { MP = Mathf.Clamp(value, 0, MaxMP); }
    }
}
