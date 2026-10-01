using System.Numerics;

namespace Ukiyo.Physics;

/// <summary>Up to two contact points between A and B. <see cref="Normal"/> points from A to B.</summary>
internal struct Manifold
{
    public Vector2 Normal;
    public int Count;
    public Vector2 Point0;
    public Vector2 Point1;
    public float Penetration0;
    public float Penetration1;

    public readonly Vector2 Point(int i) => i == 0 ? Point0 : Point1;

    public readonly float Penetration(int i) => i == 0 ? Penetration0 : Penetration1;

    public void Add(Vector2 point, float penetration)
    {
        if (Count == 0)
        {
            Point0 = point;
            Penetration0 = penetration;
        }
        else
        {
            Point1 = point;
            Penetration1 = penetration;
        }

        Count++;
    }
}

/// <summary>Body geometry in world space for one step: rotation computed once with <see cref="DetMath"/>.</summary>
internal readonly struct WorldShape
{
    public readonly Vector2 Center;
    public readonly float Sin;
    public readonly float Cos;
    public readonly float Radius;
    public readonly Vector2 HalfExtents;
    public readonly bool IsCircle;

    public WorldShape(Body2D body)
    {
        Center = body.Position;
        (Sin, Cos) = DetMath.SinCos(body.Angle);
        if (body.Shape is CircleShape circle)
        {
            IsCircle = true;
            Radius = circle.Radius;
        }
        else
        {
            HalfExtents = ((BoxShape)body.Shape).HalfExtents;
        }
    }

    public (Vector2 Min, Vector2 Max) Bounds()
    {
        if (IsCircle)
        {
            return (Center - new Vector2(Radius), Center + new Vector2(Radius));
        }

        var ex = MathF.Abs(Cos) * HalfExtents.X + MathF.Abs(Sin) * HalfExtents.Y;
        var ey = MathF.Abs(Sin) * HalfExtents.X + MathF.Abs(Cos) * HalfExtents.Y;
        return (Center - new Vector2(ex, ey), Center + new Vector2(ex, ey));
    }

    /// <summary>Box corners counter-clockwise (bottom-left first) and outward edge normals; edge i runs from vertex i to i+1.</summary>
    public void Polygon(Span<Vector2> vertices, Span<Vector2> normals)
    {
        var h = HalfExtents;
        vertices[0] = Center + DetMath.Rotate(new Vector2(-h.X, -h.Y), Sin, Cos);
        vertices[1] = Center + DetMath.Rotate(new Vector2(h.X, -h.Y), Sin, Cos);
        vertices[2] = Center + DetMath.Rotate(new Vector2(h.X, h.Y), Sin, Cos);
        vertices[3] = Center + DetMath.Rotate(new Vector2(-h.X, h.Y), Sin, Cos);
        normals[0] = DetMath.Rotate(new Vector2(0, -1), Sin, Cos);
        normals[1] = DetMath.Rotate(new Vector2(1, 0), Sin, Cos);
        normals[2] = DetMath.Rotate(new Vector2(0, 1), Sin, Cos);
        normals[3] = DetMath.Rotate(new Vector2(-1, 0), Sin, Cos);
    }
}

/// <summary>Narrow phase: circle/circle, circle/box and box/box (SAT with reference-face clipping, as in Box2D).</summary>
internal static class Collision2D
{
    private const float Epsilon = 1e-6f;

    public static bool Collide(in WorldShape a, in WorldShape b, out Manifold manifold)
    {
        manifold = default;
        if (a.IsCircle && b.IsCircle)
        {
            return Circles(a, b, ref manifold);
        }

        if (!a.IsCircle && b.IsCircle)
        {
            return BoxCircle(a, b, ref manifold);
        }

        if (a.IsCircle && !b.IsCircle)
        {
            var hit = BoxCircle(b, a, ref manifold);
            manifold.Normal = -manifold.Normal;
            return hit;
        }

        return Boxes(a, b, ref manifold);
    }

    private static bool Circles(in WorldShape a, in WorldShape b, ref Manifold manifold)
    {
        var delta = b.Center - a.Center;
        var radius = a.Radius + b.Radius;
        var distanceSquared = delta.LengthSquared();
        if (distanceSquared >= radius * radius)
        {
            return false;
        }

        var distance = MathF.Sqrt(distanceSquared);
        manifold.Normal = distance > Epsilon ? delta / distance : Vector2.UnitY;
        manifold.Add(a.Center + manifold.Normal * a.Radius, radius - distance);
        return true;
    }

    private static bool BoxCircle(in WorldShape box, in WorldShape circle, ref Manifold manifold)
    {
        var local = DetMath.RotateInverse(circle.Center - box.Center, box.Sin, box.Cos);
        var h = box.HalfExtents;
        var clamped = Vector2.Clamp(local, -h, h);
        var inside = clamped == local;
        if (inside)
        {
            // Center inside the box: push out through the nearest face.
            var dx = h.X - MathF.Abs(local.X);
            var dy = h.Y - MathF.Abs(local.Y);
            Vector2 localNormal;
            float depth;
            if (dx < dy)
            {
                localNormal = new Vector2(local.X >= 0 ? 1 : -1, 0);
                depth = dx;
            }
            else
            {
                localNormal = new Vector2(0, local.Y >= 0 ? 1 : -1);
                depth = dy;
            }

            manifold.Normal = DetMath.Rotate(localNormal, box.Sin, box.Cos);
            manifold.Add(circle.Center - manifold.Normal * circle.Radius, depth + circle.Radius);
            return true;
        }

        var offset = local - clamped;
        var distanceSquared = offset.LengthSquared();
        if (distanceSquared >= circle.Radius * circle.Radius)
        {
            return false;
        }

        var distance = MathF.Sqrt(distanceSquared);
        manifold.Normal = DetMath.Rotate(offset / distance, box.Sin, box.Cos);
        manifold.Add(box.Center + DetMath.Rotate(clamped, box.Sin, box.Cos), circle.Radius - distance);
        return true;
    }

    private static bool Boxes(in WorldShape a, in WorldShape b, ref Manifold manifold)
    {
        Span<Vector2> va = stackalloc Vector2[4];
        Span<Vector2> na = stackalloc Vector2[4];
        Span<Vector2> vb = stackalloc Vector2[4];
        Span<Vector2> nb = stackalloc Vector2[4];
        a.Polygon(va, na);
        b.Polygon(vb, nb);

        var (edgeA, separationA) = MaxSeparation(va, na, vb);
        if (separationA > 0)
        {
            return false;
        }

        var (edgeB, separationB) = MaxSeparation(vb, nb, va);
        if (separationB > 0)
        {
            return false;
        }

        // Prefer A as reference unless B is clearly better; the tolerance keeps the choice stable frame to frame.
        var flip = separationB > separationA + 0.0005f;
        var refVertices = flip ? vb : va;
        var refNormals = flip ? nb : na;
        var incVertices = flip ? va : vb;
        var incNormals = flip ? na : nb;
        var refEdge = flip ? edgeB : edgeA;
        var normal = refNormals[refEdge];

        // Incident edge: the one most anti-parallel to the reference normal.
        var incEdge = 0;
        var minDot = float.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var dot = Vector2.Dot(normal, incNormals[i]);
            if (dot < minDot)
            {
                minDot = dot;
                incEdge = i;
            }
        }

        Span<Vector2> incident = [incVertices[incEdge], incVertices[(incEdge + 1) % 4]];
        var v1 = refVertices[refEdge];
        var v2 = refVertices[(refEdge + 1) % 4];
        var tangent = Vector2.Normalize(v2 - v1);

        Span<Vector2> clipped = stackalloc Vector2[2];
        if (Clip(incident, clipped, -tangent, -Vector2.Dot(tangent, v1)) < 2)
        {
            return false;
        }

        Span<Vector2> clipped2 = stackalloc Vector2[2];
        if (Clip(clipped, clipped2, tangent, Vector2.Dot(tangent, v2)) < 2)
        {
            return false;
        }

        manifold.Normal = flip ? -normal : normal;
        for (var i = 0; i < 2; i++)
        {
            var separation = Vector2.Dot(normal, clipped2[i] - v1);
            if (separation <= 0)
            {
                manifold.Add(clipped2[i], -separation);
            }
        }

        return manifold.Count > 0;
    }

    /// <summary>Largest separation of <paramref name="other"/> along the edge normals of the polygon.</summary>
    private static (int Edge, float Separation) MaxSeparation(ReadOnlySpan<Vector2> vertices, ReadOnlySpan<Vector2> normals, ReadOnlySpan<Vector2> other)
    {
        var bestEdge = 0;
        var best = float.MinValue;
        for (var i = 0; i < 4; i++)
        {
            var min = float.MaxValue;
            for (var j = 0; j < 4; j++)
            {
                min = MathF.Min(min, Vector2.Dot(normals[i], other[j] - vertices[i]));
            }

            if (min > best)
            {
                best = min;
                bestEdge = i;
            }
        }

        return (bestEdge, best);
    }

    /// <summary>Keeps the part of a segment where dot(normal, p) &lt;= offset (Sutherland–Hodgman for one plane).</summary>
    private static int Clip(ReadOnlySpan<Vector2> input, Span<Vector2> output, Vector2 normal, float offset)
    {
        var count = 0;
        var d0 = Vector2.Dot(normal, input[0]) - offset;
        var d1 = Vector2.Dot(normal, input[1]) - offset;
        if (d0 <= 0)
        {
            output[count++] = input[0];
        }

        if (d1 <= 0)
        {
            output[count++] = input[1];
        }

        if (d0 * d1 < 0 && count < 2)
        {
            output[count++] = input[0] + (input[1] - input[0]) * (d0 / (d0 - d1));
        }

        return count;
    }
}
