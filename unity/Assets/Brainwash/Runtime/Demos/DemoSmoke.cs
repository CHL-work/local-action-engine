using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Automated acceptance for the five demos. Runs only with
    // -brainwash-demo-smoke <report.json>, or from the editor menu
    // "Brainwash/Run demo smoke", which leaves a request file in Library/.
    // Commands are injected as already-compiled host directives, so the
    // checks exercise the world and executor without the local model.
    public sealed class DemoSmoke : MonoBehaviour
    {
        SandboxBootstrap app;
        SandboxWorld world;
        readonly JArray checks = new JArray();
        bool failed;
        string reportPath, currentDemo;
        int errors;
        // A smoke step that throws ends its coroutine; the watchdog still writes a
        // report and quits instead of leaving a player window open forever.
        bool running, finished, crashed;
        float deadline;
        int sampledFrames;
        float sampledSeconds, largestFrameSeconds, clippedSeconds;

        public static string EditorRequestFile => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "brainwash-demo-smoke.request");

        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-brainwash-demo-smoke") reportPath = args[i + 1];
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(reportPath) && File.Exists(EditorRequestFile))
            {
                reportPath = File.ReadAllText(EditorRequestFile).Trim();
                File.Delete(EditorRequestFile);
            }
#endif
            if (string.IsNullOrEmpty(reportPath)) yield break;
            // A hidden Windows player has no presentation throttle and can busy-spin
            // at thousands of FPS. Exercise normal gameplay cadence in automation.
            // Deadlines and control leases still use their real-time clocks.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            app = GetComponent<SandboxBootstrap>(); world = app.World;
            Application.logMessageReceived += Count;
            running = true; deadline = Time.realtimeSinceStartup + 300; SandboxBootstrap.Automated = true;
            app.InMenu = false;
            yield return null;
            var (soundCount, clipCount, missingClips) = GameAudio.Verify();
            Check("sound_library_loaded", soundCount >= 90 && missingClips.Count == 0, soundCount + " sounds, " + clipCount + " clips" + (missingClips.Count > 0 ? ", missing: " + string.Join(", ", missingClips.Take(8)) : ""));
            foreach (var factory in DemoScenario.Catalogue)
            {
                var probe = factory();
                currentDemo = probe.Id;
                int before = errors;
                app.StartDemo(probe.Id); yield return null; yield return null;
                Check("built", world.Scenario != null && world.Scenario.Id == probe.Id && world.Monsters.Count > 0 && world.PlayerHP == world.MaxHP, "monsters=" + world.Monsters.Count);
                Check("snapshots_bounded", SnapshotsBounded(out string snapshotDetail), snapshotDetail);
                yield return new WaitForSecondsRealtime(2.2f);
                DemoCapture.Save(app.View, Path.ChangeExtension(reportPath, "." + probe.Id + ".png"));
                yield return null;
                switch (probe.Id)
                {
                    case "pit_arena": yield return PitArena(); break;
                    case "puppet_war": yield return PuppetWar(); break;
                    case "ledge_key": yield return LedgeKey(); break;
                    case "rite_of_return": yield return Rite(); break;
                    case "eye_heist": yield return Heist(); break;
                }
                Check("no_runtime_errors", errors == before, (errors - before) + " error(s)");
            }
            currentDemo = "gallery";
            yield return Gallery();
            currentDemo = "menu";
            app.OpenMenu(); yield return null;
            Check("menu_returns_to_selection", app.InMenu && world.Scenario == null);
            Finish();
        }

        // Every monster model in a lineup: all generated, and photographed idle,
        // walking, striking and dancing (screenshots beside the report).
        IEnumerator Gallery()
        {
            int before = errors;
            app.StartDemo("gallery"); yield return null; yield return null;
            var gallery = world.Scenario as MonsterGalleryDemo;
            Check("gallery_built", gallery != null && gallery.Lineup.Count == MonsterGalleryDemo.Species.Length, gallery == null ? "no gallery" : gallery.Lineup.Count + " monsters");
            if (gallery == null) yield break;
            var missing = gallery.Lineup.Where(m => m.GetComponent<CreatureAnimator>() == null).Select(m => m.Entity.Species).ToArray();
            Check("gallery_uses_generated_models", missing.Length == 0, missing.Length == 0 ? "all " + gallery.Lineup.Count + " species" : "primitive fallback: " + string.Join(", ", missing));
            SandboxHud.HideForCapture = true; app.ShowEquipment(false);
            foreach (string show in new[] { "idle", "walk", "strike", "dance" })
            {
                while (gallery.Current != show) gallery.Next();
                yield return new WaitForSecondsRealtime(show == "walk" ? 1.4f : 1f);
                DemoCapture.Save(app.View, Path.ChangeExtension(reportPath, ".gallery_" + show + ".png"));
                yield return null;
            }
            // A closer look at the middle of the lineup.
            app.Teleport(new Vector3(0, 0, -2.5f), 0);
            while (gallery.Current != "idle") gallery.Next();
            yield return new WaitForSecondsRealtime(1f);
            DemoCapture.Save(app.View, Path.ChangeExtension(reportPath, ".gallery_close.png"));
            yield return null;
            SandboxHud.HideForCapture = false; app.ShowEquipment(true);
            Check("gallery_no_runtime_errors", errors == before, (errors - before) + " error(s)");
        }

        void Update()
        {
            if (!running || finished) return;
            sampledFrames++;
            sampledSeconds += Time.unscaledDeltaTime;
            largestFrameSeconds = Mathf.Max(largestFrameSeconds, Time.unscaledDeltaTime);
            clippedSeconds += Mathf.Max(0, Time.unscaledDeltaTime - .1f);
            if (crashed) { Check("smoke_steps_completed", false, "a smoke step threw an exception in " + currentDemo); Finish(); }
            else if (Time.realtimeSinceStartup > deadline) { Check("smoke_steps_completed", false, "timed out in " + currentDemo); Finish(); }
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            Application.logMessageReceived -= Count;
            app.AddLog("Demo acceptance: " + (failed ? "FAIL" : "PASS"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, new JObject { ["at"] = DateTime.UtcNow.ToString("o"), ["unity"] = Application.unityVersion, ["passed"] = !failed,
                ["timing"] = new JObject { ["frames"] = sampledFrames, ["sampledSeconds"] = sampledSeconds,
                    ["averageFramesPerSecond"] = sampledSeconds > 0 ? sampledFrames / sampledSeconds : 0,
                    ["largestFrameSeconds"] = largestFrameSeconds, ["worldClippedSeconds"] = clippedSeconds },
                ["checks"] = checks }.ToString());
            StopAllCoroutines();
            Invoke(nameof(Quit), .5f);
        }

        void Quit()
        {
#if UNITY_EDITOR
            SandboxBootstrap.Automated = false;
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(failed ? 1 : 0);
#endif
        }

        void Count(string condition, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
            {
                errors++;
                if (type == LogType.Exception && stack != null && stack.Contains("DemoSmoke")) crashed = true;
                string where = string.IsNullOrEmpty(stack) ? "" : " @ " + stack.Split('\n')[0].Trim();
                Check("log_" + type, false, condition + where);
            }
        }

        void Check(string name, bool pass, string detail = null)
        {
            checks.Add(new JObject { ["demo"] = currentDemo, ["name"] = name, ["pass"] = pass, ["detail"] = detail });
            failed |= !pass; Debug.Log("BRAINWASH_DEMO_SMOKE " + currentDemo + "." + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }

        bool SnapshotsBounded(out string detail)
        {
            detail = "";
            foreach (var monster in world.Monsters.ToArray())
            {
                if (monster == null || !monster.Entity.Alive) continue;
                var snapshot = world.DebugSnapshot(monster);
                var perceived = (JArray)snapshot["perceived"]; var all = (JArray)snapshot["world"];
                var perKind = perceived.GroupBy(e => (string)e["kind"]).Select(g => g.Count()).DefaultIfEmpty(0).Max();
                bool labels = all.All(e => ((string)e["label"]).Length <= 200 && ((string)e["label"]).Length > 0);
                if (all.Count > 64 || perKind > 8 || !labels) { detail = monster.Entity.DisplayName + " world=" + all.Count + " perKind=" + perKind; return false; }
                detail = "checked " + world.Monsters.Count + " listeners";
            }
            return true;
        }

        SandboxMonster First(string species) => world.Monsters.FirstOrDefault(m => m != null && m.Entity.Alive && m.Entity.Species == species);

        static JObject Step(string template, JObject args, JObject bindings, string completion = "one_activation", string recipe = null)
            => new JObject { ["step"] = new JObject { ["template"] = template, ["args"] = args }, ["task"] = new JObject { ["recipe"] = recipe ?? template, ["bindings"] = bindings, ["completion"] = completion } };

        static JObject Directive(params JObject[] steps)
        {
            var stepArray = new JArray(); var taskArray = new JArray();
            for (int i = 0; i < steps.Length; i++) { stepArray.Add(steps[i]["step"]); var task = (JObject)steps[i]["task"]; task["step"] = i; task["sharesAbsoluteExpiry"] = true; taskArray.Add(task); }
            return new JObject { ["processable"] = true, ["disposition"] = "execute", ["directive"] = new JObject { ["steps"] = stepArray, ["tasks"] = taskArray } };
        }

        static JObject Bound(string handle) => new JObject { ["kind"] = "bound", ["handle"] = handle };
        static JObject Speaker() => new JObject { ["kind"] = "speaker", ["reference"] = new JObject { ["kind"] = "speaker" } };
        static JObject Entity(string handle) => new JObject { ["kind"] = "entity", ["handle"] = handle };

        IEnumerator Wait(Func<bool> condition, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until && !condition()) yield return null;
        }

        void Face(Vector3 point)
        {
            Vector3 flat = Vector3.ProjectOnPlane(point - app.Player.position, Vector3.up);
            app.Teleport(app.Player.position, Quaternion.LookRotation(flat).eulerAngles.y);
        }

        // ---------- Demo-specific checks ----------
        IEnumerator PitArena()
        {
            var goblins = world.Monsters.Where(m => m.Entity.Alive && m.Entity.Species == "goblin").ToList();
            foreach (var m in world.Monsters) if (m.Brain != null) m.Brain.Passive = true;
            if (goblins.Count >= 2)
            {
                // Clear floor away from the rocks, crates and pits.
                app.Teleport(new Vector3(-8, 0, -5.5f), 0);
                goblins[0].Teleport(new Vector3(-8, 0, -4.1f), Vector3.back); Physics.SyncTransforms();
                yield return null;
                bool kicked = world.PlayerKick();
                var body = goblins[0].Comp<PhysicsBody>();
                Check("kick_launches_light_monster", kicked && body != null && body.Knocked);
                goblins[1].Teleport(new Vector3(-5, .2f, 2), Vector3.back); Physics.SyncTransforms();
                yield return Wait(() => !goblins[1].Entity.Alive, 2.5f);
                Check("spike_pit_kills", !goblins[1].Entity.Alive && goblins[1].Entity.LastElement == "spikes", goblins[1].Entity.LastElement);
            }
            else Check("kick_launches_light_monster", false, "not enough goblins");
            var barrel = world.Entities.FirstOrDefault(e => e != null && e.ObjectKind == "barrel" && e.Alive);
            if (barrel != null) { barrel.TakeDamage(5, world.PlayerEntity, "fire"); yield return null; }
            Check("fire_detonates_barrel", barrel != null && !barrel.Alive);
            var ogreTest = world.SpawnMonster("ogre", "m_test_ogre", "test ogre", new Vector3(-2, 0, -9.6f), Vector3.back);
            ogreTest.Brain.Passive = true;
            app.Teleport(new Vector3(-2, 0, -11), 0); Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(.8f); // past the kick cooldown
            bool kickedOgre = world.PlayerKick();
            var ogreBody = ogreTest.Comp<PhysicsBody>();
            Check("heavy_ogre_barely_moves", kickedOgre && ogreBody != null && ogreBody.Knocked && ogreBody.Velocity.magnitude < 2.5f,
                ogreBody != null ? ogreBody.Velocity.magnitude.ToString("0.0") : "not knocked");
            yield return SeveralListeners();
            yield return WalkingNoFall();
        }

        // A monster whose steps go nowhere (blocked by a crowd or a wall) stays on
        // the floor; when it stops it has not fallen and must take no fall damage.
        IEnumerator WalkingNoFall()
        {
            var walker = world.SpawnMonster("goblin", "m_walker", "stuck goblin", new Vector3(-10.5f, 0, -9.5f), Vector3.left);
            walker.Brain.Passive = true; walker.NavigationEnabled = false; Physics.SyncTransforms(); yield return null;
            float full = walker.Entity.Health, until = Time.realtimeSinceStartup + 1.6f;
            while (Time.realtimeSinceStartup < until)
            {
                walker.MoveToward(new Vector3(-20, 0, -9.5f), 3, Mathf.Min(Time.unscaledDeltaTime, .1f), .2f, out _);
                walker.Motor.Move(Vector3.zero); // a step that goes nowhere
                yield return null;
            }
            yield return new WaitForSecondsRealtime(.4f);
            Check("blocked_monster_takes_no_fall_damage", walker.Entity.Alive && walker.Entity.Health >= full - .01f,
                walker.Entity.Health.ToString("0") + "/" + full.ToString("0") + " HP at x=" + walker.transform.position.x.ToString("0.0") + ", last damage " + (walker.Entity.LastElement ?? "none"));
        }

        // Concept §6: one cast reaches every monster meeting your eyes, monsters
        // join and leave while the key is held, and obeying monsters sit out a
        // new cast without blocking it.
        IEnumerator SeveralListeners()
        {
            world.Cancel();
            app.Teleport(new Vector3(0, 0, -5), 0); Physics.SyncTransforms();
            var left = world.SpawnMonster("goblin", "m_listen_left", "left goblin", new Vector3(-1.5f, 0, -1), Vector3.back);
            var middle = world.SpawnMonster("goblin", "m_listen_middle", "middle goblin", new Vector3(0, 0, -.4f), Vector3.back);
            var right = world.SpawnMonster("imp", "m_listen_right", "right imp", new Vector3(1.6f, 0, -1), Vector3.forward);
            foreach (var m in world.Monsters) if (m.Brain != null) m.Brain.Passive = true;
            Physics.SyncTransforms(); yield return null;
            world.DebugSetMP(world.MaxMP);
            bool cast = world.BeginCast(out string castReason);
            string State() => "left " + world.StatusOf(left) + ", middle " + world.StatusOf(middle) + ", right " + world.StatusOf(right) + "; " + world.Leases.Count + " leases";
            Check("hold_b_locks_every_monster_meeting_your_eyes", cast && world.StatusOf(left) == "Listening" && world.StatusOf(middle) == "Listening" && world.StatusOf(right) == "Normal", castReason + " · " + State());
            right.Teleport(right.transform.position, Vector3.back); Physics.SyncTransforms();
            yield return Wait(() => world.StatusOf(right) == "Listening", 1);
            Check("monster_meeting_your_eyes_mid_cast_joins_then", world.StatusOf(right) == "Listening" && world.LeaseOf(right).JoinOffset > 0, State());
            left.Teleport(left.transform.position, Vector3.forward); Physics.SyncTransforms();
            yield return Wait(() => world.StatusOf(left) != "Listening", 1);
            Check("listener_losing_eye_contact_leaves_alone", world.StatusOf(left) != "Listening" && world.StatusOf(middle) == "Listening" && world.Casting, State());
            var sent = world.EndCast();
            Check("release_sends_each_remaining_listener", sent.Contains(world.LeaseOf(middle)) && sent.Contains(world.LeaseOf(right)) && sent.All(l => l.Status == "Thinking"), sent.Count + " sent · " + State());
            foreach (var lease in sent) world.ApplyOutcome(lease.Id, Directive(Step("stop", new JObject(), new JObject(), "duration_or_expiry")));
            Check("several_monsters_obey_at_once", world.StatusOf(middle) == "Brainwashed" && world.StatusOf(right) == "Brainwashed", State());
            left.Teleport(left.transform.position, Vector3.back); Physics.SyncTransforms(); yield return null;
            string middleLease = world.LeaseOf(middle)?.Id;
            bool again = world.BeginCast(out string againReason);
            Check("cast_again_while_others_obey", again && world.StatusOf(left) == "Listening" && world.LeaseOf(middle)?.Id == middleLease
                && world.StatusOf(middle) == "Brainwashed" && world.StatusOf(right) == "Brainwashed", againReason + " · " + State());
            world.Cancel();
        }

        IEnumerator PuppetWar()
        {
            foreach (var m in world.Monsters) if (m.Brain != null) m.Brain.Passive = true;
            var imp = First("imp"); var goblin = First("goblin");
            imp.Teleport(new Vector3(-1, 0, -9), Vector3.left); goblin.Teleport(new Vector3(-7, 0, -9), Vector3.right);
            app.Teleport(new Vector3(3, 0, -12), 0); Physics.SyncTransforms(); yield return null;
            bool committed = world.DebugCommand(imp, Directive(Step("use_skill", new JObject { ["skill"] = "fireball", ["target"] = Entity(goblin.Entity.Handle) }, new JObject { ["target"] = Bound(goblin.Entity.Handle) })));
            yield return Wait(() => goblin.Brain.StageAgainst(imp.Entity) >= 1, 4);
            Check("commanded_fireball_starts_grudge", committed && goblin.Brain.StageAgainst(imp.Entity) >= 1, "stage=" + goblin.Brain.StageAgainst(imp.Entity));
            world.Cancel();
            world.DebugSetMP(10);
            var victim = world.Monsters.First(m => m.Entity.Alive && m.Entity.Species == "goblin" && m != goblin);
            victim.Entity.TakeDamage(999, imp.Entity, "fire"); yield return null;
            Check("tithe_of_chaos_refunds_mp", world.MP >= 15.9f, "MP=" + world.MP.ToString("0.0"));
            var lever = world.Find("o_lever");
            app.Teleport(lever.transform.position + new Vector3(0, -.65f, -1.2f), 0); Physics.SyncTransforms(); yield return null;
            world.PlayerInteract(); yield return null; yield return null;
            var ogre = First("ogre");
            Check("lever_frees_ogre", world.Find("o_cage_gate").Open && ogre != null && !ogre.Brain.Passive);
        }

        IEnumerator LedgeKey()
        {
            var harpy = First("harpy");
            app.Teleport(new Vector3(4, 0, -6), 0); Physics.SyncTransforms(); yield return null;
            bool ferry = world.DebugCommand(harpy, Directive(Step("carry_to", new JObject { ["cargo"] = new JObject { ["kind"] = "speaker" }, ["destination"] = Entity("o_ledge"), ["movement"] = "fly" },
                new JObject { ["cargo"] = Speaker(), ["destination"] = Bound("o_ledge") })));
            yield return Wait(() => world.Carried == null && app.Player.position.y > 2 && app.Player.position.z > 6, 6.5f);
            yield return new WaitForSecondsRealtime(.6f);
            Check("harpy_carries_player_onto_ledge", ferry && app.Player.position.y > 2 && app.Player.position.z > 6,
                "player=" + app.Player.position.ToString("0.0") + ", carrier=" + harpy.transform.position.ToString("0.0")
                + ", status=" + world.StatusOf(harpy) + ", last=" + world.LastMessage);

            app.StartDemo("ledge_key"); yield return null; yield return null;
            var ogre = First("ogre"); ogre.Entity.Asleep = false;
            app.Teleport(new Vector3(-6, 0, -4), 0); Physics.SyncTransforms(); yield return null;
            bool toss = world.DebugCommand(ogre, Directive(Step("carry_to", new JObject { ["cargo"] = new JObject { ["kind"] = "speaker" }, ["destination"] = Entity("o_ledge"), ["movement"] = "walk" },
                new JObject { ["cargo"] = Speaker(), ["destination"] = Bound("o_ledge") })));
            yield return Wait(() => app.Player.position.z > 6.3f && app.Player.position.y > -.3f, 6);
            Check("ogre_throws_player_across", toss && app.Player.position.z > 6.3f, "player=" + app.Player.position.ToString("0.0"));

            app.StartDemo("ledge_key"); yield return null; yield return null;
            var imp = First("imp");
            app.Teleport(new Vector3(-1.6f, 0, -6), 0); Physics.SyncTransforms(); yield return null;
            bool fire = world.DebugCommand(imp, Directive(Step("use_skill", new JObject { ["skill"] = "fireball", ["target"] = Entity("o_rope") }, new JObject { ["target"] = Bound("o_rope") })));
            yield return Wait(() => !world.Find("o_rope").Alive, 4);
            yield return new WaitForSecondsRealtime(1.3f);
            var hinge = world.RoomRoot.Find("Drawbridge hinge");
            Check("fireball_burns_rope_and_drops_bridge", fire && !world.Find("o_rope").Alive && hinge != null && Mathf.Abs(Mathf.DeltaAngle(hinge.localEulerAngles.x, -90)) < 5,
                hinge != null ? "hinge=" + hinge.localEulerAngles.x.ToString("0") : "no hinge");

            var mimic = First("mimic"); mimic.Entity.Asleep = false;
            app.Teleport(new Vector3(7, 0, -10.3f), 90); Physics.SyncTransforms(); yield return null;
            bool gift = world.DebugCommand(mimic, Directive(Step("trigger", new JObject { ["trigger"] = "hidden_gift" }, new JObject())));
            var spare = world.Find("i_spare_key");
            yield return Wait(() => spare.gameObject.activeInHierarchy, 2);
            Check("mimic_hidden_gift_returns_key", gift && spare.gameObject.activeInHierarchy && spare.Owner == null);

            // Dead or alive: if the mouse died earlier, the check says how.
            var mouse = world.Monsters.FirstOrDefault(m => m != null && m.Entity.Species == "mouse"); var keeper = world.Find("m_keeper"); var brass = world.Find("i_key");
            bool tide = world.DebugCommand(mouse, Directive(Step("trigger", new JObject { ["trigger"] = "cheese_theft", ["target"] = Entity("m_keeper") }, new JObject { ["target"] = Bound("m_keeper") })));
            yield return Wait(() => brass.Owner == null, 3f);
            Check("rat_tide_makes_keeper_drop_key", tide && brass.Owner == null && keeper != null,
                "committed=" + tide + ", key holder=" + (brass.Owner != null ? brass.Owner.DisplayName : "none") + ", mouse alive=" + mouse.Entity.Alive
                + (mouse.Entity.Alive ? "" : " (" + mouse.Entity.LastElement + " by " + (mouse.Entity.LastAttacker != null ? mouse.Entity.LastAttacker.DisplayName : "nobody") + " at " + mouse.transform.position.ToString("0.0") + ")")
                + ", last message: " + world.LastMessage);

            brass.Attach(world.PlayerEntity, world.PlayerEntity.HandSocket);
            app.Teleport(new Vector3(6, 0, 11.4f), 0); Physics.SyncTransforms(); yield return null;
            Check("key_opens_exit", world.PlayerInteract() && world.Find("o_exit").Open);
            app.Teleport(new Vector3(6, 0, 14.6f), 0); yield return null; yield return null;
            Check("walking_out_wins", world.Victory);
        }

        IEnumerator Rite()
        {
            var demo = world.Scenario;
            app.Teleport(new Vector3(-6, 0, -6), 0); yield return null;
            app.TypedChant(demo.ChantLine);
            Check("typed_chant_takes_time", app.Chanting && demo.ChantProgress < 1);
            yield return Wait(() => !app.Chanting, 6);
            Check("chant_outside_circle_does_nothing", demo.ChantProgress < 1, demo.ChantProgress.ToString("0.0"));
            app.Teleport(new Vector3(0, 0, -.5f), 0); yield return null;
            app.TypedChant(demo.ChantLine);
            yield return Wait(() => !app.Chanting, 6);
            Check("exact_chant_fills_rite", demo.ChantProgress >= 20, demo.ChantProgress.ToString("0.0"));
            float before = demo.ChantProgress;
            app.TypedChant("completely different words entirely");
            yield return Wait(() => !app.Chanting, 6);
            Check("wrong_words_barely_count", demo.ChantProgress - before < 4, (demo.ChantProgress - before).ToString("0.0"));
            var nurse = First("nurse");
            nurse.Teleport(new Vector3(-1.5f, 0, 2.5f), Vector3.right); Physics.SyncTransforms(); yield return null;
            bool revive = world.DebugCommand(nurse, Directive(Step("use_skill", new JObject { ["skill"] = "revive", ["target"] = Entity("r_cultist") }, new JObject { ["target"] = Bound("r_cultist") })));
            yield return Wait(() => world.Find("r_cultist").Alive, 4);
            Check("nurse_revives_cultist", revive && world.Find("r_cultist").Alive,
                "accepted=" + revive + ", nurse=" + nurse.transform.position.ToString("0.000")
                + ", target=" + world.Find("r_cultist").transform.position.ToString("0.000")
                + ", status=" + world.StatusOf(nurse) + ", last=" + world.LastMessage);
            float chanting = demo.ChantProgress;
            yield return new WaitForSecondsRealtime(1.5f);
            Check("revived_cultist_chants", demo.ChantProgress > chanting + 1, (demo.ChantProgress - chanting).ToString("0.0"));
        }

        IEnumerator Heist()
        {
            var gargoyle = world.Monsters.First(m => m.Entity.Handle == "m_gargoyle_west");
            var other = world.Monsters.First(m => m.Entity.Handle == "m_gargoyle_east");
            other.Entity.TakeDamage(9999, null, "fire");
            app.Teleport(new Vector3(1.5f, 0, -6.5f), 0); Face(gargoyle.Entity.Center); Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(.5f);
            Check("gargoyle_petrifies_when_watched", gargoyle.Brain.Petrified, gargoyle.Brain.State);
            bool stop = world.DebugCommand(gargoyle, Directive(Step("stop", new JObject(), new JObject(), "duration_or_expiry")));
            float Clock() => world.LeaseOf(gargoyle)?.ControlRemaining ?? 0;
            float a = Clock(); yield return new WaitForSecondsRealtime(1); float b = Clock();
            Check("watched_gargoyle_holds_clock", stop && a - b < .35f, (a - b).ToString("0.00"));
            app.Teleport(app.Player.position, app.Player.eulerAngles.y + 180); yield return new WaitForSecondsRealtime(1); float c = Clock();
            Check("unwatched_gargoyle_spends_clock", b - c > .6f, (b - c).ToString("0.00"));
            world.Cancel();
            var lookout = world.Monsters.First(m => m.Entity.Handle == "m_lookout");
            lookout.Teleport(lookout.transform.position, Vector3.back);
            app.Teleport(lookout.transform.position + new Vector3(0, 0, -5), 0); Face(lookout.Entity.Center); Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(.5f);
            bool blocked = !world.CanAcquire(out string reason);
            Check("shy_lookout_hides_face", lookout.Brain.CoveringFace && blocked && reason.Contains("face"), reason);
            var golem = world.Monsters.First(m => m.Entity.Handle == "m_golem");
            var mimic = world.Monsters.First(m => m.Entity.Species == "mimic");
            var pot = world.TakeSwallowed(mimic.Entity);
            string potDetail = pot == null ? "the mimic held no fire pot" : "";
            // Thrown from the gallery side (the vault door is behind the golem). A pot
            // that lands without bursting is thrown once more.
            for (int attempt = 1; attempt <= 2 && pot != null && golem.Entity.VisorIntact; attempt++)
            {
                Vector3 from = golem.Entity.Center + Vector3.back * 2.2f + Vector3.up * .3f;
                world.TossItem(pot, from, golem.Entity.Center);
                pot.Comp<PhysicsProp>().Throw((golem.Entity.Center - from).normalized * 9, world.PlayerEntity);
                yield return Wait(() => !golem.Entity.VisorIntact || !pot.gameObject.activeSelf, 1.5f);
                yield return Wait(() => !golem.Entity.VisorIntact, .5f);
                potDetail += "throw " + attempt + " from " + from.ToString("0.0") + ": " + (pot.gameObject.activeSelf ? "landed at " : "burst at ") + pot.transform.position.ToString("0.0")
                    + ", golem at " + golem.transform.position.ToString("0.0") + " centre " + golem.Entity.Center.ToString("0.0") + ". ";
                if (!pot.gameObject.activeSelf) break;
            }
            Check("fire_pot_melts_golem_visor", pot != null && !golem.Entity.VisorIntact, potDetail);
            var door = world.Find("o_vault_door");
            door.TakeDamage(50, world.PlayerEntity, "physical");
            Check("player_cannot_break_vault_door", door.Alive && door.Health >= 59.9f);
            door.TakeDamage(80, golem.Entity, "physical"); yield return null;
            Check("heavy_monster_breaks_vault_door", !door.Alive);
        }
    }
}
