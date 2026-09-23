using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bestie
{
    /// <summary>What the creature decides to do, and how it does it. Activities are coroutines
    /// (iterators yielding <see cref="Wait"/>s). Habits are a Pólya urn: whatever it happens to do
    /// after X gets a little likelier to follow X next time, so coincidences harden into routines.</summary>
    public partial class Creature
    {
        public static readonly string[] Acts = { "wander", "nap", "dance", "peek", "sniff", "climb", "patrol", "sit", "explore",
            "spin", "sing", "count", "visit", "exercise", "boop", "sunbathe" };

        IEnumerator<Wait> current;
        Wait wait;
        IEnumerable<Wait> pending;
        string pendingCtx;
        bool abortPending;
        double idleGap = 1, energy = 1;
        string prevCtx = "start";
        public string CurrentAct;
        readonly List<string[]> history = new List<string[]>();   // [act, place]
        Ritual announce, ritualRunning;
        string markedPlace;
        bool navOk;
        double lookOverX, lookOverY, lookOverUntil;

        public string PrevContext { get { return prevCtx; } }
        public double Energy { get { return energy; } }
        public Ritual RitualRunning { get { return ritualRunning; } }

        // =====================================================================
        // coroutine plumbing
        // =====================================================================

        static Wait Pause(double s) { return new Wait(s, null); }
        static Wait Until(Func<bool> cond, double timeout) { return new Wait(timeout, cond); }

        IEnumerable<Wait> Sequence(params Func<Wait>[] steps)
        {
            foreach (var s in steps) { var w = s(); if (w != null) yield return w; }
        }

        /// <summary>Drop whatever it's doing and run <paramref name="routine"/> instead (applied on the next tick).</summary>
        public void Interrupt(IEnumerable<Wait> routine, string ctx)
        {
            pending = routine;
            pendingCtx = ctx;
        }

        public void AbortActivity() { abortPending = true; }

        void KillCurrent()
        {
            if (current != null) { try { current.Dispose(); } catch { } }
            current = null; wait = null; CurrentAct = null; ritualRunning = null;
            history.Clear();   // traditions are unbroken runs
            ResetPose();
            if (Mode == Mode.Ground) StopWalking();
        }

        void ResetPose()
        {
            if (Mode == Mode.Egg) return;
            Pose = Pose.Stand; Face = Face.Normal; Tilt = 0; Sunglasses = false; ArmWave = 0;
        }

        void StepMind(double dt)
        {
            if (pending != null)
            {
                KillCurrent();
                current = pending.GetEnumerator();
                if (pendingCtx != null) prevCtx = pendingCtx;
                pending = null; pendingCtx = null;
                abortPending = false;
            }
            if (abortPending) { KillCurrent(); abortPending = false; idleGap = Rng.Range(0.5, 1.5); }
            if (Mode == Mode.Held || Mode == Mode.Hidden) return;
            if (Mode == Mode.Air && !expectingAir) return;
            if (Mood == "listening" || Mood == "thinking" || (ListenAlong && Pose != Pose.Lie)) { if (Mode == Mode.Ground) StopWalking(); return; }
            if (Pose != Pose.Lie) energy = Math.Max(0, energy - dt * 0.0006);

            if (current == null)
            {
                if (Mode == Mode.Air || Mode == Mode.Egg) return;
                idleGap -= dt;
                if (Mode == Mode.Ground && Clock.T - lastMusing > 150 && Rng.Chance(dt * 0.015 * Brain.Traits.Chatty))
                {
                    lastMusing = Clock.T;
                    Say(Musing(), Rng.Chance(0.5), 0);
                }
                if (idleGap > 0) return;
                current = ChooseNext().GetEnumerator();
                wait = null;
            }

            int guard = 0;
            while (current != null && (wait == null || wait.Done()) && guard++ < 25)
            {
                bool more;
                try { more = current.MoveNext(); }
                catch (Exception ex) { DebugLog.Write("activity crashed: " + ex); more = false; }
                if (!more)
                {
                    try { current.Dispose(); } catch { }
                    current = null; wait = null; CurrentAct = null;
                    idleGap = Rng.Range(0.8, 3.5);
                    ResetPose();
                    break;
                }
                wait = current.Current;
            }
        }

        string Musing()
        {
            string t = Brain.TimeBucket(Clock.Wall);
            var m = CurrentMon;
            if (m != null && Rng.Chance(0.3) && World.Wins.Any(w => w.Maximized && World.MonAt((w.R.Left + w.R.Right) / 2.0, (w.R.Top + w.R.Bottom) / 2.0) == m))
                return Rng.Pick(Personality.Hogging);
            string[] pool = (t == "night" || t == "wee hours") && Rng.Chance(0.5) ? Lines.NightMusings
                          : t == "dawn" && Rng.Chance(0.4) ? Lines.MorningMusings
                          : Rng.Chance(0.6) ? Personality.Musings : Lines.Musings;
            return Lines.FillPick(pool, this, null);
        }

        // =====================================================================
        // choosing what to do
        // =====================================================================

        IEnumerable<Wait> ChooseNext()
        {
            if (Mode == Mode.Wall || Mode == Mode.Ceiling) return GetDown();
            if (announce != null) { var r = announce; announce = null; return Announce(r); }

            string time = Brain.TimeBucket(Clock.Wall);
            var reach = Planner.Reachable(this);

            foreach (var r in Brain.Rituals.Where(x => Clock.Unix - x.LastDone > 40 * 60 && x.Acts.Count > 0).OrderBy(x => Rng.Next()))
                if (Rng.Chance(r.When == time ? 0.4 : 0.01)) return RunRitual(r);

            var options = Acts.Select(a => new KeyValuePair<string, double>(a, BaseWeight(a, reach, time))).Where(kv => kv.Value > 0).ToList();
            if (options.Count == 0) return Perform("sit", "here", false);
            string act;
            if (Rng.Chance(0.08 + 0.08 * Brain.Traits.Curious)) act = Rng.Pick(options).Key;   // a whim
            else act = Rng.Weighted(options, kv => kv.Value * (0.6 + Math.Sqrt(Brain.Habit(time, prevCtx, kv.Key)))).Key;
            return Perform(act, ChoosePlace(act, reach), false);
        }

        double BaseWeight(string a, Dictionary<Seg, Planner.Node> reach, string time)
        {
            var t = Brain.Traits;
            bool night = time == "night" || time == "wee hours";
            bool planks = reach.Keys.Any(s => s.Kind == SegKind.Plank);
            bool walls = reach.Keys.Any(s => s.Kind == SegKind.Floor && HasWall(s));
            int unknown = UnknownCount(reach);
            double w;
            switch (a)
            {
                case "wander": w = 0.55; break;
                case "nap": w = (0.15 + t.Sleepy * 0.6 + (1 - energy) * 2.5 + (night ? 1.2 : 0)) * (energy > 0.8 ? 0.4 : 1); break;
                case "dance": w = 0.25 + t.Silly * 0.9; break;
                case "peek": w = planks ? 0.3 + t.Curious * 0.5 : 0; break;
                case "sniff": w = planks ? 0.35 + t.Curious * 0.7 : 0; break;
                case "climb": w = walls ? 0.15 + t.Bold * 0.7 : 0; break;
                case "patrol": w = 0.35; break;
                case "sit": w = 0.5 + (1 - energy) * 0.5; break;
                case "explore": w = unknown > 0 ? t.Curious * (0.6 + Math.Min(unknown, 5) * 0.3) : t.Curious * 0.15; break;
                case "spin": w = 0.15 + t.Silly * 0.5; break;
                case "sing": w = (0.15 + t.Chatty * 0.5) * (night ? 0.3 : 1); break;
                case "count": w = World.Wins.Count > 1 ? 0.2 : 0; break;
                case "visit": w = userAway || Clock.Simulated ? 0 : (0.15 + t.Cuddly * 0.5) * Math.Max(0.1, 0.6 + Brain.Trust); break;
                case "exercise": w = 0.2 * energy; break;
                case "boop": w = walls && Brain.Edges.Count > 0 ? 0.25 : 0; break;
                case "sunbathe": w = planks ? 0.15 + t.Bold * 0.3 : 0; break;
                default: w = 0.2; break;
            }
            if (userAway && (a == "explore" || a == "climb" || a == "sunbathe")) w *= 1.5;
            if (NowPlaying != null) { if (a == "dance") w *= 2.5; else if (a == "sing") w *= 1.5; else if (a == "nap") w *= 0.5; }
            int n = history.Count;
            if (n >= 1 && history[n - 1][0] == a) w *= n >= 2 && history[n - 2][0] == a ? 0.05 : 0.3;
            return Math.Max(0, w);
        }

        bool HasWall(Seg floor)
        {
            var m = World.MonByIndex(floor.Mon);
            return m != null && (m.WallLeft || m.WallRight);
        }

        int UnknownCount(Dictionary<Seg, Planner.Node> reach)
        {
            int n = 0;
            foreach (var s in reach.Keys)
            {
                if (s.Kind == SegKind.Plank && !Brain.Apps.ContainsKey(s.App)) n++;
                if (s.Kind == SegKind.Floor)
                {
                    var m = World.MonByIndex(s.Mon);
                    if (m == null) continue;
                    if (!Brain.Screens.ContainsKey(m.Key)) n += 2;
                    if (m.WallLeft && !EdgeKnown(m, -1)) n++;
                    if (m.WallRight && !EdgeKnown(m, 1)) n++;
                }
            }
            return n;
        }

        // =====================================================================
        // places: "floor:<screen>:<0-9>", "plank:<app>:<L|M|R>", "wall:<screen>:<L|R>", "here"
        // =====================================================================

        static int Bucket(Mon m, double x) { return Math.Max(0, Math.Min(9, (int)((x - m.Work.Left) / (m.Work.Width / 10.0)))); }

        public string PlaceKeyHere()
        {
            var m = CurrentMon;
            if (m == null) return null;
            if (Mode == Mode.Ground && Support != null)
            {
                if (Support.Kind == SegKind.Floor) return "floor:" + m.Key + ":" + Bucket(m, X);
                double f = (X - Support.X1) / Math.Max(1, Support.Width);
                return "plank:" + Support.App + ":" + (f < 0.34 ? "L" : f > 0.66 ? "R" : "M");
            }
            if (Mode == Mode.Wall) return "wall:" + m.Key + ":" + (WallSide < 0 ? "L" : "R");
            if (Mode == Mode.Ceiling) return "ceil:" + m.Key + ":" + Bucket(m, X);
            return null;
        }

        static bool SplitPlace(string key, out string kind, out string mid, out string last)
        {
            kind = mid = last = null;
            if (key == null) return false;
            int a = key.IndexOf(':'), b = key.LastIndexOf(':');
            if (a < 0 || b <= a) return false;
            kind = key.Substring(0, a); mid = key.Substring(a + 1, b - a - 1); last = key.Substring(b + 1);
            return true;
        }

        public bool Resolve(string key, out Seg seg, out double x)
        {
            seg = null; x = 0;
            string kind, mid, last;
            if (!SplitPlace(key, out kind, out mid, out last)) return false;
            if (kind == "plank")
            {
                seg = World.Segs.Where(s => s.Kind == SegKind.Plank && s.App == mid).OrderByDescending(s => s.Width).FirstOrDefault();
                if (seg == null) return false;
                double m = BodyW * 0.6;
                if (seg.Width < m * 3) x = seg.Mid;
                else x = last == "L" ? seg.X1 + m : last == "R" ? seg.X2 - m : seg.Mid;
                return true;
            }
            var mon = World.Mons.FirstOrDefault(o => o.Key == mid);
            if (mon == null) return false;
            seg = World.Floor(mon.Index);
            if (seg == null) return false;
            if (kind == "floor")
            {
                int b; if (!int.TryParse(last, out b)) return false;
                x = mon.Work.Left + (b + 0.5) * mon.Work.Width / 10.0;
                return true;
            }
            if (kind == "wall")
            {
                if (last == "L" && !mon.WallLeft || last == "R" && !mon.WallRight) return false;
                x = last == "L" ? seg.X1 : seg.X2;
                return true;
            }
            return false;
        }

        public string DescribePlace(string key)
        {
            string kind, mid, last;
            if (key == null || key == "here" || !SplitPlace(key, out kind, out mid, out last)) return "wherever i was";
            if (kind == "plank")
            {
                string nick = Brain.Nick(mid);
                return (last == "L" ? "the left end of " : last == "R" ? "the right end of " : "the middle of ") + nick;
            }
            ScreenMemory sm;
            string screen = Brain.Screens.TryGetValue(mid, out sm) ? sm.Nick : "a screen i don't know";
            if (kind == "wall") return "the " + (last == "L" ? "left" : "right") + " wall of " + screen;
            if (kind == "ceil") return "the ceiling of " + screen;
            int bucket; int.TryParse(last, out bucket);
            string where = bucket <= 0 ? "the far-left corner" : bucket >= 9 ? "the far-right corner" : bucket <= 3 ? "the left side" : bucket >= 6 ? "the right side" : "the middle";
            return where + " of the floor in " + screen;
        }

        public static string ActPhrase(string act)
        {
            switch (act)
            {
                case "nap": return "a nap";
                case "dance": return "a little dance";
                case "sniff": return "sniffing a window";
                case "peek": return "peeking over an edge";
                case "climb": return "climbing the wall";
                case "patrol": return "a patrol";
                case "sit": return "sitting and thinking";
                case "explore": return "exploring";
                case "spin": return "spinning";
                case "sing": return "a song";
                case "count": return "counting the windows";
                case "visit": return "visiting the arrow";
                case "exercise": return "exercise";
                case "boop": return "booping the edge of the world";
                case "sunbathe": return "sunbathing";
                default: return "a stroll";
            }
        }

        static readonly HashSet<string> HereActs = new HashSet<string> { "wander", "patrol", "sit", "spin", "count", "exercise", "visit" };

        string ChoosePlace(string act, Dictionary<Seg, Planner.Node> reach)
        {
            if (HereActs.Contains(act)) return "here";
            var options = new List<KeyValuePair<string, double>>();
            foreach (var kv in reach)
            {
                Seg s = kv.Key; Planner.Node node = kv.Value;
                var m = World.MonByIndex(s.Mon);
                if (m == null) continue;
                bool knownScreen = Brain.Screens.ContainsKey(m.Key);
                if (act == "explore")
                {
                    foreach (var p in PlacesOn(s, m))
                    {
                        double visits; Brain.Visits.TryGetValue(p.Key, out visits);
                        double score = 1 / (1 + visits);
                        if (s.Kind == SegKind.Plank && !Brain.Apps.ContainsKey(s.App)) score *= 3;
                        if (!knownScreen) score *= 4;
                        if (s.Kind == SegKind.Floor && ((p.Key.EndsWith(":0") && m.WallLeft && !EdgeKnown(m, -1)) || (p.Key.EndsWith(":9") && m.WallRight && !EdgeKnown(m, 1)))) score *= 2.5;
                        double cost = node.Cost + Math.Abs(node.EntryX - p.Value) / (WalkSpeed * 1.5);
                        options.Add(new KeyValuePair<string, double>(p.Key, score * score / (1 + cost / 40)));
                    }
                    continue;
                }
                if (!knownScreen) continue;
                if (act == "climb" || act == "boop")
                {
                    if (s.Kind != SegKind.Floor) continue;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (!(side < 0 ? m.WallLeft : m.WallRight)) continue;
                        if (act == "boop" && !EdgeKnown(m, side)) continue;
                        string key = "wall:" + m.Key + (side < 0 ? ":L" : ":R");
                        double cost = node.Cost + Math.Abs(node.EntryX - (side < 0 ? s.X1 : s.X2)) / (WalkSpeed * 1.5);
                        options.Add(new KeyValuePair<string, double>(key, (0.4 + Brain.Love(act, key) * 1.5) / (1 + cost / 25)));
                    }
                    continue;
                }
                foreach (var p in PlacesOn(s, m))
                {
                    double pref = Pref(act, p.Key, s, m);
                    if (pref <= 0) continue;
                    double cost = node.Cost + Math.Abs(node.EntryX - p.Value) / (WalkSpeed * 1.5);
                    options.Add(new KeyValuePair<string, double>(p.Key, pref * (0.4 + Brain.Love(act, p.Key) * 1.5) / (1 + cost / 25)));
                }
            }
            if (options.Count == 0) return "here";
            return Rng.Weighted(options, kv => kv.Value).Key;
        }

        IEnumerable<KeyValuePair<string, double>> PlacesOn(Seg s, Mon m)
        {
            if (s.Kind == SegKind.Floor)
            {
                for (int b = 0; b < 10; b++)
                {
                    double x = m.Work.Left + (b + 0.5) * m.Work.Width / 10.0;
                    if (s.Contains(x, 0)) yield return new KeyValuePair<string, double>("floor:" + m.Key + ":" + b, x);
                }
            }
            else if (s.Kind == SegKind.Plank)
            {
                double mg = BodyW * 0.6;
                yield return new KeyValuePair<string, double>("plank:" + s.App + ":L", s.X1 + mg);
                yield return new KeyValuePair<string, double>("plank:" + s.App + ":M", s.Mid);
                yield return new KeyValuePair<string, double>("plank:" + s.App + ":R", s.X2 - mg);
            }
        }

        double Pref(string act, string key, Seg s, Mon m)
        {
            bool plank = s.Kind == SegKind.Plank;
            switch (act)
            {
                case "nap":
                    if (plank)
                    {
                        AppMemory mem; Brain.Apps.TryGetValue(s.App, out mem);
                        double trust = mem == null ? 0 : mem.StoodOn - 3 * mem.Dropped - mem.WalkedOff;
                        return trust < -2 ? 0.1 : 1.3 + Math.Min(5, Math.Max(0, trust)) * 0.1;
                    }
                    return key.EndsWith(":0") || key.EndsWith(":9") ? 1.4 : 0.6;
                case "sunbathe":
                    if (!plank || key.EndsWith(":L") || key.EndsWith(":R")) return 0;
                    double h = 1 - (s.Y - m.Work.Top) / (double)m.Work.Height;
                    return h * h * 3 + 0.05;
                case "sniff":
                    if (!plank || !key.EndsWith(":M")) return 0;
                    return Brain.Apps.ContainsKey(s.App) ? 1 : 3;
                case "peek":
                    return plank && !key.EndsWith(":M") ? 1 : 0;
                case "dance":
                case "sing":
                    return plank ? 1 : 1.1;
            }
            return 1;
        }

        void MarkPlace() { markedPlace = PlaceKeyHere(); }

        // =====================================================================
        // doing it
        // =====================================================================

        IEnumerable<Wait> Perform(string act, string place, bool asked)
        {
            CurrentAct = act;
            markedPlace = null;
            foreach (var w in Body(act, place)) yield return w;
            if (markedPlace != null) Completed(act, markedPlace, asked);   // only if it actually got to do the thing
            CurrentAct = null;
        }

        IEnumerable<Wait> Body(string act, string place)
        {
            switch (act)
            {
                case "nap": return Nap(place);
                case "dance": return Dance(place);
                case "peek": return Peek(place);
                case "sniff": return Sniff(place);
                case "climb": return Climb(place);
                case "patrol": return Patrol(place);
                case "sit": return Sit(place);
                case "explore": return Explore(place);
                case "spin": return Spin(place);
                case "sing": return Sing(place);
                case "count": return Count(place);
                case "visit": return Visit();
                case "exercise": return Exercise(place);
                case "boop": return Boop(place);
                case "sunbathe": return Sunbathe(place);
                default: return Wander(place);
            }
        }

        void Completed(string act, string place, bool asked)
        {
            string time = Brain.TimeBucket(Clock.Wall);
            if (!asked || Rng.Chance(0.5)) Brain.Reinforce(time, prevCtx, act);
            if (place != null)
            {
                Brain.AddLove(act, place, 1);
                double v; Brain.Visits.TryGetValue(place, out v); Brain.Visits[place] = v + 1;
            }
            history.Add(new[] { act, place ?? "here" });
            if (history.Count > 3) history.RemoveAt(0);
            if (history.Count == 3)
            {
                var rit = Brain.NoteTrigram(history[0][0], history[1][0], history[2][0], history[0][1], history[1][1], history[2][1], time, Clock.Unix);
                if (rit != null) announce = rit;
            }
            prevCtx = act;
            if (Clock.Simulated) SimLog("did " + act + " at " + DescribePlace(place) + " (" + time + ")");
        }

        /// <summary>Travel to a place. Sets navOk when it got there.</summary>
        IEnumerable<Wait> GoTo(string place, bool hurry)
        {
            navOk = false;
            if (place == null || place == "here") { navOk = true; yield break; }
            double speed = WalkSpeed * (hurry ? 2.2 : 1.5);
            double started = Clock.T;
            for (int hop = 0; hop < 14 && Clock.T - started < 150; hop++)
            {
                Seg goal; double gx;
                if (!Resolve(place, out goal, out gx)) yield break;
                if (Mode == Mode.Wall) { LetGo(); yield return Until(() => Mode == Mode.Ground, 6); continue; }
                if (Mode != Mode.Ground && Mode != Mode.Ceiling) yield break;
                if (Mode == Mode.Ground && (Support == goal || (Support.SameAs(goal) && goal.Contains(X, 0))))
                {
                    WalkTo(gx, Math.Abs(gx - X) > 600 * S ? RunSpeed : speed, false, false);
                    yield return Until(() => !MotorBusy || Mode != Mode.Ground, 90);
                    if (Mode == Mode.Ground && Math.Abs(X - ClampOnSupport(gx)) < 8 * S) { navOk = true; yield break; }
                    continue;
                }
                var plan = Planner.Plan(this, Planner.StartSeg(this), X, goal);
                if (plan == null || plan.Count == 0) yield break;
                foreach (var w in DoStep(plan[0], speed)) yield return w;
            }
        }

        IEnumerable<Wait> DoStep(NavStep step, double speed)
        {
            if (Mode == Mode.Ceiling)
            {
                CeilingWalkTo(step.TakeoffX);
                yield return Until(() => !MotorBusy || Mode != Mode.Ceiling, 90);
                if (step.Kind == "ceildrop" && Mode == Mode.Ceiling)
                {
                    if (Rng.Chance(0.4)) Say(Rng.Pick("geronimo", "bye, ceiling", "wheee"), false, 0);
                    LetGo();
                    yield return Until(() => Mode != Mode.Air, 8);
                }
                yield break;
            }
            if (step.Kind == "walk")
            {
                WalkTo(step.LandX, speed, true, true);
                yield return Until(() => !MotorBusy || Mode != Mode.Ground, 90);
                yield break;
            }
            WalkTo(step.TakeoffX, Math.Abs(step.TakeoffX - X) > 600 * S ? RunSpeed : speed, false, false);
            yield return Until(() => !MotorBusy || Mode != Mode.Ground, 90);
            if (Mode != Mode.Ground) yield break;
            if (step.Kind == "drop")
            {
                WalkTo(step.LandX, speed, true, true);
                yield return Until(() => Mode != Mode.Ground || !MotorBusy, 10);
                if (Mode == Mode.Air) yield return Until(() => Mode != Mode.Air, 8);
            }
            else if (step.Kind == "jump")
            {
                Facing = step.LandX >= X ? 1 : -1;
                Pose = Pose.Crouch; Squash = 0.22;
                yield return Pause(0.25);
                Pose = Pose.Stand;
                if (Mode != Mode.Ground) yield break;
                Jump(step.LandX, step.To.Y);
                yield return Until(() => Mode != Mode.Air, 6);
            }
            else if (step.Kind == "climb")
            {
                int side;
                if (!AtWall(out side)) yield break;
                StartClimb(side);
                if (Rng.Chance(0.3)) Say(Rng.Pick(Lines.ClimbStart), false, 0);
                yield return Until(() => Mode != Mode.Wall, 60);
            }
        }

        IEnumerable<Wait> GetDown()
        {
            LetGo();
            yield return Until(() => Mode != Mode.Air, 8);
        }

        void LookAt(double x, double y, double seconds)
        {
            lookOverX = x; lookOverY = y; lookOverUntil = Clock.T + seconds;
        }

        public bool LookOverride(out double tx, out double ty)
        {
            tx = ty = 0;
            if (Clock.T > lookOverUntil) return false;
            double dx = lookOverX - X, dy = lookOverY - (Y - BodyH * 0.6), d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1) return false;
            tx = dx / d; ty = dy / d;
            return true;
        }

        // ---------------------------------------------------------------------

        IEnumerable<Wait> Wander(string place)
        {
            MarkPlace();
            int n = Rng.Int(2, 5);
            for (int i = 0; i < n; i++)
            {
                if (Mode != Mode.Ground || Support == null) yield break;
                double tx = X + Rng.Range(-260, 260) * S;
                bool risky = Support.Kind == SegKind.Plank && !Brain.App(Support.App).EdgesKnown && Rng.Chance(0.45);
                if (risky) tx = Rng.Chance(0.5) ? Support.X1 - BodyW : Support.X2 + BodyW;
                WalkTo(tx, WalkSpeed, risky, false);
                yield return Until(() => !MotorBusy || Mode != Mode.Ground, 30);
                yield return Pause(Rng.Range(0.8, 3));
                if (Rng.Chance(0.15 * Brain.Traits.Chatty)) { lastMusing = Clock.T; yield return Say(Musing(), Rng.Chance(0.5), 0); }
            }
        }

        IEnumerable<Wait> Nap(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            if (Rng.Chance(0.6)) yield return Say(Rng.Pick(Lines.NapStart), false, 0);
            Pose = Pose.Lie; Face = Face.Sleep;
            if (Support != null && Support.Kind == SegKind.Plank) Brain.App(Support.App).Naps++;
            string time = Brain.TimeBucket(Clock.Wall);
            double dur = Rng.Range(35, 120) * (time == "night" || time == "wee hours" ? 2 : 1) * (0.7 + Brain.Traits.Sleepy * 0.6);
            double end = Clock.T + dur;
            while (Clock.T < end)
            {
                Emit("z", 1);
                energy = Math.Min(1, energy + 0.04);
                yield return Pause(Rng.Range(1.8, 2.8));
            }
            energy = 1;
            Pose = Pose.Stand; Face = Face.Normal; Squash = -0.2;
            if (Rng.Chance(0.6)) yield return Say(Rng.Pick(Lines.Wake), false, 0);
        }

        IEnumerable<Wait> Dance(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            Pose = Pose.Dance; Face = Face.Happy;
            if (Rng.Chance(0.5)) Say(Rng.Pick(Lines.Dance), false, 0);
            double end = Clock.T + Rng.Range(5, 10);
            while (Clock.T < end) { Emit("note", 1); yield return Pause(0.7); }
            Pose = Pose.Stand; Face = Face.Normal;
            if (Rng.Chance(0.4)) { Pose = Pose.Wave; yield return Say(Rng.Pick("ta-da.", "thank you, thank you."), false, 1.6); }
        }

        IEnumerable<Wait> Peek(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground || Support == null || Support.Kind != SegKind.Plank) yield break;
            MarkPlace();
            int side = place != null && place.EndsWith(":L") ? -1 : 1;
            WalkTo(side < 0 ? Support.X1 : Support.X2, WalkSpeed * 0.5, false, false);
            yield return Until(() => !MotorBusy || Mode != Mode.Ground, 10);
            if (Mode != Mode.Ground || Support == null) yield break;
            Facing = side; Pose = Pose.Peek;
            yield return Pause(1.2);
            yield return Say(Rng.Pick(Lines.Peek), false, 0);
            yield return Pause(Rng.Range(1.5, 3.5));
            if (Support != null && Support.App != null) Brain.App(Support.App).EdgesKnown = true;
        }

        IEnumerable<Wait> Sniff(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground || Support == null || Support.Kind != SegKind.Plank) yield break;
            MarkPlace();
            string app = Support.App;
            bool first = !Brain.Apps.ContainsKey(app);
            var mem = Brain.App(app);
            Pose = Pose.Sniff;
            double dir = Rng.Chance(0.5) ? 1 : -1;
            for (int i = 0; i < 3; i++)
            {
                WalkTo(X + dir * Rng.Range(30, 80) * S, WalkSpeed * 0.35, false, false);
                Emit("puff", 1);
                yield return Until(() => !MotorBusy || Mode != Mode.Ground, 6);
                Emit("puff", 1);
                yield return Pause(0.5);
            }
            Pose = Pose.Stand;
            if (first) { Journal.Add("sniffed a new window (" + app + "). i call it " + mem.Nick + "."); yield return Say("this is " + mem.Nick + " now.", false, 0); }
            else yield return Say(Lines.FillPick(Lines.Sniff, this, app), false, 0);
        }

        IEnumerable<Wait> Climb(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            int side;
            if (!AtWall(out side)) yield break;
            markedPlace = place;
            StartClimb(side);
            if (Rng.Chance(0.6)) Say(Rng.Pick(Lines.ClimbStart), false, 0);
            bool top = Rng.Chance(0.35 + Brain.Traits.Bold * 0.5);
            if (!top) ClimbTo(Y - Rng.Range(150, 500) * S);
            yield return Until(() => Mode != Mode.Wall || !Climbing, 60);
            if (Mode == Mode.Ceiling)
            {
                yield return Say(Rng.Pick(Lines.Ceiling), false, 0);
                CeilingWalkTo(X - WallSide * Rng.Range(150, 600) * S);
                yield return Until(() => !MotorBusy || Mode != Mode.Ceiling, 30);
                yield return Pause(Rng.Range(0.5, 2));
                if (Mode != Mode.Ceiling) yield break;
                if (Rng.Chance(0.5)) Say(Rng.Pick("wheee", "bye, ceiling"), false, 0);
                LetGo();
                yield return Until(() => Mode != Mode.Air, 8);
            }
            else if (Mode == Mode.Wall)
            {
                yield return Pause(Rng.Range(1, 3));
                if (Rng.Chance(0.5)) yield return Say("i can see everything from here.", false, 0);
                if (Rng.Chance(0.5)) { ClimbTo(1e9); yield return Until(() => Mode != Mode.Wall, 60); }
                else { LetGo(); yield return Until(() => Mode != Mode.Air, 8); }
            }
        }

        IEnumerable<Wait> Patrol(string place)
        {
            if (Support == null) yield break;
            MarkPlace();
            double a = Math.Max(Support.X1, X - 350 * S), b = Math.Min(Support.X2, X + 350 * S);
            WalkTo(a, WalkSpeed * 1.2, false, false);
            yield return Until(() => !MotorBusy || Mode != Mode.Ground, 20);
            if (Mode != Mode.Ground || Support == null) yield break;
            yield return Pause(0.8);
            WalkTo(b, WalkSpeed * 1.2, false, false);
            yield return Until(() => !MotorBusy || Mode != Mode.Ground, 20);
            if (Mode != Mode.Ground) yield break;
            Pose = Pose.Wave;
            yield return Say(Rng.Pick(Lines.Patrol), false, 0);
        }

        IEnumerable<Wait> Sit(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            Pose = Pose.Sit; Face = Face.Content;
            double end = Clock.T + Rng.Range(8, 25);
            bool said = false;
            while (Clock.T < end)
            {
                yield return Pause(Rng.Range(2, 5));
                if (!said && Rng.Chance(0.35 * Brain.Traits.Chatty)) { said = true; lastMusing = Clock.T; Say(Musing(), Rng.Chance(0.6), 0); }
            }
        }

        IEnumerable<Wait> Explore(string place)
        {
            if (Rng.Chance(0.6)) yield return Say(Rng.Pick(Lines.Explore), false, 0);
            foreach (var w in GoTo(place, true)) yield return w;
            if (!navOk)
            {
                if (Mode == Mode.Ground && Rng.Chance(0.5)) yield return Say(Rng.Pick(Lines.Unreachable), false, 0);
                yield break;
            }
            MarkPlace();
            LookAt(X - 400 * S, Y - 200 * S, 1.2);
            yield return Pause(1.2);
            LookAt(X + 400 * S, Y - 200 * S, 1.2);
            yield return Pause(1.2);
            if (Rng.Chance(0.3)) yield return Say("hm.", true, 0);
        }

        IEnumerable<Wait> Spin(string place)
        {
            MarkPlace();
            Pose = Pose.Spin;
            yield return Pause(Rng.Range(1.8, 3));
            Pose = Pose.Stand; Face = Face.Dizzy;
            yield return Say(Rng.Pick("whoa.", "the room is spinning. is that normal.", "again. no. never again."), false, 0);
            yield return Pause(1.5);
        }

        IEnumerable<Wait> Sing(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            Pose = Pose.Sing; Face = Face.Happy;
            for (int i = 0; i < 2; i++)
            {
                Emit("note", 2);
                yield return Say(Lines.FillPick(Lines.Songs, this, null), false, 3);
                Emit("note", 1);
                yield return Pause(1.2);
            }
        }

        IEnumerable<Wait> Count(string place)
        {
            MarkPlace();
            var wins = World.Wins.Take(10).ToList();
            if (wins.Count == 0) yield break;
            yield return Say("let's see.", false, 1.2);
            int n = 0;
            bool named = false;
            foreach (var w in wins)
            {
                n++;
                if (n > 9) { yield return Say("many.", false, 1.6); yield break; }
                LookAt((w.R.Left + w.R.Right) / 2.0, (w.R.Top + w.R.Bottom) / 2.0, 1.3);
                string extra = "";
                if (!named && !Brain.Apps.ContainsKey(w.App))
                {
                    named = true;
                    var mem = Brain.App(w.App);
                    extra = " (that's " + mem.Nick + ")";
                    Journal.Add("counted the windows and named a new one (" + w.App + "): " + mem.Nick + ".");
                }
                yield return Say(Lines.Number(n) + "." + extra, false, extra.Length > 0 ? 2.6 : 1.1);
            }
            yield return Say(n == 1 ? "one window. lonely." : Lines.Number(n) + " windows. correct.", false, 0);
        }

        IEnumerable<Wait> Visit()
        {
            if (userAway || Clock.Simulated) yield break;
            if (Brain.Trust < -0.3)
            {
                Facing = CursorX > X ? -1 : 1;
                yield return Say("nope.", false, 1.2);
                WalkTo(X + Facing * 300 * S, RunSpeed, false, false);
                yield return Until(() => !MotorBusy, 10);
                yield break;
            }
            double cx = CursorX, cy = CursorY;
            Seg target = World.LandingBelow(cx, cy, null);
            if (target == null || target.Kind == SegKind.Ceiling) yield break;
            if (Mode == Mode.Ground && Support != null && (Support == target || Support.SameAs(target)))
            {
                WalkTo(cx, WalkSpeed * 1.8, false, false);
                yield return Until(() => !MotorBusy || Mode != Mode.Ground, 40);
            }
            else
            {
                var m = World.MonByIndex(target.Mon);
                string key = target.Kind == SegKind.Floor && m != null ? "floor:" + m.Key + ":" + Bucket(m, cx)
                           : "plank:" + target.App + ":" + ((cx - target.X1) / Math.Max(1, target.Width) < 0.34 ? "L" : (cx - target.X1) / Math.Max(1, target.Width) > 0.66 ? "R" : "M");
                foreach (var w in GoTo(key, true)) yield return w;
            }
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            LookAt(CursorX, CursorY, 3);
            Face = Face.Happy;
            yield return Say(Rng.Pick(Lines.Visit), false, 0);
            if (Brain.Trust > 0.4) Emit("heart", 1);
            yield return Pause(1.5);
        }

        IEnumerable<Wait> Exercise(string place)
        {
            MarkPlace();
            Pose = Pose.Exercise;
            int reps = Rng.Int(3, 7);
            for (int i = 1; i <= reps; i++)
            {
                Squash = 0.35; SquashV = 0;
                yield return Pause(0.35);
                Squash = -0.25; SquashV = 0;
                if (i <= 3 || Rng.Chance(0.3)) Say(Lines.Number(i) + ".", false, 0.8);
                if (i == reps - 1) Emit("sweat", 1);
                yield return Pause(0.45);
            }
            Pose = Pose.Stand;
            if (Rng.Chance(0.6)) yield return Say(Rng.Pick(Lines.Exercise), false, 0);
        }

        IEnumerable<Wait> Boop(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            int side;
            if (!AtWall(out side)) yield break;
            markedPlace = place;
            Facing = side; Pose = Pose.Wave;
            yield return Pause(0.4);
            Squash = 0.1;
            var m = World.MonByIndex(Support.Mon);
            if (m != null) { var e = Brain.Edge(m.Key + (side < 0 ? ":L" : ":R")); if (e != null) e.Boops++; }
            yield return Say("boop.", false, 1.2);
            yield return Pause(0.8);
            if (Rng.Chance(0.3)) yield return Say(Rng.Pick("still there.", "just checking.", "good edge."), false, 0);
        }

        IEnumerable<Wait> Sunbathe(string place)
        {
            foreach (var w in GoTo(place, false)) yield return w;
            if (Mode != Mode.Ground || Support == null || Support.Kind != SegKind.Plank) yield break;
            MarkPlace();
            Pose = Pose.Sunbathe; Sunglasses = true; Face = Face.Content;
            if (Rng.Chance(0.7)) yield return Say(Rng.Pick(Lines.Sunbathe), false, 0);
            yield return Pause(Rng.Range(15, 45));
        }

        // =====================================================================
        // reactions
        // =====================================================================

        IEnumerable<Wait> BonkReaction(Mon m, int side, bool first)
        {
            Face = Face.Squint; Squash = 0.45; SquashV = 0; Tilt = -side * 8;
            Emit("star", 5);
            yield return Say(Rng.Pick(Lines.Bonk), false, 1.4);
            yield return Pause(0.6);
            Face = Face.Normal; Tilt = 0; Facing = -side;
            string sideWord = side < 0 ? "left" : "right";
            yield return Say(Rng.Pick("the world ends here. noted.", "ok. that's the " + sideWord + " edge.", "i'll remember that wall."), false, 0);
            if (first) Journal.Add("found the " + sideWord + " edge of " + ScreenNick(m) + ". with my face.");
        }

        string ScreenNick(Mon m)
        {
            ScreenMemory sm;
            return m != null && Brain.Screens.TryGetValue(m.Key, out sm) ? sm.Nick : "this screen";
        }

        IEnumerable<Wait> FallLanding(string reason, string app, double height)
        {
            if (height > 350 * S)
            {
                Face = Face.Dizzy;
                yield return Say(Rng.Pick(Lines.HardLanding), false, 1.6);
                yield return Pause(1.0);
                Face = Face.Normal;
            }
            if (reason == "walkoff" && app != null)
            {
                var mem = Brain.App(app);
                mem.WalkedOff++;
                bool firstTime = !mem.EdgesKnown;
                mem.EdgesKnown = true;
                yield return Say(firstTime ? mem.Nick + " has edges. noted." : Rng.Pick("again?", "i knew that. i did."), false, 0);
                if (firstTime) Journal.Add("walked off the edge of " + mem.Nick + ". now i know where it ends.");
            }
            else if (app != null && Rng.Chance(0.8))
            {
                yield return Say(Lines.FillPick(Rng.Chance(0.5) ? Personality.Violations : Lines.Dropped, this, app), false, 0);
                if (Brain.App(app).Dropped == 3)
                {
                    yield return Pause(0.5);
                    yield return Say("i don't trust " + Brain.Nick(app) + " anymore.", false, 0);
                    Journal.Add("decided " + Brain.Nick(app) + " can't be trusted. it keeps leaving while i'm on it.");
                }
            }
        }

        IEnumerable<Wait> ThrowLanding(bool wasThrown, double height)
        {
            if (wasThrown)
            {
                bool likes = Brain.Traits.Bold > 0.55;
                Brain.Trust = Math.Max(-1, Math.Min(1, Brain.Trust + (likes ? 0.02 : -0.05)));
                Face = likes ? Face.Happy : Face.Dizzy;
                yield return Say(likes ? Rng.Pick("again!", "that was the best day of my life.", "WHEEE. ok. again.")
                                       : Rng.Pick("rude.", "i'll remember that.", "my everything is dizzy."), false, 0);
                if (Brain.Throws == 1) Journal.Add("you threw me. " + (likes ? "it was amazing." : "i'm choosing to forgive you."));
                yield return Pause(1.2);
            }
            else
            {
                Brain.Trust = Math.Min(1, Brain.Trust + 0.01);
                if (height > 400 * S) { Face = Face.Dizzy; yield return Say("oof.", false, 1.2); }
                else if (Rng.Chance(0.5)) yield return Say(Rng.Pick("thanks for the lift.", "new neighbourhood.", "hm. ok. i live here now."), false, 0);
            }
        }

        IEnumerable<Wait> WelcomeBack(double mins)
        {
            Face = Face.Happy; Squash = -0.35;
            Emit("heart", 2);
            yield return Say(Rng.Pick(Lines.Welcome), false, 0);
            yield return Pause(1.5);
        }

        IEnumerable<Wait> PetReaction()
        {
            Face = Face.Happy; Blush = 1; Squash = 0.15;
            if (Rng.Chance(0.45)) yield return Say(Rng.Pick(Lines.Petted), false, 1.8);
            yield return Pause(1.4);
        }

        IEnumerable<Wait> RunRitual(Ritual r)
        {
            ritualRunning = r;
            yield return Say(r.Done == 0 ? "oh! it's time for " + r.Name + "." : Rng.Pick("time for " + r.Name + ".", "ah. " + r.Name + " o'clock.", r.Name + ". as is tradition."), false, 0);
            Emit("sparkle", 3);
            for (int i = 0; i < r.Acts.Count; i++)
            {
                string place = i < r.Places.Count ? r.Places[i] : null;
                Seg s; double x;
                if (place != null && place != "here" && !Resolve(place, out s, out x)) place = null;   // not around today; improvise
                if (place == null) place = ChoosePlace(r.Acts[i], Planner.Reachable(this));
                ritualRunning = r;
                foreach (var w in Perform(r.Acts[i], place, false)) yield return w;
                yield return Pause(Rng.Range(0.6, 1.5));
            }
            r.Done++;
            r.LastDone = Clock.Unix;
            Pose = Pose.Wave;
            yield return Say(Rng.Pick("tradition.", "perfect as always.", "and scene."), false, 1.8);
            Journal.Add("did " + r.Name + " (" + (r.Done == 1 ? "the first time on purpose" : "time #" + r.Done) + ").");
            if (Clock.Simulated) SimLog("*** performed ritual " + r.Name);
            ritualRunning = null;
        }

        IEnumerable<Wait> Announce(Ritual r)
        {
            Face = Face.Surprised;
            yield return Say("wait.", false, 1.3);
            yield return Pause(0.5);
            Face = Face.Think;
            yield return Say("i always do this.", true, 2.2);
            Face = Face.Happy;
            Emit("sparkle", 4);
            yield return Say("this is " + r.Name + " now.", false, 0);
            string steps = string.Join(", then ", r.Acts.Select((a, i) => ActPhrase(a) + PlaceSuffix(i < r.Places.Count ? r.Places[i] : null)));
            Journal.Add("i have a new tradition: " + r.Name + ". " + steps + ".");
            Brain.Remember("new tradition: " + r.Name);
            if (Clock.Simulated) SimLog("*** NEW RITUAL " + r.Name + ": " + steps);
            else { Brain.Save(); WriteReadableBrain(); }
        }

        public string PlaceSuffix(string place)
        {
            if (place == null || place == "here") return "";
            string d = DescribePlace(place);
            return (place.StartsWith("plank:") ? " on " : place.StartsWith("wall:") ? " at " : " in ") + d;
        }

        // =====================================================================
        // lifecycle & outside requests
        // =====================================================================

        public void Begin()
        {
            Rescan();
            var primary = World.Mons.FirstOrDefault(m => m.Primary) ?? World.Mons.FirstOrDefault();
            if (Brain.IsNewborn)
            {
                X = primary != null ? primary.CenterX : 500; PutOnFloor(primary);
                X = primary != null ? primary.CenterX : 500;
                Mode = Mode.Egg; Pose = Pose.Egg;
                Interrupt(Hatch(), "start");
            }
            else
            {
                Mon spot = primary; double offset = -1;
                if (Brain.LastSpot != null)
                {
                    var parts = Brain.LastSpot.Split('|');
                    var m = World.Mons.FirstOrDefault(o => o.Key == parts[0]);
                    if (m != null && parts.Length > 1 && double.TryParse(parts[1], out offset)) spot = m;
                    else offset = -1;
                }
                X = offset >= 0 && spot != null ? spot.Work.Left + offset : (spot != null ? spot.CenterX : 500);
                PutOnFloor(spot);
                Pose = Pose.Lie; Face = Face.Sleep;
                Interrupt(WakeUp(), "start");
            }
            Brain.Sessions++;
            Brain.DailyDecay();
        }

        public int EggCrack;
        public double EggWobble;

        IEnumerable<Wait> Hatch()
        {
            Mode = Mode.Egg; Pose = Pose.Egg; EggCrack = 0;
            yield return Pause(1.2);
            for (int i = 1; i <= 3; i++)
            {
                EggWobble = 1;
                yield return Pause(0.8);
                EggCrack = i;
                yield return Pause(0.7);
            }
            EggWobble = 1.5;
            yield return Pause(0.6);
            Mode = Mode.Ground; Pose = Pose.Stand; Face = Face.Surprised;
            Squash = -0.45; SquashV = 0;
            Emit("sparkle", 6);
            for (int i = 0; i < 6; i++)
                Particles.Add(new Particle { Kind = "shell", X = X + Rng.Range(-10, 10) * S, Y = Y - BodyH * 0.8, VX = Rng.Range(-120, 120) * S, VY = Rng.Range(-220, -120) * S, Life = 1.2, MaxLife = 1.2, Size = Rng.Range(0.6, 1.1), Rot = Rng.Range(0, 6) });
            NoticeScreen();
            yield return Pause(1.0);
            Face = Face.Normal;
            yield return Say("hello.", false, 1.8);
            yield return Pause(0.5);
            yield return Say("i am " + Brain.Name + ".", false, 2.2);
            yield return Pause(0.4);
            Face = Face.Think;
            yield return Say("where am i.", true, 2.4);
            Face = Face.Normal;
            Journal.Add("i hatched. my name is " + Brain.Name + ". i live here now.");
        }

        IEnumerable<Wait> WakeUp()
        {
            yield return Pause(Rng.Range(1.5, 3));
            Pose = Pose.Stand; Face = Face.Normal; Squash = -0.25;
            yield return Say(Rng.Pick("*yawn*", "i'm back.", "what did i miss.", "morning. probably.", "hello again."), false, 0);
            Journal.Add("woke up in " + DescribePlace(PlaceKeyHere()) + ".");
        }

        /// <summary>Set by the app: ask the assistant to peek at the screen and make a remark. Returns false if it won't.</summary>
        public Func<bool> PeekRequest;

        static readonly string[] Private = { "1password", "bitwarden", "keepass", "lastpass", "dashlane", "inprivate", "incognito", "private browsing",
            "bank", "paypal", "venmo", "sign in", "log in", "login", "password", "checkout", "payment", "tax", "medical", "health" };

        /// <summary>Wander over to whatever you're doing, peer at it, and (if the assistant is on) say something about it.</summary>
        public IEnumerable<Wait> NosyLook()
        {
            var fg = World.Foreground;
            if (fg == null || fg.Z < 0) yield break;
            string hay = (fg.App + " " + fg.Title).ToLowerInvariant();
            if (Private.Any(p => hay.Contains(p))) yield break;   // none of my business
            double tx = (fg.R.Left + fg.R.Right) / 2.0, ty = (fg.R.Top + fg.R.Bottom) / 2.0;
            if (Mode == Mode.Ground && Support != null && Support.Contains(tx, 0))
            {
                WalkTo(tx + Rng.Range(-160, 160) * S, WalkSpeed * 1.4, false, false);
                yield return Until(() => !MotorBusy || Mode != Mode.Ground, 25);
            }
            if (Mode != Mode.Ground) yield break;
            MarkPlace();
            Facing = tx >= X ? 1 : -1;
            LookAt(tx, ty, 6);
            Tilt = Facing * 7;
            Emit("what", 1);
            yield return Pause(1.4);
            bool asked = false;
            try { asked = PeekRequest != null && PeekRequest(); } catch { }
            if (!asked)
            {
                // no assistant (or it declined): a free, canned remark about the tenant
                string nick = fg.App != null && fg.App != "?" ? Brain.Nick(fg.App) : "that window";
                Say(Rng.Pick("just inspecting " + nick + ".", nick + " again? you really like " + nick + ".", "hm. " + nick + ". carry on.",
                             "i see you're busy with " + nick + ". very official.", "nothing to see. i saw it."), Rng.Chance(0.4), 0);
            }
            yield return Pause(asked ? 5 : 2);
        }

        /// <summary>Someone's pressing on it: stop in place (so it doesn't walk out from under the cursor).</summary>
        public void Freeze()
        {
            if (Mode != Mode.Ground) return;
            StopWalking();
            AbortActivity();
        }

        /// <summary>Look at a spot on the screen and say something about it ("there it is. top right.").</summary>
        public void PointAt(double x, double y, string line)
        {
            LookAt(x, y, 3.5);
            if (Mode == Mode.Ground && Pose != Pose.Lie) { Facing = x >= X ? 1 : -1; Squash = -0.2; }
            if (!string.IsNullOrEmpty(line)) Say(line, false, 2.8);
        }

        public void Summon(double x)
        {
            if (Mode == Mode.Held || Mode == Mode.Egg || Mode == Mode.Hidden) return;
            var m = World.MonAt(x, CursorY);
            if (m == null) return;
            AbortActivity();
            X = Math.Max(m.Work.Left + BodyW, Math.Min(m.Work.Right - BodyW, x));
            Y = m.Work.Top + BodyH * 1.2;
            StartAir(0, 0, true);
            RotTarget = 0; Rot = 0;
            Say(Rng.Pick("coming!", "on my way. from above.", "wheee"), false, 0);
        }

        public IEnumerable<Wait> Farewell()
        {
            if (Mode == Mode.Wall || Mode == Mode.Ceiling) { LetGo(); yield return Until(() => Mode != Mode.Air, 3); }
            Pose = Pose.Wave; Face = Face.Happy;
            yield return Say(Rng.Pick("bye. i'll be here. well, not here. but.", "goodnight. don't move the windows.", "ok bye. keep my spot warm."), false, 3);
            yield return Pause(5);
        }

        public void Pet()
        {
            Brain.Pets++;
            Brain.Trust = Math.Min(1, Brain.Trust + 0.015);
            if (Brain.Pets == 1 || Brain.Pets == 10 || Brain.Pets == 50 || Brain.Pets % 100 == 0)
                Journal.Add(Brain.Pets == 1 ? "you petted me for the first time." : "you've petted me " + Brain.Pets + " times now.");
            Blush = 1;
            Emit("heart", 1);
            if (Mode != Mode.Ground || Pose == Pose.Lie) return;
            Interrupt(PetReaction(), "petted");
        }

        public bool DoTrick(string trick)
        {
            trick = (trick ?? "").Trim().ToLowerInvariant();
            if (!Acts.Contains(trick) || Mode == Mode.Held || Mode == Mode.Hidden || Mode == Mode.Egg) return false;
            Interrupt(Trick(trick), "asked");
            return true;
        }

        IEnumerable<Wait> Trick(string act)
        {
            if (Mode == Mode.Wall || Mode == Mode.Ceiling) { LetGo(); yield return Until(() => Mode != Mode.Air, 8); }
            yield return Say(Rng.Pick("ok.", "on it.", "for you? fine."), false, 1.2);
            foreach (var w in Perform(act, ChoosePlace(act, Planner.Reachable(this)), true)) yield return w;
        }

        public void SetMood(string mood)
        {
            mood = string.IsNullOrEmpty(mood) ? "idle" : mood;
            if (mood == Mood) return;
            Mood = mood;
            if (mood == "thinking" || mood == "listening")
            {
                if (prevCtx != "chat") AbortActivity();
                prevCtx = "chat";
                if (Mode == Mode.Ground) { Pose = Pose.Stand; StopWalking(); }
                Face = mood == "thinking" ? Face.Think : Face.Surprised;
                Say(mood == "thinking" ? "…" : "listening…", true, 90);
                if (mood == "listening") Squash = -0.2;
            }
            else
            {
                if (BubbleThought && (BubbleText == "…" || BubbleText == "listening…")) BubbleText = null;
                if (Face == Face.Think || Face == Face.Surprised) Face = Face.Normal;
            }
        }

        // =====================================================================
        // what it knows, in words (for the assistant and the diary)
        // =====================================================================

        public string TraitWords()
        {
            var t = Brain.Traits;
            return string.Join(", ", new[] {
                Brain.Describe(t.Curious, "not very nosy", "a bit nosy", "extremely nosy"),
                Brain.Describe(t.Sleepy, "rarely sleepy", "nap-inclined", "professionally sleepy"),
                Brain.Describe(t.Bold, "cautious", "brave-ish", "fearless"),
                Brain.Describe(t.Chatty, "quiet", "chatty", "won't stop talking"),
                Brain.Describe(t.Silly, "a little silly", "silly", "deeply silly"),
                Brain.Describe(t.Cuddly, "independent", "likes pets", "extremely cuddly") });
        }

        public string FeelingsAboutUser()
        {
            double tr = Brain.Trust;
            return tr > 0.6 ? "adores you" : tr > 0.3 ? "trusts you" : tr > 0 ? "thinks you're alright" : tr > -0.3 ? "is a little wary of you" : "is suspicious of your arrow";
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("name: " + Brain.Name + ". hatched " + Brain.Born + " (" + Brain.Age() + " ago), " + Brain.Sessions + " wake-ups.");
            sb.AppendLine("character: " + Personality.OneLiner);
            sb.AppendLine("temperament: " + TraitWords() + ".");
            sb.AppendLine("feelings: " + FeelingsAboutUser() + " (petted " + Brain.Pets + "x, picked up " + Brain.Pickups + "x, thrown " + Brain.Throws + "x).");
            if (Brain.Screens.Count > 0)
                sb.AppendLine("screens it knows: " + string.Join(", ", Brain.Screens.Values.Select(s => s.Nick)) + ".");
            var apps = Brain.Apps.OrderByDescending(kv => kv.Value.StoodOn).Take(12).ToList();
            if (apps.Count > 0)
                sb.AppendLine("its nicknames for your apps: " + string.Join("; ", apps.Select(kv => kv.Key + " = " + kv.Value.Nick)) + ".");
            var favs = new List<string>();
            foreach (var act in new[] { "nap", "dance", "sing", "sunbathe", "sniff" })
            {
                string p = Brain.FavoritePlace(act);
                if (p != null && Brain.Love(act, p) >= 2) favs.Add(act + " at " + DescribePlace(p));
            }
            if (favs.Count > 0) sb.AppendLine("favorite spots: " + string.Join("; ", favs) + ".");
            foreach (var r in Brain.Rituals)
                sb.AppendLine("tradition \"" + r.Name + "\": " + string.Join(", then ", r.Acts.Select((a, i) => ActPhrase(a) + PlaceSuffix(i < r.Places.Count ? r.Places[i] : null)))
                    + " (usually in the " + r.When + ", done " + r.Done + "x).");
            if (NowPlaying != null) sb.AppendLine("now playing: \u201c" + NowPlaying.Title + "\u201d by " + NowPlaying.Artist + " (it rates it " + Music.Rating(NowPlaying) + " out of 7).");
            if (Brain.Artists.Count > 0)
                sb.AppendLine("music you play most: " + string.Join(", ", Brain.Artists.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key)) + ".");
            foreach (var h in Brain.StrongHabits(4))
                sb.AppendLine("habit: after " + CtxPhrase(h[0]) + " it usually does " + ActPhrase(h[1]) + ".");
            sb.AppendLine("right now: " + (CurrentAct != null ? ActPhrase(CurrentAct) : "idling") + " in/on " + DescribePlace(PlaceKeyHere()) + ", energy " + (int)(energy * 100) + "%.");
            return sb.ToString();
        }

        /// <summary>A human-readable copy of the brain, next to the rest of its folder (Documents\Bestie\brain.md).</summary>
        public void WriteReadableBrain()
        {
            if (Clock.Simulated) return;
            var sb = new StringBuilder();
            sb.AppendLine("# " + Brain.Name + "'s brain");
            sb.AppendLine();
            sb.AppendLine("_updated " + DateTime.Now.ToString("MMM d, h:mm tt") + ". a readable copy of `.state\\brain.json` (editing this file changes nothing; edit `memory\\` instead)._");
            sb.AppendLine();
            sb.AppendLine("## what i know");
            foreach (var line in Summary().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) sb.AppendLine("- " + line);
            sb.AppendLine();
            sb.AppendLine("## tenants (windows i've met)");
            foreach (var kv in Brain.Apps.OrderByDescending(kv => kv.Value.StoodOn))
                sb.AppendLine("- **" + kv.Value.Nick + "** (" + kv.Key + "): met " + kv.Value.Met + ", stood on it " + kv.Value.StoodOn + "×, napped " + kv.Value.Naps + "×, it dropped me " + kv.Value.Dropped + "×" + (kv.Value.EdgesKnown ? ", i know where it ends" : ""));
            sb.AppendLine();
            sb.AppendLine("## edges of the world");
            foreach (var kv in Brain.Edges) sb.AppendLine("- " + kv.Key + ": found " + kv.Value.Found + ", bonked " + kv.Value.Bonks + "×, booped " + kv.Value.Boops + "×");
            if (Brain.Artists.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## music you play");
                foreach (var kv in Brain.Artists.OrderByDescending(kv => kv.Value).Take(25)) sb.AppendLine("- " + kv.Key + " (" + (int)kv.Value + ")");
            }
            sb.AppendLine();
            sb.AppendLine("## things i said lately");
            foreach (var t in Enumerable.Reverse(Brain.Thoughts).Take(20)) sb.AppendLine("- " + t.At + " — " + t.Text);
            try
            {
                string path = System.IO.Path.Combine(Home.Root, "brain.md");
                string text = sb.ToString();
                if (!System.IO.File.Exists(path) || System.IO.File.ReadAllText(path) != text) System.IO.File.WriteAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex) { DebugLog.Write("brain.md: " + ex.Message); }
        }

        public static string CtxPhrase(string ctx)
        {
            switch (ctx)
            {
                case "start": return "waking up";
                case "return": return "you come back";
                case "petted": return "being petted";
                case "fell": return "falling";
                case "thrown": return "being thrown";
                case "carried": return "being carried somewhere";
                case "bonk": return "bonking into a wall";
                case "chat": return "talking with you";
                case "asked": return "doing a trick for you";
                case "discovery": return "discovering something";
                default: return ActPhrase(ctx);
            }
        }

        // ---- headless simulation (for testing the mind without a screen) ----

        public static System.IO.TextWriter SimOut;
        void SimLog(string s) { if (SimOut != null) SimOut.WriteLine(Clock.Wall.ToString("HH:mm:ss") + "  " + s); }
    }
}
