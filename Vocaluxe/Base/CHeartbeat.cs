#region license
// This file is part of Vocaluxe.
//
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using VocaluxeLib.Log;

namespace Vocaluxe.Base
{
    /// <summary>
    ///     Two things a log of events cannot show: what the program looked like shortly before it
    ///     vanished, and whether it was still alive at all.
    ///
    ///     <b>Heartbeat</b>: once a minute one line with memory, threads, open files, CPU, frame rate
    ///     and the game state. After a crash the last line is the last known state; a trend in
    ///     memory or open files shows a leak long before it kills the process.
    ///
    ///     <b>Stall watch</b>: the main loop calls <see cref="Tick()" /> every frame. The web server,
    ///     the screens, the rendering, everything runs on that loop, so when it stands still the game
    ///     is frozen for guests and singers alike - without any crash. A monitor thread notices and
    ///     logs it with the state of the main thread as the kernel sees it (blocked in the kernel?
    ///     waiting for a lock? spinning?), which tells a hung disk from a deadlock.
    ///
    ///     The game state comes from a snapshot the MAIN thread builds every few seconds, so the
    ///     monitor thread never touches game structures.
    /// </summary>
    static class CHeartbeat
    {
        internal static int IntervalSeconds = 60;
        internal static int StallSeconds = 5;
        internal static int SnapshotSeconds = 5;
        internal static int StallReminderSeconds = 30;

        private static readonly long _Frequency = Stopwatch.Frequency;

        private static long _LastTick; // 0 until the main loop runs: no stall can be reported before
        private static long _Frames;
        private static long _LastSnapshot;
        private static string _Snapshot = "no snapshot yet";
        private static long _SnapshotAt;

        // Only the monitor thread touches these.
        private static long _LastHeartbeat;
        private static long _FramesAtLastHeartbeat;
        private static TimeSpan _CpuAtLastHeartbeat;
        private static bool _Stalled;
        private static long _StallReportedAt;
        private static long _StallBegan;
        private static long _MainThreadTicksAtStall;

        private static Thread _Monitor;

        /// <summary>
        ///     Builds the game state for the log. Runs on the main thread, every <see cref="SnapshotSeconds" />.
        /// </summary>
        public static Func<string> DescribeState;

        /// <summary>Starts the monitor thread. Call once, shortly before the main loop.</summary>
        public static void Start()
        {
            if (_Monitor != null)
                return;
            _Monitor = new Thread(_Run) {IsBackground = true, Name = "Heartbeat"};
            _Monitor.Start();
        }

        /// <summary>The main loop calls this once per frame. Cheap.</summary>
        public static void Tick()
        {
            Tick(Stopwatch.GetTimestamp());
        }

        internal static void Tick(long now)
        {
            Volatile.Write(ref _LastTick, now);
            Interlocked.Increment(ref _Frames);

            if (now - Volatile.Read(ref _LastSnapshot) >= SnapshotSeconds * _Frequency)
            {
                Volatile.Write(ref _LastSnapshot, now);
                string snapshot;
                try
                {
                    snapshot = DescribeState != null ? DescribeState() : "n/a";
                }
                catch (Exception e)
                {
                    snapshot = "unavailable (" + e.GetType().Name + ")";
                }
                Volatile.Write(ref _Snapshot, snapshot);
                Volatile.Write(ref _SnapshotAt, now);
            }
        }

        private static void _Run()
        {
            while (!CExit.ShutdownStarted)
            {
                Thread.Sleep(1000);
                try
                {
                    Check(Stopwatch.GetTimestamp());
                }
                catch (Exception e)
                {
                    CLog.Warning("Heartbeat check failed", CLog.Params(new {Error = e.Message}));
                }
            }
        }

        /// <summary>One pass of the monitor. Separate from the thread so a test can drive the clock.</summary>
        internal static void Check(long now)
        {
            long lastTick = Volatile.Read(ref _LastTick);
            if (lastTick != 0)
                _CheckStall(now, lastTick);

            if (_LastHeartbeat == 0)
                _ResetHeartbeatBaseline(now);
            else if (now - _LastHeartbeat >= IntervalSeconds * _Frequency)
                _LogHeartbeat(now);
        }

        #region stall

        private static void _CheckStall(long now, long lastTick)
        {
            double silent = _Seconds(now - lastTick);

            if (silent >= StallSeconds)
            {
                if (!_Stalled)
                {
                    _Stalled = true;
                    _StallBegan = lastTick;
                    _StallReportedAt = now;
                    _MainThreadTicksAtStall = MainThreadCpuTicks();
                    CLog.Warning("Main loop stalled", CLog.Params(new {SilentSeconds = Math.Round(silent, 1)}, new {MainThread = DescribeMainThread()}, new {LastKnownState = _LastKnownState(now)}));
                }
                else if (now - _StallReportedAt >= StallReminderSeconds * _Frequency)
                {
                    _StallReportedAt = now;
                    long ticks = MainThreadCpuTicks();
                    CLog.Warning("Main loop still stalled", CLog.Params(new {SilentSeconds = Math.Round(silent, 1)}, new {MainThread = DescribeMainThread()},
                                                                        new {MainThreadCpuTicksSinceStall = ticks < 0 || _MainThreadTicksAtStall < 0 ? -1 : ticks - _MainThreadTicksAtStall}));
                }
            }
            else if (_Stalled)
            {
                _Stalled = false;
                CLog.Information("Main loop resumed", CLog.Params(new {StalledForSeconds = Math.Round(_Seconds(lastTick - _StallBegan), 1)}));
            }
        }

        // Linux: how the kernel sees the main thread (its tid is the pid). "D" with a wchan in a file
        // system or I/O function is a hung disk or network share, "S" in futex_* a lock someone never
        // releases, "R" a thread that is busy rather than blocked.
        internal static string DescribeMainThread()
        {
            try
            {
                string dir = "/proc/self/task/" + Environment.ProcessId;
                string stat = File.ReadAllText(dir + "/stat");
                string state = stat.Substring(stat.LastIndexOf(')') + 2, 1);
                string wchan = File.Exists(dir + "/wchan") ? File.ReadAllText(dir + "/wchan").Trim() : "n/a";
                return "state=" + state + " (" + _StateName(state) + "), wchan=" + (wchan.Length == 0 ? "-" : wchan);
            }
            catch (Exception)
            {
                return "unavailable";
            }
        }

        internal static long MainThreadCpuTicks()
        {
            try
            {
                string stat = File.ReadAllText("/proc/self/task/" + Environment.ProcessId + "/stat");
                string[] f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                return long.Parse(f[11]) + long.Parse(f[12]); // utime + stime (fields 14 and 15 of stat)
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string _StateName(string s)
        {
            switch (s)
            {
                case "R": return "running";
                case "S": return "sleeping";
                case "D": return "uninterruptible I/O wait";
                case "T": return "stopped";
                case "t": return "traced";
                case "Z": return "zombie";
                default: return "other";
            }
        }

        #endregion

        #region heartbeat

        private static void _ResetHeartbeatBaseline(long now)
        {
            _LastHeartbeat = now;
            _FramesAtLastHeartbeat = Interlocked.Read(ref _Frames);
            _CpuAtLastHeartbeat = _ProcessCpu();
        }

        private static TimeSpan _ProcessCpu()
        {
            using (Process p = Process.GetCurrentProcess())
                return p.TotalProcessorTime;
        }

        private static string _LastKnownState(long now)
        {
            return Volatile.Read(ref _Snapshot) + " [snapshot " + Math.Max(0, Math.Round(_Seconds(now - Volatile.Read(ref _SnapshotAt)), 0)) + " s old]";
        }

        private static void _LogHeartbeat(long now)
        {
            double wall = _Seconds(now - _LastHeartbeat);
            long frames = Interlocked.Read(ref _Frames);
            TimeSpan cpu = _ProcessCpu();

            double fps = Math.Round((frames - _FramesAtLastHeartbeat) / wall, 1);
            double cpuPercent = Math.Round((cpu - _CpuAtLastHeartbeat).TotalSeconds / wall * 100, 0);

            long workingSet, threads;
            TimeSpan uptime;
            using (Process p = Process.GetCurrentProcess())
            {
                workingSet = p.WorkingSet64 / (1024 * 1024);
                threads = p.Threads.Count;
                uptime = DateTime.Now - p.StartTime;
            }

            CLog.Information("Heartbeat", CLog.Params(
                new {Uptime = uptime.ToString(@"d\.hh\:mm\:ss")},
                new {Fps = fps},
                new {CpuPercentOfOneCore = cpuPercent},
                new {WorkingSetMB = workingSet},
                new {ManagedHeapMB = GC.GetTotalMemory(false) / (1024 * 1024)},
                new {Gc012 = GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2)},
                new {Threads = threads},
                new {OpenFiles = _OpenFiles()},
                new {MainLoopStalled = _Stalled},
                new {State = _LastKnownState(now)}));

            _LastHeartbeat = now;
            _FramesAtLastHeartbeat = frames;
            _CpuAtLastHeartbeat = cpu;
        }

        private static int _OpenFiles()
        {
            try
            {
                return Directory.GetFileSystemEntries("/proc/self/fd").Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        #endregion

        private static double _Seconds(long ticks)
        {
            return (double)ticks / _Frequency;
        }

        /// <summary>Back to a fresh start. For tests; the program never calls this.</summary>
        internal static void Reset()
        {
            _LastTick = 0;
            _Frames = 0;
            _LastSnapshot = 0;
            _Snapshot = "no snapshot yet";
            _SnapshotAt = 0;
            _LastHeartbeat = 0;
            _FramesAtLastHeartbeat = 0;
            _CpuAtLastHeartbeat = TimeSpan.Zero;
            _Stalled = false;
            _StallReportedAt = 0;
            _StallBegan = 0;
            _MainThreadTicksAtStall = 0;
            DescribeState = null;
            IntervalSeconds = 60;
            StallSeconds = 5;
            SnapshotSeconds = 5;
            StallReminderSeconds = 30;
        }
    }
}
