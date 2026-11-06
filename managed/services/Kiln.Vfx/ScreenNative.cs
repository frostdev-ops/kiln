// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_screen.h (kiln.screen v1). Layouts are checked by size at run time.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class ScreenNative
{
    public const string Name = "kiln.screen";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Effect
    {
        public uint Size;
        public int Order;
        public float Strength, FadeIn, Seconds, FadeOut, Exposure, Contrast, Saturation, Gamma, Temperature;
        public fixed float Tint[4];
        public fixed float Vignette[4];
        public float VignetteRadius, VignetteSoftness;
        public fixed float Fill[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Enabled, Ready, Transfer, Reserved;
        public uint Playing, Format, Width, Height;
        public ulong Plays, Refused, Frames, Skipped;
        public double PipelineMs;
        public fixed byte Reason[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, Effect*, uint> Play;
        public delegate* unmanaged[Cdecl]<uint, int, int> Stop;
        public delegate* unmanaged[Cdecl]<uint, float, int> SetStrength;
        public delegate* unmanaged[Cdecl]<uint, int> IsPlaying;
        public delegate* unmanaged[Cdecl]<ulong, int, uint> StopOwner;
    }

    public static (int Effect, int Status, int Table) Sizes => (sizeof(Effect), sizeof(Status), sizeof(Table));

    private static Table* _table;
    private static int _looked;

    /// <summary>The kiln.screen table, or null (dedicated server, kiln-screen.dll missing, or off by kiln/config/screen.json).</summary>
    public static Table* T
    {
        get
        {
            if (_table != null) return _table;
            if (Volatile.Read(ref _looked) != 0) return null;
            var t = Services.Get<Table>(Name, Version);
            if (t != null && (t->Header.Size < (uint)sizeof(Table) || Sizes != (100, 192, 56)))
            {
                ScreenLog.Warn($"kiln.screen's table is {t->Header.Size} bytes and this Kiln.Vfx expects {sizeof(Table)}: screen effects are off");
                t = null;
            }
            _table = t;
            Volatile.Write(ref _looked, 1);
            return _table;
        }
    }

    /// <summary>What kiln_screen_effect_defaults gives, with the effect's fields over it. The effect has been checked.</summary>
    public static Effect From(ScreenEffect e)
    {
        var n = new Effect
        {
            Size = (uint)sizeof(Effect),
            Order = e.Order ?? 0,
            Strength = e.Strength ?? 1,
            FadeIn = e.FadeIn ?? 0,
            Seconds = e.Seconds ?? -1,
            FadeOut = e.FadeOut ?? 0,
            Exposure = e.Exposure ?? 0,
            Contrast = e.Contrast ?? 1,
            Saturation = e.Saturation ?? 1,
            Gamma = e.Gamma ?? 1,
            Temperature = e.Temperature ?? 0,
            VignetteRadius = e.Vignette?.Radius ?? 0.5f,
            VignetteSoftness = e.Vignette?.Softness ?? 0.5f,
        };
        Colour(n.Tint, e.Tint?.Color, e.Tint?.Amount ?? 0, (1, 1, 1));
        Colour(n.Vignette, e.Vignette?.Color, e.Vignette is { } v ? v.Amount ?? 1 : 0, (0, 0, 0));
        Colour(n.Fill, e.Fill?.Color, e.Fill?.Amount ?? 0, (0, 0, 0));
        return n;
    }

    private static void Colour(float* to, string? text, float amount, (float R, float G, float B) fallback)
    {
        var c = text != null && ScreenColor.TryParse(text, out var rgb) ? rgb : fallback;
        to[0] = c.R;
        to[1] = c.G;
        to[2] = c.B;
        to[3] = amount;
    }
}

internal static class ScreenLog
{
    public static void Info(string text) => Backend.Current.Log(0, LogLevel.Info, "screen: " + text);
    public static void Warn(string text) => Backend.Current.Log(0, LogLevel.Warn, "screen: " + text);
    public static void Debug(string text) => Backend.Current.Log(0, LogLevel.Debug, "screen: " + text);
}
