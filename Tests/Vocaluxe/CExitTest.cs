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
using System.IO;
using NUnit.Framework;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     CExit records why the program ends. The signal handlers are not covered here on purpose:
    ///     registering one inside the test host would kill the host after the grace period.
    /// </summary>
    [TestFixture]
    public class CExitTest
    {
        private string _Folder;

        [SetUp]
        public void SetUp()
        {
            CExit.Reset();
            _Folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_Folder);
        }

        [TearDown]
        public void TearDown()
        {
            CLog.Close();
            CExit.Reset();
            Directory.Delete(_Folder, true);
        }

        private static SKeyEvent _Key(Keys key, bool alt = false, bool ctrl = false, bool shift = false)
        {
            return new SKeyEvent(ESender.Keyboard, alt, shift, ctrl, true, '\0', key);
        }

        [Test]
        public void FirstRequestWins()
        {
            CExit.Request(EExitReason.MenuExit, "first");
            CExit.Request(EExitReason.WindowClose, "second");

            Assert.AreEqual(EExitReason.MenuExit, CExit.Reason);
            Assert.AreEqual("first", CExit.Detail);
        }

        [Test]
        public void RequestStopsTheMainLoop()
        {
            Assert.IsFalse(CExit.StopRequested);

            CExit.Request(EExitReason.WindowClose, "x");

            Assert.IsTrue(CExit.StopRequested);
        }

        [Test]
        public void ExitCodeIsKeptButNotApplied()
        {
            CExit.Request(EExitReason.Signal, "SIGTERM received", 143);

            Assert.AreEqual(143, CExit.ExitCode);
            Assert.AreNotEqual(143, Environment.ExitCode, "applying the code is the program's job");
        }

        [Test]
        public void ShutdownWithoutReasonIsFlagged()
        {
            CExit.ShutdownBegin();

            Assert.AreEqual(EExitReason.MainLoopEnded, CExit.Reason);
        }

        [Test]
        public void RequestsDuringShutdownAreIgnored()
        {
            CExit.Request(EExitReason.MenuExit, "real reason");
            CExit.ShutdownBegin();

            // the shutdown closes the window, which raises a Closing event
            CExit.Request(EExitReason.WindowClose, "caused by the shutdown itself");

            Assert.AreEqual(EExitReason.MenuExit, CExit.Reason);
        }

        [Test]
        public void ContextNamesTheLastKeysWithModifiers()
        {
            CExit.NoteKey(_Key(Keys.Escape));
            CExit.NoteKey(_Key(Keys.F4, alt: true));

            string context = CExit.Context();

            StringAssert.Contains("Alt+F4", context);
            StringAssert.Contains("Escape", context);
        }

        [Test]
        public void ContextKeepsOnlyTheLastEightInputs()
        {
            for (int i = 0; i < 20; i++)
                CExit.NoteKey(_Key(i % 2 == 0 ? Keys.Left : Keys.Right));
            CExit.NoteKey(_Key(Keys.Enter, ctrl: true));

            string recent = CExit.Context();
            recent = recent.Substring(recent.IndexOf("recentInput=[", StringComparison.Ordinal));

            Assert.AreEqual(8, recent.Split(new[] {" @"}, StringSplitOptions.None).Length - 1);
            StringAssert.Contains("Ctrl+Enter", recent);
        }

        [Test]
        public void ContextShowsWhatTheWindowReceivedRaw()
        {
            CExit.NoteRawKey("LeftAlt down +Alt");
            CExit.NoteRawKey("F4 down +Alt");
            CExit.NoteRawKey("window lost focus");

            string context = CExit.Context();

            StringAssert.Contains("rawWindowEvents=[LeftAlt down +Alt @", context);
            StringAssert.Contains("F4 down +Alt", context);
            StringAssert.Contains("window lost focus", context);
        }

        [Test]
        public void RawWindowEventsKeepOnlyTheLastTwelve()
        {
            for (int i = 0; i < 30; i++)
                CExit.NoteRawKey("key" + i);

            string raw = CExit.Context();
            raw = raw.Substring(raw.IndexOf("rawWindowEvents=[", StringComparison.Ordinal));

            Assert.AreEqual(12, raw.Split(new[] {" @"}, StringSplitOptions.None).Length - 1);
            StringAssert.Contains("key29", raw);
            StringAssert.DoesNotContain("key17 ", raw);
        }

        [Test]
        public void ContextNamesTheWebRemoteKey()
        {
            CExit.NoteRemoteKey("return");

            StringAssert.Contains("lastWebRemoteKey=return", CExit.Context());
        }

        [Test]
        public void ContextSaysWhenThereWasNoInput()
        {
            string context = CExit.Context();

            StringAssert.Contains("lastKey=never", context);
            StringAssert.Contains("lastMouse=never", context);
            StringAssert.Contains("lastWebRemoteKey=never", context);
        }

        [Test]
        public void ContextShowsTheGameState()
        {
            CExit.DescribeState = () => "screen=CScreenSing";

            StringAssert.Contains("screen=CScreenSing", CExit.Context());
        }

        [Test]
        public void BrokenStateProviderDoesNotBreakTheExitPath()
        {
            CExit.DescribeState = () => { throw new InvalidOperationException("graphics not up"); };

            string context = null;
            Assert.DoesNotThrow(() => context = CExit.Context());
            StringAssert.Contains("unavailable", context);
        }

        [Test]
        public void ReasonAndContextLandInTheLog()
        {
            CLog.Init(_Folder, "Vocaluxe.log", "Song.log", "Marker", "Test Version", null, ELogLevel.Information);
            CExit.NoteKey(_Key(Keys.F4, alt: true));

            CExit.Request(EExitReason.WindowClose, "close request from the window system (test)", 0);
            CExit.ShutdownBegin();
            CExit.LogShutdownComplete();
            CLog.Close();

            string log = File.ReadAllText(Path.Combine(_Folder, "Vocaluxe.log"));
            StringAssert.Contains("Exit requested: WindowClose", log);
            StringAssert.Contains("close request from the window system (test)", log);
            StringAssert.Contains("Alt+F4", log);
            StringAssert.Contains("Shutdown complete", log);
        }

        [Test]
        public void KillSwitchLeavesAFinishedShutdownAlone()
        {
            // The positive case (killing a hung process) cannot be tested in-process.
            CExit.SignalGraceSeconds = 1;
            CExit.Request(EExitReason.Signal, "SIGTERM received", 143);
            CExit.ShutdownBegin();
            CExit.LogShutdownComplete();

            CExit.StartKillSwitch("SIGTERM");
            System.Threading.Thread.Sleep(2500); // if the switch fired, the test host would be gone

            Assert.Pass();
        }

        [Test]
        public void SecondRequestIsLoggedAsIgnored()
        {
            CLog.Init(_Folder, "Vocaluxe.log", "Song.log", "Marker", "Test Version", null, ELogLevel.Information);

            CExit.Request(EExitReason.MenuExit, "first");
            CExit.Request(EExitReason.Signal, "second", 143);
            CLog.Close();

            string log = File.ReadAllText(Path.Combine(_Folder, "Vocaluxe.log"));
            StringAssert.Contains("Further exit request ignored", log);
            Assert.AreEqual(EExitReason.MenuExit, CExit.Reason);
        }
    }
}
