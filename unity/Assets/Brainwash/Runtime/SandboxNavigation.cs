using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    // A small floor-grid planner for fixed arenas. CharacterController still
    // performs every movement; the planner never teleports through geometry.
    // Demo scenarios may resize the grid and mark holes/hazards as unwalkable.
    public static class SandboxNavigation
    {
        const float Cell = .7f;
        static float Edge = 10.5f;
        static Vector3 center = Vector3.zero;
        static int Side = 31;
        // When true (demo scenarios), cells need floor underneath and must avoid hazards.
        public static bool GroundChecks;

        public static void Configure(Vector3 middle, float halfExtent, bool groundChecks)
        {
            center = new Vector3(middle.x, 0, middle.z);
            Edge = Mathf.Max(3, halfExtent);
            Side = Mathf.Clamp(Mathf.RoundToInt(Edge * 2 / Cell) + 1, 5, 81);
            GroundChecks = groundChecks;
        }
        public static void ResetClassic() => Configure(Vector3.zero, 10.5f, false);
        public static Vector3 ClampToBounds(Vector3 point)
        {
            point.x = Mathf.Clamp(point.x, center.x - Edge, center.x + Edge);
            point.z = Mathf.Clamp(point.z, center.z - Edge, center.z + Edge);
            return point;
        }

        static bool Blocks(Collider collider, SandboxMonster monster)
        {
            if (collider.transform == monster.transform || collider.transform.IsChildOf(monster.transform)) return false;
            var entity = collider.GetComponentInParent<SandboxEntity>();
            return entity == null || (entity.Kind != "monster" && entity.Kind != "player" && entity.Kind != "item" && entity.Kind != "remains");
        }
        static void Capsule(SandboxMonster monster, Vector3 point, out Vector3 bottom, out Vector3 top, out float radius)
        {
            radius = monster.Motor.radius + .04f;
            float baseY = GroundChecks ? monster.transform.position.y : 0;
            bottom = new Vector3(point.x, baseY + radius + .07f, point.z);
            top = new Vector3(point.x, baseY + Mathf.Max(radius + .07f, monster.Motor.height - radius), point.z);
        }
        // Floor under the point, not a pit or a hazard volume.
        public static bool Walkable(Vector3 point, float referenceY)
        {
            if (!GroundChecks) return true;
            if (DemoPhysics.HazardAt(new Vector3(point.x, referenceY, point.z))) return false;
            foreach (var hit in Physics.RaycastAll(new Vector3(point.x, referenceY + .6f, point.z), Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                var entity = hit.collider.GetComponentInParent<SandboxEntity>();
                if (entity != null && (entity.Kind == "monster" || entity.Kind == "player" || entity.Kind == "item")) continue;
                if (hit.normal.y > .6f) return true;
            }
            return false;
        }
        public static bool Clear(SandboxMonster monster, Vector3 from, Vector3 to) => Clear(monster, from, to, true);
        public static bool Clear(SandboxMonster monster, Vector3 from, Vector3 to, bool checkGround)
        {
            Capsule(monster, from, out var bottom, out var top, out float radius);
            Vector3 delta = Vector3.ProjectOnPlane(to - from, Vector3.up);
            if (delta.sqrMagnitude < .0001f) return true;
            foreach (var hit in Physics.CapsuleCastAll(bottom, top, radius, delta.normalized, delta.magnitude, ~0, QueryTriggerInteraction.Ignore))
                if (Blocks(hit.collider, monster)) return false;
            if (checkGround && GroundChecks)
            {
                int samples = Mathf.CeilToInt(delta.magnitude / .45f);
                for (int i = 1; i <= samples; i++)
                    if (!Walkable(from + delta * (i / (float)samples), monster.transform.position.y)) return false;
            }
            return true;
        }
        static bool Open(SandboxMonster monster, Vector3 point)
        {
            Capsule(monster, point, out var bottom, out var top, out float radius);
            foreach (var collider in Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore))
                if (Blocks(collider, monster)) return false;
            return Walkable(point, monster.transform.position.y);
        }
        static Vector3 Point(int id) => new Vector3((id % Side) * Cell - Edge + center.x, 0, (id / Side) * Cell - Edge + center.z);
        // Demo levels: is any blocking collider within a capsule radius plus half a
        // diagonal step of this cell? If neither end of a step is crowded, the swept
        // capsule between them cannot hit anything, so the step needs no cast.
        static bool Crowded(SandboxMonster monster, int id, sbyte[] near)
        {
            if (near[id] == 0)
            {
                float radius = monster.Motor.radius + .04f, baseY = monster.transform.position.y;
                float top = Mathf.Max(radius * 2 + .07f, monster.Motor.height), reach = radius + .5f;
                Vector3 p = Point(id);
                var middle = new Vector3(p.x, baseY + .07f + (top - .07f) * .5f, p.z);
                bool crowded = false;
                foreach (var collider in Physics.OverlapBox(middle, new Vector3(reach, (top - .07f) * .5f, reach), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    if (Blocks(collider, monster)) { crowded = true; break; }
                near[id] = (sbyte)(crowded ? 1 : -1);
            }
            return near[id] > 0;
        }
        // Demo levels: is there any open cell within arrival range of the goal?
        static bool GoalReachable(SandboxMonster monster, Vector3 destination, float range, sbyte[] occupancy)
        {
            int goal = Index(destination), gx = goal % Side, gz = goal / Side, reach = Mathf.CeilToInt(range / Cell) + 1;
            for (int dz = -reach; dz <= reach; dz++) for (int dx = -reach; dx <= reach; dx++)
            {
                int x = gx + dx, z = gz + dz;
                if (x < 0 || x >= Side || z < 0 || z >= Side) continue;
                int id = z * Side + x;
                if (Vector3.ProjectOnPlane(Point(id) - destination, Vector3.up).magnitude > range) continue;
                if (occupancy[id] == 0) occupancy[id] = (sbyte)(Open(monster, Point(id)) ? 1 : -1);
                if (occupancy[id] > 0) return true;
            }
            return false;
        }
        static int Index(Vector3 p) => Mathf.Clamp(Mathf.RoundToInt((p.z - center.z + Edge) / Cell), 0, Side - 1) * Side + Mathf.Clamp(Mathf.RoundToInt((p.x - center.x + Edge) / Cell), 0, Side - 1);
        public static List<Vector3> FindPath(SandboxMonster monster, Vector3 destination, float arrivalRange)
        {
            var path = new List<Vector3>();
            int approximate = Index(monster.transform.position), start = -1, count = Side * Side;
            float closest = float.PositiveInfinity;
            int approximateX = approximate % Side, approximateZ = approximate / Side;
            for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
            {
                int x = approximateX + dx, z = approximateZ + dz;
                if (x < 0 || x >= Side || z < 0 || z >= Side) continue;
                int candidate = z * Side + x;
                float distance = Vector3.ProjectOnPlane(Point(candidate) - monster.transform.position, Vector3.up).sqrMagnitude;
                if (distance >= closest || !Open(monster, Point(candidate)) || !Clear(monster, monster.transform.position, Point(candidate), false)) continue;
                closest = distance; start = candidate;
            }
            if (start < 0) return path;
            var costs = new float[count]; var previous = new int[count]; var closed = new bool[count]; var occupancy = new sbyte[count];
            var near = GroundChecks ? new sbyte[count] : null;
            // A goal inside a wall, on a ledge or beyond the grid fails at once
            // instead of after a full search.
            if (GroundChecks && !GoalReachable(monster, destination, Mathf.Max(arrivalRange, .35f) + .01f, occupancy)) return path;
            for (int i = 0; i < count; i++) { costs[i] = float.PositiveInfinity; previous[i] = -1; }
            var open = new List<int> { start }; costs[start] = 0;
            int found = -1;
            // Demo levels are larger and often contain unreachable goals (a player on
            // a ledge); bound the search so one replan cannot stall a frame.
            int limit = GroundChecks ? System.Math.Min(count, 800) : count;
            for (int iteration = 0; open.Count > 0 && iteration < limit; iteration++)
            {
                int bestAt = 0; float bestScore = float.PositiveInfinity;
                for (int i = 0; i < open.Count; i++)
                { float score = costs[open[i]] + Vector3.Distance(Point(open[i]), destination); if (score < bestScore) { bestScore = score; bestAt = i; } }
                int current = open[bestAt]; open.RemoveAt(bestAt); if (closed[current]) continue;
                closed[current] = true;
                if (Vector3.ProjectOnPlane(Point(current) - destination, Vector3.up).magnitude <= Mathf.Max(arrivalRange, .35f) + .01f) { found = current; break; }
                int x = current % Side, z = current / Side;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dz == 0) || x + dx < 0 || x + dx >= Side || z + dz < 0 || z + dz >= Side) continue;
                    int next = (z + dz) * Side + x + dx;
                    if (closed[next]) continue;
                    if (occupancy[next] == 0) occupancy[next] = (sbyte)(Open(monster, Point(next)) ? 1 : -1);
                    if (occupancy[next] < 0) continue;
                    if ((near == null || Crowded(monster, current, near) || Crowded(monster, next, near)) && !Clear(monster, Point(current), Point(next), false)) continue;
                    float cost = costs[current] + (dx != 0 && dz != 0 ? Cell * 1.414214f : Cell);
                    if (cost >= costs[next]) continue;
                    costs[next] = cost; previous[next] = current; if (!open.Contains(next)) open.Add(next);
                }
            }
            if (found < 0) return path;
            for (int node = found; node != start && node >= 0; node = previous[node]) path.Add(Point(node));
            path.Reverse();
            if (Vector3.ProjectOnPlane(Point(start) - monster.transform.position, Vector3.up).magnitude > .12f) path.Insert(0, Point(start));
            return path;
        }
    }
}
