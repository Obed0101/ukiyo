namespace Ukiyo.AI;

/// <summary>A transition out of a state: taken on the first tick its guard is true, in declaration order.</summary>
public sealed record Transition<TState>(TState To, Func<bool> Guard) where TState : struct, Enum;

/// <summary>Callbacks and transitions of one state. Built with the fluent methods; read-only once the machine runs.</summary>
public sealed class StateDefinition<TState> where TState : struct, Enum
{
    internal StateDefinition(TState id)
    {
        Id = id;
    }

    public TState Id { get; }

    internal Action? Enter { get; private set; }

    internal Action? Exit { get; private set; }

    internal Action? TickAction { get; private set; }

    internal List<Transition<TState>> Transitions { get; } = [];

    public StateDefinition<TState> OnEnter(Action action)
    {
        Enter = action;
        return this;
    }

    public StateDefinition<TState> OnExit(Action action)
    {
        Exit = action;
        return this;
    }

    public StateDefinition<TState> OnTick(Action action)
    {
        TickAction = action;
        return this;
    }

    /// <summary>Goes to <paramref name="to"/> when <paramref name="guard"/> holds. Earlier transitions win ties.</summary>
    public StateDefinition<TState> When(Func<bool> guard, TState to)
    {
        ArgumentNullException.ThrowIfNull(guard);
        Transitions.Add(new Transition<TState>(to, guard));
        return this;
    }
}

/// <summary>
/// Finite state machine over an enum. Per tick: the current state's transitions are checked in order; the first true
/// guard runs exit, switches and runs enter (one transition per tick, so a chain of guards cannot spin); otherwise the
/// state's tick action runs. Callbacks are delegates created at setup, so ticking allocates nothing. The state is a
/// plain index, <see cref="WriteSnapshot"/> publishes it so agents can read what the NPC is doing.
/// </summary>
public sealed class StateMachine<TState> where TState : struct, Enum
{
    private readonly List<StateDefinition<TState>> _states = [];
    private int _current = -1;

    public bool Started => _current >= 0;

    public TState Current => _current < 0 ? throw new InvalidOperationException("[AI]: the state machine has not started") : _states[_current].Id;

    /// <summary>Ticks spent in the current state, 0 on the tick it was entered.</summary>
    public long TicksInState { get; private set; }

    /// <summary>Number of transitions taken since <see cref="Start"/>.</summary>
    public long Transitions { get; private set; }

    /// <summary>Registers a state; each enum value once, in an order that is also the snapshot order.</summary>
    public StateDefinition<TState> State(TState id)
    {
        if (_current >= 0)
        {
            throw new InvalidOperationException("[AI]: states cannot be added after the machine started");
        }

        if (IndexOf(id) >= 0)
        {
            throw new ArgumentException($"[AI]: state {id} is already defined", nameof(id));
        }

        var definition = new StateDefinition<TState>(id);
        _states.Add(definition);
        return definition;
    }

    /// <summary>Validates every transition target, then enters <paramref name="initial"/>.</summary>
    public void Start(TState initial)
    {
        foreach (var state in _states)
        {
            foreach (var transition in state.Transitions)
            {
                if (IndexOf(transition.To) < 0)
                {
                    throw new InvalidOperationException($"[AI]: state {state.Id} transitions to undefined state {transition.To}");
                }
            }
        }

        var index = IndexOf(initial);
        if (index < 0)
        {
            throw new ArgumentException($"[AI]: initial state {initial} is not defined", nameof(initial));
        }

        _current = index;
        TicksInState = 0;
        Transitions = 0;
        _states[index].Enter?.Invoke();
    }

    public void Tick()
    {
        if (_current < 0)
        {
            throw new InvalidOperationException("[AI]: the state machine has not started");
        }

        var state = _states[_current];
        foreach (var transition in state.Transitions)
        {
            if (transition.Guard())
            {
                GoTo(transition.To);
                return;
            }
        }

        TicksInState++;
        state.TickAction?.Invoke();
    }

    /// <summary>Forces a transition (exit, switch, enter), for events the guards cannot see. Going to the current state re-enters it.</summary>
    public void GoTo(TState next)
    {
        var index = IndexOf(next);
        if (index < 0)
        {
            throw new ArgumentException($"[AI]: state {next} is not defined", nameof(next));
        }

        _states[_current].Exit?.Invoke();
        _current = index;
        TicksInState = 0;
        Transitions++;
        _states[index].Enter?.Invoke();
    }

    /// <summary>
    /// Writes <c>prefix.state</c> (index in registration order) and one <c>prefix.state.Name</c> per state (1 for the
    /// current one, 0 otherwise), so a snapshot says what the NPC is in without a legend.
    /// </summary>
    public void WriteSnapshot(IDictionary<string, double> values, string prefix)
    {
        values[$"{prefix}.state"] = _current;
        for (var i = 0; i < _states.Count; i++)
        {
            values[$"{prefix}.state.{_states[i].Id}"] = i == _current ? 1d : 0d;
        }

        values[$"{prefix}.stateTicks"] = TicksInState;
    }

    private int IndexOf(TState id)
    {
        for (var i = 0; i < _states.Count; i++)
        {
            if (EqualityComparer<TState>.Default.Equals(_states[i].Id, id))
            {
                return i;
            }
        }

        return -1;
    }
}
