using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>Notices when the UI thread stops responding (the creature freezes) and writes where it was stuck
    /// to .state\debug.log, so a freeze can be tracked down instead of guessed at.</summary>
    public static class Watchdog
    {
        static Thread ui;
        static Control owner;
        static volatile int lastBeat;
        static bool reported;

        public static void Start(Control control)
        {
            ui = Thread.CurrentThread;
            owner = control;
            lastBeat = Environment.TickCount;
            new Thread(Loop) { IsBackground = true, Name = "bestie watchdog" }.Start();
        }

        static void Loop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    if (owner.IsDisposed) return;
                    if (owner.IsHandleCreated) owner.BeginInvoke(new Action(() => lastBeat = Environment.TickCount));
                }
                catch { return; }

                int stalled = Environment.TickCount - lastBeat;
                if (stalled > 4000 && !reported)
                {
                    reported = true;
                    string where = "(couldn't read the stack)";
#pragma warning disable 618   // Suspend/Resume are deprecated, but they're the only way to peek at another thread's stack here
                    try
                    {
                        ui.Suspend();
                        try { where = new StackTrace(ui, false).ToString(); }
                        finally { ui.Resume(); }
                    }
                    catch (Exception ex) { where = ex.Message; }
#pragma warning restore 618
                    DebugLog.Write("FREEZE: the ui thread has been stuck for " + stalled + " ms, at:\r\n" + where);
                }
                else if (stalled < 1500 && reported)
                {
                    reported = false;
                    DebugLog.Write("freeze over.");
                }
            }
        }
    }
}
