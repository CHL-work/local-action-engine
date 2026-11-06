using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Consumes the host's typed task graph. This class never sees transcript text.
    public sealed partial class SandboxExecutor
    {
        private readonly SandboxWorld world;
        private readonly BrainwashLease lease;
        private readonly SandboxMonster monster;
        private readonly string leaseId, actorGeneration;
        private readonly Vector3 savedHere, savedListenerForward, savedSpeakerForward;
        private readonly JArray steps, tasks;
        private readonly Dictionary<int, Target> results = new Dictionary<int, Target>();
        private Dictionary<string, Target> bindings;
        private int index;
        private float started, blockedSeconds, nextAttack;
        private bool finished, acted;
        private string template;
        private JObject args, task;
        // Demo scenarios extend the verified classic executor in SandboxExecutor.Demo.cs.
        private readonly bool demo;
        private sealed class Target
        {
            public SandboxEntity Entity;
            public string Generation;
            public Vector3 Point;
            public bool IsPoint;
            public Vector3 Position => IsPoint ? Point : Entity.transform.position;
        }
        public SandboxExecutor(SandboxWorld world, BrainwashLease lease, JObject directive)
        {
            this.world = world; this.lease = lease; monster = lease.Monster; leaseId = lease.Id;
            actorGeneration = monster.Entity.Generation;
            savedHere = lease.SavedHere; savedListenerForward = lease.SavedListenerForward; savedSpeakerForward = lease.SavedSpeakerForward;
            steps = (JArray)directive["steps"].DeepClone(); tasks = (JArray)directive["tasks"].DeepClone();
            demo = world.Scenario != null;
        }
        private bool OwnsControl => monster != null && world.FindLease(leaseId) == lease && world.IsCurrent(monster.Entity)
            && monster.Entity.Generation == actorGeneration && monster.Entity.Alive && world.PlayerEntity.Alive
            && lease.Status == "Brainwashed" && lease.ControlRemaining > 0;
        private bool Fail(string message)
        {
            finished = true;
            if (OwnsControl) monster.SetPose("confused");
            world.Report("Execution failed: " + message + ". Remaining steps cancelled."); return false;
        }
        private Target Resolve(JObject binding)
        {
            if (binding == null) return null;
            string kind = (string)binding["kind"]; var reference = binding["reference"] as JObject;
            if (kind == "bound" || kind == "bound_inventory")
            { var entity = world.Find((string)binding["handle"]); return lease.IsSnapshotIdentity(entity) ? new Target { Entity = entity } : null; }
            if (kind == "self") return new Target { Entity = monster.Entity };
            if (kind == "speaker") return new Target { Entity = world.PlayerEntity };
            if (kind == "held" || kind == "inventory")
            {
                var entity = world.HeldBy(monster.Entity); string expected = (string)reference?["expected_kind"];
                return entity != null && (expected == null || expected == entity.ItemKind) ? new Target { Entity = entity } : null;
            }
            if (kind == "step_result")
            {
                return results.TryGetValue((int?)reference?["step"] ?? -1, out var result)
                    && world.IsCurrent(result.Entity) && result.Entity.Generation == result.Generation ? new Target { Entity = result.Entity } : null;
            }
            if (kind == "here") return new Target { Point = savedHere, IsPoint = true };
            if (kind == "direction")
            {
                string axis = (string)reference?["axis"], frame = (string)reference?["frame"];
                if (axis == "up" || axis == "down") return null;
                Vector3 forward = frame == "speaker" ? savedSpeakerForward : savedListenerForward;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 direction = axis == "forward" ? forward : axis == "back" ? -forward : axis == "left" ? -right : right;
                float amount = (string)task["completion"] == "advance_two_player_widths" ? 2 * SandboxPerception.PlayerWidth : 50;
                return new Target { IsPoint = true, Point = monster.transform.position + direction * amount };
            }
            return null;
        }
        private Target ResolveOwnedSelector(string slot)
        {
            // Transfer verbs operate on the actor's inventory. A kind-only
            // selector may have compiled to a ground match or search task because
            // held objects are excluded from perception. Explicit handles and
            // previous-step results must retain their original exact identity.
            if (slot != "item" || (template != "drop" && template != "give" && template != "throw")) return null;
            var reference = args[slot] as JObject;
            if ((string)reference?["kind"] != "selector" || (string)reference["entity_kind"] != "item"
                || (string)reference["rank"] != "any" || !(reference["filters"] is JArray filters) || filters.Count != 1
                || (string)filters[0]?["type"] != "kind") return null;
            var item = world.HeldBy(monster.Entity);
            return item != null && world.IsCurrent(item) && item.ItemKind == (string)filters[0]["value"] ? new Target { Entity = item } : null;
        }
        private bool BeginStep()
        {
            var step = steps[index] as JObject; task = tasks[index] as JObject;
            if (step == null || task == null || !(step["args"] is JObject arguments)) return Fail("invalid task");
            args = arguments; template = (string)step["template"];
            if (demo) return DemoBeginStep();
            switch (template)
            {
                case "move": case "follow": case "flee": case "guard": case "stop": case "pose": case "attack":
                case "use_skill": case "pick_up": case "drop": case "give": case "throw": case "interact": break;
                default: return Fail("understood_but_not_implemented: " + template);
            }
            if ((string)args["movement"] == "fly") return Fail("this monster cannot fly");
            if (template == "use_skill" && (string)args["skill"] != "fireball" && (string)args["skill"] != "strongest") return Fail("unavailable skill");
            if (template == "use_skill" && monster.Entity.Species != "imp") return Fail("this monster does not have fireball");
            bindings = new Dictionary<string, Target>();
            var bindingObjects = task["bindings"] as JObject;
            if (bindingObjects == null) return Fail("missing typed bindings");
            foreach (var pair in bindingObjects)
            {
                var target = ResolveOwnedSelector(pair.Key);
                if (target == null && (string)pair.Value?["kind"] == "search") return Fail("search_not_implemented: no perceived match");
                if (target == null) target = Resolve(pair.Value as JObject);
                if (target == null) return Fail("missing target/item, stale identity or unsupported vertical direction");
                if (!target.IsPoint) target.Generation = target.Entity.Generation;
                bindings[pair.Key] = target;
            }
            started = Time.realtimeSinceStartup; blockedSeconds = 0; nextAttack = 0; acted = false;
            world.Report("Step " + (index + 1) + ": " + template);
            if ((string)args["movement"] == "jump") monster.Jump();
            return true;
        }
        private Target Get(string key) => bindings.TryGetValue(key, out var target) ? target : null;
        private bool Move(Target target, float speed, float dt, float range, out bool failed, bool away = false)
        {
            failed = false;
            if (target == null) { failed = true; Fail("missing movement destination"); return false; }
            // Arrival means reaching the object's surface, not trying to put the
            // character controller through its collider to reach the center.
            if (!target.IsPoint)
            {
                var targetMonster = target.Entity.GetComponent<SandboxMonster>();
                var collider = target.Entity.GetComponent<Collider>();
                Vector3 extents = collider != null && collider.enabled ? collider.bounds.extents
                    : target.Entity.Body != null ? target.Entity.Body.bounds.extents : Vector3.zero;
                float targetRadius = targetMonster != null && targetMonster.Motor != null ? targetMonster.Motor.radius : Mathf.Max(extents.x, extents.z);
                range = Mathf.Max(range, monster.Motor.radius + targetRadius + .08f);
            }
            bool arrived = monster.MoveToward(target.Position, speed, dt, range, out float moved, away);
            blockedSeconds = !arrived && moved < speed * dt * .12f ? blockedSeconds + dt : 0;
            if (blockedSeconds > .8f) { failed = true; Fail("no traversable path to the target"); }
            return arrived;
        }
        private bool HasSight(Target target) => target.IsPoint || SandboxPerception.HasLineOfSight(monster.Eye, target.Entity, monster.transform);
        // This primitive sandbox represents a toss as a bounded floor placement.
        // Sweep the whole item from the actor's side of an obstacle; an unchecked
        // hand/forward offset could begin beyond a thin wall before any raycast.
        public static bool TryFindDropPosition(SandboxMonster monster, SandboxEntity item, Vector3 destination, float maxDistance, out Vector3 position)
        {
            position = Vector3.zero;
            if (monster == null || item == null || monster.Motor == null || maxDistance <= 0) return false;
            Physics.SyncTransforms();
            float radius = item.Body != null ? item.Body.bounds.extents.magnitude : .25f;
            radius = Mathf.Max(.05f, radius);
            Vector3 start = monster.transform.position + Vector3.up * (radius + .03f);
            Vector3 delta = Vector3.ProjectOnPlane(destination - monster.transform.position, Vector3.up);
            float distance = Mathf.Min(maxDistance, delta.magnitude);
            position = start;
            float clearance = monster.Motor.radius + radius + .06f;
            if (distance < clearance) return false;
            Vector3 direction = delta.normalized;
            bool Ignore(Collider collider) => collider.transform == monster.transform || collider.transform.IsChildOf(monster.transform)
                || collider.transform == item.transform || collider.transform.IsChildOf(item.transform);
            var leaving = new HashSet<Collider>();
            foreach (var collider in Physics.OverlapSphere(start, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (Ignore(collider)) continue;
                // A wide item can overlap a wall at the actor's center even when
                // the character itself fits. Permit only movement away from that
                // surface, then verify every part of the exit below.
                Vector3 separation = start - collider.ClosestPoint(start);
                if (separation.sqrMagnitude < .00001f || Vector3.Dot(separation.normalized, direction) <= .01f) return false;
                leaving.Add(collider);
            }
            foreach (var hit in Physics.SphereCastAll(start, radius, direction, distance, ~0, QueryTriggerInteraction.Ignore))
                if (!Ignore(hit.collider) && !leaving.Contains(hit.collider)) distance = Mathf.Min(distance, Mathf.Max(0, hit.distance - .03f));
            // A center ray also prevents an initially overlapping sphere from
            // passing through the obstacle to reach its far side.
            foreach (var hit in Physics.RaycastAll(start, direction, distance, ~0, QueryTriggerInteraction.Ignore))
                if (!Ignore(hit.collider)) distance = Mathf.Min(distance, Mathf.Max(0, hit.distance - radius - .03f));
            if (distance < clearance) return false;
            position = start + direction * distance;
            foreach (var collider in Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Ignore))
                if (!Ignore(collider)) return false;
            if (leaving.Count > 0)
            {
                int samples = Mathf.CeilToInt(distance / .08f);
                for (int i = 1; i <= samples; i++)
                {
                    var overlaps = Physics.OverlapSphere(start + direction * (distance * i / samples), radius, ~0, QueryTriggerInteraction.Ignore);
                    var stillLeaving = new HashSet<Collider>();
                    foreach (var collider in overlaps)
                    {
                        if (Ignore(collider)) continue;
                        if (!leaving.Contains(collider)) return false;
                        stillLeaving.Add(collider);
                    }
                    leaving = stillLeaving;
                }
            }
            // Rest the item on the actual floor instead of suspending it at the
            // sweep sphere's height. Keys have a centered root box collider.
            var floors = Physics.RaycastAll(position + Vector3.up * .3f, Vector3.down, 8, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(floors, (a, b) => a.distance.CompareTo(b.distance));
            RaycastHit floor = default; bool foundFloor = false;
            foreach (var hit in floors)
            {
                if (Ignore(hit.collider)) continue;
                if (hit.normal.y < .8f || hit.point.y > monster.transform.position.y + .25f) return false;
                floor = hit; foundFloor = true; break;
            }
            if (!foundFloor) return false;
            var box = item.GetComponent<BoxCollider>();
            float halfHeight = box != null ? Mathf.Abs(box.size.y * item.transform.lossyScale.y) * .5f : radius;
            position.y = floor.point.y + halfHeight + .025f;
            foreach (var collider in Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Ignore))
                if (!Ignore(collider) && collider != floor.collider) return false;
            return true;
        }
        // Shared by explicit drop and lifecycle release. Failure keeps the one
        // real item attached; the world retries after the crowd clears.
        public static bool TryDropHeldItem(SandboxWorld world, SandboxMonster monster, out string reason)
        {
            reason = null;
            if (world == null || monster == null || monster.Entity == null) { reason = "The item's owner is unavailable."; return false; }
            var item = world.HeldBy(monster.Entity);
            if (item == null) return true;
            float radius = item.Body != null ? item.Body.bounds.extents.magnitude : .25f;
            float near = Mathf.Max(.95f, monster.Motor.radius + radius + .12f);
            float[] angles = { 0, 45, -45, 90, -90, 135, -135, 180 };
            foreach (float distance in new[] { near, near + .6f, near + 1.2f })
                foreach (float angle in angles)
                {
                    Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * monster.transform.forward;
                    if (!TryFindDropPosition(monster, item, monster.transform.position + direction * distance, distance, out var point)) continue;
                    item.Drop(world.RoomRoot, point); return true;
                }
            reason = "No safe floor space is available beside the monster; the held item will be retried.";
            return false;
        }
        public bool Tick(float dt)
        {
            if (finished) return false;
            if (!OwnsControl) { finished = true; return false; }
            if (index >= steps.Count) { finished = true; monster.SetPose("idle"); world.Report("All requested actions completed; waiting for control expiry."); return false; }
            if (bindings == null && !BeginStep()) return false;
            if (demo) return DemoTick(dt);
            foreach (var target in bindings.Values)
                if (!target.IsPoint && (!world.IsCurrent(target.Entity) || target.Entity.Generation != target.Generation)) return Fail("a bound target disappeared");
            bool done = false, failed = false;
            SandboxEntity produced = null;
            float elapsed = Time.realtimeSinceStartup - started;
            if (template == "move") done = Move(Get("destination"), (string)args["movement"] == "run" ? 5 : 3, dt, .35f, out failed);
            else if (template == "follow" || template == "guard" || template == "flee")
            {
                var target = Get(template == "follow" ? "subject" : template == "guard" ? "ward" : "threat");
                if (target == null || (!target.IsPoint && !target.Entity.Alive)) return Fail("target is no longer alive");
                Move(target, template == "flee" ? 4 : 3, dt, 1.1f, out failed, template == "flee");
            }
            else if (template == "stop") monster.SetPose("stopped");
            else if (template == "pose")
            {
                var pose = (string)args["pose"]; monster.SetPose(pose);
                if (pose == "turn_around" && !acted) { monster.TurnAround(); acted = true; }
                if (monster.Entity.Body != null)
                    monster.Entity.Body.transform.localRotation = Quaternion.Euler(pose == "bow" ? 25 : 0, pose == "dance" ? Mathf.Sin(elapsed * 12) * 30 : 0, pose == "wave" ? Mathf.Sin(elapsed * 15) * 12 : 0);
                done = elapsed >= .85f;
            }
            else if (template == "pick_up")
            {
                var item = Get("item")?.Entity;
                if (item == null || item.Kind != "item" || item.Owner != null || world.HeldBy(monster.Entity) != null) return Fail("pickup item unavailable or hand occupied");
                if (Move(Get("item"), 3, dt, .8f, out failed))
                { if (!HasSight(Get("item"))) return Fail("item is occluded"); item.Attach(monster.Entity, monster.Hand); produced = item; done = true; }
            }
            else if (template == "drop" || template == "give" || template == "throw")
            {
                var item = Get("item")?.Entity;
                if (item == null || item.Owner != monster.Entity || world.HeldBy(monster.Entity) != item) return Fail("the exact requested item is no longer held");
                var recipient = Get(template == "give" ? "recipient" : "target");
                if (template == "give" && (recipient == null || recipient.IsPoint || !recipient.Entity.Alive)) return Fail("recipient is unavailable");
                bool close = template != "give" || Move(recipient, 3, dt, 1.3f, out failed);
                if (close)
                {
                    if (template == "give")
                    {
                        if (!HasSight(recipient)) return Fail("recipient is behind an obstruction");
                        if (world.HeldBy(recipient.Entity) != null) return Fail("recipient hand is occupied");
                        var socket = recipient.Entity.HandSocket;
                        item.Attach(recipient.Entity, socket != null ? socket : recipient.Entity.transform);
                        if (socket == null) item.transform.localPosition = new Vector3(.4f, 1, .4f);
                    }
                    else if (template == "drop")
                    {
                        if (!TryDropHeldItem(world, monster, out var reason)) return Fail(reason);
                    }
                    else
                    {
                        if (recipient == null) return Fail("missing throw destination");
                        if (!TryFindDropPosition(monster, item, recipient.Position, 4, out var drop))
                            return Fail("no unobstructed space to place the held item");
                        item.Drop(world.RoomRoot, drop);
                    }
                    produced = item; done = true;
                }
            }
            else if (template == "attack" || template == "use_skill")
            {
                var target = Get("target"); if (target == null) return Fail("missing attack target");
                bool skill = template == "use_skill" && (string)task["recipe"] != "basic_attack_same_target";
                if (skill && Time.time < monster.FireballReadyAt) { if (target.IsPoint) return Fail("fireball is on cooldown"); skill = false; }
                if (!target.IsPoint && !target.Entity.Alive) return Fail("attack target is no longer alive");
                else if (!target.IsPoint && target.Entity.Kind == "object" && !target.Entity.Destructible) return Fail("target cannot be damaged");
                else if (skill || Move(target, 4, dt, 1.3f, out failed))
                {
                    if (!HasSight(target)) return Fail("attack target is now occluded");
                    if (Time.realtimeSinceStartup >= nextAttack)
                    {
                        nextAttack = Time.realtimeSinceStartup + .65f; monster.SetPose(skill ? "fireball" : "attacking"); if (!skill) monster.Cue("strike");
                        if (skill) { SandboxProjectile.Launch(world, monster, target.IsPoint ? target.Point : target.Entity.Center); monster.FireballReadyAt = Time.time + 1.5f; }
                        else if (!target.IsPoint) target.Entity.TakeDamage(15);
                        world.Report((skill ? "Fireball" : "Basic attack") + " → " + (target.IsPoint ? "direction" : target.Entity.DisplayName));
                        string completion = (string)task["completion"];
                        done = template == "use_skill" || completion == "one_activation" || completion == "one_attack_cycle" || (!target.IsPoint && !target.Entity.Alive);
                    }
                }
            }
            else if (template == "interact")
            {
                var target = Get("object")?.Entity; string operation = (string)args["operation"];
                string expected = operation == "pull" ? "lever" : operation == "push" ? "crate" : "door";
                if (target == null || target.ObjectKind != expected) return Fail("object does not support this operation");
                if (Move(Get("object"), 3, dt, 1.4f, out failed))
                {
                    if (!HasSight(Get("object"))) return Fail("interaction is blocked");
                    if (operation == "push")
                    {
                        Vector3 direction = Vector3.ProjectOnPlane(target.transform.position - monster.transform.position, Vector3.up).normalized;
                        foreach (var hit in Physics.BoxCastAll(target.Center, target.Body.bounds.extents * .9f, direction, target.transform.rotation, .7f, ~0, QueryTriggerInteraction.Ignore))
                            if (hit.transform != target.transform && !hit.transform.IsChildOf(target.transform) && !hit.transform.IsChildOf(monster.transform)) return Fail("crate cannot be pushed through an obstacle");
                        target.transform.position += direction * .7f;
                    }
                    else if (expected == "door") target.SetOpen(operation == "open");
                    else target.SetOpen(!target.Open);
                    done = true;
                }
            }
            if (failed) return false;
            if (task["durationSeconds"] != null)
            {
                bool durationEnded = elapsed >= Mathf.Clamp((float)task["durationSeconds"], 0, 5);
                done = template == "attack" ? done || durationEnded : durationEnded;
            }
            if (done)
            {
                if (produced != null) results[index] = new Target { Entity = produced, Generation = produced.Generation };
                world.Report("Completed step " + (index + 1) + ": " + template + (produced != null ? " [" + produced.Handle + "]" : ""));
                if (monster.Entity.Body != null) monster.Entity.Body.transform.localRotation = Quaternion.identity;
                index++; bindings = null;
            }
            return true;
        }
    }

    // A committed projectile continues independently after its command expires.
    public sealed class SandboxProjectile : MonoBehaviour
    {
        private SandboxWorld world;
        private SandboxEntity source;
        private Vector3 velocity;
        private float expires;
        // Demo projectiles carry an element and damage and use world damage routing.
        private string element;
        private float damage = 35;
        private bool routed;
        public static SandboxProjectile Launch(SandboxWorld world, SandboxMonster monster, Vector3 target, string element, float damage, Color color)
        {
            var projectile = Launch(world, monster, target);
            projectile.name = element == "ice" ? "Ice bolt" : element == "acid" ? "Acid glob" : "Fireball";
            projectile.element = element; projectile.damage = damage; projectile.routed = true;
            projectile.GetComponent<Renderer>().sharedMaterial = world.Material(color);
            if (element == "fire")
            {
                var light = projectile.gameObject.AddComponent<Light>(); light.type = LightType.Point; light.color = new Color(1, .5f, .15f); light.range = 4; light.intensity = 2;
            }
            return projectile;
        }
        public static SandboxProjectile Launch(SandboxWorld world, SandboxMonster monster, Vector3 target)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "Committed fireball";
            go.transform.SetParent(world.RoomRoot, true); go.transform.localScale = Vector3.one * .22f;
            monster.Cue("cast");
            // Begin at the eye so the first swept segment includes a nearby wall.
            // Source colliders are filtered below; no forward spawn offset is needed.
            go.transform.position = monster.Eye;
            go.GetComponent<Collider>().enabled = false;
            go.GetComponent<Renderer>().sharedMaterial = world.Material(new Color(1, .3f, .05f));
            var projectile = go.AddComponent<SandboxProjectile>(); projectile.world = world; projectile.source = monster.Entity;
            projectile.velocity = (target - monster.Eye).normalized * 12; projectile.expires = Time.time + 3;
            return projectile;
        }
        private void Update()
        {
            if (world == null || Time.time >= expires) { Destroy(gameObject); return; }
            Vector3 step = velocity * Time.deltaTime;
            var hits = Physics.SphereCastAll(transform.position, .11f, step.normalized, step.magnitude, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var entity = hit.transform.GetComponentInParent<SandboxEntity>();
                if (entity == source) continue;
                if (routed) { world.ProjectileHit(this, entity, source, element, damage, hit.point); Destroy(gameObject); return; }
                if (entity != null) { entity.TakeDamage(35); world.Report("Fireball hit " + entity.DisplayName); }
                else world.Report("Fireball hit solid geometry.");
                Destroy(gameObject); return;
            }
            transform.position += step;
        }
    }
}
