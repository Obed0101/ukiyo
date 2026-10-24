namespace Ukiyo.AI;

/// <summary>Result of ticking a behavior-tree node. <see cref="Idle"/> only appears as "not visited this tick".</summary>
public enum BtStatus
{
    Idle = 0,
    Success = 1,
    Failure = 2,
    Running = 3,
}

/// <summary>What a node sees while it ticks: the NPC's blackboard and the simulation tick (never wall-clock time).</summary>
public readonly record struct BtContext(Blackboard Blackboard, long Tick);

/// <summary>
/// A behavior-tree node. Build a tree once per NPC (nodes keep their own running memory, so a tree is not shared
/// between NPCs) and tick it through <see cref="BehaviorTree"/>. Composites and decorators are in <see cref="Bt"/>.
/// </summary>
public abstract class BtNode
{
    private static readonly BtNode[] NoChildren = [];

    protected BtNode(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Position in a depth-first walk of the owning tree, assigned by <see cref="BehaviorTree"/>; -1 before that.</summary>
    public int Id { get; internal set; } = -1;

    /// <summary>Result of this node in the last tree tick; <see cref="BtStatus.Idle"/> when it was not visited.</summary>
    public BtStatus Status { get; private set; }

    public virtual IReadOnlyList<BtNode> Children => NoChildren;

    public BtStatus Tick(in BtContext context)
    {
        Status = OnTick(context);
        return Status;
    }

    /// <summary>Drops any running memory (here and below): the node was interrupted or its parent finished.</summary>
    public void Abort()
    {
        OnAbort();
        foreach (var child in Children)
        {
            child.Abort();
        }
    }

    internal void ClearStatus() => Status = BtStatus.Idle;

    protected abstract BtStatus OnTick(in BtContext context);

    protected virtual void OnAbort()
    {
    }
}

/// <summary>Owns a root, numbers its nodes and publishes what is running. Ticking allocates nothing.</summary>
public sealed class BehaviorTree
{
    private readonly BtNode[] _nodes;
    private readonly int[] _depths;

    public BehaviorTree(BtNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        var nodes = new List<BtNode>();
        var depths = new List<int>();
        Number(root, 0, nodes, depths);
        _nodes = [.. nodes];
        _depths = [.. depths];
    }

    public BtNode Root { get; }

    public int NodeCount => _nodes.Length;

    public BtNode this[int id] => _nodes[id];

    public BtStatus Tick(in BtContext context)
    {
        foreach (var node in _nodes)
        {
            node.ClearStatus();
        }

        return Root.Tick(context);
    }

    /// <summary>Forgets every node's running memory, as if the tree had never ticked.</summary>
    public void Reset()
    {
        Root.Abort();
        foreach (var node in _nodes)
        {
            node.ClearStatus();
        }
    }

    /// <summary>
    /// Fills <paramref name="path"/> with the ids of the running nodes from the root down (first running child at each
    /// level) and returns how many; zero when nothing is running. No allocation.
    /// </summary>
    public int ActivePath(Span<int> path)
    {
        var count = 0;
        var node = Root;
        while (node.Status == BtStatus.Running && count < path.Length)
        {
            path[count++] = node.Id;
            BtNode? next = null;
            foreach (var child in node.Children)
            {
                if (child.Status == BtStatus.Running)
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                break;
            }

            node = next;
        }

        return count;
    }

    /// <summary>The active path as <c>Root/Selector/Chase</c>, for logs and tests; empty when nothing runs.</summary>
    public string DescribeActivePath()
    {
        Span<int> ids = stackalloc int[_nodes.Length];
        var count = ActivePath(ids);
        return string.Join('/', ids[..count].ToArray().Select(id => _nodes[id].Name));
    }

    /// <summary>
    /// Writes <c>prefix.bt.leaf</c> (id of the deepest node on the active path, -1 when idle), <c>prefix.bt.depth</c>
    /// (path length) and <c>prefix.bt.run.&lt;id&gt;.&lt;Name&gt;</c> = depth for every running node (a parallel can run
    /// several), so a snapshot says which branch the NPC is executing.
    /// </summary>
    public void WriteSnapshot(IDictionary<string, double> values, string prefix)
    {
        Span<int> ids = stackalloc int[_nodes.Length];
        var count = ActivePath(ids);
        values[$"{prefix}.bt.leaf"] = count == 0 ? -1 : ids[count - 1];
        values[$"{prefix}.bt.depth"] = count;
        for (var i = 0; i < _nodes.Length; i++)
        {
            if (_nodes[i].Status == BtStatus.Running)
            {
                values[$"{prefix}.bt.run.{i}.{_nodes[i].Name}"] = _depths[i];
            }
        }
    }

    private static void Number(BtNode node, int depth, List<BtNode> nodes, List<int> depths)
    {
        if (node.Id >= 0)
        {
            throw new ArgumentException($"[AI]: node '{node.Name}' is already part of a tree; build one tree per NPC");
        }

        node.Id = nodes.Count;
        nodes.Add(node);
        depths.Add(depth);
        foreach (var child in node.Children)
        {
            Number(child, depth + 1, nodes, depths);
        }
    }
}
