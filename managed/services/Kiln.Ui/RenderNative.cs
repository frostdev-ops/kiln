// SPDX-License-Identifier: MIT
// kiln.render (native/core/abi/services/kiln_render.h): drawing on the overlay's background list and the game's camera.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class RenderNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Camera
    {
        public uint Size;
        public byte Valid, Available, R0, R1;
        public double X, Y, Z;
        public float Qx, Qy, Qz, Qw;
        public float FovY, Aspect, Near, Far, Distance, R2;
        public ulong Frame;
        public uint AgeMs, R3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Camera, CameraValid, R0, R1;
        public uint FrameHolders;
        public ulong CameraFrames, Draws;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, ulong*, int> AcquireFrames;
        public delegate* unmanaged[Cdecl]<ulong, int> Release;
        public delegate* unmanaged[Cdecl]<Camera*, int> GetCamera;
        public delegate* unmanaged[Cdecl]<double*, float, float, float*, byte> WorldToScreen;
        public delegate* unmanaged[Cdecl]<float, float, float, float, uint, float, void> Line;
        public delegate* unmanaged[Cdecl]<float, float, float, float, uint, float, float, int, void> Rect;
        public delegate* unmanaged[Cdecl]<float, float, float, uint, int, float, int, void> Circle;
        public delegate* unmanaged[Cdecl]<float, float, float, float, float, float, uint, float, int, void> Triangle;
        public delegate* unmanaged[Cdecl]<float*, uint, uint, float, int, int, void> Polyline;
        public delegate* unmanaged[Cdecl]<float, float, uint, float, uint, NativeStr, void> Text;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float*, float*, void> TextSize;
        public delegate* unmanaged[Cdecl]<ulong, float, float, float, float, float, float, float, float, uint, void> Image;
    }

    // Publish the completed lookup once; another thread must never observe "tried" before the pointer.
    private static readonly Lazy<nint> TableAddress = new(() => (nint)Services.Get<Table>("kiln.render", 1));
    public static Table* T => (Table*)TableAddress.Value;

    public static bool Available => T != null;

    public static Camera ReadCamera()
    {
        Camera c = default;
        c.Size = (uint)sizeof(Camera);
        if (T != null) T->GetCamera(&c);
        return c;
    }

    public static Status? ReadStatus()
    {
        if (T == null) return null;
        Status s = default;
        s.Size = (uint)sizeof(Status);
        return T->GetStatus(&s) == 0 ? s : null;
    }
}
