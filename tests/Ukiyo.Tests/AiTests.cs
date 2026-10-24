using System.Numerics;
using Ukiyo.AI;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Ukiyo.Samples.Patrol;
using Xunit;

namespace Ukiyo.Tests;

public sealed class AiTests
{
    private enum Mood
    {
        Calm,
        Alert,
        Gone,
    }

    // ---- state machines ----

    [Fact]
    public void State_machine_runs_exit_then_enter_on_a_transition_and_ticks_only_without_one()
    {
        var log = new List<string>();
        var danger = false;
        var machine = new StateMachine<Mood>();
        machine.State(Mood.Calm).OnEnter(() => log.Add("enter calm")).OnExit(() => log.Add("exit calm")).OnTick(() => log.Add("tick calm")).When(() => danger, Mood.Alert);
        machine.State(Mood.Alert).OnEnter(() => log.Add("enter alert")).OnTick(() => log.Add("tick alert"));
        machine.Start(Mood.Calm);

        machine.Tick();
        danger = true;
        machine.Tick();
        machine.Tick();

        Assert.Equal(["enter calm", "tick calm", "exit calm", "enter alert", "tick alert"], log);
        Assert.Equal(Mood.Alert, machine.Current);
        Assert.Equal(1, machine.Transitions);
        Assert.Equal(1, machine.TicksInState);
    }

    [Fact]
    public void State_machine_takes_one_transition_per_tick_and_the_first_true_guard_wins()
    {
        var machine = new StateMachine<Mood>();
        machine.State(Mood.Calm).When(() => true, Mood.Alert).When(() => true, Mood.Gone);
        machine.State(Mood.Alert).When(() => true, Mood.Gone);
        machine.State(Mood.Gone);
        machine.Start(Mood.Calm);

        machine.Tick();
        Assert.Equal(Mood.Alert, machine.Current);
        machine.Tick();
        Assert.Equal(Mood.Gone, machine.Current);
    }

    [Fact]
    public void State_machine_rejects_setup_mistakes_with_tagged_errors()
    {
        var machine = new StateMachine<Mood>();
        machine.State(Mood.Calm).When(() => true, Mood.Gone);
        Assert.Throws<ArgumentException>(() => machine.State(Mood.Calm));
        Assert.Throws<InvalidOperationException>(machine.Tick);
        var undefined = Assert.Throws<InvalidOperationException>(() => machine.Start(Mood.Calm));
        Assert.StartsWith("[AI]:", undefined.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => _ = machine.Current);
    }

    [Fact]
    public void State_machine_snapshot_names_the_current_state()
    {
        var machine = new StateMachine<Mood>();
        machine.State(Mood.Calm).When(() => true, Mood.Alert);
        machine.State(Mood.Alert);
        machine.Start(Mood.Calm);
        machine.Tick();

        var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
        machine.WriteSnapshot(values, "npc");

        Assert.Equal(1, values["npc.state"]);
        Assert.Equal(1, values["npc.state.Alert"]);
        Assert.Equal(0, values["npc.state.Calm"]);
    }

    // ---- blackboard ----

    [Fact]
    public void Blackboard_slots_are_declared_once_and_unset_until_written()
    {
        var board = new Blackboard();
        var target = board.Declare("target");
        Assert.False(board.Has(target));
        board.SetPoint(target, new Vector2(2, 3));
        Assert.True(board.Has(target));
        Assert.Equal(new Vector2(2, 3), board.GetPoint(target));
        Assert.Throws<ArgumentException>(() => board.Declare("target"));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.GetNumber(new BlackboardKey(7)));
        board.Clear(target);
        Assert.False(board.Has(target));
    }

    // ---- behavior trees ----

    private static BtContext Context(Blackboard board, long tick) => new(board, tick);

    private static BtNode Leaf(string name, Func<BtStatus> result, List<string>? log = null) =>
        Bt.Action(name, _ =>
        {
            log?.Add(name);
            return result();
        });

    [Fact]
    public void Sequence_stops_at_the_first_failure_and_resumes_at_the_running_child()
    {
        var log = new List<string>();
        var running = true;
        var tree = new BehaviorTree(Bt.Sequence(
            Leaf("a", () => BtStatus.Success, log),
            Leaf("b", () => running ? BtStatus.Running : BtStatus.Success, log),
            Leaf("c", () => BtStatus.Failure, log)));
        var board = new Blackboard();

        Assert.Equal(BtStatus.Running, tree.Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Running, tree.Tick(Context(board, 1)));
        running = false;
        Assert.Equal(BtStatus.Failure, tree.Tick(Context(board, 2)));

        Assert.Equal(["a", "b", "b", "b", "c"], log);
    }

    [Fact]
    public void Selector_is_reactive_a_higher_priority_branch_preempts_and_aborts_the_running_one()
    {
        var aborted = 0;
        var danger = false;
        var tree = new BehaviorTree(Bt.Selector(
            Bt.Guard("danger", _ => danger, Bt.Action("flee", _ => BtStatus.Running)),
            Bt.Action("wander", _ => BtStatus.Running, () => aborted++)));
        var board = new Blackboard();

        tree.Tick(Context(board, 0));
        Assert.Equal("Selector/wander", tree.DescribeActivePath());
        danger = true;
        tree.Tick(Context(board, 1));
        Assert.Equal("Selector/danger/flee", tree.DescribeActivePath());
        Assert.Equal(1, aborted);
    }

    [Fact]
    public void Selector_fails_when_every_child_fails_and_succeeds_on_the_first_success()
    {
        var board = new Blackboard();
        var failing = new BehaviorTree(Bt.Selector(Leaf("x", () => BtStatus.Failure), Leaf("y", () => BtStatus.Failure)));
        var winning = new BehaviorTree(Bt.Selector(Leaf("x", () => BtStatus.Failure), Leaf("y", () => BtStatus.Success)));
        Assert.Equal(BtStatus.Failure, failing.Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Success, winning.Tick(Context(board, 0)));
    }

    [Fact]
    public void Inverter_and_succeeder_change_only_finished_results()
    {
        var board = new Blackboard();
        Assert.Equal(BtStatus.Failure, new BehaviorTree(Bt.Inverter(Leaf("s", () => BtStatus.Success))).Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Success, new BehaviorTree(Bt.Inverter(Leaf("f", () => BtStatus.Failure))).Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Running, new BehaviorTree(Bt.Inverter(Leaf("r", () => BtStatus.Running))).Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Success, new BehaviorTree(Bt.Succeeder(Leaf("f", () => BtStatus.Failure))).Tick(Context(board, 0)));
    }

    [Fact]
    public void Repeat_runs_the_child_the_requested_number_of_times_one_per_tick()
    {
        var runs = 0;
        var tree = new BehaviorTree(Bt.Repeat(3, Leaf("work", () =>
        {
            runs++;
            return BtStatus.Success;
        })));
        var board = new Blackboard();

        Assert.Equal(BtStatus.Running, tree.Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Running, tree.Tick(Context(board, 1)));
        Assert.Equal(BtStatus.Success, tree.Tick(Context(board, 2)));
        Assert.Equal(3, runs);
    }

    [Fact]
    public void Repeat_fails_as_soon_as_the_child_fails()
    {
        var tree = new BehaviorTree(Bt.RepeatForever(Leaf("work", () => BtStatus.Failure)));
        Assert.Equal(BtStatus.Failure, tree.Tick(Context(new Blackboard(), 0)));
    }

    [Fact]
    public void Cooldown_blocks_the_child_for_the_given_number_of_ticks_after_it_finishes()
    {
        var runs = new List<long>();
        var board = new Blackboard();
        var tree = new BehaviorTree(Bt.Cooldown(10, Bt.Action("fire", context =>
        {
            runs.Add(context.Tick);
            return BtStatus.Success;
        })));

        var results = Enumerable.Range(0, 25).Select(tick => tree.Tick(Context(board, tick))).ToArray();

        Assert.Equal([0L, 10L, 20L], runs);
        Assert.Equal(BtStatus.Failure, results[5]);
        Assert.Equal(BtStatus.Success, results[10]);
    }

    [Fact]
    public void Guard_and_condition_read_the_blackboard()
    {
        var board = new Blackboard();
        var hp = board.Declare("hp");
        var tree = new BehaviorTree(Bt.Sequence(Bt.Condition("alive", context => context.Blackboard.GetNumber(hp) > 0), Leaf("act", () => BtStatus.Success)));

        Assert.Equal(BtStatus.Failure, tree.Tick(Context(board, 0)));
        board.SetNumber(hp, 5);
        Assert.Equal(BtStatus.Success, tree.Tick(Context(board, 1)));
    }

    [Fact]
    public void Parallel_finishes_by_policy_and_remembers_finished_children()
    {
        var ticks = 0;
        var all = new BehaviorTree(Bt.Parallel(
            ParallelPolicy.RequireAll,
            ParallelPolicy.RequireOne,
            Leaf("quick", () =>
            {
                ticks++;
                return BtStatus.Success;
            }),
            Bt.Wait("slow", 2)));
        var board = new Blackboard();

        Assert.Equal(BtStatus.Running, all.Tick(Context(board, 0)));
        Assert.Equal(BtStatus.Running, all.Tick(Context(board, 1)));
        Assert.Equal(BtStatus.Success, all.Tick(Context(board, 2)));
        Assert.Equal(1, ticks);

        var oneFails = new BehaviorTree(Bt.Parallel(ParallelPolicy.RequireAll, ParallelPolicy.RequireOne, Leaf("run", () => BtStatus.Running), Leaf("bad", () => BtStatus.Failure)));
        Assert.Equal(BtStatus.Failure, oneFails.Tick(Context(board, 0)));
    }

    [Fact]
    public void Wait_runs_for_its_ticks_then_succeeds_and_restarts()
    {
        var tree = new BehaviorTree(Bt.Wait("pause", 2));
        var board = new Blackboard();
        var statuses = Enumerable.Range(0, 6).Select(tick => tree.Tick(Context(board, tick))).ToArray();
        Assert.Equal([BtStatus.Running, BtStatus.Running, BtStatus.Success, BtStatus.Running, BtStatus.Running, BtStatus.Success], statuses);
    }

    [Fact]
    public void Tree_publishes_the_active_path_and_snapshot_keys_without_allocating_per_tick()
    {
        var tree = new BehaviorTree(Bt.Selector(
            "Brain",
            Bt.Guard("never", _ => false, Leaf("hidden", () => BtStatus.Running)),
            Bt.Sequence("Patrol", Leaf("walk", () => BtStatus.Running))));
        var board = new Blackboard();
        tree.Tick(Context(board, 0));

        Assert.Equal("Brain/Patrol/walk", tree.DescribeActivePath());
        var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
        tree.WriteSnapshot(values, "npc");
        Assert.Equal(3, values["npc.bt.depth"]);
        Assert.Equal(tree.Root.Children[1].Children[0].Id, values["npc.bt.leaf"]);
        Assert.Equal(2, values["npc.bt.run.4.walk"]);

        // Warm up, then measure: ticking a built tree must not allocate.
        for (var i = 0; i < 10; i++)
        {
            tree.Tick(Context(board, i));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            tree.Tick(Context(board, i));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_node_cannot_belong_to_two_trees_and_composites_need_children()
    {
        var shared = Leaf("shared", () => BtStatus.Success);
        _ = new BehaviorTree(Bt.Sequence(shared));
        Assert.Throws<ArgumentException>(() => new BehaviorTree(Bt.Sequence(shared)));
        Assert.Throws<ArgumentException>(() => Bt.Selector());
        Assert.Throws<ArgumentOutOfRangeException>(() => Bt.Cooldown(-1, shared));
    }

    // ---- grid and A* ----

    private static readonly string[] Maze =
    [
        "..........",
        ".########.",
        ".#......#.",
        ".#.####.#.",
        ".#.#..#.#.",
        ".#.#.##.#.",
        ".#.#....#.",
        ".#.######.",
        ".#........",
        "..........",
    ];

    private static int StepCost(GridMap map, GridCell from, GridCell to) =>
        (from.X != to.X && from.Y != to.Y ? GridPathfinder.DiagonalCost : GridPathfinder.StraightCost) * map.WeightAt(to);

    private static int PathCost(GridMap map, IReadOnlyList<GridCell> path, bool diagonals)
    {
        var total = 0;
        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(map.IsWalkable(path[i]));
            Assert.True(diagonals || path[i].X == path[i - 1].X || path[i].Y == path[i - 1].Y, "diagonal step in a four-direction path");
            total += StepCost(map, path[i - 1], path[i]);
        }

        return total;
    }

    /// <summary>Plain O(n^2) Dijkstra, independent of the A* implementation; int.MaxValue when unreachable.</summary>
    private static int ReferenceCost(GridMap map, GridCell start, GridCell goal, bool diagonals)
    {
        if (!map.IsWalkable(start) || !map.IsWalkable(goal))
        {
            return int.MaxValue;
        }

        var dist = new int[map.Width * map.Height];
        Array.Fill(dist, int.MaxValue);
        var done = new bool[dist.Length];
        dist[map.IndexOf(start)] = 0;
        while (true)
        {
            var best = -1;
            for (var i = 0; i < dist.Length; i++)
            {
                if (!done[i] && dist[i] != int.MaxValue && (best < 0 || dist[i] < dist[best]))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return dist[map.IndexOf(goal)];
            }

            done[best] = true;
            var cell = map.CellOf(best);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0) || (!diagonals && dx != 0 && dy != 0))
                    {
                        continue;
                    }

                    var next = new GridCell(cell.X + dx, cell.Y + dy);
                    if (!map.IsWalkable(next))
                    {
                        continue;
                    }

                    if (dx != 0 && dy != 0 && (!map.IsWalkable(new GridCell(cell.X + dx, cell.Y)) || !map.IsWalkable(new GridCell(cell.X, cell.Y + dy))))
                    {
                        continue;
                    }

                    var cost = dist[best] + StepCost(map, cell, next);
                    var index = map.IndexOf(next);
                    if (cost < dist[index])
                    {
                        dist[index] = cost;
                    }
                }
            }
        }
    }

    [Fact]
    public void Open_grid_four_direction_cost_is_the_manhattan_distance()
    {
        var map = new GridMap(8, 8);
        var path = new List<GridCell>();
        var result = new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(5, 3), path);

        Assert.True(result.Found);
        Assert.Equal(8 * GridPathfinder.StraightCost, result.Cost);
        Assert.Equal(9, path.Count);
        Assert.Equal(new GridCell(0, 0), path[0]);
        Assert.Equal(new GridCell(5, 3), path[^1]);
        Assert.All(path.Zip(path.Skip(1)), pair => Assert.Equal(1, Math.Abs(pair.First.X - pair.Second.X) + Math.Abs(pair.First.Y - pair.Second.Y)));
    }

    [Fact]
    public void Open_grid_eight_direction_cost_is_octile()
    {
        var map = new GridMap(8, 8);
        var path = new List<GridCell>();
        var result = new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(5, 3), path, new PathOptions(Diagonals: true));

        Assert.Equal(3 * GridPathfinder.DiagonalCost + 2 * GridPathfinder.StraightCost, result.Cost);
        Assert.Equal(6, path.Count);
    }

    [Fact]
    public void Diagonal_moves_never_cut_a_blocked_corner()
    {
        var map = GridMap.FromRows(["..#", ".#.", "..."]);
        var path = new List<GridCell>();
        var result = new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(2, 1), path, new PathOptions(Diagonals: true));

        Assert.True(result.Found);
        foreach (var (a, b) in path.Zip(path.Skip(1)))
        {
            if (a.X != b.X && a.Y != b.Y)
            {
                Assert.True(map.IsWalkable(new GridCell(b.X, a.Y)) && map.IsWalkable(new GridCell(a.X, b.Y)), $"{a} -> {b} cuts a corner");
            }
        }

        // (0,0) -> (1,1) would cut the blocked (1,1)... the only diagonal-free route has 4 straight steps and a diagonal at the end is not available.
        Assert.Equal(new GridCell(2, 1), path[^1]);
    }

    [Fact]
    public void Maze_path_has_the_known_optimal_length()
    {
        var map = GridMap.FromRows(Maze);
        var path = new List<GridCell>();
        var pathfinder = new GridPathfinder(map);

        var result = pathfinder.FindPath(new GridCell(0, 0), new GridCell(4, 4), path);

        Assert.True(result.Found);
        Assert.Equal(ReferenceCost(map, new GridCell(0, 0), new GridCell(4, 4), diagonals: false), result.Cost);
        Assert.All(path, cell => Assert.True(map.IsWalkable(cell)));
    }

    [Fact]
    public void Weighted_cells_are_avoided_when_a_detour_is_cheaper()
    {
        var map = GridMap.FromRows(["....", ".99.", "...."]);
        var path = new List<GridCell>();
        var result = new GridPathfinder(map).FindPath(new GridCell(0, 1), new GridCell(3, 1), path);

        Assert.True(result.Found);
        Assert.DoesNotContain(new GridCell(1, 1), path);
        Assert.Equal(ReferenceCost(map, new GridCell(0, 1), new GridCell(3, 1), diagonals: false), result.Cost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Random_maps_match_a_reference_dijkstra_cost_and_are_repeatable(bool diagonals)
    {
        var random = new Random(1234);
        for (var round = 0; round < 40; round++)
        {
            var map = new GridMap(14, 11);
            for (var i = 0; i < 40; i++)
            {
                map.SetWeight(new GridCell(random.Next(14), random.Next(11)), random.Next(4) == 0 ? 0 : 1 + random.Next(3));
            }

            var pathfinder = new GridPathfinder(map);
            var start = new GridCell(random.Next(14), random.Next(11));
            var goal = new GridCell(random.Next(14), random.Next(11));
            var options = new PathOptions(diagonals);
            var first = new List<GridCell>();
            var second = new List<GridCell>();

            var a = pathfinder.FindPath(start, goal, first, options);
            var b = new GridPathfinder(map).FindPath(start, goal, second, options);

            Assert.Equal(a, b);
            Assert.Equal(first, second);
            if (a.Status == PathStatus.Found)
            {
                Assert.Equal(ReferenceCost(map, start, goal, diagonals), a.Cost);
                Assert.Equal(a.Cost, PathCost(map, first, diagonals));
            }
            else if (a.Status == PathStatus.Unreachable)
            {
                Assert.Equal(int.MaxValue, ReferenceCost(map, start, goal, diagonals));
            }
        }
    }

    [Fact]
    public void Equal_cost_routes_resolve_the_same_way_every_time()
    {
        var map = new GridMap(6, 6);
        var pathfinder = new GridPathfinder(map);
        var first = new List<GridCell>();
        pathfinder.FindPath(new GridCell(0, 0), new GridCell(5, 5), first);
        for (var i = 0; i < 5; i++)
        {
            var again = new List<GridCell>();
            pathfinder.FindPath(new GridCell(0, 0), new GridCell(5, 5), again);
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void Unreachable_target_is_a_typed_result_not_an_exception()
    {
        var map = GridMap.FromRows([".#.", ".#.", ".#."]);
        var path = new List<GridCell> { new(9, 9) };
        var result = new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(2, 0), path);

        Assert.Equal(PathStatus.Unreachable, result.Status);
        Assert.False(result.Found);
        Assert.Empty(path);
        Assert.True(result.Expanded > 0);
    }

    [Fact]
    public void Blocked_and_out_of_bounds_endpoints_are_typed_results()
    {
        var map = GridMap.FromRows(["..#", "..."]);
        var pathfinder = new GridPathfinder(map);
        var path = new List<GridCell>();

        Assert.Equal(PathStatus.StartBlocked, pathfinder.FindPath(new GridCell(2, 0), new GridCell(0, 0), path).Status);
        Assert.Equal(PathStatus.GoalBlocked, pathfinder.FindPath(new GridCell(0, 0), new GridCell(2, 0), path).Status);
        Assert.Equal(PathStatus.OutOfBounds, pathfinder.FindPath(new GridCell(-1, 0), new GridCell(0, 0), path).Status);
        Assert.Equal(PathStatus.OutOfBounds, pathfinder.FindPath(new GridCell(0, 0), new GridCell(0, 2), path).Status);
    }

    [Fact]
    public void Start_equal_to_goal_is_a_one_cell_path_and_the_search_limit_is_reported()
    {
        var map = new GridMap(30, 30);
        var pathfinder = new GridPathfinder(map);
        var path = new List<GridCell>();

        var same = pathfinder.FindPath(new GridCell(3, 3), new GridCell(3, 3), path);
        Assert.Equal(new PathResult(PathStatus.Found, 0, 0, 1), same);
        Assert.Equal([new GridCell(3, 3)], path);

        var limited = pathfinder.FindPath(new GridCell(0, 0), new GridCell(29, 29), path, new PathOptions(MaxExpansions: 5));
        Assert.Equal(PathStatus.SearchLimit, limited.Status);
        Assert.Empty(path);
    }

    [Fact]
    public void Map_changes_are_seen_by_the_next_search_and_bump_the_version()
    {
        var map = new GridMap(5, 1);
        var pathfinder = new GridPathfinder(map);
        var path = new List<GridCell>();
        Assert.True(pathfinder.FindPath(new GridCell(0, 0), new GridCell(4, 0), path).Found);

        var version = map.Version;
        map.SetBlocked(new GridCell(2, 0));

        Assert.NotEqual(version, map.Version);
        Assert.Equal(PathStatus.Unreachable, pathfinder.FindPath(new GridCell(0, 0), new GridCell(4, 0), path).Status);
    }

    [Fact]
    public void Map_parsing_and_edits_reject_bad_input()
    {
        Assert.Throws<ArgumentException>(() => GridMap.FromRows(["..", "."]));
        Assert.Throws<ArgumentException>(() => GridMap.FromRows([".x"]));
        Assert.Throws<ArgumentException>(() => GridMap.FromRows([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridMap(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridMap(3, 3).SetWeight(new GridCell(3, 0), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridMap(3, 3).SetWeight(new GridCell(0, 0), 300));
    }

    [Fact]
    public void World_conversions_map_cells_for_2d_and_for_3d_games_on_a_plane()
    {
        var map = new GridMap(10, 10, cellSize: 2f, origin: new Vector2(-10f, -10f));
        Assert.Equal(new Vector2(-9f, -9f), map.CellCenter(new GridCell(0, 0)));
        Assert.Equal(new GridCell(5, 5), map.CellAt(new Vector2(0.5f, 0.5f)));
        Assert.Equal(new GridCell(-1, 0), map.CellAt(new Vector2(-10.1f, -9f)));
        Assert.Equal(new Vector3(-9f, 1.5f, -7f), map.CellCenterXZ(new GridCell(0, 1), 1.5f));
        Assert.Equal(new GridCell(0, 1), map.CellAtXZ(new Vector3(-9.5f, 4f, -7.2f)));
    }

    [Fact]
    public void Line_of_sight_stops_at_walls_and_does_not_slip_between_diagonal_corners()
    {
        var map = GridMap.FromRows(["....", ".#..", "...."]);
        Assert.True(map.HasLineOfSight(new GridCell(0, 0), new GridCell(3, 0)));
        Assert.False(map.HasLineOfSight(new GridCell(0, 1), new GridCell(3, 1)));
        Assert.True(map.HasLineOfSight(new GridCell(2, 2), new GridCell(2, 2)));

        var corner = GridMap.FromRows([".#", "#."]);
        Assert.False(corner.HasLineOfSight(new GridCell(0, 0), new GridCell(1, 1)));
    }

    [Fact]
    public void Simplify_keeps_the_ends_and_drops_waypoints_a_straight_walk_can_skip()
    {
        var map = new GridMap(10, 10);
        var path = new List<GridCell>();
        new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(9, 0), path);
        PathSmoothing.Simplify(map, path);
        Assert.Equal([new GridCell(0, 0), new GridCell(9, 0)], path);

        var detour = GridMap.FromRows(["....", ".##.", ".##.", "...."]);
        var around = new List<GridCell>();
        new GridPathfinder(detour).FindPath(new GridCell(0, 0), new GridCell(3, 3), around);
        PathSmoothing.Simplify(detour, around);
        Assert.Equal(new GridCell(0, 0), around[0]);
        Assert.Equal(new GridCell(3, 3), around[^1]);
        Assert.All(around.Zip(around.Skip(1)), pair => Assert.True(detour.HasLineOfSight(pair.First, pair.Second)));
    }

    // ---- steering ----

    [Fact]
    public void Arrive_slows_inside_the_radius_and_stops_on_the_target()
    {
        var far = Steering.Arrive(Vector2.Zero, new Vector2(10, 0), 4f, 2f);
        var near = Steering.Arrive(Vector2.Zero, new Vector2(1, 0), 4f, 2f);

        Assert.Equal(new Vector2(4, 0), far);
        Assert.Equal(new Vector2(2, 0), near);
        Assert.Equal(Vector2.Zero, Steering.Arrive(new Vector2(3, 3), new Vector2(3, 3), 4f, 2f));
        Assert.Equal(new Vector2(-4, 0), Steering.Flee(Vector2.Zero, new Vector2(5, 0), 4f));
    }

    [Fact]
    public void Agent_acceleration_is_capped_per_tick_and_speed_never_exceeds_the_limit()
    {
        var agent = new SteeringAgent(Vector2.Zero, maxSpeed: 3f, maxAcceleration: 6f);
        agent.Step(new Vector2(30, 0), Simulation.TickSecondsF);
        Assert.True(agent.Velocity.X <= 6f * Simulation.TickSecondsF + 1e-5f);
        for (var i = 0; i < 120; i++)
        {
            agent.Step(new Vector2(30, 0), Simulation.TickSecondsF);
        }

        Assert.True(DetMath.Length(agent.Velocity) <= 3f + 1e-5f);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SteeringAgent(Vector2.Zero, 0f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => agent.MaxSpeed = -1f);
    }

    [Fact]
    public void Path_follower_walks_a_route_around_walls_and_stops_on_the_goal_cell()
    {
        var map = GridMap.FromRows(Maze);
        var path = new List<GridCell>();
        new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(9, 9), path, new PathOptions(Diagonals: true));
        var agent = new SteeringAgent(map.CellCenter(new GridCell(0, 0)), 4f, 20f);
        var follower = new PathFollower(agent);
        follower.Follow(map, path);

        var ticks = 0;
        while (!follower.Finished && ticks < 2000)
        {
            follower.Step(Simulation.TickSecondsF);
            Assert.True(map.IsWalkable(map.CellAt(agent.Position)), $"left the walkable cells at tick {ticks}: {agent.Position}");
            ticks++;
        }

        Assert.True(follower.Finished);
        Assert.Equal(map.CellCenter(new GridCell(9, 9)), agent.Position);
        Assert.Equal(Vector2.Zero, agent.Velocity);
    }

    [Fact]
    public void Path_follower_runs_are_bit_identical()
    {
        Vector2 Run()
        {
            var map = GridMap.FromRows(Maze);
            var path = new List<GridCell>();
            new GridPathfinder(map).FindPath(new GridCell(0, 0), new GridCell(9, 9), path, new PathOptions(Diagonals: true));
            var follower = new PathFollower(new SteeringAgent(map.CellCenter(new GridCell(0, 0)), 3.3f, 14f));
            follower.Follow(map, path);
            for (var i = 0; i < 300; i++)
            {
                follower.Step(Simulation.TickSecondsF);
            }

            return follower.Agent.Position;
        }

        Assert.Equal(Run(), Run());
    }

    // ---- the Patrol sample ----

    private static readonly SourceIdentity NoSource = new(new Dictionary<string, string>());

    private static async Task<(GameRuntime Runtime, PatrolGame Game)> RunPatrolAsync(long ticks)
    {
        var game = new PatrolGame();
        var runtime = new GameRuntime(game, NoSource) { Script = InputScript.Parse(File.ReadAllText(Path.Combine(CoreTests.RepoRoot(), "samples", "Patrol", "demo-input.json"))) };
        await runtime.StartAsync(new NullRenderer(), new RenderConfiguration("test", new RenderExtent(640, 360, 1f)));
        runtime.StepTo(ticks);
        runtime.RenderFrame(new RenderExtent(640, 360, 1f));
        return (runtime, game);
    }

    [Fact]
    public void Patrol_level_is_one_connected_region_reaching_every_route_and_the_player_start()
    {
        var map = PatrolMap.Create();
        var pathfinder = new GridPathfinder(map);
        var path = new List<GridCell>();
        foreach (var cell in PatrolMap.SentryRoute.Concat(PatrolMap.HoundRoute))
        {
            Assert.True(pathfinder.FindPath(PatrolMap.PlayerStart, cell, path, new PathOptions(Diagonals: true)).Found, $"{cell} is unreachable");
        }
    }

    [Fact]
    public async Task Patrol_is_deterministic_and_the_snapshot_says_what_each_guard_thinks()
    {
        var (a, _) = await RunPatrolAsync(330);
        var (b, _) = await RunPatrolAsync(330);
        Assert.Equal(a.SnapshotJson(), b.SnapshotJson());

        var json = a.SnapshotJson();
        Assert.Contains("\"hound.state.Chase\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"sentry.bt.leaf\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patrol_guards_chase_the_player_who_walks_into_sight_and_patrol_otherwise()
    {
        var (_, calm) = await RunPatrolAsync(60);
        Assert.All(calm.Npcs, npc => Assert.False(npc.Alerted));

        var (_, hunting) = await RunPatrolAsync(360);
        Assert.All(hunting.Npcs, npc => Assert.True(npc.Alerted, $"{npc.Name} did not notice the player"));
        var hound = Assert.IsType<Hound>(hunting.Npcs.Single(npc => npc.Name == "hound"));
        Assert.Equal(HoundState.Chase, hound.State);
    }

    [Fact]
    public async Task Patrol_round_restarts_when_a_guard_catches_the_player()
    {
        var (_, game) = await RunPatrolAsync(640);
        Assert.True(game.Caught >= 1);
    }
}
