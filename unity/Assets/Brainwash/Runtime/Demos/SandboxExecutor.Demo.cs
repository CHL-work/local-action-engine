using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Demo-scenario half of the executor. The classic sandbox never reaches
    // these methods; they add the rest of the protocol-3 templates, searches,
    // flight, carrying, species skills and triggers. Still no transcript text.
    public sealed partial class SandboxExecutor
    {
        private Dictionary<string, JObject> searches;
        private float searchTurned;
        private bool gazeFrozen;
        private int phase;
        private float phaseStarted;

        // A watched Gargoyle is stone: its order waits and the shared clock pauses.
        public bool HoldsClock => demo && gazeFrozen;

        private SpeciesProfile Profile => monster.Entity.Profile;
        private float WalkSpeed => Mathf.Max(2.6f, (Profile != null ? Profile.Speed : 2.2f) * 1.25f);
        private float RunSpeed => WalkSpeed * 1.6f;
        private float CarryLimit => Profile != null && Profile.Mass >= 250 ? 320 : 80;

        private bool DemoBeginStep()
        {
            switch (template)
            {
                case "move": case "follow": case "flee": case "guard": case "stop": case "pose": case "attack":
                case "use_skill": case "pick_up": case "drop": case "give": case "throw": case "interact":
                case "carry_to": case "eat": case "call_allies": case "trigger": case "use_item": break;
                case "read_scripture": return Fail("there is no scripture here to read");
                default: return Fail("understood_but_not_implemented: " + template);
            }
            var p = Profile;
            if ((string)args["movement"] == "fly" && (p == null || !p.CanFly)) return Fail("this monster cannot fly");
            if (template == "carry_to" && (p == null || !p.CanCarry)) return Fail("this monster cannot carry anything");
            if (template == "eat" && (p == null || !p.CanEat)) return Fail("this monster cannot eat that");
            if (template == "call_allies" && (p == null || !p.CanCallAllies)) return Fail("this monster has no allies to call");
            if (template == "use_skill" && (string)task["recipe"] != "basic_attack_same_target")
            {
                string skill = (string)args["skill"];
                if (p == null || (skill == "strongest" ? p.Skills.Length == 0 : !p.Has(skill))) return Fail("unavailable skill");
            }
            bindings = new Dictionary<string, Target>(); searches = null;
            if (!(task["bindings"] is JObject bindingObjects)) return Fail("missing typed bindings");
            foreach (var pair in bindingObjects)
            {
                var target = ResolveOwnedSelector(pair.Key);
                if (target == null && (string)pair.Value?["kind"] == "search")
                {
                    if (searches == null) searches = new Dictionary<string, JObject>();
                    searches[pair.Key] = (JObject)pair.Value; continue;
                }
                if (target == null) target = DemoResolve(pair.Value as JObject);
                if (target == null) return Fail("missing target/item, stale identity or unsupported vertical direction");
                if (!target.IsPoint) target.Generation = target.Entity.Generation;
                bindings[pair.Key] = target;
            }
            started = Time.realtimeSinceStartup; blockedSeconds = 0; nextAttack = 0; acted = false;
            phase = 0; phaseStarted = started; searchTurned = 0;
            // Commanded movement into a hazard is deliberate ("jump into the pit").
            monster.Reckless = template == "move" && ((string)args["movement"] == "jump" || IsHazard(Get("destination")));
            world.Report("Step " + (index + 1) + ": " + template.Replace('_', ' ') + (searches != null ? " (searching)" : ""));
            if ((string)args["movement"] == "jump") monster.Jump();
            return true;
        }

        private Target DemoResolve(JObject binding)
        {
            if (binding != null && (string)binding["kind"] == "direction" && monster.Flying)
            {
                string axis = (string)binding["reference"]?["axis"];
                if (axis == "up" || axis == "down")
                    return new Target { IsPoint = true, Point = monster.transform.position + (axis == "up" ? Vector3.up : Vector3.down) * 3 };
            }
            var target = Resolve(binding);
            // "Run forward" points 50 m away, past the walls of a demo level: walk to
            // the farthest floor in that direction instead of failing at the wall.
            if (target != null && target.IsPoint && template == "move" && !monster.Flying && (string)binding?["kind"] == "direction" && (string)args["movement"] != "jump")
                target.Point = FarthestWalkable(target.Point);
            return target;
        }

        private Vector3 FarthestWalkable(Vector3 point)
        {
            Vector3 from = monster.transform.position;
            Vector3 delta = Vector3.ProjectOnPlane(point - from, Vector3.up);
            float total = delta.magnitude; if (total < .5f) return point;
            Vector3 direction = delta / total, last = from;
            for (float distance = .5f; distance <= total; distance += .5f)
            {
                Vector3 next = from + direction * distance;
                if (!SandboxNavigation.Clear(monster, last, next, true)) break;
                last = next;
            }
            return Vector3.Distance(last, from) < .6f ? point : last;
        }

        private static bool IsHazard(Target target)
        {
            if (target == null) return false;
            return target.IsPoint ? DemoPhysics.HazardAt(target.Point, true) : target.Entity.Note == "hazard";
        }

        // ---------- Searching ----------
        private bool SearchTick(float dt)
        {
            foreach (var key in new List<string>(searches.Keys))
            {
                var found = FindSearchMatch(searches[key]);
                if (found == null) continue;
                bindings[key] = new Target { Entity = found, Generation = found.Generation };
                searches.Remove(key);
                // A pit found by searching is still a deliberate order to walk into it.
                if (template == "move" && key == "destination") monster.Reckless = (string)args["movement"] == "jump" || IsHazard(bindings[key]);
                world.Report(monster.Entity.DisplayName + " spots the " + found.DisplayName + ".");
            }
            if (searches.Count == 0) { searches = null; started = Time.realtimeSinceStartup; return true; }
            float turn = 360 * dt; searchTurned += turn;
            monster.transform.Rotate(0, turn, 0); monster.SetPose("listening");
            if (searchTurned >= 360) return Fail("searched all around but found no match");
            return true;
        }

        private SandboxEntity FindSearchMatch(JObject search)
        {
            var selector = search["selector"] as JObject;
            var accepts = search["accepts"] as JArray;
            SandboxEntity best = null; float bestDistance = float.PositiveInfinity;
            foreach (var entity in world.Entities)
            {
                if (entity == null || entity == monster.Entity || entity.Owner != null || entity == world.PlayerProp || !entity.gameObject.activeInHierarchy) continue;
                if (!SandboxPerception.InCone(monster.Eye, monster.transform.forward, entity.Center, 60, true)
                    || !SandboxPerception.HasLineOfSight(monster.Eye, entity, monster.transform)) continue;
                if (selector != null ? !MatchesSelector(entity, selector) : !AcceptsDefault(entity, accepts)) continue;
                float distance = Vector3.Distance(entity.transform.position, monster.transform.position);
                if (distance < bestDistance) { best = entity; bestDistance = distance; }
            }
            return best;
        }

        private bool MatchesSelector(SandboxEntity entity, JObject selector)
        {
            if ((string)selector["entity_kind"] != entity.Kind) return false;
            if (entity.Kind == "monster" && !entity.Alive) return false;
            if (selector["filters"] is JArray filters)
                foreach (var filter in filters)
                {
                    string type = (string)filter["type"];
                    if (type == "species" && entity.Species != (string)filter["value"]) return false;
                    if (type == "kind" && (entity.ItemKind ?? entity.ObjectKind) != (string)filter["value"]) return false;
                    if (type == "held_item" && world.HeldBy(entity)?.ItemKind != (string)filter["value"]) return false;
                    if (type == "direction")
                    {
                        Vector3 forward = (string)filter["frame"] == "speaker" ? savedSpeakerForward : savedListenerForward;
                        Vector3 right = Vector3.Cross(Vector3.up, forward);
                        Vector3 delta = Vector3.ProjectOnPlane(entity.transform.position - savedHere, Vector3.up);
                        float f = Vector3.Dot(delta, forward), r = Vector3.Dot(delta, right);
                        string axis = Mathf.Abs(f) >= Mathf.Abs(r) ? (f >= 0 ? "forward" : "back") : (r >= 0 ? "right" : "left");
                        if (axis != (string)filter["axis"]) return false;
                    }
                }
            return true;
        }

        private bool AcceptsDefault(SandboxEntity entity, JArray accepts)
        {
            if (accepts == null) return false;
            bool kindAllowed = false;
            foreach (var kind in accepts) if ((string)kind == entity.Kind) kindAllowed = true;
            if (!kindAllowed) return false;
            if (entity.Kind == "monster" && !entity.Alive && template != "use_skill") return false;
            if ((template == "attack" || template == "throw" || template == "use_item") && entity.Kind == "object" && !entity.Destructible) return false;
            if (template == "interact")
            {
                string operation = (string)args["operation"];
                string expected = operation == "pull" ? "lever" : operation == "push" ? "crate" : "door";
                if (entity.ObjectKind != expected) return false;
            }
            return true;
        }

        // ---------- Movement ----------
        private Vector3 FlyGoal(Target target)
        {
            if (target.IsPoint)
            {
                var point = target.Point;
                point.y = Mathf.Max(point.y, SandboxMonster.GroundHeight(point, monster.transform) + monster.HoverHeight * .6f);
                return point;
            }
            var entity = target.Entity;
            if (entity.ObjectKind == "ledge") return entity.Center + Vector3.up * 1.4f;
            return entity.Center + Vector3.up * (entity == world.PlayerEntity ? 1.5f : .9f);
        }

        private bool DemoMove(Target target, float speed, float dt, float range, out bool failed, bool away = false)
        {
            if (!monster.Flying) return Move(target, speed, dt, range, out failed, away);
            failed = false;
            if (target == null) { failed = true; Fail("missing movement destination"); return false; }
            Vector3 goal = FlyGoal(target);
            if (away)
            {
                Vector3 flee = Vector3.ProjectOnPlane(monster.transform.position - goal, Vector3.up).normalized;
                goal = monster.transform.position + flee * 4; goal.y = monster.transform.position.y;
            }
            bool arrived = monster.FlyToward(goal, speed * 1.15f, dt, Mathf.Max(range, .5f), out float moved);
            blockedSeconds = !arrived && moved < speed * dt * .12f ? blockedSeconds + dt : 0;
            if (blockedSeconds > .8f) { failed = true; Fail("no clear flight path to the target"); }
            return arrived && !away;
        }

        // Walk straight to a hazard's center; only used for explicit orders.
        private bool MoveInto(Target target, float speed, float dt, out bool failed)
        {
            failed = false;
            bool arrived = monster.MoveToward(target.Position, speed, dt, .15f, out float moved);
            blockedSeconds = !arrived && moved < speed * dt * .12f && monster.Grounded ? blockedSeconds + dt : 0;
            if (blockedSeconds > .8f) { failed = true; Fail("cannot reach the hazard"); }
            return arrived;
        }

        // ---------- Main loop ----------
        private bool DemoTick(float dt)
        {
            var eyes = monster.Brain != null ? monster.Brain.Eyes : Profile != null ? Profile.Eyes : EyeRule.Normal;
            gazeFrozen = eyes == EyeRule.Gargoyle && world.PlayerWatching(monster);
            if (gazeFrozen) { started += Time.unscaledDeltaTime; monster.SetPose("statue"); return true; }
            var physics = monster.Comp<PhysicsBody>();
            if (physics != null && physics.Knocked) { started += Time.unscaledDeltaTime; return true; }
            if (searches != null) return SearchTick(dt);
            foreach (var target in bindings.Values)
                if (!target.IsPoint && (!world.IsCurrent(target.Entity) || target.Entity.Generation != target.Generation
                    || (!target.Entity.gameObject.activeInHierarchy && target.Entity.Owner == null))) return Fail("a bound target disappeared");
            bool done = false, failed = false;
            SandboxEntity produced = null;
            float elapsed = Time.realtimeSinceStartup - started;
            switch (template)
            {
                case "move":
                {
                    var destination = Get("destination");
                    float speed = (string)args["movement"] == "run" ? RunSpeed : WalkSpeed;
                    done = IsHazard(destination) && !monster.Flying ? MoveInto(destination, speed, dt, out failed) : DemoMove(destination, speed, dt, .35f, out failed);
                    break;
                }
                case "follow": case "flee":
                {
                    var target = Get(template == "follow" ? "subject" : "threat");
                    if (target == null || (!target.IsPoint && !target.Entity.Alive)) return Fail("target is no longer alive");
                    DemoMove(target, template == "flee" ? RunSpeed : WalkSpeed, dt, 1.1f, out failed, template == "flee");
                    break;
                }
                case "guard":
                    if (!GuardTick(dt, out failed)) return false;
                    break;
                case "stop": monster.SetPose("stopped"); break;
                case "pose":
                {
                    var pose = (string)args["pose"]; monster.SetPose(pose);
                    if (pose == "turn_around" && !acted) { monster.TurnAround(); acted = true; }
                    done = elapsed >= .85f;
                    break;
                }
                case "pick_up":
                {
                    var item = Get("item")?.Entity;
                    if (item == null || item.Kind != "item" || item.Owner != null || world.HeldBy(monster.Entity) != null) return Fail("pickup item unavailable or hand occupied");
                    if (DemoMove(Get("item"), WalkSpeed, dt, monster.Flying ? 1.1f : .8f, out failed))
                    {
                        if (!monster.Flying && !HasSight(Get("item"))) return Fail("item is occluded");
                        item.Attach(monster.Entity, monster.Hand); produced = item; done = true;
                        world.Report(monster.Entity.DisplayName + " picks up the " + item.DisplayName + ".");
                    }
                    break;
                }
                case "drop": case "give": case "throw": case "use_item":
                    if (!TransferTick(dt, ref done, ref produced, out failed)) return false;
                    break;
                case "attack":
                    if (!AttackTick(dt, ref done, out failed, false)) return false;
                    break;
                case "use_skill":
                    if ((string)task["recipe"] == "basic_attack_same_target") { if (!AttackTick(dt, ref done, out failed, true)) return false; }
                    else if (!SkillTick(dt, ref done, out failed)) return false;
                    break;
                case "interact":
                    if (!InteractTick(dt, ref done, out failed)) return false;
                    break;
                case "carry_to":
                    if (!CarryTick(dt, ref done, out failed)) return false;
                    break;
                case "eat":
                {
                    var item = Get("item")?.Entity;
                    if (item == null || item.Kind != "item") return Fail("there is nothing edible");
                    if (item.Owner != null && item.Owner != monster.Entity) return Fail("someone else is holding it");
                    if (item.Owner == monster.Entity || DemoMove(Get("item"), WalkSpeed, dt, monster.Flying ? 1.1f : .7f, out failed))
                    { world.EatItem(monster, item); done = true; }
                    break;
                }
                case "call_allies":
                    if (!acted) { acted = true; monster.SetPose("wave"); int count = world.CallAllies(monster); world.Report(monster.Entity.DisplayName + " howls for help — " + count + " " + (count == 1 ? "ally answers" : "allies answer") + "."); }
                    done = elapsed >= .8f;
                    break;
                case "trigger":
                    if (!TriggerTick(ref done, elapsed)) return false;
                    break;
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
                world.Report("Completed step " + (index + 1) + ": " + template.Replace('_', ' ') + (produced != null ? " [" + produced.Handle + "]" : ""));
                monster.Reckless = false;
                index++; bindings = null;
            }
            return true;
        }

        // Guard: stay beside the ward and strike anything that threatens it.
        // Guarding never fails for lack of a path; it simply holds position.
        private bool GuardTick(float dt, out bool failed)
        {
            failed = false;
            var ward = Get("ward");
            if (ward == null || (!ward.IsPoint && !ward.Entity.Alive)) return Fail("nothing left to guard");
            Vector3 center = ward.Position;
            SandboxMonster threat = null; float closest = 4.5f;
            foreach (var other in world.Monsters)
            {
                // Monsters you are brainwashing are not threats while they obey.
                if (other == null || other == monster || !other.Entity.Alive || world.IsControlled(other) || (!ward.IsPoint && other.Entity == ward.Entity)) continue;
                float distance = Vector3.Distance(other.transform.position, center);
                bool hostile = other.Brain != null && (other.Brain.Target == world.PlayerEntity || (!ward.IsPoint && other.Brain.Target == ward.Entity)
                    || (other.Entity.Profile != null && other.Entity.Profile.Aggressive));
                if (hostile && distance < closest) { threat = other; closest = distance; }
            }
            if (threat != null)
            {
                float reach = (Profile?.Reach ?? 1) + monster.Motor.radius + threat.Motor.radius + .1f;
                if (SoftMove(threat.transform.position, RunSpeed, dt, reach) && Time.realtimeSinceStartup >= nextAttack)
                {
                    FaceToward(threat.transform.position);
                    nextAttack = Time.realtimeSinceStartup + Mathf.Max(.6f, (Profile?.AttackCooldown ?? 1.6f) * .55f);
                    monster.SetPose("attacking"); world.MeleeHit(monster, threat.Entity, true);
                }
                return true;
            }
            float hold = ward.IsPoint ? .4f : 1.4f + monster.Motor.radius + (ward.Entity.Comp<SandboxMonster>() != null ? ward.Entity.Comp<SandboxMonster>().Motor.radius : .35f);
            // A bodyguard stands at the player's right shoulder, not in their view.
            Vector3 post = center;
            if (!ward.IsPoint && ward.Entity == world.PlayerEntity) { post = world.Player.position + world.Player.right * hold - world.Player.forward * .4f; hold = .5f; }
            if (SoftMove(post, WalkSpeed, dt, hold)) monster.SetPose("guarding");
            return true;
        }

        private bool SoftMove(Vector3 point, float speed, float dt, float range)
        {
            if (monster.Flying) return monster.FlyToward(point + Vector3.up * .9f, speed, dt, range + .6f, out _);
            return monster.MoveToward(point, speed, dt, range, out _);
        }

        private bool AttackTick(float dt, ref bool done, out bool failed, bool single)
        {
            failed = false;
            var target = Get("target"); if (target == null) return Fail("missing attack target");
            if (!target.IsPoint && !target.Entity.Alive) return Fail("attack target is no longer alive");
            if (!target.IsPoint && target.Entity.Kind == "object" && !target.Entity.Destructible) return Fail("target cannot be damaged");
            if (!target.IsPoint && target.Entity == monster.Entity)
            {
                world.MeleeHit(monster, monster.Entity, true); done = true; return true;
            }
            float reach = (Profile?.Reach ?? 1f) + .25f;
            if (DemoMove(target, RunSpeed * .85f, dt, reach, out failed))
            {
                if (!HasSight(target)) return Fail("attack target is now occluded");
                if (Time.realtimeSinceStartup >= nextAttack)
                {
                    nextAttack = Time.realtimeSinceStartup + Mathf.Max(.55f, (Profile?.AttackCooldown ?? 1.6f) * .5f);
                    monster.SetPose("attacking");
                    if (!target.IsPoint) world.MeleeHit(monster, target.Entity, true);
                    else world.Report(monster.Entity.DisplayName + " swings at empty air.");
                    string completion = (string)task["completion"];
                    done = single || completion == "one_activation" || completion == "one_attack_cycle" || (!target.IsPoint && !target.Entity.Alive);
                }
            }
            return true;
        }

        private string StrongestSkill()
        {
            foreach (var skill in new[] { "ultimate", "fireball", "ice_bolt", "acid_pool" })
                if (Profile != null && Profile.Has(skill)) return skill;
            return null;
        }

        private bool SkillTick(float dt, ref bool done, out bool failed)
        {
            failed = false;
            string skill = (string)args["skill"];
            if (skill == "strongest") skill = StrongestSkill();
            if (skill == null) return Fail("no offensive skill");
            var target = Get("target"); if (target == null) return Fail("missing skill target");
            Vector3 aim = target.IsPoint ? target.Point + Vector3.up : target.Entity.Center;
            float distance = Vector3.Distance(monster.transform.position, target.Position);
            // Direction targets resolve to a point far beyond the room: aim that
            // way from here instead of walking toward it.
            Vector3 heading = Vector3.ProjectOnPlane(target.Position - monster.transform.position, Vector3.up);
            if (target.IsPoint && heading.magnitude > 12) aim = monster.Eye + heading.normalized * 12;
            switch (skill)
            {
                case "fireball": case "ice_bolt":
                {
                    if (!target.IsPoint && !target.Entity.Alive) return Fail("skill target is no longer alive");
                    if (!target.IsPoint && distance > 13 && !DemoMove(target, WalkSpeed, dt, 10, out failed)) return true;
                    if (!target.IsPoint && !HasSight(target)) { DemoMove(target, WalkSpeed, dt, 1.5f, out failed); return true; }
                    FaceToward(target.IsPoint ? monster.transform.position + heading : target.Position);
                    if (skill == "fireball" && Time.time < monster.FireballReadyAt) return true;
                    monster.SetPose("fireball");
                    bool fire = skill == "fireball";
                    SandboxProjectile.Launch(world, monster, aim, fire ? "fire" : "ice", fire ? 28 : 22, fire ? new Color(1, .35f, .05f) : new Color(.6f, .9f, 1));
                    if (fire) monster.FireballReadyAt = Time.time + 1.5f;
                    world.Report((fire ? "Fireball" : "Ice bolt") + " → " + (target.IsPoint ? "that direction" : target.Entity.DisplayName));
                    done = true; return true;
                }
                case "acid_pool":
                {
                    Vector3 spot = target.Position;
                    if (target.IsPoint) spot = monster.transform.position + heading.normalized * Mathf.Min(heading.magnitude, 6);
                    else if (distance > 8 && !DemoMove(target, WalkSpeed, dt, 7, out failed)) return true;
                    FaceToward(spot); monster.SetPose("fireball"); monster.Cue("cast");
                    world.SpawnAcidPool(spot, 1.5f, 12, 10, monster.Entity);
                    world.Report(monster.Entity.DisplayName + " sprays a pool of acid.");
                    done = true; return true;
                }
                case "heal":
                {
                    if (target.IsPoint || !target.Entity.Alive) return Fail("heal needs a living target");
                    if (target.Entity != monster.Entity && !DemoMove(target, WalkSpeed, dt, 2.2f, out failed)) return true;
                    monster.SetPose("heal"); monster.Cue("heal");
                    target.Entity.Heal(45);
                    world.Effects.Pulse(target.Entity.Center, new Color(.4f, 1, .5f), 1.3f);
                    world.Report(monster.Entity.DisplayName + " heals " + (target.Entity == world.PlayerEntity ? "you" : target.Entity.DisplayName) + " (+45).");
                    done = true; return true;
                }
                case "revive":
                {
                    if (target.IsPoint || target.Entity.Kind != "remains") return Fail("revive needs fallen remains");
                    if (!DemoMove(target, WalkSpeed, dt, 1.8f, out failed)) return true;
                    monster.SetPose("heal"); monster.Cue("heal");
                    if (!world.ReviveRemains(target.Entity, monster)) return Fail("those remains cannot be revived");
                    done = true; return true;
                }
                case "ultimate":
                {
                    string species = monster.Entity.Species;
                    if (species == "mouse")
                    {
                        if (target.IsPoint) return Fail("the rat tide needs a creature to swarm");
                        world.RatTide(target.Entity, monster); done = true; return true;
                    }
                    if (species == "ogre")
                    {
                        if (!target.IsPoint && !DemoMove(target, RunSpeed, dt, 2.2f, out failed)) return true;
                        monster.SetPose("attacking"); world.GroundSlam(monster); done = true; return true;
                    }
                    if (target.IsPoint) return Fail("no target for the ultimate");
                    if (!DemoMove(target, RunSpeed, dt, (Profile?.Reach ?? 1) + .3f, out failed)) return true;
                    monster.SetPose("attacking"); world.MeleeHit(monster, target.Entity, true, 2.5f); done = true; return true;
                }
            }
            return Fail("unavailable skill");
        }

        private void FaceToward(Vector3 point)
        {
            Vector3 toward = Vector3.ProjectOnPlane(point - monster.transform.position, Vector3.up);
            if (toward.sqrMagnitude > .001f) monster.transform.rotation = Quaternion.LookRotation(toward);
        }

        private bool TransferTick(float dt, ref bool done, ref SandboxEntity produced, out bool failed)
        {
            failed = false;
            var item = Get("item")?.Entity;
            if (item == null || item.Owner != monster.Entity || world.HeldBy(monster.Entity) != item) return Fail("the exact requested item is no longer held");
            var recipient = Get(template == "give" ? "recipient" : "target");
            if (template == "give")
            {
                if (recipient == null || recipient.IsPoint || !recipient.Entity.Alive) return Fail("recipient is unavailable");
                if (!DemoMove(recipient, WalkSpeed, dt, 1.3f, out failed)) return true;
                if (!monster.Flying && !HasSight(recipient)) return Fail("recipient is behind an obstruction");
                if (world.HeldBy(recipient.Entity) != null) return Fail("recipient hand is occupied");
                var socket = recipient.Entity.HandSocket;
                item.Attach(recipient.Entity, socket != null ? socket : recipient.Entity.transform);
                if (socket == null) item.transform.localPosition = new Vector3(.4f, 1, .4f);
                if (recipient.Entity == world.PlayerEntity) world.Scenario?.OnPlayerPickedUp(item);
                world.Report(monster.Entity.DisplayName + " hands over the " + item.DisplayName + ".");
            }
            else if (template == "drop")
            {
                if (monster.Flying)
                {
                    item.Drop(world.RoomRoot, monster.Hand.position + Vector3.down * .3f);
                    if (item.Comp<PhysicsProp>() == null) PhysicsProp.Make(item, 1);
                }
                else if (!TryDropHeldItem(world, monster, out var reason)) return Fail(reason);
            }
            else
            {
                if (recipient == null) return Fail("missing throw destination");
                Vector3 aim = recipient.IsPoint ? recipient.Point : recipient.Entity.Center;
                if (Vector3.Distance(aim, monster.transform.position) > 14) aim = monster.transform.position + (aim - monster.transform.position).normalized * 14;
                FaceToward(aim);
                Vector3 from = monster.Hand.position + Vector3.up * .2f;
                item.Drop(world.RoomRoot, from);
                var physics = item.Comp<PhysicsProp>(); if (physics == null) physics = PhysicsProp.Make(item, 1);
                physics.Throw(DemoPhysics.Ballistic(from, aim, 1), monster.Entity);
                monster.SetPose("attacking");
                world.Report(monster.Entity.DisplayName + " throws the " + item.DisplayName + ".");
            }
            world.Scenario?.OnItemFreed(item, "commanded", monster.Entity);
            produced = item; done = true;
            return true;
        }

        private bool InteractTick(float dt, ref bool done, out bool failed)
        {
            failed = false;
            var target = Get("object")?.Entity; string operation = (string)args["operation"];
            string expected = operation == "pull" ? "lever" : operation == "push" ? "crate" : "door";
            if (target == null) return Fail("missing object");
            var prop = target.Comp<PhysicsProp>();
            if (target.ObjectKind != expected && !(operation == "push" && prop != null)) return Fail("the " + target.DisplayName + " cannot be " + (operation == "pull" ? "pulled" : operation + "ed"));
            if (!DemoMove(Get("object"), WalkSpeed, dt, 1.4f, out failed)) return true;
            if (!HasSight(Get("object"))) return Fail("interaction is blocked");
            if (operation == "push")
            {
                Vector3 direction = Vector3.ProjectOnPlane(target.transform.position - monster.transform.position, Vector3.up).normalized;
                if (prop != null) prop.Throw(direction * (Profile != null && Profile.Mass >= 250 ? 8 : 4) + Vector3.up * .6f, monster.Entity);
                else
                {
                    foreach (var hit in Physics.BoxCastAll(target.Center, target.Body.bounds.extents * .9f, direction, target.transform.rotation, .7f, ~0, QueryTriggerInteraction.Ignore))
                        if (hit.transform != target.transform && !hit.transform.IsChildOf(target.transform) && !hit.transform.IsChildOf(monster.transform)) return Fail("crate cannot be pushed through an obstacle");
                    target.transform.position += direction * .7f;
                }
            }
            else if (expected == "door")
            {
                if (target.Note == "locked" && operation == "open") return Fail("the door is locked");
                target.SetOpen(operation == "open");
            }
            else target.SetOpen(!target.Open);
            world.Scenario?.OnInteracted(target, operation, monster);
            done = true;
            return true;
        }

        // Carry: reach the cargo, lift it, then fly it there (Harpy) or hurl it (Ogre).
        private bool CarryTick(float dt, ref bool done, out bool failed)
        {
            failed = false;
            var cargo = Get("cargo"); var destination = Get("destination");
            if (cargo == null || destination == null) return Fail("missing cargo or destination");
            if (cargo.IsPoint) return Fail("a place cannot be carried");
            var load = cargo.Entity;
            if (load == monster.Entity) return Fail("it cannot carry itself");
            float mass = load.Profile != null ? load.Profile.Mass : load == world.PlayerEntity ? 70 : load.Kind == "item" ? 1 : load.Comp<PhysicsProp>()?.Body != null ? load.Comp<PhysicsProp>().Body.mass : 999;
            if (load.Kind == "object" && !load.Grabbable) return Fail("the " + load.DisplayName + " is fixed in place");
            if (mass > CarryLimit) return Fail("the " + load.DisplayName + " is too heavy to carry");
            if (phase == 0)
            {
                if (load.Kind == "monster" && !load.Alive) return Fail("cargo is gone");
                if (!DemoMove(cargo, RunSpeed, dt, monster.Flying ? 1.2f : 1.1f, out failed)) return true;
                // A tumbling body is gripped once it lands.
                var tumbling = load.Comp<PhysicsBody>();
                if (tumbling != null && tumbling.Knocked) return true;
                if (!world.BeginCarry(monster, load, true)) return Fail("cannot get a grip on the " + load.DisplayName);
                world.CarryGoal = monster.transform.position;
                phase = 1; phaseStarted = Time.realtimeSinceStartup; blockedSeconds = 0;
                return true;
            }
            if (world.Carrier != monster) { done = true; return true; }
            // Ledges are landed on; creatures are hit by thrown cargo but a carried
            // player is set down beside them; doors and other solids are approached
            // from the carrier's side.
            Vector3 goal;
            if (destination.IsPoint) goal = destination.Point;
            else if (destination.Entity.ObjectKind == "ledge") goal = destination.Entity.Center + Vector3.up * .3f;
            else if (load != world.PlayerEntity && (destination.Entity.Kind == "monster" || destination.Entity.Kind == "player")) goal = destination.Entity.transform.position;
            else goal = world.LandingNear(destination.Entity, monster.transform.position);
            // A pit, or a creature hit by thrown cargo, is aimed at exactly.
            bool exact = !destination.IsPoint && (destination.Entity.Note == "hazard"
                || (load != world.PlayerEntity && (destination.Entity.Kind == "monster" || destination.Entity.Kind == "player")));
            if (!monster.Flying)
            {
                // Ogres do not walk across chasms: anything farther than a few steps is thrown.
                float flat = Vector3.ProjectOnPlane(goal - monster.transform.position, Vector3.up).magnitude;
                if (flat > 3.2f || destination.IsPoint == false && destination.Entity.ObjectKind == "ledge")
                {
                    if (Time.realtimeSinceStartup - phaseStarted < .35f) { FaceToward(goal); monster.SetPose("attacking"); return true; }
                    world.TossCarried(monster, goal, exact);
                    done = true; return true;
                }
                if (!Move(destination, WalkSpeed, dt, 1.1f, out failed)) return true;
                world.ReleaseCarry(Vector3.zero); done = true; return true;
            }
            // A carried player hangs 1.95 m below the Harpy: hover high enough to
            // set them down on the floor, not in it.
            Vector3 hover = goal + Vector3.up * 2.05f;
            world.CarryGoal = hover;
            float flightSpeed = (Profile?.Speed ?? 4) * 1.5f;
            bool arrived = monster.FlyToward(hover, flightSpeed, dt, .45f, out float moved);
            // Measure progress relative to the requested step, as walking does.
            // A fixed distance per frame mistakes smooth high-FPS flight for a wall.
            blockedSeconds = !arrived && moved < flightSpeed * dt * .12f ? blockedSeconds + dt : 0;
            if (blockedSeconds > 1f) { world.ReleaseCarry(Vector3.zero); return Fail("no clear flight path with the cargo"); }
            if (arrived) { world.ReleaseCarry(Vector3.down * .5f); done = true; world.Report(monster.Entity.DisplayName + " sets down " + (load == world.PlayerEntity ? "you" : "the " + load.DisplayName) + "."); }
            return true;
        }

        private bool TriggerTick(ref bool done, float elapsed)
        {
            string trigger = (string)args["trigger"];
            if (!acted)
            {
                acted = true;
                if (trigger == "cheese_theft")
                {
                    var target = Get("target");
                    if (target == null || target.IsPoint) return Fail("nobody to accuse");
                    monster.SetPose("attacking");
                    world.RatTide(target.Entity, monster);
                }
                else if (trigger == "hidden_gift") { monster.SetPose("attacking"); world.Scenario?.OnHiddenGift(monster); }
                else if (trigger == "dragon_delusion") world.DragonBreath(monster);
                else return Fail("unknown trigger");
                world.Scenario?.OnTriggered(trigger, monster);
            }
            done = elapsed >= (trigger == "dragon_delusion" ? 1.4f : .9f);
            return true;
        }
    }
}
