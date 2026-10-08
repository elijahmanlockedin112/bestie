// Trust.cs - what Bestie may do without stopping to ask. Built in: browsing and playing music/video and quick
// look-ups (time, weather, calculator) on the computer, as long as the job doesn't buy, send, delete or sign in.
// Theirs: apps they've said it can use freely ("you don't need to ask for chrome"), and commands they've
// approved once (the exact same command never asks again). Kept in .state\trusted.json. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bestie
{
    internal static class Trust
    {
        static readonly object gate = new object();
        static List<string> commands, apps;

        static string StatePath { get { return Home.State("trusted.json"); } }

        static readonly Regex Risky = new Regex(@"\b(buy|purchase|pay|payment|order|checkout|subscribe|subscription|premium|send|email|e-mail|message|text|dm|post|tweet|comment|reply|share|upload|delete|remove|erase|uninstall|install|sign ?in|sign ?out|log ?in|log ?out|login|logout|password|account|card|bank|format|reset|wipe|unfollow|block|report|transfer)\b", RegexOptions.IgnoreCase);
        static readonly Regex BuiltIn = new Regex(@"\b(spotify|music|songs?|playlists?|albums?|artists?|podcasts?|youtube|videos?|netflix|twitch|soundcloud|clock|time|timer|weather|calculator|volume|now playing)\b", RegexOptions.IgnoreCase);

        static void Load()
        {
            if (commands != null) return;
            commands = new List<string>(); apps = new List<string>();
            try
            {
                if (!File.Exists(StatePath)) return;
                var d = Json.Obj(File.ReadAllText(StatePath, Encoding.UTF8));
                commands.AddRange(Json.Arr(d, "commands").Select(o => Convert.ToString(o)).Where(s => !string.IsNullOrEmpty(s)));
                apps.AddRange(Json.Arr(d, "apps").Select(o => Convert.ToString(o)).Where(s => !string.IsNullOrEmpty(s)));
            }
            catch (Exception ex) { DebugLog.Write("trust: load: " + ex.Message); }
        }

        static void Save()
        {
            try { File.WriteAllText(StatePath, Json.Write(new Dictionary<string, object> { { "commands", commands.ToArray() }, { "apps", apps.ToArray() } }), new UTF8Encoding(false)); }
            catch (Exception ex) { DebugLog.Write("trust: save: " + ex.Message); }
        }

        static string Norm(string s) { return Regex.Replace((s ?? "").Trim(), @"\s+", " ").ToLowerInvariant(); }

        /// <summary>A command they've already said yes to, word for word.</summary>
        public static bool CommandTrusted(string cmd) { lock (gate) { Load(); return commands.Contains(Norm(cmd)); } }

        public static void TrustCommand(string cmd)
        {
            lock (gate)
            {
                Load();
                string n = Norm(cmd);
                if (n.Length == 0 || commands.Contains(n)) return;
                commands.Add(n);
                if (commands.Count > 200) commands.RemoveAt(0);
                Save();
            }
        }

        /// <summary>A use_computer job it can just do: in a built-in safe area or an app they trust, and nothing risky in it.</summary>
        public static bool SafeComputerTask(string task)
        {
            if (string.IsNullOrEmpty(task) || Risky.IsMatch(task)) return false;
            if (BuiltIn.IsMatch(task)) return true;
            lock (gate)
            {
                Load();
                string t = task.ToLowerInvariant();
                return apps.Any(a => Regex.IsMatch(t, @"\b" + Regex.Escape(a) + @"\b"));
            }
        }

        /// <summary>For the dont_ask tool: trust (or stop trusting) an app or a command. Returns what changed.</summary>
        public static string Change(string kind, string value, bool undo)
        {
            string v = Norm(value);
            if (kind != "list" && v.Length == 0) return "trust what? an app name or a command.";
            lock (gate)
            {
                Load();
                List<string> list = kind == "command" ? commands : apps;
                if (kind == "list") return Describe();
                if (undo)
                {
                    int n = list.RemoveAll(x => x == v || (kind == "app" && x.Contains(v)));
                    Save();
                    return n > 0 ? "you'll ask again before " + (kind == "app" ? "using " + v : "running that command") + "." : "that wasn't on the no-ask list.";
                }
                if (!list.Contains(v)) { list.Add(v); Save(); }
                return kind == "app"
                    ? "you won't ask before using " + v + " anymore (you still ask before anything that buys, sends, deletes or signs in)."
                    : "you won't ask before running that exact command anymore.";
            }
        }

        public static string Describe()
        {
            lock (gate)
            {
                Load();
                return "never asks: music and video apps (spotify, youtube...), the time, weather, calculator"
                    + (apps.Count > 0 ? "; apps they trust: " + string.Join(", ", apps.ToArray()) : "")
                    + "; " + commands.Count + " command" + (commands.Count == 1 ? "" : "s") + " they've approved before. still always asks before buying, sending, deleting or signing in.";
            }
        }
    }
}
