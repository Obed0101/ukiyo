using System.Runtime.InteropServices;

namespace Ukiyo.Hosting;

// Minimal SDL 3.4.16 surface used by the desktop host (constants verified against the pinned headers with
// clang; see scripts/fetch-native.sh). Dev builds load libSDL3.dylib; NativeAOT links libSDL3.a statically.
internal static unsafe partial class Sdl3
{
    private const string Library = "SDL3";

    public const uint InitVideo = 0x20;
    public const ulong WindowResizable = 0x20;
    public const ulong WindowHighPixelDensity = 0x2000;
    public const ulong WindowMetal = 0x20000000;
    public const uint EventQuit = 0x100;
    public const uint EventWindowPixelSizeChanged = 0x207;
    public const uint EventWindowCloseRequested = 0x210;
    public const uint EventKeyDown = 0x300;
    public const uint KeyEscape = 0x1B;
    public const uint KeySpace = 0x20;
    public const uint KeyP = 0x70;
    public const uint KeyS = 0x73;

    /// <summary>SDL_Event is a 128-byte union. Keyboard events keep the keycode at offset 28 and the repeat flag at 37.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(28)] public uint Key;
        [FieldOffset(37)] public byte Repeat;
    }

    [LibraryImport(Library)] public static partial byte SDL_Init(uint flags);
    [LibraryImport(Library)] public static partial void SDL_Quit();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] public static partial nint SDL_CreateWindow(string title, int width, int height, ulong flags);
    [LibraryImport(Library)] public static partial void SDL_DestroyWindow(nint window);
    [LibraryImport(Library)] public static partial byte SDL_SetWindowSize(nint window, int width, int height);
    [LibraryImport(Library)] public static partial byte SDL_SyncWindow(nint window);
    [LibraryImport(Library)] public static partial byte SDL_GetWindowSizeInPixels(nint window, int* width, int* height);
    [LibraryImport(Library)] public static partial byte SDL_PollEvent(Event* sdlEvent);
    [LibraryImport(Library)] public static partial nint SDL_Metal_CreateView(nint window);
    [LibraryImport(Library)] public static partial void SDL_Metal_DestroyView(nint view);
    [LibraryImport(Library)] public static partial nint SDL_Metal_GetLayer(nint view);
    [LibraryImport(Library)] private static partial byte* SDL_GetError();

    public static string LastError => Marshal.PtrToStringUTF8((nint)SDL_GetError()) ?? "unknown SDL error";
}
