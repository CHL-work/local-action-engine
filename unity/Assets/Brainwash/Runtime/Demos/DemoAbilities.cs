using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // Cheese Mouse trigger (monsters §cheese_theft): the colony pours out of
    // the walls at the accused, bites it and scares it off.
    public sealed class RatTideEffect : MonoBehaviour
    {
        SandboxWorld world;
        SandboxEntity target, mouse;
        readonly List<Transform> rats = new List<Transform>();
        readonly List<Vector3> offsets = new List<Vector3>();
        float started, nextBite;
        const float Duration = 3.6f;

        public void Begin(SandboxWorld world, SandboxEntity target, SandboxEntity mouse)
        {
            this.world = world; this.target = target; this.mouse = mouse; started = Time.time;
            Vector3 origin = mouse.transform.position;
            for (int i = 0; i < 16; i++)
            {
                var rat = GameObject.CreatePrimitive(PrimitiveType.Capsule); rat.name = "Rat";
                var collider = rat.Comp<Collider>(); collider.enabled = false; Destroy(collider);
                rat.transform.SetParent(transform, false);
                rat.transform.localScale = new Vector3(.16f, .12f, .16f);
                rat.transform.rotation = Quaternion.Euler(90, 0, 0);
                Vector2 ring = Random.insideUnitCircle.normalized * Random.Range(2f, 5f);
                rat.transform.position = origin + new Vector3(ring.x, .1f, ring.y);
                rat.Comp<Renderer>().sharedMaterial = world.Material(new Color(.42f, .4f, .42f));
                rats.Add(rat.transform); offsets.Add(new Vector3(Random.Range(0, Mathf.PI * 2), Random.Range(.5f, 1.1f), Random.Range(2.5f, 4.5f)));
            }
        }

        void Update()
        {
            if (world == null) { Destroy(gameObject); return; }
            float t = Time.time - started;
            bool scatter = t > Duration || target == null || !target.Alive;
            Vector3 center = target != null ? target.transform.position : transform.position;
            for (int i = 0; i < rats.Count; i++)
            {
                var rat = rats[i]; var o = offsets[i];
                Vector3 goal = scatter
                    ? rat.position + (rat.position - center).normalized * 6
                    : center + new Vector3(Mathf.Cos(o.x + t * o.z), 0, Mathf.Sin(o.x + t * o.z)) * o.y;
                goal.y = center.y + .1f;
                rat.position = Vector3.MoveTowards(rat.position, goal, (scatter ? 9 : 7) * Time.deltaTime);
                Vector3 heading = goal - rat.position; heading.y = 0;
                if (heading.sqrMagnitude > .001f) rat.rotation = Quaternion.LookRotation(heading) * Quaternion.Euler(90, 0, 0);
            }
            if (!scatter && t > .6f && Time.time >= nextBite)
            {
                nextBite = Time.time + .5f;
                if (target == world.PlayerEntity) world.DamagePlayer(3, "The rat tide");
                else target.TakeDamage(5, mouse, "physical");
            }
            if (t > Duration + 1.2f) Destroy(gameObject);
        }
    }

    // Imp trigger (dragon_delusion): convinced it is a dragon, it breathes a
    // long cone of fire. Fire ignites, melts visors, burns ropes and detonates.
    public sealed class DragonBreathEffect : MonoBehaviour
    {
        SandboxWorld world;
        SandboxMonster imp;
        ParticleSystem flames;
        float started, nextTick;
        public const float Duration = 2.6f, Range = 9.5f, HalfAngle = 26;

        public void Begin(SandboxWorld world, SandboxMonster imp)
        {
            this.world = world; this.imp = imp; started = Time.time;
            flames = world.Effects.Particles("Dragon breath", imp.transform, imp.Eye + imp.transform.forward * .4f, new Vector3(.25f, .25f, .1f),
                new Color(1, .9f, .35f), new Color(1, .3f, .02f), 140, 9, .7f, .8f, true);
            var shape = flames.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = HalfAngle * .8f; shape.radius = .1f;
            shape.rotation = Vector3.zero;
            flames.transform.rotation = Quaternion.LookRotation(imp.transform.forward + Vector3.down * .12f);
            var main = flames.main; main.gravityModifier = -.1f;
            var light = flames.gameObject.AddComponent<Light>(); light.type = LightType.Point; light.color = new Color(1, .55f, .2f); light.range = 9; light.intensity = 3;
        }

        void Update()
        {
            if (world == null || imp == null || !imp.Entity.Alive || Time.time - started > Duration)
            {
                if (flames != null) Destroy(flames.gameObject);
                Destroy(this); return;
            }
            imp.SetPose("fireball");
            if (flames != null)
            {
                flames.transform.position = imp.Eye + imp.transform.forward * .4f;
                flames.transform.rotation = Quaternion.LookRotation(imp.transform.forward + Vector3.down * .12f);
            }
            if (Time.time < nextTick) return;
            nextTick = Time.time + .25f;
            Vector3 origin = imp.Eye;
            foreach (var entity in new List<SandboxEntity>(world.Entities))
            {
                if (entity == null || entity == imp.Entity || !entity.Alive || entity.Owner != null || !entity.gameObject.activeInHierarchy) continue;
                Vector3 delta = entity.Center - origin;
                if (delta.magnitude > Range || !SandboxPerception.InCone(origin, imp.transform.forward, entity.Center, HalfAngle, true)) continue;
                if (entity == world.PlayerEntity) { world.DamagePlayer(5, "Dragon fire"); continue; }
                if (entity.Kind == "object" && !entity.Destructible && !entity.Flammable) continue;
                entity.TakeDamage(entity.Kind == "object" ? 14 : 8, imp.Entity, "fire");
                if (entity.Flammable || entity.Kind == "monster") world.Ignite(entity, imp.Entity);
            }
            world.MakeNoise(origin, 8);
        }
    }
}
