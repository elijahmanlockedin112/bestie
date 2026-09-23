using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Bestie
{
    /// <summary>Turns every other app's audio down while the creature talks or listens (Spotify, videos, games…),
    /// then fades it back. Uses the Windows per-app volume mixer (WASAPI audio sessions). UI thread only.</summary>
    public sealed class AudioDucker
    {
        public double DuckTo = 0.2;          // other apps play at 20% of their volume while it talks
        const float Slack = 0.02f;
        const double ParkSeconds = 120;

        /// <summary>One app we turned down: its real volume, and the range of volumes we've set it to since.
        /// Other apps duck too (Wispr Flow mutes music while you dictate, then puts back whatever it saw), so a
        /// volume inside that range is still "ours" even if someone else put it there.</summary>
        sealed class Tracked
        {
            public float Original, Lo, Hi, Last;   // Last: what we set it to most recently
            public bool HeldBelow;                 // something else pushed it under what we set (wispr muting for dictation)
            public double ParkedAt = -1;
        }

        readonly Dictionary<string, Tracked> tracked = new Dictionary<string, Tracked>();   // session id -> what we know
        readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        double level = 1, target = 1;        // 1 = normal, DuckTo = ducked
        bool active;
        double clock, nextParkCheck;

        public bool Ducked { get { return target < 1; } }

        public void Duck() { target = DuckTo; active = true; }
        public void Restore() { target = 1; }

        /// <summary>Put everything back right now (no fade), e.g. before another app starts ducking on its own.</summary>
        public void RestoreNow()
        {
            target = level = 1;
            if (!active) return;
            try { Apply(1); } catch (Exception ex) { DebugLog.Write("ducking: " + ex.Message); }
            Finish();
        }

        /// <summary>Call every frame; fades smoothly toward the target (fast down, gentler back up).</summary>
        public void Tick(double dt)
        {
            clock += dt;
            if (!active) { CheckParked(); return; }
            if (Math.Abs(target - level) < 0.001) { if (target >= 1) Finish(); return; }
            double speed = target < level ? 4.0 : 1.6;   // ~0.2 s down, ~0.5 s up
            level += Math.Sign(target - level) * Math.Min(Math.Abs(target - level), speed * dt);
            try { Apply(level); }
            catch (Exception ex) { DebugLog.Write("ducking: " + ex.Message); active = false; }
        }

        void Finish()
        {
            try { Apply(1); } catch { }
            // anything someone else is still holding down waits ("parked") until they let go, then gets its real volume back
            foreach (var k in tracked.Where(kv => kv.Value.ParkedAt < 0).Select(kv => kv.Key).ToList()) tracked.Remove(k);
            active = false;
        }

        /// <summary>After a duck: an app another program was holding down comes back up to where it really was.</summary>
        void CheckParked()
        {
            if (tracked.Count == 0 || clock < nextParkCheck) return;
            nextParkCheck = clock + 0.5;
            try
            {
                foreach (var s in Sessions())
                {
                    try
                    {
                        string id; s.Control.GetSessionInstanceIdentifier(out id);
                        Tracked t;
                        if (id == null || !tracked.TryGetValue(id, out t)) continue;
                        float now; s.Volume.GetMasterVolume(out now);
                        if (now > t.Hi + Slack) { tracked.Remove(id); continue; }        // turned up past anything we did: theirs now
                        if (now >= t.Lo - Slack)                                        // let go, back at one of our ducked levels: undo it
                        {
                            var ctx = Guid.Empty;
                            s.Volume.SetMasterVolume(t.Original, ref ctx);
                            tracked.Remove(id);
                            DebugLog.Write("ducking: put an app back to " + (int)Math.Round(t.Original * 100) + "% after another app let go of it");
                        }
                    }
                    finally { Marshal.ReleaseComObject(s.Control); }
                }
            }
            catch (Exception ex) { DebugLog.Write("ducking: " + ex.Message); }
            foreach (var k in tracked.Where(kv => clock - kv.Value.ParkedAt > ParkSeconds).Select(kv => kv.Key).ToList()) tracked.Remove(k);
        }

        void Apply(double factor)
        {
            foreach (var s in Sessions())
            {
                try
                {
                    uint pid; s.Control.GetProcessId(out pid);
                    if (pid == myPid || pid == 0 || s.Control.IsSystemSoundsSession() == 0) continue;
                    string id; s.Control.GetSessionInstanceIdentifier(out id);
                    if (id == null) continue;
                    float now; s.Volume.GetMasterVolume(out now);
                    Tracked t;
                    if (!tracked.TryGetValue(id, out t))
                    {
                        if (factor >= 1) continue;
                        tracked[id] = t = new Tracked { Original = now, Lo = now, Hi = now, Last = now };
                    }
                    else if (Math.Abs(now - t.Last) > Slack)
                    {
                        if (now < t.Last)
                        {
                            // something else (like wispr) turned it down: don't fight it, and put it back once it lets go
                            t.HeldBelow = true;
                            if (factor >= 1 && t.ParkedAt < 0) t.ParkedAt = clock;
                            continue;
                        }
                        // turned up: if nothing else was holding it down, that's them choosing a volume: hands off.
                        // (coming back up to one of our levels after something held it down is that app letting go.)
                        if (!t.HeldBelow || now > t.Hi + Slack) { tracked.Remove(id); continue; }
                    }
                    t.ParkedAt = -1;
                    t.HeldBelow = false;
                    float v = (float)Math.Max(0, Math.Min(1, t.Original * factor));
                    var ctx = Guid.Empty;
                    s.Volume.SetMasterVolume(v, ref ctx);
                    t.Last = v;
                    t.Lo = Math.Min(t.Lo, v); t.Hi = Math.Max(t.Hi, v);
                }
                finally { Marshal.ReleaseComObject(s.Control); }
            }
        }

        /// <summary>"spotify 100%, chrome 80%" (for the debug log).</summary>
        public string Describe()
        {
            var parts = new List<string>();
            foreach (var s in Sessions())
            {
                try
                {
                    uint pid; s.Control.GetProcessId(out pid);
                    float v; s.Volume.GetMasterVolume(out v);
                    if (pid != 0) parts.Add((Native.ProcessNameOf(pid) ?? pid.ToString()) + " " + (int)Math.Round(v * 100) + "%");
                }
                finally { Marshal.ReleaseComObject(s.Control); }
            }
            return string.Join(", ", parts);
        }

        struct Session { public IAudioSessionControl2 Control; public ISimpleAudioVolume Volume; }

        IEnumerable<Session> Sessions()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice device = null; IAudioSessionManager2 manager = null; IAudioSessionEnumerator sessions = null;
            var list = new List<Session>();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device) != 0) return list;
                var iid = typeof(IAudioSessionManager2).GUID;
                object o;
                if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) return list;
                manager = (IAudioSessionManager2)o;
                if (manager.GetSessionEnumerator(out sessions) != 0) return list;
                int count; sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 ctl;
                    if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                    list.Add(new Session { Control = ctl, Volume = (ISimpleAudioVolume)ctl });
                }
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
            return list;
        }

        // ---- Core Audio COM interfaces (just the bits we need, in vtable order) ----

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();   // S_OK (0) = it is
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute(bool mute, ref Guid ctx);
            [PreserveSig] int GetMute(out bool mute);
        }
    }
}
