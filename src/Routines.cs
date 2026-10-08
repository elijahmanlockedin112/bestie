// Routines.cs - their own one-word setups: "study mode" = snap chrome left and claude right, start a focus block, play lofi.
// They say "save this as study mode" (or list the steps), and from then on saying its name runs every step at once, in
// order, through the same tools (and the same yes/no checks) as always. Saved in .state\routines.json. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        static string RoutinesPath { get { return Home.State("routines.json"); } }
        static readonly object routinesGate = new object();

        void AddRoutineTools(List<object> t)
        {
            t.Add(Tool("my_routines", "their saved one-word setups. run: when they say a routine's name ('study mode', 'wind down'), every step happens at once, in "
                + "order. save: only when they ask to save or name a setup ('save this as study mode', 'when i say game time, close discord and open steam'); the "
                + "steps are the exact tool calls (use what you just did if they say 'save that'). also list and delete.", R("action"),
                PE("action", "what to do", new[] { "run", "save", "list", "delete" }),
                P("name", "string", "the routine's name, as they said it"),
                P("steps", "string", "save: a JSON array of the tool calls, in order, e.g. [{\"tool\":\"window_control\",\"args\":{\"action\":\"snap_left\",\"app\":\"chrome\"}},"
                    + "{\"tool\":\"play_music\",\"args\":{\"kind\":\"playlist\",\"title\":\"lofi beats\"}}]")));
        }

        sealed class Routine { public string Name; public List<KeyValuePair<string, Dictionary<string, object>>> Steps = new List<KeyValuePair<string, Dictionary<string, object>>>(); }

        static List<Routine> LoadRoutines()
        {
            var list = new List<Routine>();
            try
            {
                if (!File.Exists(RoutinesPath)) return list;
                foreach (var o in (Json.Parse(File.ReadAllText(RoutinesPath, Encoding.UTF8)) as object[]) ?? new object[0])
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || string.IsNullOrEmpty(Json.Str(d, "name"))) continue;
                    var r = new Routine { Name = Json.Str(d, "name") };
                    foreach (var so in Json.Arr(d, "steps"))
                    {
                        var sd = so as Dictionary<string, object>;
                        if (sd != null && !string.IsNullOrEmpty(Json.Str(sd, "tool")))
                            r.Steps.Add(new KeyValuePair<string, Dictionary<string, object>>(Json.Str(sd, "tool"), Json.Obj(sd, "args") ?? new Dictionary<string, object>()));
                    }
                    list.Add(r);
                }
            }
            catch (Exception ex) { DebugLog.Write("routines: load: " + ex.Message); }
            return list;
        }

        static void SaveRoutines(List<Routine> list)
        {
            var arr = list.Select(r => (object)new Dictionary<string, object>
            {
                { "name", r.Name },
                { "steps", r.Steps.Select(s => (object)new Dictionary<string, object> { { "tool", s.Key }, { "args", s.Value } }).ToArray() },
            }).ToArray();
            string tmp = RoutinesPath + ".tmp";
            File.WriteAllText(tmp, Json.Write(arr), new UTF8Encoding(false));
            if (File.Exists(RoutinesPath)) File.Replace(tmp, RoutinesPath, null); else File.Move(tmp, RoutinesPath);
        }

        /// <summary>For the live context: the names they can say.</summary>
        internal static string RoutineNames()
        {
            List<Routine> all;
            lock (routinesGate) all = LoadRoutines();
            return all.Count == 0 ? null : string.Join(", ", all.Select(r => r.Name).ToArray());
        }

        static Routine FindRoutine(List<Routine> all, string name)
        {
            string want = Norm(name);
            if (want.Length == 0) return null;
            return all.FirstOrDefault(r => Norm(r.Name) == want)
                ?? all.FirstOrDefault(r => Norm(r.Name).Contains(want) || want.Contains(Norm(r.Name)));
        }

        AssistantToolResult RoutinesTool(Dictionary<string, object> a, AssistantSettings s, AssistantTurnContext ctx)
        {
            string action = Json.Str(a, "action") ?? "list";
            string name = (Json.Str(a, "name") ?? "").Trim();
            List<Routine> all;
            lock (routinesGate) all = LoadRoutines();
            switch (action)
            {
                case "list":
                    if (all.Count == 0) return new AssistantToolResult("no routines saved yet. they can say e.g. 'save this as study mode' after you set something up.");
                    return new AssistantToolResult("their routines: " + string.Join("; ", all.Select(r => r.Name + " (" + string.Join(", then ", r.Steps.Select(x => Describe(x)).ToArray()) + ")").ToArray()) + ".");
                case "delete":
                    {
                        Routine r = FindRoutine(all, name);
                        if (r == null) return new AssistantToolResult("there's no routine called " + name + ".");
                        lock (routinesGate) { all.Remove(r); SaveRoutines(all); }
                        return new AssistantToolResult("deleted the " + r.Name + " routine.", "deleted routine: " + r.Name, null);
                    }
                case "save":
                    {
                        if (name.Length == 0) return new AssistantToolResult("what should the routine be called?");
                        object[] raw;
                        try { raw = (Json.Parse(Json.Str(a, "steps") ?? "") as object[]) ?? new object[0]; }
                        catch { return new AssistantToolResult("steps must be a JSON array of {\"tool\":..., \"args\":{...}}."); }
                        var known = new HashSet<string>(Definitions(s, ctx, true).OfType<Dictionary<string, object>>()
                            .Select(d => Json.Str(Json.Obj(d, "function"), "name")).Where(n => n != null));
                        var r = new Routine { Name = OpenAIClient.Trunc(name, 40) };
                        foreach (var o in raw)
                        {
                            var sd = o as Dictionary<string, object>;
                            string tool = sd != null ? Json.Str(sd, "tool") : null;
                            if (tool == null) continue;
                            if (tool == "my_routines") return new AssistantToolResult("a routine can't run other routines; put the steps in directly.");
                            if (!known.Contains(tool)) return new AssistantToolResult("there's no tool called " + tool + "; not saved.");
                            r.Steps.Add(new KeyValuePair<string, Dictionary<string, object>>(tool, Json.Obj(sd, "args") ?? new Dictionary<string, object>()));
                        }
                        if (r.Steps.Count == 0) return new AssistantToolResult("no steps to save. list the tool calls, in order.");
                        if (r.Steps.Count > 12) return new AssistantToolResult("that's a lot of steps; keep a routine to 12 or fewer.");
                        lock (routinesGate)
                        {
                            all = LoadRoutines();
                            all.RemoveAll(x => Norm(x.Name) == Norm(r.Name));
                            all.Add(r);
                            SaveRoutines(all);
                        }
                        Journal.Add("saved a routine, " + r.Name + ": " + string.Join(", then ", r.Steps.Select(x => Describe(x)).ToArray()) + ".");
                        return new AssistantToolResult("saved \"" + r.Name + "\": " + string.Join(", then ", r.Steps.Select(x => Describe(x)).ToArray())
                            + ". from now on they just say \"" + r.Name.ToLowerInvariant() + "\". tell them that in a sentence.", "saved routine: " + r.Name, null);
                    }
                case "run":
                    {
                        Routine r = FindRoutine(all, name);
                        if (r == null) return new AssistantToolResult(all.Count == 0 ? "no routines saved yet." : "there's no routine called " + name + ". they have: " + string.Join(", ", all.Select(x => x.Name).ToArray()) + ".");
                        if (DryRun) { lock (DryRunLog) DryRunLog.Add("routine " + r.Name); }
                        var done = new List<string>();
                        foreach (var step in r.Steps)
                        {
                            AssistantToolResult res = Execute(step.Key, Json.Write(step.Value), s, ctx);
                            string said = res != null ? OpenAIClient.Trunc((res.Text ?? "").Replace("\n", " "), 140) : "";
                            done.Add(step.Key + ": " + said);
                            Thread.Sleep(step.Key == "window_control" || step.Key == "open_app" || step.Key == "press_keys" ? 450 : 150);   // let windows settle
                        }
                        DebugLog.Write("routines: ran " + r.Name + " (" + r.Steps.Count + " steps)");
                        return new AssistantToolResult("ran the " + r.Name + " routine. each step said: " + string.Join(" | ", done.ToArray())
                            + ". tell them it's set (mention anything that didn't work).", "routine: " + r.Name, null);
                    }
            }
            return new AssistantToolResult("my_routines can run, save, list or delete.");
        }

        static string Describe(KeyValuePair<string, Dictionary<string, object>> step)
        {
            var bits = step.Value.Where(kv => kv.Value != null && !(kv.Value is Dictionary<string, object>)).Take(3).Select(kv => Convert.ToString(kv.Value)).ToArray();
            return step.Key.Replace('_', ' ') + (bits.Length > 0 ? " " + string.Join(" ", bits) : "");
        }
    }
}
