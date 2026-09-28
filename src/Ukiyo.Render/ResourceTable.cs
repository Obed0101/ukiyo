namespace Ukiyo.Rendering;

/// <summary>
/// Generational slot table shared by renderers. Destroying a slot bumps its generation, so an old handle
/// can never silently resolve to a newer resource.
/// </summary>
public sealed class ResourceTable<T> where T : class
{
    private readonly List<Slot> _slots = [];

    public int Count { get; private set; }

    public void Add(ResourceHandle handle, T value)
    {
        if (!handle.IsValid)
        {
            throw new RenderException(RenderErrorCode.InvalidPacket, $"handle {handle} has generation 0");
        }

        while (_slots.Count <= handle.Index)
        {
            _slots.Add(new Slot(0, null));
        }

        var slot = _slots[(int)handle.Index];
        if (slot.Value is not null)
        {
            throw new RenderException(RenderErrorCode.DuplicateHandle, $"slot {handle.Index} already holds generation {slot.Generation}");
        }

        if (handle.Generation <= slot.Generation)
        {
            throw new RenderException(RenderErrorCode.StaleHandle, $"{handle} is not newer than generation {slot.Generation}");
        }

        _slots[(int)handle.Index] = new Slot(handle.Generation, value);
        Count++;
    }

    public T Get(ResourceHandle handle)
    {
        if (handle.Index >= _slots.Count)
        {
            throw new RenderException(RenderErrorCode.UnknownHandle, $"{handle} was never created");
        }

        var slot = _slots[(int)handle.Index];
        if (slot.Generation != handle.Generation || slot.Value is null)
        {
            throw new RenderException(RenderErrorCode.StaleHandle, $"{handle} is stale (slot generation {slot.Generation}, live={slot.Value is not null})");
        }

        return slot.Value;
    }

    public T Remove(ResourceHandle handle)
    {
        var value = Get(handle);
        _slots[(int)handle.Index] = new Slot(handle.Generation, null);
        Count--;
        return value;
    }

    public IEnumerable<T> Values => _slots.Where(slot => slot.Value is not null).Select(slot => slot.Value!);

    private readonly record struct Slot(uint Generation, T? Value);
}

/// <summary>Allocates handles on the game side. Freed indices are reused with a higher generation.</summary>
public sealed class HandleAllocator
{
    private readonly Dictionary<ResourceKind, (uint Next, Stack<(uint Index, uint Generation)> Free)> _state = [];

    public ResourceHandle Allocate(ResourceKind kind)
    {
        if (!_state.TryGetValue(kind, out var state))
        {
            state = (0, new Stack<(uint, uint)>());
        }

        ResourceHandle handle;
        if (state.Free.TryPop(out var reused))
        {
            handle = new ResourceHandle(kind, reused.Index, reused.Generation + 1);
        }
        else
        {
            handle = new ResourceHandle(kind, state.Next, 1);
            state.Next++;
        }

        _state[kind] = state;
        return handle;
    }

    public void Release(ResourceHandle handle)
    {
        if (!_state.TryGetValue(handle.Kind, out var state))
        {
            throw new RenderException(RenderErrorCode.UnknownHandle, $"{handle} was not allocated here");
        }

        state.Free.Push((handle.Index, handle.Generation));
    }
}
