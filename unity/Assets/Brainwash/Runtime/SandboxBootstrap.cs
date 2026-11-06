using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Brainwash
{
    public sealed class SandboxBootstrap : MonoBehaviour
    {
        public SandboxWorld World { get; private set; }
        public InferenceClient Client { get; private set; }
        public VoiceCapture Voice { get; private set; }
        // Defaults to the real microphone. A recorder can be supplied by the
        // integration harness without bypassing input, lease or submission code.
        public IVoiceRecorder Recorder { get; set; }
        public bool IsVoiceInputActive => voiceInput.Active;
        public bool IsVoiceRecording => Recorder != null && Recorder.Recording;
        public VoiceInputSource ActiveVoiceSource => voiceInput.Source;
        public Transform Player { get; private set; }
        public Camera View { get; private set; }
        public Camera Overview { get; private set; }
        public string Command = "Pick up the key and give it to me";
        public string Transcript = "No command yet.";
        public string Outcome = "Acquire a monster, then send a command.";
        public string Timing = "";
        public bool TextFocused;
        public bool ShowTestControls = true;
        public readonly List<string> Logs = new List<string>();
        public JObject LastResponse { get; private set; }
        CharacterController motor;
        float pitch, gravity;
        readonly VoiceInputState voiceInput = new VoiceInputState();
        Transform staff;
        TrailRenderer staffTrail;
        float staffSwingEnd;
        // Demo selection and demo-only player verbs.
        public bool InMenu = true;
        public Transform HoldPoint { get; private set; }
        public LocalServiceLauncher Launcher { get; private set; }
        public bool Chanting => chantActive;
        public string ChantStatus = "";
        public bool Carried => carriedBy != null;
        SandboxMonster carriedBy;
        Vector3 external;
        float airSpeed, nextNoise, kickEnd;
        Transform kickBoot;
        bool chantActive, chantPending, voiceChant;
        // A typed chant takes about as long as speaking the line, so the
        // keyboard test path keeps the "one mouth" cost.
        string typedChant;
        float typedChantEnd;
        Light sun;
        // Game feel: head bob, landing dip, strafe roll, screen shake and sprint FOV.
        // Smoke runs (Automated) skip the shake so automated aiming stays exact.
        public static bool Automated;
        public static event Action<Vector3, float> PlayerFootstep;
        public static event Action<float> PlayerLanded;
        static SandboxBootstrap active;
        float bobPhase, bobWeight, landDip, landVelocity, shakeAmount, shakeClock, viewRoll, fovPunch, fallSpeed;
        Vector3 lastFeelPosition;
        bool wasGrounded = true;
        public float Pitch => pitch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureSandbox()
        {
            if (FindFirstObjectByType<SandboxBootstrap>() == null)
                new GameObject("Brainwash sandbox").AddComponent<SandboxBootstrap>();
        }

        // Shakes the player's view; amount 0-1 (1 is a nearby explosion).
        public static void Shake(float amount) { if (active != null && !Automated) active.shakeAmount = Mathf.Clamp01(Mathf.Max(active.shakeAmount, amount)); }
        // A shake that fades with distance from the player.
        public static void ShakeAt(Vector3 point, float amount, float radius)
        {
            if (active == null || active.Player == null) return;
            float distance = Vector3.Distance(active.Player.position, point);
            if (distance < radius) Shake(amount * (1 - distance / radius));
        }
        public static void Punch(float fov) { if (active != null) active.fovPunch += fov; }

        void OnDestroy()
        {
            if (active == this) active = null;
            CreatureAnimator.Footstep -= HeavyStep; CreatureAnimator.Thud -= HeavyThud;
        }

        // Big creatures shake the view and kick up dust when they stomp or fall near you.
        static void HeavyStep(SandboxEntity entity, Vector3 point, float weight)
        {
            if (weight > .5f) ShakeAt(point, (weight - .5f) * .55f, 10);
            if (weight >= .5f) Dust(point, weight * .6f);
        }
        static void HeavyThud(SandboxEntity entity, Vector3 point, float weight)
        {
            if (weight > .3f) ShakeAt(point, weight * .45f, 10);
            if (weight >= .1f) Dust(point, weight);
        }
        static void Dust(Vector3 point, float amount)
        {
            if (active == null || active.World == null || active.World.Scenario == null || active.World.Effects == null) return;
            active.World.Effects.Dust(point, amount);
        }

        void Awake()
        {
            active = this;
            CreatureAnimator.Footstep += HeavyStep; CreatureAnimator.Thud += HeavyThud;
            Application.targetFrameRate = 90;
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.runInBackground = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var body = new GameObject("Player — WASD / hold right mouse to look");
            Player = body.transform;
            Player.position = new Vector3(0, 0, -6);
            motor = body.AddComponent<CharacterController>();
            motor.height = 1.8f; motor.radius = .35f; motor.center = Vector3.up * .9f;
            motor.stepOffset = .3f;
            var eye = new GameObject("Player camera");
            eye.tag = "MainCamera";
            eye.transform.SetParent(Player, false);
            eye.transform.localPosition = new Vector3(0, 1.6f, 0);
            View = eye.AddComponent<Camera>();
            View.fieldOfView = 75; View.nearClipPlane = .05f; View.farClipPlane = 80;
            View.clearFlags = CameraClearFlags.SolidColor;
            View.backgroundColor = new Color(.075f, .095f, .13f);
            eye.AddComponent<AudioListener>();
            var playerHand = new GameObject("Player held item socket");
            playerHand.transform.SetParent(eye.transform, false);
            playerHand.transform.localPosition = new Vector3(-.38f, -.32f, .8f);
            playerHand.transform.localRotation = Quaternion.Euler(0, 0, 25);
            staff = new GameObject("Player staff").transform;
            staff.SetParent(eye.transform, false); staff.localPosition = new Vector3(.43f, -.48f, .8f);
            if (ModelLibrary.Spawn("staff", staff, new Color(.5f, .3f, .14f)) == null)
            {
                AddStaffPart(PrimitiveType.Cylinder, new Vector3(0, .05f, 0), new Vector3(.055f, .5f, .055f), new Color(.5f, .3f, .14f));
                AddStaffPart(PrimitiveType.Sphere, new Vector3(0, .57f, 0), Vector3.one * .17f, new Color(.3f, .8f, 1f));
            }
            // A short glowing streak behind the orb while the staff swings.
            var trail = new GameObject("Staff swing trail"); trail.transform.SetParent(staff, false); trail.transform.localPosition = new Vector3(0, .58f, 0);
            staffTrail = trail.AddComponent<TrailRenderer>();
            staffTrail.time = .11f; staffTrail.minVertexDistance = .015f; staffTrail.emitting = false;
            staffTrail.widthCurve = AnimationCurve.Linear(0, .1f, 1, 0);
            staffTrail.material = new Material(Shader.Find("Sprites/Default"));
            var streak = new Gradient();
            streak.SetKeys(new[] { new GradientColorKey(new Color(.6f, .92f, 1f), 0), new GradientColorKey(new Color(.25f, .55f, 1f), 1) },
                new[] { new GradientAlphaKey(.6f, 0), new GradientAlphaKey(0, 1) });
            staffTrail.colorGradient = streak;
            staffTrail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; staffTrail.receiveShadows = false;
            var overview = new GameObject("Tactical sight view");
            overview.transform.SetPositionAndRotation(new Vector3(0, 24, 0), Quaternion.Euler(90, 0, 0));
            Overview = overview.AddComponent<Camera>();
            Overview.orthographic = true; Overview.orthographicSize = 10;
            Overview.depth = 1; Overview.rect = new Rect(.265f, .025f, .47f, .28f);
            Overview.clearFlags = CameraClearFlags.SolidColor; Overview.backgroundColor = new Color(.05f, .07f, .09f);
            var bodyMesh = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bodyMesh.name = "White speaker body"; bodyMesh.transform.SetParent(Player, false);
            bodyMesh.transform.localPosition = Vector3.up * .8f;
            bodyMesh.transform.localScale = new Vector3(.65f, .8f, .65f);
            bodyMesh.GetComponent<Collider>().enabled = false; Destroy(bodyMesh.GetComponent<Collider>());
            bodyMesh.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(.9f, .93f, 1f) };
            RenderSettings.ambientLight = new Color(.55f, .6f, .68f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            sun = new GameObject("Sandbox sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.4f;
            sun.transform.rotation = Quaternion.Euler(48, -25, 0); sun.shadows = LightShadows.Soft;
            World = gameObject.AddComponent<SandboxWorld>();
            World.App = this; World.Sun = sun;
            var hold = new GameObject("Player grab point"); hold.transform.SetParent(eye.transform, false);
            hold.transform.localPosition = new Vector3(0, -.25f, 1.3f); HoldPoint = hold.transform;
            kickBoot = new GameObject("Kick boot").transform; kickBoot.SetParent(eye.transform, false);
            kickBoot.localPosition = new Vector3(.15f, -1.1f, .6f);
            if (PropModels.Fit("boot", kickBoot, new Vector3(.22f, .2f, .45f), new Color(.3f, .2f, .12f), true) == null)
            {
                var boot = GameObject.CreatePrimitive(PrimitiveType.Cube); boot.name = "Boot"; boot.transform.SetParent(kickBoot, false);
                boot.transform.localScale = new Vector3(.22f, .2f, .45f); Destroy(boot.GetComponent<Collider>());
                boot.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(.3f, .2f, .12f) };
            }
            kickBoot.gameObject.SetActive(false);
            World.Log += AddLog;
            World.RunEnded += () => { voiceInput.Cancel(); Recorder?.Cancel(); Client?.CancelPending(); TextFocused = false; };
            World.ReleaseRequested += reason => {
                AddLog("Auto-send: " + reason);
                if (voiceInput.Active) EndAnyVoice();
                else World.CancelCast("The cast ended without a recording; nothing was sent.");
            };
            World.ListenersSealed += SendEarly;
            World.Initialize(Player, View);
            World.PlayerEntity.Body = bodyMesh.GetComponent<Renderer>();
            World.PlayerEntity.HandSocket = playerHand.transform;
            GameAudio.Create(this);
            Client = gameObject.AddComponent<InferenceClient>();
            Client.Log += AddLog;
            Voice = gameObject.AddComponent<VoiceCapture>();
            Recorder = Voice;
            gameObject.AddComponent<SandboxHud>().App = this;
            gameObject.AddComponent<SandboxSmoke>();
            gameObject.AddComponent<PlayModeSmoke>();
            gameObject.AddComponent<DemoSmoke>();
            Launcher = gameObject.AddComponent<LocalServiceLauncher>();
            string[] commandLine = Environment.GetCommandLineArgs();
            string requested = null;
            for (int i = 0; i < commandLine.Length; i++)
            {
                if (commandLine[i].StartsWith("-brainwash-") && commandLine[i].EndsWith("smoke")) { InMenu = false; Automated = true; }
                if (commandLine[i] == "-brainwash-demo" && i + 1 < commandLine.Length) requested = commandLine[i + 1];
            }
            if (requested != null) StartDemo(requested);
            else if (InMenu) { World.DebugCones = false; ShowTestControls = false; }
            AddLog("Unity 3D sandbox ready. All inference stays on this computer.");
        }

        void Update()
        {
            // Release is processed even if a text field gained focus mid-hold.
            if (Input.GetKeyUp(KeyCode.B)) EndHoldVoice(VoiceInputSource.KeyboardHold);
            if (Input.GetMouseButtonUp(0)) EndHoldVoice(VoiceInputSource.PointerHold);
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Input.GetKeyDown(KeyCode.Tab)) { ShowTestControls = !ShowTestControls; TextFocused = false; }
            if (!TextFocused && Input.GetKeyDown(KeyCode.N)) GameAudio.Muted = !GameAudio.Muted;
            if (Input.GetKeyUp(KeyCode.C)) EndChant();
            if (chantActive && !voiceChant && Time.time >= typedChantEnd)
            {
                chantActive = false; string text = typedChant; typedChant = null;
                ChantStatus = "Typed chant: \"" + text + "\"";
                World.Scenario?.OnChant(text ?? "", true);
            }
            bool demo = World.Scenario != null;
            if (InMenu) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; if (Overview != null) Overview.enabled = false; return; }
            if (!TextFocused)
            {
                if (Input.GetKeyDown(KeyCode.R)) ResetRoom();
                if (Input.GetKeyDown(KeyCode.M)) { OpenMenu(); return; }
                if (!World.GameOver && !World.Victory)
                {
                    if (Input.GetKeyDown(KeyCode.V)) ToggleVoice();
                    if (Input.GetKeyDown(KeyCode.B)) BeginHoldVoice(VoiceInputSource.KeyboardHold);
                    if (Input.GetKeyDown(KeyCode.E)) World.PlayerInteract();
                    if (Input.GetKeyDown(KeyCode.F) || (Input.GetMouseButton(1) && Input.GetMouseButtonDown(0))) Attack();
                    if (demo)
                    {
                        if (Input.GetKeyDown(KeyCode.Q) && World.Scenario.KickEnabled && World.PlayerKick()) kickEnd = Time.time + .3f;
                        if (Input.GetKeyDown(KeyCode.G)) World.PlayerGrabOrThrow(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
                        if (Input.GetKeyDown(KeyCode.C) && World.Scenario.ChantEnabled) BeginChant();
                    }
                    if (!Carried)
                    {
                        Vector3 move = Player.right * Input.GetAxisRaw("Horizontal") + Player.forward * Input.GetAxisRaw("Vertical");
                        move = Vector3.ClampMagnitude(move, 1) * (chantActive ? 1.6f : Input.GetKey(KeyCode.LeftShift) ? 5 : 3);
                        if (motor.isGrounded && gravity < 0)
                        {
                            if (demo && airSpeed > 11) World.DamagePlayer(Mathf.Round((airSpeed - 11) * 6), "The fall");
                            // A landing after a throw or blast sticks instead of sliding off ledges.
                            if (airSpeed > 3) external *= .15f;
                            airSpeed = 0; gravity = -2;
                        }
                        if (demo && motor.isGrounded && Input.GetKeyDown(KeyCode.Space)) { gravity = 5.4f; GameAudio.Play2D("jump", .7f); }
                        gravity += Physics.gravity.y * Time.deltaTime;
                        if (!motor.isGrounded) airSpeed = Mathf.Max(airSpeed, -gravity);
                        // Little air drag so ballistic throws (Ogre toss) land where aimed.
                        external = Vector3.MoveTowards(external, Vector3.zero, (motor.isGrounded ? 14 : .3f) * Time.deltaTime);
                        motor.Move((move + external + Vector3.up * gravity) * Time.deltaTime);
                    }
                }
            }
            if (Carried) FollowCarrier();
            if (demo && IsVoiceRecording && Time.time >= nextNoise && Voice != null)
            {
                nextNoise = Time.time + .4f;
                World.VoiceNoise(Mathf.Clamp(2 + Voice.Level * 90, 2, 14));
            }
            if (Input.GetMouseButton(1) && !TextFocused && !World.GameOver && !World.Victory)
            {
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
                Player.Rotate(Vector3.up, Input.GetAxis("Mouse X") * 2.1f);
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 2.1f, -75, 75);
                View.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            }
            else { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            // Keyboard look: J/L turn, I/K look down and up (arrow keys already move).
            if (!InMenu && !TextFocused && !World.GameOver && !World.Victory)
            {
                float turn = (Input.GetKey(KeyCode.L) ? 1 : 0) - (Input.GetKey(KeyCode.J) ? 1 : 0);
                float tilt = (Input.GetKey(KeyCode.K) ? 1 : 0) - (Input.GetKey(KeyCode.I) ? 1 : 0);
                if (turn != 0) Player.Rotate(Vector3.up, turn * 110 * Time.deltaTime);
                if (tilt != 0) { pitch = Mathf.Clamp(pitch + tilt * 80 * Time.deltaTime, -75, 75); View.transform.localRotation = Quaternion.Euler(pitch, 0, 0); }
            }
            if (voiceInput.Active && (Recorder == null || !Recorder.Recording))
            {
                voiceInput.Cancel(); World.CancelCast("The microphone stopped; the listeners go back to normal.");
                Transcript = "No voice command submitted."; Outcome = "Microphone stopped. Check the input device and record again."; AddLog(Outcome);
            }
            if (voiceInput.Active && Recorder.Elapsed >= VoiceCapture.MaxSeconds) EndAnyVoice();
            // The world can end a cast on its own (reset, death, the run ending).
            if (voiceInput.Active && !World.Casting) { voiceInput.Cancel(); Recorder.Cancel(); }
            if (Overview != null) Overview.enabled = World.DebugCones;
            if (staff != null) AnimateStaff();
            if (kickBoot != null)
            {
                // Kick: a fast snap forward, a short hold, a slower return.
                float u = 1 - Mathf.Clamp01((kickEnd - Time.time) / .3f);
                float k = u < .3f ? Smooth(u / .3f) : u < .45f ? 1 : 1 - Smooth((u - .45f) / .55f);
                kickBoot.gameObject.SetActive(u < 1);
                kickBoot.localPosition = new Vector3(.15f, -1.1f + k * .55f, .45f + k * .65f);
                kickBoot.localRotation = Quaternion.Euler(-35 * k, 0, 0);
            }
        }

        // ---------- Demo selection ----------
        public void StartDemo(string id)
        {
            var scenario = DemoScenario.Create(id);
            if (scenario == null) { AddLog("Unknown demo: " + id); return; }
            Cancel(); InterruptChant("The chant was abandoned."); InMenu = false; ShowTestControls = false; TextFocused = false;
            external = Vector3.zero; airSpeed = 0;
            World.StartScenario(scenario);
            Transcript = "No command yet."; Outcome = scenario.Title + " started."; Timing = ""; LastResponse = null;
            // The typed-command box starts with an order that fits this demo.
            if (scenario.Presets.Length > 0) Command = scenario.Presets[0];
            ChantStatus = scenario.ChantEnabled ? "Hold C and read the scripture aloud." : "";
            AddLog("Demo started: " + scenario.Title);
        }

        public void OpenMenu()
        {
            Cancel(); InterruptChant("The chant was abandoned."); ReleaseCarried(Vector3.zero);
            World.StopPlayMode(); Teleport(new Vector3(0, 0, -6), 0);
            World.DebugCones = false; if (Overview != null) Overview.enabled = false;
            InMenu = true; ShowTestControls = false; TextFocused = false;
            Transcript = "No command yet."; Outcome = "Choose a demo."; Timing = ""; LastResponse = null;
        }

        // ---------- Demo player physics ----------
        public void AddExternalVelocity(Vector3 velocity)
        {
            external += new Vector3(velocity.x, 0, velocity.z);
            if (velocity.y > 0) gravity = Mathf.Max(gravity, velocity.y);
        }

        public void SetCarried(SandboxMonster carrier)
        {
            // While carried the player's capsule is switched off so it cannot
            // snag the carrier's own controller.
            carriedBy = carrier; external = Vector3.zero; gravity = 0; motor.enabled = false;
            if (voiceInput.Active) EndAnyVoice();
        }

        public void ReleaseCarried(Vector3 velocity)
        {
            if (carriedBy == null) return;
            carriedBy = null; airSpeed = 0;
            // Never switch the capsule back on inside a door, a wall or the floor.
            if (World.Scenario != null) Player.position = World.FreeStandingSpot(Player.position, World.CarryOrigin, motor.radius, motor.height);
            motor.enabled = true;
            external = new Vector3(velocity.x, 0, velocity.z); gravity = velocity.y;
        }

        void FollowCarrier()
        {
            if (carriedBy == null) return;
            if (!carriedBy.Entity.Alive || World.Carrier != carriedBy) { ReleaseCarried(Vector3.zero); return; }
            Vector3 hold = carriedBy.Flying ? carriedBy.transform.position + Vector3.down * 1.95f : carriedBy.Hand.position + Vector3.down * .6f;
            if (!carriedBy.Flying)
            {
                // An Ogre's fist hangs low: keep the dangling player's feet on the floor.
                float floor = float.NegativeInfinity;
                foreach (var hit in Physics.RaycastAll(hold + Vector3.up, Vector3.down, 1.8f, ~0, QueryTriggerInteraction.Ignore))
                    if (!hit.transform.IsChildOf(carriedBy.transform) && hit.collider.GetComponentInParent<SandboxMonster>() == null && hit.normal.y > .6f) floor = Mathf.Max(floor, hit.point.y);
                if (hold.y < floor) hold.y = floor + .02f;
            }
            motor.enabled = false; Player.position = hold;
        }

        // ---------- Rite of Return chanting (one mouth: chant or brainwash) ----------
        public bool BeginChant()
        {
            if (World.Scenario == null || !World.Scenario.ChantEnabled || chantActive || chantPending || World.GameOver || World.Victory) return false;
            if (voiceInput.Active || IsVoiceRecording || World.Casting) { ChantStatus = "Your mouth is busy with a brainwash. Finish it first."; return false; }
            string error = null;
            bool began = false;
            try { began = Recorder != null && Recorder.Begin(out error); } catch (Exception exception) { error = exception.Message; }
            if (!began) { ChantStatus = "Chant failed: " + (error ?? "no microphone") + ". Use the typed chant button."; return false; }
            chantActive = true; voiceChant = true; ChantStatus = "CHANTING... release C when the line is done.";
            return true;
        }

        public void EndChant()
        {
            if (!chantActive || !voiceChant) return;
            chantActive = false; voiceChant = false;
            var scenario = World.Scenario;
            byte[] audio = null;
            try { audio = Recorder != null && Recorder.Recording ? Recorder.Finish() : null; } catch (Exception exception) { ChantStatus = "Chant failed: " + exception.Message; }
            finally { Recorder?.Cancel(); }
            if (audio == null || audio.Length <= 44) { ChantStatus = "No chant was heard."; return; }
            chantPending = true; ChantStatus = "Listening to your chant...";
            Client.Transcribe(audio, (transcript, error, sttMs) =>
            {
                chantPending = false;
                // A result that returns after a restart belongs to the old run.
                if (World.Scenario != scenario || scenario == null) return;
                if (error != null) { ChantStatus = "Chant not understood: " + error; return; }
                ChantStatus = "You chanted: \"" + transcript + "\"";
                scenario.OnChant(transcript ?? "", false);
            });
        }

        public void InterruptChant(string reason)
        {
            if (!chantActive) return;
            if (voiceChant) Recorder?.Cancel();
            chantActive = false; voiceChant = false; typedChant = null; ChantStatus = reason; AddLog(reason);
        }

        public void TypedChant(string text)
        {
            if (World.Scenario == null || !World.Scenario.ChantEnabled || string.IsNullOrWhiteSpace(text)) return;
            if (World.Casting || voiceInput.Active || IsVoiceRecording) { ChantStatus = "Your mouth is busy with a brainwash. Finish it first."; return; }
            if (chantActive || chantPending) return;
            typedChant = text.Trim();
            int words = typedChant.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            chantActive = true; voiceChant = false; typedChantEnd = Time.time + Mathf.Clamp(words * .38f, 1.5f, 4.5f);
            ChantStatus = "CHANTING (typed)... keep still and stay out of reach.";
        }

        void AddStaffPart(PrimitiveType kind, Vector3 position, Vector3 scale, Color color)
        {
            var part = GameObject.CreatePrimitive(kind); part.transform.SetParent(staff, false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            part.GetComponent<Collider>().enabled = false; Destroy(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
        }

        public void Attack()
        {
            if (World.GameOver || World.Victory || InMenu) return;
            GameAudio.Play2D("staff_swing", .8f);
            World.PlayerAttack(); staffSwingEnd = Time.time + .34f;
        }

        public void KickAnimation() { kickEnd = Time.time + .3f; Shake(.18f); Punch(-2.5f); }

        // Hides the first-person staff (automated captures of the scene).
        public void ShowEquipment(bool visible) { if (staff != null) staff.gameObject.SetActive(visible); }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }

        // The staff breathes, bobs with each step, and swings in a fast diagonal arc
        // that peaks when the hit lands (the damage is applied on the press).
        void AnimateStaff()
        {
            float t = Time.time;
            float u = 1 - Mathf.Clamp01((staffSwingEnd - t) / .34f);
            float wind = u < .14f ? Smooth(u / .14f) : u < .3f ? 1 - Smooth((u - .14f) / .16f) : 0;
            float strike = u < .14f ? 0 : u < .34f ? Smooth((u - .14f) / .2f) : 1 - Smooth((u - .34f) / .66f);
            float breathe = Mathf.Sin(t * 1.7f);
            float step = Mathf.Sin(bobPhase) * bobWeight, lift = Mathf.Abs(Mathf.Cos(bobPhase)) * bobWeight;
            staff.localPosition = new Vector3(.43f - strike * .32f + step * .02f, -.48f + breathe * .006f - lift * .025f + wind * .06f, .8f + strike * .12f);
            staff.localRotation = Quaternion.Euler(-12 + breathe * 1.2f + wind * 28 - strike * 78, wind * -18 + strike * 30, -12 + step * 3 + strike * 38);
            if (staffTrail != null) staffTrail.emitting = u > .12f && u < .5f;
        }

        void LateUpdate()
        {
            if (View == null || Player == null) return;
            float dt = Mathf.Max(Time.deltaTime, .0001f);
            Vector3 position = Player.position;
            Vector3 velocity = (position - lastFeelPosition) / dt;
            lastFeelPosition = position;
            if (velocity.sqrMagnitude > 400 || InMenu) velocity = Vector3.zero;   // teleports and the menu
            float speed = Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude;
            bool grounded = motor.enabled && motor.isGrounded && !Carried;
            bobWeight = Mathf.MoveTowards(bobWeight, grounded ? Mathf.Clamp01(speed / 4.5f) : 0, dt * 5);
            float before = bobPhase;
            bobPhase += speed * dt * 1.75f;
            if (grounded && speed > .8f && Mathf.Floor(before / Mathf.PI) != Mathf.Floor(bobPhase / Mathf.PI)) PlayerFootstep?.Invoke(position, speed);
            if (!grounded) fallSpeed = Mathf.Max(fallSpeed, -velocity.y);
            if (grounded && !wasGrounded)
            {
                float impact = Mathf.Clamp(fallSpeed, 0, 14);
                if (impact > 2.5f) { landVelocity -= .06f + impact * .025f; PlayerLanded?.Invoke(impact); if (impact > 7) Shake(impact / 30f); }
                fallSpeed = 0;
            }
            wasGrounded = grounded;
            landVelocity += (-landDip * 170 - landVelocity * 15) * dt;
            landDip += landVelocity * dt;
            float bobY = (Mathf.Abs(Mathf.Sin(bobPhase)) - .5f) * .045f * bobWeight;
            float bobX = Mathf.Sin(bobPhase) * .025f * bobWeight;
            View.transform.localPosition = new Vector3(bobX, 1.6f + bobY + Mathf.Clamp(landDip, -.25f, .08f), 0);
            float strafe = Vector3.Dot(velocity, Player.right);
            viewRoll = Mathf.Lerp(viewRoll, Mathf.Clamp(-strafe * .45f, -2.2f, 2.2f) + bobX * 14, 1 - Mathf.Exp(-dt * 8));
            shakeAmount = Mathf.MoveTowards(shakeAmount, 0, dt * 2.2f);
            shakeClock += dt * 26;
            float s = shakeAmount * shakeAmount;
            float sx = (Mathf.PerlinNoise(shakeClock, .3f) - .5f) * 2 * s * 3.5f;
            float sy = (Mathf.PerlinNoise(.7f, shakeClock) - .5f) * 2 * s * 3.5f;
            float sz = (Mathf.PerlinNoise(shakeClock, 2.1f) - .5f) * 2 * s * 2.5f;
            View.transform.localRotation = Quaternion.Euler(pitch + sx, sy, viewRoll + sz);
            fovPunch = Mathf.MoveTowards(fovPunch, 0, dt * 25);
            bool sprinting = grounded && speed > 3.9f;
            View.fieldOfView = Mathf.Lerp(View.fieldOfView, 75 + (sprinting ? 5 : 0) + fovPunch, 1 - Mathf.Exp(-dt * 9));
        }

        // ---------- Brainwashing ----------
        // One key hold is one cast with one recording (concept §6.1). Every monster
        // that meets your eyes listens; one that does while you hold the key joins
        // then. Each listener is sent with the words said between its joining and
        // its sending: your release, or the moment it loses eye contact. Monsters
        // still thinking or obeying an earlier order sit this cast out.
        public bool BeginHoldVoice(VoiceInputSource source)
        {
            if (source == VoiceInputSource.None || voiceInput.Active || IsVoiceRecording || World.GameOver || World.Victory) return false;
            if (chantActive || chantPending) { AddLog("You are chanting. One mouth: finish the chant before brainwashing."); return false; }
            if (Carried) { AddLog("You cannot hold eye contact while being carried."); return false; }
            if (!Client.Ready) { AddLog("Voice input unavailable: " + Client.Health); return false; }
            if (!World.CanBeginCast(out string blocked)) { if (blocked.Contains("MP")) GameAudio.Play2D("mp_low", 1, 1, .8f); Outcome = blocked; AddLog("Brainwash blocked: " + blocked); return false; }
            string error = null;
            bool began = false;
            try { began = Recorder != null && Recorder.Begin(out error); }
            catch (Exception exception) { error = exception.Message; }
            if (!began)
            {
                Outcome = "Voice capture failed: " + (Recorder == null ? "No recorder is available." : error);
                Transcript = "No voice command submitted."; AddLog(Outcome); return false;
            }
            if (!World.BeginCast(out string reason)) { Recorder.Cancel(); Outcome = reason; AddLog("Brainwash blocked: " + reason); return false; }
            voiceInput.TryBegin(source);
            Transcript = "Recording English..."; LastResponse = null; Timing = "";
            Outcome = source == VoiceInputSource.Toggle ? "Recording. Press V again to send." : "Recording. Release to send.";
            AddLog("Voice capture started: " + source + " · " + (ReferenceEquals(Recorder, Voice) ? Voice.DeviceLabel : "injected recorder")); return true;
        }

        public void EndHoldVoice(VoiceInputSource source)
        {
            if (voiceInput.TryFinish(source)) FinishVoiceCast();
        }
        void EndAnyVoice()
        {
            if (voiceInput.TryFinishAny()) FinishVoiceCast();
        }
        public void ToggleVoice()
        {
            if (voiceInput.Active) EndAnyVoice();
            else BeginHoldVoice(VoiceInputSource.Toggle);
        }

        void FinishVoiceCast()
        {
            var listeners = World.EndCast();
            try
            {
                if (Recorder == null || !Recorder.Recording) throw new InvalidOperationException("The microphone stopped before release. No text was substituted.");
                if (listeners.Count == 0) { Transcript = "No voice command submitted."; Outcome = "Nobody met your eyes while you spoke."; AddLog(Outcome); return; }
                SendVoice(listeners);
            }
            catch (Exception e)
            {
                Outcome = "Voice capture failed: " + e.Message; Transcript = "No voice command submitted.";
                foreach (var lease in listeners) World.ReleaseListener(lease, "the recording failed");
                AddLog(Outcome);
            }
            finally { Recorder?.Cancel(); }
        }

        // Listeners that lost eye contact while you keep speaking to the others.
        void SendEarly(List<BrainwashLease> listeners)
        {
            if (!voiceInput.Active || Recorder == null || !Recorder.Recording)
            {
                // A typed cast never stays open, so this is a recording that already stopped.
                foreach (var lease in listeners) World.ReleaseListener(lease, "no recording");
                return;
            }
            SendVoice(listeners);
        }

        // Listeners that joined together share one window of the recording and
        // one speech recognition; each then gets its own interpretation.
        void SendVoice(List<BrainwashLease> listeners)
        {
            foreach (var window in listeners.GroupBy(lease => Mathf.Round(lease.JoinOffset * 20) / 20))
            {
                var group = window.ToList();
                byte[] audio = null; string problem = null;
                try { audio = Recorder.Slice(group[0].JoinOffset, group[0].SealOffset); }
                catch (Exception e) { problem = e.Message; }
                if (audio == null)
                {
                    foreach (var lease in group) World.ReleaseListener(lease, problem != null ? "no speech (" + problem + ")" : "no speech in its window");
                    Transcript = "No voice command submitted."; Outcome = "Nothing was said to " + SandboxWorld.Names(group) + "."; AddLog(Outcome);
                    continue;
                }
                AddLog("Voice sent: " + audio.Length + " WAV bytes for " + SandboxWorld.Names(group) + " (" + group[0].JoinOffset.ToString("0.0") + "–" + group[0].SealOffset.ToString("0.0") + " s).");
                if (World.Scenario == null) Transcript = "Transcribing recorded English...";
                Outcome = "Thinking: " + SandboxWorld.Names(group);
                TranscribeWindow(group, audio);
            }
        }

        void TranscribeWindow(List<BrainwashLease> group, byte[] audio)
        {
            var ids = group.Select(lease => lease.Id).ToArray();
            float budget = group.Min(lease => lease.ThinkingRemaining);
            Client.Transcribe(audio, (text, error, sttMs) =>
            {
                var live = ids.Select(World.FindLease).Where(lease => lease != null && lease.Status == "Thinking").ToList();
                if (live.Count == 0) { AddLog("Discarded a late transcript."); return; }
                if (error != null || string.IsNullOrWhiteSpace(text))
                {
                    // Speech that was heard but not recognized is not proof of silence.
                    string reason = error ?? "no words recognized";
                    Outcome = "Technical failure: " + reason; AddLog(Outcome);
                    foreach (var lease in live) World.TechnicalFailure(lease, reason);
                    return;
                }
                Transcript = text.Trim();
                World.Scenario?.OnCommandText(Transcript);
                AddLog("Heard \"" + Transcript + "\" (" + SandboxWorld.Names(live) + ")");
                foreach (var lease in live) SubmitCommand(lease, Transcript, sttMs);
            }, budget, () => ids.Any(id => World.FindLease(id)?.Status == "Thinking"));
        }

        void SubmitCommand(BrainwashLease lease, string text, double sttMs = -1)
        {
            string id = lease.Id; string name = lease.Monster.Entity.DisplayName;
            Client.Submit(id, lease.Scene, text, null, (response, error) =>
            {
                var current = World.FindLease(id);
                if (current == null || current.Status != "Thinking") { AddLog("Discarded late, expired, or cancelled response for " + name + "."); return; }
                if (error != null || response == null)
                { Outcome = "Technical failure: " + (error ?? "Empty response"); AddLog(Outcome); World.TechnicalFailure(current, error ?? "Empty response"); return; }
                LastResponse = response;
                var timings = response["timings"];
                Timing = (sttMs >= 0 ? "STT " + Math.Round(sttMs) + " ms | " : "") + "Laya " + Milliseconds(timings?["interpretMs"])
                    + " (" + (timings?["cachedQuestions"]?.Value<int>() ?? 0) + "/" + (timings?["questionCount"]?.Value<int>() ?? 0) + " shared)"
                    + " | wait " + Milliseconds(timings?["queueMs"]) + " | total " + Milliseconds(timings?["totalMs"]);
                var compiled = response["compiled"] as JObject;
                Outcome = name + ":\n" + (response["outcome"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "No outcome");
                AddLog(name + " · " + compiled?.Value<string>("disposition") + " | " + Timing);
                if (compiled == null || !World.ApplyOutcome(id, compiled)) AddLog("Response for " + name + " was not committed; inspect host outcome.");
            }, lease.ThinkingRemaining, () => World.FindLease(id)?.Status == "Thinking");
        }

        public void SendPreset(string text)
        {
            if (voiceInput.Active || IsVoiceRecording) { AddLog("Release or cancel voice capture before selecting a preset."); return; }
            Command = text;
            SendTypedCommand();
        }

        // A typed command is an instant cast: everyone meeting your eyes right now
        // hears the text.
        public void SendTypedCommand()
        {
            if (voiceInput.Active || IsVoiceRecording) { AddLog("Release or cancel voice capture before sending text."); return; }
            if (string.IsNullOrWhiteSpace(Command)) { AddLog("Enter an English command first."); return; }
            if (!Client.Ready) { Outcome = Client.Health; AddLog("Typed command unavailable: " + Client.Health); return; }
            if (!World.BeginCast(out string reason)) { Outcome = reason; AddLog("Brainwash blocked: " + reason); return; }
            var listeners = World.EndCast();
            if (listeners.Count == 0)
            {
                World.CanAcquire(out string why);
                Outcome = "Nobody listened: " + why; AddLog(Outcome); return;
            }
            Transcript = Command.Trim(); Timing = ""; LastResponse = null;
            Outcome = "Thinking: " + SandboxWorld.Names(listeners);
            AddLog("Text sent to " + SandboxWorld.Names(listeners) + ": " + Transcript);
            if (World.Scenario != null) World.VoiceNoise(3);
            World.Scenario?.OnCommandText(Transcript);
            foreach (var lease in listeners) SubmitCommand(lease, Transcript);
        }

        static string Milliseconds(JToken token) => token == null ? "—" : Math.Round(token.Value<double>()) + " ms";

        // Esc, reset and the menu: stop recording and end every cast and order.
        public void Cancel()
        {
            voiceInput.Cancel();
            Recorder?.Cancel(); Client?.CancelPending(); World?.Cancel();
        }
        public void CancelVoiceForFocusLoss()
        {
            if (!voiceInput.Active && !IsVoiceRecording) return;
            if (chantActive && voiceChant) { InterruptChant("The chant was interrupted: the window lost focus."); return; }
            voiceInput.Cancel(); Recorder?.Cancel();
            World?.CancelCast("The window lost focus; the listeners go back to normal.");
            Transcript = "Voice capture cancelled."; Outcome = "Window focus changed. Hold B again to record."; AddLog(Outcome);
        }
        void OnApplicationFocus(bool focused) { if (!focused) CancelVoiceForFocusLoss(); }
        void OnApplicationPause(bool paused) { if (paused) CancelVoiceForFocusLoss(); }
        void OnDisable() => CancelVoiceForFocusLoss();
        public void ResetRoom()
        {
            if (World.Scenario != null) { StartDemo(World.Scenario.Id); return; }
            Cancel(); World.ResetSandbox(); Teleport(new Vector3(0, 0, -6), 0);
            TextFocused = false;
            Transcript = "No command yet."; Outcome = "Room reset."; Timing = ""; LastResponse = null;
        }
        public void StartPlay()
        {
            InMenu = false; ReleaseCarried(Vector3.zero);
            Cancel(); World.StartPlayMode(); Teleport(new Vector3(0, 0, -6), 0);
            ShowTestControls = false; TextFocused = false;
            Transcript = "No command yet."; Outcome = "Play mode started."; Timing = ""; LastResponse = null;
        }
        public void BackToTests()
        {
            InMenu = false; ReleaseCarried(Vector3.zero);
            Cancel(); World.StopPlayMode(); Teleport(new Vector3(0, 0, -6), 0);
            ShowTestControls = true; TextFocused = false;
            Transcript = "No command yet."; Outcome = "Test mode ready."; Timing = ""; LastResponse = null;
        }
        public void Teleport(Vector3 position, float yaw)
        {
            motor.enabled = false; Player.SetPositionAndRotation(position, Quaternion.Euler(0, yaw, 0)); motor.enabled = true;
            pitch = 0; View.transform.localRotation = Quaternion.identity; gravity = 0; external = Vector3.zero; airSpeed = 0; Physics.SyncTransforms();
        }
        public void AddLog(string message)
        {
            string entry = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            Logs.Add(entry); if (Logs.Count > 150) Logs.RemoveAt(0);
            Debug.Log("[Brainwash] " + message);
        }
        public void ExportLog()
        {
            try
            {
                string file = Path.Combine(Application.persistentDataPath, "brainwash-debug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                var data = new JObject { ["capturedAt"] = DateTime.UtcNow.ToString("o"), ["unity"] = Application.unityVersion, ["events"] = new JArray(Logs), ["lastResponse"] = LastResponse?.DeepClone() };
                File.WriteAllText(file, data.ToString()); AddLog("Exported debug log: " + file);
            }
            catch (Exception e) { AddLog("Log export failed: " + e.Message); }
        }
    }
}
