using System;
using UnityEngine;

namespace Brainwash
{
    // Procedural animation for the generated creature models (art/models).
    // Every frame starts from the bind pose and layers: a gait that follows the
    // controller's real speed, state poses (listening, thinking, sleeping,
    // covering, dancing...), one-shot actions cued by gameplay (strikes, casts,
    // throws, slams, heals), a flinch and white flash when hit, a hypnotised
    // sway while obeying, a head that meets the player's eyes when the body
    // already faces them, deaths that buckle and fall, limp flailing while
    // knocked, and spring-driven ears, tails and wings. Rotations are applied
    // about the creature's own right/up/forward axes, so they do not depend on
    // how the FBX importer oriented each bone.
    public sealed class CreatureAnimator : MonoBehaviour
    {
        enum B
        {
            Hips, Spine, Chest, Neck, Head, Jaw,
            UpperArmL, ForearmL, HandL, UpperArmR, ForearmR, HandR,
            ThighL, ShinL, FootL, ThighR, ShinR, FootR,
            EarL, EarR, WingL, WingTipL, WingR, WingTipR,
            Tail1, Tail2, Tail3, Tail4, Lid, Tongue, Sac, Count
        }

        static readonly string[] BoneNames =
        {
            "Hips", "Spine", "Chest", "Neck", "Head", "Jaw",
            "UpperArm.L", "Forearm.L", "Hand.L", "UpperArm.R", "Forearm.R", "Hand.R",
            "Thigh.L", "Shin.L", "Foot.L", "Thigh.R", "Shin.R", "Foot.R",
            "Ear.L", "Ear.R", "Wing.L", "WingTip.L", "Wing.R", "WingTip.R",
            "Tail1", "Tail2", "Tail3", "Tail4", "Lid", "Tongue", "Sac"
        };

        public enum GaitKind { Biped, Heavy, Quadruped, Hopper, Flyer, Shuffle, Glide }

        // Audio and effects listen here: entity, foot position, weight 0-1.
        public static event Action<SandboxEntity, Vector3, float> Footstep;
        // A hard landing at the end of a hop, fall or death.
        public static event Action<SandboxEntity, Vector3, float> Thud;
        // Each downstroke of a flying creature's wings.
        public static event Action<SandboxEntity, Vector3> WingBeat;
        float lastFlap;

        const int N = (int)B.Count;
        readonly Transform[] bones = new Transform[N];
        readonly Quaternion[] restRotation = new Quaternion[N];
        readonly Vector3[] restPosition = new Vector3[N];
        readonly Vector3[] restScale = new Vector3[N];
        readonly Vector3[] offset = new Vector3[N];          // pitch, yaw, roll (degrees, creature axes)
        readonly Vector3[] scaleOffset = new Vector3[N];
        int[] order = Array.Empty<int>();

        SandboxEntity entity;
        SandboxMonster monster;
        Transform model, handSocket, mouthSocket, naturalWeapon;
        Renderer[] renderers = Array.Empty<Renderer>();
        public Renderer Body { get; private set; }
        public Transform HeadBone => bones[(int)B.Head];
        public string Species { get; private set; }
        public GaitKind Gait { get; private set; }
        float scale = 1, height = 1, legLength = .5f, seed;

        // motion tracking
        Vector3 lastPosition, velocity;
        float verticalSpeed, lastYaw, yawRate, phase, hopPhase;
        int lastStep, lastHop;
        float move, run;

        // events and timelines
        string lastPose = "idle";
        float poseSince;
        string action;
        float actionAt = -100, actionLength = .45f, lastCue = -100;
        float hitAt = -100, lastDamage = -100, hitStrength, lastHealth = -1;
        Vector3 hitDirection;
        float deathAt = -1;
        int deathSide = 1;
        bool landedAfterDeath;

        // springs
        float earSpring, earVelocity, tailSpring, tailVelocity, lookYaw, lookPitch, lid, sac = 1;
        Vector3 rootOffset;
        Quaternion rootRotation;
        Vector3 rootScale;
        bool flashOn;
        MaterialPropertyBlock block;

        // ------------------------------------------------------------------
        public static CreatureAnimator Build(SandboxEntity entity, string species, float height, Color color)
        {
            var go = ModelLibrary.Spawn(species, entity.transform, color);
            if (go == null) return null;
            var animator = entity.gameObject.AddComponent<CreatureAnimator>();
            animator.Bind(entity, species, go.transform, height);
            entity.VisualRoot = go.transform;
            entity.Body = animator.Body;
            entity.ModelTint = color;
            return animator;
        }

        void Bind(SandboxEntity owner, string species, Transform root, float targetHeight)
        {
            entity = owner; Species = species; model = root; height = targetHeight;
            float built = ModelLibrary.Height(species, targetHeight);
            scale = built > .01f ? targetHeight / built : 1;
            model.localScale = Vector3.one * scale;
            model.localRotation = Facing;
            Gait = Enum.TryParse(ModelLibrary.Gait(species) ?? "Biped", true, out GaitKind kind) ? kind : GaitKind.Biped;
            seed = UnityEngine.Random.value * 100;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                int i = Array.IndexOf(BoneNames, t.name);
                if (i >= 0 && bones[i] == null) { bones[i] = t; restRotation[i] = t.localRotation; restPosition[i] = t.localPosition; restScale[i] = t.localScale; }
                else if (t.name == "Socket.Hand") handSocket = t;
                else if (t.name == "Socket.Mouth") mouthSocket = t;
                else if (t.name == "Club" || t.name.StartsWith("Weapon", StringComparison.Ordinal)) naturalWeapon = t;
            }
            renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers) if (r.name == "Body") Body = r;
            if (Body == null && renderers.Length > 0) Body = renderers[0];
            // Parents before children, so a child's offset adds to its parent's.
            var list = new System.Collections.Generic.List<int>();
            for (int i = 0; i < N; i++) if (bones[i] != null) list.Add(i);
            list.Sort((a, b) => Depth(bones[a]).CompareTo(Depth(bones[b])));
            order = list.ToArray();
            if (bones[(int)B.Hips] != null) legLength = Mathf.Max(.15f, model.InverseTransformPoint(bones[(int)B.Hips].position).y);
            else legLength = .3f;
            lastPosition = transform.position; lastYaw = transform.eulerAngles.y;
            block = new MaterialPropertyBlock();
        }

        int Depth(Transform t) { int d = 0; while (t != null && t != model) { d++; t = t.parent; } return d; }

        // ------------------------------------------------------------------
        void LateUpdate()
        {
            if (entity == null || model == null) return;
            if (monster == null) monster = GetComponent<SandboxMonster>();
            float dt = Mathf.Clamp(Time.deltaTime, .0001f, .1f), t = Time.time + seed;
            Track(dt);
            string pose = monster != null ? monster.Pose : "idle";
            if (pose != lastPose) { PoseChanged(pose); lastPose = pose; poseSince = Time.time; }
            if (monster != null && monster.LastCueTime > lastCue) { lastCue = monster.LastCueTime; StartAction(monster.LastCue); }
            if (entity.LastDamageTime > lastDamage) { lastDamage = entity.LastDamageTime; Hurt(); }
            lastHealth = entity.Health;
            bool alive = entity.Alive;
            if (!alive && deathAt < 0) { deathAt = Time.time; deathSide = UnityEngine.Random.value < .5f ? 1 : -1; landedAfterDeath = false; }
            if (alive) deathAt = -1;
            bool frozen = alive && pose == "statue";
            if (!frozen)
            {
                Clear();
                if (!alive) Death(t);
                else if (pose == "knocked") Knocked(t);
                else
                {
                    Locomotion(dt, t);
                    Poses(pose, t);
                    Actions(t);
                    Flinch();
                    Trance(t);
                    Look(dt);
                }
                Secondary(dt, t, alive);
                Apply();
            }
            Sockets();
            Flash();
        }

        void Track(float dt)
        {
            Vector3 p = transform.position;
            Vector3 v = (p - lastPosition) / dt;
            lastPosition = p;
            if (v.sqrMagnitude > 900) v = Vector3.zero;   // teleported
            Vector3 flat = Vector3.ProjectOnPlane(v, Vector3.up);
            float k = 1 - Mathf.Exp(-dt * 12);
            velocity = Vector3.Lerp(velocity, flat, k);
            verticalSpeed = Mathf.Lerp(verticalSpeed, v.y, k);
            float yaw = transform.eulerAngles.y;
            yawRate = Mathf.Lerp(yawRate, Mathf.DeltaAngle(lastYaw, yaw) / dt, 1 - Mathf.Exp(-dt * 8));
            lastYaw = yaw;
            float speed = velocity.magnitude / Mathf.Max(.01f, scale);
            float targetMove = Mathf.Clamp01(speed / (legLength * 1.1f));
            move = Mathf.MoveTowards(move, targetMove, dt * 5);
            run = Mathf.Clamp01((speed / legLength - 2.4f) / 2f);
            float stride = legLength * (Gait == GaitKind.Heavy ? 1.7f : Gait == GaitKind.Quadruped ? 1.1f : 1.45f);
            phase += speed / (2 * stride) * Mathf.PI * 2 * dt;
            hopPhase += speed / (legLength * 3.2f) * dt;
            int step = Mathf.FloorToInt(phase / Mathf.PI);
            if (step != lastStep)
            {
                lastStep = step;
                if (move > .3f && entity.Alive && (monster == null || !monster.Flying) && Gait != GaitKind.Hopper && Gait != GaitKind.Shuffle)
                {
                    var foot = bones[(int)((step & 1) == 0 ? B.FootL : B.FootR)];
                    Footstep?.Invoke(entity, foot != null ? foot.position : transform.position, Weight());
                }
            }
        }

        float Weight() => entity.Profile != null ? Mathf.Clamp01(entity.Profile.Mass / 400f) : Mathf.Clamp01(height / 3f);

        void Clear()
        {
            for (int i = 0; i < N; i++) { offset[i] = Vector3.zero; scaleOffset[i] = Vector3.one; }
            rootOffset = Vector3.zero; rootRotation = Quaternion.identity; rootScale = Vector3.one;
            foreach (int i in order) { bones[i].localRotation = restRotation[i]; bones[i].localPosition = restPosition[i]; bones[i].localScale = restScale[i]; }
        }

        void Add(B b, float pitch, float yaw = 0, float roll = 0) { offset[(int)b] += new Vector3(pitch, yaw, roll); }
        // Mirrored pairs: positive "out" spreads a limb away from the body.
        void Pair(B left, B right, float pitchL, float pitchR, float outward = 0, float yawOut = 0)
        {
            Add(left, pitchL, -yawOut, -outward);
            Add(right, pitchR, yawOut, outward);
        }
        bool Has(B b) => bones[(int)b] != null;

        // The generated models face -Z in their own space after the FBX import, so the
        // model turns half a circle to face the monster's forward. Motion is applied
        // about the monster's own axes (right, up, forward), tilted by the root motion.
        static readonly Quaternion Facing = Quaternion.Euler(0, 180, 0);

        void Apply()
        {
            model.localPosition = rootOffset;
            model.localRotation = rootRotation * Facing;
            model.localScale = Vector3.Scale(Vector3.one * scale, rootScale);
            Quaternion basis = transform.rotation * rootRotation;
            Vector3 right = basis * Vector3.right, up = basis * Vector3.up, forward = basis * Vector3.forward;
            foreach (int i in order)
            {
                var e = offset[i];
                if (e != Vector3.zero)
                    bones[i].rotation = Quaternion.AngleAxis(e.y, up) * Quaternion.AngleAxis(e.x, right) * Quaternion.AngleAxis(e.z, forward) * bones[i].rotation;
                if (scaleOffset[i] != Vector3.one) bones[i].localScale = Vector3.Scale(restScale[i], scaleOffset[i]);
            }
        }

        // ------------------------------------------------------------------
        void Locomotion(float dt, float t)
        {
            float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
            float breathe = Mathf.Sin(t * 1.9f);
            Add(B.Chest, breathe * 1.6f); Add(B.Spine, breathe * .8f);
            Add(B.Head, Mathf.Sin(t * .7f) * 3, Mathf.Sin(t * .43f) * 6);
            Pair(B.UpperArmL, B.UpperArmR, breathe * 2, breathe * 2, 4 + breathe);
            Add(B.Hips, 0, 0, Mathf.Sin(t * .5f) * 1.5f * (1 - move));
            bool flying = monster != null && monster.Flying;
            switch (Gait)
            {
                case GaitKind.Flyer:
                    Fly(t, flying);
                    return;
                case GaitKind.Quadruped: Quadruped(s, c); break;
                case GaitKind.Hopper: Hop(); break;
                case GaitKind.Shuffle: Shuffle(t); break;
                default: Biped(s, c, Gait == GaitKind.Heavy, Gait == GaitKind.Glide); break;
            }
            // Turning leans the body into the turn; speed leans it forward.
            float turnLean = Mathf.Clamp(-yawRate * .045f, -10, 10) * move;
            rootRotation = Quaternion.Euler(0, 0, turnLean) * rootRotation;
            if (Has(B.WingL)) { Pair(B.WingL, B.WingR, 0, 0, 6 * Mathf.Sin(t * 2.2f) + move * 8, -10 * move); }
        }

        void Biped(float s, float c, bool heavy, bool glide)
        {
            float a = move * (heavy ? 22 : glide ? 14 : 27 + 13 * run);
            Add(B.ThighL, -a * s); Add(B.ThighR, a * s);
            Add(B.ShinL, a * 1.25f * Mathf.Max(0, c) + move * 6);
            Add(B.ShinR, a * 1.25f * Mathf.Max(0, -c) + move * 6);
            Add(B.FootL, a * .45f * s - a * .4f * Mathf.Max(0, c)); Add(B.FootR, -a * .45f * s - a * .4f * Mathf.Max(0, -c));
            float arm = a * (heavy ? .55f : .75f);
            Pair(B.UpperArmL, B.UpperArmR, arm * s, -arm * s, heavy ? 6 : 2);
            Pair(B.ForearmL, B.ForearmR, -12 - arm * .45f * (1 + s), -12 - arm * .45f * (1 - s));
            Add(B.Hips, 0, s * 5 * move, s * (heavy ? 7 : 4) * move);
            Add(B.Spine, (5 + 9 * run) * move, -s * 6 * move, -s * (heavy ? 4 : 2) * move);
            Add(B.Chest, 0, s * 7 * move);
            Add(B.Head, -(5 + 9 * run) * move * .6f, -s * 4 * move);
            float bob = legLength * (heavy ? .07f : glide ? .015f : .045f) * move;
            rootOffset += Vector3.up * (-Mathf.Cos(2 * phase) * bob - bob * .5f) * scale;
            if (heavy) rootRotation = Quaternion.Euler(0, 0, s * 3 * move) * rootRotation;
        }

        void Quadruped(float s, float c)
        {
            float a = move * (30 + 12 * run);
            // Trot: diagonal pairs move together.
            Add(B.UpperArmL, -a * s); Add(B.ThighR, -a * s);
            Add(B.UpperArmR, a * s); Add(B.ThighL, a * s);
            Add(B.ForearmL, a * .6f * Mathf.Max(0, c)); Add(B.ForearmR, a * .6f * Mathf.Max(0, -c));
            Add(B.ShinR, a * .7f * Mathf.Max(0, c)); Add(B.ShinL, a * .7f * Mathf.Max(0, -c));
            Add(B.Spine, Mathf.Sin(2 * phase) * 4 * move);
            rootOffset += Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * legLength * .12f * move * scale;
            Add(B.Head, -Mathf.Sin(2 * phase) * 4 * move);
        }

        void Hop()
        {
            if (move < .05f) { hopPhase = 0; return; }
            float u = hopPhase - Mathf.Floor(hopPhase);
            float crouch = u < .2f ? u / .2f : u < .35f ? 1 - (u - .2f) / .15f : u > .85f ? (u - .85f) / .15f : 0;
            float air = u > .3f && u < .88f ? Mathf.Sin((u - .3f) / .58f * Mathf.PI) : 0;
            float extend = u > .25f && u < .7f ? Mathf.Sin((u - .25f) / .45f * Mathf.PI) : 0;
            Add(B.ThighL, 25 * crouch - 45 * extend); Add(B.ThighR, 25 * crouch - 45 * extend);
            Add(B.ShinL, -35 * crouch + 50 * extend); Add(B.ShinR, -35 * crouch + 50 * extend);
            Pair(B.UpperArmL, B.UpperArmR, -30 * air, -30 * air);
            rootOffset += Vector3.up * air * legLength * .9f * move * scale;
            rootRotation = Quaternion.Euler(-12 * air + 8 * crouch, 0, 0) * rootRotation;
            float squash = .14f * crouch;
            rootScale = new Vector3(1 + squash, 1 - squash, 1 + squash);
            int hop = Mathf.FloorToInt(hopPhase);
            if (hop != lastHop) { lastHop = hop; Thud?.Invoke(entity, transform.position, Weight() * .6f); }
        }

        void Shuffle(float t)
        {
            float u = Mathf.Abs(Mathf.Sin(phase * 1.4f));
            int hop = Mathf.FloorToInt(phase * 1.4f / Mathf.PI);
            if (hop != lastHop) { lastHop = hop; if (move > .3f) Thud?.Invoke(entity, transform.position, .35f); }
            rootOffset += Vector3.up * u * height * .07f * move;
            rootRotation = Quaternion.Euler(Mathf.Sin(phase * 1.4f) * 6 * move, 0, Mathf.Sin(phase * .7f) * 4 * move) * rootRotation;
        }

        void Fly(float t, bool flying)
        {
            float rate = flying ? (move > .3f ? 10.5f : 7.5f) : 3;
            float f = Mathf.Sin(t * rate), lag = Mathf.Sin(t * rate - 1.1f);
            if (flying && lastFlap > 0 && f <= 0) WingBeat?.Invoke(entity, transform.position + Vector3.up * height * .6f);
            lastFlap = f;
            float sweep = flying ? 42 : 8;
            Pair(B.WingL, B.WingR, -6 * f, -6 * f, sweep * f + (flying ? 8 : -30));
            Pair(B.WingTipL, B.WingTipR, 0, 0, sweep * .7f * lag);
            if (flying)
            {
                rootOffset += Vector3.up * -f * .07f * scale;
                rootRotation = Quaternion.Euler(14 * move, 0, Mathf.Clamp(-yawRate * .05f, -18, 18)) * rootRotation;
                Pair(B.ThighL, B.ThighR, 35 + lag * 6, 35 + lag * 6);
                Pair(B.ShinL, B.ShinR, 30, 30);
                Add(B.Tail1, -8 * f);
            }
            else Biped(Mathf.Sin(phase), Mathf.Cos(phase), false, false);
            Add(B.Head, 6 * f);
        }

        // ------------------------------------------------------------------
        void PoseChanged(string pose)
        {
            if (pose == "fireball") StartAction("cast");
            else if (pose == "heal") StartAction("heal");
        }

        void Poses(string pose, float t)
        {
            float since = Time.time - poseSince;
            float w = Mathf.Clamp01(since * 6);
            switch (pose)
            {
                case "listening":
                    Add(B.Head, -6 * w, 0, 14 * w);
                    Add(B.Spine, 6 * w);
                    if (Gait == GaitKind.Biped || Gait == GaitKind.Heavy || Gait == GaitKind.Glide)
                    { Add(B.UpperArmL, -35 * w, 0, -75 * w); Add(B.ForearmL, -115 * w); }
                    if (Has(B.EarL)) Pair(B.EarL, B.EarR, -20 * w, -20 * w);
                    break;
                case "thinking":
                    Add(B.Head, -8 + Mathf.Sin(t * 1.3f) * 6, Mathf.Sin(t * .8f) * 12, Mathf.Sin(t * 1.7f) * 9);
                    if (Gait == GaitKind.Biped || Gait == GaitKind.Heavy || Gait == GaitKind.Glide)
                    { Add(B.UpperArmR, -55 * w, 0, 12 * w); Add(B.ForearmR, -125 * w); Add(B.HandR, 0, 0, -20 * w); }
                    rootRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 1.1f) * 3) * rootRotation;
                    break;
                case "confused":
                    Add(B.Head, 0, Mathf.Sin(t * 9) * 22, Mathf.Sin(t * 5) * 8);
                    Pair(B.UpperArmL, B.UpperArmR, -25, -25, 35);
                    Pair(B.ForearmL, B.ForearmR, -55, -55);
                    rootRotation = Quaternion.Euler(0, Mathf.Sin(t * 3) * 10, Mathf.Sin(t * 4) * 5) * rootRotation;
                    break;
                case "sleep":
                    rootOffset += Vector3.down * height * .06f;
                    Add(B.Spine, 22); Add(B.Head, 32 + Mathf.Sin(t * 1.2f) * 3);
                    Pair(B.UpperArmL, B.UpperArmR, 12, 12, -4);
                    Pair(B.ThighL, B.ThighR, -20, -20, 8); Pair(B.ShinL, B.ShinR, 30, 30);
                    rootScale = new Vector3(1, 1 + Mathf.Sin(t * 1.4f) * .025f, 1);
                    break;
                case "cover":
                    Pair(B.UpperArmL, B.UpperArmR, -125 * w, -125 * w, -18 * w);
                    Pair(B.ForearmL, B.ForearmR, -65 * w, -65 * w, 0, 25 * w);
                    Add(B.Head, 22 * w); Add(B.Spine, 12 * w);
                    rootOffset += Vector3.down * height * .03f * w;
                    break;
                case "heal":
                    Pair(B.UpperArmL, B.UpperArmR, -95, -95, 18);
                    Pair(B.ForearmL, B.ForearmR, -25, -25);
                    Add(B.Head, -10);
                    break;
                case "wave":
                    Add(B.UpperArmR, 0, 0, 150 * w); Add(B.ForearmR, 0, 0, 20 + Mathf.Sin(t * 11) * 28);
                    Add(B.Head, 0, 0, -8);
                    break;
                case "dance":
                    float beat = t * 7;
                    rootOffset += Vector3.up * Mathf.Abs(Mathf.Sin(beat)) * height * .04f;
                    rootRotation = Quaternion.Euler(0, Mathf.Sin(t * 1.6f) * 40, Mathf.Sin(beat) * 9) * rootRotation;
                    Pair(B.UpperArmL, B.UpperArmR, -20 + Mathf.Sin(beat) * 25, -20 - Mathf.Sin(beat) * 25, 110 + Mathf.Sin(beat) * 30);
                    Pair(B.ForearmL, B.ForearmR, -45, -45);
                    Pair(B.ThighL, B.ThighR, -Mathf.Max(0, Mathf.Sin(beat)) * 35, -Mathf.Max(0, -Mathf.Sin(beat)) * 35);
                    Pair(B.ShinL, B.ShinR, Mathf.Max(0, Mathf.Sin(beat)) * 50, Mathf.Max(0, -Mathf.Sin(beat)) * 50);
                    Add(B.Head, 0, Mathf.Sin(beat * .5f) * 18, Mathf.Sin(beat) * 10);
                    Add(B.Hips, 0, 0, Mathf.Sin(beat) * 10);
                    if (Has(B.WingL)) Pair(B.WingL, B.WingR, 0, 0, 30 + Mathf.Sin(beat) * 30);
                    break;
                case "bow":
                    Add(B.Spine, 30 * w); Add(B.Chest, 15 * w); Add(B.Head, 18 * w);
                    Pair(B.UpperArmL, B.UpperArmR, 12 * w, 12 * w);
                    break;
                case "attacking":
                case "guarding":
                    if (Gait == GaitKind.Biped || Gait == GaitKind.Heavy || Gait == GaitKind.Glide)
                    {
                        float bounce = Mathf.Sin(t * 6) * 3;
                        Pair(B.ThighL, B.ThighR, -16 + bounce, -10 + bounce, 6);
                        Pair(B.ShinL, B.ShinR, 26 - bounce, 20 - bounce);
                        Pair(B.UpperArmL, B.UpperArmR, -40, -48, 16);
                        Pair(B.ForearmL, B.ForearmR, -70, -60);
                        Add(B.Spine, pose == "attacking" ? 14 : 6);
                        rootOffset += Vector3.down * legLength * .05f * scale;
                    }
                    else if (Gait == GaitKind.Quadruped) { Add(B.Spine, -8); Add(B.Head, -10); rootOffset += Vector3.down * legLength * .1f * scale; }
                    break;
                case "stopped":
                    Add(B.Head, -4);
                    break;
                case "fireball":
                    // Held after the cast (an imp's dragon breath keeps this pose).
                    if (action == null && since > .5f && Gait != GaitKind.Quadruped)
                    {
                        Pair(B.UpperArmL, B.UpperArmR, -80, -80, 14);
                        Add(B.Spine, 10); Add(B.Head, 6);
                        if (Has(B.WingL)) Pair(B.WingL, B.WingR, 0, 0, 40);
                    }
                    break;
            }
        }

        void StartAction(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            action = name; actionAt = Time.time;
            actionLength = name switch { "strike" => Gait == GaitKind.Heavy ? .7f : .42f, "cast" => .6f, "throw" => .55f, "slam" => .95f, "heal" => .8f, "swell" => 1.25f, "bite" => .45f, _ => .5f };
        }

        void Actions(float t)
        {
            if (action == null) return;
            float u = (Time.time - actionAt) / actionLength;
            if (u >= 1) { action = null; return; }
            switch (action)
            {
                case "strike":
                case "bite":
                    Strike(u);
                    break;
                case "cast":
                {
                    float up = Bell(u, 0, .35f, .55f), push = Bell(u, .3f, .5f, 1);
                    if (Has(B.UpperArmR) && Gait != GaitKind.Quadruped)
                    {
                        Pair(B.UpperArmL, B.UpperArmR, -150 * up - 80 * push, -150 * up - 80 * push, 10);
                        Pair(B.ForearmL, B.ForearmR, -30 * up, -30 * up);
                    }
                    Add(B.Spine, -12 * up + 16 * push); Add(B.Head, -10 * up + 6 * push);
                    if (Has(B.WingL)) Pair(B.WingL, B.WingR, 0, 0, 50 * up);
                    break;
                }
                case "throw":
                {
                    float wind = Bell(u, 0, .4f, .5f), release = Bell(u, .4f, .55f, 1);
                    bool heavy = Gait == GaitKind.Heavy;
                    Add(B.UpperArmR, -150 * wind - 70 * release, 0, 10);
                    if (heavy) Add(B.UpperArmL, -150 * wind - 70 * release, 0, -10);
                    Add(B.ForearmR, -40 * wind);
                    Add(B.Spine, -18 * wind + 22 * release, -20 * wind + 25 * release);
                    break;
                }
                case "slam":
                {
                    float raise = Bell(u, 0, .45f, .55f), crash = Bell(u, .45f, .58f, 1);
                    Pair(B.UpperArmL, B.UpperArmR, -165 * raise + 15 * crash, -165 * raise + 15 * crash, 10);
                    Pair(B.ForearmL, B.ForearmR, -20 * raise, -20 * raise);
                    Add(B.Spine, -15 * raise + 40 * crash); Add(B.Head, 10 * crash);
                    Pair(B.ThighL, B.ThighR, -30 * crash, -30 * crash, 10 * crash); Pair(B.ShinL, B.ShinR, 50 * crash, 50 * crash);
                    rootOffset += Vector3.down * legLength * .2f * crash * scale;
                    if (u > .5f && u - Time.deltaTime / actionLength <= .5f) Thud?.Invoke(entity, transform.position + transform.forward * height * .4f, 1);
                    break;
                }
                case "heal":
                {
                    float up = Bell(u, 0, .3f, 1);
                    Pair(B.UpperArmL, B.UpperArmR, -110 * up, -110 * up, 25 * up);
                    Add(B.Head, -15 * up);
                    break;
                }
                case "swell":
                    sac = 1 + u * 1.1f;
                    rootScale = Vector3.one * (1 + Mathf.Sin(u * 40) * .03f * u);
                    break;
            }
        }

        void Strike(float u)
        {
            float wind = Bell(u, 0, .16f, .3f), hit = Bell(u, .14f, .3f, 1);
            switch (Gait)
            {
                case GaitKind.Heavy:
                    Pair(B.UpperArmL, B.UpperArmR, -150 * wind + 25 * hit, -150 * wind + 25 * hit, 12);
                    Add(B.Spine, -12 * wind + 28 * hit);
                    Pair(B.ThighL, B.ThighR, -15 * hit, -15 * hit); Pair(B.ShinL, B.ShinR, 25 * hit, 25 * hit);
                    rootOffset += (transform.forward * .15f * hit - Vector3.up * .08f * hit) * height;
                    if (u > .3f && u - Time.deltaTime / actionLength <= .3f) Thud?.Invoke(entity, transform.position + transform.forward * height * .45f, .8f);
                    break;
                case GaitKind.Quadruped:
                case GaitKind.Hopper:
                    Add(B.Head, -10 * wind + 20 * hit); Add(B.Jaw, 35 * Bell(u, .1f, .2f, .35f));
                    rootOffset += transform.forward * height * (.25f * hit - .06f * wind);
                    break;
                case GaitKind.Shuffle:
                    lid = Mathf.Lerp(lid, u < .35f ? -88 : 0, .5f);
                    rootOffset += transform.forward * height * .2f * hit + Vector3.up * height * .1f * wind;
                    break;
                case GaitKind.Flyer:
                    Pair(B.ThighL, B.ThighR, -80 * hit, -80 * hit); Pair(B.ShinL, B.ShinR, -20 * hit, -20 * hit);
                    Pair(B.WingL, B.WingR, 0, 0, 55 * wind);
                    rootOffset += transform.forward * height * .2f * hit;
                    break;
                default:
                    Add(B.UpperArmR, -135 * wind + 30 * hit, 0, 18 * wind);
                    Add(B.ForearmR, -50 * wind + 10 * hit);
                    Add(B.UpperArmL, -30 * hit, 0, -10);
                    Add(B.Chest, 0, -22 * wind + 26 * hit);
                    Add(B.Spine, -6 * wind + 16 * hit);
                    rootOffset += transform.forward * height * .07f * hit;
                    break;
            }
        }

        static float Bell(float u, float start, float peak, float end)
        {
            if (u <= start || u >= end) return 0;
            if (u < peak) { float x = (u - start) / Mathf.Max(.0001f, peak - start); return x * x * (3 - 2 * x); }
            float y = 1 - (u - peak) / Mathf.Max(.0001f, end - peak); return y * y * (3 - 2 * y);
        }

        void Hurt()
        {
            hitAt = Time.time;
            Vector3 from = entity.LastAttacker != null ? entity.LastAttacker.transform.position : transform.position - transform.forward;
            hitDirection = transform.InverseTransformDirection(Vector3.ProjectOnPlane(transform.position - from, Vector3.up).normalized);
            if (hitDirection.sqrMagnitude < .01f) hitDirection = Vector3.back;
            float lost = lastHealth > 0 ? lastHealth - entity.Health : 0;
            hitStrength = Mathf.Lerp(.55f, 1.15f, Mathf.Clamp01(lost / Mathf.Max(1, entity.MaximumHealth * .3f)));
        }

        void Flinch()
        {
            float since = Time.time - hitAt;
            if (since > .6f) return;
            float e = Mathf.Exp(-since * 8) * Mathf.Cos(since * 22) * hitStrength;
            Add(B.Spine, hitDirection.z * 16 * e, 0, -hitDirection.x * 12 * e);
            Add(B.Chest, hitDirection.z * 8 * e);
            Add(B.Head, hitDirection.z * 22 * e, hitDirection.x * 14 * e);
            Pair(B.UpperArmL, B.UpperArmR, -20 * e, -20 * e, 25 * Mathf.Abs(e));
            rootOffset += transform.TransformDirection(hitDirection) * height * .05f * Mathf.Max(0, e);
        }

        void Trance(float t)
        {
            if (monster == null || entity.World == null || (lastPose != "brainwashed" && entity.World.StatusOf(monster) != "Brainwashed")) return;
            Add(B.Head, 9 + Mathf.Sin(t * 1.3f) * 3, 0, Mathf.Sin(t * 1.1f) * 5);
            Add(B.Spine, -4, 0, Mathf.Sin(t * 1.1f) * 3);
            if (lastPose != "attacking" && lastPose != "dance" && lastPose != "wave" && lastPose != "heal" && action == null && (Gait == GaitKind.Biped || Gait == GaitKind.Heavy))
                Pair(B.UpperArmL, B.UpperArmR, -28, -28, 6);
        }

        // The head turns to meet the player's eyes only when the body already
        // faces them (the eye-contact cone), so a gaze always means contact.
        void Look(float dt)
        {
            float targetYaw = 0, targetPitch = 0;
            var world = entity.World;
            if (world != null && world.View != null && Has(B.Head) && lastPose != "cover" && lastPose != "sleep" && lastPose != "dance")
            {
                Vector3 eye = monster != null ? monster.Eye : transform.position + Vector3.up * height * .85f;
                Vector3 to = world.View.transform.position - eye;
                Vector3 local = transform.InverseTransformDirection(to);
                float distance = to.magnitude;
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                if (distance < 9 && Mathf.Abs(yaw) < SandboxPerception.MonsterHalfAngle)
                {
                    targetYaw = Mathf.Clamp(yaw, -50, 50);
                    targetPitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -30, 30);
                }
            }
            float k = 1 - Mathf.Exp(-dt * 6);
            lookYaw = Mathf.Lerp(lookYaw, targetYaw, k); lookPitch = Mathf.Lerp(lookPitch, targetPitch, k);
            if (Has(B.Neck)) { Add(B.Neck, lookPitch * .35f, lookYaw * .35f); Add(B.Head, lookPitch * .65f, lookYaw * .65f); }
            else Add(B.Head, lookPitch, lookYaw);
        }

        void Death(float t)
        {
            float since = Time.time - deathAt;
            float buckle = Mathf.Clamp01(since / .3f);
            float fall = Mathf.Clamp01((since - .2f) / .45f);
            fall *= fall;
            if (Gait == GaitKind.Shuffle)
            {
                rootRotation = Quaternion.Euler(0, 0, 20 * deathSide * fall);
                lid = Mathf.Lerp(lid, -20, .2f);
                LidAndSac(t, false);
                return;
            }
            Pair(B.ThighL, B.ThighR, -45 * buckle, -40 * buckle, 10 * buckle);
            Pair(B.ShinL, B.ShinR, 75 * buckle, 70 * buckle);
            Add(B.Spine, 22 * buckle); Add(B.Head, 28 * buckle, 0, 15 * deathSide * fall);
            Pair(B.UpperArmL, B.UpperArmR, -20 * buckle, -30 * buckle, 40 * fall);
            Pair(B.ForearmL, B.ForearmR, -20 * buckle, -35 * buckle);
            if (Has(B.WingL)) Pair(B.WingL, B.WingR, 0, 0, -40 * fall);
            float bounce = since > .65f ? Mathf.Exp(-(since - .65f) * 9) * Mathf.Abs(Mathf.Sin((since - .65f) * 18)) * .05f : 0;
            rootRotation = Quaternion.Euler(0, 0, 84 * deathSide * fall);
            rootOffset = Vector3.up * (height * .13f * fall - legLength * .25f * buckle * (1 - fall) + bounce * height) * 1;
            if (!landedAfterDeath && since > .65f) { landedAfterDeath = true; Thud?.Invoke(entity, transform.position, Weight()); }
        }

        void Knocked(float t)
        {
            float f = t * 13;
            Pair(B.UpperArmL, B.UpperArmR, Mathf.Sin(f) * 40 - 60, Mathf.Sin(f + 1.7f) * 40 - 60, 70 + Mathf.Sin(f * 1.3f) * 25);
            Pair(B.ForearmL, B.ForearmR, -30 + Mathf.Sin(f * 1.1f) * 25, -30 + Mathf.Sin(f * .9f) * 25);
            Pair(B.ThighL, B.ThighR, Mathf.Sin(f * .8f) * 35 - 15, Mathf.Sin(f * .8f + 2) * 35 - 15, 12);
            Pair(B.ShinL, B.ShinR, 30 + Mathf.Sin(f) * 20, 30 + Mathf.Sin(f + 1) * 20);
            Add(B.Head, -25); Add(B.Spine, -12);
            if (Has(B.WingL)) Pair(B.WingL, B.WingR, 0, 0, Mathf.Sin(f * 1.5f) * 50);
        }

        void Secondary(float dt, float t, bool alive)
        {
            // Ears flop against vertical motion; tails swing against turns.
            float earTarget = Mathf.Clamp(-verticalSpeed * 6 - rootOffset.y / Mathf.Max(.01f, height) * 120, -30, 30);
            Spring(ref earSpring, ref earVelocity, earTarget, 90, 7, dt);
            if (Has(B.EarL)) Pair(B.EarL, B.EarR, earSpring * .4f, earSpring * .4f, earSpring + Mathf.Sin(t * 2.3f) * 3 * (alive ? 1 : 0));
            float tailTarget = Mathf.Clamp(-yawRate * .25f, -35, 35);
            Spring(ref tailSpring, ref tailVelocity, tailTarget, 60, 6, dt);
            float wag = alive ? 1 : 0;
            float tailRate = Species == "mouse" ? 7 : 3.2f;
            for (int i = 0; i < 4; i++)
            {
                B tb = B.Tail1 + i;
                if (!Has(tb)) break;
                float yaw = tailSpring * (.4f + i * .25f) + Mathf.Sin(t * tailRate - i * .8f) * (8 + i * 4) * wag;
                float pitch = Mathf.Sin(t * tailRate * .5f - i * .6f) * 4 * wag - run * 8;
                Add(tb, pitch, yaw);
            }
            LidAndSac(t, alive);
        }

        void LidAndSac(float t, bool alive)
        {
            if (Has(B.Lid))
            {
                float target;
                if (!alive) target = -20;
                else if (entity.Asleep) target = 0;
                else if (action == "strike" || action == "bite") target = lid;
                else if (lastPose == "attacking") target = -80 + Mathf.Sin(t * 20) * 25;
                else target = -55 - Mathf.Abs(Mathf.Sin(t * 3)) * 15 - Mathf.Abs(Mathf.Sin(phase * 1.4f)) * 10 * move;
                lid = Mathf.MoveTowards(lid, target, 300 * Time.deltaTime);
                Add(B.Lid, lid);
                if (Has(B.Tongue)) Add(B.Tongue, Mathf.Sin(t * 4) * 10, Mathf.Sin(t * 2.6f) * 12);
            }
            if (Has(B.Sac))
            {
                bool swelling = action == "swell";
                if (!swelling) sac = Mathf.MoveTowards(sac, 1, Time.deltaTime * 2);
                float pulse = alive ? (Species == "bomb_toad" ? Mathf.Abs(Mathf.Sin(t * (lastPose == "attacking" ? 9 : 2))) * .25f : Mathf.Abs(Mathf.Sin(t * 2.5f)) * .12f) : 0;
                scaleOffset[(int)B.Sac] = Vector3.one * (sac + pulse);
            }
        }

        static void Spring(ref float value, ref float velocity, float target, float stiffness, float damping, float dt)
        {
            velocity += ((target - value) * stiffness - velocity * damping) * dt;
            value += velocity * dt;
        }

        void Sockets()
        {
            if (monster != null && monster.Hand != null)
            {
                var socket = Species == "mouse" && mouthSocket != null ? mouthSocket : handSocket != null ? handSocket : mouthSocket;
                if (socket != null) monster.Hand.SetPositionAndRotation(socket.position, socket.rotation);
            }
            if (naturalWeapon != null) naturalWeapon.gameObject.SetActive(entity.HeldItem == null && entity.Alive);
        }

        void Flash()
        {
            bool flashing = entity.Alive && Time.time - hitAt < .09f;
            if (flashing == flashOn) return;
            flashOn = flashing;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (flashing)
                {
                    block.Clear(); block.SetColor("_BaseColor", new Color(2.4f, 2.2f, 2.1f, 1)); block.SetColor("_Color", new Color(2.4f, 2.2f, 2.1f, 1));
                    r.SetPropertyBlock(block);
                }
                else if (entity.Alive) r.SetPropertyBlock(null);
                else
                {
                    Color dead = Color.Lerp(Color.white, new Color(.16f, .17f, .19f), .72f);
                    block.Clear(); block.SetColor("_BaseColor", dead); block.SetColor("_Color", dead); r.SetPropertyBlock(block);
                }
            }
        }
    }
}
