using System.Collections.Generic;
using UnityEngine;

namespace Brainwash
{
    public sealed class SandboxMonster : MonoBehaviour
    {
        public SandboxEntity Entity { get; private set; }
        public CharacterController Motor { get; private set; }
        public Transform Hand { get; private set; }
        public Vector3 Eye => transform.position + Vector3.up * (Motor != null ? Motor.height * .83f : 1.5f);
        public string Pose { get; private set; } = "idle";
        public float FireballReadyAt;
        public bool NavigationEnabled;
        public SandboxAI AI { get; private set; }
        // Demo scenarios use DemoAI instead of the classic arena AI.
        public DemoAI Brain;
        public bool Flying;
        public float HoverHeight = 2.4f;
        // Commanded movement may walk straight into a hazard ("jump into the pit").
        public bool Reckless;
        // Set when the controller lands faster than 9 m/s; consumed by the demo world.
        public float LandingSpeed;
        private float verticalSpeed;
        private List<Vector3> path;
        private Vector3 plannedDestination;
        private float planAfter;
        public void Initialize(SandboxEntity entity, float width = .7f, float height = 1.8f)
        {
            Entity = entity;
            Motor = TryGetComponent(out CharacterController existing) ? existing : gameObject.AddComponent<CharacterController>();
            Motor.height = height; Motor.radius = width * .48f; Motor.center = Vector3.up * height * .5f;
            Motor.stepOffset = .25f; Motor.slopeLimit = 45f; Motor.skinWidth = .035f;
            var hand = new GameObject("Held item socket"); hand.transform.SetParent(transform, false);
            hand.transform.localPosition = new Vector3(width * .65f, height * .55f, width * .55f); Hand = hand.transform;
            Entity.HandSocket = Hand;
            AI = new SandboxAI(this);
        }
        // One-shot actions for animation and sound: strike, bite, cast, throw,
        // slam, heal, swell. Gameplay calls Cue at the moment the action happens.
        public static event System.Action<SandboxMonster, string> Cued;
        public string LastCue { get; private set; }
        public float LastCueTime { get; private set; } = -100;
        public void Cue(string action)
        {
            LastCue = action; LastCueTime = Time.time;
            Cued?.Invoke(this, action);
        }
        public void SetPose(string pose)
        {
            Pose = pose;
            if (Entity != null && Entity.Body != null && pose != "dance" && pose != "wave" && pose != "bow") Entity.Body.transform.localRotation = Quaternion.identity;
        }
        public void TurnAround() { transform.Rotate(0, 180, 0); }
        public void Teleport(Vector3 position, Vector3 forward)
        {
            if (Motor != null) Motor.enabled = false;
            transform.position = position;
            if (forward.sqrMagnitude > .01f) transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up));
            if (Motor != null) Motor.enabled = true;
            verticalSpeed = 0; Grounded = true;
            path = null; planAfter = 0;
        }
        // Ground contact as of the last gravity step, which always pushes down.
        // CharacterController.isGrounded only describes the latest Move call, and a
        // walking or fleeing step earlier in the frame is horizontal: read alone,
        // it let a moving monster build up falling speed on flat ground and take
        // fall damage when it stopped.
        public bool Grounded { get; private set; } = true;
        public void Jump() { if (Grounded) { verticalSpeed = 5.5f; Grounded = false; } }
        public void Gravity(float dt)
        {
            if (Motor == null || !Motor.enabled) return;
            if (Flying && Entity.Alive) { verticalSpeed = 0; Grounded = false; return; }
            verticalSpeed = Mathf.Max(-20f, verticalSpeed - 18f * dt);
            float falling = -verticalSpeed;
            Grounded = (Motor.Move(Vector3.up * verticalSpeed * dt) & CollisionFlags.Below) != 0;
            if (!Grounded) return;
            // Report a hard landing once so demo scenarios can apply fall damage.
            if (falling > 9f) LandingSpeed = falling;
            verticalSpeed = -2f;
        }
        public bool MoveToward(Vector3 target, float speed, float dt, float range, out float distanceMoved, bool away = false)
        {
            Vector3 delta = Vector3.ProjectOnPlane(target - transform.position, Vector3.up);
            float distance = delta.magnitude;
            distanceMoved = 0;
            if (!away && distance <= range) return true;
            if (distance < .001f || Motor == null || !Motor.enabled || !Entity.Alive) return false;
            Vector3 direction = delta / distance * (away ? -1 : 1);
            float movementDistance = Mathf.Min(speed * dt, Mathf.Max(0, distance - (away ? 0 : range)));
            if (NavigationEnabled)
            {
                Vector3 destination = away ? transform.position + direction * 3 : target;
                Vector3 stopPoint = away ? destination : target - direction * range;
                if (!SandboxNavigation.Clear(this, transform.position, stopPoint, !Reckless))
                {
                    // Direction commands intentionally point far beyond this room.
                    // Plan toward its walkable edge, retaining the real target
                    // for command completion and the shared control deadline.
                    destination = SandboxNavigation.ClampToBounds(destination);
                    if (path == null || Time.time >= planAfter || ((plannedDestination - destination).sqrMagnitude > 1 && (!SandboxNavigation.GroundChecks || Time.time >= planAfter - .6f)))
                    {
                        path = SandboxNavigation.FindPath(this, destination, away ? .4f : range); plannedDestination = destination;
                        // Demo levels: an unreachable goal is retried less often.
                        planAfter = Time.time + (path.Count == 0 && SandboxNavigation.GroundChecks ? 1.6f : 1);
                    }
                    while (path.Count > 0 && Vector3.ProjectOnPlane(path[0] - transform.position, Vector3.up).magnitude < .18f) path.RemoveAt(0);
                    if (path.Count == 0) return false;
                    Vector3 segment = Vector3.ProjectOnPlane(path[0] - transform.position, Vector3.up);
                    direction = segment.normalized; movementDistance = Mathf.Min(speed * dt, segment.magnitude);
                }
                else path = null;
            }
            Vector3 before = transform.position;
            Motor.Move(direction * movementDistance);
            distanceMoved = Vector3.ProjectOnPlane(transform.position - before, Vector3.up).magnitude;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), 360 * dt);
            Pose = "moving";
            return !away && Vector3.ProjectOnPlane(target - transform.position, Vector3.up).magnitude <= range + .025f;
        }
        // Free 3D movement for flyers (Harpy). The controller still collides with walls.
        public bool FlyToward(Vector3 target, float speed, float dt, float range, out float distanceMoved, bool keepAltitude = true)
        {
            distanceMoved = 0;
            if (Motor == null || !Motor.enabled || !Entity.Alive) return false;
            Vector3 goal = target;
            if (keepAltitude) goal.y = Mathf.Max(goal.y, GroundHeight(goal, transform) + .2f);
            Vector3 delta = goal - transform.position;
            if (delta.magnitude <= range) return true;
            Vector3 step = Vector3.ClampMagnitude(delta, speed * dt);
            Vector3 before = transform.position;
            Motor.Move(step);
            distanceMoved = (transform.position - before).magnitude;
            Vector3 flat = Vector3.ProjectOnPlane(delta, Vector3.up);
            if (flat.sqrMagnitude > .001f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flat), 360 * dt);
            Pose = "flying";
            return (goal - transform.position).magnitude <= range + .05f;
        }
        public static float GroundHeight(Vector3 point, Transform ignore)
        {
            float best = float.NegativeInfinity;
            foreach (var hit in Physics.RaycastAll(point + Vector3.up * 3, Vector3.down, 14, ~0, QueryTriggerInteraction.Ignore))
            {
                if (ignore != null && hit.transform.IsChildOf(ignore)) continue;
                if (hit.collider.GetComponentInParent<SandboxMonster>() != null) continue;
                if (hit.point.y > best) best = hit.point.y;
            }
            return float.IsNegativeInfinity(best) ? 0 : best;
        }
    }
}
