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
    public const uint EventWindowFocusLost = 0x20F;
    public const uint EventKeyDown = 0x300;
    public const uint EventKeyUp = 0x301;
    public const uint EventMouseMotion = 0x400;
    public const uint EventMouseButtonDown = 0x401;
    public const uint EventMouseButtonUp = 0x402;

    // SDL3 keycodes: printable keys are their ASCII value; the rest are scancode | 0x40000000.
    public const uint KeyEscape = 0x1B;
    public const uint KeySpace = 0x20;
    public const uint KeyReturn = 0x0D;
    public const uint KeyTab = 0x09;
    public const uint KeyBackspace = 0x08;
    public const uint KeyF1 = 0x4000003A;
    public const uint KeyF5 = 0x4000003E;
    public const uint KeyF6 = 0x4000003F;
    public const uint KeyF12 = 0x40000045;
    public const uint KeyRight = 0x4000004F;
    public const uint KeyLeft = 0x40000050;
    public const uint KeyDown = 0x40000051;
    public const uint KeyUp = 0x40000052;
    public const uint KeyLeftCtrl = 0x400000E0;
    public const uint KeyLeftShift = 0x400000E1;
    public const uint KeyLeftAlt = 0x400000E2;
    public const uint KeyRightCtrl = 0x400000E4;
    public const uint KeyRightShift = 0x400000E5;
    public const uint KeyRightAlt = 0x400000E6;
    public const byte ButtonLeft = 1;
    public const byte ButtonMiddle = 2;
    public const byte ButtonRight = 3;

    /// <summary>
    /// SDL_Event is a 128-byte union. Keyboard: keycode at 28, repeat at 37. Mouse motion and button events: button at 24,
    /// x and y (window coordinates, float) at 28 and 32.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(24)] public byte Button;
        [FieldOffset(28)] public uint Key;
        [FieldOffset(28)] public float MouseX;
        [FieldOffset(32)] public float MouseY;
        [FieldOffset(37)] public byte Repeat;
    }

    [LibraryImport(Library)] public static partial byte SDL_Init(uint flags);
    [LibraryImport(Library)] public static partial void SDL_Quit();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] public static partial nint SDL_CreateWindow(string title, int width, int height, ulong flags);
    [LibraryImport(Library)] public static partial void SDL_DestroyWindow(nint window);
    [LibraryImport(Library)] public static partial byte SDL_SetWindowSize(nint window, int width, int height);
    [LibraryImport(Library)] public static partial byte SDL_SyncWindow(nint window);
    [LibraryImport(Library)] public static partial byte SDL_RaiseWindow(nint window);
    [LibraryImport(Library)] public static partial byte SDL_GetWindowSizeInPixels(nint window, int* width, int* height);
    [LibraryImport(Library)] public static partial float SDL_GetWindowPixelDensity(nint window);
    [LibraryImport(Library)] public static partial byte SDL_PollEvent(Event* sdlEvent);
    [LibraryImport(Library)] public static partial nint SDL_Metal_CreateView(nint window);
    [LibraryImport(Library)] public static partial void SDL_Metal_DestroyView(nint view);
    [LibraryImport(Library)] public static partial nint SDL_Metal_GetLayer(nint view);
    [LibraryImport(Library)] private static partial byte* SDL_GetError();

    // Audio (SDL_audio.h). Push mode: the host mixes in C# and queues float stereo on a device stream.
    public const uint InitAudio = 0x10;
    public const uint AudioDeviceDefaultPlayback = 0xFFFFFFFF;
    public const int AudioF32LE = 0x8120;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioSpec
    {
        public int Format;
        public int Channels;
        public int Freq;
    }

    [LibraryImport(Library)] public static partial byte SDL_InitSubSystem(uint flags);
    [LibraryImport(Library)] public static partial nint SDL_OpenAudioDeviceStream(uint device, AudioSpec* spec, nint callback, nint userdata);
    [LibraryImport(Library)] public static partial byte SDL_ResumeAudioStreamDevice(nint stream);
    [LibraryImport(Library)] public static partial byte SDL_PutAudioStreamData(nint stream, void* data, int length);
    [LibraryImport(Library)] public static partial int SDL_GetAudioStreamQueued(nint stream);
    [LibraryImport(Library)] public static partial void SDL_DestroyAudioStream(nint stream);

    public static string LastError => Marshal.PtrToStringUTF8((nint)SDL_GetError()) ?? "unknown SDL error";
}
