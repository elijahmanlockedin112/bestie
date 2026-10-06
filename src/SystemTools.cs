// SystemTools.cs - small things about Bestie itself and the computer that came up in real use: "pause your
// notifications for an hour" (quiet_mode), and "I can't hear spotify" when Windows was playing everything
// through a virtual device (sound_output: see and switch the speakers or headphones it plays through).
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        void AddSystemTools(List<object> t)
        {
            t.Add(Tool("quiet_mode", "pause your own notifications (do not disturb): while it's on you don't talk out loud on your own; reminders still show silently "
                + "in your bubble, and \"x finished\" notices wait and come as one summary afterwards. use it when they want you to stop going off, snooze, or "
                + "be quiet for a while; minutes 0 turns it off.", R("minutes"),
                P("minutes", "number", "how long, e.g. 60 for an hour. 0 = talk again now")));
            t.Add(Tool("sound_output", "see or change which speakers or headphones the computer plays sound through, with the volume and mute state. use it first "
                + "whenever they can't hear something (it's often playing through the wrong or a virtual device) or want sound on other speakers or headphones.", R("action"),
                PE("action", "list the outputs, or switch the default output", new[] { "list", "set" }),
                P("device", "string", "for set: part of the device name, as list showed it, e.g. 'realtek' or 'headphones'")));
        }

        AssistantToolResult SystemTool(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "quiet_mode":
                {
                    int minutes = (int)Json.Num(a, "minutes", 60);
                    if (DryRun) { lock (DryRunLog) DryRunLog.Add("quiet " + minutes); return new AssistantToolResult("ok."); }
                    if (minutes <= 0)
                    {
                        Quiet.For(0);
                        string held = Quiet.TakeSummary(true);
                        return new AssistantToolResult("quiet time is off; you talk again." + (held != null ? " tell them what waited: " + held : ""), "quiet off", null);
                    }
                    Quiet.For(minutes);
                    return new AssistantToolResult("quiet until " + When.Clock(Quiet.Until) + ": you won't talk out loud on your own; reminders still show silently in your "
                        + "bubble, and finished sessions wait for a summary at the end. they can also right-click you → \"you can talk again\". tell them in a few words.",
                        "quiet until " + When.Clock(Quiet.Until), null);
                }
                case "sound_output":
                    return SoundOutputTool(Json.Str(a, "action") ?? "list", Json.Str(a, "device") ?? "");
            }
            return null;
        }

        AssistantToolResult SoundOutputTool(string action, string device)
        {
            List<SoundOutput.Device> all;
            try { all = SoundOutput.List(); }
            catch (Exception ex) { DebugLog.Write("sound: list: " + ex.Message); return new AssistantToolResult("couldn't read the sound devices (" + ex.Message + "). use use_computer to check sound settings."); }
            if (all.Count == 0) return new AssistantToolResult("windows reports no active speakers or headphones at all (unplugged, or disabled in sound settings).");
            if (action != "set")
            {
                var sb = new StringBuilder("sound outputs (the default is what apps like spotify play through unless set otherwise):\n");
                foreach (var d in all.OrderByDescending(d => d.Default))
                    sb.Append(d.Default ? "- DEFAULT: " : "- ").Append(d.Name).Append(d.Volume >= 0 ? ", volume " + d.Volume + "%" : "").Append(d.Muted ? ", MUTED" : "")
                      .Append(LooksVirtual(d.Name) ? " (a virtual device: no real speaker plays this)" : "").Append('\n');
                sb.Append("if the default is a virtual device, muted, or not what they're listening on, switch it with action set (it's quick and easy to undo).");
                return new AssistantToolResult(sb.ToString());
            }
            string want = device.Trim().ToLowerInvariant();
            if (want.Length == 0) return new AssistantToolResult("set needs a device name: " + string.Join(", ", all.Select(d => d.Name).ToArray()) + ".");
            SoundOutput.Device hit = all.FirstOrDefault(d => d.Name.ToLowerInvariant() == want)
                ?? all.FirstOrDefault(d => d.Name.ToLowerInvariant().Contains(want))
                ?? all.FirstOrDefault(d => want.Split(' ').Where(w => w.Length > 2).All(w => d.Name.ToLowerInvariant().Contains(w)));
            if (hit == null) return new AssistantToolResult("no output called \"" + device + "\". the outputs are: " + string.Join(", ", all.Select(d => d.Name).ToArray()) + ".");
            if (hit.Default) return new AssistantToolResult(hit.Name + " is already the default output" + (hit.Muted ? ", but it's MUTED" : "") + (hit.Volume >= 0 ? " (volume " + hit.Volume + "%)" : "") + ".");
            if (DryRun) { lock (DryRunLog) DryRunLog.Add("sound " + hit.Name); return new AssistantToolResult("switched."); }
            try { SoundOutput.SetDefault(hit.Id); }
            catch (Exception ex) { DebugLog.Write("sound: set: " + ex.Message); return new AssistantToolResult("windows wouldn't switch it (" + ex.Message + "). use use_computer in sound settings."); }
            DebugLog.Write("sound: default output is now " + hit.Name);
            return new AssistantToolResult("switched the sound to " + hit.Name + (hit.Muted ? " (note: it's muted)" : "") + ". apps already playing follow within a second or two.",
                "sound → " + hit.Name, null);
        }

        static bool LooksVirtual(string name)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"steam streaming|virtual|cable|voicemeeter|nvidia broadcast", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }

    /// <summary>Windows' playback devices (Core Audio): which exist, which one is the default, and switching it.</summary>
    internal static class SoundOutput
    {
        public sealed class Device { public string Id, Name; public bool Default, Muted; public int Volume = -1; }

        const int eRender = 0, DEVICE_STATE_ACTIVE = 1, eConsole = 0, eMultimedia = 1;

        public static List<Device> List()
        {
            var list = new List<Device>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                string defaultId = null;
                IMMDevice def;
                if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out def) == 0 && def != null) { def.GetId(out defaultId); Marshal.ReleaseComObject(def); }
                IMMDeviceCollection devices;
                Check(en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out devices));
                uint count;
                Check(devices.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice d;
                    if (devices.Item(i, out d) != 0 || d == null) continue;
                    try
                    {
                        string id; d.GetId(out id);
                        var dev = new Device { Id = id, Name = FriendlyName(d) ?? id, Default = id == defaultId };
                        ReadVolume(d, dev);
                        list.Add(dev);
                    }
                    finally { Marshal.ReleaseComObject(d); }
                }
                Marshal.ReleaseComObject(devices);
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        /// <summary>Makes this the default output for apps and games (what the speaker icon in the taskbar switches).</summary>
        public static void SetDefault(string id)
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            try
            {
                Check(policy.SetDefaultEndpoint(id, eConsole));
                Check(policy.SetDefaultEndpoint(id, eMultimedia));
            }
            finally { Marshal.ReleaseComObject(policy); }
        }

        /// <summary>The default output's volume, 0-100.</summary>
        public static void SetVolume(int percent)
        {
            WithDefaultVolume(v => { Guid ctx = Guid.Empty; Check(v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, percent)) / 100f, ref ctx)); if (percent > 0) Check(v.SetMute(false, ref ctx)); });
        }

        public static void SetMute(bool mute)
        {
            WithDefaultVolume(v => { Guid ctx = Guid.Empty; Check(v.SetMute(mute, ref ctx)); });
        }

        static void WithDefaultVolume(Action<IAudioEndpointVolume> act)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice d = null;
            try
            {
                Check(en.GetDefaultAudioEndpoint(eRender, eMultimedia, out d));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                Check(d.Activate(ref iid, 1, IntPtr.Zero, out o));
                var vol = (IAudioEndpointVolume)o;
                try { act(vol); } finally { Marshal.ReleaseComObject(vol); }
            }
            finally
            {
                if (d != null) Marshal.ReleaseComObject(d);
                Marshal.ReleaseComObject(en);
            }
        }

        static string FriendlyName(IMMDevice d)
        {
            IPropertyStore store;
            if (d.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // PKEY_Device_FriendlyName
                var v = new PROPVARIANT();
                if (store.GetValue(ref key, out v) != 0) return null;
                try { return v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.pointer) : null; }
                finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        static void ReadVolume(IMMDevice d, Device dev)
        {
            try
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                if (d.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out o) != 0 || o == null) return;
                var vol = (IAudioEndpointVolume)o;
                try
                {
                    float level; bool muted;
                    if (vol.GetMasterVolumeLevelScalar(out level) == 0) dev.Volume = (int)Math.Round(level * 100);
                    if (vol.GetMute(out muted) == 0) dev.Muted = muted;
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            catch { }
        }

        static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pvar);

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public int pid; }

        // 24 bytes on 64-bit (16 on 32-bit): the type tag, padding, then the value
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pointer; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // undocumented but stable since Windows 7: how the sound settings themselves switch the default device
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
        }
    }
}
