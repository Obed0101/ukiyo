using System.Numerics;

namespace Ukiyo.Physics;

public enum BodyType : byte
{
    /// <summary>Never moves; infinite mass (ground, walls).</summary>
    Static = 0,

    /// <summary>Moved by its velocity only; pushes dynamic bodies but is not pushed (platforms, doors).</summary>
    Kinematic = 1,

    /// <summary>Moved by forces, gravity and contacts.</summary>
    Dynamic = 2,
}

public abstract record Shape2D
{
    public abstract float Area { get; }

    /// <summary>Moment of inertia per unit mass around the center.</summary>
    public abstract float InertiaPerMass { get; }
}

public sealed record CircleShape(float Radius) : Shape2D
{
    public override float Area => MathF.PI * Radius * Radius;

    public override float InertiaPerMass => 0.5f * Radius * Radius;
}

/// <summary>Oriented box centered on the body, in meters.</summary>
public sealed record BoxShape(Vector2 HalfExtents) : Shape2D
{
    public static BoxShape FromSize(float width, float height) => new(new Vector2(width * 0.5f, height * 0.5f));

    public override float Area => 4f * HalfExtents.X * HalfExtents.Y;

    public override float InertiaPerMass => (HalfExtents.X * HalfExtents.X + HalfExtents.Y * HalfExtents.Y) / 3f;
}

/// <summary>
/// A rigid body on the XY plane (+Y up, meters, radians counter-clockwise). Create through
/// <see cref="PhysicsWorld2D.Add"/>; the world owns ordering and ids, which keeps the simulation deterministic.
/// </summary>
public sealed class Body2D
{
    private float _density = 1f;
    private bool _fixedRotation;

    internal Body2D(int id, string name, BodyType type, Shape2D shape)
    {
        // "not > 0" also rejects NaN, which "<= 0" would let through.
        if (shape is CircleShape { Radius: not > 0 } || shape is BoxShape { HalfExtents.X: not > 0 } || shape is BoxShape { HalfExtents.Y: not > 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(shape), shape, "[PHYSICS]: shape dimensions must be positive");
        }

        Id = id;
        Name = name;
        Type = type;
        Shape = shape;
        UpdateMass();
    }

    /// <summary>Creation order inside its world. Solvers iterate in this order.</summary>
    public int Id { get; }

    public string Name { get; }

    public BodyType Type { get; }

    public Shape2D Shape { get; }

    public Vector2 Position { get; set; }

    public float Angle { get; set; }

    public Vector2 Velocity { get; set; }

    public float AngularVelocity { get; set; }

    public float Friction { get; set; } = 0.5f;

    /// <summary>Bounciness 0..1.</summary>
    public float Restitution { get; set; }

    public float LinearDamping { get; set; }

    public float AngularDamping { get; set; }

    public float GravityScale { get; set; } = 1f;

    /// <summary>Kilograms per square meter; mass follows the shape area.</summary>
    public float Density
    {
        get => _density;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "[PHYSICS]: density must be positive");
            }

            _density = value;
            UpdateMass();
        }
    }

    /// <summary>Keeps the body upright (characters). Contacts never rotate it.</summary>
    public bool FixedRotation
    {
        get => _fixedRotation;
        set
        {
            _fixedRotation = value;
            UpdateMass();
        }
    }

    /// <summary>Detects overlaps (<see cref="PhysicsWorld2D.TriggerEvents"/>) without any collision response.</summary>
    public bool IsSensor { get; set; }

    /// <summary>Category bits of this body.</summary>
    public uint Layer { get; set; } = 1;

    /// <summary>Categories this body collides with. Both bodies must accept each other.</summary>
    public uint Mask { get; set; } = uint.MaxValue;

    public bool Enabled { get; set; } = true;

    /// <summary>Anything the game wants to find from a contact (an enemy, a coin id).</summary>
    public object? UserData { get; set; }

    public float Mass { get; private set; }

    public float InverseMass { get; private set; }

    public float InverseInertia { get; private set; }

    /// <summary>Touched ground (a contact whose normal points against gravity) during the last step.</summary>
    public bool IsGrounded { get; internal set; }

    /// <summary>Normal of the ground contact that set <see cref="IsGrounded"/>, pointing away from the ground.</summary>
    public Vector2 GroundNormal { get; internal set; }

    internal Vector2 Force { get; set; }

    internal float Torque { get; set; }

    public void ApplyForce(Vector2 force) => Force += force;

    public void ApplyTorque(float torque) => Torque += torque;

    /// <summary>Instant change of momentum, optionally at a world point (which also spins the body).</summary>
    public void ApplyImpulse(Vector2 impulse, Vector2? worldPoint = null)
    {
        if (Type != BodyType.Dynamic)
        {
            return;
        }

        Velocity += impulse * InverseMass;
        if (worldPoint is { } point)
        {
            AngularVelocity += InverseInertia * DetMath.Cross(point - Position, impulse);
        }
    }

    /// <summary>Pose for snapshots and rendering: position on XY, rotation around +Z.</summary>
    public EntityPose Pose => EntityPose.Planar(Position, Angle);

    public override string ToString() => $"{Name}#{Id} {Type} p=({Position.X:0.###},{Position.Y:0.###}) v=({Velocity.X:0.###},{Velocity.Y:0.###})";

    private void UpdateMass()
    {
        if (Type != BodyType.Dynamic)
        {
            Mass = 0;
            InverseMass = 0;
            InverseInertia = 0;
            return;
        }

        Mass = _density * Shape.Area;
        InverseMass = 1f / Mass;
        InverseInertia = _fixedRotation ? 0 : 1f / (Mass * Shape.InertiaPerMass);
    }
}
