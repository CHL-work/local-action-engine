using System;
using UnityEngine;

namespace Brainwash
{
    public sealed class SandboxEntity : MonoBehaviour
    {
        public string Handle;
        public string Kind;
        public string Species;
        public string ItemKind;
        public string ObjectKind;
        public string ColorName;
        public string DisplayName;
        public string Generation = Guid.NewGuid().ToString("N");
        public SandboxEntity Owner;
        public float Health = 100f;
        public float MaximumHealth = 100f;
        public float Size = 1f;
        public bool Destructible;
        public bool Open;
        public bool Alive => Health > 0f;
        public Renderer Body;
        public TextMesh Label;
        public float LabelHeight;
        public Transform HandSocket;
        public Transform VisualRoot;
        // The colour a generated model was painted with (debris and effects use it).
        public Color? ModelTint;
        public SandboxEntity HeldItem { get; private set; }
        // Demo-only facts. Classic sandbox entities leave these at defaults.
        public SandboxWorld World;
        public SpeciesProfile Profile;
        public bool Flammable;
        public bool Explosive;
        public bool VisorIntact;
        public bool Asleep;
        public bool Grabbable;
        public float BurningUntil;
        public SandboxEntity BurnSource;
        public SandboxEntity LastAttacker;
        public string LastElement;
        public float LastDamageTime = -100;
        public string Note;
        internal Transform OpenVisual;
        internal Quaternion OpenVisualRotation = Quaternion.Euler(0, 90, 0);
        private Quaternion closedRotation;
        private bool rotationSaved;
        private Quaternion groundRotation;
        private Vector3 groundScale = Vector3.one;
        private bool groundTransformSaved;
        // Demo chain reactions can kill an entity inside its own damage call
        // (an exploding barrel or toad); death is processed exactly once.
        private bool deathHandled;

        public Vector3 Center => Body != null ? Body.bounds.center : transform.position + Vector3.up;
        public void SetOpen(bool open)
        {
            var movingPart = OpenVisual != null ? OpenVisual : transform;
            if (!rotationSaved) { closedRotation = movingPart.localRotation; rotationSaved = true; }
            if (Open != open)
                GameAudio.Play(ObjectKind == "lever" ? "lever_pull" : DisplayName != null && DisplayName.Contains("cage") ? "cage_open" : open ? "door_open" : "door_close", Center);
            Open = open;
            movingPart.localRotation = open ? closedRotation * OpenVisualRotation : closedRotation;
            if (ObjectKind == "door") SetPhysicalCollidersEnabled(!open);
        }
        public void TakeDamage(float damage) => TakeDamage(damage, null, "physical");
        // Attribution supports TDD §10 retaliation and persistent-effect credit.
        // Element weakness only applies inside demo scenarios; the classic
        // sandbox keeps its verified damage numbers.
        public void TakeDamage(float damage, SandboxEntity attacker, string element)
        {
            if (!Alive || (Kind == "object" && !Destructible) || damage <= 0) return;
            if (World != null) damage = World.AdjustDamage(this, damage, attacker, element);
            if (damage <= 0) return;
            Health = Mathf.Max(0, Health - damage);
            LastAttacker = attacker; LastElement = element; LastDamageTime = Time.time;
            World?.EntityDamaged(this, damage, attacker, element);
            if (!Alive && !deathHandled)
            {
                deathHandled = true;
                var block = new MaterialPropertyBlock();
                // Held objects belong to another entity and keep their own color.
                foreach (var renderer in GetComponentsInChildren<Renderer>())
                {
                    if (renderer.GetComponentInParent<SandboxEntity>() != this) continue;
                    Color original = renderer.sharedMaterial != null ? renderer.sharedMaterial.color : Color.gray;
                    Color dead = Color.Lerp(original, new Color(.16f, .17f, .19f), .72f);
                    block.SetColor("_BaseColor", dead); block.SetColor("_Color", dead);
                    renderer.SetPropertyBlock(block);
                }
                SetPhysicalCollidersEnabled(false);
                World?.EntityKilled(this, attacker, element);
            }
        }
        public void RestoreHealth(float maximum = 100f)
        {
            MaximumHealth = Health = maximum; deathHandled = false;
            foreach (var renderer in GetComponentsInChildren<Renderer>())
                if (renderer.GetComponentInParent<SandboxEntity>() == this) renderer.SetPropertyBlock(null);
            SetPhysicalCollidersEnabled(true);
        }
        // Gameplay colliders live on the entity root. Decorative primitive
        // colliders are disabled by SandboxVisuals and must never be re-enabled.
        public void SetPhysicalCollidersEnabled(bool enabled)
        {
            foreach (var collider in GetComponents<Collider>())
                collider.enabled = enabled && Alive && Owner == null && !(ObjectKind == "door" && Open);
        }
        public void Heal(float amount)
        {
            if (!Alive || amount <= 0) return;
            Health = Mathf.Min(MaximumHealth, Health + amount);
        }
        // Remains are revived by a nurse's revive skill (concept §12).
        public void Revive(float health)
        {
            if (Alive) return;
            Health = Mathf.Clamp(health, 1, MaximumHealth); deathHandled = false;
            foreach (var renderer in GetComponentsInChildren<Renderer>())
                if (renderer.GetComponentInParent<SandboxEntity>() == this) renderer.SetPropertyBlock(null);
            SetPhysicalCollidersEnabled(true);
        }
        public void Attach(SandboxEntity owner, Transform hand)
        {
            if (owner == null || hand == null) throw new ArgumentNullException(owner == null ? nameof(owner) : nameof(hand));
            if (Owner == null)
            { groundRotation = transform.rotation; groundScale = transform.localScale; groundTransformSaved = true; }
            else if (Owner.HeldItem == this) Owner.HeldItem = null;
            Owner = owner;
            owner.HeldItem = this;
            // Demo items may be free rigid bodies; holding makes them kinematic.
            this.Comp<PhysicsProp>()?.SetHeld(true);
            transform.SetParent(hand, false);
            // The key lies flat on the ground and is held upright, clear of the palm.
            transform.localPosition = ItemKind == "key" ? new Vector3(.04f, -.10f, .12f) : Vector3.zero;
            transform.localRotation = ItemKind == "key"
                ? Quaternion.AngleAxis(-90, Vector3.forward) * Quaternion.AngleAxis(90, Vector3.right)
                : Quaternion.identity;
            transform.localScale = groundScale;
            SetPhysicalCollidersEnabled(false);
            if (Label != null) Label.gameObject.SetActive(false);
        }
        public void Drop(Transform room, Vector3 position)
        {
            if (Owner != null && Owner.HeldItem == this) Owner.HeldItem = null;
            Owner = null;
            transform.SetParent(room, true);
            transform.position = position;
            if (groundTransformSaved) { transform.rotation = groundRotation; transform.localScale = groundScale; }
            SetPhysicalCollidersEnabled(true);
            this.Comp<PhysicsProp>()?.SetHeld(false);
            if (Label != null) Label.gameObject.SetActive(true);
        }
    }
}
