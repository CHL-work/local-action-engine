using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Temporary rigid-body knockback for a CharacterController monster. Normal
    // locomotion stays kinematic; kicks, explosions and throws hand the body to
    // PhysX until it settles, then the controller takes over again.
    public sealed class PhysicsBody : MonoBehaviour
    {
        SandboxEntity entity;
        SandboxMonster monster;
        Rigidbody body;
        GameObject colliderHost;
        static PhysicsMaterial knockedMaterial;
        float knockedAt, settledFor, lastImpact;
        Vector3 lastVelocity;
        public bool Knocked => body != null;
        public SandboxEntity Thrower { get; private set; }
        public float Mass => entity != null && entity.Profile != null ? entity.Profile.Mass : 50;
        public Vector3 Velocity => body != null ? body.linearVelocity : Vector3.zero;

        public static PhysicsBody For(SandboxMonster monster)
        {
            if (monster == null) return null;
            var existing = monster.Comp<PhysicsBody>();
            return existing != null ? existing : monster.gameObject.AddComponent<PhysicsBody>();
        }

        void Ensure()
        {
            if (entity == null) entity = GetComponent<SandboxEntity>();
            if (monster == null) monster = GetComponent<SandboxMonster>();
        }

        // Velocity is a velocity change in m/s, already scaled by the caller for mass.
        public void Knock(Vector3 velocity, SandboxEntity thrower)
        {
            Ensure();
            if (monster == null || monster.Motor == null || velocity.sqrMagnitude < .01f) return;
            Thrower = thrower;
            if (body == null)
            {
                // Recover() destroys the body immediately, so a second knock in the
                // same frame always gets a fresh Rigidbody.
                body = this.Comp<Rigidbody>();
                if (body == null) body = gameObject.AddComponent<Rigidbody>();
                if (body == null) return;
                monster.Motor.enabled = false;
                if (colliderHost == null)
                {
                    colliderHost = new GameObject("Knockback collider");
                    colliderHost.transform.SetParent(transform, false);
                    var capsule = colliderHost.AddComponent<CapsuleCollider>();
                    capsule.height = monster.Motor.height; capsule.radius = monster.Motor.radius; capsule.center = monster.Motor.center;
                    if (knockedMaterial == null) knockedMaterial = new PhysicsMaterial("Knocked body") { dynamicFriction = .55f, staticFriction = .6f, bounciness = .15f };
                    capsule.sharedMaterial = knockedMaterial;
                }
                body.isKinematic = false; body.detectCollisions = true;
                body.mass = Mathf.Max(1, Mass);
                body.linearDamping = .05f; body.angularDamping = 1.6f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.maxAngularVelocity = 9;
                knockedAt = Time.time;
                transform.position += Vector3.up * .05f;
            }
            body.linearVelocity = velocity;
            Vector3 spin = Vector3.Cross(Vector3.up, velocity.normalized) * velocity.magnitude * .9f;
            body.angularVelocity = spin;
            settledFor = 0;
            monster.SetPose("knocked");
            entity.World?.MonsterKnocked(monster, velocity, thrower);
        }

        void FixedUpdate()
        {
            if (body == null) return;
            lastVelocity = body.linearVelocity;
            bool slow = body.linearVelocity.magnitude < .5f && body.angularVelocity.magnitude < 1.4f;
            settledFor = slow ? settledFor + Time.fixedDeltaTime : 0;
            if ((settledFor > .35f && Time.time - knockedAt > .35f) || Time.time - knockedAt > 6f) Recover();
            else if (transform.position.y < -14) { entity.TakeDamage(9999, Thrower, "fall"); Recover(); }
        }

        void OnCollisionEnter(Collision collision)
        {
            if (body == null || entity == null) return;
            float speed = collision.relativeVelocity.magnitude;
            if (speed < 4.6f || Time.time - lastImpact < .12f) return;
            lastImpact = Time.time;
            var world = entity.World;
            var other = collision.collider.GetComponentInParent<SandboxEntity>();
            float severity = speed - 4.6f;
            float massScale = Mathf.Clamp(Mass / 60f, .5f, 2.5f);
            // Slamming into anything hurts the flying body. Credit goes to whoever launched it.
            if (entity.Alive) entity.TakeDamage(severity * 6.5f * massScale, Thrower, "impact");
            if (other != null && other != entity && other.Alive && world != null)
            {
                float otherMass = other.Profile != null ? other.Profile.Mass : other.Kind == "player" ? 70 : 40;
                float share = Mass / (Mass + otherMass);
                float damage = severity * 5.5f * Mathf.Clamp(Mass / 50f, .3f, 4f);
                // The struck creature saw this body hit it: a monster-on-monster hit (TDD §10).
                if (other == world.PlayerEntity) world.DamagePlayer(Mathf.Round(damage * .5f), entity.DisplayName + "'s flying body");
                else other.TakeDamage(damage, entity, "impact");
                var otherMonster = other.Comp<SandboxMonster>();
                Vector3 push = lastVelocity * share * .85f + Vector3.up * 1.6f;
                if (otherMonster != null && other.Alive) For(otherMonster).Knock(push, entity);
                else if (other == world.PlayerEntity) world.KnockPlayer(push * .6f);
                world.Impact(entity, other, speed);
            }
            else world?.Impact(entity, other, speed);
        }

        public void Recover()
        {
            if (body == null) return;
            Ensure();
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (forward.sqrMagnitude < .01f) forward = Vector3.ProjectOnPlane(-transform.up, Vector3.up);
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            // Immediate destruction: a deferred Destroy would make a knock later in
            // this frame fail to add its Rigidbody and freeze the monster.
            DestroyImmediate(body); body = null;
            if (colliderHost != null) { DestroyImmediate(colliderHost); colliderHost = null; }
            transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            if (!entity.Alive) return;
            // Stand the controller up on whatever is underneath.
            if (Physics.Raycast(transform.position + Vector3.up * 1.2f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(transform)) transform.position = hit.point + Vector3.up * .02f;
            monster.Motor.enabled = true;
            monster.SetPose("idle");
            entity.World?.MonsterRecovered(monster);
        }

        void OnDestroy() { if (colliderHost != null) Destroy(colliderHost); }
    }

    // A free physical prop or item: thrown crates, barrels, pebbles, cheese,
    // keys knocked off a ledge. Attach/Drop switch it between kinematic and free.
    public sealed class PhysicsProp : MonoBehaviour
    {
        SandboxEntity entity;
        Rigidbody body;
        float lastImpact;
        public SandboxEntity Thrower;
        public float ThrownAt = -100;
        public Rigidbody Body => body;
        // A thrown item starts inside its thrower's body; ignore that body briefly
        // so the throw is not deflected and does not hit the thrower.
        readonly List<Collider> ignored = new List<Collider>();
        float ignoreUntil;

        public static PhysicsProp Make(SandboxEntity entity, float mass)
        {
            var prop = entity.Comp<PhysicsProp>();
            if (prop == null) prop = entity.gameObject.AddComponent<PhysicsProp>();
            prop.entity = entity;
            prop.body = entity.Comp<Rigidbody>();
            if (prop.body == null) prop.body = entity.gameObject.AddComponent<Rigidbody>();
            prop.body.mass = mass;
            prop.body.linearDamping = .08f; prop.body.angularDamping = .4f;
            prop.body.interpolation = RigidbodyInterpolation.Interpolate;
            prop.body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            entity.Grabbable = true;
            return prop;
        }

        public void SetHeld(bool held)
        {
            if (body == null) return;
            // Kinematic bodies do not support swept continuous detection.
            if (held) { body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; body.isKinematic = true; body.detectCollisions = false; }
            else
            {
                // A held body followed its holder's transform; let go from there, not
                // from the pose the body last simulated. A free body keeps its own pose.
                if (body.isKinematic) { body.position = transform.position; body.rotation = transform.rotation; }
                body.isKinematic = false; body.detectCollisions = true; body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }

        // Moves the prop at once. With interpolation on, a Transform write alone can
        // be overwritten by the body's old pose (a swallowed pot bursting at the Mimic).
        public void Teleport(Vector3 position)
        {
            transform.position = position;
            if (body == null) return;
            body.position = position;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        }

        // PhysX can only pair colliders that are enabled and active; Unity logs an
        // error for any other pair.
        static bool Live(Collider collider) => collider != null && collider.enabled && collider.gameObject.activeInHierarchy;

        public void Throw(Vector3 velocity, SandboxEntity thrower, SandboxEntity passThrough = null)
        {
            if (body == null) return;
            SetHeld(false);
            body.linearVelocity = velocity;
            body.angularVelocity = Random.insideUnitSphere * 4;
            Thrower = thrower; ThrownAt = Time.time;
            RestoreIgnored();
            var source = passThrough != null ? passThrough : thrower;
            if (source == null || entity == null) return;
            var mine = entity.GetComponents<Collider>();
            foreach (var other in source.GetComponentsInChildren<Collider>())
            {
                if (!Live(other)) continue;
                foreach (var own in mine) if (Live(own)) Physics.IgnoreCollision(own, other, true);
                ignored.Add(other);
            }
            ignoreUntil = Time.time + .5f;
        }

        void Update() { if (ignored.Count > 0 && Time.time >= ignoreUntil) RestoreIgnored(); }

        void RestoreIgnored()
        {
            if (entity == null) { ignored.Clear(); return; }
            var mine = entity.GetComponents<Collider>();
            // A collider that was disabled since has already lost the ignore state.
            foreach (var other in ignored)
                if (Live(other)) foreach (var own in mine) if (Live(own)) Physics.IgnoreCollision(own, other, false);
            ignored.Clear();
        }

        void OnCollisionEnter(Collision collision)
        {
            if (body == null || entity == null || body.isKinematic) return;
            float speed = collision.relativeVelocity.magnitude;
            var world = entity.World;
            if (world == null || speed < 3f || Time.time - lastImpact < .15f) return;
            lastImpact = Time.time;
            world.PropImpact(entity, collision.collider.GetComponentInParent<SandboxEntity>(), speed, Thrower, body.mass);
        }
    }

    public enum HazardKind { Spikes, Fire, Void, Acid }

    // Axis-aligned danger volume, checked by the world each frame. Spike pits are
    // holes with spikes below; navigation treats every hazard as unwalkable.
    public sealed class Hazard : MonoBehaviour
    {
        public HazardKind Kind;
        public Bounds Volume;
        public float DamagePerSecond = 10;
        public SandboxEntity Source;
        public float Expires = float.PositiveInfinity;
        public string Label = "hazard";
        public bool Walkable;
        public bool Contains(Vector3 point) => Volume.Contains(point);
    }

    // Short-lived visual effect: expanding flash, fading pool, noise ring.
    // Effects own their material instance and destroy it with themselves.
    public sealed class TimedEffect : MonoBehaviour
    {
        public float Born, Lifetime = .5f, GrowTo = 1, FadeFrom;
        public Vector3 StartScale = Vector3.one;
        public bool Grow, Flicker, FlatGrow;
        Renderer cached;
        float startAlpha = 1;
        void Start()
        {
            Born = Time.time; StartScale = transform.localScale; cached = GetComponent<Renderer>();
            if (cached != null && cached.sharedMaterial != null) startAlpha = cached.sharedMaterial.color.a;
        }
        void Update()
        {
            float t = (Time.time - Born) / Mathf.Max(.01f, Lifetime);
            if (t >= 1) { Destroy(gameObject); return; }
            if (Grow)
            {
                float k = Mathf.Lerp(1, GrowTo, Mathf.Sqrt(t));
                transform.localScale = FlatGrow ? new Vector3(StartScale.x * k, StartScale.y, StartScale.z * k) : StartScale * k;
            }
            if (Flicker) transform.localScale = StartScale * (1 + Mathf.Sin(Time.time * 37 + Born) * .12f);
            if (cached != null && cached.sharedMaterial != null)
            {
                float fade = t <= FadeFrom ? 1 : 1 - (t - FadeFrom) / Mathf.Max(.01f, 1 - FadeFrom);
                var color = cached.sharedMaterial.color; color.a = startAlpha * fade; cached.sharedMaterial.color = color;
            }
        }
        void OnDestroy() { if (cached != null && cached.sharedMaterial != null) Destroy(cached.sharedMaterial); }
    }

    // GetComponent returns a non-null "fake null" in the Editor when the
    // component is missing, so ?. and ?? must never be applied to it.
    public static class ComponentLookup
    {
        public static T Comp<T>(this Component owner) where T : Component => owner != null && owner.TryGetComponent(out T found) ? found : null;
        public static T Comp<T>(this GameObject owner) where T : Component => owner != null && owner.TryGetComponent(out T found) ? found : null;
    }

    public static class DemoPhysics
    {
        // Kick and blast impulses are scaled by mass: light goblins fly, golems barely move.
        public static float MassScale(SandboxEntity entity, float reference = 60, float min = .1f, float max = 1.6f)
        {
            float mass = entity != null && entity.Profile != null ? entity.Profile.Mass : 50;
            return Mathf.Clamp(reference / Mathf.Max(1, mass), min, max);
        }

        // Launch velocity that lands on a target point from origin with a given apex height.
        public static Vector3 Ballistic(Vector3 origin, Vector3 target, float apex)
        {
            float g = -Physics.gravity.y;
            float up = Mathf.Max(apex, target.y - origin.y + .6f);
            float vy = Mathf.Sqrt(2 * g * Mathf.Max(.3f, up));
            float rise = vy / g;
            float fall = Mathf.Sqrt(2 * Mathf.Max(.05f, up - (target.y - origin.y)) / g);
            Vector3 flat = Vector3.ProjectOnPlane(target - origin, Vector3.up);
            return flat / Mathf.Max(.2f, rise + fall) + Vector3.up * vy;
        }

        static readonly RaycastHit[] sweepHits = new RaycastHit[24];

        // Sweeps a sphere along a ballistic path. True when something solid that
        // is not a creature, an item or one of the ignored bodies stands in the way
        // before the landing point.
        public static bool ArcBlocked(Vector3 origin, Vector3 velocity, Vector3 landing, float radius, Transform ignoreA, Transform ignoreB)
        {
            Vector3 gravity = Physics.gravity, position = origin, v = velocity;
            float flatTotal = Vector3.ProjectOnPlane(landing - origin, Vector3.up).magnitude;
            const float step = .05f;
            for (float time = 0; time < 5; time += step)
            {
                Vector3 next = position + v * step + gravity * (.5f * step * step);
                v += gravity * step;
                Vector3 delta = next - position; float length = delta.magnitude;
                if (length > .0001f)
                {
                    int count = Physics.SphereCastNonAlloc(position, radius, delta / length, sweepHits, length, ~0, QueryTriggerInteraction.Ignore);
                    float nearest = float.PositiveInfinity; Vector3 point = Vector3.zero;
                    for (int i = 0; i < count; i++)
                    {
                        var hit = sweepHits[i];
                        if (hit.distance <= 0 || hit.distance >= nearest) continue; // overlaps where the sweep starts
                        var other = hit.collider.transform;
                        if ((ignoreA != null && other.IsChildOf(ignoreA)) || (ignoreB != null && other.IsChildOf(ignoreB))) continue;
                        var entity = hit.collider.GetComponentInParent<SandboxEntity>();
                        if (entity != null && (entity.Kind == "monster" || entity.Kind == "player" || entity.Kind == "item" || entity.Kind == "remains")) continue;
                        nearest = hit.distance; point = hit.point;
                    }
                    if (!float.IsPositiveInfinity(nearest)) return Vector3.Distance(point, landing) > .9f;
                }
                position = next;
                if (Vector3.ProjectOnPlane(position - origin, Vector3.up).magnitude >= flatTotal) return false;
            }
            return false;
        }

        public static readonly List<Hazard> Hazards = new List<Hazard>();
        public static bool HazardAt(Vector3 point, bool includeWalkable = false)
        {
            foreach (var hazard in Hazards)
                if (hazard != null && (includeWalkable || !hazard.Walkable) && hazard.Contains(point + Vector3.up * .3f)) return true;
            return false;
        }
    }
}
