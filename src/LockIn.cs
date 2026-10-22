// LockIn.cs - Locked In mode, fired from the phone. An NFC tag on the desk runs an iPhone Shortcut that posts
// {"action":"lockin"} to /api/control: the distraction tabs close, Synaptic comes up front and a focus block starts.
// {"action":"lockin","value":"off"} stops the block; "preview" says what it would do and does nothing. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Bestie
{
    internal sealed partial class PhoneServer
    {
        // youtube music and studio stay open (music to study to, their own channel)
        static readonly string[] Distractions = { "youtube.com", "youtu.be", "tiktok.com", "instagram.com", "reddit.com", "x.com", "twitter.com",
            "facebook.com", "netflix.com", "twitch.tv", "snapchat.com" };
        const string SynapticUrlFile = @"C:\Users\elija\Downloads\Coding Projects\learning-hub\apps\synaptic\.synaptic-url";
        const string SynapticFallback = "http://localhost:3000";

        /// <summary>The distraction site this address is on ("youtube.com"), or null.</summary>
        internal static string DistractionOf(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            string host = u.Host.ToLowerInvariant();
            if (host == "music.youtube.com" || host == "studio.youtube.com") return null;
            foreach (string d in Distractions) if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return d;
            return null;
        }

        /// <summary>Where Synaptic is serving (it writes the address it bound to), or its usual port.</summary>
        static string StudyUrl()
        {
            try
            {
                string url = File.Exists(SynapticUrlFile) ? File.ReadAllText(SynapticUrlFile).Trim() : "";
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) return url.TrimEnd('/');
            }
            catch { }
            return SynapticFallback;
        }

        string LockIn(string value)
        {
            if (value == "off")
            {
                if (assistant != null) assistant.OnUi<string>(delegate { TimeBlocks.Stop(); return "ok"; }, null);
                return "unlocked. the focus block is stopped.";
            }
            bool preview = value == "preview";
            var said = new List<string>();
            string study = StudyUrl();

            BrowserBridge b = AssistantTools.LiveBridge();
            if (b == null)
            {
                said.Add("the browser extension isn't connected, so no tabs closed");
                if (!preview) Process.Start(new ProcessStartInfo(study) { UseShellExecute = true });
                said.Add((preview ? "would open " : "opened ") + "synaptic");
            }
            else
            {
                List<TabInfo> tabs = b.GetTabs();
                var gone = new List<string>();
                foreach (TabInfo t in tabs)
                {
                    string site = DistractionOf(t.Url);
                    if (site == null) continue;
                    if (!preview)
                    {
                        try { b.Execute("close_tab", new Dictionary<string, object> { { "tabId", t.Id } }, 5000); }
                        catch (BridgeException ex) { DebugLog.Write("phone: lockin couldn't close a " + site + " tab: " + ex.Message); continue; }
                    }
                    gone.Add(site);
                }
                said.Add(gone.Count == 0 ? "no distractions open"
                    : (preview ? "would close " : "closed ") + gone.Count + " tab" + (gone.Count == 1 ? "" : "s") + " (" + string.Join(", ", gone.Distinct()) + ")");

                TabInfo open = tabs.FirstOrDefault(t => t.Url != null && t.Url.StartsWith(study, StringComparison.OrdinalIgnoreCase));
                if (!preview)
                {
                    try
                    {
                        if (open != null) b.Execute("focus_tab", new Dictionary<string, object> { { "tabId", open.Id } }, 5000);
                        else b.Execute("open_tab", new Dictionary<string, object> { { "url", study }, { "active", true } }, 5000);
                    }
                    catch (BridgeException ex)
                    {
                        DebugLog.Write("phone: lockin couldn't open synaptic in the browser (" + ex.Message + "), using the shell");
                        Process.Start(new ProcessStartInfo(study) { UseShellExecute = true });
                    }
                }
                said.Add((preview ? "would " : "") + (open != null ? (preview ? "switch to" : "switched to") : (preview ? "open" : "opened")) + " synaptic");
            }

            if (assistant == null) said.Add("no timer (the assistant is off)");
            else if (preview) said.Add(TimeBlocks.Running ? "your block is already running" : "would start a " + TimeBlocks.FocusMinutes + "-minute block");
            else said.Add(assistant.OnUi<string>(delegate
            {
                if (TimeBlocks.Running && TimeBlocks.Paused) { TimeBlocks.Resume(); return "your block is back on"; }
                if (TimeBlocks.Running) return "your block is already running (" + TimeBlocks.Clock(TimeBlocks.Left) + " left)";
                TimeBlocks.Start(TimeBlocks.FocusMinutes, TimeBlocks.BreakMinutes);
                WidgetManager.Show("timeblocks");
                return TimeBlocks.FocusMinutes + "-minute block started";
            }, "couldn't start the timer"));

            return (preview ? "preview: " : "locked in. ") + string.Join(", ", said) + ".";
        }
    }
}
