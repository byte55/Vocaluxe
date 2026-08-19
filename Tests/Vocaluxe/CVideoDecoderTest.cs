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
using Vocaluxe.Lib.Video;
using Vocaluxe.Lib.Video.Acinerella;
using Vocaluxe.Lib.Video.FFmpeg;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Holds the direct ffmpeg video backend against the Acinerella one it is meant to replace.
    /// </summary>
    /// <remarks>
    ///     The pixel comparison is the point. Acinerella asks ffmpeg for AV_PIX_FMT_RGB32, which is
    ///     BGRA in memory on a little-endian machine; a backend that writes RGBA instead would decode
    ///     perfectly, drop no frames, pass every timing check - and put blue faces on the beamer.
    ///     Nothing but comparing the bytes catches that.
    /// </remarks>
    [TestFixture]
    public class CVideoDecoderTest
    {
        private const double _OverflowPoint = 152.175;

        #region helpers

        private static string _LongVideo()
        {
            string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UltraStar Songs");
            if (!Directory.Exists(library))
                return null;

            foreach (string file in Directory.EnumerateFiles(library, "*.mp4", SearchOption.AllDirectories)
                                             .OrderBy(f => f, StringComparer.Ordinal))
            {
                var decoder = new CFFmpegStreamDecoder();
                try
                {
                    if (decoder.Open(file) && decoder.Length > _OverflowPoint + 30)
                        return file;
                }
                finally
                {
                    decoder.Free();
                }
            }
            return null;
        }

        /// <summary>Decodes the next frame and copies it out as managed bytes.</summary>
        private static byte[] _NextFrame(IVideoStreamDecoder decoder, int size, out float time)
        {
            time = 0;
            if (!decoder.GetFrame())
                return null;

            IntPtr data;
            if (!decoder.GetDecodedFrame(out data, out time))
                return null;

            var managed = new byte[size];
            Marshal.Copy(data, managed, 0, size);
            return managed;
        }

        private static string _Skip()
        {
            if (!CNativeTestLibs.AcinerellaAvailable(typeof(CAcinerellaStreamDecoder).Assembly))
                return "libacinerella.so not built - nothing to compare against";
            return null;
        }

        #endregion helpers

        [Test]
        public void ProducesTheSamePixelsAsAcinerella()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongVideo();
            if (file == null)
                Assert.Ignore("no video longer than " + _OverflowPoint + " s in the library");
            string skip = _Skip();
            if (skip != null)
                Assert.Ignore(skip);

            IVideoStreamDecoder mine = new CFFmpegStreamDecoder();
            IVideoStreamDecoder theirs = new CAcinerellaStreamDecoder();
            try
            {
                Assert.IsTrue(mine.Open(file), "ffmpeg could not open " + file);
                Assert.IsTrue(theirs.Open(file), "acinerella could not open " + file);
                Assert.AreEqual(theirs.Length, mine.Length, 0.5, "length");

                int myWidth, myHeight, theirWidth, theirHeight;
                float myDuration, theirDuration;
                Assert.IsTrue(mine.OpenVideoStream(out myWidth, out myHeight, out myDuration));
                Assert.IsTrue(theirs.OpenVideoStream(out theirWidth, out theirHeight, out theirDuration));

                Assert.AreEqual(theirWidth, myWidth, "frame width");
                Assert.AreEqual(theirHeight, myHeight, "frame height");
                Assert.AreEqual(theirDuration, myDuration, 0.001, "frame duration");

                int size = myWidth * myHeight * 4;
                int compared = 0;
                for (int i = 0; i < 20; i++)
                {
                    float myTime, theirTime;
                    byte[] myFrame = _NextFrame(mine, size, out myTime);
                    byte[] theirFrame = _NextFrame(theirs, size, out theirTime);
                    if (myFrame == null || theirFrame == null)
                        break;

                    Assert.AreEqual(theirTime, myTime, 0.001, "timestamp of frame " + i);
                    // Byte for byte: same pixel format, same channel order, same content.
                    Assert.AreEqual(theirFrame, myFrame, "pixels of frame " + i);
                    compared++;
                }

                Assert.GreaterOrEqual(compared, 10, "decoded too few frames to say anything");
                TestContext.WriteLine(Path.GetFileName(file) + ": " + compared + " frames identical, "
                                      + myWidth + "x" + myHeight + ", " + myDuration.ToString("F4") + " s per frame");
            }
            finally
            {
                mine.Free();
                theirs.Free();
            }
        }

        [Test]
        public void KeepsTheTimelinePastTheOverflowPoint()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongVideo();
            if (file == null)
                Assert.Ignore("no video longer than " + _OverflowPoint + " s in the library");

            IVideoStreamDecoder decoder = new CFFmpegStreamDecoder();
            try
            {
                Assert.IsTrue(decoder.Open(file));
                int width, height;
                float frameDuration;
                Assert.IsTrue(decoder.OpenVideoStream(out width, out height, out frameDuration));

                // Land past the point where the old int32 timestamp went negative and walk on.
                Assert.IsTrue(decoder.Seek(160f, true), "seek failed");

                int size = width * height * 4;
                float previous = -1;
                int frames = 0;
                for (int i = 0; i < 60; i++)
                {
                    float time;
                    if (_NextFrame(decoder, size, out time) == null)
                        break;
                    Assert.Greater(time, _OverflowPoint, "timestamp fell back before the overflow point");
                    Assert.GreaterOrEqual(time + 0.001, previous, "timestamps went backwards");
                    previous = time;
                    frames++;
                }

                Assert.GreaterOrEqual(frames, 30, "too few frames after seeking");
                TestContext.WriteLine("last timestamp " + previous.ToString("F2") + " s after " + frames + " frames");
            }
            finally
            {
                decoder.Free();
            }
        }

        [Test]
        public void SeeksToWhereItWasAsked()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongVideo();
            if (file == null)
                Assert.Ignore("no video longer than " + _OverflowPoint + " s in the library");

            IVideoStreamDecoder decoder = new CFFmpegStreamDecoder();
            try
            {
                Assert.IsTrue(decoder.Open(file));
                int width, height;
                float frameDuration;
                Assert.IsTrue(decoder.OpenVideoStream(out width, out height, out frameDuration));

                foreach (float target in new[] {30f, 100f, 180f})
                {
                    Assert.IsTrue(decoder.Seek(target, true), "seek to " + target + " s failed");

                    float time;
                    Assert.IsNotNull(_NextFrame(decoder, width * height * 4, out time),
                                     "no frame after seeking to " + target + " s");
                    Assert.AreEqual(target, time, 1.0, "landed somewhere else than asked");
                }
            }
            finally
            {
                decoder.Free();
            }
        }

        [Test]
        public void SkippingFramesAdvancesTheTimeline()
        {
            CNativeTestLibs.RequireFFmpeg();
            string file = _LongVideo();
            if (file == null)
                Assert.Ignore("no video longer than " + _OverflowPoint + " s in the library");

            IVideoStreamDecoder decoder = new CFFmpegStreamDecoder();
            try
            {
                Assert.IsTrue(decoder.Open(file));
                int width, height;
                float frameDuration;
                Assert.IsTrue(decoder.OpenVideoStream(out width, out height, out frameDuration));

                float before;
                Assert.IsNotNull(_NextFrame(decoder, width * height * 4, out before));

                const int skip = 25;
                Assert.IsTrue(decoder.SkipFrames(skip), "skipping failed");

                float after;
                Assert.IsNotNull(_NextFrame(decoder, width * height * 4, out after));

                // Each skipped frame is worth one frame duration, give or take a variable frame rate.
                Assert.AreEqual(before + (skip + 1) * frameDuration, after, frameDuration * 5,
                                "skipping did not move the timeline by roughly the right amount");
            }
            finally
            {
                decoder.Free();
            }
        }
    }
}
