// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Runtime.InteropServices;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// Calls into COM objects through their function tables. The audio interfaces are
/// used from worker threads, and Media Foundation and WASAPI objects are free
/// threaded, so no runtime callable wrappers (and no apartments) are involved.
/// </summary>
internal static unsafe class NativeCom
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    /// <summary>The function table of a COM object.</summary>
    public static void** Table(IntPtr obj) => *(void***)obj;

    public static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        IntPtr ptr;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Table(obj)[0])(obj, &iid, &ptr);
        result = hr >= 0 ? ptr : IntPtr.Zero;
        return hr;
    }

    public static void AddRef(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(obj)[1])(obj);
    }

    /// <summary>Releases the object and clears the reference.</summary>
    public static void Release(ref IntPtr obj)
    {
        var ptr = obj;
        obj = IntPtr.Zero;
        if (ptr != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(ptr)[2])(ptr);
    }

    /// <summary>Throws for a failed HRESULT.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new AudioOutputException(what, hr);
    }

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int context, in Guid iid, out IntPtr result);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr value);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    /// <summary>A PROPVARIANT in unmanaged memory (24 bytes on 64-bit, 16 on 32-bit).</summary>
    public sealed class PropVariant : IDisposable
    {
        public const ushort VT_EMPTY = 0;
        public const ushort VT_I8 = 20;
        public const ushort VT_UI4 = 19;
        public const ushort VT_UI8 = 21;
        public const ushort VT_LPWSTR = 31;
        public const ushort VT_BLOB = 65;

        public PropVariant()
        {
            Pointer = Marshal.AllocCoTaskMem(24);
            new Span<byte>((void*)Pointer, 24).Clear();
        }

        public IntPtr Pointer { get; private set; }

        public ushort Type => (ushort)Marshal.ReadInt16(Pointer);

        public long Int64 => Marshal.ReadInt64(Pointer, 8);

        public string? String => Type == VT_LPWSTR ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(Pointer, 8)) : null;

        /// <summary>A VT_BLOB's bytes: its size, then (aligned) a pointer to them.</summary>
        public byte[]? Blob
        {
            get
            {
                if (Type != VT_BLOB)
                    return null;

                int size = Marshal.ReadInt32(Pointer, 8);
                var data = Marshal.ReadIntPtr(Pointer, 8 + IntPtr.Size);
                if (size <= 0 || data == IntPtr.Zero)
                    return null;

                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);
                return bytes;
            }
        }

        public void SetInt64(long value)
        {
            Clear();
            Marshal.WriteInt16(Pointer, (short)VT_I8);
            Marshal.WriteInt64(Pointer, 8, value);
        }

        public void Clear()
        {
            if (Pointer != IntPtr.Zero)
                PropVariantClear(Pointer);
        }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
                return;

            Clear();
            Marshal.FreeCoTaskMem(Pointer);
            Pointer = IntPtr.Zero;
        }
    }
}
