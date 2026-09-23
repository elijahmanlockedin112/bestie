using System;
using System.Collections.Generic;
using System.Linq;

namespace Bestie
{
    public static class Clock
    {
        public static double T;                 // seconds since start (real or simulated)
        public static bool Simulated;
        static readonly DateTime start = DateTime.Now;
        public static DateTime Wall { get { return Simulated ? start.AddSeconds(T) : DateTime.Now; } }
        public static double Unix { get { return (Wall.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalSeconds; } }
    }

    /// <summary>What an activity coroutine is waiting for.</summary>
    public class Wait
    {
        readonly double until;
        readonly Func<bool> cond;
        public Wait(double seconds, Func<bool> condition) { until = Clock.T + seconds; cond = condition; }
        public bool Done() { return Clock.T >= until || (cond != null && cond()); }
    }

    public enum Mode { Ground, Air, Wall, Ceiling, Held, Hidden, Egg }
    public enum Face { Normal, Happy, Sleep, Dizzy, Surprised, Squint, Content, Think }
    public enum Pose { Stand, Walk, Sit, Lie, Dance, Sing, Sniff, Peek, Climb, Held, Crouch, Egg, Wave, Exercise, Sunbathe, Spin }

    public class Particle
    {
        public string Kind;         // z, heart, star, note, dust, puff, bang, what, sweat, sparkle
        public double X, Y, VX, VY, Life, MaxLife, Size, Rot;
    }

    /// <summary>The little weirdo: body physics, senses and the bits of mind that react to things.
    /// Activities and choosing what to do live in Activities.cs, navigation in Planner.cs.</summary>
    public partial class Creature
    {
        public Brain Brain;
        public World World;
        readonly uint ownPid;

        // sizes and speeds, in physical pixels (scaled by DPI)
        public double S, BodyW, BodyH, Gravity, WalkSpeed, RunSpeed, ClimbSpeed, JumpUpMax, JumpXMax, StepMax;

        // body
        public Mode Mode = Mode.Ground;
        public double X, Y, VX, VY;
        public int Facing = 1;
        public Seg Support;
        RECT supportRect;
        public int WallSide;       // -1: on the left wall, +1: on the right wall
        public int HangMon;        // monitor index while on a wall/ceiling

        // motor
        double walkTarget = double.NaN, walkSpeed;
        bool allowWalkOff, walkOffOnPurpose;
        public bool MotorBusy { get { return !double.IsNaN(walkTarget); } }
        public bool MotorBlocked;
        double climbTargetY = double.NaN;
        bool expectingAir, fromHand, thrown;
        string fallReason, fallApp;
        double airStartY, airTime, fallPeak;

        // held by the cursor
        double grabDX, grabDY, heldVX, heldVY, lastCursorX, lastCursorY, heldSince;

        // looks
        public Pose Pose = Pose.Stand;
        public Face Face = Face.Normal;
        public double Rot, RotTarget, Tilt, WalkPhase, Blink, Blush, ArmWave, Talk;
        public double Squash, SquashV, Antenna, AntennaV, LookX, LookY, SpinPhase;
        public bool Sunglasses;
        public double Alpha = 1;
        double blinkTimer = 3, lookTX, lookTY, lastVX, lastAnimX, lastAnimY;
        public bool Moving;
        public readonly List<Particle> Particles = new List<Particle>();

        // speech
        public string BubbleText;
        public bool BubbleThought;
        public double BubbleUntil, BubbleStart, TalkUntil;
        double lastMusing;

        // mood set by the assistant: idle, thinking, listening, talking
        public string Mood = "idle";

        // senses
        bool userAway; double awaySince, idleCheck, fgCheck, lastStartle;
        string fgKey; double fgSince; string fgApp, fgTitle;
        double scanTimer, saveTimer, aliveTimer;
        double cursorX, cursorY, cursorSpeed;
        double musicCheck, lastSongTalk = -999;
        string songKey;
        public Song NowPlaying;
        public bool Vibing { get { return NowPlaying != null && Mode == Mode.Ground && Pose == Pose.Stand && !Moving && Face != Face.Sleep; } }

        public event Action<string> Spoke;   // for the diary / logs

        public Creature(Brain brain, uint ownPid)
        {
            Brain = brain;
            this.ownPid = ownPid;
            World = World.Scan(ownPid, 40);
            SetScale(World.Scale);
        }

        void SetScale(double s)
        {
            S = Math.Max(1, s);
            BodyW = 46 * S; BodyH = 40 * S;
            Gravity = 1900 * S; WalkSpeed = 52 * S; RunSpeed = 135 * S; ClimbSpeed = 48 * S;
            JumpUpMax = 300 * S; JumpXMax = 330 * S; StepMax = 36 * S;
        }

        public Mon CurrentMon { get { return World.MonAt(X, Mode == Mode.Ceiling ? Y + 5 : Y - 5); } }

        // =====================================================================
        // main tick
        // =====================================================================

        public void Update(double dt)
        {
            dt = Math.Min(dt, 0.1);
            aliveTimer += dt;
            if (aliveTimer >= 1) { Brain.AliveSeconds += aliveTimer; aliveTimer = 0; }

            scanTimer -= dt;
            if (scanTimer <= 0)
            {
                Rescan();
                scanTimer = Mode == Mode.Air ? 0.12 : 0.3;
            }

            saveTimer += dt;
            if (saveTimer > 60 && !Clock.Simulated) { saveTimer = 0; SaveSpot(); Brain.DailyDecay(); Brain.Save(); }

            if (!Clock.Simulated) Senses(dt);

            switch (Mode)
            {
                case Mode.Ground: UpdateGround(dt); break;
                case Mode.Air: UpdateAir(dt); break;
                case Mode.Wall: UpdateWall(dt); break;
                case Mode.Ceiling: UpdateCeiling(dt); break;
                case Mode.Held: UpdateHeld(dt); break;
                case Mode.Hidden: UpdateHidden(dt); break;
            }

            StepMind(dt);
            Animate(dt);
        }

        void Rescan()
        {
            World = World.Scan(ownPid, BodyH);
            if (Math.Abs(World.Scale - S) > 0.01) SetScale(World.Scale);
            if (World.Mons.Count == 0) return;

            if (Mode == Mode.Ground && Support != null)
            {
                Seg again = World.Refind(Support, X);
                if (again == null || !again.Contains(X, BodyW * 0.35))
                {
                    if (Support.Kind == SegKind.Plank) LoseSupport(again == null ? "gone" : "covered");
                    else PutOnFloor(World.MonAt(X, Y));
                }
                else
                {
                    Support = again;
                    if (Support.Kind == SegKind.Plank) Native.FrameBounds(Support.Hwnd, out supportRect);
                    Y = Support.Y;
                }
            }

            // wandered somewhere impossible (monitor unplugged, etc.)
            var mon = World.MonAt(X, Y);
            if (mon != null && (X < World.MinX - 200 || X > World.MaxX + 200 || Y > mon.Bounds.Bottom + 400 || Y < mon.Bounds.Top - 400))
            {
                PutOnFloor(World.Mons.FirstOrDefault(m => m.Primary) ?? World.Mons[0]);
                Say("where am i.", false, 0);
            }

            if (Mode == Mode.Ground) NoticeScreen();
            if (!Clock.Simulated && World.FullscreenMon >= 0 && mon != null && World.FullscreenMon == mon.Index && Mode != Mode.Held && Mode != Mode.Egg)
                HideForFullscreen();
        }

        public void PutOnFloor(Mon m)
        {
            if (m == null) return;
            Mode = Mode.Ground;
            Support = World.Floor(m.Index);
            X = Math.Max(m.Work.Left + BodyW, Math.Min(m.Work.Right - BodyW, X));
            Y = Support.Y;
            VX = VY = 0;
            RotTarget = 0;
        }

        // =====================================================================
        // motor
        // =====================================================================

        /// <summary>Where on its current surface the creature may stand, given what it knows about edges.</summary>
        public double ClampOnSupport(double x)
        {
            if (Support == null) return x;
            double lo = Support.X1, hi = Support.X2;
            if (Support.Kind == SegKind.Floor)
            {
                var m = World.MonByIndex(Support.Mon);
                double touch = BodyW * 0.42;
                if (m != null && m.WallLeft) lo += EdgeKnown(m, -1) ? touch + 8 * S : touch;
                if (m != null && m.WallRight) hi -= EdgeKnown(m, 1) ? touch + 8 * S : touch;
            }
            else
            {
                lo += BodyW * 0.2; hi -= BodyW * 0.2;
            }
            if (lo > hi) return (Support.X1 + Support.X2) / 2;
            return Math.Max(lo, Math.Min(hi, x));
        }

        public bool EdgeKnown(Mon m, int side)
        {
            return Brain.Edges.ContainsKey(m.Key + (side < 0 ? ":L" : ":R"));
        }

        public void WalkTo(double x, double speed, bool mayWalkOff, bool onPurpose)
        {
            allowWalkOff = mayWalkOff;
            walkOffOnPurpose = onPurpose;
            walkTarget = mayWalkOff ? x : ClampOnSupport(x);
            walkSpeed = speed;
            MotorBlocked = false;
        }

        public void StopWalking() { walkTarget = double.NaN; }

        void UpdateGround(double dt)
        {
            if (Support == null) { PutOnFloor(World.MonAt(X, Y)); return; }

            // ride along with the window we're standing on
            if (Support.Kind == SegKind.Plank)
            {
                RECT r;
                IntPtr h = Support.Hwnd;
                if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || !Native.FrameBounds(h, out r))
                {
                    LoseSupport("gone");
                    return;
                }
                if (r.Left != supportRect.Left || r.Top != supportRect.Top || r.Right != supportRect.Right)
                {
                    double dx = r.Left - supportRect.Left;
                    if (supportRect.Width > 0 && Math.Abs(r.Width - supportRect.Width) < 2)
                    {
                        X += dx; Support.X1 += dx; Support.X2 += dx;
                        if (!double.IsNaN(walkTarget)) walkTarget += dx;
                    }
                    Support.Y = r.Top;
                    Y = r.Top;
                    supportRect = r;
                    var m = World.MonAt(X, Y);
                    if (m == null || r.Top < m.Work.Top + BodyH * 1.1 || Native.IsZoomed(h) || X < r.Left - 4 || X > r.Right + 4)
                    {
                        LoseSupport("moved");
                        return;
                    }
                    if (Math.Abs(dx) > 30 * S && Rng.Chance(0.02)) Say(Rng.Pick("wheee", "where are we going", "i'm riding it"), false, 0);
                }
            }

            if (double.IsNaN(walkTarget)) { VX = 0; return; }

            double dir = Math.Sign(walkTarget - X);
            double step = walkSpeed * dt;
            if (Math.Abs(walkTarget - X) <= step)
            {
                X = walkTarget;
                walkTarget = double.NaN;
                VX = 0;
            }
            else
            {
                X += dir * step;
                VX = dir * walkSpeed;
                Facing = dir > 0 ? 1 : -1;
                WalkPhase += dt * walkSpeed / (BodyW * 0.32);
            }

            // ran out of surface?
            if (X < Support.X1 || X > Support.X2)
            {
                int side = X > Support.X2 ? 1 : -1;
                Seg next = World.Segs.FirstOrDefault(s => s != Support && !s.SameAs(Support) && s.Contains(X, 2) && Math.Abs(s.Y - Y) <= StepMax);
                if (next != null)
                {
                    Support = next; Y = next.Y;
                    if (next.Kind == SegKind.Plank) Native.FrameBounds(next.Hwnd, out supportRect);
                }
                else if (allowWalkOff)
                {
                    string app = Support.App;
                    StartAir(side * walkSpeed * 1.1, -60 * S, walkOffOnPurpose);
                    if (!walkOffOnPurpose) { fallReason = "walkoff"; fallApp = app; AbortActivity(); Say(Rng.Pick("oh no the edge", "whoa—", "uh oh"), false, 0); }
                    walkTarget = double.NaN;
                    return;
                }
                else
                {
                    X = side > 0 ? Support.X2 : Support.X1;
                    walkTarget = double.NaN;
                    MotorBlocked = true;
                }
            }

            // bumped into the edge of the world
            if (Support.Kind == SegKind.Floor && dir != 0)
            {
                var m = World.MonByIndex(Support.Mon);
                double touch = BodyW * 0.42;
                if (m != null)
                {
                    if (dir < 0 && m.WallLeft && X <= Support.X1 + touch + 0.5) { X = Support.X1 + touch; walkTarget = double.NaN; Bumped(m, -1); }
                    else if (dir > 0 && m.WallRight && X >= Support.X2 - touch - 0.5) { X = Support.X2 - touch; walkTarget = double.NaN; Bumped(m, 1); }
                }
            }
        }

        void Bumped(Mon m, int side)
        {
            string key = m.Key + (side < 0 ? ":L" : ":R");
            EdgeMemory e = Brain.Edge(key);
            if (e == null)
            {
                Brain.Edges[key] = new EdgeMemory { Bonks = 1, Found = Clock.Wall.ToString("yyyy-MM-dd") };
                Brain.Bonks++;
                Interrupt(BonkReaction(m, side, true), "bonk");
            }
            else if (Pose != Pose.Wave)
            {
                e.Boops++;
            }
        }

        public void Jump(double lx, double ly)
        {
            double dx = lx - X, up = Y - ly;
            double apex = Math.Max(up, 0) + 45 * S + Math.Abs(dx) * 0.12;
            double vy0 = -Math.Sqrt(2 * Gravity * apex);
            double tUp = -vy0 / Gravity;
            double tDown = Math.Sqrt(2 * Math.Max(4 * S, apex - up) / Gravity);
            Facing = dx >= 0 ? 1 : -1;
            StartAir(dx / (tUp + tDown), vy0, true);
            Squash = -0.25;
        }

        void StartAir(double vx, double vy, bool onPurpose)
        {
            Mode = Mode.Air;
            VX = vx; VY = vy;
            expectingAir = onPurpose;
            airStartY = Y; fallPeak = Y; airTime = 0;
            Support = null;
            walkTarget = double.NaN;
        }

        void LoseSupport(string reason)
        {
            if (Mode != Mode.Ground || Support == null) return;
            fallApp = Support.App;
            fallReason = reason;
            if (fallApp != null) Brain.App(fallApp).Dropped++;
            StartAir(0, 0, false);
            AbortActivity();
            Face = Face.Surprised;
            Particles.Add(new Particle { Kind = "bang", X = X, Y = Y - BodyH * 1.3, VY = -30 * S, Life = 0.9, MaxLife = 0.9, Size = 1 });
        }

        void UpdateAir(double dt)
        {
            airTime += dt;
            double prevY = Y;
            VY += Gravity * dt;
            if (fromHand) { VX *= Math.Pow(0.6, dt); SpinPhase += dt * VX / (BodyW * 0.6); }
            X += VX * dt;
            double ny = Y + VY * dt;
            fallPeak = Math.Min(fallPeak, ny);

            var m = World.MonAt(X, ny);
            if (m != null)
            {
                // walls of the world are solid
                if (X - BodyW * 0.45 < m.Bounds.Left && m.WallLeft) { X = m.Bounds.Left + BodyW * 0.45; VX = Math.Abs(VX) * 0.45; }
                if (X + BodyW * 0.45 > m.Bounds.Right && m.WallRight) { X = m.Bounds.Right - BodyW * 0.45; VX = -Math.Abs(VX) * 0.45; }
                if (ny - BodyH < m.Work.Top && VY < 0) { ny = m.Work.Top + BodyH; VY = 0; }
            }

            if (VY > 0)
            {
                Seg land = null;
                foreach (var s in World.Segs)
                {
                    if (!s.Contains(X, 2)) continue;
                    if (s.Y >= prevY - 0.5 && s.Y <= ny && (land == null || s.Y < land.Y)) land = s;
                }
                if (land != null) { Land(land); return; }
            }
            Y = ny;

            if (airTime > 8 || (m != null && Y > m.Bounds.Bottom + 300))
            {
                PutOnFloor(m ?? World.Mons[0]);
                expectingAir = true;
            }
        }

        void Land(Seg s)
        {
            double speed = VY;
            double height = s.Y - fallPeak;
            Mode = Mode.Ground;
            Support = s;
            Y = s.Y;
            VX = VY = 0;
            RotTarget = 0;
            SpinPhase = 0;
            if (s.Kind == SegKind.Plank) Native.FrameBounds(s.Hwnd, out supportRect);
            Squash = Math.Min(0.5, speed / (2600 * S));
            SquashV = 0;
            for (int i = 0; i < 5; i++)
                Particles.Add(new Particle { Kind = "dust", X = X + Rng.Range(-BodyW * 0.5, BodyW * 0.5), Y = Y - 2, VX = Rng.Range(-60, 60) * S, VY = Rng.Range(-40, -10) * S, Life = 0.5, MaxLife = 0.5, Size = Rng.Range(3, 6) * S });

            if (s.Kind == SegKind.Plank)
            {
                bool first = !Brain.Apps.ContainsKey(s.App);
                var mem = Brain.App(s.App);
                mem.StoodOn++;
                if (first) Discovered(s.App, mem);
            }
            NoticeScreen();

            bool wasHand = fromHand, wasThrown = thrown;
            fromHand = thrown = false;
            if (wasHand) { Brain.Falls++; Interrupt(ThrowLanding(wasThrown, height), wasThrown ? "thrown" : "carried"); }
            else if (!expectingAir) { Brain.Falls++; Interrupt(FallLanding(fallReason, fallApp, height), "fell"); }
            expectingAir = false;
        }

        // ---- walls & ceilings ----

        public bool AtWall(out int side)
        {
            side = 0;
            if (Mode != Mode.Ground || Support == null || Support.Kind != SegKind.Floor) return false;
            var m = World.MonByIndex(Support.Mon);
            if (m == null) return false;
            double reach = BodyW * 0.42 + 12 * S;
            if (m.WallLeft && X <= Support.X1 + reach) { side = -1; return true; }
            if (m.WallRight && X >= Support.X2 - reach) { side = 1; return true; }
            return false;
        }

        public void StartClimb(int side)
        {
            var m = World.MonByIndex(Support.Mon);
            Mode = Mode.Wall;
            WallSide = side;
            HangMon = m.Index;
            X = side < 0 ? m.Work.Left : m.Work.Right;
            Y = Y - BodyW * 0.5;
            RotTarget = side < 0 ? 90 : -90;
            climbTargetY = m.Work.Top;
            Support = null;
            walkTarget = double.NaN;
        }

        public void ClimbTo(double y) { climbTargetY = y; }
        public bool Climbing { get { return Mode == Mode.Wall && !double.IsNaN(climbTargetY); } }

        void UpdateWall(double dt)
        {
            var m = World.MonByIndex(HangMon);
            if (m == null) { PutOnFloor(World.MonAt(X, Y)); return; }
            X = WallSide < 0 ? m.Work.Left : m.Work.Right;
            if (double.IsNaN(climbTargetY)) return;
            double top = m.Work.Top + BodyW * 0.5, bottom = m.Work.Bottom - BodyW * 0.5;
            double target = Math.Max(top, Math.Min(bottom, climbTargetY));
            bool overTheTop = climbTargetY <= top + 0.5, downToFloor = climbTargetY >= bottom - 0.5;
            double dir = Math.Sign(target - Y);
            double step = ClimbSpeed * dt;
            WalkPhase += dt * ClimbSpeed / (BodyW * 0.3);
            bool arrived = Math.Abs(target - Y) <= step;
            if (arrived) { Y = target; climbTargetY = double.NaN; }
            else Y += dir * step;
            if (!arrived) return;

            if (overTheTop)
            {
                // over the top and onto the ceiling
                Mode = Mode.Ceiling;
                Y = m.Work.Top;
                X = WallSide < 0 ? m.Work.Left + BodyW * 0.6 : m.Work.Right - BodyW * 0.6;
                RotTarget = WallSide < 0 ? 180 : -180;
                climbTargetY = double.NaN;
                Facing = -WallSide;
            }
            else if (downToFloor)
            {
                PutOnFloor(m);
                X = WallSide < 0 ? m.Work.Left + BodyW * 0.6 : m.Work.Right - BodyW * 0.6;
            }
        }

        public void CeilingWalkTo(double x) { walkTarget = x; walkSpeed = WalkSpeed * 0.8; }

        void UpdateCeiling(double dt)
        {
            var m = World.MonByIndex(HangMon);
            if (m == null) { PutOnFloor(World.MonAt(X, Y)); return; }
            Y = m.Work.Top;
            if (double.IsNaN(walkTarget)) return;
            double t = Math.Max(m.Work.Left + BodyW * 0.6, Math.Min(m.Work.Right - BodyW * 0.6, walkTarget));
            double dir = Math.Sign(t - X), step = walkSpeed * dt;
            if (Math.Abs(t - X) <= step) { X = t; walkTarget = double.NaN; }
            else { X += dir * step; Facing = dir > 0 ? 1 : -1; WalkPhase += dt * walkSpeed / (BodyW * 0.32); }
        }

        /// <summary>Let go of a wall or ceiling.</summary>
        public void LetGo()
        {
            if (Mode == Mode.Wall) { StartAir(-WallSide * 90 * S, -80 * S, true); X += -WallSide * BodyH * 0.5; Y += BodyW * 0.5; }
            else if (Mode == Mode.Ceiling) { Y += BodyH; StartAir(0, 0, true); }
            RotTarget = 0;
        }

        // ---- held by the user ----

        public void Grab(double cx, double cy)
        {
            if (Mode == Mode.Egg || Mode == Mode.Hidden) return;
            AbortActivity();
            Mode = Mode.Held;
            Support = null;
            walkTarget = double.NaN;
            grabDX = X - cx; grabDY = Y - cy;
            // hold it by the scruff, whatever orientation it was in
            grabDX = 0; grabDY = BodyH * 0.85;
            lastCursorX = cx; lastCursorY = cy; heldVX = heldVY = 0;
            heldSince = Clock.T;
            RotTarget = 0;
            Brain.Pickups++;
            Pose = Pose.Held;
            Face = Brain.Trust > 0.2 ? Face.Happy : Face.Surprised;
            if (Rng.Chance(0.7)) Say(Rng.Pick(Lines.PickedUp), false, 0);
        }

        void UpdateHeld(double dt)
        {
            var p = Native.Cursor();
            double vx = (p.X - lastCursorX) / Math.Max(dt, 0.001), vy = (p.Y - lastCursorY) / Math.Max(dt, 0.001);
            heldVX = heldVX * 0.6 + vx * 0.4; heldVY = heldVY * 0.6 + vy * 0.4;
            lastCursorX = p.X; lastCursorY = p.Y;
            X = p.X + grabDX; Y = p.Y + grabDY;
            RotTarget = Math.Max(-35, Math.Min(35, -heldVX / (40 * S)));
            WalkPhase += dt * 9;   // legs kick
            if (Clock.T - heldSince > 6 && Rng.Chance(dt * 0.08)) Say(Rng.Pick(Lines.HeldLong), false, 0);
        }

        public void Release()
        {
            if (Mode != Mode.Held) return;
            double vx = heldVX, vy = heldVY, max = 3200 * S, mag = Math.Sqrt(vx * vx + vy * vy);
            if (mag > max) { vx *= max / mag; vy *= max / mag; mag = max; }
            thrown = mag > 1300 * S;
            fromHand = true;
            StartAir(vx, vy, false);
            fromHand = true;
            if (thrown) { Brain.Throws++; Say(Rng.Pick("WHEEE", "aaaaa", "i'm flyinggg"), false, 0); Face = Face.Happy; }
            Pose = Pose.Stand;
        }

        public bool IsHeld { get { return Mode == Mode.Held; } }

        // ---- hiding while something is fullscreen ----

        void HideForFullscreen()
        {
            if (Mode == Mode.Hidden) return;
            AbortActivity();
            Mode = Mode.Hidden;
            BubbleText = null;
        }

        void UpdateHidden(double dt)
        {
            Alpha = Math.Max(0, Alpha - dt * 3);
            var m = World.MonAt(X, Y);
            if (World.FullscreenMon < 0 || m == null || World.FullscreenMon != m.Index)
            {
                PutOnFloor(m);
                Alpha = 1;
                Interrupt(Sequence(() => Say("was i gone? no.", false, 0)), "return");
            }
        }

        // =====================================================================
        // senses
        // =====================================================================

        void Senses(double dt)
        {
            var p = Native.Cursor();
            double dx = p.X - cursorX, dy = p.Y - cursorY;
            cursorSpeed = cursorSpeed * 0.7 + Math.Sqrt(dx * dx + dy * dy) / Math.Max(dt, 0.001) * 0.3;
            cursorX = p.X; cursorY = p.Y;

            // startled by a cursor zooming past
            double hx = X, hy = Y - BodyH * 0.6;
            double dist = Math.Sqrt((cursorX - hx) * (cursorX - hx) + (cursorY - hy) * (cursorY - hy));
            if (Mode == Mode.Ground && Pose != Pose.Lie && cursorSpeed > 5000 * S && dist < 90 * S && Clock.T - lastStartle > 20)
            {
                lastStartle = Clock.T;
                Face = Face.Surprised;
                Squash = -0.3;
                Particles.Add(new Particle { Kind = "bang", X = X, Y = Y - BodyH * 1.3, VY = -30 * S, Life = 0.8, MaxLife = 0.8, Size = 1 });
                if (Rng.Chance(0.5)) Say(Rng.Pick("!!", "yikes", "careful with that arrow"), false, 1.5);
            }

            idleCheck -= dt;
            if (idleCheck <= 0)
            {
                idleCheck = 1;
                double idle = Native.IdleSeconds();
                if (!userAway && idle > 300)
                {
                    userAway = true; awaySince = Clock.T - idle;
                    Journal.Add("you went quiet. i'm in charge now.");
                }
                else if (userAway && idle < 3)
                {
                    userAway = false;
                    double mins = (Clock.T - awaySince) / 60;
                    Journal.Add("you came back after " + FormatMinutes(mins) + ".");
                    if (mins > 3 && Mode == Mode.Ground) { Brain.Welcomes++; Interrupt(WelcomeBack(mins), "return"); }
                }
            }

            musicCheck -= dt;
            if (musicCheck <= 0) { musicCheck = 3; CheckMusic(); }

            fgCheck -= dt;
            if (fgCheck <= 0)
            {
                fgCheck = 1;
                var fg = World.Foreground;
                string key = fg == null ? "" : fg.App + "|" + fg.Title;
                if (key != fgKey)
                {
                    double mins = (Clock.T - fgSince) / 60;
                    if (fgKey != null && fgApp != null && mins >= 3 && !userAway)
                        Journal.Add("you spent " + FormatMinutes(mins) + " in [" + fgApp + "] “" + Clip(fgTitle, 80) + "”.");
                    fgKey = key; fgSince = Clock.T;
                    fgApp = fg == null ? null : fg.App; fgTitle = fg == null ? null : fg.Title;
                }
            }
        }

        void CheckMusic()
        {
            Song s;
            try { s = Music.Detect(); } catch { s = null; }
            NowPlaying = s;
            if (s == null) { songKey = null; return; }
            if (s.Key == songKey) return;
            songKey = s.Key;
            string artist = s.Artist.ToLowerInvariant();
            double plays; bool heard = Brain.Artists.TryGetValue(artist, out plays);
            Brain.Artists[artist] = plays + 1;
            if (!heard) Journal.Add("you put on " + s.Artist + " (\u201c" + s.Title + "\u201d). new to me. " + Music.Rating(s) + " out of 7.");
            if (Mode == Mode.Ground && Mood == "idle" && Pose != Pose.Lie && Clock.T - lastSongTalk > 180 && Rng.Chance(0.45))
            {
                lastSongTalk = Clock.T;
                Say(Music.React(s, heard), false, 0);
                Emit("note", 2);
            }
        }

        public bool UserAway { get { return userAway; } }
        public double CursorX { get { return cursorX; } }
        public double CursorY { get { return cursorY; } }

        static string FormatMinutes(double mins)
        {
            if (mins < 1.5) return "a minute";
            if (mins < 90) return (int)Math.Round(mins) + " minutes";
            return (mins / 60).ToString("0.#") + " hours";
        }

        static string Clip(string s, int n) { return s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        void NoticeScreen()
        {
            var m = CurrentMon;
            if (m == null) return;
            ScreenMemory sm;
            if (Brain.Screens.TryGetValue(m.Key, out sm)) return;
            Mon home = null;
            foreach (var o in World.Mons) if (Brain.Screens.ContainsKey(o.Key)) { home = o; break; }
            sm = new ScreenMemory { Found = Clock.Wall.ToString("yyyy-MM-dd"), Nick = Names.Screen(m, home, Brain.Screens.Values.Select(v => v.Nick)) };
            Brain.Screens[m.Key] = sm;
            if (home != null)
            {
                Journal.Add("discovered a whole new screen. i named it " + sm.Nick + ".");
                Brain.Remember("discovered " + sm.Nick);
                Interrupt(Sequence(() => Say("THERE'S MORE???", false, 2.2), () => Pause(0.4), () => Say("i'll call it " + sm.Nick + ".", false, 0)), "discovery");
            }
        }

        void Discovered(string app, AppMemory mem)
        {
            Journal.Add("stood on a new window (" + app + "). i call it " + mem.Nick + ".");
            if (Rng.Chance(0.85)) Say(Lines.FillPick(Lines.NewPlank, this, app), false, 0);
        }

        public void SaveSpot()
        {
            var m = CurrentMon;
            if (m != null) Brain.LastSpot = m.Key + "|" + (int)(X - m.Work.Left);
        }

        // =====================================================================
        // speech & particles
        // =====================================================================

        public Wait Say(string text, bool thought, double seconds)
        {
            if (string.IsNullOrEmpty(text)) return null;
            double dur = seconds > 0 ? seconds : Math.Max(2.2, Math.Min(9, 1.6 + 0.055 * text.Length));
            BubbleText = text;
            BubbleThought = thought;
            BubbleStart = Clock.T;
            BubbleUntil = Clock.T + dur;
            if (!thought) TalkUntil = Clock.T + Math.Min(dur * 0.6, 0.35 + text.Length * 0.035);
            if (text.Length >= 10 && !thought || text.Length >= 14) Brain.Remember(text);
            if (Spoke != null) Spoke(text);
            if (Clock.Simulated) SimLog((thought ? "thinks: " : "says: ") + text);
            return new Wait(Math.Min(dur, 2.5), null);
        }

        public void Emit(string kind, int n)
        {
            double hx = X, hy = Y - BodyH;
            if (Mode == Mode.Ceiling) hy = Y + BodyH;
            for (int i = 0; i < n; i++)
            {
                var p = new Particle { Kind = kind, X = hx + Rng.Range(-8, 8) * S, Y = hy, Life = 1.6, MaxLife = 1.6, Size = 1, Rot = Rng.Range(-0.4, 0.4) };
                switch (kind)
                {
                    case "z": p.X = hx + Facing * 14 * S; p.VX = Rng.Range(10, 25) * S; p.VY = -26 * S; p.Life = p.MaxLife = 2.4; p.Size = Rng.Range(0.8, 1.2); break;
                    case "heart": p.VX = Rng.Range(-30, 30) * S; p.VY = Rng.Range(-70, -45) * S; p.Life = p.MaxLife = 1.3; p.Size = Rng.Range(0.8, 1.2); break;
                    case "note": p.X = hx + Rng.Range(-20, 20) * S; p.VX = Rng.Range(-20, 20) * S; p.VY = -40 * S; p.Life = p.MaxLife = 1.8; break;
                    case "star": double a = Rng.Range(0, Math.PI * 2); p.Y = hy + 6 * S; p.VX = Math.Cos(a) * 50 * S; p.VY = Math.Sin(a) * 30 * S - 25 * S; p.Life = p.MaxLife = 1.1; break;
                    case "puff": p.X = X + Facing * BodyW * 0.55; p.Y = Y - 6 * S; p.VX = Facing * 25 * S; p.VY = -12 * S; p.Life = p.MaxLife = 0.6; p.Size = Rng.Range(2, 4) * S; break;
                    case "sweat": p.X = hx - Facing * 16 * S; p.Y = hy + 8 * S; p.VX = -Facing * 20 * S; p.VY = -30 * S; p.Life = p.MaxLife = 0.8; break;
                    case "sparkle": p.X = hx + Rng.Range(-30, 30) * S; p.Y = hy + Rng.Range(-10, 30) * S; p.Life = p.MaxLife = 0.9; break;
                    case "what": case "bang": p.Y = hy - 14 * S; p.VY = -20 * S; p.Life = p.MaxLife = 1.2; break;
                }
                Particles.Add(p);
            }
        }

        // =====================================================================
        // animation state
        // =====================================================================

        void Animate(double dt)
        {
            // squash-and-stretch spring
            SquashV += (-140 * Squash - 9 * SquashV) * dt;
            Squash += SquashV * dt;

            // antenna / topper spring, pushed around by acceleration
            double accel = (VX - lastVX) / Math.Max(dt, 0.001);
            lastVX = VX;
            AntennaV += (-90 * Antenna - 4 * AntennaV - accel * 0.0022 / S) * dt;
            if (Mode == Mode.Held) AntennaV += -heldVX * 0.00004 / S;
            Antenna = Math.Max(-1, Math.Min(1, Antenna + AntennaV * dt));

            // rotation toward the surface we're on (or a spin when thrown)
            double target = RotTarget;
            if (Mode == Mode.Air && fromHand && thrown) target = SpinPhase * 57.3;
            double diff = target - Rot;
            if (Mode != Mode.Air || !thrown) { while (diff > 180) diff -= 360; while (diff < -180) diff += 360; }
            Rot += diff * Math.Min(1, dt * 10);

            // blinking
            blinkTimer -= dt;
            if (blinkTimer <= 0) { Blink = 1; blinkTimer = Rng.Range(2, 6); if (Rng.Chance(0.15)) blinkTimer = 0.25; }
            Blink = Math.Max(0, Blink - dt * 8);

            Blush = Math.Max(0, Blush - dt * 0.35);
            EggWobble = Math.Max(0, EggWobble - dt * 1.2);
            double moved = Math.Abs(X - lastAnimX) + Math.Abs(Y - lastAnimY);
            Moving = moved / Math.Max(dt, 0.001) > 8 * S && Mode != Mode.Held && Mode != Mode.Air;
            lastAnimX = X; lastAnimY = Y;
            Talk = Clock.T < TalkUntil || Mood == "talking" ? 0.5 + 0.5 * Math.Sin(Clock.T * 22) : 0;

            // eyes: cursor if close, otherwise where we're going
            double hx = X, hy = Y - BodyH * 0.6;
            double cdx = cursorX - hx, cdy = cursorY - hy, cd = Math.Sqrt(cdx * cdx + cdy * cdy);
            double ox, oy;
            if (Mood == "listening" || Mood == "thinking") { lookTX = Facing * 0.3; lookTY = -0.8; }
            else if (LookOverride(out ox, out oy)) { lookTX = ox; lookTY = oy; }
            else if (!Clock.Simulated && cd < 380 * S && cd > 1 && Mode != Mode.Held) { lookTX = cdx / cd; lookTY = cdy / cd; }
            else if (Math.Abs(VX) > 1) { lookTX = Math.Sign(VX); lookTY = 0.1; }
            else if (Rng.Chance(dt * 0.3)) { lookTX = Rng.Range(-1, 1); lookTY = Rng.Range(-0.6, 0.5); }
            LookX += (lookTX - LookX) * Math.Min(1, dt * 8);
            LookY += (lookTY - LookY) * Math.Min(1, dt * 8);

            // pose from body state when nothing more specific was asked for
            if (Mode == Mode.Air) { if (!fromHand || !thrown) Pose = Pose.Held; }
            else if (Mode == Mode.Wall || Mode == Mode.Ceiling) Pose = Pose.Climb;
            else if (Mode == Mode.Ground && Pose == Pose.Stand && MotorBusy) Pose = Pose.Walk;
            else if (Mode == Mode.Ground && Pose == Pose.Walk && !MotorBusy) Pose = Pose.Stand;

            for (int i = Particles.Count - 1; i >= 0; i--)
            {
                var p = Particles[i];
                p.Life -= dt;
                if (p.Life <= 0) { Particles.RemoveAt(i); continue; }
                p.X += p.VX * dt; p.Y += p.VY * dt;
                if (p.Kind == "star") p.VY += 60 * S * dt;
                if (p.Kind == "z") p.VX *= Math.Pow(0.5, dt);
                if (p.Kind == "sweat") p.VY += 200 * S * dt;
            }
            if (Particles.Count > 60) Particles.RemoveRange(0, Particles.Count - 60);

            if (BubbleText != null && Clock.T > BubbleUntil) BubbleText = null;
        }

        /// <summary>Screen-space box that contains the body (used for hit tests and window placement).</summary>
        public void HeadPoint(out double hx, out double hy)
        {
            double r = Rot * Math.PI / 180, up = BodyH * 1.05;
            hx = X + Math.Sin(r) * up;
            hy = Y - Math.Cos(r) * up;
        }
    }
}
