using System;
using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Sound effects and ambience synthesized by art/audio/generate_sfx.py.
    // Clips load from Resources/Audio through the sfx.json manifest; one-shots
    // play from a pool of 3D sources with per-sound volume, pitch jitter and
    // rolloff. While the player records a command or chants, everything except
    // the quiet cast drone ducks, so the microphone hears speech, not the game.
    // Every entry point is a no-op when the library is missing.
    public sealed class GameAudio : MonoBehaviour
    {
        [Serializable] sealed class Sound
        {
            public string id; public string[] files; public bool loop; public float volume = 1; public float pitchJitter;
            public float minDistance = 1.5f, maxDistance = 25; public string category;
        }
        [Serializable] sealed class Manifest { public int version; public Sound[] sounds; }

        sealed class Voice { public AudioSource Source; public float Base; public bool Duck; public float Ends; }

        static GameAudio instance;
        readonly Dictionary<string, Sound> sounds = new Dictionary<string, Sound>();
        readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
        readonly Dictionary<string, float> lastPlayed = new Dictionary<string, float>();
        readonly List<Voice> pool = new List<Voice>();
        Voice ambience, castLoop, chantLoop;
        string ambienceId;
        float duck = 1;
        SandboxBootstrap app;

        public static bool Ready => instance != null && instance.sounds.Count > 0;
        const string VolumeKey = "brainwash.volume", MuteKey = "brainwash.muted";
        public static float Volume
        {
            get { try { return PlayerPrefs.GetFloat(VolumeKey, .8f); } catch (Exception) { return .8f; } }
            set { try { PlayerPrefs.SetFloat(VolumeKey, Mathf.Clamp01(value)); } catch (Exception) { } ApplyVolume(); }
        }
        public static bool Muted
        {
            get { try { return PlayerPrefs.GetInt(MuteKey, 0) == 1; } catch (Exception) { return false; } }
            set { try { PlayerPrefs.SetInt(MuteKey, value ? 1 : 0); } catch (Exception) { } ApplyVolume(); }
        }
        static void ApplyVolume() => AudioListener.volume = Muted ? 0 : Volume;

        public static GameAudio Create(SandboxBootstrap app)
        {
            var audio = app.gameObject.AddComponent<GameAudio>();
            audio.app = app;
            return audio;
        }

        void Awake()
        {
            instance = this;
            var asset = Resources.Load<TextAsset>("Audio/sfx");
            if (asset != null)
            {
                try
                {
                    var manifest = JsonUtility.FromJson<Manifest>(asset.text);
                    foreach (var sound in manifest?.sounds ?? Array.Empty<Sound>()) if (!string.IsNullOrEmpty(sound.id)) sounds[sound.id] = sound;
                }
                catch (ArgumentException) { sounds.Clear(); }
            }
            for (int i = 0; i < 28; i++) pool.Add(new Voice { Source = NewSource("Sfx voice " + i) });
            ApplyVolume();
            CreatureAnimator.Footstep += OnCreatureStep;
            CreatureAnimator.Thud += OnCreatureThud;
            CreatureAnimator.WingBeat += OnWingBeat;
            SandboxMonster.Cued += OnCue;
            SandboxBootstrap.PlayerFootstep += OnPlayerStep;
            SandboxBootstrap.PlayerLanded += OnPlayerLanded;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            CreatureAnimator.Footstep -= OnCreatureStep;
            CreatureAnimator.Thud -= OnCreatureThud;
            CreatureAnimator.WingBeat -= OnWingBeat;
            SandboxMonster.Cued -= OnCue;
            SandboxBootstrap.PlayerFootstep -= OnPlayerStep;
            SandboxBootstrap.PlayerLanded -= OnPlayerLanded;
        }

        AudioSource NewSource(string name)
        {
            var go = new GameObject(name); go.transform.SetParent(transform, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false; source.dopplerLevel = 0; source.rolloffMode = AudioRolloffMode.Logarithmic;
            return source;
        }

        AudioClip[] Clips(Sound sound)
        {
            if (clips.TryGetValue(sound.id, out var found)) return found;
            var list = new List<AudioClip>();
            foreach (var file in sound.files ?? Array.Empty<string>())
            {
                var clip = Resources.Load<AudioClip>("Audio/" + file);
                if (clip != null) list.Add(clip);
            }
            found = list.ToArray();
            clips[sound.id] = found;
            return found;
        }

        // Loads every clip in the manifest: (sounds, clips, missing file names).
        public static (int sounds, int clips, List<string> missing) Verify()
        {
            var missing = new List<string>(); int count = 0;
            if (instance == null) return (0, 0, missing);
            foreach (var sound in instance.sounds.Values)
                foreach (var file in sound.files ?? Array.Empty<string>())
                {
                    if (Resources.Load<AudioClip>("Audio/" + file) != null) count++;
                    else missing.Add(file);
                }
            return (instance.sounds.Count, count, missing);
        }

        // ------------------------------------------------------------------
        // Public entry points
        public static bool Has(string id) => instance != null && instance.sounds.ContainsKey(id);

        // A 3D one-shot at a point. Returns false when the sound does not exist.
        public static bool Play(string id, Vector3 position, float volume = 1, float pitch = 1, float minInterval = 0)
            => instance != null && instance.PlayInternal(id, position, true, volume, pitch, minInterval, null);

        // A 2D one-shot (the player's own sounds and the interface).
        public static bool Play2D(string id, float volume = 1, float pitch = 1, float minInterval = 0)
            => instance != null && instance.PlayInternal(id, Vector3.zero, false, volume, pitch, minInterval, null);

        // A looping source that follows a transform and dies with it (fires, burning bodies).
        public static AudioSource Attach(string id, Transform follow, float volume = 1)
        {
            if (instance == null || follow == null || !instance.sounds.TryGetValue(id, out var sound)) return null;
            var set = instance.Clips(sound); if (set.Length == 0) return null;
            var source = follow.gameObject.AddComponent<AudioSource>();
            source.clip = set[0]; source.loop = true; source.playOnAwake = false; source.dopplerLevel = 0;
            source.spatialBlend = 1; source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = sound.minDistance; source.maxDistance = sound.maxDistance;
            source.volume = sound.volume * volume * instance.duck;
            source.time = UnityEngine.Random.Range(0, source.clip.length);
            source.Play();
            return source;
        }

        bool PlayInternal(string id, Vector3 position, bool spatial, float volume, float pitch, float minInterval, Voice into)
        {
            if (!sounds.TryGetValue(id, out var sound)) return false;
            var set = Clips(sound); if (set.Length == 0) return false;
            float now = Time.unscaledTime;
            if (minInterval > 0 && lastPlayed.TryGetValue(id, out float last) && now - last < minInterval) return true;
            lastPlayed[id] = now;
            var voice = into ?? NextVoice();
            var source = voice.Source;
            source.Stop();
            source.clip = set[UnityEngine.Random.Range(0, set.Length)];
            source.loop = false;
            source.spatialBlend = spatial ? 1 : 0;
            source.minDistance = sound.minDistance; source.maxDistance = sound.maxDistance;
            source.transform.position = position;
            source.pitch = pitch * (1 + UnityEngine.Random.Range(-sound.pitchJitter, sound.pitchJitter));
            voice.Base = sound.volume * volume; voice.Duck = true;
            source.volume = voice.Base * duck;
            voice.Ends = now + source.clip.length / Mathf.Max(.05f, Mathf.Abs(source.pitch)) + .05f;
            source.Play();
            return true;
        }

        Voice NextVoice()
        {
            Voice oldest = pool[0];
            float now = Time.unscaledTime;
            foreach (var voice in pool)
            {
                if (!voice.Source.isPlaying || now > voice.Ends) return voice;
                if (voice.Ends < oldest.Ends) oldest = voice;
            }
            return oldest;
        }

        // ------------------------------------------------------------------
        void Update()
        {
            if (sounds.Count == 0) return;
            // Duck the game while the microphone records speech.
            bool speaking = app != null && (app.IsVoiceRecording || app.Chanting);
            duck = Mathf.MoveTowards(duck, speaking ? .15f : 1, Time.unscaledDeltaTime * (speaking ? 6 : 1.5f));
            foreach (var voice in pool) if (voice.Source.isPlaying && voice.Duck) voice.Source.volume = voice.Base * duck;
            if (ambience != null) ambience.Source.volume = ambience.Base * duck;
            // The cast drone and the chant pad stay audible but soft while you speak,
            // and fade out quickly when the cast or the chant ends.
            castLoop = HoldLoop(castLoop, "cast_loop", app != null && app.World != null && app.World.Casting, .55f, 3, 4);
            chantLoop = HoldLoop(chantLoop, "chant_pad", app != null && app.Chanting, 1, 1.5f, 3);
            Chatter();
            // Ambience follows the demo that is running.
            string wanted = app == null || app.InMenu ? null : AmbienceFor(app.World?.Scenario?.Id);
            if (wanted != ambienceId)
            {
                if (ambience != null) { Destroy(ambience.Source.gameObject); ambience = null; }
                ambienceId = wanted; poses.Clear();
                if (wanted != null) ambience = StartLoop(wanted, 1, true);
            }
        }

        // Monsters near the player make occasional noises, and a gargoyle grinds
        // when it comes out of its stone pose (it moved while you looked away).
        readonly Dictionary<SandboxMonster, string> poses = new Dictionary<SandboxMonster, string>();
        float nextChatter;
        void Chatter()
        {
            var world = app != null ? app.World : null;
            if (world == null || app.InMenu || app.Player == null) return;
            bool roll = Time.time >= nextChatter;
            if (roll) nextChatter = Time.time + .5f;
            foreach (var monster in world.Monsters)
            {
                if (monster == null || !monster.Entity.Alive) continue;
                string pose = monster.Pose;
                poses.TryGetValue(monster, out string last);
                poses[monster] = pose;
                if (last == "statue" && pose != "statue") Play("gargoyle_grind", monster.Entity.Center, .8f);
                if (!roll || world.IsControlled(monster)) continue;
                if (Vector3.Distance(monster.transform.position, app.Player.position) > 11) continue;
                string id; float chance;
                switch (monster.Entity.Species)
                {
                    case "goblin": id = "goblin_alert"; chance = .05f; break;
                    case "mouse": id = "mouse_squeak"; chance = .07f; break;
                    case "bomb_toad": id = "toad_croak"; chance = .07f; break;
                    case "nurse": id = "nurse_hiss"; chance = .04f; break;
                    case "ogre": id = "ogre_roar"; chance = .02f; break;
                    case "harpy": id = "harpy_screech"; chance = .03f; break;
                    default: continue;
                }
                if (pose == "sleep" || pose == "statue") continue;
                if (UnityEngine.Random.value < chance) Play(id, monster.Entity.Center, .45f);
            }
        }

        // A 2D loop that fades in while `on` holds and is removed once it fades out.
        Voice HoldLoop(Voice voice, string id, bool on, float level, float fadeIn, float fadeOut)
        {
            if (voice == null && on) voice = StartLoop(id, 0, false);
            if (voice == null) return null;
            voice.Base = Mathf.MoveTowards(voice.Base, on ? level : 0, Time.unscaledDeltaTime * (on ? fadeIn : fadeOut));
            voice.Source.volume = voice.Base * (sounds.TryGetValue(id, out var sound) ? sound.volume : 1);
            if (on || voice.Base > 0) return voice;
            voice.Source.Stop(); Destroy(voice.Source.gameObject);
            return null;
        }

        Voice StartLoop(string id, float volume, bool ambient)
        {
            if (!sounds.TryGetValue(id, out var sound)) return null;
            var set = Clips(sound); if (set.Length == 0) return null;
            var voice = new Voice { Source = NewSource(id), Base = (ambient ? sound.volume : 1) * volume, Duck = ambient };
            voice.Source.clip = set[0]; voice.Source.loop = true; voice.Source.spatialBlend = 0;
            voice.Source.volume = voice.Base * (ambient ? duck : sound.volume);
            voice.Source.Play();
            return voice;
        }

        static string AmbienceFor(string scenario)
        {
            switch (scenario)
            {
                case "pit_arena": return "amb_pit";
                case "puppet_war": return "amb_courtyard";
                case "ledge_key": return "amb_chasm";
                case "rite_of_return": return "amb_chapel";
                case "eye_heist": return "amb_vault";
                default: return "amb_room";
            }
        }

        // ------------------------------------------------------------------
        // Game events
        static string Species(SandboxEntity entity) => entity == null ? null : entity.Species == "ice_golem" ? "golem" : entity.Species == "bomb_toad" ? "toad" : entity.Species;

        // A species sound with a fallback (e.g. goblin_attack, else a generic one).
        public static void Creature(SandboxEntity entity, string suffix, string fallback = null, float volume = 1, float minInterval = .25f)
        {
            if (instance == null || entity == null) return;
            string id = Species(entity) + "_" + suffix;
            if (!Has(id)) id = fallback;
            if (id != null) Play(id, entity.Center, volume, 1, 0);
        }

        void OnCue(SandboxMonster monster, string action)
        {
            if (monster == null) return;
            var entity = monster.Entity; string species = Species(entity);
            Vector3 at = entity.Center;
            switch (action)
            {
                case "strike":
                    Play(species == "ogre" || species == "golem" ? "throw_whoosh" : "kick_whoosh", at, .7f);
                    Creature(entity, "attack");
                    if (species == "harpy") Play("harpy_screech", at);
                    break;
                case "bite": Play("mimic_chomp", at); break;
                case "cast":
                    if (species == "imp") Play("fireball_cast", at);
                    else if (species == "golem") Play("ice_bolt_cast", at);
                    else if (species == "nurse") { Play("nurse_hiss", at); Play("acid_sizzle", at, .6f); }
                    else Play("fireball_cast", at);
                    break;
                case "throw": Play("throw_whoosh", at); Creature(entity, "attack"); break;
                case "slam": Play("explosion", entity.transform.position, .8f, .7f); Creature(entity, "roar", "ogre_attack"); break;
                case "heal": Play("heal", at); break;
                case "swell": Play("toad_inflate", at); break;
            }
        }

        void OnCreatureStep(SandboxEntity entity, Vector3 point, float weight)
        {
            if (app == null || app.Player == null || Vector3.Distance(app.Player.position, point) > 22) return;
            string id = entity.Species == "gargoyle" ? "footstep_stone" : weight >= .5f ? "footstep_heavy" : weight >= .12f ? "footstep_medium" : "footstep_light";
            Play(id, point, .55f + weight * .45f);
        }

        void OnCreatureThud(SandboxEntity entity, Vector3 point, float weight)
        {
            if (entity != null && entity.Species == "bomb_toad" && entity.Alive) Play("toad_hop", point, .8f);
            else if (entity != null && entity.Species == "mimic" && entity.Alive) Play("wood_impact", point, .5f, 1.2f);
            else Play("body_impact", point, .4f + weight * .6f, 1.1f - weight * .3f);
        }

        void OnWingBeat(SandboxEntity entity, Vector3 point) => Play("wing_flap", point, .55f);

        void OnPlayerStep(Vector3 point, float speed) => Play2D("footstep_player", speed > 4 ? .5f : .32f);
        void OnPlayerLanded(float impact) => Play2D("land", Mathf.Clamp01(.3f + impact / 12f));

        // Damage taken: a hurt sound per species, at most a few times a second.
        public static void Hurt(SandboxEntity victim, float amount, string element)
        {
            if (instance == null || victim == null) return;
            if (victim.Kind == "monster")
            {
                if (element == "fire" || element == "acid") { Creature(victim, "hurt", null, .6f); return; }
                Creature(victim, "hurt");
            }
            else if (victim.Kind == "object" && amount > 3) Play(victim.ObjectKind == "barrel" || victim.ObjectKind == "crate" ? "wood_impact" : "staff_hit_hard", victim.Center, .8f);
        }

        public static void Died(SandboxEntity victim, string element)
        {
            if (instance == null || victim == null) return;
            if (victim.Kind == "monster")
            {
                Creature(victim, "death", "body_impact");
                if (element == "fall") Play("fall_whistle", victim.Center);
                if (element == "spike" || element == "pit") Play("spike_impale", victim.Center);
            }
            else if (victim.Kind == "object" && victim.ObjectKind == "crate") Play("wood_break", victim.Center);
            else if (victim.Kind == "object" && victim.ObjectKind == "door") Play("door_smash", victim.Center);
        }
    }
}
