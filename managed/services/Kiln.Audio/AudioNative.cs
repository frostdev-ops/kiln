// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_audio.h (kiln.audio v1). Layouts are checked by size at run time.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class AudioNative
{
    public const string Name = "kiln.audio";
    public const uint Version = 1;

    public const int Ok = 0, NotFound = 2, Unavailable = 3;

    // KilnAudioPlay.flags
    public const uint At = 1, Follow = 2, Verify = 4, MusicSlot = 8;

    // KilnAudioStatus.flags
    public const uint HasPlay = 1, HasVoiceover = 2, HasNarration = 4, System = 8, Manager = 16, Frames = 32, Probe = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(WorldPosition p) => (X, Y, Z) = (p.X, p.Y, p.Z);
        public readonly WorldPosition ToPosition() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Play
    {
        public uint Size, Flags;
        public fixed byte Cue[16];
        public uint Scene, Entity;
        public Vec3 Position;
        public float Volume, VerifyRadius;
        public long FadeInNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Size, State, Kind, Reason, Flags, Scene, Instance, Thread, Entity, LanguageSlot;
        public ulong QueuedMs, StartedMs, EndedMs;
        public Vec3 Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Flags;
        public fixed uint Scenes[7];
        public fixed uint Listeners[7];
        public uint MainThread, VoiceLanguage;
        public float VoiceVolume;
        public uint VoiceScene, LoreScene, LoreMode, LoreQueued, Live, GameThreads;
        public ulong Played, Failed, GameStarts, GameStartsOffMain;
        public nuint System;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, Play*, uint> Play;
        public delegate* unmanaged[Cdecl]<uint, long, int> Stop;
        public delegate* unmanaged[Cdecl]<uint, float, long, int> SetVolume;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> SetPosition;
        public delegate* unmanaged[Cdecl]<uint, uint> State;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> GetInfo;
        public delegate* unmanaged[Cdecl]<ulong, long, uint> StopOwner;
        public delegate* unmanaged[Cdecl]<ulong, byte*, uint, Vec3*, uint> VoiceoverPlay;
        public delegate* unmanaged[Cdecl]<long, int> VoiceoverStop;
        public delegate* unmanaged[Cdecl]<ulong, byte*, uint, uint, uint> Narrate;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> StatusJson;
    }

    private static Table* _table;
    private static int _looked;  // 0 not yet, 1 looked up

    /// <summary>The kiln.audio table, or null (dedicated server, unsupported build, kiln-audio.dll missing).</summary>
    public static Table* T
    {
        get
        {
            if (_table != null) return _table;
            if (Volatile.Read(ref _looked) != 0) return null;
            _table = Services.Get<Table>(Name, Version);
            Volatile.Write(ref _looked, 1);
            return _table;
        }
    }

    static AudioNative()
    {
        // fail loudly in development when the mirror drifts from the C header
        global::System.Diagnostics.Debug.Assert(sizeof(Play) == 72 && sizeof(Info) == 88 && sizeof(Status) == 144);
    }

    public static void WriteGuid(Guid g, byte* dst)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);  // .NET order = the game's resource-id order
        for (var i = 0; i < 16; i++) dst[i] = b[i];
    }
}
