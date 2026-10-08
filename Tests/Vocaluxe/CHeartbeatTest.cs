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
using NUnit.Framework;
using Vocaluxe.Base;
using VocaluxeLib.Log;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     The monitor is driven with a synthetic clock (<see cref="CHeartbeat.Tick(long)" /> and
    ///     <see cref="CHeartbeat.Check" />), so nothing here sleeps or starts the thread.
    /// </summary>
    [TestFixture]
    public class CHeartbeatTest
    {
        private static readonly long _F = Stopwatch.Frequency;
        private static readonly long _T0 = 1000 * Stopwatch.Frequency;

        private string _Folder;

        private static long _At(double seconds)
        {
            return _T0 + (long)(seconds * _F);
        }

        [SetUp]
        public void SetUp()
        {
            CHeartbeat.Reset();
            _Folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_Folder);
            CLog.Init(_Folder, "Vocaluxe.log", "Song.log", "Marker", "Test Version", null, ELogLevel.Information);
        }

        [TearDown]
        public void TearDown()
        {
            CLog.Close();
            CHeartbeat.Reset();
            Directory.Delete(_Folder, true);
        }

        private string _ReadLog()
        {
            CLog.Close();
            return File.ReadAllText(Path.Combine(_Folder, "Vocaluxe.log"));
        }

        [Test]
        public void NoStallIsReportedBeforeTheMainLoopRuns()
        {
            CHeartbeat.Check(_At(0));
            CHeartbeat.Check(_At(600));

            StringAssert.DoesNotContain("Main loop stalled", _ReadLog());
        }

        [Test]
        public void ARunningLoopIsNotReported()
        {
            for (int s = 0; s < 30; s++)
            {
                CHeartbeat.Tick(_At(s));
                CHeartbeat.Check(_At(s + 0.5));
            }

            StringAssert.DoesNotContain("stalled", _ReadLog());
        }

        [Test]
        public void StallIsReportedOnceWithTheLastKnownState()
        {
            CHeartbeat.DescribeState = () => "screen=CScreenSing; queue: 3 waiting";
            CHeartbeat.Tick(_At(0));

            CHeartbeat.Check(_At(3)); // below the threshold
            CHeartbeat.Check(_At(6));
            CHeartbeat.Check(_At(7));
            CHeartbeat.Check(_At(8));

            string log = _ReadLog();
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(log, "Main loop stalled").Count, "must not repeat every second");
            StringAssert.Contains("screen=CScreenSing; queue: 3 waiting", log);
            StringAssert.Contains("snapshot", log);
            StringAssert.Contains("MainThread", log);
        }

        [Test]
        public void ALongStallIsRemindedNotForgotten()
        {
            CHeartbeat.Tick(_At(0));
            CHeartbeat.Check(_At(6));

            CHeartbeat.Check(_At(20)); // still within the reminder interval
            CHeartbeat.Check(_At(40));

            string log = _ReadLog();
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(log, "Main loop still stalled").Count);
        }

        [Test]
        public void AResumedLoopIsReportedWithTheStallDuration()
        {
            CHeartbeat.Tick(_At(0));
            CHeartbeat.Check(_At(8));
            CHeartbeat.Tick(_At(12));
            CHeartbeat.Check(_At(12.5));

            string log = _ReadLog();
            StringAssert.Contains("Main loop resumed", log);
            StringAssert.Contains("StalledForSeconds = 12", log);
        }

        [Test]
        public void HeartbeatComesOncePerInterval()
        {
            CHeartbeat.DescribeState = () => "screen=CScreenSong";
            CHeartbeat.Tick(_At(0));
            CHeartbeat.Check(_At(0)); // sets the baseline
            for (int s = 1; s <= 59; s++)
            {
                CHeartbeat.Tick(_At(s));
                CHeartbeat.Check(_At(s));
            }
            CHeartbeat.Tick(_At(61));
            CHeartbeat.Check(_At(61));
            CHeartbeat.Tick(_At(62));
            CHeartbeat.Check(_At(62));

            string log = _ReadLog();
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(log, @"\[Information\] Heartbeat").Count, "exactly one line per interval");
            StringAssert.Contains("WorkingSetMB", log);
            StringAssert.Contains("OpenFiles", log);
            StringAssert.Contains("ManagedHeapMB", log);
            StringAssert.Contains("Fps", log);
            StringAssert.Contains("screen=CScreenSong", log);
        }

        [Test]
        public void HeartbeatSaysWhenTheLoopIsStalled()
        {
            CHeartbeat.Tick(_At(0));
            CHeartbeat.Check(_At(0));
            CHeartbeat.Check(_At(70)); // no tick since 0: stalled, and the minute is up

            StringAssert.Contains("MainLoopStalled = True", _ReadLog());
        }

        [Test]
        [Platform("Linux")]
        public void MainThreadIsDescribedFromProc()
        {
            string description = CHeartbeat.DescribeMainThread();

            StringAssert.IsMatch(@"^state=[A-Za-z] \([a-z /]+\), wchan=\S+$", description);
            Assert.GreaterOrEqual(CHeartbeat.MainThreadCpuTicks(), 0);
        }

        [Test]
        public void SnapshotIsRefreshedOnlyEveryFewSeconds()
        {
            int calls = 0;
            CHeartbeat.DescribeState = () => "call " + ++calls;

            CHeartbeat.Tick(_At(0));
            CHeartbeat.Tick(_At(1));
            CHeartbeat.Tick(_At(2));
            Assert.AreEqual(1, calls, "the state is built on the main thread, not every frame");

            CHeartbeat.Tick(_At(6));
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void BrokenStateProviderDoesNotBreakTheMainLoop()
        {
            CHeartbeat.DescribeState = () => { throw new InvalidOperationException("queue locked"); };

            Assert.DoesNotThrow(() => CHeartbeat.Tick(_At(0)));
            CHeartbeat.Check(_At(8));

            StringAssert.Contains("unavailable", _ReadLog());
        }
    }
}
