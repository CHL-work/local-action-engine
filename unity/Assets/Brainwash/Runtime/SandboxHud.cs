using System;
using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    public sealed class SandboxHud : MonoBehaviour
    {
        public SandboxBootstrap App;
        Vector2 logScroll, outcomeScroll, transcriptScroll, controlsScroll, menuScroll;
        GUIStyle title, small, label, box, button, input, big, cardTitle, card;
        Texture2D panel, cardPanel, shade;
        List<DemoScenario> catalogue;
        string lastLog;
        bool previousControls;
        int voiceHotControl;
        readonly string[] presets = { "Dance", "Follow me", "Stop", "Move to my left", "Pick up the key", "Drop the key", "Pick up the key and give it to me", "Go to the key then dance then wave", "Attack the goblin", "Use fireball on the ice golem", "Open the door", "Fly forward" };
        bool Ended => App.World.GameOver || App.World.Victory;
        // Requests queue in the local service, so a cast never waits for an earlier one.
        bool Ready => App.Client.Ready && !Ended;
        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = true };
            label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            small = new GUIStyle(label) { fontSize = 13 };
            button = new GUIStyle(GUI.skin.button) { fontSize = 15, wordWrap = true, padding = new RectOffset(10, 10, 7, 7) };
            input = new GUIStyle(GUI.skin.textArea) { fontSize = 16, wordWrap = true, padding = new RectOffset(10, 10, 8, 8) };
            box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 14, 14) };
            panel = new Texture2D(1, 1); panel.SetPixel(0, 0, new Color(.035f, .05f, .07f, .97f)); panel.Apply(); box.normal.background = panel;
            big = new GUIStyle(title) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
            cardTitle = new GUIStyle(title) { fontSize = 22 };
            card = new GUIStyle(box) { padding = new RectOffset(14, 14, 12, 12) };
            cardPanel = new Texture2D(1, 1); cardPanel.SetPixel(0, 0, new Color(.07f, .09f, .13f, .98f)); cardPanel.Apply(); card.normal.background = cardPanel;
            shade = new Texture2D(1, 1); shade.SetPixel(0, 0, new Color(.01f, .015f, .025f, .86f)); shade.Apply();
        }
        // Lets automated captures photograph the scene without panels.
        public static bool HideForCapture;

        void OnGUI()
        {
            if (App?.World == null || HideForCapture) return;
            Styles();
            // A soft click when a pressed button, slider or field is released.
            if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && GUIUtility.hotControl != 0 && voiceHotControl == 0)
                GameAudio.Play2D("ui_click", .6f, 1, .05f);
            if (Event.current.rawType == EventType.MouseUp && Event.current.button == 0 && voiceHotControl != 0)
            {
                GUIUtility.hotControl = 0; voiceHotControl = 0;
                App.EndHoldVoice(VoiceInputSource.PointerHold); Event.current.Use();
            }
            if (voiceHotControl != 0 && App.ActiveVoiceSource != VoiceInputSource.PointerHold)
            { GUIUtility.hotControl = 0; voiceHotControl = 0; }
            if (previousControls != App.ShowTestControls) { GUI.FocusControl(null); App.TextFocused = false; previousControls = App.ShowTestControls; }
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1) { GUI.FocusControl(null); App.TextFocused = false; }
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            float width = Screen.width / scale, height = Screen.height / scale;
            GUI.backgroundColor = Color.white;
            if (App.InMenu) { Menu(width, height); App.TextFocused = false; GUI.enabled = true; GUI.contentColor = Color.white; GUI.matrix = Matrix4x4.identity; return; }
            if (App.World.Scenario != null && !App.ShowTestControls) DemoHud(width, height);
            else if (App.ShowTestControls) FullControls(width, height); else CompactControls(width, height);
            GUI.Label(new Rect(width / 2 - 10, height / 2 - 15, 30, 30), "+", title);
            if (App.World.DebugCones) GUI.Label(new Rect(width * .265f + 10, height * .695f + 5, width * .47f - 20, 24), "TACTICAL VIEW — same room, physical sight cones", small);
            if (Ended) { if (App.World.Scenario != null) DemoEndScreen(width, height); else EndScreen(width, height); }
            App.TextFocused = !Ended && GUI.GetNameOfFocusedControl() == "command";
            GUI.enabled = true; GUI.contentColor = Color.white; GUI.matrix = Matrix4x4.identity;
        }
        void Resources()
        {
            Meter("HP", App.World.PlayerHP, App.World.MaxHP, new Color(.95f, .3f, .32f));
            Meter("MP", App.World.MP, App.World.MaxMP, new Color(.25f, .65f, 1f));
        }
        void Meter(string name, float value, float max, Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(100, 23, GUILayout.ExpandWidth(true));
            Color old = GUI.color; GUI.color = new Color(.12f, .16f, .21f); GUI.DrawTexture(rect, Texture2D.whiteTexture);
            Rect fill = rect; fill.width *= Mathf.Clamp01(value / max);
            GUI.color = color * .65f; GUI.DrawTexture(fill, Texture2D.whiteTexture); GUI.color = old;
            GUI.Label(new Rect(rect.x + 7, rect.y + 1, rect.width - 14, rect.height), name + "  " + Mathf.CeilToInt(value) + " / " + max, label);
        }
        void Service()
        {
            GUI.contentColor = App.Client.Ready ? new Color(.55f, 1, .7f) : new Color(1, .72f, .3f);
            GUILayout.Label(App.Client.Health, small); GUI.contentColor = Color.white;
        }
        void Sight()
        {
            var world = App.World;
            int ready = world.EligibleCount();
            world.CanAcquire(out string reason);
            GUI.contentColor = ready > 0 ? new Color(.55f, 1, .7f) : new Color(1, .7f, .4f);
            GUILayout.Label(ready > 1 ? "SIGHT: " + ready + " monsters meet your eyes — all will listen"
                : ready == 1 ? "SIGHT: 1 monster meets your eyes — ready" : "SIGHT: " + reason, label);
            GUI.contentColor = Color.white;
        }
        // Two lines at most: who obeys, who thinks, who listens, with their clocks.
        public static string LeaseSummary(SandboxWorld world)
        {
            var parts = new List<string>();
            foreach (var status in new[] { "Brainwashed", "Thinking", "Listening" })
            {
                var names = new List<string>();
                foreach (var lease in world.Leases)
                    if (lease.Status == status)
                        names.Add(lease.Monster.Entity.DisplayName + (status == "Brainwashed" ? " " + lease.ControlRemaining.ToString("0.0") + " s" : ""));
                if (names.Count > 0) parts.Add((status == "Brainwashed" ? "Obeying" : status) + ": " + string.Join(", ", names));
            }
            return string.Join(" · ", parts);
        }
        // One line per monster you are brainwashing, with its own clock.
        public static string LeaseLines(SandboxWorld world, int max = 6)
        {
            var lines = new List<string>();
            foreach (var lease in world.Leases)
            {
                if (lines.Count >= max) { lines.Add("+" + (world.Leases.Count - max) + " more"); break; }
                string clock = lease.Status == "Thinking" ? " " + lease.ThinkingRemaining.ToString("0.0") + " s" : lease.Status == "Brainwashed" ? " " + lease.ControlRemaining.ToString("0.0") + " s" : "";
                lines.Add(lease.Monster.Entity.DisplayName + " · " + (lease.Status == "Brainwashed" ? "OBEYS" : lease.Status.ToUpperInvariant()) + clock);
            }
            return string.Join("\n", lines);
        }
        void ModeButtons()
        {
            if (App.World.Scenario != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Restart demo", button)) { GUI.FocusControl(null); App.ResetRoom(); }
                if (GUILayout.Button("Main menu", button)) { GUI.FocusControl(null); App.OpenMenu(); }
                GUILayout.EndHorizontal();
                return;
            }
            if (App.World.PlayMode)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Restart play", button)) { GUI.FocusControl(null); App.StartPlay(); }
                if (GUILayout.Button("Back to tests", button)) { GUI.FocusControl(null); App.BackToTests(); }
                GUILayout.EndHorizontal();
            }
            else if (GUILayout.Button("START PLAY MODE", button, GUILayout.Height(42))) { GUI.FocusControl(null); App.StartPlay(); }
        }
        void CommandControls(bool compact)
        {
            GUI.enabled = App.IsVoiceInputActive || (Ready && !App.World.Casting);
            HoldVoiceButton(compact);
            GUI.enabled = !Ended && !App.IsVoiceInputActive;
            GUILayout.Label("Typed test command", small);
            GUI.SetNextControlName("command");
            App.Command = GUILayout.TextArea(App.Command, 300, input, GUILayout.Height(compact ? 42 : 60));
            GUILayout.BeginHorizontal();
            GUI.enabled = Ready && !App.World.Casting && !App.IsVoiceInputActive;
            if (GUILayout.Button("Send typed text", button, GUILayout.Height(34))) { GUI.FocusControl(null); App.SendTypedCommand(); }
            GUI.enabled = !Ended && (App.IsVoiceInputActive || (Ready && !App.World.Casting));
            if (GUILayout.Button(App.IsVoiceInputActive ? "Stop & send [V]" : "Toggle mic [V]", button, GUILayout.Height(34))) { GUI.FocusControl(null); App.ToggleVoice(); }
            GUI.enabled = true; GUILayout.EndHorizontal();
            string sendHint = App.ActiveVoiceSource == VoiceInputSource.Toggle ? "press V to send" : "release to send";
            int listening = App.World.CountStatus("Listening");
            // Fixed height: the preset buttons below must not move while you click them.
            GUILayout.Label(App.IsVoiceInputActive ? "RECORDING  " + App.Recorder.Elapsed.ToString("0.0") + " / 8 s · " + listening + " listening · " + sendHint
                : App.World.Leases.Count == 0 ? "No monster is brainwashed." : LeaseSummary(App.World), small, GUILayout.Height(34));
            if (App.IsVoiceInputActive)
            {
                Meter("MIC", Mathf.Clamp01(App.Voice.Level * 12) * 100, 100, new Color(.3f, 1, .65f));
                GUILayout.Label(App.Voice.Feedback, small);
            }
        }
        void HoldVoiceButton(bool compact)
        {
            string caption = App.ActiveVoiceSource == VoiceInputSource.KeyboardHold ? "RECORDING — RELEASE B TO SEND"
                : App.ActiveVoiceSource == VoiceInputSource.PointerHold ? "RECORDING — RELEASE TO SEND"
                : App.ActiveVoiceSource == VoiceInputSource.Toggle ? "RECORDING — PRESS V TO SEND"
                : "HOLD TO BRAINWASH  [hold B]";
            Rect rect = GUILayoutUtility.GetRect(new GUIContent(caption), button, GUILayout.Height(compact ? 38 : 44), GUILayout.ExpandWidth(true));
            int control = GUIUtility.GetControlID("Brainwash voice hold".GetHashCode(), FocusType.Passive, rect);
            var current = Event.current;
            if (GUI.enabled && current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                GUI.FocusControl(null); App.TextFocused = false;
                if (App.BeginHoldVoice(VoiceInputSource.PointerHold)) { GUIUtility.hotControl = control; voiceHotControl = control; }
                current.Use();
            }
            if (current.type == EventType.Repaint)
            {
                Color previous = GUI.color;
                if (!GUI.enabled) GUI.color = new Color(previous.r * .65f, previous.g * .65f, previous.b * .65f, previous.a);
                button.Draw(rect, new GUIContent(caption), GUI.enabled && rect.Contains(current.mousePosition), App.IsVoiceInputActive, false, false);
                GUI.color = previous;
            }
        }
        void Preset(string command, string caption = null)
        {
            GUI.enabled = Ready && !App.World.Casting && !App.IsVoiceInputActive;
            if (GUILayout.Button(caption ?? command, button, GUILayout.Height(31))) { GUI.FocusControl(null); App.SendPreset(command); }
            GUI.enabled = true;
        }
        void FullControls(float width, float height)
        {
            GUILayout.BeginArea(new Rect(18, 18, 335, height - 36), box);
            GUILayout.Label("BRAINWASH", title); GUILayout.Label(App.World.PlayMode ? "PLAY MODE · ENEMIES ARE ACTIVE" : "TEST MODE · ENEMIES ARE STILL", small);
            Resources(); ModeButtons(); controlsScroll = GUILayout.BeginScrollView(controlsScroll);
            Service(); GUILayout.Label("Speech: " + App.Client.SpeechModel, small);
            GUILayout.Label("Aim at a monster's face to acquire", small); CommandControls(false);
            GUILayout.Label("Mic: " + App.Voice.DeviceLabel, small);
            GUI.enabled = !App.IsVoiceInputActive;
            if (GUILayout.Button("Change microphone", GUI.skin.button))
            {
                var devices = App.Voice.Devices;
                if (devices.Length > 0) { int index = Array.IndexOf(devices, App.Voice.Device); App.Voice.Device = index >= devices.Length - 1 ? null : devices[index + 1]; }
                else App.AddLog("No microphone found.");
            }
            GUI.enabled = true; GUILayout.Space(6); GUILayout.Label("ONE-CLICK COMMANDS", small);
            foreach (var preset in presets) Preset(preset);
            GUILayout.EndScrollView();
            GUILayout.Label("WASD / Shift · Hold right mouse to look\nHold B: speak, release: send · V: toggle mic\nF staff · E interact · R reset · Esc cancel · M menu", small); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(width - 360, 18, 342, height - 36), box);
            GUILayout.Label("LIVE DIAGNOSTICS", title); Sight();
            GUILayout.Label(App.World.Leases.Count == 0 ? "No listeners · nobody brainwashed" : LeaseLines(App.World, 4), small, GUILayout.Height(72));
            App.World.DebugCones = GUILayout.Toggle(App.World.DebugCones, "Show sight cones and tactical view");
            GUILayout.Label("Cyan: player cone · Amber: listener cone\nWalls block both cone and Near sight.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", button)) { GUI.FocusControl(null); App.Cancel(); }
            if (GUILayout.Button("Reset [R]", button)) { GUI.FocusControl(null); App.ResetRoom(); }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Hide test controls [Tab]", button)) App.ShowTestControls = false;
            GUILayout.Label(App.World.ObjectiveText, label); GUILayout.Space(5); GUILayout.Label("TRANSCRIPT", small);
            transcriptScroll = GUILayout.BeginScrollView(transcriptScroll, GUILayout.Height(55)); GUILayout.Label(App.Transcript, label); GUILayout.EndScrollView();
            GUILayout.Label(App.Timing, small); GUILayout.Label("MODEL PROPOSAL / EXECUTION", small);
            outcomeScroll = GUILayout.BeginScrollView(outcomeScroll, GUILayout.Height(135)); GUILayout.Label(App.Outcome, small); GUILayout.EndScrollView();
            GUILayout.Label(App.World.LastMessage, label); GUILayout.Space(5); GUILayout.Label("DEBUG LOG", small);
            if (App.Logs.Count > 0 && App.Logs[App.Logs.Count - 1] != lastLog) { lastLog = App.Logs[App.Logs.Count - 1]; logScroll.y = float.MaxValue; }
            logScroll = GUILayout.BeginScrollView(logScroll);
            for (int i = Math.Max(0, App.Logs.Count - 45); i < App.Logs.Count; i++) GUILayout.Label(App.Logs[i], small);
            GUILayout.EndScrollView(); if (GUILayout.Button("Export log JSON", button)) App.ExportLog(); GUILayout.EndArea();
            GUI.Box(new Rect(370, 18, width - 748, 72), "", box);
            GUI.Label(new Rect(385, 26, width - 778, 60), App.World.PlayMode ? App.World.ObjectiveText : "Aim at any monster. Test mode keeps enemies still. Start Play to enter the arena.", label);
        }
        void CompactControls(float width, float height)
        {
            GUILayout.BeginArea(new Rect(18, 18, 360, 180), box);
            GUILayout.Label(App.World.PlayMode ? "ESCAPE THE ARENA" : "BRAINWASH TESTS", title); Resources(); GUILayout.Label(App.World.ObjectiveText, label); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(width - 328, 18, 310, 180), box);
            Service(); if (GUILayout.Button("Test controls / debug [Tab]", button)) App.ShowTestControls = true;
            ModeButtons(); App.World.DebugCones = GUILayout.Toggle(App.World.DebugCones, "Show sight cones"); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(18, height - 432, 380, 414), box);
            Sight(); CommandControls(true);
            GUILayout.BeginHorizontal(); Preset("Follow me", "Follow"); Preset("Drop the key", "Drop key"); Preset("Stop"); GUILayout.EndHorizontal(); GUILayout.EndArea();
            GUILayout.BeginArea(new Rect(width - 378, height - 185, 360, 167), box);
            GUILayout.Label(App.World.LastMessage, label);
            GUILayout.Label("WASD move · Shift run\nHold right mouse to look\nF staff attack · E pick up / exit\nHold B to speak; release to send\nV toggle mic · R restart", small); GUILayout.EndArea();
        }
        void EndScreen(float width, float height)
        {
            GUI.enabled = true;
            GUILayout.BeginArea(new Rect(width / 2 - 220, height / 2 - 130, 440, 250), box);
            GUILayout.Label(App.World.Victory ? "YOU ESCAPED" : "YOU WERE DEFEATED", title);
            GUILayout.Label(App.World.Victory ? "The key opened the exit. Try another round or return to the command tests." : "Use cover, brainwash a nearby enemy, and keep an eye on HP and MP.", label); GUILayout.Space(15);
            if (GUILayout.Button("PLAY AGAIN", button, GUILayout.Height(42))) { GUI.FocusControl(null); App.StartPlay(); }
            if (GUILayout.Button("BACK TO TESTS", button, GUILayout.Height(36))) { GUI.FocusControl(null); App.BackToTests(); }
            GUILayout.EndArea();
        }
        // ---------- Demo selection menu ----------
        void Menu(float width, float height)
        {
            if (catalogue == null) { catalogue = new List<DemoScenario>(); foreach (var factory in DemoScenario.Catalogue) catalogue.Add(factory()); }
            GUI.DrawTexture(new Rect(0, 0, width, height), shade);
            GUI.Label(new Rect(0, 18, width, 50), "PROJECT BRAINWASH — FIVE DEMOS", big);
            GUI.Label(new Rect(width * .1f, 70, width * .8f, 46), "Same core in every demo: meet a monster's eyes, hold B and speak. It obeys for five seconds — up to three actions — then remembers who hurt it. Each demo builds a different game around that.", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
            // Five tall cards in one row leave room for every description.
            float gap = 14, cardWidth = (width - 40 - gap * (catalogue.Count - 1)) / catalogue.Count, cardHeight = height - 128 - 132;
            for (int i = 0; i < catalogue.Count; i++)
                Card(new Rect(20 + i * (cardWidth + gap), 124, cardWidth, cardHeight), catalogue[i], i + 1);
            GUILayout.BeginArea(new Rect(width * .08f, height - 118, width * .84f, 104), box);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(width * .42f));
            Service();
            // Read the status before the button: a click sets it, and IMGUI needs the
            // same controls in every event of a frame.
            string launcherStatus = App.Launcher?.Status;
            LauncherButton("Start local AI service (Laya + speech)", "Starting local AI service...");
            if (!string.IsNullOrEmpty(launcherStatus)) GUILayout.Label(launcherStatus, small);
            GUILayout.Label("Mic: " + App.Voice.DeviceLabel + " · typed commands always work", small);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Classic test room", button, GUILayout.Height(36))) { GUI.FocusControl(null); App.BackToTests(); }
            if (GUILayout.Button("Classic key arena", button, GUILayout.Height(36))) { GUI.FocusControl(null); App.StartPlay(); }
            if (GUILayout.Button("Monster gallery", button, GUILayout.Height(36))) { GUI.FocusControl(null); App.StartDemo("gallery"); }
            GUILayout.EndHorizontal();
            GUILayout.Label("In any demo: M returns here · R restarts · Tab shows the full debug panel", small);
            SoundControls();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // Master volume and mute (N toggles mute anywhere).
        void SoundControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Sound", small, GUILayout.Width(44));
            float volume = GameAudio.Volume;
            float changed = GUILayout.HorizontalSlider(volume, 0, 1, GUILayout.Width(150));
            if (Mathf.Abs(changed - volume) > .001f) GameAudio.Volume = changed;
            if (GUILayout.Button(GameAudio.Muted ? "Unmute [N]" : "Mute [N]", button, GUILayout.Width(110))) { GUI.FocusControl(null); GameAudio.Muted = !GameAudio.Muted; }
            GUILayout.EndHorizontal();
        }

        void Card(Rect rect, DemoScenario demo, int number)
        {
            GUILayout.BeginArea(rect, card);
            GUILayout.Label(number + ". " + demo.Title, cardTitle);
            GUI.contentColor = new Color(.6f, .85f, 1f); GUILayout.Label(demo.Tagline, label); GUI.contentColor = Color.white;
            GUILayout.Label(demo.Description, small);
            foreach (var line in demo.Mechanics) GUILayout.Label("• " + line, small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("PLAY", button, GUILayout.Height(38))) { GUI.FocusControl(null); App.StartDemo(demo.Id); }
            GUILayout.EndArea();
        }

        // Start the service, or restart an older one that is still running.
        void LauncherButton(string start, string starting)
        {
            if (App.Client.Ready || App.Launcher == null || !App.Launcher.CanLaunch) return;
            bool busy = App.Launcher.Starting || App.Launcher.Restarting;
            GUI.enabled = !busy;
            string caption = busy ? starting : App.Client.Outdated ? "Restart local AI service (update)" : start;
            if (GUILayout.Button(caption, button))
            {
                if (App.Client.Outdated) App.Launcher.Restart(PortFromEndpoint()); else App.Launcher.Launch(PortFromEndpoint());
            }
            GUI.enabled = true;
        }

        int PortFromEndpoint()
        {
            var endpoint = App.Client.Endpoint; int colon = endpoint.LastIndexOf(':');
            return colon >= 0 && int.TryParse(endpoint.Substring(colon + 1), out int port) ? port : 4174;
        }

        // ---------- In-demo HUD ----------
        void DemoHud(float width, float height)
        {
            var world = App.World; var demo = world.Scenario;
            GUILayout.BeginArea(new Rect(18, 18, 400, 300), box);
            GUILayout.Label(demo.Title.ToUpperInvariant(), title);
            GUILayout.Label(world.ObjectiveText, label);
            Resources();
            foreach (var line in demo.StatusLines()) GUILayout.Label(line, small);
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(width - 348, 18, 330, 186), box);
            Service();
            if (!App.Client.Ready && App.Launcher != null && App.Launcher.CanLaunch)
            {
                string launcherStatus = App.Launcher.Status;
                LauncherButton("Start local AI service", "Starting local AI...");
                if (!string.IsNullOrEmpty(launcherStatus)) GUILayout.Label(launcherStatus, small);
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Menu [M]", button)) { GUI.FocusControl(null); App.OpenMenu(); }
            if (GUILayout.Button("Restart [R]", button)) { GUI.FocusControl(null); App.ResetRoom(); }
            if (GUILayout.Button("Debug [Tab]", button)) { GUI.FocusControl(null); App.ShowTestControls = true; }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            int rows = 0;
            for (int i = 0; i < demo.Presets.Length; i++, rows++)
                if (i + 1 < demo.Presets.Length && demo.Presets[i].Length <= 22 && demo.Presets[i + 1].Length <= 22) i++;
            float bottomHeight = Mathf.Min(height - 340, 236 + rows * 36 + (demo.ChantEnabled ? 96 : 0));
            GUILayout.BeginArea(new Rect(18, height - bottomHeight - 18, 410, bottomHeight), box);
            Sight(); CommandControls(true);
            var presets = demo.Presets;
            if (presets.Length > 0)
            {
                GUILayout.Label("TRY SAYING", small);
                for (int i = 0; i < presets.Length; i++)
                {
                    // Short phrases share a row; long ones get the full width.
                    bool pair = i + 1 < presets.Length && presets[i].Length <= 22 && presets[i + 1].Length <= 22;
                    GUILayout.BeginHorizontal();
                    Preset(presets[i]);
                    if (pair) Preset(presets[++i]);
                    GUILayout.EndHorizontal();
                }
            }
            if (demo.ChantEnabled)
            {
                GUILayout.Space(4);
                GUI.contentColor = new Color(1, .9f, .55f); GUILayout.Label("SCRIPTURE: " + demo.ChantLine, label); GUI.contentColor = Color.white;
                GUILayout.BeginHorizontal();
                GUI.enabled = !Ended && !world.Casting && !App.IsVoiceInputActive;
                if (GUILayout.Button("Chant line (typed)", button)) { GUI.FocusControl(null); App.TypedChant(demo.ChantLine); }
                if (GUILayout.Button("Chant my text", button)) { GUI.FocusControl(null); App.TypedChant(App.Command); }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.Label(App.ChantStatus, small);
            }
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(width - 398, height - 228, 380, 210), box);
            GUILayout.Label(world.LastMessage, label);
            GUILayout.Label(demo.Controls, small);
            GUILayout.EndArea();

            string centre = null; bool obeying = false;
            int listening = world.CountStatus("Listening"), thinking = world.CountStatus("Thinking"), obey = world.CountStatus("Brainwashed");
            if (world.Casting) centre = listening == 0 ? "LOOK A MONSTER IN THE EYES" : "LISTENING — " + listening + (listening == 1 ? " MONSTER HEARS YOU" : " MONSTERS HEAR YOU");
            else if (thinking > 0) centre = "THINKING  " + thinking + (thinking == 1 ? " MONSTER" : " MONSTERS");
            else if (obey > 0)
            {
                obeying = true; float longest = 0; string only = null;
                foreach (var lease in world.Leases) if (lease.Status == "Brainwashed") { longest = Mathf.Max(longest, lease.ControlRemaining); only = lease.Monster.Entity.DisplayName; }
                centre = (obey == 1 ? only.ToUpperInvariant() + " OBEYS" : obey + " MONSTERS OBEY") + "  " + longest.ToString("0.0") + " s";
            }
            if (centre != null)
            {
                GUI.contentColor = obeying ? new Color(.9f, .55f, 1f) : new Color(.55f, .95f, 1f);
                GUI.Label(new Rect(width / 2 - 340, height * .2f, 680, 40), centre, new GUIStyle(title) { alignment = TextAnchor.MiddleCenter });
                if (world.Leases.Count > 1 || (world.Casting && (thinking + obey) > 0))
                    GUI.Label(new Rect(width / 2 - 340, height * .2f + 66, 680, 120), LeaseLines(world, 5), new GUIStyle(small) { alignment = TextAnchor.UpperCenter });
                GUI.contentColor = Color.white;
            }
            if (!string.IsNullOrEmpty(App.Transcript) && App.Transcript != "No command yet.")
                GUI.Label(new Rect(width / 2 - 360, height * .2f + 40, 720, 28), "“" + App.Transcript + "”", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
        }

        void DemoEndScreen(float width, float height)
        {
            var demo = App.World.Scenario;
            GUI.enabled = true;
            GUILayout.BeginArea(new Rect(width / 2 - 260, height / 2 - 150, 520, 300), box);
            GUILayout.Label(App.World.Victory ? "VICTORY — " + demo.Title.ToUpperInvariant() : "DEFEATED", title);
            GUILayout.Label(App.World.Victory ? demo.WinSummary : demo.LoseText, label);
            GUILayout.Label("Time " + Mathf.FloorToInt(demo.Elapsed / 60) + ":" + Mathf.FloorToInt(demo.Elapsed % 60).ToString("00"), small);
            GUILayout.Space(12);
            if (GUILayout.Button("PLAY AGAIN", button, GUILayout.Height(42))) { GUI.FocusControl(null); App.StartDemo(demo.Id); }
            if (GUILayout.Button("MAIN MENU", button, GUILayout.Height(36))) { GUI.FocusControl(null); App.OpenMenu(); }
            GUILayout.EndArea();
        }

        void OnDestroy() { if (panel != null) Destroy(panel); if (cardPanel != null) Destroy(cardPanel); if (shade != null) Destroy(shade); }
    }
}
