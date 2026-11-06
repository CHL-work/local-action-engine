using System;
using UnityEngine;

namespace Brainwash
{
    public static class SandboxPerception
    {
        public const float PlayerWidth = .7f;
        public const float NearRadius = 4 * PlayerWidth;
        public const float MonsterHalfAngle = 60f;
        public const float CastHalfAngle = 25f;
        public const float CastRange = 12f;

        public static bool InCone(Vector3 origin, Vector3 forward, Vector3 point, float halfAngle, bool planar = false)
        {
            var delta = point - origin;
            if (planar) { delta.y = 0; forward.y = 0; }
            if (delta.sqrMagnitude < .0001f) return true;
            return Vector3.Dot(forward.normalized, delta.normalized) >= Mathf.Cos(halfAngle * Mathf.Deg2Rad);
        }
        public static bool HasLineOfSight(Vector3 origin, SandboxEntity target, Transform source)
        {
            if (target == null) return false;
            var delta = target.Center - origin;
            var hits = Physics.RaycastAll(origin, delta.normalized, delta.magnitude + .03f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (source != null && (hit.transform == source || hit.transform.IsChildOf(source))) continue;
                return hit.transform == target.transform || hit.transform.IsChildOf(target.transform);
            }
            // A collider-free target can still be seen if the segment is unobstructed.
            return true;
        }
        public static bool Visible(SandboxMonster monster, SandboxEntity entity)
        {
            return entity != null && entity.Owner == null && InCone(monster.Eye, monster.transform.forward, entity.Center, MonsterHalfAngle, true)
                && HasLineOfSight(monster.Eye, entity, monster.transform);
        }
        public static bool Perceived(SandboxMonster monster, SandboxEntity entity)
        {
            if (entity == null || entity.Owner != null || entity == monster.Entity) return false;
            bool near = Vector3.ProjectOnPlane(entity.transform.position - monster.transform.position, Vector3.up).magnitude <= NearRadius;
            return (near || InCone(monster.Eye, monster.transform.forward, entity.Center, MonsterHalfAngle, true))
                && HasLineOfSight(monster.Eye, entity, monster.transform);
        }
    }
}
