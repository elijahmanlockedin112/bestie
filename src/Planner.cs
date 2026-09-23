using System;
using System.Collections.Generic;
using System.Linq;

namespace Bestie
{
    public class NavStep
    {
        public string Kind;          // walk (onto an adjacent surface), drop, jump, climb (wall -> ceiling), ceildrop
        public Seg To;
        public double TakeoffX, LandX;
        public override string ToString() { return Kind + " -> " + To + " @" + (int)TakeoffX + "->" + (int)LandX; }
    }

    /// <summary>Route-finding over the surfaces of the desktop: walk, hop between windows, drop off edges,
    /// or climb a wall and shuffle along the ceiling to drop onto something unreachable from below.</summary>
    public static class Planner
    {
        public class Node
        {
            public Seg Seg;
            public double Cost, EntryX;
            public NavStep Via;
            public Node Prev;
            public bool Settled;
        }

        public static Seg StartSeg(Creature c)
        {
            if (c.Mode == Mode.Ground) return c.Support;
            if (c.Mode == Mode.Ceiling) return c.World.Ceiling(c.HangMon);
            return null;
        }

        /// <summary>Dijkstra from where the creature is. Stops early once <paramref name="goal"/> is settled.</summary>
        public static Dictionary<Seg, Node> Search(Creature c, Seg start, double x, Seg goal)
        {
            var nodes = new Dictionary<Seg, Node>();
            if (start == null) return nodes;
            nodes[start] = new Node { Seg = start, EntryX = x };
            while (true)
            {
                Node cur = null;
                foreach (var n in nodes.Values)
                    if (!n.Settled && (cur == null || n.Cost < cur.Cost)) cur = n;
                if (cur == null) break;
                cur.Settled = true;
                if (cur.Seg == goal) break;
                double speed = cur.Seg.Kind == SegKind.Ceiling ? c.WalkSpeed * 0.8 : c.WalkSpeed * 1.5;
                foreach (var step in Edges(c, cur.Seg))
                {
                    double cost = cur.Cost + Math.Abs(cur.EntryX - step.TakeoffX) / speed + StepCost(c, cur.Seg, step);
                    Node n;
                    if (nodes.TryGetValue(step.To, out n))
                    {
                        if (n.Settled || n.Cost <= cost) continue;
                    }
                    else { n = new Node { Seg = step.To }; nodes[step.To] = n; }
                    n.Cost = cost; n.EntryX = step.LandX; n.Via = step; n.Prev = cur;
                }
            }
            return nodes;
        }

        public static List<NavStep> Plan(Creature c, Seg start, double x, Seg goal)
        {
            var nodes = Search(c, start, x, goal);
            Node n;
            if (!nodes.TryGetValue(goal, out n) || !n.Settled) return null;
            var steps = new List<NavStep>();
            while (n.Prev != null) { steps.Add(n.Via); n = n.Prev; }
            steps.Reverse();
            return steps;
        }

        /// <summary>Every surface the creature could get to right now, with a rough cost in seconds.</summary>
        public static Dictionary<Seg, Node> Reachable(Creature c)
        {
            Seg start = StartSeg(c);
            if (start == null) return new Dictionary<Seg, Node>();
            var all = Search(c, start, c.X, null);
            return all.Where(kv => kv.Value.Settled && kv.Key.Kind != SegKind.Ceiling).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        static double StepCost(Creature c, Seg from, NavStep s)
        {
            double bold = c.Brain.Traits.Bold;
            switch (s.Kind)
            {
                case "walk": return 0.2;
                case "drop": return 0.8 + Math.Max(0, s.To.Y - from.Y) / (1500 * c.S) + (1 - bold);
                case "jump": return (1.2 + Math.Abs(s.LandX - s.TakeoffX) / (400 * c.S) + Math.Max(0, from.Y - s.To.Y) / (300 * c.S)) * (1.4 - bold * 0.6);
                case "climb": return Math.Abs(from.Y - s.To.Y) / c.ClimbSpeed + 3;
                case "ceildrop": return 1.5 + (1 - bold) * 2;
            }
            return 5;
        }

        public static IEnumerable<NavStep> Edges(Creature c, Seg a)
        {
            World w = c.World;
            double m = c.BodyW * 0.4;

            if (a.Kind == SegKind.Ceiling)
            {
                // drop onto anything that's the first thing below some stretch of this ceiling
                foreach (var b in w.Segs)
                {
                    if (b.Mon != a.Mon) continue;
                    var pieces = new List<double[]> { new[] { Math.Max(b.X1 + m, a.X1 + c.BodyW * 0.6), Math.Min(b.X2 - m, a.X2 - c.BodyW * 0.6) } };
                    foreach (var d in w.Segs)
                        if (d != b && d.Y < b.Y) pieces = Subtract(pieces, d.X1 - c.BodyW * 0.3, d.X2 + c.BodyW * 0.3);
                    double[] best = null;
                    foreach (var p in pieces) if (p[1] - p[0] >= 2 && (best == null || p[1] - p[0] > best[1] - best[0])) best = p;
                    if (best == null) continue;
                    double x = (best[0] + best[1]) / 2;
                    yield return new NavStep { Kind = "ceildrop", To = b, TakeoffX = x, LandX = x };
                }
                yield break;
            }

            var mon = w.MonByIndex(a.Mon);

            foreach (var b in w.Segs)
            {
                if (b == a) continue;
                double up = a.Y - b.Y;

                // walk straight across onto a neighbour at about the same height
                if (Math.Abs(up) <= c.StepMax)
                {
                    if (Math.Abs(b.X1 - a.X2) <= 4) { yield return new NavStep { Kind = "walk", To = b, TakeoffX = a.X2 - 1, LandX = b.X1 + m }; continue; }
                    if (Math.Abs(a.X1 - b.X2) <= 4) { yield return new NavStep { Kind = "walk", To = b, TakeoffX = a.X1 + 1, LandX = b.X2 - m }; continue; }
                }

                if (up > c.JumpUpMax) continue;
                double o1 = Math.Max(a.X1, b.X1) + m, o2 = Math.Min(a.X2, b.X2) - m;
                if (o2 >= o1)
                {
                    // straight up through a plank above (they're one-way, like in platformers)
                    if (up > c.StepMax)
                    {
                        double x = (o1 + o2) / 2;
                        yield return new NavStep { Kind = "jump", To = b, TakeoffX = x, LandX = x };
                    }
                    continue;
                }
                double takeoff, land;
                if (b.X1 >= a.X2 - m) { takeoff = a.X2 - m; land = Math.Min(b.X1 + m * 1.5, b.Mid); }
                else if (b.X2 <= a.X1 + m) { takeoff = a.X1 + m; land = Math.Max(b.X2 - m * 1.5, b.Mid); }
                else continue;
                double reach = up >= 0 ? c.JumpXMax : c.JumpXMax * 1.4;
                if (Math.Abs(land - takeoff) > reach) continue;
                yield return new NavStep { Kind = "jump", To = b, TakeoffX = takeoff, LandX = land };
            }

            // step off either end
            for (int side = -1; side <= 1; side += 2)
            {
                if (a.Kind == SegKind.Floor && mon != null && (side < 0 ? mon.WallLeft : mon.WallRight)) continue;
                double ex = side > 0 ? a.X2 + c.BodyW * 0.35 : a.X1 - c.BodyW * 0.35;
                Seg l = w.LandingBelow(ex, a.Y + 1, a);
                if (l == null || Math.Abs(l.Y - a.Y) <= c.StepMax) continue;
                yield return new NavStep { Kind = "drop", To = l, TakeoffX = side > 0 ? a.X2 - 1 : a.X1 + 1, LandX = ex };
            }

            // climb a wall of the world up to the ceiling
            if (a.Kind == SegKind.Floor && mon != null)
            {
                Seg ceil = w.Ceiling(a.Mon);
                double touch = c.BodyW * 0.42;
                if (ceil != null && mon.WallLeft)
                    yield return new NavStep { Kind = "climb", To = ceil, TakeoffX = a.X1 + touch + (c.EdgeKnown(mon, -1) ? 8 * c.S : 0), LandX = ceil.X1 + c.BodyW * 0.6 };
                if (ceil != null && mon.WallRight)
                    yield return new NavStep { Kind = "climb", To = ceil, TakeoffX = a.X2 - touch - (c.EdgeKnown(mon, 1) ? 8 * c.S : 0), LandX = ceil.X2 - c.BodyW * 0.6 };
            }
        }

        static List<double[]> Subtract(List<double[]> pieces, double a, double b)
        {
            var result = new List<double[]>();
            foreach (var p in pieces)
            {
                if (b <= p[0] || a >= p[1]) { result.Add(p); continue; }
                if (a > p[0]) result.Add(new[] { p[0], a });
                if (b < p[1]) result.Add(new[] { b, p[1] });
            }
            return result;
        }
    }
}
