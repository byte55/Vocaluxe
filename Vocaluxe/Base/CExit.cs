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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Base
{
    enum EExitReason
    {
        /// <summary>The exit button of the main menu.</summary>
        MenuExit,
        /// <summary>The window system asked the window to close: Alt+F4, the window's close button, Quit in the dock, logout.</summary>
        WindowClose,
        /// <summary>SIGTERM, SIGINT, SIGHUP or SIGQUIT.</summary>
        Signal,
        /// <summary>Something failed before the main loop ran.</summary>
        StartupFailure,
        /// <summary>Another instance holds the single-instance mutex.</summary>
        SecondInstance,
        /// <summary>An exception nobody handled.</summary>
        FatalException,
        /// <summary>The main loop ended and nothing said why. Should not happen; if it does, a code path is missing here.</summary>
        MainLoopEnded
    }

    /// <summary>
    ///     Records WHY the program ends. Until this existed a vanished Vocaluxe left no trace of whether
    ///     somebody pressed Exit, the window system closed the window, a signal arrived or it died:
    ///     every one of them ended in "exit code 0, nothing in the log".
    ///
    ///     The first request wins; later ones are logged and ignored. A run that dies without ever
    ///     reaching this class is detected on the next start by the crash marker (see CLog.Init).
    ///
    ///     Exit codes: 0 menu/window close, 1 fatal exception, 2 startup failure, 3 second instance,
    ///     128+n after a handled signal (143 SIGTERM, 130 SIGINT, 129 SIGHUP, 131 SIGQUIT).
    /// </summary>
    static class CExit
    {
        /// <summary>How long a signal-requested shutdown may take before the process kills itself.</summary>
        internal static int SignalGraceSeconds = 15; // not const: the test shortens it

        private const int _RecentInputCount = 8;

        private static readonly object _Lock = new object();
        private static readonly Stopwatch _Uptime = Stopwatch.StartNew();
        private static readonly List<string> _RecentInput = new List<string>();
        private static readonly List<string> _RecentRaw = new List<string>();
        private static readonly List<PosixSignalRegistration> _SignalRegistrations = new List<PosixSignalRegistration>();

        private static EExitReason? _Reason;
        private static string _Detail;
        private static int _ExitCode;
        private static bool _ShutdownStarted;
        private static bool _ShutdownDone;
        private static long _LastKeyMs = -1;
        private static long _LastMouseMs = -1;
        private static long _LastRemoteKeyMs = -1;
        private static string _LastRemoteKey;
        private static volatile bool _StopRequested;

        /// <summary>
        ///     Describes the state of the game for the log (current screen, ...). Set by the program,
        ///     so this class does not depend on the graphics code.
        /// </summary>
        public static Func<string> DescribeState;

        /// <summary>True once something asked the program to end; the main loop watches this.</summary>
        public static bool StopRequested
        {
            get { return _StopRequested; }
        }

        /// <summary>True once the program started tearing everything down.</summary>
        public static bool ShutdownStarted
        {
            get { lock (_Lock) return _ShutdownStarted; }
        }

        public static EExitReason? Reason
        {
            get { lock (_Lock) return _Reason; }
        }

        public static string Detail
        {
            get { lock (_Lock) return _Detail; }
        }

        /// <summary>The exit code the process should end with. Applied by the program, not here.</summary>
        public static int ExitCode
        {
            get { lock (_Lock) return _ExitCode; }
        }

        /// <summary>Back to the state of a fresh start. For tests; the program never calls this.</summary>
        internal static void Reset()
        {
            lock (_Lock)
            {
                _Reason = null;
                _Detail = null;
                _ExitCode = 0;
                _ShutdownStarted = false;
                _ShutdownDone = false;
                _LastKeyMs = _LastMouseMs = _LastRemoteKeyMs = -1;
                _LastRemoteKey = null;
                _RecentInput.Clear();
                _RecentRaw.Clear();
            }
            _StopRequested = false;
            DescribeState = null;
            SignalGraceSeconds = 15;
        }

        #region input context

        public static void NoteKey(SKeyEvent key)
        {
            string text = (key.ModCtrl ? "Ctrl+" : "") + (key.ModAlt ? "Alt+" : "") + (key.ModShift ? "Shift+" : "") + key.Key;
            lock (_Lock)
            {
                _LastKeyMs = _Uptime.ElapsedMilliseconds;
                _Remember(text);
            }
        }

        public static void NoteMouse(SMouseEvent mouse)
        {
            lock (_Lock)
            {
                _LastMouseMs = _Uptime.ElapsedMilliseconds;
                if (mouse.LB || mouse.RB || mouse.MB)
                    _Remember("Mouse" + (mouse.LB ? " left" : "") + (mouse.RB ? " right" : "") + (mouse.MB ? " middle" : "") + " button");
            }
        }

        /// <summary>
        ///     What the window itself received, before the game maps it to its own keys: the raw key
        ///     name with press/release, and focus changes. Keys the game does not know (Alt, F4 under some
        ///     compositors) show up as "None" in the game's input list; this list says which they were.
        /// </summary>
        public static void NoteRawKey(string what)
        {
            lock (_Lock)
            {
                _RecentRaw.Add(what + " @" + _Uptime.Elapsed.TotalSeconds.ToString("0.0") + "s");
                if (_RecentRaw.Count > 12)
                    _RecentRaw.RemoveAt(0);
            }
        }

        /// <summary>A key sent through the web remote (/api/remote/key). Arrives in the game like a keyboard key.</summary>
        public static void NoteRemoteKey(string key)
        {
            lock (_Lock)
            {
                _LastRemoteKeyMs = _Uptime.ElapsedMilliseconds;
                _LastRemoteKey = key;
            }
        }

        // Only called with _Lock held.
        private static void _Remember(string text)
        {
            _RecentInput.Add(text + " @" + _Uptime.Elapsed.TotalSeconds.ToString("0.0") + "s");
            if (_RecentInput.Count > _RecentInputCount)
                _RecentInput.RemoveAt(0);
        }

        private static string _Ago(long ms)
        {
            return ms < 0 ? "never" : ((_Uptime.ElapsedMilliseconds - ms) / 1000.0).ToString("0.0") + " s ago";
        }

        /// <summary>
        ///     What was going on: uptime, game state, the last keys with modifiers and how long ago the
        ///     last input was. An Alt+F4 never reaches the game as a key under GNOME/Wayland (the
        ///     compositor consumes it and just closes the window), so "WindowClose with no input for
        ///     minutes" and "WindowClose right after Alt" are different stories.
        /// </summary>
        public static string Context()
        {
            string state;
            try
            {
                state = DescribeState != null ? DescribeState() : "n/a";
            }
            catch (Exception e)
            {
                state = "unavailable (" + e.GetType().Name + ")";
            }

            lock (_Lock)
            {
                return "uptime=" + _Uptime.Elapsed.ToString(@"d\.hh\:mm\:ss") +
                       "; " + state +
                       "; lastKey=" + _Ago(_LastKeyMs) +
                       "; lastMouse=" + _Ago(_LastMouseMs) +
                       "; lastWebRemoteKey=" + (_LastRemoteKey == null ? "never" : _LastRemoteKey + " " + _Ago(_LastRemoteKeyMs)) +
                       "; recentInput=[" + string.Join(", ", _RecentInput) + "]" +
                       "; rawWindowEvents=[" + string.Join(", ", _RecentRaw) + "]";
            }
        }

        #endregion

        #region requests

        /// <summary>
        ///     Something wants the program to end. The first request fixes the reason; the main loop
        ///     stops at its next iteration. Safe to call from any thread.
        /// </summary>
        public static void Request(EExitReason reason, string detail, int exitCode = 0)
        {
            string context = Context();
            bool first;
            EExitReason? earlier;
            string earlierDetail;
            lock (_Lock)
            {
                if (_ShutdownStarted)
                    return; // the shutdown itself closes windows etc.; not interesting
                earlier = _Reason;
                earlierDetail = _Detail;
                first = earlier == null;
                if (first)
                {
                    _Reason = reason;
                    _Detail = detail;
                    _ExitCode = exitCode;
                }
            }

            if (first)
            {
                string message = "Exit requested: " + reason;
                object[] data = CLog.Params(new {Reason = reason}, new {Detail = detail}, new {ExitCode = exitCode}, new {Context = context});
                if (reason == EExitReason.MenuExit || reason == EExitReason.WindowClose)
                    CLog.Information(message, data);
                else
                    CLog.Warning(message, data);
            }
            else
                CLog.Information("Further exit request ignored", CLog.Params(new {Reason = reason}, new {Detail = detail}, new {FirstReason = earlier}, new {FirstDetail = earlierDetail}));

            _StopRequested = true;
        }

        /// <summary>Called when the program starts to tear everything down.</summary>
        public static void ShutdownBegin()
        {
            bool unknown;
            lock (_Lock)
            {
                _ShutdownStarted = true;
                unknown = _Reason == null;
                if (unknown)
                {
                    _Reason = EExitReason.MainLoopEnded;
                    _Detail = "the main loop ended without anybody saying why";
                }
            }
            if (unknown)
                CLog.Warning("Shutdown without a recorded reason", CLog.Params(new {Context = Context()}));
        }

        /// <summary>The last line of every clean run.</summary>
        public static void LogShutdownComplete()
        {
            EExitReason? reason;
            string detail;
            int code;
            lock (_Lock)
            {
                _ShutdownDone = true;
                reason = _Reason;
                detail = _Detail;
                code = _ExitCode;
            }
            CLog.Information("Shutdown complete", CLog.Params(new {Reason = reason}, new {Detail = detail}, new {ExitCode = code}, new {Uptime = _Uptime.Elapsed.ToString(@"d\.hh\:mm\:ss")}));
        }

        /// <summary>
        ///     The process is going away. If our own shutdown did not run, something ended it behind our
        ///     back: Environment.Exit from elsewhere, or the runtime tearing down after a crash.
        /// </summary>
        public static void OnProcessExit(object sender, EventArgs e)
        {
            bool done;
            lock (_Lock)
                done = _ShutdownDone;
            if (!done)
                CLog.Warning("Process exit without the normal shutdown sequence",
                             CLog.Params(new {Reason = Reason}, new {Detail = Detail}, new {Context = Context()}));
        }

        #endregion

        #region signals

        /// <summary>
        ///     Handles SIGTERM, SIGINT, SIGHUP and SIGQUIT by shutting down through the normal path.
        ///     Without this Vocaluxe does not react to SIGTERM at all (measured 2026-10-08: still
        ///     running after 12 s), which is why it used to be killed with -9 and left a stale crash
        ///     marker behind.
        /// </summary>
        public static void InstallSignalHandlers()
        {
            _Register(PosixSignal.SIGTERM, 143);
            _Register(PosixSignal.SIGINT, 130);
            _Register(PosixSignal.SIGHUP, 129);
            _Register(PosixSignal.SIGQUIT, 131);
        }

        private static void _Register(PosixSignal signal, int exitCode)
        {
            try
            {
                _SignalRegistrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true; // keep the runtime from ending the process by itself
                    Request(EExitReason.Signal, signal + " received", exitCode);
                    StartKillSwitch(signal.ToString());
                }));
            }
            catch (Exception e) // not supported on every platform
            {
                CLog.Warning("Cannot handle signal", CLog.Params(new {Signal = signal}, new {Error = e.Message}));
            }
        }

        private static int _KillSwitchStarted;

        // The main thread may be the very thing that hangs (it did when .NET wrote a dump).
        // Then the graceful path never finishes: say so and end it.
        internal static void StartKillSwitch(string signal)
        {
            if (Interlocked.Exchange(ref _KillSwitchStarted, 1) != 0)
                return;
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(SignalGraceSeconds * 1000);
                bool done;
                lock (_Lock)
                    done = _ShutdownDone;
                if (done)
                    return;
                CLog.Error("Shutdown after " + signal + " did not finish within " + SignalGraceSeconds + " s - killing the process", CLog.Params(new {Context = Context()}));
                Process.GetCurrentProcess().Kill();
            }) {IsBackground = true, Name = "ExitWatchdog"};
            watchdog.Start();
        }

        #endregion
    }
}
