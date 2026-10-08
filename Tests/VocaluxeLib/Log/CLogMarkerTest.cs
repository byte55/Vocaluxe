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
using VocaluxeLib.Log;

namespace Tests.VocaluxeLib.Log
{
    /// <summary>
    /// The crash marker says "the previous run did not end through CLog.Close()". Finding one used to
    /// throw (the reporter delegate is null on the cross-platform build), which made Vocaluxe exit with
    /// code 0 and no log entry on the first start after every crash or kill.
    /// </summary>
    [TestFixture]
    public class CLogMarkerTest
    {
        private const string _VersionTag = "Test Version (1.2.4)";
        private const string _MainLog = "Vocaluxe.log";
        private const string _SongLog = "Song.log";
        private const string _Marker = "Marker";

        private string _Folder;

        [SetUp]
        public void SetUp()
        {
            _Folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_Folder);
        }

        [TearDown]
        public void TearDown()
        {
            CLog.Close(); // no-op if the test already closed
            Directory.Delete(_Folder, true);
        }

        private string _Path(string name)
        {
            return Path.Combine(_Folder, name);
        }

        private void _Init(ShowReporterDelegate reporter = null)
        {
            CLog.Init(_Folder, _MainLog, _SongLog, _Marker, _VersionTag, reporter, ELogLevel.Information);
        }

        private void _LeaveStaleRun()
        {
            File.WriteAllText(_Path(_MainLog), "log of the run that died");
            File.WriteAllText(_Path(_Marker), _VersionTag + "\nPid=4711\nStart=2026-09-18T23:00:00");
        }

        [Test]
        public void StaleMarkerWithoutReporterDoesNotThrow()
        {
            _LeaveStaleRun();

            Assert.DoesNotThrow(() => _Init());
        }

        [Test]
        public void StaleMarkerIsWrittenToTheNewLog()
        {
            _LeaveStaleRun();

            _Init();
            CLog.Close();

            string log = File.ReadAllText(_Path(_MainLog));
            StringAssert.Contains("Previous run did not shut down cleanly", log);
            StringAssert.Contains("Pid=4711", log, "marker content of the dead run is missing");
        }

        [Test]
        public void StaleMarkerOfOtherVersionIsStillLogged()
        {
            File.WriteAllText(_Path(_MainLog), "log of the run that died");
            File.WriteAllText(_Path(_Marker), "Some Other Version");

            _Init();
            CLog.Close();

            StringAssert.Contains("Previous run did not shut down cleanly", File.ReadAllText(_Path(_MainLog)));
        }

        [Test]
        public void StaleMarkerStillCallsReporterIfPresent()
        {
            _LeaveStaleRun();
            bool called = false;

            _Init((crash, cont, tag, log, error) =>
            {
                called = true;
                Assert.IsTrue(crash);
                Assert.AreEqual(_VersionTag, tag);
            });

            Assert.IsTrue(called);
        }

        [Test]
        public void CleanShutdownLeavesNoMarkerAndNoWarning()
        {
            _Init();
            CLog.Close();
            Assert.IsFalse(File.Exists(_Path(_Marker)), "Close() must delete the marker");

            _Init();
            CLog.Close();
            StringAssert.DoesNotContain("did not shut down cleanly", File.ReadAllText(_Path(_MainLog)));
        }

        [Test]
        public void MarkerNamesTheRunToBlame()
        {
            _Init();

            string[] lines = File.ReadAllLines(_Path(_Marker));
            Assert.AreEqual(_VersionTag, lines[0].Trim(), "first line has to stay the version tag");
            Assert.That(lines, Has.Some.StartsWith("Pid=" + Environment.ProcessId));
            Assert.That(lines, Has.Some.StartsWith("Start="));
            Assert.That(lines, Has.Some.StartsWith("BootId="));
        }

        [Test]
        public void KeepsFiftyOldMainLogs()
        {
            File.WriteAllText(_Path(_MainLog), "newest old run");
            for (int i = 1; i <= 60; i++)
                File.WriteAllText(_Path("Vocaluxe_" + i + ".log"), "run " + i);

            _Init();
            CLog.Close();

            Assert.IsTrue(File.Exists(_Path("Vocaluxe_50.log")), "50th old log has to survive");
            Assert.IsFalse(File.Exists(_Path("Vocaluxe_51.log")), "51st old log has to go");
            Assert.AreEqual("newest old run", File.ReadAllText(_Path("Vocaluxe_1.log")));
            Assert.AreEqual("run 49", File.ReadAllText(_Path("Vocaluxe_50.log")));
        }
    }
}
