using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // One monster's part in a cast: Listening → Thinking → Brainwashed, then
    // Normal again (concept §6, TDD §4). While it holds a lease the monster
    // cannot join any other cast; every other monster still can.
    public sealed class BrainwashLease
    {
        public BrainwashLease(SandboxMonster monster, int cast, float joinOffset)
        {
            Monster = monster; Cast = cast; JoinOffset = joinOffset; Id = Guid.NewGuid().ToString("N");
        }
        public SandboxMonster Monster { get; }
        public string Id { get; }
        public int Cast { get; }
        public string Status { get; internal set; } = "Listening";
        // This listener hears the cast's recording from JoinOffset to SealOffset
        // (seconds from the key press), never words spoken before it joined.
        public float JoinOffset { get; }
        public float SealOffset { get; internal set; }
        public string SealReason { get; internal set; }
        internal float ThinkingEnd, ControlEnd;
        public float ThinkingRemaining => Status == "Thinking" ? Mathf.Max(0, ThinkingEnd - Time.realtimeSinceStartup) : 0;
        public float ControlRemaining => Status == "Brainwashed" ? Mathf.Max(0, ControlEnd - Time.realtimeSinceStartup) : 0;
        public Vector3 SavedHere { get; internal set; }
        public Vector3 SavedListenerForward { get; internal set; }
        public Vector3 SavedSpeakerForward { get; internal set; }
        public JObject Scene { get; internal set; }
        public JObject Compiled { get; internal set; }
        internal SandboxExecutor Executor;
        internal readonly Dictionary<string, string> Generations = new Dictionary<string, string>();
        public bool IsSnapshotIdentity(SandboxEntity entity) => entity != null && Generations.TryGetValue(entity.Handle, out var generation) && entity.Generation == generation;
    }

    public sealed partial class SandboxWorld : MonoBehaviour
    {
        // Play mode charges this once per cast, when the first listener locks on.
        public const float CastFee = 5;
        public float MP { get; private set; } = 30;
        // The monster the HUD and sight lines describe: the one you aim at, or the
        // latest listener. Commands never depend on it.
        public SandboxMonster Monster { get; private set; }
        public List<SandboxMonster> Monsters { get; } = new List<SandboxMonster>();
        public bool PlayMode { get; private set; }
        public bool GameOver { get; private set; }
        public bool Victory { get; private set; }
        public float MaxHP => 100;
        public float MaxMP => 30;
        public float PlayerHP => PlayerEntity != null ? PlayerEntity.Health : 0;
        public string ObjectiveText => Scenario != null ? Scenario.Objective : !PlayMode ? "Stationary diagnostics: test a command on any monster." : GameOver ? "You fell. Restart the arena to try again."
            : Victory ? "Escaped with the brass key!" : HeldBy(PlayerEntity)?.ItemKind == "key"
            ? "Take the key to the north exit. Press E at the gate, then walk through."
            : "Recover the brass key with brainwashing or E pickup. Survive, then escape north.";
        public List<SandboxEntity> Entities { get; } = new List<SandboxEntity>();
        public SandboxEntity PlayerEntity { get; private set; }
        public Transform Player { get; private set; }
        public Camera View { get; private set; }
        public Transform RoomRoot { get; private set; }
        public string LastMessage { get; private set; } = "Ready";
        public bool DebugCones = true;
        public event Action<string> Log;
        // Listeners sent while the key is still held (they lost eye contact).
        public event Action<List<BrainwashLease>> ListenersSealed;
        // The held cast must end now (MP ran out or the recording is full).
        public event Action<string> ReleaseRequested;
        public event Action RunEnded;
        public JObject LastCompiled { get; private set; }
        public BrainwashLease LastLease { get; private set; }
        // ---------- Casts and leases ----------
        public bool Casting { get; private set; }
        public int CastNumber { get; private set; }
        public float CastElapsed => Casting ? Time.realtimeSinceStartup - castStart : 0;
        public IReadOnlyList<BrainwashLease> Leases => leases;
        private readonly List<BrainwashLease> leases = new List<BrainwashLease>();
        private float castStart;
        private bool castCharged, castEndRequested;
        private readonly List<LineRenderer> debugLines = new List<LineRenderer>();
        private readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();
        private Material lineMaterial;
        private float nextPlayerAttack;
        private float nextDropAttempt;
        private bool exitUnlocked;
        private readonly HashSet<SandboxMonster> pendingDrops = new HashSet<SandboxMonster>();
        private readonly HashSet<SandboxMonster> deadMonsters = new HashSet<SandboxMonster>();
        private int eligibleFrame = -1, eligibleCount;

        public void Initialize(Transform player, Camera camera)
        {
            Player = player; View = camera;
            PlayerEntity = player.TryGetComponent(out SandboxEntity existing) ? existing : player.gameObject.AddComponent<SandboxEntity>();
            PlayerEntity.Handle = "p_white"; PlayerEntity.Kind = "player"; PlayerEntity.ColorName = "white";
            PlayerEntity.DisplayName = "white player speaking"; PlayerEntity.World = this;
            PlayerEntity.Body = player.GetComponentInChildren<Renderer>();
            ResetSandbox();
        }
        public void Report(string message) { LastMessage = message; Log?.Invoke(message); }
        public SandboxEntity Find(string handle) => Entities.Find(e => e != null && e.Handle == handle);
        public SandboxEntity HeldBy(SandboxEntity owner) => Entities.Find(e => e != null && e.Owner == owner);
        public bool IsCurrent(SandboxEntity entity) => entity != null && Entities.Contains(entity);
        public BrainwashLease LeaseOf(SandboxMonster monster) { foreach (var lease in leases) if (lease.Monster == monster) return lease; return null; }
        public BrainwashLease FindLease(string id) { foreach (var lease in leases) if (lease.Id == id) return lease; return null; }
        public string StatusOf(SandboxMonster monster) => LeaseOf(monster)?.Status ?? "Normal";
        public bool IsControlled(SandboxMonster monster) => monster != null && LeaseOf(monster) != null;
        public int CountStatus(string status) { int count = 0; foreach (var lease in leases) if (lease.Status == status) count++; return count; }
        public void StartPlayMode() { Scenario = null; PlayMode = true; DebugCones = false; ResetSandbox(); }
        public void StopPlayMode() { Scenario = null; PlayMode = false; DebugCones = true; ResetSandbox(); }

        private SandboxMonster AimedMonster()
        {
            SandboxMonster selected = null; float score = float.NegativeInfinity;
            foreach (var candidate in Monsters)
            {
                if (candidate == null || !candidate.Entity.Alive) continue;
                Vector3 delta = candidate.Eye - View.transform.position;
                if (delta.magnitude > SandboxPerception.CastRange || !SandboxPerception.InCone(View.transform.position, View.transform.forward, candidate.Eye, SandboxPerception.CastHalfAngle)) continue;
                float candidateScore = Vector3.Dot(View.transform.forward, delta.normalized) * 100 - delta.magnitude * .1f;
                if (SandboxPerception.InCone(candidate.Eye, candidate.transform.forward, Player.position, SandboxPerception.MonsterHalfAngle, true)
                    && SandboxPerception.HasLineOfSight(View.transform.position, candidate.Entity, Player)) candidateScore += 1000;
                if (candidateScore > score) { score = candidateScore; selected = candidate; }
            }
            return selected;
        }

        // Range, the speaker's eye cone, the monster facing the speaker and clear
        // sight. A listener that loses any of these is sent at once.
        private bool InEyeContact(SandboxMonster monster, out string reason)
        {
            if (Vector3.Distance(View.transform.position, monster.Eye) > SandboxPerception.CastRange) { reason = "Monster is outside the 12 m cast range."; return false; }
            if (!SandboxPerception.InCone(View.transform.position, View.transform.forward, monster.Eye, SandboxPerception.CastHalfAngle)) { reason = "Aim the eye cone at a monster."; return false; }
            if (!SandboxPerception.InCone(monster.Eye, monster.transform.forward, Player.position, SandboxPerception.MonsterHalfAngle, true)) { reason = "The monster is not facing you."; return false; }
            if (!SandboxPerception.HasLineOfSight(View.transform.position, monster.Entity, Player)) { reason = "Line of sight to the monster is blocked."; return false; }
            reason = null; return true;
        }

        // Whether a monster would start listening now: alive, Normal (no lease of
        // its own), in eye contact, and passing the demo's eye rules.
        public bool CanJoin(SandboxMonster monster, out string reason)
        {
            if (Player == null || View == null) { reason = "World is not initialized."; return false; }
            if (GameOver || Victory) { reason = "The run has ended. Restart to continue."; return false; }
            if (monster == null || !monster.Entity.Alive || !PlayerEntity.Alive) { reason = "Speaker and listener must be alive."; return false; }
            var lease = LeaseOf(monster);
            if (lease != null) { reason = monster.Entity.DisplayName + " is already " + lease.Status + "; it can listen again once it is back to normal."; return false; }
            if (pendingDrops.Contains(monster) && HeldBy(monster.Entity) != null) { reason = "The monster needs free floor space to release its previous item."; return false; }
            if (!InEyeContact(monster, out reason)) return false;
            if (Scenario != null && !DemoEyeContact(monster, out reason)) return false;
            reason = "Eye contact: ready to listen."; return true;
        }

        public bool CanBeginCast(out string reason)
        {
            if (Player == null || View == null) { reason = "World is not initialized."; return false; }
            if (GameOver || Victory) { reason = "The run has ended. Restart to continue."; return false; }
            if (!PlayerEntity.Alive) { reason = "The speaker must be alive."; return false; }
            if (Casting) { reason = "Already brainwashing. Release first."; return false; }
            if (MP <= 0) { reason = PlayMode ? "No MP. It regenerates while you are not brainwashing." : "No MP. Reset the sandbox to refill."; return false; }
            if (PlayMode && MP < CastFee) { reason = "Need " + CastFee + " MP to brainwash. MP regenerates while you are not brainwashing."; return false; }
            reason = null; return true;
        }

        // The sight readout for the monster you aim at, which also becomes the
        // focus of the debug lines.
        public bool CanAcquire(out string reason)
        {
            if (!CanBeginCast(out reason) && !Casting) return false;
            var aimed = AimedMonster(); if (aimed != null) Monster = aimed;
            if (aimed == null) { reason = "Aim the eye cone at a monster."; return false; }
            return CanJoin(aimed, out reason);
        }

        // How many monsters would start listening if you pressed B now.
        public int EligibleCount()
        {
            if (eligibleFrame == Time.frameCount) return eligibleCount;
            eligibleFrame = Time.frameCount; eligibleCount = 0;
            foreach (var monster in Monsters) if (monster != null && CanJoin(monster, out _)) eligibleCount++;
            return eligibleCount;
        }

        public static string Names(IEnumerable<BrainwashLease> group)
        {
            var names = new List<string>();
            foreach (var lease in group) if (lease?.Monster != null) names.Add(lease.Monster.Entity.DisplayName);
            return names.Count == 0 ? "nobody" : string.Join(", ", names);
        }

        // Key down: one cast, one recording. Every eligible monster listens now,
        // and any monster that meets your eyes while you hold the key joins then.
        public bool BeginCast(out string reason)
        {
            if (!CanBeginCast(out reason)) return false;
            Casting = true; CastNumber++; castStart = Time.realtimeSinceStartup; castCharged = false; castEndRequested = false;
            // Soft: the microphone is already recording when the cast starts.
            GameAudio.Play2D("cast_start", .4f);
            var joined = JoinEligible();
            Report(joined.Count > 0 ? "Listening: " + Names(joined) + "." : "Brainwashing: no monster meets your eyes yet. Any that does while you hold the key listens from then on.");
            reason = joined.Count + " listening"; return true;
        }

        int CountListeners() { int n = 0; foreach (var lease in leases) if (lease.Cast == CastNumber && lease.Status == "Listening") n++; return n; }

        private List<BrainwashLease> JoinEligible()
        {
            var joined = new List<BrainwashLease>();
            foreach (var monster in Monsters)
            {
                if (monster == null || !CanJoin(monster, out _)) continue;
                if (PlayMode && !castCharged) { MP = Mathf.Max(0, MP - CastFee); castCharged = true; }
                var lease = new BrainwashLease(monster, CastNumber, CastElapsed);
                leases.Add(lease); joined.Add(lease);
                monster.SetPose("listening"); Monster = monster;
                // Each listener of this cast pings a little higher.
                GameAudio.Play("listener_join", monster.Entity.Center, .9f, Mathf.Pow(1.122f, Mathf.Min(8, CountListeners() - 1)));
            }
            if (joined.Count > 0) eligibleFrame = -1;
            return joined;
        }

        private void UpdateCast(float dt)
        {
            // MP drains from press to release, however many monsters listen.
            MP = Mathf.Max(0, MP - dt);
            List<BrainwashLease> leaving = null;
            foreach (var lease in leases)
            {
                if (lease.Status != "Listening" || lease.Cast != CastNumber || !lease.Monster.Entity.Alive) continue;
                if (InEyeContact(lease.Monster, out string why)) continue;
                lease.SealReason = why;
                (leaving ??= new List<BrainwashLease>()).Add(lease);
            }
            if (leaving != null)
            {
                foreach (var lease in leaving) GameAudio.Play("listener_lost", lease.Monster.Entity.Center);
                Seal(leaving);
                Report("Sent early (eye contact lost): " + Names(leaving) + ".");
                ListenersSealed?.Invoke(leaving);
            }
            var joined = JoinEligible();
            if (joined.Count > 0) Report("Joins mid-sentence: " + Names(joined) + ".");
            if (!castEndRequested && (MP <= 0 || CastElapsed >= VoiceCapture.MaxSeconds))
            {
                castEndRequested = true;
                string reason = MP <= 0 ? "MP ran out" : "eight-second recording limit";
                if (MP <= 0) GameAudio.Play2D("mp_low", 1, 1, .8f);
                Report("Sending: " + reason); ReleaseRequested?.Invoke(reason);
            }
        }

        // Release: every listener of this cast is sent with what it heard.
        public List<BrainwashLease> EndCast()
        {
            var sent = new List<BrainwashLease>();
            if (!Casting) return sent;
            foreach (var lease in leases)
                if (lease.Status == "Listening" && lease.Cast == CastNumber && lease.Monster.Entity.Alive) { lease.SealReason = "release"; sent.Add(lease); }
            Seal(sent);
            Casting = false;
            GameAudio.Play2D(sent.Count > 0 ? "cast_send" : "cast_fail");
            return sent;
        }

        // Abandons the held cast; its listeners go back to Normal without an order.
        public void CancelCast(string reason)
        {
            if (!Casting) return;
            Casting = false;
            var abandoned = new List<BrainwashLease>();
            foreach (var lease in leases) if (lease.Cast == CastNumber && lease.Status == "Listening") abandoned.Add(lease);
            foreach (var lease in abandoned) { leases.Remove(lease); EndControl(lease.Monster, false); }
            GameAudio.Play2D("cast_fail");
            Report(reason ?? "Brainwash cancelled.");
        }

        // No speech reached this listener: it is released, not confused.
        public void ReleaseListener(BrainwashLease lease, string reason)
        {
            if (lease == null || !leases.Contains(lease) || lease.Status == "Brainwashed") return;
            leases.Remove(lease); EndControl(lease.Monster, false);
            Report(lease.Monster.Entity.DisplayName + " goes back to normal: " + reason + ".");
        }

        private void Seal(List<BrainwashLease> group)
        {
            if (group.Count == 0) return;
            float offset = CastElapsed, now = Time.realtimeSinceStartup;
            Physics.SyncTransforms();
            foreach (var lease in group)
            {
                var listener = lease.Monster;
                lease.Status = "Thinking"; lease.ThinkingEnd = now + 5; lease.SealOffset = offset;
                lease.SavedHere = listener.transform.position;
                lease.SavedListenerForward = Vector3.ProjectOnPlane(listener.transform.forward, Vector3.up).normalized;
                lease.SavedSpeakerForward = Vector3.ProjectOnPlane(Player.forward, Vector3.up).normalized;
                listener.SetPose("thinking");
                GameAudio.Play("thinking", listener.Entity.Center, .8f);
                lease.Scene = BuildSnapshot(listener);
                lease.Generations.Clear();
                foreach (var entity in Entities) if (entity != null) lease.Generations[entity.Handle] = entity.Generation;
            }
        }

        public JObject BuildSnapshot(SandboxMonster listener)
        {
            if (Scenario != null) return BuildDemoSnapshot(listener);
            Physics.SyncTransforms();
            var all = new JArray(); var perceived = new JArray();
            foreach (var entity in Entities)
            {
                if (entity == null || entity == listener.Entity || entity.Owner != null) continue;
                var delta = entity.transform.position - listener.transform.position;
                float forward = Vector3.Dot(delta, listener.transform.forward), right = Vector3.Dot(delta, listener.transform.right);
                string axis = Mathf.Abs(forward) >= Mathf.Abs(right) ? (forward >= 0 ? "forward" : "back") : (right >= 0 ? "right" : "left");
                string direction = axis == "forward" ? "in front of you" : axis == "back" ? "behind you" : "on your " + axis;
                bool los = SandboxPerception.HasLineOfSight(listener.Eye, entity, listener.transform);
                bool visible = SandboxPerception.InCone(listener.Eye, listener.transform.forward, entity.Center, 60, true);
                float widths = Vector3.ProjectOnPlane(delta, Vector3.up).magnitude / SandboxPerception.PlayerWidth;
                var record = new JObject { ["handle"] = entity.Handle, ["kind"] = entity.Kind,
                    ["label"] = entity.DisplayName + " " + direction, ["visible"] = visible, ["los"] = los,
                    ["distanceWidths"] = widths, ["axis"] = axis, ["alive"] = entity.Alive,
                    ["health"] = entity.Health / entity.MaximumHealth, ["size"] = entity.Size, ["destructible"] = entity.Destructible,
                    ["generation"] = entity.Generation };
                if (!string.IsNullOrEmpty(entity.Species)) record["species"] = entity.Species;
                if (!string.IsNullOrEmpty(entity.ItemKind)) record["itemKind"] = entity.ItemKind;
                if (!string.IsNullOrEmpty(entity.ObjectKind)) record["objectKind"] = entity.ObjectKind;
                if (!string.IsNullOrEmpty(entity.ColorName)) record["color"] = entity.ColorName;
                var held = HeldBy(entity); if (held != null) record["held"] = held.ItemKind;
                all.Add(record); if (los && (visible || widths <= 4)) perceived.Add(record.DeepClone());
            }
            var ownItem = HeldBy(listener.Entity); var inventory = new JArray();
            if (ownItem != null) inventory.Add(new JObject { ["handle"] = ownItem.Handle, ["itemKind"] = ownItem.ItemKind, ["transferable"] = true, ["usable"] = false, ["throwable"] = true });
            return new JObject {
                ["id"] = "unity-sandbox", ["actor"] = new JObject { ["handle"] = listener.Entity.Handle, ["species"] = listener.Entity.Species,
                    ["skills"] = listener.Entity.Species == "imp" ? new JArray("fireball") : new JArray(), ["canFly"] = false, ["canCarry"] = false, ["canEat"] = false,
                    ["held"] = ownItem == null ? JValue.CreateNull() : new JValue(ownItem.ItemKind), ["inventory"] = inventory,
                    ["cooldown"] = listener.Entity.Species == "imp" && Time.time < listener.FireballReadyAt ? new JArray("fireball") : new JArray(), ["triggerReady"] = true },
                ["speaker"] = new JObject { ["color"] = "white", ["label"] = "white player speaking" },
                ["world"] = all, ["perceived"] = perceived, ["controlSeconds"] = 5, ["status"] = "Thinking", ["leaseValid"] = true };
        }

        public bool ApplyOutcome(string leaseId, JObject compiled)
        {
            var lease = FindLease(leaseId);
            if (lease == null || lease.Status != "Thinking" || GameOver || Victory || !lease.Monster.Entity.Alive || !PlayerEntity.Alive) return false;
            if (Time.realtimeSinceStartup >= lease.ThinkingEnd) { TechnicalFailure(lease, "thinking_deadline"); return false; }
            var monster = lease.Monster; string name = monster.Entity.DisplayName;
            lease.Compiled = compiled == null ? null : (JObject)compiled.DeepClone();
            LastCompiled = lease.Compiled; LastLease = lease;
            lease.Status = "Brainwashed"; lease.ControlEnd = Time.realtimeSinceStartup + ControlSeconds;
            if (Scenario != null) OnDemoBrainwashed(monster, compiled);
            if (compiled == null || (bool?)compiled["processable"] != true || (string)compiled["disposition"] != "execute")
            {
                monster.SetPose("confused"); GameAudio.Play("cast_fail", monster.Entity.Center);
                Report(name + " — no execution: " + ((string)compiled?["directive"]?["fallback"]?["reason"] ?? (string)compiled?["disposition"] ?? "invalid response")); return true;
            }
            var directive = compiled["directive"] as JObject;
            var steps = directive?["steps"] as JArray;
            if (steps == null || steps.Count == 0 || steps.Count > 3 || !(directive["tasks"] is JArray tasks) || tasks.Count != steps.Count)
            { monster.SetPose("confused"); Report(name + ": rejected malformed or overlong directive."); return true; }
            lease.Executor = new SandboxExecutor(this, lease, directive); monster.SetPose("brainwashed");
            GameAudio.Play("obey", monster.Entity.Center);
            Report(name + " accepted " + steps.Count + " action(s). Five seconds of control."); return true;
        }
        public void TechnicalFailure(string leaseId, string reason) => TechnicalFailure(FindLease(leaseId), reason);
        public void TechnicalFailure(BrainwashLease lease, string reason)
        {
            if (lease == null || lease.Status != "Thinking" || !leases.Contains(lease)) return;
            lease.Status = "Brainwashed"; lease.ControlEnd = Time.realtimeSinceStartup + ControlSeconds; lease.Monster.SetPose("confused");
            if (Scenario != null) OnDemoBrainwashed(lease.Monster, null);
            Report(lease.Monster.Entity.DisplayName + " — technical fallback: " + reason);
        }
        // Ends every cast and lease. Monsters that were obeying release what they
        // hold; listeners that never received an order keep theirs.
        public void Cancel()
        {
            bool active = Casting || leases.Count > 0;
            Casting = false;
            var all = leases.ToArray(); leases.Clear();
            foreach (var lease in all) { lease.Executor = null; EndControl(lease.Monster, lease.Status == "Brainwashed"); }
            if (active) Report("Brainwash cancelled. Late replies cannot execute.");
        }
        private void EndControl(SandboxMonster actor, bool releaseItems = true)
        {
            if (actor == null) return;
            if (Scenario != null) DemoEndControl(actor);
            actor.SetPose(actor.Entity.Alive ? "idle" : "dead"); actor.AI?.ResumeAfterControl(); actor.Brain?.ResumeAfterControl();
            if (releaseItems && actor.Entity.Alive) GameAudio.Play("control_end", actor.Entity.Center, .8f);
            if (releaseItems && !SandboxExecutor.TryDropHeldItem(this, actor, out var reason)) { pendingDrops.Add(actor); Report(reason); }
        }
        public void DamagePlayer(float amount, string source)
        {
            if (!PlayMode || GameOver || Victory || !PlayerEntity.Alive) return;
            if (Scenario != null) { amount = Mathf.Round(amount * Scenario.PlayerDamageScale); if (amount <= 0) return; }
            PlayerEntity.TakeDamage(amount); Report(source + " hit you for " + amount + " HP.");
            SandboxBootstrap.Shake(Mathf.Clamp(.2f + amount / 40f, .2f, .7f)); SandboxBootstrap.Punch(3);
            GameAudio.Play2D(PlayerEntity.Alive ? "player_hurt" : "player_death", 1, 1, .15f);
            if (!PlayerEntity.Alive) FinishRun(false);
        }
        internal void FinishRun(bool won)
        {
            if (GameOver || Victory) return;
            Victory = won; GameOver = !won; Cancel(); Scenario?.MarkFinished();
            GameAudio.Play2D(won ? "victory" : "defeat");
            foreach (var actor in Monsters) if (actor.Entity.Alive) actor.SetPose("idle");
            Report(Scenario != null ? (won ? Scenario.WinText : Scenario.LoseText) : won ? "Escaped with the key!" : "You fell. Restart the arena to try again."); RunEnded?.Invoke();
        }
        public bool PlayerAttack()
        {
            if (Scenario != null) return DemoPlayerAttack();
            if (!PlayMode || GameOver || Victory || !PlayerEntity.Alive || Time.time < nextPlayerAttack) return false;
            nextPlayerAttack = Time.time + .5f; Physics.SyncTransforms();
            SandboxMonster target = null; float closest = float.PositiveInfinity;
            foreach (var candidate in Monsters)
            {
                if (!candidate.Entity.Alive) continue;
                float distance = Vector3.Distance(Player.position, candidate.transform.position);
                if (distance > 2.1f + candidate.Motor.radius || distance >= closest
                    || !SandboxPerception.InCone(View.transform.position, View.transform.forward, candidate.Entity.Center, 40)
                    || !SandboxPerception.HasLineOfSight(View.transform.position, candidate.Entity, Player)) continue;
                target = candidate; closest = distance;
            }
            if (target == null) { Report("Staff swing missed."); return false; }
            GameAudio.Play("staff_hit_flesh", target.Entity.Center);
            target.Entity.TakeDamage(25); target.AI.Alert(Player.position);
            Report("Staff hit " + target.Entity.DisplayName + " for 25 HP."); return true;
        }
        public bool PlayerInteract()
        {
            if (Scenario != null) return DemoPlayerInteract();
            if (!PlayMode || GameOver || Victory) return false;
            var door = Find("o_door");
            if (door != null && Vector3.ProjectOnPlane(Player.position - door.transform.position, Vector3.up).magnitude <= 2.5f
                && SandboxPerception.InCone(View.transform.position, View.transform.forward, door.Center, 65)
                && SandboxPerception.HasLineOfSight(View.transform.position, door, Player))
            {
                if (HeldBy(PlayerEntity)?.ItemKind != "key") { Report("The exit needs the brass key. Ask a monster for it or pick it up with E."); return false; }
                exitUnlocked = true; door.SetOpen(true); Report("Exit unlocked. Walk through the north gate."); return true;
            }
            if (HeldBy(PlayerEntity) == null)
            {
                SandboxEntity nearestKey = null; float closest = 1.6f;
                foreach (var entity in Entities)
                {
                    if (entity == null || entity.Kind != "item" || entity.ItemKind != "key" || entity.Owner != null) continue;
                    float distance = Vector3.Distance(Player.position, entity.transform.position);
                    if (distance > closest || !SandboxPerception.InCone(View.transform.position, View.transform.forward, entity.Center, 65)
                        || !SandboxPerception.HasLineOfSight(View.transform.position, entity, Player)) continue;
                    nearestKey = entity; closest = distance;
                }
                if (nearestKey != null)
                {
                    nearestKey.Attach(PlayerEntity, PlayerEntity.HandSocket != null ? PlayerEntity.HandSocket : Player);
                    Report("Picked up the brass key. Take it to the north exit."); return true;
                }
            }
            Report("Face a nearby loose key and press E to pick it up, or use E at the north exit while holding it."); return false;
        }
        private void Update()
        {
            if (Player == null || PlayerEntity == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f), now = Time.realtimeSinceStartup;
            if (PlayMode && !GameOver && !Victory && !PlayerEntity.Alive) FinishRun(false);
            if (PlayMode && !GameOver && !Victory && !Casting) MP = Mathf.Min(MaxMP, MP + Time.unscaledDeltaTime * 1.5f);
            UpdateLeases(dt, now);
            if (Casting) UpdateCast(Time.unscaledDeltaTime);
            foreach (var actor in Monsters)
            {
                if (!actor.Entity.Alive && deadMonsters.Add(actor)) EndControl(actor);
                if (PlayMode && !GameOver && !Victory && actor.Entity.Alive && !IsControlled(actor))
                { if (actor.Brain != null) actor.Brain.Tick(dt); else actor.AI.Tick(this, dt); }
                actor.Gravity(dt);
            }
            if (now >= nextDropAttempt && pendingDrops.Count > 0)
            {
                nextDropAttempt = now + .25f;
                foreach (var actor in new List<SandboxMonster>(pendingDrops))
                    if (actor == null || SandboxExecutor.TryDropHeldItem(this, actor, out _)) pendingDrops.Remove(actor);
            }
            if (Scenario == null && PlayMode && !GameOver && !Victory && exitUnlocked && HeldBy(PlayerEntity)?.ItemKind == "key" && Player.position.z > 10.5f && Mathf.Abs(Player.position.x) < 1.7f) FinishRun(true);
            if (Scenario != null) DemoUpdate(dt);
            foreach (var entity in Entities) if (entity != null && entity.Label != null)
            {
                entity.Label.transform.position = entity.transform.position + Vector3.up * entity.LabelHeight;
                entity.Label.transform.rotation = Quaternion.LookRotation(entity.Label.transform.position - View.transform.position);
                var actor = entity.GetComponent<SandboxMonster>();
                entity.Label.text = Scenario != null ? DemoLabel(entity, actor) : entity.DisplayName + (actor != null ? " · " + (IsControlled(actor) ? StatusOf(actor) : !entity.Alive ? "DEAD" : PlayMode ? actor.AI.State : "Normal") + "\nHP " + Mathf.RoundToInt(entity.Health) : "");
            }
            DrawDebug();
        }

        // Each lease keeps its own clocks: Thinking ends within five seconds of its
        // send, and Brainwashed lasts five seconds from its own commit.
        private void UpdateLeases(float dt, float now)
        {
            if (leases.Count == 0) return;
            foreach (var lease in leases.ToArray())
            {
                if (!leases.Contains(lease)) continue;
                var actor = lease.Monster;
                if (actor == null || !actor.Entity.Alive || !PlayerEntity.Alive)
                {
                    // A dead listener is handled with the other deaths below.
                    leases.Remove(lease); lease.Executor = null;
                    if (actor != null && actor.Entity.Alive) EndControl(actor, lease.Status == "Brainwashed");
                    continue;
                }
                if (lease.Status == "Thinking" && now >= lease.ThinkingEnd) TechnicalFailure(lease, "thinking_deadline");
                else if (lease.Status == "Brainwashed")
                {
                    if (now >= lease.ControlEnd)
                    {
                        leases.Remove(lease); lease.Executor = null; EndControl(actor);
                        Report(actor.Entity.DisplayName + " is no longer brainwashed.");
                    }
                    else if (lease.Executor != null && !lease.Executor.Tick(dt)) lease.Executor = null;
                    else if (Scenario != null && lease.Executor != null && lease.Executor.HoldsClock) lease.ControlEnd += Time.unscaledDeltaTime;
                }
            }
        }
        public void ResetSandbox()
        {
            Cancel(); LastCompiled = null; LastLease = null; MP = MaxMP; GameOver = false; Victory = false; exitUnlocked = false; nextPlayerAttack = 0;
            pendingDrops.Clear(); deadMonsters.Clear(); Monsters.Clear(); nextDropAttempt = 0;
            foreach (var entity in Entities) if (entity != null && entity != PlayerEntity) { entity.gameObject.SetActive(false); Destroy(entity.gameObject); }
            Entities.Clear(); debugLines.Clear();
            if (RoomRoot != null) { RoomRoot.gameObject.SetActive(false); Destroy(RoomRoot.gameObject); }
            RoomRoot = new GameObject("Sandbox geometry").transform; RoomRoot.SetParent(transform, false);
            Entities.Add(PlayerEntity); PlayerEntity.RestoreHealth(MaxHP); PlayerEntity.Owner = null;
            ResetDemoState();
            if (Scenario != null) { BuildScenario(); return; }
            SandboxNavigation.ResetClassic(); RestoreClassicLighting();
            Solid("Floor", new Vector3(0, -.15f, 0), new Vector3(24, .3f, 24), new Color(.22f, .26f, .28f));
            Solid("West wall", new Vector3(-12, 1.5f, 0), new Vector3(.3f, 3, 24), Color.gray);
            Solid("East wall", new Vector3(12, 1.5f, 0), new Vector3(.3f, 3, 24), Color.gray);
            Solid("North wall", new Vector3(0, 1.5f, 12), new Vector3(24, 3, .3f), Color.gray);
            Solid("South wall", new Vector3(0, 1.5f, -12), new Vector3(24, 3, .3f), Color.gray);
            Solid("Occlusion test wall", new Vector3(3, 1.4f, 0), new Vector3(.35f, 2.8f, 7), new Color(.38f, .42f, .47f));
            var imp = Creature("listener", "imp", "red imp", Vector3.zero, new Color(.85f, .25f, .18f), .7f, 1.8f);
            Monster = imp.GetComponent<SandboxMonster>();
            var goblin = Creature("g1", "goblin", "green goblin", PlayMode ? new Vector3(-4, 0, 2) : new Vector3(-2, 0, -3), new Color(.55f, .75f, .2f), .7f, 1.5f); goblin.Health = 80;
            var golem = Creature("i1", "ice_golem", "big blue ice golem", PlayMode ? new Vector3(-7, 0, 5) : new Vector3(-4.5f, 0, -3), new Color(.25f, .65f, .95f), 1.1f, 2.4f); golem.Size = 3;
            if (PlayMode)
            {
                imp.RestoreHealth(70); goblin.RestoreHealth(90); golem.RestoreHealth(150);
                var secondImp = Creature("imp2", "imp", "orange imp", new Vector3(7, 0, 4), new Color(1, .4f, .12f), .7f, 1.8f); secondImp.ColorName = "orange"; secondImp.RestoreHealth(70);
                Creature("g2", "goblin", "dark green goblin", new Vector3(6, 0, -6), new Color(.25f, .55f, .16f), .7f, 1.5f).RestoreHealth(90);
                var secondGolem = Creature("i2", "ice_golem", "pale blue ice golem", new Vector3(7, 0, 8), new Color(.55f, .8f, 1), 1.1f, 2.4f); secondGolem.Size = 3; secondGolem.RestoreHealth(150);
            }
            Item("k1", "key", "brass key", new Vector3(-1, .18f, -1.4f), new Color(1, .75f, .15f));
            if (!PlayMode) Item("k_hidden", "key", "key behind the wall", new Vector3(4, .18f, 0), new Color(.95f, .7f, .1f));
            Object("o_lever", "lever", "lever", new Vector3(1.4f, .65f, -2), new Vector3(.25f, 1.3f, .25f), Color.red);
            Object("o_door", "door", PlayMode ? "north exit gate" : "door", PlayMode ? new Vector3(0, 1.2f, 10.5f) : new Vector3(-5, 1.1f, -5), PlayMode ? new Vector3(2.8f, 2.4f, .25f) : new Vector3(2, 2.2f, .25f), new Color(.5f, .3f, .12f));
            var crate = Object("o_crate", "crate", "wooden crate", new Vector3(2, .5f, 1.5f), Vector3.one, new Color(.55f, .4f, .22f)); crate.Destructible = true;
            var rope = Object("o_rope", "rope", "rope target", new Vector3(-5, 1, -6), new Vector3(.15f, 2, .15f), Color.yellow); rope.Destructible = true;
            Physics.SyncTransforms(); Report(PlayMode ? "Arena started. Get the key from a monster and reach the north exit. F attacks, E interacts." : "Test room reset. Face a monster. Wall blocks sight and movement.");
        }
        internal Material Material(Color color)
        {
            if (!materials.TryGetValue(color, out var material))
            { material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")); material.color = color; materials[color] = material; }
            return material;
        }
        internal GameObject Solid(string name, Vector3 position, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.transform.SetParent(RoomRoot, false);
            go.transform.position = position; go.transform.localScale = scale;
            // Floors, walls and wooden furniture get a tiling surface pattern.
            var surface = SurfaceTextures.Classify(name);
            go.GetComponent<Renderer>().sharedMaterial = surface == SurfaceTextures.Kind.None ? Material(color) : SurfaceTextures.Material(color, surface);
            if (surface != SurfaceTextures.Kind.None) SurfaceTextures.WorldUV(go);
            return go;
        }
        private SandboxEntity Creature(string handle, string species, string name, Vector3 position, Color color, float width, float height)
        {
            var root = new GameObject(name); root.transform.SetParent(RoomRoot, false); root.transform.position = position;
            var entity = root.AddComponent<SandboxEntity>(); entity.Handle = handle; entity.Kind = "monster"; entity.Species = species; entity.DisplayName = name; entity.World = this;
            entity.ColorName = species == "ice_golem" ? "blue" : species == "goblin" ? "green" : "red";
            SandboxVisuals.BuildCreature(entity, width, height, color, Material);
            var actor = root.AddComponent<SandboxMonster>(); actor.Initialize(entity, width, height); actor.NavigationEnabled = PlayMode; actor.Teleport(position, Vector3.back); Monsters.Add(actor);
            Entities.Add(entity); Label(entity, height + .45f); return entity;
        }
        private SandboxEntity Item(string handle, string kind, string name, Vector3 position, Color color)
        {
            var root = new GameObject(name); root.transform.SetParent(RoomRoot, false); root.transform.position = position;
            var entity = root.AddComponent<SandboxEntity>(); entity.Handle = handle; entity.Kind = "item"; entity.ItemKind = kind; entity.DisplayName = name; entity.World = this;
            root.AddComponent<BoxCollider>().size = kind == "key" ? new Vector3(.66f, .1f, .32f) : new Vector3(.38f, .2f, .2f);
            SandboxVisuals.BuildItem(entity, color, Material); Entities.Add(entity); Label(entity, .8f); return entity;
        }
        private SandboxEntity Object(string handle, string kind, string name, Vector3 position, Vector3 scale, Color color)
        {
            var go = new GameObject(name); go.transform.SetParent(RoomRoot, false); go.transform.position = position;
            var entity = go.AddComponent<SandboxEntity>();
            entity.Handle = handle; entity.Kind = "object"; entity.ObjectKind = kind; entity.DisplayName = name; entity.World = this;
            go.AddComponent<BoxCollider>().size = scale; SandboxVisuals.BuildObject(entity, scale, color, Material);
            Entities.Add(entity); Label(entity, .7f + scale.y * .5f); return entity;
        }
        internal void Label(SandboxEntity entity, float height)
        {
            var go = new GameObject("Debug label"); go.transform.SetParent(entity.transform, false); go.transform.position = entity.transform.position + Vector3.up * height;
            // Keep text scale independent of a primitive's nonuniform scale.
            go.transform.SetParent(RoomRoot, true); go.transform.localScale = Vector3.one * .025f;
            entity.Label = go.AddComponent<TextMesh>(); entity.Label.fontSize = 48; entity.Label.anchor = TextAnchor.MiddleCenter; entity.Label.alignment = TextAlignment.Center; entity.Label.color = Color.white;
            entity.LabelHeight = height;
        }
        private LineRenderer DebugLine(int index, Color color)
        {
            while (debugLines.Count <= index)
            {
                var go = new GameObject("Perception debug"); go.transform.SetParent(RoomRoot, false);
                var line = go.AddComponent<LineRenderer>(); line.useWorldSpace = true; line.widthMultiplier = .025f;
                if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
                line.sharedMaterial = lineMaterial; debugLines.Add(line);
            }
            var result = debugLines[index]; result.gameObject.SetActive(DebugCones); result.startColor = result.endColor = color;
            var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", color); block.SetColor("_Color", color); result.SetPropertyBlock(block);
            return result;
        }
        private void DrawDebug()
        {
            if (Scenario != null && !DebugCones) { for (int i = 0; i < debugLines.Count; i++) debugLines[i].gameObject.SetActive(false); return; }
            DrawDebugLines();
        }
        private void DrawDebugLines()
        {
            int index = 0;
            if (DebugCones && Monster != null)
            {
                DrawCone(DebugLine(index++, Color.cyan), Player.position, Player.forward, 25, 12);
                DrawCone(DebugLine(index++, new Color(1, .65f, .15f)), Monster.transform.position, Monster.transform.forward, 60, 7);
                var ring = DebugLine(index++, new Color(.1f, .75f, .9f)); ring.positionCount = 49;
                for (int i = 0; i <= 48; i++) { float angle = i * Mathf.PI * 2 / 48; ring.SetPosition(i, Monster.transform.position + new Vector3(Mathf.Cos(angle) * SandboxPerception.NearRadius, .08f, Mathf.Sin(angle) * SandboxPerception.NearRadius)); }
                foreach (var entity in Entities) if (entity != null && entity != Monster.Entity && entity.Owner == null)
                {
                    bool los = SandboxPerception.HasLineOfSight(Monster.Eye, entity, Monster.transform);
                    var line = DebugLine(index++, !los ? Color.red : SandboxPerception.Perceived(Monster, entity) ? Color.green : Color.gray);
                    line.positionCount = 2; line.SetPosition(0, Monster.Eye); line.SetPosition(1, entity.Center);
                }
            }
            for (; index < debugLines.Count; index++) debugLines[index].gameObject.SetActive(false);
        }
        private void DrawCone(LineRenderer line, Vector3 origin, Vector3 forward, float halfAngle, float range)
        {
            origin.y = .07f; forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            line.positionCount = 27; line.SetPosition(0, origin);
            for (int i = 0; i <= 24; i++) line.SetPosition(i + 1, origin + Quaternion.AngleAxis(-halfAngle + 2 * halfAngle * i / 24, Vector3.up) * forward * range);
            line.SetPosition(26, origin);
        }
        private void OnDestroy() { foreach (var material in materials.Values) if (material != null) Destroy(material); if (lineMaterial != null) Destroy(lineMaterial); }
    }
}
