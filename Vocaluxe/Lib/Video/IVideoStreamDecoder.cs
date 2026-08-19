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

namespace Vocaluxe.Lib.Video
{
    /// <summary>
    ///     The handful of things the decoder thread actually asks of a video backend.
    /// </summary>
    /// <remarks>
    ///     Deliberately small. Everything that makes video playback hard - the decode thread, the ring
    ///     buffer, dropping frames to catch up, looping - lives in <see cref="Acinerella.CDecoderThread" />
    ///     and is shared by both backends, so switching one for the other really only switches who
    ///     decodes. Frames are handed over as a tightly packed BGRA buffer of Width * Height * 4 bytes,
    ///     which is the byte order the renderer uploads and what Acinerella has always produced
    ///     (its RGBA32 is ffmpeg's AV_PIX_FMT_RGB32, and that is BGRA in memory on little-endian).
    /// </remarks>
    interface IVideoStreamDecoder
    {
        /// <summary>Opens the container. Everything else may be called only after this succeeded.</summary>
        bool Open(string fileName);

        /// <summary>Length of the video in seconds, valid after <see cref="Open" />.</summary>
        float Length { get; }

        /// <summary>
        ///     Sets up the video stream itself. Called on the decoder thread, unlike <see cref="Open" />.
        /// </summary>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="frameDuration">Seconds per frame, or 0 if the file does not say.</param>
        bool OpenVideoStream(out int width, out int height, out float frameDuration);

        /// <summary>Decodes the next frame. False when there is none (end of file).</summary>
        bool GetFrame();

        /// <summary>
        ///     Decodes and throws away frames to catch up. False if the file ran out on the way.
        /// </summary>
        bool SkipFrames(int count);

        /// <summary>
        ///     Seeks to a position in seconds.
        /// </summary>
        /// <param name="time">Target position.</param>
        /// <param name="decodeFrame">
        ///     True while catching up, when the caller wants a frame decoded at the target and uses the
        ///     return value to decide whether seeking worked at all; false when merely repositioning.
        /// </param>
        bool Seek(float time, bool decodeFrame);

        /// <summary>
        ///     Hands out the frame decoded last: a pointer to Width * Height * 4 bytes of BGRA and the
        ///     time it belongs to. The pointer stays valid until the next decode.
        /// </summary>
        /// <returns>False if there is nothing to hand out.</returns>
        bool GetDecodedFrame(out IntPtr data, out float time);

        /// <summary>Releases everything. Called only when the decoder thread is no longer running.</summary>
        void Free();
    }
}
