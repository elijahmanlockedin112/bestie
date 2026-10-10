// BtAudio.cs - connect (or let go of) a paired bluetooth speaker on demand, the way the speaker picker in the taskbar does:
// Windows audio's one-shot reconnect (KSPROPERTY_ONESHOT_RECONNECT on the bluetooth audio filter). The speaker has to be
// paired once (Settings > Bluetooth); after that "connect party time" works even when it isn't connected right now.
// For the morning show: the PC wakes up and plays through the echo. C# 5 only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    internal static class BtAudio
    {
        // ------------------------------------------------------------------ com
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
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
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Explicit)] struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr p; }

        [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDeviceTopology
        {
            [PreserveSig] int GetConnectorCount(out uint count);
            [PreserveSig] int GetConnector(uint index, out IConnector connector);
        }

        [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IConnector
        {
            [PreserveSig] int GetType(out int type);
            [PreserveSig] int GetDataFlow(out int flow);
            [PreserveSig] int ConnectTo(IConnector other);
            [PreserveSig] int Disconnect();
            [PreserveSig] int IsConnected(out bool connected);
            [PreserveSig] int GetConnectedTo(out IConnector other);
        }

        [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPart
        {
            [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int GetLocalId(out uint id);
            [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetPartType(out int type);
            [PreserveSig] int GetSubType(out Guid subType);
            [PreserveSig] int GetControlInterfaceCount(out uint count);
            [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
            [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
            [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
            [PreserveSig] int GetTopologyObject(out IntPtr topology);
            [PreserveSig] int Activate(int clsCtx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IKsControl
        {
            [PreserveSig] int KsProperty(ref KSPROPERTY property, int propertyLength, IntPtr data, int dataLength, out int bytesReturned);
        }

        [StructLayout(LayoutKind.Sequential)] struct KSPROPERTY { public Guid Set; public int Id; public int Flags; }

        static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        const int KSPROPERTY_ONESHOT_RECONNECT = 0, KSPROPERTY_ONESHOT_DISCONNECT = 1, KSPROPERTY_TYPE_GET = 1;
        const int eRender = 0, DEVICE_STATEMASK_ALL = 0xF, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
        static readonly PROPERTYKEY FriendlyNameKey = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        public sealed class Speaker { public string Id, Name; public bool Connected; }

        /// <summary>Every audio output windows knows, connected or not (paired bluetooth speakers show up here while away).</summary>
        public static List<Speaker> All()
        {
            var list = new List<Speaker>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL, out col) != 0) return list;
                uint n; col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    try
                    {
                        string id; d.GetId(out id);
                        int state; d.GetState(out state);
                        list.Add(new Speaker { Id = id, Name = Name(d) ?? id, Connected = state == DEVICE_STATE_ACTIVE });
                    }
                    finally { Marshal.ReleaseComObject(d); }
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            return list;
        }

        static string Name(IMMDevice d)
        {
            IPropertyStore store;
            if (d.OpenPropertyStore(0, out store) != 0 || store == null) return null;
            try
            {
                PROPERTYKEY k = FriendlyNameKey;
                PROPVARIANT v;
                if (store.GetValue(ref k, out v) != 0 || v.vt != 31 /* VT_LPWSTR */) return null;
                return Marshal.PtrToStringUni(v.p);
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        /// <summary>Asks windows to connect (or disconnect) this paired bluetooth speaker. Null when windows took the request.</summary>
        static string OneShot(string endpointId, int what)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection col = null;
            try
            {
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL, out col) != 0) return "couldn't list the speakers";
                uint n; col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    try
                    {
                        string id; d.GetId(out id);
                        if (id != endpointId) continue;
                        // endpoint -> its topology -> the connector -> what it plugs into (the bluetooth audio filter) -> ks control
                        Guid iidTopo = typeof(IDeviceTopology).GUID;
                        object topoObj;
                        if (d.Activate(ref iidTopo, CLSCTX_ALL, IntPtr.Zero, out topoObj) != 0) return "it isn't a speaker windows can reconnect";
                        var topo = (IDeviceTopology)topoObj;
                        IConnector conn, other;
                        if (topo.GetConnector(0, out conn) != 0 || conn.GetConnectedTo(out other) != 0 || other == null) return "it isn't a bluetooth speaker";
                        var part = (IPart)other;
                        Guid iidKs = typeof(IKsControl).GUID;
                        object ksObj;
                        if (part.Activate(CLSCTX_ALL, ref iidKs, out ksObj) != 0) return "windows won't let go of it";
                        var ks = (IKsControl)ksObj;
                        var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = what, Flags = KSPROPERTY_TYPE_GET };
                        int got;
                        int hr = ks.KsProperty(ref prop, Marshal.SizeOf(typeof(KSPROPERTY)), IntPtr.Zero, 0, out got);
                        return hr == 0 ? null : "windows said no (0x" + hr.ToString("x8") + ")";
                    }
                    finally { Marshal.ReleaseComObject(d); }
                }
                return "that speaker isn't paired";
            }
            catch (Exception ex) { return ex.Message; }
            finally { if (col != null) Marshal.ReleaseComObject(col); Marshal.ReleaseComObject(en); }
        }

        /// <summary>The paired speaker whose name has these words ("party time" -> "Party Time Stereo"), or null.</summary>
        public static Speaker Find(string name)
        {
            string want = (name ?? "").Trim().ToLowerInvariant();
            if (want.Length == 0) return null;
            var all = All();
            return all.FirstOrDefault(s => s.Name.ToLowerInvariant().Contains(want))
                ?? all.FirstOrDefault(s => want.Split(' ').Where(w => w.Length > 2).All(w => s.Name.ToLowerInvariant().Contains(w)));
        }

        /// <summary>A paired echo under its own bluetooth name ("Echo Dot-1AB"), for when it doesn't go by its alexa name. Not "echo cancelling" mics.</summary>
        public static Speaker FindEcho()
        {
            return All().FirstOrDefault(s => Regex.IsMatch(s.Name, @"\b(echo|alexa)\b", RegexOptions.IgnoreCase) && s.Name.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) < 0);
        }

        /// <summary>Connects the paired speaker and waits (up to ~25 s) until windows can play through it. Null when it's ready.</summary>
        public static string Connect(string name, out Speaker speaker)
        {
            speaker = Find(name);
            if (speaker == null) return "\"" + name + "\" isn't paired with this computer (alexa app: devices > echo & alexa > the echo > bluetooth devices > pair a new device; then Settings > Bluetooth & devices > Add device).";
            if (speaker.Connected) return null;
            string problem = OneShot(speaker.Id, KSPROPERTY_ONESHOT_RECONNECT);
            if (problem != null) return "couldn't connect to " + speaker.Name + ": " + problem + ".";
            string id = speaker.Id;
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(500);
                Speaker now = All().FirstOrDefault(s => s.Id == id);
                if (now != null && now.Connected) { speaker = now; return null; }
            }
            return speaker.Name + " didn't connect (is it on, and in range?).";
        }

        public static void Disconnect(string endpointId)
        {
            string problem = OneShot(endpointId, KSPROPERTY_ONESHOT_DISCONNECT);
            if (problem != null) DebugLog.Write("bluetooth: disconnect: " + problem);
        }
    }
}
