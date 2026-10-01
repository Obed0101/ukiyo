// Linked into development hosts only (UkiyoDevBridge=true in Directory.Build.targets). The module initializer runs
// before Program.cs, so the shared entry point stays identical on every target and exports carry no bridge.
using System.Runtime.CompilerServices;

namespace Ukiyo.DevBridge;

internal static class DevBridgeRegistration
{
    [ModuleInitializer]
    internal static void Register() => DevHooks.Factory = DevBridgeServer.TryStart;
}
