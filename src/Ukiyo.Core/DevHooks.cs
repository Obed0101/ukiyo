namespace Ukiyo;

/// <summary>A live development session attached to a running game (for example the dev bridge). Pumped on the host thread.</summary>
public interface IDevSession : IDisposable
{
    /// <summary>Runs queued requests on the thread that owns the runtime and renderer. Hosts call it once per frame.</summary>
    void Pump();
}

/// <summary>
/// Where development tooling plugs into hosts without being compiled into exports. A dev host links a registration file
/// (see Directory.Build.targets, <c>UkiyoDevBridge=true</c>) whose module initializer sets <see cref="Factory"/>. Export
/// profiles never link it, so a shipped game carries no listener, socket or agent code.
/// </summary>
public static class DevHooks
{
    public static Func<GameRuntime, string, IDevSession?>? Factory { get; set; }

    public static IDevSession? Start(GameRuntime runtime, string target) => Factory?.Invoke(runtime, target);
}
