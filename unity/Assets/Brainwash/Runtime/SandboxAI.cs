using UnityEngine;

namespace Brainwash
{
    // Normal-state behavior is separate from the command executor. The world
    // never ticks this AI while its actor owns a listening/control lease.
    public sealed class SandboxAI
    {
        readonly SandboxMonster monster;
        readonly Vector3 home;
        Vector3 destination, lastSeen;
        float alertUntil, nextAttack, nextPatrol;
        int patrolIndex;
        public string State { get; private set; } = "Patrol";
        public SandboxAI(SandboxMonster monster) { this.monster = monster; home = monster.transform.position; destination = home; }
        public void Alert(Vector3 position) { lastSeen = position; alertUntil = Time.time + 6; }
        public void ResumeAfterControl() { nextAttack = Time.time + .8f; }
        public void Tick(SandboxWorld world, float dt)
        {
            if (!monster.Entity.Alive || world.GameOver || world.Victory || world.IsControlled(monster)) return;
            float distance = Vector3.ProjectOnPlane(world.Player.position - monster.transform.position, Vector3.up).magnitude;
            bool sight = distance < 8 && SandboxPerception.InCone(monster.Eye, monster.transform.forward, world.PlayerEntity.Center, 60, true)
                && SandboxPerception.HasLineOfSight(monster.Eye, world.PlayerEntity, monster.transform);
            if (sight) Alert(world.Player.position);
            bool golem = monster.Entity.Species == "ice_golem", goblin = monster.Entity.Species == "goblin";
            float speed = golem ? 1.4f : goblin ? 2.4f : 2;
            if (Time.time < alertUntil)
            {
                State = "Chasing";
                float reach = monster.Motor.radius + .95f;
                if (sight && distance <= reach)
                {
                    State = "Attacking";
                    Vector3 toward = Vector3.ProjectOnPlane(world.Player.position - monster.transform.position, Vector3.up);
                    if (toward.sqrMagnitude > .001f) monster.transform.rotation = Quaternion.LookRotation(toward);
                    monster.SetPose("attacking");
                    if (Time.time >= nextAttack)
                    {
                        nextAttack = Time.time + (golem ? 2.4f : goblin ? 1.5f : 1.8f);
                        monster.Cue("strike");
                        world.DamagePlayer(golem ? 14 : goblin ? 8 : 6, monster.Entity.DisplayName);
                    }
                }
                else monster.MoveToward(lastSeen, speed, dt, sight ? reach : .4f, out _);
            }
            else
            {
                State = "Patrol";
                if (Time.time >= nextPatrol || Vector3.Distance(monster.transform.position, destination) < .4f)
                {
                    nextPatrol = Time.time + 7;
                    Vector3[] offsets = { Vector3.right, Vector3.forward, Vector3.left, Vector3.back };
                    destination = home + offsets[patrolIndex++ % offsets.Length] * 1.6f;
                    destination.x = Mathf.Clamp(destination.x, -9, 9); destination.z = Mathf.Clamp(destination.z, -9, 9);
                }
                monster.MoveToward(destination, speed * .45f, dt, .3f, out _);
            }
        }
    }
}
