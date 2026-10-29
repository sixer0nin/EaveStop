using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eavestop.Prototype
{
    internal static class Program
    {
        // ================= COM interop =================

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public short vt;
            [FieldOffset(8)] public IntPtr pointerValue;
        }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string n, IntPtr p);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.Bool)] bool b, IntPtr p);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string n);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string n, IntPtr a, IntPtr b);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.Bool)] bool b, IntPtr a, IntPtr c);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string n, IntPtr p);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string n, IntPtr p);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string n, IntPtr p);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.Bool)] bool b, ref PROPERTYKEY k, IntPtr pv);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.Bool)] bool b, ref PROPERTYKEY k, IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string n, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string n, [MarshalAs(UnmanagedType.Bool)] bool visible);
        }

        // ================= Constants =================

        private const int eCapture = 1;
        private const int STATE_ALL = 0xF;
        private const int STATE_ACTIVE = 0x1;
        private const int STATE_DISABLED = 0x2;
        private const int STATE_NOTPRESENT = 0x4;
        private const int STATE_UNPLUGGED = 0x8;
        private const int STGM_READ = 0;
        private const short VT_LPWSTR = 31;

        private static readonly PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        private static readonly Guid CLSID_PolicyConfigClient = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

        // ================= Device model =================

        private sealed class Device
        {
            public string Id;
            public string Name;
            public int State;
            public override string ToString() => $"[{Label(State),-10}] {Name}";
        }

        private static string Label(int s)
        {
            if ((s & STATE_ACTIVE) != 0) return "Active";
            if ((s & STATE_DISABLED) != 0) return "Disabled";
            if ((s & STATE_NOTPRESENT) != 0) return "NotPresent";
            if ((s & STATE_UNPLUGGED) != 0) return "Unplugged";
            return "Unknown";
        }

        // ================= Enumeration =================

        private static List<Device> Enumerate()
        {
            var list = new List<Device>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (en.EnumAudioEndpoints(eCapture, STATE_ALL, out var coll) != 0) return list;
                try
                {
                    coll.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        if (coll.Item(i, out var dev) != 0) continue;
                        try
                        {
                            dev.GetId(out string id);
                            dev.GetState(out int state);
                            string name = "";
                            if (dev.OpenPropertyStore(STGM_READ, out var ps) == 0 && ps != null)
                            {
                                try
                                {
                                    var key = PKEY_FriendlyName;
                                    if (ps.GetValue(ref key, out var pv) == 0)
                                    {
                                        if (pv.vt == VT_LPWSTR && pv.pointerValue != IntPtr.Zero)
                                            name = Marshal.PtrToStringUni(pv.pointerValue);
                                        if (pv.pointerValue != IntPtr.Zero)
                                            Marshal.FreeCoTaskMem(pv.pointerValue);
                                    }
                                }
                                finally { Marshal.ReleaseComObject(ps); }
                            }
                            list.Add(new Device { Id = id, Name = name, State = state });
                        }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(coll); }
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        // ================= Toggle =================

        private static int Toggle(bool disable)
        {
            var targets = new List<Device>();
            foreach (var d in Enumerate())
            {
                bool active = (d.State & STATE_ACTIVE) != 0;
                bool disabled = (d.State & STATE_DISABLED) != 0;
                if (disable && active) targets.Add(d);
                if (!disable && disabled) targets.Add(d);
            }
            if (targets.Count == 0) return 0;

            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient, true);
            var policy = (IPolicyConfig)Activator.CreateInstance(type);
            try
            {
                int ok = 0;
                foreach (var d in targets)
                {
                    int hr = policy.SetEndpointVisibility(d.Id, !disable);
                    if (hr == 0) ok++;
                    else Console.WriteLine($"  ! {d.Name}: HRESULT=0x{hr:X8}");
                }
                return ok;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }

        // ================= Main =================

        private static void Main()
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("Eavestop — Phase 1 prototype");
                Console.WriteLine("1) List capture devices");
                Console.WriteLine("2) Disable all microphones");
                Console.WriteLine("3) Enable all microphones");
                Console.WriteLine("4) Exit");
                Console.Write("> ");
                try
                {
                    switch (Console.ReadLine())
                    {
                        case "1": Dump(); break;
                        case "2": Console.WriteLine($"Disabled {Toggle(true)} device(s)."); Dump(); break;
                        case "3": Console.WriteLine($"Enabled  {Toggle(false)} device(s)."); Dump(); break;
                        case "4": return;
                    }
                }
                catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
            }
        }

        private static void Dump()
        {
            var d = Enumerate();
            if (d.Count == 0) { Console.WriteLine("  (none)"); return; }
            foreach (var x in d) Console.WriteLine("  " + x + "\n    " + x.Id);
        }
    }
}