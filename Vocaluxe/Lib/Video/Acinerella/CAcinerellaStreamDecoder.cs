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
using System.Runtime.InteropServices;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.Acinerella
{
    /// <summary>
    ///     The Acinerella side of <see cref="IVideoStreamDecoder" />. This is the code that used to sit
    ///     inline in <see cref="CDecoderThread" />, moved out unchanged so both backends can be held
    ///     against each other.
    /// </summary>
    class CAcinerellaStreamDecoder : IVideoStreamDecoder
    {
        private IntPtr _Instance = IntPtr.Zero;
        private IntPtr _Videodecoder = IntPtr.Zero;
        private string _FileName;

        public float Length { get; private set; }

        public bool Open(string fileName)
        {
            _FileName = fileName;
            try
            {
                _Instance = CAcinerella.AcInit();
                CAcinerella.AcOpen2(_Instance, fileName);

                var instance = (SACInstance)Marshal.PtrToStructure(_Instance, typeof(SACInstance));
                Length = instance.Info.Duration / 1000f;
                if (instance.Opened && Length > 0.001f)
                    return true;
                Free();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error opening video file: " + _FileName);
                _Instance = IntPtr.Zero;
                return false;
            }
            CLog.Error("Error opening video file: " + _FileName);
            _Instance = IntPtr.Zero;
            return false;
        }

        public bool OpenVideoStream(out int width, out int height, out float frameDuration)
        {
            width = 0;
            height = 0;
            frameDuration = 0;

            SACDecoder decoder;
            try
            {
                _Videodecoder = CAcinerella.AcCreateVideoDecoder(_Instance);
                decoder = (SACDecoder)Marshal.PtrToStructure(_Videodecoder, typeof(SACDecoder));
            }
            catch (Exception)
            {
                CLog.Error("Error opening video file (can't find decoder): " + _FileName);
                return false;
            }

            if (decoder.StreamIndex < 0)
                return false;

            width = decoder.StreamInfo.VideoInfo.FrameWidth;
            height = decoder.StreamInfo.VideoInfo.FrameHeight;
            if (decoder.StreamInfo.VideoInfo.FramesPerSecond > 0)
                frameDuration = 1f / (float)decoder.StreamInfo.VideoInfo.FramesPerSecond;
            return true;
        }

        public bool GetFrame()
        {
            try
            {
                return CAcinerella.AcGetFrame(_Instance, _Videodecoder);
            }
            catch (Exception)
            {
                CLog.Error("Error AcGetFrame " + _FileName);
                return false;
            }
        }

        public bool SkipFrames(int count)
        {
            try
            {
                return CAcinerella.AcSkipFrames(_Instance, _Videodecoder, count);
            }
            catch (Exception)
            {
                CLog.Error("Error AcSkipFrame " + _FileName);
                return false;
            }
        }

        public bool Seek(float time, bool decodeFrame)
        {
            try
            {
                // The middle argument is Acinerella's seek direction; -1 means "may go backwards".
                return CAcinerella.AcSeek(_Videodecoder, decodeFrame ? 0 : -1, (Int64)(time * 1000f));
            }
            catch (Exception e)
            {
                CLog.Error("Error seeking video file \"" + _FileName + "\": " + e.Message);
                return false;
            }
        }

        public bool GetDecodedFrame(out IntPtr data, out float time)
        {
            data = IntPtr.Zero;
            time = 0;

            SACDecoder decoder;
            try
            {
                decoder = (SACDecoder)Marshal.PtrToStructure(_Videodecoder, typeof(SACDecoder));
            }
            catch (Exception e)
            {
                CLog.Error(e, "Couldn't copy the frame to the managed environment.");
                return false;
            }

            if (decoder.Buffer == IntPtr.Zero)
                return false;

            data = decoder.Buffer;
            time = (float)decoder.Timecode;
            return true;
        }

        public void Free()
        {
            if (_Videodecoder != IntPtr.Zero)
            {
                CAcinerella.AcFreeDecoder(_Videodecoder);
                _Videodecoder = IntPtr.Zero;
            }
            if (_Instance != IntPtr.Zero)
            {
                CAcinerella.AcClose(_Instance);
                CAcinerella.AcFree(_Instance);
                _Instance = IntPtr.Zero;
            }
        }
    }
}
