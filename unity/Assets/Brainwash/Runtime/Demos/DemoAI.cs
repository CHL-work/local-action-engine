using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // TDD §10 per-(victim, attacker) retaliation record.
    public sealed class Grudge
    {
        public int Stage;
        public float StageStart, LastHit, LastSeen;
        public bool RetaliationPending;
    }

    // Normal-state behaviour for demo monsters. The world never ticks this
    // while its monster owns a Listening/Thinking/Brainwashed lease.
    public sealed class DemoAI
    {
        readonly SandboxMonster monster;
        readonly SandboxWorld world;
        public Vector3 Home;
        readonly Dictionary<SandboxEntity, Grudge> grudges = new Dictionary<SandboxEntity, Grudge>();
        float nextAttack, nextSkill, nextPatrol, alertUntil, staggerUntil, confusedUntil, rallyUntil, noiseUntil, fleeUntil, nextHeal, nextThrow;
        float fuseAt, nextFusePulse;
        Vector3 patrolPoint, lastSeenPlayer, rallyPoint, noisePoint;
        SandboxEntity fleeFrom;
        public string State { get; private set; } = "Idle";
        public SandboxEntity Target { get; private set; }
        public bool CoveringFace { get; private set; }
        public bool Petrified { get; private set; }
        public bool Passive;              // scenario can freeze a monster's normal AI (statues, sleeping guards)
        public bool GuardPost;            // stays near Home unless alerted
        public float Aggro = 1;           // multiplier on sight range
        public SandboxEntity ForcedEnemy; // scenario-scripted rivalry (rare relationships)
        public float Leash;               // guards return home when a chase strays this far
        public bool Snatcher;             // harpies may grab the player and fly off with them
        public EyeRule Eyes;              // per-individual eye behaviour (a shy goblin, a gargoyle)
        float snatchUntil;
        Vector3 snatchGoal;
        bool returning;
        public IReadOnlyDictionary<SandboxEntity, Grudge> Grudges => grudges;

        SpeciesProfile P => monster.Entity.Profile;
        public DemoAI(SandboxMonster monster, SandboxWorld world)
        {
            this.monster = monster; this.world = world; Home = monster.transform.position; patrolPoint = Home;
            Eyes = monster.Entity.Profile != null ? monster.Entity.Profile.Eyes : EyeRule.Normal;
        }

        public void Stagger(float seconds) { staggerUntil = Mathf.Max(staggerUntil, Time.time + seconds); }
        public void Alert(Vector3 position) { lastSeenPlayer = position; alertUntil = Time.time + 8; }
        public void Rally(Vector3 point, float seconds) { rallyPoint = point; rallyUntil = Time.time + seconds; }
        // Noise turns heads. Guards (not shy or timid ones) also go to look.
        public void HearNoise(Vector3 point, float seconds)
        {
            noisePoint = point; noiseUntil = Time.time + seconds;
            if (monster.Entity.Asleep && P.Id != "mimic") { monster.Entity.Asleep = false; world.Report(monster.Entity.DisplayName + " wakes up!"); }
            if (Eyes != EyeRule.Shy && !P.Timid && !Passive && Aggro > 0) { lastSeenPlayer = point; alertUntil = Mathf.Max(alertUntil, Time.time + seconds + 2); }
        }
        public void Flee(SandboxEntity from, float seconds) { fleeFrom = from; fleeUntil = Time.time + seconds; }
        public void ResumeAfterControl() { nextAttack = Time.time + .8f; }
        public bool HasEnemy(SandboxEntity attacker) => attacker != null && grudges.TryGetValue(attacker, out var g) && g.Stage >= 3;
        public int StageAgainst(SandboxEntity attacker) => attacker != null && grudges.TryGetValue(attacker, out var g) ? g.Stage : 0;

        // Brainwash resets only this victim's outgoing records (TDD §10).
        public void ClearGrudges() { grudges.Clear(); }

        // Called for every damage event whose direct attacker is another monster.
        public void OnHitByMonster(SandboxEntity attacker)
        {
            if (attacker == null || attacker == monster.Entity || !monster.Entity.Alive) return;
            float now = Time.time;
            if (!grudges.TryGetValue(attacker, out var g)) { g = new Grudge(); grudges[attacker] = g; }
            if (g.Stage == 0) { g.Stage = 1; g.StageStart = now; confusedUntil = now + .7f; world.Report(monster.Entity.DisplayName + " looks around, confused (1/3 vs " + attacker.DisplayName + ")."); }
            else if (g.Stage == 1 && now >= g.StageStart + 2) { g.Stage = 2; g.StageStart = now; g.RetaliationPending = true; world.Report(monster.Entity.DisplayName + " shoves back at " + attacker.DisplayName + " (2/3)."); }
            else if (g.Stage == 2 && now >= g.StageStart + 3) { g.Stage = 3; g.StageStart = now; world.Report(monster.Entity.DisplayName + " is now ANGRY at " + attacker.DisplayName + " (3/3)."); }
            g.LastHit = now; g.LastSeen = now;
        }

        void DecayGrudges()
        {
            if (grudges.Count == 0) return;
            float now = Time.time;
            List<SandboxEntity> drop = null;
            foreach (var pair in grudges)
            {
                var attacker = pair.Key; var g = pair.Value;
                bool gone = attacker == null || !attacker.Alive || !world.IsCurrent(attacker);
                if (!gone && SandboxPerception.Perceived(monster, attacker)) g.LastSeen = now;
                // Records calm down 10 s after the last hit; an enemy also ends
                // 10 s after the attacker leaves perception (TDD §10).
                if (gone || now - g.LastHit > 10 || (g.Stage >= 3 && now - g.LastSeen > 10))
                    (drop ??= new List<SandboxEntity>()).Add(attacker);
            }
            if (drop != null) foreach (var key in drop) grudges.Remove(key);
        }

        bool Sees(SandboxEntity entity, float range)
        {
            if (entity == null || !entity.Alive) return false;
            float distance = Vector3.Distance(entity.transform.position, monster.transform.position);
            if (distance > range) return false;
            bool near = distance <= SandboxPerception.NearRadius;
            return (near || SandboxPerception.InCone(monster.Eye, monster.transform.forward, entity.Center, 65, true))
                && SandboxPerception.HasLineOfSight(monster.Eye, entity, monster.transform);
        }

        public void Tick(float dt)
        {
            var me = monster.Entity;
            if (!me.Alive || world.GameOver || world.Victory || world.IsControlled(monster)) return;
            // A lit bomb toad bursts on time, even mid-flight after a kick.
            if (fuseAt > 0)
            {
                if (Time.time >= fuseAt) { fuseAt = 0; world.Explode(me.Center, 3.4f, 55, me, me); return; }
                if (Time.time >= nextFusePulse) { nextFusePulse = Time.time + .35f; world.Effects.Pulse(me.Center, new Color(1, .35f, .1f), 1.2f); }
            }
            var physics = monster.Comp<PhysicsBody>();
            if (physics != null && physics.Knocked) { State = "Knocked"; return; }
            DecayGrudges();
            float now = Time.time;
            CoveringFace = false; Petrified = false;
            if (Passive) { State = "Waiting"; monster.SetPose("idle"); return; }
            if (world.CarryCommitted && world.Carrier == monster) { State = "Finishing a carry"; return; }
            if (world.Carrier == monster)
            {
                // Carrying a snatched player away from where they wanted to be.
                State = "Snatching you";
                bool there = monster.FlyToward(snatchGoal, P.Speed, dt, .6f, out _);
                if (there || now >= snatchUntil) { world.ReleaseCarry(Vector3.down, monster.Entity); nextAttack = now + 3; }
                return;
            }
            if (now < staggerUntil) { State = "Staggered"; monster.SetPose("confused"); return; }
            if (now < confusedUntil) { State = "Confused"; monster.SetPose("confused"); return; }
            if (me.Asleep)
            {
                State = "Asleep"; monster.SetPose("sleep");
                if (Vector3.Distance(world.Player.position, monster.transform.position) < 1.3f && P.Id != "mimic") { me.Asleep = false; world.Report(me.DisplayName + " wakes up!"); Alert(world.Player.position); }
                return;
            }
            float range = P.SightRange * Aggro;
            bool seesPlayer = world.PlayerEntity.Alive && Sees(world.PlayerEntity, range);
            if (seesPlayer) lastSeenPlayer = world.Player.position;

            // Eye-contact behaviours that change with being watched.
            bool watched = world.PlayerWatching(monster);
            if (Eyes == EyeRule.Gargoyle && watched)
            { Petrified = true; State = "Statue"; monster.SetPose("statue"); return; }
            if (Eyes == EyeRule.Shy)
            {
                if (now < noiseUntil) { Face(noisePoint, dt); State = "Distracted"; monster.SetPose("idle"); return; }
                if (watched && Vector3.Distance(world.Player.position, monster.transform.position) < 9) { CoveringFace = true; State = "Hiding face"; monster.SetPose("cover"); return; }
            }
            if (now < noiseUntil) { Face(noisePoint, dt); State = "Listening"; monster.SetPose("idle"); }

            if (fleeFrom != null && now < fleeUntil && fleeFrom.Alive)
            { State = "Fleeing"; Go(fleeFrom.transform.position, P.Speed * 1.1f, dt, 0, true); return; }

            // Species specials.
            if (P.Id == "mimic") { MimicTick(dt, seesPlayer); return; }
            if (P.Id == "mouse" && MouseTick(dt)) return;
            if (P.Id == "nurse" && NurseTick(dt, seesPlayer)) return;

            // One retaliation at stage 2, then normal behaviour.
            foreach (var pair in grudges)
            {
                var g = pair.Value;
                if (!g.RetaliationPending || pair.Key == null || !pair.Key.Alive) continue;
                if (Approach(pair.Key, dt))
                {
                    Strike(pair.Key, true); g.RetaliationPending = false;
                }
                State = "Retaliating"; return;
            }

            if (returning)
            {
                if (Vector3.ProjectOnPlane(monster.transform.position - Home, Vector3.up).magnitude < 1) returning = false;
                else { State = "Returning to post"; Go(Home, P.Speed, dt, .5f); return; }
            }
            Target = ChooseTarget(range, seesPlayer);
            if (Target == null && now < rallyUntil)
            {
                State = "Answering a call"; Go(rallyPoint, P.Speed, dt, 1.2f); return;
            }
            if (Target == null && now < alertUntil && !GuardPost)
            {
                State = "Searching"; Go(lastSeenPlayer, P.Speed, dt, .5f); return;
            }
            if (Target == null) { Patrol(dt); return; }

            if (Target == world.PlayerEntity) Alert(world.Player.position);
            if (Leash > 0 && Vector3.Distance(monster.transform.position, Home) > Leash)
            {
                State = "Returning to post"; Target = null; alertUntil = 0; returning = true;
                Go(Home, P.Speed, dt, .5f); return;
            }
            if (TryRanged(Target)) return;
            if (Approach(Target, dt)) { State = "Attacking"; if (now >= nextAttack) Strike(Target, false); else monster.SetPose("attacking"); }
            else State = Target == world.PlayerEntity ? "Chasing you" : "Chasing " + Target.DisplayName;
        }

        SandboxEntity ChooseTarget(float range, bool seesPlayer)
        {
            if (ForcedEnemy != null && ForcedEnemy.Alive && Sees(ForcedEnemy, range * 1.5f)) return ForcedEnemy;
            SandboxEntity enemy = null; float best = float.PositiveInfinity;
            foreach (var pair in grudges)
            {
                if (pair.Value.Stage < 3 || pair.Key == null || !pair.Key.Alive) continue;
                float d = Vector3.Distance(pair.Key.transform.position, monster.transform.position);
                if (d < best) { best = d; enemy = pair.Key; }
            }
            if (enemy != null) return enemy;
            if (P.Timid) return null;
            if (P.Aggressive)
            {
                SandboxEntity nearest = seesPlayer ? world.PlayerEntity : null;
                best = seesPlayer ? Vector3.Distance(world.Player.position, monster.transform.position) : float.PositiveInfinity;
                foreach (var other in world.Monsters)
                {
                    if (other == monster || other == null || !other.Entity.Alive || other.Entity.Species == monster.Entity.Species) continue;
                    if (!Sees(other.Entity, range)) continue;
                    float d = Vector3.Distance(other.transform.position, monster.transform.position);
                    if (d < best) { best = d; nearest = other.Entity; }
                }
                return nearest;
            }
            // Bioweapons and non-aggressive mutants proactively hunt only believers.
            if (seesPlayer || Time.time < alertUntil && Vector3.Distance(lastSeenPlayer, monster.transform.position) < 2.5f && Sees(world.PlayerEntity, range * 1.4f))
                return world.PlayerEntity;
            return null;
        }

        bool Approach(SandboxEntity target, float dt)
        {
            float reach = P.Reach + monster.Motor.radius + (target.Comp<SandboxMonster>()?.Motor.radius ?? .35f);
            float distance = Vector3.ProjectOnPlane(target.transform.position - monster.transform.position, Vector3.up).magnitude;
            if (distance <= reach)
            {
                Face(target.transform.position, dt);
                return SandboxPerception.HasLineOfSight(monster.Eye, target, monster.transform);
            }
            if (monster.Flying) monster.FlyToward(target.Center, P.Speed, dt, reach * .8f, out _);
            else monster.MoveToward(target.transform.position, P.Speed, dt, reach * .9f, out _);
            return false;
        }

        // Ground monsters path on the floor grid; flyers move freely at hover height.
        bool Go(Vector3 point, float speed, float dt, float range, bool away = false)
        {
            if (!monster.Flying) return monster.MoveToward(point, speed, dt, range, out _, away);
            Vector3 goal = away ? monster.transform.position + Vector3.ProjectOnPlane(monster.transform.position - point, Vector3.up).normalized * 4 : point;
            goal.y = SandboxMonster.GroundHeight(goal, monster.transform) + monster.HoverHeight;
            return monster.FlyToward(goal, speed, dt, Mathf.Max(range, .4f), out _) && !away;
        }

        void Face(Vector3 point, float dt)
        {
            Vector3 toward = Vector3.ProjectOnPlane(point - monster.transform.position, Vector3.up);
            if (toward.sqrMagnitude > .001f) monster.transform.rotation = Quaternion.RotateTowards(monster.transform.rotation, Quaternion.LookRotation(toward), 320 * dt);
        }

        void Strike(SandboxEntity target, bool retaliation)
        {
            nextAttack = Time.time + P.AttackCooldown;
            monster.SetPose("attacking");
            monster.Cue(P.Id == "bomb_toad" ? "swell" : "strike");
            if (Snatcher && target == world.PlayerEntity && world.Carrier == null && Random.value < .6f && world.BeginCarry(monster, target))
            {
                Vector2 away = Random.insideUnitCircle.normalized * 9;
                snatchGoal = new Vector3(world.Player.position.x + away.x, monster.transform.position.y + 1.5f, world.Player.position.z + away.y);
                snatchGoal = SandboxNavigation.ClampToBounds(snatchGoal);
                snatchUntil = Time.time + 2.6f;
                world.Report(monster.Entity.DisplayName + " snatches you into the air!");
                return;
            }
            if (P.Id == "bomb_toad")
            {
                if (fuseAt <= 0) { fuseAt = Time.time + 1.2f; State = "About to burst!"; world.Report(monster.Entity.DisplayName + " swells up. Kick it away!"); }
                return;
            }
            float damage = retaliation ? Mathf.Max(3, P.Damage * .5f) : P.Damage;
            string element = P.Id == "ice_golem" ? "ice" : "physical";
            if (target == world.PlayerEntity) world.DamagePlayer(damage, monster.Entity.DisplayName);
            else target.TakeDamage(damage, monster.Entity, element);
            if (P.Mass >= 250)
            {
                Vector3 push = Vector3.ProjectOnPlane(target.transform.position - monster.transform.position, Vector3.up).normalized * 5f + Vector3.up * 2.2f;
                var targetMonster = target.Comp<SandboxMonster>();
                if (targetMonster != null && target.Alive) PhysicsBody.For(targetMonster).Knock(push * DemoPhysics.MassScale(target, 80), monster.Entity);
                else if (target == world.PlayerEntity) world.KnockPlayer(push * .7f);
            }
        }

        bool TryRanged(SandboxEntity target)
        {
            if (Time.time < nextSkill) return false;
            float distance = Vector3.Distance(target.transform.position, monster.transform.position);
            if (!SandboxPerception.HasLineOfSight(monster.Eye, target, monster.transform)) return false;
            if (P.Has("fireball") && distance > 3 && distance < 10)
            {
                nextSkill = Time.time + 3.6f; monster.SetPose("fireball");
                SandboxProjectile.Launch(world, monster, target.Center, "fire", 22, new Color(1, .35f, .05f));
                return true;
            }
            if (P.Has("ice_bolt") && distance > 3.5f && distance < 11)
            {
                nextSkill = Time.time + 5.5f; monster.SetPose("fireball");
                SandboxProjectile.Launch(world, monster, target.Center, "ice", 18, new Color(.6f, .9f, 1));
                return true;
            }
            if (P.Id == "ogre" && Time.time >= nextThrow && distance > 3.5f && distance < 12)
            {
                var prop = world.NearestGrabbable(monster.transform.position, 3.2f);
                if (prop != null)
                {
                    nextThrow = Time.time + 6; nextSkill = Time.time + 1.2f; monster.SetPose("attacking");
                    world.MonsterThrowProp(monster, prop, target.Center);
                    return true;
                }
            }
            return false;
        }

        void Patrol(float dt)
        {
            State = GuardPost ? "Guarding" : "Patrol";
            if (Time.time >= nextPatrol)
            {
                nextPatrol = Time.time + Random.Range(5f, 9f);
                Vector2 offset = Random.insideUnitCircle * (GuardPost ? 1.2f : 3f);
                patrolPoint = Home + new Vector3(offset.x, 0, offset.y);
            }
            bool arrived = monster.Flying
                ? monster.FlyToward(new Vector3(patrolPoint.x, Home.y, patrolPoint.z), P.Speed * .4f, dt, .4f, out _)
                : monster.MoveToward(patrolPoint, P.Speed * .45f, dt, .35f, out _);
            // Between walks, a neutral monster watches a nearby player: curious, and a
            // chance for the player to meet its eyes.
            if (arrived && Aggro <= 0 && Vector3.Distance(world.Player.position, monster.transform.position) < 14) Face(world.Player.position, dt);
        }

        bool MouseTick(float dt)
        {
            var cheese = world.NearestItem(monster.transform.position, "cheese", 9, monster);
            if (cheese != null)
            {
                State = "Smells cheese";
                if (monster.MoveToward(cheese.transform.position, P.Speed, dt, .5f, out _))
                { world.EatItem(monster, cheese); }
                return true;
            }
            float player = Vector3.Distance(world.Player.position, monster.transform.position);
            if (player < 3.5f && !world.PlayerHoldsKind("cheese"))
            { State = "Fleeing you"; monster.MoveToward(world.Player.position, P.Speed, dt, 0, out _, true); return true; }
            if (world.PlayerHoldsKind("cheese") && player < 7)
            {
                State = "Begging for cheese";
                if (player > 1.4f) monster.MoveToward(world.Player.position, P.Speed * .6f, dt, 1.3f, out _);
                else Face(world.Player.position, dt);
                return true;
            }
            return false;
        }

        bool NurseTick(float dt, bool seesPlayer)
        {
            if (Time.time >= nextHeal)
            {
                SandboxMonster wounded = null; float worst = .75f;
                foreach (var other in world.Monsters)
                {
                    if (other == monster || !other.Entity.Alive) continue;
                    float ratio = other.Entity.Health / other.Entity.MaximumHealth;
                    if (ratio < worst && Vector3.Distance(other.transform.position, monster.transform.position) < 10) { worst = ratio; wounded = other; }
                }
                if (wounded != null)
                {
                    State = "Healing " + wounded.Entity.DisplayName;
                    if (monster.MoveToward(wounded.transform.position, P.Speed, dt, 1.8f, out _))
                    {
                        wounded.Entity.Heal(22); nextHeal = Time.time + 3.5f; monster.SetPose("heal"); monster.Cue("heal");
                        world.Report("The Plague Nurse heals " + wounded.Entity.DisplayName + ".");
                        world.Effects.Pulse(wounded.Entity.Center, new Color(.4f, 1, .5f), 1.2f);
                    }
                    return true;
                }
            }
            if (seesPlayer && Time.time >= nextSkill && Vector3.Distance(world.Player.position, monster.transform.position) < 8)
            {
                nextSkill = Time.time + 7; monster.SetPose("fireball"); monster.Cue("cast");
                world.SpawnAcidPool(world.Player.position, 1.3f, 8, 9, monster.Entity);
                world.Report("The Plague Nurse sprays acid at you!");
                return true;
            }
            return false;
        }

        void MimicTick(float dt, bool seesPlayer)
        {
            State = monster.Entity.Asleep ? "A chest" : "Lid open";
            if (Vector3.Distance(world.Player.position, monster.transform.position) < 1.6f && Time.time >= nextAttack && !monster.Entity.Asleep)
            { nextAttack = Time.time + 2.2f; monster.SetPose("attacking"); monster.Cue("bite"); world.DamagePlayer(P.Damage, "The Mimic"); }
        }
    }
}
