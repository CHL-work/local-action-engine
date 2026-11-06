using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    // Explicit CLI-only integration harness. A supplied PCM fixture replaces the
    // microphone, but acquisition, input-source ownership, release, WAV encoding,
    // HTTP, STT, Laya, deadline validation and execution use the gameplay path.
    public sealed class VoiceWorkflowSmoke : MonoBehaviour
    {
        readonly JArray checks = new JArray();
        SandboxBootstrap app;
        FixtureRecorder recorder;
        string reportPath, wavPath;
        float[] speech;
        bool failed;
        const string StaleText = "Pick up the key and give it to me";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallWhenRequested()
        {
            if (Environment.GetCommandLineArgs().Contains("-brainwash-voice-smoke"))
                new GameObject("Explicit voice workflow smoke").AddComponent<VoiceWorkflowSmoke>();
        }

        IEnumerator Start()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (arguments[i] == "-brainwash-voice-smoke") reportPath = arguments[i + 1];
                if (arguments[i] == "-brainwash-voice-wav") wavPath = arguments[i + 1];
            }
            if (string.IsNullOrWhiteSpace(reportPath)) yield break;
            yield return null;
            app = FindFirstObjectByType<SandboxBootstrap>();
            if (app == null) { Check("sandbox_available", false); yield return SaveAndExit(); yield break; }
            try { speech = ReadFixture(wavPath); }
            catch (Exception error) { Check("three_second_dance_fixture", false, error.Message); }
            if (speech == null) { yield return SaveAndExit(); yield break; }
            Check("three_second_dance_fixture", true, "Explicit PCM fixture; no microphone or OS key injection.");
            recorder = new FixtureRecorder();
            app.Recorder = recorder;
            yield return ServiceIdle();
            Check("real_stt_laya_ready", ServiceReady, app.Client.Health);
            if (!ServiceReady) { yield return SaveAndExit(); yield break; }

            yield return Prepare(speech);
            float mp = app.World.MP, pressed = Time.realtimeSinceStartup;
            var imp = app.World.Monster;
            Check("b_press_starts_voice_and_listening", app.BeginHoldVoice(VoiceInputSource.KeyboardHold)
                && recorder.Recording && app.IsVoiceInputActive && app.World.Casting && app.World.StatusOf(imp) == "Listening");
            string lease = app.World.LeaseOf(imp)?.Id;
            app.EndHoldVoice(VoiceInputSource.PointerHold);
            Check("unrelated_pointer_release_does_not_end_b_hold", recorder.Recording && app.World.StatusOf(imp) == "Listening");
            yield return new WaitForSecondsRealtime(3.05f);
            // One MP per second held, measured against the time actually held (a
            // slow frame can make the wait run long).
            float held = Time.realtimeSinceStartup - pressed;
            Check("three_second_hold_drains_only_cast_mp", recorder.Recording && app.World.StatusOf(imp) == "Listening"
                && mp - app.World.MP >= 2.8f && Mathf.Abs(mp - app.World.MP - held) < .25f, "MP spent=" + (mp - app.World.MP) + " in " + held.ToString("0.00") + " s");
            int sends = VoiceSendCount;
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            float releasedMp = app.World.MP;
            Check("b_release_immediately_seals_and_sends_audio", !recorder.Recording && !app.IsVoiceInputActive && !app.World.Casting
                && app.World.StatusOf(imp) == "Thinking" && app.Client.Busy && VoiceSendCount == sends + 1);
            Check("release_encodes_only_captured_three_seconds", recorder.LastAudioSeconds >= 3 && recorder.LastAudioSeconds < 3.5f,
                recorder.LastAudioSeconds.ToString("0.000"));
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            Check("second_release_cannot_submit_twice", recorder.SliceCount == 1 && VoiceSendCount == sends + 1);
            yield return new WaitForSecondsRealtime(.25f);
            Check("release_stops_listening_mp_drain", Mathf.Abs(app.World.MP - releasedMp) < .02f);
            yield return ResponseFinished("three_second_release");
            Check("released_audio_uses_real_stt_and_laya", lease != null && app.Client.LastResponse?.Value<string>("requestId") == lease
                && (int?)app.Client.LastResponse?["timings"]?["questionCount"] > 0
                && (string)app.World.LastCompiled?["disposition"] == "execute", app.Client.LastResponse?.ToString());
            Check("recorded_words_replace_stale_text", app.Transcript.IndexOf("dance", StringComparison.OrdinalIgnoreCase) >= 0
                && app.Transcript != StaleText, app.Transcript);
            yield return new WaitForSecondsRealtime(.2f);
            Check("voice_command_causes_monster_action", (string)app.LastResponse?["outcome"]?["steps"]?[0]?["args"]?["pose"] == "dance"
                && (app.World.Monster.Pose == "dance" || app.Logs.Any(log => log.Contains("Completed step 1: pose"))), app.World.Monster.Pose);

            yield return Prepare(new float[16000]);
            Vector3 keyBefore = app.World.Find("k1").transform.position;
            Check("silence_hold_begins", app.BeginHoldVoice(VoiceInputSource.KeyboardHold));
            lease = app.World.LeaseOf(app.World.Monster)?.Id;
            yield return new WaitForSecondsRealtime(.6f);
            sends = VoiceSendCount;
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            Check("silent_capture_rejected_before_network_or_stale_text", !app.Client.Busy && VoiceSendCount == sends
                && app.World.Leases.Count == 0 && !recorder.Recording && !app.IsVoiceInputActive
                && app.Client.LastResponse?.Value<string>("requestId") != lease
                && app.Transcript == "No voice command submitted.", app.Outcome);
            Check("silence_has_no_stale_item_effect", app.World.Find("k1").Owner == null
                && Vector3.Distance(keyBefore, app.World.Find("k1").transform.position) < .02f);

            yield return CancellationCase("escape_cancel", () => app.Cancel());
            yield return CancellationCase("room_reset", () => app.ResetRoom());
            yield return CancellationCase("focus_loss", () => app.CancelVoiceForFocusLoss());

            yield return Prepare(new float[16000 * VoiceCapture.MaxSeconds]);
            Check("maximum_duration_hold_begins", app.BeginHoldVoice(VoiceInputSource.KeyboardHold));
            sends = VoiceSendCount;
            float cutoff = Time.realtimeSinceStartup + VoiceCapture.MaxSeconds + 1;
            while (recorder.Recording && Time.realtimeSinceStartup < cutoff) yield return null;
            Check("maximum_duration_consumes_recording_once", !recorder.Recording && !app.IsVoiceInputActive
                && recorder.SliceCount == 1 && VoiceSendCount == sends
                && recorder.LastAudioSeconds <= VoiceCapture.MaxSeconds, recorder.LastAudioSeconds.ToString("0.000"));
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            Check("release_after_silent_limit_does_not_send_stale_text", VoiceSendCount == sends && recorder.SliceCount == 1
                && !app.World.Casting && app.World.Leases.Count == 0);

            yield return Prepare(new float[16000]);
            Check("lost_sight_hold_begins", app.BeginHoldVoice(VoiceInputSource.KeyboardHold));
            yield return new WaitForSecondsRealtime(.3f);
            sends = VoiceSendCount;
            app.Teleport(new Vector3(0, 0, -6), 180);
            cutoff = Time.realtimeSinceStartup + 1;
            while (recorder.Recording && Time.realtimeSinceStartup < cutoff) yield return null;
            // The listener that lost eye contact is sent alone; its window was silent,
            // so it goes back to Normal. The cast and its recording carry on.
            Check("lost_sight_sends_only_that_listener_without_text_fallback", recorder.Recording && app.IsVoiceInputActive && app.World.Casting
                && recorder.SliceCount == 1 && VoiceSendCount == sends && app.World.Leases.Count == 0);
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            Check("release_after_lost_sight_does_not_send_stale_text", VoiceSendCount == sends && !recorder.Recording && !app.World.Casting);

            yield return Prepare(speech);
            Check("fresh_b_hold_after_cancellation_and_silence", app.BeginHoldVoice(VoiceInputSource.KeyboardHold));
            string repeatedLease = app.World.LeaseOf(app.World.Monster)?.Id;
            yield return new WaitForSecondsRealtime(3.05f);
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            yield return ResponseFinished("repeated_cast");
            Check("repeated_cast_has_new_lease_and_real_action", repeatedLease != null && repeatedLease != lease
                && app.Client.LastResponse?.Value<string>("requestId") == repeatedLease
                && (string)app.World.LastCompiled?["disposition"] == "execute"
                && (string)app.LastResponse?["outcome"]?["steps"]?[0]?["args"]?["pose"] == "dance", app.Client.LastResponse?.ToString());
            // One cast, several listeners: one recording window and one speech
            // recognition reach every monster meeting your eyes, and each gets its
            // own interpretation of the words.
            yield return Prepare(speech);
            var world = app.World;
            var red = world.Monsters.First(m => m.Entity.Species == "imp");
            var goblin = world.Monsters.First(m => m.Entity.Species == "goblin");
            var golem = world.Monsters.First(m => m.Entity.Species == "ice_golem");
            red.Teleport(Vector3.zero, Vector3.back); goblin.Teleport(new Vector3(-1.5f, 0, .5f), Vector3.back); golem.Teleport(new Vector3(1.2f, 0, 2.5f), Vector3.back);
            app.Teleport(new Vector3(0, 0, -6), 0); Physics.SyncTransforms(); yield return null;
            sends = VoiceSendCount;
            Check("hold_b_reaches_three_listeners", app.BeginHoldVoice(VoiceInputSource.KeyboardHold) && world.CountStatus("Listening") == 3, world.CountStatus("Listening") + " listening");
            yield return new WaitForSecondsRealtime(2.2f);
            app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            Check("three_listeners_share_one_recording_window", VoiceSendCount == sends + 1 && recorder.SliceCount == 1 && world.CountStatus("Thinking") == 3,
                VoiceSendCount - sends + " sends, " + recorder.SliceCount + " windows, " + world.CountStatus("Thinking") + " thinking");
            yield return ResponseFinished("three_listeners");
            yield return new WaitForSecondsRealtime(.2f);
            var obeying = new[] { red, goblin, golem };
            Check("each_listener_executes_the_spoken_order", obeying.All(m => (string)world.LeaseOf(m)?.Compiled?["disposition"] == "execute"),
                string.Join("; ", obeying.Select(m => m.Entity.DisplayName + " " + world.StatusOf(m) + " " + (string)world.LeaseOf(m)?.Compiled?["disposition"])) + " · heard: " + app.Transcript);
            app.Cancel(); app.Recorder = app.Voice;
            yield return SaveAndExit();
        }

        bool ServiceReady => app.Client.Ready && !app.Client.Busy;
        int VoiceSendCount => app.Logs.Count(log => log.Contains("Voice sent:"));
        IEnumerator ServiceIdle()
        {
            float until = Time.realtimeSinceStartup + 90;
            while (!ServiceReady && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(.1f);
        }
        IEnumerator Prepare(float[] samples)
        {
            app.BackToTests(); yield return null;
            yield return ServiceIdle();
            recorder.Samples = samples; recorder.FinishCount = 0; recorder.SliceCount = 0; recorder.LastAudioSeconds = 0;
            app.Command = StaleText; app.Logs.Clear();
        }
        IEnumerator ResponseFinished(string stage)
        {
            float until = Time.realtimeSinceStartup + 7.5f;
            while ((app.Client.Busy || app.World.CountStatus("Thinking") > 0) && Time.realtimeSinceStartup < until) yield return null;
            Check(stage + "_request_finishes_within_bounded_wait", !app.Client.Busy && app.World.CountStatus("Thinking") == 0, app.World.Leases.Count + " leases");
        }
        IEnumerator CancellationCase(string name, Action cancel)
        {
            yield return Prepare(speech);
            Check(name + "_capture_begins", app.BeginHoldVoice(VoiceInputSource.KeyboardHold));
            yield return new WaitForSecondsRealtime(.2f);
            int sends = VoiceSendCount;
            cancel(); app.EndHoldVoice(VoiceInputSource.KeyboardHold);
            yield return new WaitForSecondsRealtime(.15f);
            Check(name + "_stops_capture_without_submission", !recorder.Recording && !app.IsVoiceInputActive
                && !app.World.Casting && app.World.Leases.Count == 0 && recorder.SliceCount == 0 && VoiceSendCount == sends);
        }
        IEnumerator SaveAndExit()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            File.WriteAllText(reportPath, new JObject {
                ["at"] = DateTime.UtcNow.ToString("o"), ["unity"] = Application.unityVersion,
                ["passed"] = !failed, ["checks"] = checks,
                ["notes"] = "Synthetic PCM replaces only the microphone source. Shared gameplay hold/release, actual WAV encoding, real local STT/Laya and execution are exercised. This does not verify a user's microphone, Windows device routing, physical key events, accents or noise."
            }.ToString());
            ScreenCapture.CaptureScreenshot(Path.ChangeExtension(reportPath, ".png"));
            yield return new WaitForSecondsRealtime(1); Application.Quit(failed ? 1 : 0);
        }
        void Check(string name, bool pass, string detail = null)
        {
            checks.Add(new JObject { ["name"] = name, ["pass"] = pass, ["detail"] = detail });
            failed |= !pass;
            Debug.Log("BRAINWASH_VOICE_SMOKE " + name + " " + (pass ? "PASS" : "FAIL") + " " + detail);
        }
        static float[] ReadFixture(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Pass -brainwash-voice-wav with a PCM16 mono 16 kHz WAV saying Dance, at most three seconds.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
                || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") throw new ArgumentException("Fixture is not a WAV.");
            int data = -1, size = 0; bool format = false;
            for (int offset = 12; offset + 8 <= bytes.Length;)
            {
                int count = BitConverter.ToInt32(bytes, offset + 4), start = offset + 8;
                if (count < 0 || count > bytes.Length - start) throw new ArgumentException("Truncated WAV fixture.");
                string chunk = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                if (chunk == "fmt ") format = count >= 16 && BitConverter.ToInt16(bytes, start) == 1
                    && BitConverter.ToInt16(bytes, start + 2) == 1 && BitConverter.ToInt32(bytes, start + 4) == 16000
                    && BitConverter.ToInt16(bytes, start + 14) == 16;
                if (chunk == "data") { if (data >= 0) throw new ArgumentException("Multiple recordings in fixture."); data = start; size = count; }
                offset = start + count + count % 2;
            }
            if (!format || data < 0 || size <= 0 || size % 2 != 0 || size > 3 * 16000 * 2)
                throw new ArgumentException("Fixture must be PCM16 mono 16 kHz, at most three seconds.");
            var samples = new float[size / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, data + i * 2) / 32768f;
            return samples;
        }
        sealed class FixtureRecorder : IVoiceRecorder
        {
            public float[] Samples;
            public int FinishCount, SliceCount;
            public float LastAudioSeconds;
            public bool Recording { get; private set; }
            float started;
            public float Elapsed => Recording ? Time.realtimeSinceStartup - started : 0;
            public bool Begin(out string error)
            {
                error = Recording ? "Fixture already recording." : null;
                if (error != null) return false;
                started = Time.realtimeSinceStartup; Recording = true; return true;
            }
            public byte[] Finish()
            {
                if (!Recording) throw new InvalidOperationException("No active fixture capture.");
                int frames = Mathf.Clamp(Mathf.RoundToInt(Elapsed * 16000), 1, VoiceCapture.MaxSeconds * 16000);
                var captured = new float[frames]; Array.Copy(Samples, captured, Mathf.Min(Samples.Length, frames));
                LastAudioSeconds = frames / 16000f; FinishCount++; Recording = false;
                return VoiceCapture.EncodeCapturedWav(captured, 1, 16000);
            }
            public byte[] Slice(float fromSeconds, float toSeconds)
            {
                if (!Recording) throw new InvalidOperationException("No active fixture capture.");
                SliceCount++;
                int available = Mathf.Clamp(Mathf.RoundToInt(Elapsed * 16000), 0, VoiceCapture.MaxSeconds * 16000);
                int from = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0, fromSeconds) * 16000), 0, available);
                int to = Mathf.Clamp(Mathf.CeilToInt(toSeconds * 16000), from, available);
                LastAudioSeconds = (to - from) / 16000f;
                if (to - from < VoiceCapture.MinSliceSeconds * 16000) return null;
                var captured = new float[to - from];
                for (int i = 0; i < captured.Length; i++) captured[i] = from + i < Samples.Length ? Samples[from + i] : 0;
                return VoiceCapture.EncodeCapturedWav(captured, 1, 16000);
            }
            public void Cancel() => Recording = false;
        }
    }
}
