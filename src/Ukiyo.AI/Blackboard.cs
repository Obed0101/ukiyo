using System.Numerics;

namespace Ukiyo.AI;

/// <summary>A declared blackboard slot. Handles are plain indices: reading and writing never hashes a string.</summary>
public readonly record struct BlackboardKey(int Index);

/// <summary>
/// Shared memory of one NPC: declared slots holding a number and a point (targets, timers, flags as 0/1). Slots are
/// declared once, in a fixed order, so reads and writes are array accesses and the snapshot lists them in declaration
/// order. Nothing here allocates after <see cref="Declare"/>.
/// </summary>
public sealed class Blackboard
{
    private readonly List<string> _names = [];
    private readonly List<double> _numbers = [];
    private readonly List<Vector2> _points = [];
    private readonly List<bool> _set = [];

    public int Count => _names.Count;

    /// <summary>Declares a slot; names are unique so a typo fails at setup, not as a silent second slot.</summary>
    public BlackboardKey Declare(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_names.Contains(name))
        {
            throw new ArgumentException($"[AI]: blackboard slot '{name}' is already declared", nameof(name));
        }

        _names.Add(name);
        _numbers.Add(0d);
        _points.Add(Vector2.Zero);
        _set.Add(false);
        return new BlackboardKey(_names.Count - 1);
    }

    public string NameOf(BlackboardKey key) => _names[Check(key)];

    public bool Has(BlackboardKey key) => _set[Check(key)];

    public double GetNumber(BlackboardKey key) => _numbers[Check(key)];

    public Vector2 GetPoint(BlackboardKey key) => _points[Check(key)];

    public void SetNumber(BlackboardKey key, double value)
    {
        _numbers[Check(key)] = value;
        _set[key.Index] = true;
    }

    public void SetPoint(BlackboardKey key, Vector2 value)
    {
        _points[Check(key)] = value;
        _set[key.Index] = true;
    }

    public void Clear(BlackboardKey key)
    {
        _numbers[Check(key)] = 0d;
        _points[key.Index] = Vector2.Zero;
        _set[key.Index] = false;
    }

    /// <summary>Writes every set slot as <c>prefix.bb.name</c> (and <c>.x</c>/<c>.y</c> for points) in declaration order.</summary>
    public void WriteSnapshot(IDictionary<string, double> values, string prefix)
    {
        for (var i = 0; i < _names.Count; i++)
        {
            if (!_set[i])
            {
                continue;
            }

            var key = $"{prefix}.bb.{_names[i]}";
            values[key] = DetMath.PositiveZero(_numbers[i]);
            if (_points[i] != Vector2.Zero)
            {
                values[key + ".x"] = DetMath.PositiveZero(_points[i].X);
                values[key + ".y"] = DetMath.PositiveZero(_points[i].Y);
            }
        }
    }

    private int Check(BlackboardKey key)
    {
        if ((uint)key.Index >= (uint)_names.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(key), $"[AI]: blackboard key {key.Index} was not declared on this blackboard");
        }

        return key.Index;
    }
}
