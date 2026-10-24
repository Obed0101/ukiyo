---
name: ukiyo-ai
description: Use for NPC behaviour — finite state machines, behavior trees with a blackboard, grid A* pathfinding, seek/arrive steering, path following, line-of-sight perception, and publishing what an NPC thinks in snapshots (Ukiyo.AI).
---

# ukiyo AI (NPC behaviour)

`Ukiyo.AI` is deterministic and allocation-light: fixed 60 Hz ticks, no libm, no random inside the module, integer A*
costs, fixed neighbour order and tie-breaking. Same map and inputs give the same path and the same state on every
target. Reference `src/Ukiyo.AI/Ukiyo.AI.csproj` from each host csproj. Errors are `[AI]:`-tagged. Worked example:
`samples/Patrol` (a behavior-tree sentry and a state-machine hound).

## State machine

```csharp
enum HoundState { Patrol, Chase, Search }
var machine = new StateMachine<HoundState>();
machine.State(HoundState.Patrol).OnEnter(() => ...).OnTick(Patrol).When(() => seesPlayer, HoundState.Chase);
machine.State(HoundState.Chase).When(() => lostFor > 20, HoundState.Search);
machine.State(HoundState.Search);          // every transition target must be defined
machine.Start(HoundState.Patrol);          // validates targets
machine.Tick();                            // once per game tick
```

Per tick the first true guard wins and at most one transition happens (exit, then enter; the new state's OnTick runs
next tick). Read `Current`, `TicksInState`, `Transitions`.

## Behavior tree

```csharp
var board = new Blackboard();
var alert = board.Declare("alert");               // typed slots: numbers and points, unset until written
var tree = new BehaviorTree(Bt.Selector("Brain",
    Bt.Guard("Alerted", ctx => ctx.Blackboard.GetNumber(alert) > 0,
        Bt.Parallel(ParallelPolicy.RequireAll, ParallelPolicy.RequireOne,
            Bt.Action("Pursue", ctx => ...),
            Bt.RepeatForever(Bt.Succeeder(Bt.Cooldown(180, Bt.Action("Alarm", ctx => BtStatus.Success)))))),
    Bt.Sequence("Patrol", Bt.Action("GoToLeg", ctx => ...), Bt.Wait("Pause", 20))));
tree.Tick(new BtContext(board, tick));            // Success, Failure or Running
```

Nodes: `Sequence` (remembers its running child), `Selector` (reactive: a higher-priority branch preempts and aborts the
running one), `Parallel(success, failure, ...)`, `Inverter`, `Succeeder`, `Repeat(n)`, `RepeatForever`,
`Cooldown(ticks)`, `Guard`, `Condition`, `Action(name, tick, onAbort)`, `Wait(name, ticks)`. A node belongs to one
tree: build one tree per NPC. Cooldowns and waits count ticks, never wall time.

## Navigation

```csharp
var map = GridMap.FromRows(["....", ".##.", "...."]);       // # blocks, 2-9 weigh a cell, row 0 is y = 0
var pathfinder = new GridPathfinder(map);                    // reuse: it keeps its buffers
var path = new List<GridCell>();                             // reuse too
var result = pathfinder.FindPath(start, goal, path, new PathOptions(Diagonals: true));
if (!result.Found) { /* Unreachable, StartBlocked, GoalBlocked, OutOfBounds or SearchLimit */ }
```

Never an exception for "no route": check `result.Status`. Straight steps cost 10, diagonals 14, times the cell
weight; 8-direction paths never cut a blocked corner. `PathOptions.MaxExpansions` (0 = unlimited) bounds the work.
Map edits bump `GridMap.Version`. `CellCenter`/`CellAt` map to world 2D; `CellCenterXZ`/`CellAtXZ` walk a 3D game on
the X/Z plane. `map.HasLineOfSight(a, b)` is the perception test; `PathSmoothing.Simplify(map, path)` drops waypoints
on unweighted maps.

Movement: `Steering.Seek/Flee/Arrive` return a desired velocity; `SteeringAgent.Step(desired, dt)` caps acceleration
and speed (`MaxSpeed` is settable for walk and run); `PathFollower.Follow(map, path)` then `Step(dt)` per tick.

## Show what the NPC thinks

`machine.WriteSnapshot(values, "hound")` and `tree.WriteSnapshot(values, "sentry")`, `board.WriteSnapshot(values,
"sentry")` add numeric keys to `GameSnapshot.Values`: `hound.state.Chase` (0/1), `hound.stateTicks`, `sentry.bt.leaf`
(active node id), `sentry.bt.run.<id>.<Name>` (running node, value = depth), `sentry.bb.<slot>`. Read them with
`ukiyo_run` snapshots instead of guessing from pixels.

## Prove it

Test paths on small known maps (cost equals a reference Dijkstra, 4 vs 8 directions, unreachable status), run the game
twice and compare snapshots, and pin the sample with a golden. See `tests/Ukiyo.Tests/AiTests.cs`.
