using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Only runs when explicitly requested with -brainwash-smoke <report.json>.
    // Functional smoke, not a model accuracy evaluation or microphone claim.
    public sealed class SandboxSmoke : MonoBehaviour
    {
        readonly JArray checks = new JArray();
        SandboxBootstrap app;
        string reportPath, voicePath;
        bool failed;
        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-brainwash-smoke") reportPath = args[i + 1];
                if (args[i] == "-brainwash-smoke-wav") voicePath = args[i + 1];
            }
            if (string.IsNullOrEmpty(reportPath)) yield break;
            app = GetComponent<SandboxBootstrap>();
            yield return null;
            app.ResetRoom(); yield return null;
            var world = app.World;
            Check("initial_eye_contact", world.CanAcquire(out var initialReason), initialReason);
            app.Teleport(new Vector3(0, 0, -6), 90);
            Check("outside_player_cone_rejected", !world.CanAcquire(out var outsideReason), outsideReason);
            app.Teleport(new Vector3(0, 0, -6), 0);
            world.Monster.transform.rotation = Quaternion.identity;
            Check("monster_facing_away_rejected", !world.CanAcquire(out var facingReason), facingReason);
            world.Monster.Teleport(new Vector3(4, 0, 0), Vector3.left);
            app.Teleport(Vector3.zero, 90);
            Physics.SyncTransforms();
            Check("occluding_wall_rejects_cast", !world.CanAcquire(out var wallReason) && wallReason.Contains("blocked"), wallReason);
            world.Monster.Teleport(new Vector3(2, 0, 0), Vector3.right);
            Check("near_does_not_see_through_wall", !SandboxPerception.Perceived(world.Monster, world.Find("k_hidden")));
            for (int i = 0; i < 30; i++)
            {
                world.Monster.MoveToward(new Vector3(4, 0, 0), 3, .025f, .05f, out _);
                yield return null;
            }
            Check("monster_motor_cannot_cross_wall", world.Monster.transform.position.x < 2.8f, world.Monster.transform.position.ToString());
            world.Monster.Teleport(new Vector3(2.45f, 0, 0), Vector3.right);
            var heldKey = world.Find("k1"); heldKey.Attach(world.Monster.Entity, world.Monster.Hand);
            bool placed = SandboxExecutor.TryFindDropPosition(world.Monster, heldKey, new Vector3(4, 0, 0), 4, out var dropPoint);
            Check("drop_throw_cannot_cross_adjacent_wall", !placed || dropPoint.x + heldKey.Body.bounds.extents.magnitude < 2.825f, dropPoint.ToString());
            var projectile = SandboxProjectile.Launch(world, world.Monster, new Vector3(4, 1.5f, 0));
            yield return new WaitForSecondsRealtime(.3f);
            Check("fireball_hits_adjacent_wall", projectile == null);
            app.ResetRoom(); yield return null;
            var key = world.Find("k1");
            key.transform.position = new Vector3(0, .18f, 2);
            Physics.SyncTransforms();
            Check("near_rear_item_perceived", SandboxPerception.Perceived(world.Monster, key));
            key.transform.position = new Vector3(0, .18f, 5);
            Physics.SyncTransforms();
            Check("far_rear_item_excluded", !SandboxPerception.Perceived(world.Monster, key));
            app.ResetRoom(); yield return null;
            Check("begin_listening", world.BeginCast(out _) && world.CountStatus("Listening") == 1);
            var listening = world.EndCast();
            string cancelled = listening.Count > 0 ? listening[0].Id : null;
            JObject snapshot = listening.Count > 0 ? listening[0].Scene : new JObject { ["perceived"] = new JArray() };
            Check("hidden_key_not_in_perceived_snapshot", !snapshot["perceived"].Any(e => e.Value<string>("handle") == "k_hidden"));
            world.Cancel();
            Check("cancelled_lease_cannot_apply", !world.ApplyOutcome(cancelled, new JObject()));
            byte[] wav = VoiceCapture.EncodeWav(new[] { 1f, -1f, .5f, .5f }, 2, 16000);
            Check("wav_mono_pcm16_header_and_downmix", wav.Length == 48 && BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt16(wav, 44) == 0);

            float timeout = Time.realtimeSinceStartup + 120;
            while (!app.Client.Ready && Time.realtimeSinceStartup < timeout) yield return new WaitForSecondsRealtime(.25f);
            Check("real_local_models_ready", app.Client.Ready, app.Client.Health);
            if (app.Client.Ready)
            {
                yield return Command("Dance", null, "typed_dance");
                Check("dance_interpreted", (string)app.LastResponse?["outcome"]?["steps"]?[0]?["args"]?["pose"] == "dance");
                yield return new WaitForSecondsRealtime(.25f);
                Check("dance_executed", world.Monster.Pose == "dance" || app.Logs.Any(s => s.Contains("dance") && s.Contains("complete")), world.Monster.Pose);
                if (!string.IsNullOrEmpty(voicePath) && File.Exists(voicePath))
                {
                    yield return Command(null, File.ReadAllBytes(voicePath), "synthetic_voice_pickup_give");
                    float end = Time.realtimeSinceStartup + 4.8f;
                    while (Time.realtimeSinceStartup < end && world.Find("k1").Owner != world.PlayerEntity) yield return null;
                    Check("exact_key_transferred_to_speaker", world.Find("k1").Owner == world.PlayerEntity);
                }
                else Check("synthetic_voice_fixture_provided", false, "Pass -brainwash-smoke-wav containing Pick up the key and give it to me.");
                yield return Command("Go to the key then dance then wave", null, "typed_three_steps");
                Check("three_steps_interpreted", (app.LastResponse?["outcome"]?["steps"] as JArray)?.Count == 3);
                yield return new WaitForSecondsRealtime(4.9f);
                Check("three_step_execution_completed", app.Logs.Any(s => s.Contains("Completed step 3: pose")), world.LastMessage);
            }
            app.ResetRoom(); yield return null;
            world.BeginCast(out _); var expiring = world.EndCast(); string expired = expiring.Count > 0 ? expiring[0].Id : null;
            yield return new WaitForSecondsRealtime(5.15f);
            Check("thinking_expiry_rejects_late_reply", !world.ApplyOutcome(expired, new JObject()));
            app.ResetRoom(); yield return null;
            app.AddLog("Functional smoke complete: " + (failed ? "FAIL" : "PASS") + ". Details saved locally.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, new JObject { ["at"] = DateTime.UtcNow.ToString("o"), ["unity"] = Application.unityVersion, ["passed"] = !failed, ["checks"] = checks, ["notes"] = "Local functional smoke. Voice input is synthesized, not a real microphone/accent/noise playtest." }.ToString());
            string screenshot = Path.ChangeExtension(reportPath, ".png");
            ScreenCapture.CaptureScreenshot(screenshot);
            yield return new WaitForSecondsRealtime(1);
            Application.Quit(failed ? 1 : 0);
        }

        IEnumerator Command(string text, byte[] audio, string name)
        {
            app.ResetRoom(); yield return null;
            if (audio == null) app.SendPreset(text);
            else
            {
                if (!app.World.BeginCast(out var reason)) { Check(name, false, reason); yield break; }
                var sent = app.World.EndCast();
                if (sent.Count != 1) { Check(name, false, sent.Count + " listeners"); app.World.Cancel(); yield break; }
                string lease = sent[0].Id;
                app.Client.Submit(lease, sent[0].Scene, null, audio, (response, error) => {
                    if (error != null) { app.AddLog(error); app.World.Cancel(); return; }
                    app.Transcript = response.Value<string>("transcript");
                    app.World.ApplyOutcome(lease, response["compiled"] as JObject);
                    app.AddLog("Synthetic voice transcript: " + app.Transcript);
                });
            }
            float end = Time.realtimeSinceStartup + 6;
            while (app.World.CountStatus("Thinking") > 0 && Time.realtimeSinceStartup < end) yield return null;
            Check(name, app.World.CountStatus("Brainwashed") > 0 && (string)app.World.LastCompiled?["disposition"] == "execute", app.World.LastCompiled?.ToString());
        }
        void Check(string name, bool pass, string detail = null)
        {
            checks.Add(new JObject { ["name"] = name, ["pass"] = pass, ["detail"] = detail });
            if (!pass) failed = true;
            Debug.Log("BRAINWASH_SMOKE " + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }
    }
}
