using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Explicit packaged-game acceptance mode, never active during normal play.
    public sealed class PlayModeSmoke : MonoBehaviour
    {
        SandboxBootstrap app;
        SandboxWorld world;
        readonly JArray checks = new JArray();
        bool failed;
        string reportPath;
        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-brainwash-play-smoke") reportPath = args[i + 1];
            if (string.IsNullOrEmpty(reportPath)) yield break;
            app = GetComponent<SandboxBootstrap>(); world = app.World;
            yield return null; app.BackToTests(); yield return null;
            Check("test_mode_keeps_original_three_species", !world.PlayMode && world.Monsters.Select(m => m.Entity.Species).Distinct().Count() == 3);
            var stationary = world.Monsters.Select(m => m.transform.position).ToArray();
            yield return new WaitForSecondsRealtime(.7f);
            Check("test_mode_monsters_do_not_patrol", world.Monsters.Select((m, i) => Vector3.ProjectOnPlane(m.transform.position - stationary[i], Vector3.up).magnitude).All(d => d < .05f));
            Check("monsters_have_distinct_built_models", world.Monsters.All(m => m.Entity.VisualRoot != null && (m.GetComponent<CreatureAnimator>() != null || m.GetComponentsInChildren<Renderer>().Length > 8)));
            Check("key_has_custom_mesh", world.Find("k1").GetComponentInChildren<MeshFilter>().sharedMesh.vertexCount > 24);
            foreach (string species in new[] { "imp", "goblin", "ice_golem" })
            {
                app.BackToTests(); yield return null;
                var actor = world.Monsters.First(m => m.Entity.Species == species);
                Isolate(actor); actor.Teleport(Vector3.zero, Vector3.back); app.Teleport(new Vector3(0, 0, -4), 0);
                Physics.SyncTransforms();
                bool cast = world.BeginCast(out var reason);
                Check("can_brainwash_" + species, cast && world.StatusOf(actor) == "Listening", reason);
                var sent = world.EndCast();
                if (sent.Count == 1)
                {
                    var snapshot = sent[0].Scene;
                    Check("snapshot_uses_" + species + "_capabilities", (string)snapshot["actor"]["species"] == species && ((JArray)snapshot["actor"]["skills"]).Count == (species == "imp" ? 1 : 0));
                }
                world.Cancel();
            }
            app.StartPlay(); yield return null;
            Check("play_starts_six_living_monsters", world.PlayMode && world.Monsters.Count == 6 && world.Monsters.All(m => m.Entity.Alive));
            Check("play_starts_full_hp_mp", world.PlayerHP == world.MaxHP && world.MP == world.MaxMP);
            var before = world.Monsters.Select(m => m.transform.position).ToArray();
            yield return new WaitForSecondsRealtime(1);
            Check("play_monsters_move", world.Monsters.Select((m, i) => Vector3.Distance(m.transform.position, before[i])).Any(d => d > .25f));
            ScreenCapture.CaptureScreenshot(Path.ChangeExtension(reportPath, ".arena.png"));
            yield return new WaitForSecondsRealtime(.2f);

            app.StartPlay(); yield return null;
            var imp = world.Monsters.First(m => m.Entity.Species == "imp"); Isolate(imp);
            imp.Teleport(new Vector3(0, 0, -4.8f), Vector3.back); app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(.4f);
            Check("enemy_attack_reduces_hp", world.PlayerHP < world.MaxHP);
            float hp = world.PlayerHP;
            bool listening = world.BeginCast(out var listenReason);
            Check("play_cast_costs_mp_once", listening && world.StatusOf(imp) == "Listening" && world.MP <= 25, listenReason);
            yield return new WaitForSecondsRealtime(2);
            Check("listening_monster_cannot_attack", world.StatusOf(imp) == "Listening" && world.PlayerHP == hp);
            Check("holding_the_cast_drains_mp", world.MP < 24);
            var sealedLeases = world.EndCast();
            if (sealedLeases.Count == 1)
            {
                string lease = sealedLeases[0].Id;
                Check("controlled_stop_is_committed", world.ApplyOutcome(lease, StopOutcome()));
                yield return new WaitForSecondsRealtime(2);
                Check("brainwashed_monster_cannot_attack", world.PlayerHP == hp);
                // Production rule: an obeying monster cannot hear a new order until it is
                // Normal again. A new cast still starts; it just has nobody to reach.
                float beforeSecondCast = world.MP;
                bool again = world.BeginCast(out var againReason);
                Check("obeying_monster_sits_out_a_new_cast", again && world.LeaseOf(imp)?.Id == lease && world.StatusOf(imp) == "Brainwashed" && world.CountStatus("Listening") == 0, againReason);
                Check("cast_without_listeners_charges_no_fee", world.MP > beforeSecondCast - 1 && world.EndCast().Count == 0);
                Check("committed_lease_cannot_apply_twice", !world.ApplyOutcome(lease, StopOutcome()));
            }
            else Check("controlled_stop_is_committed", false, sealedLeases.Count + " listeners sent");
            float spent = world.MP; world.Cancel();
            yield return new WaitForSecondsRealtime(1.2f);
            Check("mp_regenerates_after_cast", world.MP > spent);
            yield return new WaitForSecondsRealtime(1.2f);
            Check("enemy_resumes_attack_after_control", world.PlayerHP < hp);

            app.StartPlay(); yield return null;
            imp = world.Monsters.First(m => m.Entity.Species == "imp"); Isolate(imp);
            imp.Teleport(new Vector3(4, 0, 0), Vector3.left); app.Teleport(new Vector3(2, 0, 0), 90); Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(1.2f);
            Check("enemy_cannot_attack_through_wall", world.PlayerHP == world.MaxHP);
            imp.Teleport(new Vector3(2, 0, 0), Vector3.right); Physics.SyncTransforms();
            var path = SandboxNavigation.FindPath(imp, new Vector3(4, 0, 0), .35f);
            Check("navigation_plans_around_divider", path.Count > 2 && path.Any(p => Mathf.Abs(p.z) > 3.5f), "Waypoints=" + path.Count);
            app.StartPlay(); yield return null;
            imp = world.Monsters.First(m => m.Entity.Species == "imp"); Isolate(imp);
            imp.Teleport(new Vector3(0, 0, -4.5f), Vector3.back); app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms();
            float enemyHp = imp.Entity.Health;
            world.PlayerAttack(); world.PlayerAttack();
            Check("staff_damage_and_cooldown", imp.Entity.Health == enemyHp - 25);
            world.PlayerEntity.TakeDamage(1000); yield return null;
            Check("player_death_ends_play", world.GameOver && !world.CanAcquire(out _) && !world.BeginCast(out _));
            var deathPositions = world.Monsters.Select(m => m.transform.position).ToArray();
            yield return new WaitForSecondsRealtime(.5f);
            Check("enemies_stop_after_player_death", world.Monsters.Select((m, i) => Vector3.ProjectOnPlane(m.transform.position - deathPositions[i], Vector3.up).magnitude).All(d => d < .05f));
            app.StartPlay(); yield return null;
            Check("restart_restores_hp_mp_and_movement", !world.GameOver && world.PlayerHP == world.MaxHP && world.MP == world.MaxMP && app.Player.GetComponent<CharacterController>().enabled);

            app.BackToTests(); yield return null;
            imp = world.Monsters.First(m => m.Entity.Species == "imp"); Isolate(imp);
            var key = world.Find("k1");
            Check("pickup_cast_begin", world.BeginCast(out _) && world.StatusOf(imp) == "Listening");
            var itemLeases = world.EndCast(); if (itemLeases.Count == 1) world.ApplyOutcome(itemLeases[0].Id, StopOutcome());
            key.Attach(imp.Entity, imp.Hand); yield return null;
            Check("held_key_visible_on_monster_hand", key.Owner == imp.Entity && key.transform.IsChildOf(imp.Hand) && key.GetComponentsInChildren<Renderer>().Any(r => r.enabled) && Vector3.Distance(key.transform.position, imp.Hand.position) < .4f);
            Check("held_key_has_no_solid_collider", !key.GetComponentsInChildren<Collider>().Any(c => c.enabled));
            ScreenCapture.CaptureScreenshot(Path.ChangeExtension(reportPath, ".held-key.png"));
            yield return new WaitForSecondsRealtime(.2f);
            yield return new WaitForSecondsRealtime(5.2f);
            Check("control_expiry_drops_key_to_world", key.Owner == null && key.transform.parent == world.RoomRoot && key.GetComponents<Collider>().Any(c => c.enabled));
            Check("expiry_restores_key_label", key.Label != null && key.Label.gameObject.activeSelf);

            app.BackToTests(); yield return null;
            imp = world.Monsters.First(m => m.Entity.Species == "imp"); Isolate(imp); key = world.Find("k1");
            world.BeginCast(out _); var obeying = world.EndCast(); if (obeying.Count == 1) world.ApplyOutcome(obeying[0].Id, StopOutcome());
            key.Attach(imp.Entity, imp.Hand); world.Cancel(); yield return null;
            Check("cancel_also_drops_held_key", key.Owner == null);
            world.BeginCast(out _); key.Attach(imp.Entity, imp.Hand); imp.Entity.TakeDamage(1000); yield return null;
            Check("monster_death_drops_held_key", key.Owner == null && world.LeaseOf(imp) == null);
            world.Cancel();

            // Several listeners (concept §6): one cast reaches every monster that meets
            // your eyes; monsters join and leave while the key is held; monsters that
            // obey an earlier order sit a new cast out without blocking it.
            app.BackToTests(); yield return null;
            imp = world.Monsters.First(m => m.Entity.Species == "imp");
            var goblin = world.Monsters.First(m => m.Entity.Species == "goblin");
            var golem = world.Monsters.First(m => m.Entity.Species == "ice_golem");
            imp.Teleport(Vector3.zero, Vector3.back); goblin.Teleport(new Vector3(-1.5f, 0, .5f), Vector3.back); golem.Teleport(new Vector3(1.2f, 0, 2.5f), Vector3.back);
            app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms(); yield return null;
            Check("three_monsters_meet_your_eyes", world.EligibleCount() == 3, world.EligibleCount().ToString());
            bool many = world.BeginCast(out var manyReason);
            Check("one_cast_locks_every_eligible_monster", many && world.CountStatus("Listening") == 3, manyReason);
            string firstGolem = world.LeaseOf(golem)?.Id;
            golem.Teleport(golem.transform.position, Vector3.forward); Physics.SyncTransforms(); yield return null; yield return null;
            Check("listener_turning_away_leaves_alone", world.StatusOf(golem) != "Listening" && world.CountStatus("Listening") == 2 && world.Casting);
            yield return new WaitForSecondsRealtime(.3f);
            golem.Teleport(golem.transform.position, Vector3.back); Physics.SyncTransforms(); yield return null; yield return null;
            var rejoined = world.LeaseOf(golem);
            Check("normal_monster_rejoins_the_same_hold_with_a_fresh_window", rejoined != null && rejoined.Status == "Listening" && rejoined.Id != firstGolem && rejoined.JoinOffset > .2f,
                rejoined == null ? "no lease" : rejoined.Status + " from " + rejoined.JoinOffset.ToString("0.00") + " s");
            var everyone = world.EndCast();
            Check("release_sends_every_listener", everyone.Count == 3 && everyone.All(l => l.Status == "Thinking"));
            foreach (var lease in everyone) world.ApplyOutcome(lease.Id, StopOutcome());
            Check("three_monsters_obey_at_once", world.CountStatus("Brainwashed") == 3);
            world.Cancel();
            golem.Teleport(golem.transform.position, Vector3.forward); imp.Teleport(imp.transform.position, Vector3.forward); Physics.SyncTransforms();
            world.BeginCast(out _); var first = world.EndCast(); if (first.Count == 1) world.ApplyOutcome(first[0].Id, StopOutcome());
            golem.Teleport(golem.transform.position, Vector3.back); imp.Teleport(imp.transform.position, Vector3.back); Physics.SyncTransforms();
            bool second = world.BeginCast(out var secondReason);
            Check("cast_while_another_obeys_reaches_the_others", first.Count == 1 && second && world.StatusOf(goblin) == "Brainwashed"
                && world.StatusOf(imp) == "Listening" && world.StatusOf(golem) == "Listening", secondReason);
            world.Cancel();
            app.Teleport(new Vector3(0, 0, -6), 180); Physics.SyncTransforms();
            bool blind = world.BeginCast(out _);
            Check("cast_can_start_before_anyone_meets_your_eyes", blind && world.Casting && world.CountStatus("Listening") == 0);
            yield return new WaitForSecondsRealtime(.4f);
            app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms(); yield return null; yield return null;
            Check("eyes_met_mid_cast_join_from_then", world.CountStatus("Listening") == 3 && world.Leases.All(l => l.JoinOffset >= .35f));
            world.Cancel();

            app.BackToTests(); yield return null;
            float deadline = Time.realtimeSinceStartup + 90;
            while (!app.Client.Ready && Time.realtimeSinceStartup < deadline) yield return new WaitForSecondsRealtime(.25f);
            Check("models_ready_for_explicit_drop_order", app.Client.Ready);
            if (app.Client.Ready)
            {
                imp = world.Monsters.First(m => m.Entity.Species == "imp");
                app.SendPreset("Pick up the key");
                deadline = Time.realtimeSinceStartup + 8;
                while (Time.realtimeSinceStartup < deadline && world.Find("k1").Owner == null) yield return null;
                Check("real_pickup_order_holds_key", world.Find("k1").Owner == imp.Entity);
                var carrying = world.LeaseOf(imp);
                app.SendPreset("Drop the key");
                Check("obeying_monster_ignores_a_new_order", carrying != null && world.LeaseOf(imp) == carrying && world.Find("k1").Owner == imp.Entity && app.Outcome.StartsWith("Nobody listened"), app.Outcome);
                deadline = Time.realtimeSinceStartup + 6;
                while (world.LeaseOf(imp) != null && Time.realtimeSinceStartup < deadline) yield return null;
                yield return null;
                Check("control_end_releases_the_picked_up_key", world.LeaseOf(imp) == null && world.Find("k1").Owner == null);
                app.SendPreset("Drop the key");
                deadline = Time.realtimeSinceStartup + 6;
                while (world.CountStatus("Thinking") > 0 && Time.realtimeSinceStartup < deadline) yield return null;
                Check("model_understands_new_drop_order", (app.LastResponse?["outcome"]?["steps"] as JArray)?.Any(s => (string)s["template"] == "drop") == true);
                app.BackToTests(); yield return null;
                app.SendPreset("Pick up the key then drop it");
                deadline = Time.realtimeSinceStartup + 6;
                while (world.CountStatus("Thinking") > 0 && Time.realtimeSinceStartup < deadline) yield return null;
                Check("compound_proposal_contains_drop", (app.LastResponse?["outcome"]?["steps"] as JArray)?.Any(s => (string)s["template"] == "drop") == true);
                if ((string)world.LastCompiled?["disposition"] == "execute")
                {
                    deadline = Time.realtimeSinceStartup + 4.5f;
                    while (Time.realtimeSinceStartup < deadline && !app.Logs.Any(s => s.Contains("Completed step 2: drop"))) yield return null;
                    Check("compound_drop_executes_when_grounded", app.Logs.Any(s => s.Contains("Completed step 2: drop")) && world.Find("k1").Owner == null, world.LastMessage);
                }
                else
                {
                    // The preserved model can omit the reference to step 1 and
                    // ask for current inventory. The host must reject that safely.
                    Check("ungrounded_compound_rejected_without_item_side_effects", (string)world.LastCompiled?["disposition"] == "missing_item" && world.Find("k1").Owner == null && Vector3.Distance(world.Find("k1").transform.position, new Vector3(-1, .18f, -1.4f)) < .02f, app.LastResponse?.ToString());
                }
            }
            app.StartPlay(); yield return null;
            foreach (var actor in world.Monsters) actor.Entity.TakeDamage(1000);
            app.Teleport(new Vector3(0, 0, 8.6f), 0); Physics.SyncTransforms();
            Check("exit_requires_key", !world.PlayerInteract() && !world.Find("o_door").Open);
            key = world.Find("k1"); key.transform.position = new Vector3(0, .1f, -4.8f);
            app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms();
            Check("player_can_recover_loose_key", world.PlayerInteract() && key.Owner == world.PlayerEntity);
            Check("player_received_key_is_visible", key.transform.IsChildOf(app.View.transform));
            app.Teleport(new Vector3(0, 0, 8.6f), 0); Physics.SyncTransforms();
            Check("key_unlocks_exit", world.PlayerInteract() && world.Find("o_door").Open);
            app.Teleport(new Vector3(0, 0, 11), 0); yield return null;
            Check("crossing_unlocked_exit_wins", world.Victory && !world.GameOver);
            app.BackToTests(); yield return null;
            Check("can_return_to_original_tests", !world.PlayMode && !world.GameOver && !world.Victory && app.ShowTestControls);
            app.AddLog("Play mode acceptance: " + (failed ? "FAIL" : "PASS"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, new JObject { ["at"] = DateTime.UtcNow.ToString("o"), ["unity"] = Application.unityVersion, ["passed"] = !failed, ["checks"] = checks }.ToString());
            ScreenCapture.CaptureScreenshot(Path.ChangeExtension(reportPath, ".png"));
            yield return new WaitForSecondsRealtime(1); Application.Quit(failed ? 1 : 0);
        }
        void Isolate(SandboxMonster keep)
        { foreach (var monster in world.Monsters) if (monster != keep) monster.Entity.TakeDamage(1000); }
        static JObject StopOutcome() => JObject.Parse("{ 'processable':true, 'disposition':'execute', 'directive':{ 'steps':[{ 'template':'stop', 'args':{} }], 'tasks':[{ 'recipe':'stop','bindings':{},'completion':'control_expiry' }] } }");
        void Check(string name, bool pass, string detail = null)
        {
            checks.Add(new JObject { ["name"] = name, ["pass"] = pass, ["detail"] = detail });
            failed |= !pass; Debug.Log("BRAINWASH_PLAY_SMOKE " + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }
    }
}
