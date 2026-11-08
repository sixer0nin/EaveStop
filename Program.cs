using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Eavestop
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(args));
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        // ===== Names / paths =====
        private const string AppName       = "Eavestop";
        private const string RunKeyPath    = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string SettingsPath  = @"Software\Eavestop";

        private const string CmdMic    = "--toggle-mic";
        private const string CmdCamera = "--toggle-camera";
        private const string CmdBoth   = "--toggle-both";

        private enum Mode   { Mic, Camera }
        private enum Action { ToggleMic, ToggleCamera, ToggleBoth }

        // ===== State =====
        private NotifyIcon _tray;
        private Mode _mode;
        private bool _elevated;
        private Icon _micIcon, _cameraIcon, _bothOffIcon;

        // ================= COM interop (Phase 1, proven) =================

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

        private const int eCapture = 1;
        private const int STATE_ALL = 0xF, STATE_ACTIVE = 0x1, STATE_DISABLED = 0x2;
        private const int STGM_READ = 0;
        private const short VT_LPWSTR = 31;

        private static readonly PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY
        { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        private static readonly Guid CLSID_PolicyConfigClient = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

        // ================= Constructor =================

        public TrayContext(string[] args)
        {
            _elevated    = IsElevated();
            _mode        = LoadMode();
            _micIcon     = LoadIcon("mic.ico");
            _cameraIcon  = LoadIcon("camera.ico");
            _bothOffIcon = LoadIcon("both-off.ico");

            _tray = new NotifyIcon
            {
                Visible = true,
                Text = AppName,
                Icon = CurrentIcon(),
                ContextMenuStrip = BuildMenu()
            };
            _tray.MouseClick += OnTrayClick;

            // Post-elevation: perform the action the unelevated instance requested
            Action? pending = ParseAction(args);
            if (pending.HasValue)
            {
                var timer = new Timer { Interval = 150 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop(); timer.Dispose();
                    RunToggle(pending.Value);
                };
                timer.Start();
            }
        }

        private static Action? ParseAction(string[] args)
        {
            foreach (var a in args)
            {
                if (a == CmdMic)    return Action.ToggleMic;
                if (a == CmdCamera) return Action.ToggleCamera;
                if (a == CmdBoth)   return Action.ToggleBoth;
            }
            return null;
        }

        private Icon CurrentIcon() => _mode == Mode.Mic ? _micIcon : _cameraIcon;

        // ================= Menu =================

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var switchItem = new ToolStripMenuItem();
            switchItem.Click += (s, e) =>
            {
                _mode = _mode == Mode.Mic ? Mode.Camera : Mode.Mic;
                SaveMode(_mode);
                _tray.Icon = CurrentIcon();
            };
            menu.Items.Add(switchItem);

            var bothItem = new ToolStripMenuItem();
            bothItem.Click += (s, e) => Dispatch(Action.ToggleBoth);
            menu.Items.Add(bothItem);

            menu.Items.Add(new ToolStripSeparator());

            var refreshItem = new ToolStripMenuItem("Refresh devices");
            refreshItem.Click += (s, e) =>
            {
                int m = EnumerateMics().Count;
                int c = EnumerateCameras().Count;
                ShowBalloon($"{m} microphone(s), {c} camera(s) discovered.");
            };
            menu.Items.Add(refreshItem);

            var autostartItem = new ToolStripMenuItem("Start with Windows");
            autostartItem.Click += (s, e) =>
            {
                bool newState = !IsAutostartEnabled();
                SetAutostart(newState);
                autostartItem.Checked = newState;
            };
            menu.Items.Add(autostartItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => { _tray.Visible = false; ExitThread(); };
            menu.Items.Add(exitItem);

            menu.Opening += (s, e) =>
            {
                switchItem.Text = _mode == Mode.Mic ? "Switch to Camera mode" : "Switch to Mic mode";
                autostartItem.Checked = IsAutostartEnabled();

                // Determine label for the "both" toggle without much latency:
                // mics are a cheap COM call; cameras use WMI which is still fast enough on menu open.
                bool micActive = EnumerateMics().Exists(d => (d.State & STATE_ACTIVE) != 0);
                bool camActive = EnumerateCameras().Exists(c => !c.Disabled);
                bothItem.Text  = (micActive || camActive) ? "Turn both off" : "Turn both on";
            };

            return menu;
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                Dispatch(_mode == Mode.Mic ? Action.ToggleMic : Action.ToggleCamera);
        }

        // ================= Elevation handoff =================

        private void Dispatch(Action action)
        {
            if (NeedsElevationHandoff(action)) return;
            RunToggle(action);
        }

        private bool NeedsElevationHandoff(Action action)
        {
            if (_elevated) return false;

            var arg = action == Action.ToggleMic    ? CmdMic
                    : action == Action.ToggleCamera ? CmdCamera
                    : CmdBoth;

            var psi = new ProcessStartInfo
            {
                FileName        = Application.ExecutablePath,
                Arguments       = arg,
                Verb            = "runas",
                UseShellExecute = true
            };
            try
            {
                Process.Start(psi);
                _tray.Visible = false;
                ExitThread();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User cancelled UAC — stay running, do nothing
            }
            return true;
        }

        private static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ================= Toggles =================

        private void RunToggle(Action action)
        {
            try
            {
                switch (action)
                {
                    case Action.ToggleMic:
                    {
                        var mics = EnumerateMics();
                        if (mics.Count == 0) { ShowBalloon("No microphones found."); return; }
                        bool anyActive = mics.Exists(d => (d.State & STATE_ACTIVE) != 0);
                        int n = ToggleMics(disable: anyActive);
                        ShowBalloon(anyActive ? $"Disabled {n} microphone(s)." : $"Enabled {n} microphone(s).");
                        break;
                    }
                    case Action.ToggleCamera:
                    {
                        var cams = EnumerateCameras();
                        if (cams.Count == 0) { ShowBalloon("No cameras found."); return; }
                        bool anyEnabled = cams.Exists(c => !c.Disabled);
                        int n = ToggleCameras(disable: anyEnabled);
                        ShowBalloon(anyEnabled ? $"Disabled {n} camera(s)." : $"Enabled {n} camera(s).");
                        break;
                    }
                    case Action.ToggleBoth:
                    {
                        var mics = EnumerateMics();
                        var cams = EnumerateCameras();
                        bool micAny = mics.Exists(d => (d.State & STATE_ACTIVE) != 0);
                        bool camAny = cams.Exists(c => !c.Disabled);
                        if (micAny || camAny)
                        {
                            int mn = micAny ? ToggleMics(true)  : 0;
                            int cn = camAny ? ToggleCameras(true) : 0;
                            FlashBothOff();
                            ShowBalloon($"Disabled {mn} mic(s) + {cn} camera(s).");
                        }
                        else
                        {
                            int mn = mics.Count > 0 ? ToggleMics(false)    : 0;
                            int cn = cams.Count > 0 ? ToggleCameras(false) : 0;
                            ShowBalloon($"Enabled {mn} mic(s) + {cn} camera(s).");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) { ShowBalloon("Error: " + ex.Message); }
        }

        // ================= Microphones =================

        private sealed class MicDevice { public string Id, Name; public int State; }

        private static List<MicDevice> EnumerateMics()
        {
            var list = new List<MicDevice>();
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
                            list.Add(new MicDevice { Id = id, Name = name, State = state });
                        }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(coll); }
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        private static int ToggleMics(bool disable)
        {
            var targets = new List<MicDevice>();
            foreach (var d in EnumerateMics())
            {
                bool active   = (d.State & STATE_ACTIVE)   != 0;
                bool disabled = (d.State & STATE_DISABLED) != 0;
                if (disable && active)     targets.Add(d);
                if (!disable && disabled)  targets.Add(d);
            }
            if (targets.Count == 0) return 0;

            var type   = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient, true);
            var policy = (IPolicyConfig)Activator.CreateInstance(type);
            try
            {
                int ok = 0;
                foreach (var d in targets)
                    if (policy.SetEndpointVisibility(d.Id, !disable) == 0) ok++;
                return ok;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }

        // ================= Cameras =================

        private sealed class CameraDevice { public string InstanceId, Name; public bool Disabled; }

        private static List<CameraDevice> EnumerateCameras()
        {
            var list = new List<CameraDevice>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE PNPClass='Camera' OR PNPClass='Image'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var id = obj["DeviceID"]?.ToString();
                        if (string.IsNullOrEmpty(id)) continue;
                        int err = 0;
                        try { err = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0); } catch { }
                        list.Add(new CameraDevice
                        {
                            InstanceId = id,
                            Name       = obj["Name"]?.ToString() ?? "(camera)",
                            Disabled   = err == 22   // 22 = device disabled
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        private static int ToggleCameras(bool disable)
        {
            var targets = new List<CameraDevice>();
            foreach (var c in EnumerateCameras())
            {
                if (disable && !c.Disabled) targets.Add(c);
                if (!disable && c.Disabled) targets.Add(c);
            }
            if (targets.Count == 0) return 0;

            int ok = 0;
            foreach (var c in targets)
            {
                var verb = disable ? "/disable-device" : "/enable-device";
                if (RunPnpUtil($"{verb} \"{c.InstanceId}\"") == 0) ok++;
            }
            return ok;
        }

        private static int RunPnpUtil(string args, int timeoutMs = 5000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "pnputil.exe",
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // ================= Icons / balloon =================

        private void FlashBothOff()
        {
            if (_bothOffIcon == null) return;
            _tray.Icon = _bothOffIcon;
            var t = new Timer { Interval = 2500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (_tray != null) _tray.Icon = CurrentIcon(); };
            t.Start();
        }

        private static Icon LoadIcon(string file)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", file);
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return SystemIcons.Application;
        }

        private void ShowBalloon(string text)
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = AppName;
            _tray.BalloonTipText  = text;
            _tray.ShowBalloonTip(2000);
        }

        // ================= Settings (registry) =================

        private static Mode LoadMode()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsPath))
                    return (k?.GetValue("Mode") as string) == "Camera" ? Mode.Camera : Mode.Mic;
            }
            catch { return Mode.Mic; }
        }

        private static void SaveMode(Mode m)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsPath))
                    k?.SetValue("Mode", m.ToString());
            }
            catch { }
        }

        private static bool IsAutostartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return k?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private static void SetAutostart(bool enabled)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (enabled) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else         k.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        // ================= Cleanup =================

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            base.Dispose(disposing);
        }
    }
}