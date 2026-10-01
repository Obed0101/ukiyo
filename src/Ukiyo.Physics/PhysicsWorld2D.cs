using System.Numerics;

namespace Ukiyo.Physics;

/// <summary>A contact from the last step, for gameplay (damage on hit, landing sounds). Normal points from A to B.</summary>
public readonly record struct Contact2D(Body2D BodyA, Body2D BodyB, Vector2 Normal, Vector2 Point, float Penetration);

/// <summary>A sensor started (<see cref="Entered"/>) or stopped overlapping another body during the last step.</summary>
public readonly record struct TriggerEvent(Body2D Sensor, Body2D Other, bool Entered);

public readonly record struct RaycastHit(Body2D Body, Vector2 Point, Vector2 Normal, float Distance);

/// <summary>
/// Deterministic 2D rigid-body world: fixed step, sort-and-sweep broad phase, sequential impulses with friction and
/// restitution, positional correction, sensors with enter/exit events, raycasts and overlap queries. Bodies are always
/// processed in creation order and trigonometry goes through <see cref="DetMath"/>, so the same inputs give the same
/// bits on every target. Step it from <c>IGame.Update</c> with <see cref="Simulation.TickSecondsF"/>.
/// </summary>
public sealed class PhysicsWorld2D
{
    private const float LinearSlop = 0.005f;
    private const float Baumgarte = 0.4f;
    private const float RestitutionThreshold = 1f;
    private const float GroundCosine = 0.7f;

    private readonly List<Body2D> _bodies = [];
    private readonly List<Contact2D> _contacts = [];
    private readonly List<TriggerEvent> _triggerEvents = [];
    private readonly HashSet<long> _sensorPairs = [];
    private readonly List<SolverContact> _solver = [];
    private int _nextId;

    public Vector2 Gravity { get; set; } = new(0, -9.81f);

    public int VelocityIterations { get; set; } = 8;

    public IReadOnlyList<Body2D> Bodies => _bodies;

    /// <summary>Solid contacts found in the last step (sensors excluded).</summary>
    public IReadOnlyList<Contact2D> Contacts => _contacts;

    /// <summary>Sensor enter/exit events from the last step, in deterministic order.</summary>
    public IReadOnlyList<TriggerEvent> TriggerEvents => _triggerEvents;

    public long Steps { get; private set; }

    public Body2D Add(string name, BodyType type, Shape2D shape, Vector2 position, float angle = 0f)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = new Body2D(_nextId++, name, type, shape) { Position = position, Angle = angle };
        _bodies.Add(body);
        return body;
    }

    public Body2D AddBox(string name, BodyType type, Vector2 position, Vector2 size, float angle = 0f) =>
        Add(name, type, BoxShape.FromSize(size.X, size.Y), position, angle);

    public Body2D AddCircle(string name, BodyType type, Vector2 position, float radius) =>
        Add(name, type, new CircleShape(radius), position);

    public bool Remove(Body2D body)
    {
        if (!_bodies.Remove(body))
        {
            return false;
        }

        _sensorPairs.RemoveWhere(key => (int)(key >> 32) == body.Id || (int)(key & 0xFFFFFFFF) == body.Id);
        return true;
    }

    public Body2D? Find(string name) => _bodies.Find(b => b.Name == name);

    /// <summary>Advances the world by <paramref name="dt"/> seconds (use the fixed tick).</summary>
    public void Step(float dt)
    {
        if (!(dt > 0) || !float.IsFinite(dt))
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "[PHYSICS]: dt must be positive and finite");
        }

        _contacts.Clear();
        _triggerEvents.Clear();
        _solver.Clear();
        foreach (var body in _bodies)
        {
            body.IsGrounded = false;
            body.GroundNormal = Vector2.Zero;
            if (body.Type != BodyType.Dynamic || !body.Enabled)
            {
                continue;
            }

            var acceleration = Gravity * body.GravityScale + body.Force * body.InverseMass;
            body.Velocity = (body.Velocity + acceleration * dt) / (1f + dt * body.LinearDamping);
            body.AngularVelocity = (body.AngularVelocity + body.Torque * body.InverseInertia * dt) / (1f + dt * body.AngularDamping);
        }

        var shapes = new WorldShape[_bodies.Count];
        for (var i = 0; i < _bodies.Count; i++)
        {
            shapes[i] = new WorldShape(_bodies[i]);
        }

        var currentSensorPairs = new HashSet<long>();
        foreach (var (i, j) in BroadPhase(shapes))
        {
            var a = _bodies[i];
            var b = _bodies[j];
            if (!Collision2D.Collide(shapes[i], shapes[j], out var manifold))
            {
                continue;
            }

            if (a.IsSensor || b.IsSensor)
            {
                currentSensorPairs.Add(PairKey(a, b));
                continue;
            }

            for (var k = 0; k < manifold.Count; k++)
            {
                _contacts.Add(new Contact2D(a, b, manifold.Normal, manifold.Point(k), manifold.Penetration(k)));
                _solver.Add(SolverContact.Create(a, b, manifold.Normal, manifold.Point(k), manifold.Penetration(k)));
            }

            MarkGround(a, b, manifold.Normal);
        }

        UpdateTriggers(currentSensorPairs);

        for (var iteration = 0; iteration < VelocityIterations; iteration++)
        {
            for (var c = 0; c < _solver.Count; c++)
            {
                var contact = _solver[c];
                contact.SolveVelocity();
                _solver[c] = contact;
            }
        }

        foreach (var body in _bodies)
        {
            if (body.Type == BodyType.Static || !body.Enabled)
            {
                continue;
            }

            body.Position += body.Velocity * dt;
            body.Angle += body.AngularVelocity * dt;
            body.Force = Vector2.Zero;
            body.Torque = 0;
        }

        foreach (var contact in _solver)
        {
            contact.CorrectPosition();
        }

        Steps++;
    }

    /// <summary>Closest body hit by a ray. <paramref name="direction"/> need not be normalized.</summary>
    public RaycastHit? Raycast(Vector2 origin, Vector2 direction, float maxDistance, uint mask = uint.MaxValue, bool includeSensors = false)
    {
        if (direction.LengthSquared() == 0 || !(maxDistance > 0))
        {
            return null;
        }

        var dir = Vector2.Normalize(direction);
        RaycastHit? best = null;
        foreach (var body in _bodies)
        {
            if (!body.Enabled || (body.Layer & mask) == 0 || (body.IsSensor && !includeSensors))
            {
                continue;
            }

            var shape = new WorldShape(body);
            if (RayShape(shape, origin, dir, out var distance, out var normal) && distance <= maxDistance && (best is null || distance < best.Value.Distance))
            {
                best = new RaycastHit(body, origin + dir * distance, normal, distance);
            }
        }

        return best;
    }

    /// <summary>Bodies whose shape contains <paramref name="point"/>, in creation order.</summary>
    public List<Body2D> OverlapPoint(Vector2 point, uint mask = uint.MaxValue)
    {
        var probe = new WorldShape(new Body2D(-1, "probe", BodyType.Static, new CircleShape(1e-4f)) { Position = point });
        return Overlapping(probe, mask);
    }

    /// <summary>Bodies overlapping an axis-aligned box, in creation order.</summary>
    public List<Body2D> OverlapBox(Vector2 center, Vector2 size, uint mask = uint.MaxValue)
    {
        var probe = new WorldShape(new Body2D(-1, "probe", BodyType.Static, BoxShape.FromSize(size.X, size.Y)) { Position = center });
        return Overlapping(probe, mask);
    }

    /// <summary>Poses of every body by name, ready for <c>GameSnapshot</c>.</summary>
    public Dictionary<string, EntityPose> Poses()
    {
        var poses = new Dictionary<string, EntityPose>(StringComparer.Ordinal);
        foreach (var body in _bodies)
        {
            poses[body.Name] = body.Pose;
        }

        return poses;
    }

    private List<Body2D> Overlapping(WorldShape probe, uint mask)
    {
        var result = new List<Body2D>();
        foreach (var body in _bodies)
        {
            if (body.Enabled && (body.Layer & mask) != 0 && Collision2D.Collide(probe, new WorldShape(body), out _))
            {
                result.Add(body);
            }
        }

        return result;
    }

    /// <summary>Sort-and-sweep on X. Pairs come out ordered by (lower id, higher id) so the solver order is stable.</summary>
    private List<(int, int)> BroadPhase(WorldShape[] shapes)
    {
        var count = _bodies.Count;
        var order = new int[count];
        var bounds = new (Vector2 Min, Vector2 Max)[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
            bounds[i] = shapes[i].Bounds();
        }

        Array.Sort(order, (x, y) =>
        {
            var byMin = bounds[x].Min.X.CompareTo(bounds[y].Min.X);
            return byMin != 0 ? byMin : x.CompareTo(y);
        });

        var pairs = new List<(int, int)>();
        for (var oi = 0; oi < count; oi++)
        {
            var i = order[oi];
            for (var oj = oi + 1; oj < count; oj++)
            {
                var j = order[oj];
                if (bounds[j].Min.X > bounds[i].Max.X)
                {
                    break;
                }

                if (bounds[j].Min.Y > bounds[i].Max.Y || bounds[i].Min.Y > bounds[j].Max.Y)
                {
                    continue;
                }

                var (lo, hi) = i < j ? (i, j) : (j, i);
                if (ShouldCollide(_bodies[lo], _bodies[hi]))
                {
                    pairs.Add((lo, hi));
                }
            }
        }

        pairs.Sort((p, q) => p.Item1 != q.Item1 ? p.Item1.CompareTo(q.Item1) : p.Item2.CompareTo(q.Item2));
        return pairs;
    }

    private static bool ShouldCollide(Body2D a, Body2D b)
    {
        if (!a.Enabled || !b.Enabled || (a.Layer & b.Mask) == 0 || (b.Layer & a.Mask) == 0)
        {
            return false;
        }

        if (a.IsSensor || b.IsSensor)
        {
            // Sensors detect anything that can move into them; two static bodies never start or stop overlapping.
            return !(a.IsSensor && b.IsSensor) && (a.Type != BodyType.Static || b.Type != BodyType.Static);
        }

        return a.Type == BodyType.Dynamic || b.Type == BodyType.Dynamic;
    }

    private void MarkGround(Body2D a, Body2D b, Vector2 normal)
    {
        var up = Gravity.LengthSquared() > 0 ? -Vector2.Normalize(Gravity) : Vector2.UnitY;
        if (a.Type == BodyType.Dynamic && Vector2.Dot(-normal, up) > GroundCosine)
        {
            a.IsGrounded = true;
            a.GroundNormal = -normal;
        }

        if (b.Type == BodyType.Dynamic && Vector2.Dot(normal, up) > GroundCosine)
        {
            b.IsGrounded = true;
            b.GroundNormal = normal;
        }
    }

    private void UpdateTriggers(HashSet<long> current)
    {
        var entered = current.Where(key => !_sensorPairs.Contains(key)).Order().ToArray();
        var exited = _sensorPairs.Where(key => !current.Contains(key)).Order().ToArray();
        foreach (var key in entered)
        {
            AddTriggerEvent(key, entered: true);
        }

        foreach (var key in exited)
        {
            AddTriggerEvent(key, entered: false);
        }

        _sensorPairs.Clear();
        _sensorPairs.UnionWith(current);
    }

    private void AddTriggerEvent(long key, bool entered)
    {
        var a = _bodies.Find(body => body.Id == (int)(key >> 32));
        var b = _bodies.Find(body => body.Id == (int)(key & 0xFFFFFFFF));
        if (a is null || b is null)
        {
            return;
        }

        var (sensor, other) = a.IsSensor ? (a, b) : (b, a);
        _triggerEvents.Add(new TriggerEvent(sensor, other, entered));
    }

    private static long PairKey(Body2D a, Body2D b)
    {
        var (lo, hi) = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
        return ((long)lo << 32) | (uint)hi;
    }

    private static bool RayShape(in WorldShape shape, Vector2 origin, Vector2 dir, out float distance, out Vector2 normal)
    {
        distance = 0;
        normal = Vector2.Zero;
        if (shape.IsCircle)
        {
            var m = origin - shape.Center;
            var b = Vector2.Dot(m, dir);
            var c = m.LengthSquared() - shape.Radius * shape.Radius;
            if (c > 0 && b > 0)
            {
                return false;
            }

            var discriminant = b * b - c;
            if (discriminant < 0)
            {
                return false;
            }

            distance = MathF.Max(0, -b - MathF.Sqrt(discriminant));
            var hit = origin + dir * distance;
            normal = hit == shape.Center ? -dir : Vector2.Normalize(hit - shape.Center);
            return true;
        }

        // Slab test in the box frame.
        var localOrigin = DetMath.RotateInverse(origin - shape.Center, shape.Sin, shape.Cos);
        var localDir = DetMath.RotateInverse(dir, shape.Sin, shape.Cos);
        var tMin = 0f;
        var tMax = float.MaxValue;
        var localNormal = Vector2.Zero;
        for (var axis = 0; axis < 2; axis++)
        {
            var o = axis == 0 ? localOrigin.X : localOrigin.Y;
            var d = axis == 0 ? localDir.X : localDir.Y;
            var h = axis == 0 ? shape.HalfExtents.X : shape.HalfExtents.Y;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < -h || o > h)
                {
                    return false;
                }

                continue;
            }

            var t1 = (-h - o) / d;
            var t2 = (h - o) / d;
            var sign = -1f;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
                sign = 1f;
            }

            if (t1 > tMin)
            {
                tMin = t1;
                localNormal = axis == 0 ? new Vector2(sign, 0) : new Vector2(0, sign);
            }

            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        distance = tMin;
        normal = localNormal == Vector2.Zero ? -dir : DetMath.Rotate(localNormal, shape.Sin, shape.Cos);
        return true;
    }

    /// <summary>One contact point prepared for the sequential-impulse solver.</summary>
    private struct SolverContact
    {
        private Body2D _a;
        private Body2D _b;
        private Vector2 _normal;
        private Vector2 _rA;
        private Vector2 _rB;
        private float _normalMass;
        private float _tangentMass;
        private float _bounce;
        private float _friction;
        private float _penetration;
        private float _normalImpulse;
        private float _tangentImpulse;

        public static SolverContact Create(Body2D a, Body2D b, Vector2 normal, Vector2 point, float penetration)
        {
            var rA = point - a.Position;
            var rB = point - b.Position;
            var tangent = new Vector2(-normal.Y, normal.X);
            var rnA = DetMath.Cross(rA, normal);
            var rnB = DetMath.Cross(rB, normal);
            var rtA = DetMath.Cross(rA, tangent);
            var rtB = DetMath.Cross(rB, tangent);
            var kNormal = a.InverseMass + b.InverseMass + a.InverseInertia * rnA * rnA + b.InverseInertia * rnB * rnB;
            var kTangent = a.InverseMass + b.InverseMass + a.InverseInertia * rtA * rtA + b.InverseInertia * rtB * rtB;
            var relative = Vector2.Dot(RelativeVelocity(a, b, rA, rB), normal);
            return new SolverContact
            {
                _a = a,
                _b = b,
                _normal = normal,
                _rA = rA,
                _rB = rB,
                _normalMass = kNormal > 0 ? 1f / kNormal : 0,
                _tangentMass = kTangent > 0 ? 1f / kTangent : 0,
                _bounce = relative < -RestitutionThreshold ? -MathF.Max(a.Restitution, b.Restitution) * relative : 0,
                _friction = MathF.Sqrt(a.Friction * b.Friction),
                _penetration = penetration,
            };
        }

        public void SolveVelocity()
        {
            var tangent = new Vector2(-_normal.Y, _normal.X);
            var dv = RelativeVelocity(_a, _b, _rA, _rB);
            var tangentLambda = -Vector2.Dot(dv, tangent) * _tangentMass;
            var maxFriction = _friction * _normalImpulse;
            var newTangent = Math.Clamp(_tangentImpulse + tangentLambda, -maxFriction, maxFriction);
            Apply(tangent * (newTangent - _tangentImpulse));
            _tangentImpulse = newTangent;

            dv = RelativeVelocity(_a, _b, _rA, _rB);
            var normalLambda = (-Vector2.Dot(dv, _normal) + _bounce) * _normalMass;
            var newNormal = MathF.Max(_normalImpulse + normalLambda, 0);
            Apply(_normal * (newNormal - _normalImpulse));
            _normalImpulse = newNormal;
        }

        /// <summary>Pushes bodies apart along the normal by a fraction of the penetration beyond the slop.</summary>
        public readonly void CorrectPosition()
        {
            var inverseMass = _a.InverseMass + _b.InverseMass;
            if (inverseMass <= 0)
            {
                return;
            }

            var correction = _normal * (MathF.Max(_penetration - LinearSlop, 0) * Baumgarte / inverseMass);
            if (_a.Type == BodyType.Dynamic)
            {
                _a.Position -= correction * _a.InverseMass;
            }

            if (_b.Type == BodyType.Dynamic)
            {
                _b.Position += correction * _b.InverseMass;
            }
        }

        private readonly void Apply(Vector2 impulse)
        {
            if (_a.Type == BodyType.Dynamic)
            {
                _a.Velocity -= impulse * _a.InverseMass;
                _a.AngularVelocity -= _a.InverseInertia * DetMath.Cross(_rA, impulse);
            }

            if (_b.Type == BodyType.Dynamic)
            {
                _b.Velocity += impulse * _b.InverseMass;
                _b.AngularVelocity += _b.InverseInertia * DetMath.Cross(_rB, impulse);
            }
        }

        private static Vector2 RelativeVelocity(Body2D a, Body2D b, Vector2 rA, Vector2 rB) =>
            b.Velocity + DetMath.Cross(b.AngularVelocity, rB) - a.Velocity - DetMath.Cross(a.AngularVelocity, rA);
    }
}
