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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Vocaluxe.Lib.FFmpeg;
using Vocaluxe.Lib.Sound.Playback.Decoder;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Holds the direct ffmpeg audio decoder against the Acinerella one it is meant to replace.
    /// </summary>
    /// <remarks>
    ///     The point of the comparison is the timeline. The wrapper truncated ffmpeg's 64 bit
    ///     timestamps to int, which went negative after 152 seconds with the time base ffmpeg uses
    ///     for MP3; the sound kept playing while everything driven by the timestamp stood still. A
    ///     decoder that merely "produces audio" would have passed that day, so these tests look at
    ///     what the timestamps do, not just whether bytes come out.
    /// </remarks>
    [TestFixture]
    public class CAudioDecoderTest
    {
        /// <summary>The point where the old int32 overflow struck, in seconds.</summary>
        private const double _OverflowPoint = 152.175;

        private struct SDecodeRun
        {
            public double LastTimeStamp;
            public double DecodedSeconds;
            public long Bytes;
            public int Frames;
            public bool Monotonic;
            public SFormatInfo Format;
            public float ReportedLength;
        }

        #region helpers

        private static IEnumerable<string> _SongFiles()
        {
            string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UltraStar Songs");
            if (!Directory.Exists(library))
                return Array.Empty<string>();
            return Directory.EnumerateFiles(library, "*.mp3", SearchOption.AllDirectories)
                            .OrderBy(f => f, StringComparer.Ordinal);
        }

        /// <summary>
        ///     Picks a file long enough to walk past the old overflow point. Uses the ffmpeg decoder
        ///     to measure, so choosing a file does not depend on the native Acinerella library being
        ///     next to the test binary.
        /// </summary>
        private static string _LongSong()
        {
            foreach (string file in _SongFiles())
            {
                var decoder = new CAudioDecoderFFmpeg();
                try
                {
                    if (decoder.Open(file) && decoder.GetLength() > _OverflowPoint + 30)
                        return file;
                }
                finally
                {
                    decoder.Close();
                }
            }
            return null;
        }

        private static SDecodeRun _DecodeAll(IAudioDecoder decoder, string file)
        {
            Assert.IsTrue(decoder.Open(file), "could not open " + file);

            var run = new SDecodeRun
                {
                    Format = decoder.GetFormatInfo(),
                    ReportedLength = decoder.GetLength(),
                    Monotonic = true,
                    LastTimeStamp = -1
                };

            int bytesPerSecond = run.Format.SamplesPerSecond * run.Format.ChannelCount * 2;
            double previous = -1;
            while (true)
            {
                byte[] buffer;
                float timeStamp;
                decoder.Decode(out buffer, out timeStamp);
                if (buffer == null)
                    break;

                if (timeStamp + 0.001 < previous)
                    run.Monotonic = false;
                previous = timeStamp;

                run.LastTimeStamp = timeStamp;
                run.Bytes += buffer.Length;
                run.Frames++;
            }
            run.DecodedSeconds = bytesPerSecond > 0 ? run.Bytes / (double)bytesPerSecond : 0;
            decoder.Close();
            return run;
        }

        #endregion helpers

        [Test]
        public void FFmpegLibrariesAreAvailable()
        {
            CNativeTestLibs.RequireFFmpeg();
        }

        [Test]
        public void DecodesAWholeSongAndKeepsTheTimeline()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongSong();
            if (file == null)
                Assert.Ignore("no song longer than " + _OverflowPoint + " s in the library");

            SDecodeRun run = _DecodeAll(new CAudioDecoderFFmpeg(), file);

            Assert.AreEqual(16, run.Format.BitDepth, "the output stream is opened as 16 bit");
            Assert.Greater(run.Format.SamplesPerSecond, 0);
            Assert.Greater(run.Format.ChannelCount, 0);
            Assert.IsTrue(run.Monotonic, "timestamps went backwards");

            // The one that matters: the old decoder stopped here and never got further.
            Assert.Greater(run.LastTimeStamp, _OverflowPoint,
                           "timestamps stop at the old int32 overflow point");
            Assert.AreEqual(run.ReportedLength, run.LastTimeStamp, 1.0,
                            "the last timestamp should land on the end of the file");
            Assert.AreEqual(run.ReportedLength, run.DecodedSeconds, 1.0,
                            "the amount of audio should match the file's length");
        }

        [Test]
        public void AgreesWithTheAcinerellaDecoder()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongSong();
            if (file == null)
                Assert.Ignore("no song longer than " + _OverflowPoint + " s in the library");

            if (!CNativeTestLibs.AcinerellaAvailable(typeof(CAudioDecoderAcinerella).Assembly))
                Assert.Ignore("libacinerella.so not built - nothing to compare against");

            SDecodeRun mine = _DecodeAll(new CAudioDecoderFFmpeg(), file);
            SDecodeRun theirs = _DecodeAll(new CAudioDecoderAcinerella(), file);

            TestContext.WriteLine(Path.GetFileName(file));
            TestContext.WriteLine(string.Format("  ffmpeg     : {0} Hz, {1} ch, {2:F2} s reported, {3:F2} s decoded, last stamp {4:F2} s, {5} frames",
                                                mine.Format.SamplesPerSecond, mine.Format.ChannelCount, mine.ReportedLength,
                                                mine.DecodedSeconds, mine.LastTimeStamp, mine.Frames));
            TestContext.WriteLine(string.Format("  acinerella : {0} Hz, {1} ch, {2:F2} s reported, {3:F2} s decoded, last stamp {4:F2} s, {5} frames",
                                                theirs.Format.SamplesPerSecond, theirs.Format.ChannelCount, theirs.ReportedLength,
                                                theirs.DecodedSeconds, theirs.LastTimeStamp, theirs.Frames));

            Assert.AreEqual(theirs.Format.SamplesPerSecond, mine.Format.SamplesPerSecond, "sample rate");
            Assert.AreEqual(theirs.Format.ChannelCount, mine.Format.ChannelCount, "channel count");
            Assert.AreEqual(theirs.ReportedLength, mine.ReportedLength, 0.5, "reported length");
            Assert.AreEqual(theirs.DecodedSeconds, mine.DecodedSeconds, 0.5, "amount of audio decoded");
        }

        [Test]
        public void SeeksToWhereItWasAsked()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongSong();
            if (file == null)
                Assert.Ignore("no song longer than " + _OverflowPoint + " s in the library");

            var decoder = new CAudioDecoderFFmpeg();
            Assert.IsTrue(decoder.Open(file));
            try
            {
                // Deliberately past the old overflow point.
                const float target = 180f;
                decoder.SetPosition(target);

                byte[] buffer;
                float timeStamp;
                decoder.Decode(out buffer, out timeStamp);

                Assert.IsNotNull(buffer, "no audio after seeking");
                Assert.AreEqual(target, timeStamp, 2.0, "landed somewhere else than asked");
            }
            finally
            {
                decoder.Close();
            }
        }
    }
}
