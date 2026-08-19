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
using FFmpeg.AutoGen;
using Vocaluxe.Lib.FFmpeg;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.FFmpeg
{
    /// <summary>
    ///     Video decoding straight against ffmpeg, without the Acinerella C wrapper in between.
    /// </summary>
    /// <remarks>
    ///     Produces the same thing Acinerella did: tightly packed BGRA, Width * Height * 4 bytes.
    ///     Acinerella asked ffmpeg for AV_PIX_FMT_RGB32, which on a little-endian machine is BGRA in
    ///     memory - naming it RGBA here would swap red and blue and nothing would fail, it would just
    ///     look wrong.
    ///     <para>
    ///         Timestamps are kept in ffmpeg's int64 units and only narrowed at the very end. The
    ///         wrapper cast them to int, which went negative after 152 seconds with the time base
    ///         ffmpeg uses for MP3 and froze the picture while the sound played on.
    ///     </para>
    /// </remarks>
    unsafe class CFFmpegStreamDecoder : IVideoStreamDecoder
    {
        private AVFormatContext* _Format;
        private AVCodecContext* _Codec;
        private SwsContext* _Scaler;
        private AVPacket* _Packet;
        private AVFrame* _Frame;
        private AVFrame* _FrameBgra;
        private byte* _BgraBuffer;

        private int _StreamIndex = -1;
        private AVRational _TimeBase;
        private int _Width;
        private int _Height;
        private bool _HasFrame;
        private string _FileName;

        public float Length { get; private set; }

        #region opening

        public bool Open(string fileName)
        {
            _FileName = fileName;
            if (!CFFmpegLoader.IsAvailable)
                return false;

            try
            {
                fixed (AVFormatContext** format = &_Format)
                {
                    int res = ffmpeg.avformat_open_input(format, fileName, null, null);
                    if (res < 0)
                    {
                        CLog.Error("Error opening video file: " + fileName + " (" + CFFmpegLoader.ErrorText(res) + ")");
                        return false;
                    }
                }

                if (ffmpeg.avformat_find_stream_info(_Format, null) < 0)
                {
                    CLog.Error("Error reading stream info: " + fileName);
                    Free();
                    return false;
                }

                Length = _Format->duration > 0 ? _Format->duration / (float)ffmpeg.AV_TIME_BASE : 0f;
                if (Length > 0.001f)
                    return true;

                CLog.Error("Error opening video file (no usable duration): " + fileName);
                Free();
                return false;
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error opening video file: " + fileName);
                Free();
                return false;
            }
        }

        public bool OpenVideoStream(out int width, out int height, out float frameDuration)
        {
            width = 0;
            height = 0;
            frameDuration = 0;

            AVCodec* codec = null;
            _StreamIndex = ffmpeg.av_find_best_stream(_Format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (_StreamIndex < 0 || codec == null)
            {
                CLog.Error("Error opening video file (can't find decoder): " + _FileName);
                return false;
            }

            AVStream* stream = _Format->streams[_StreamIndex];
            _TimeBase = stream->time_base;

            _Codec = ffmpeg.avcodec_alloc_context3(codec);
            if (_Codec == null || ffmpeg.avcodec_parameters_to_context(_Codec, stream->codecpar) < 0)
            {
                CLog.Error("Error setting up the video decoder: " + _FileName);
                return false;
            }

            if (ffmpeg.avcodec_open2(_Codec, codec, null) < 0)
            {
                CLog.Error("Error opening the video decoder: " + _FileName);
                return false;
            }

            _Width = _Codec->width;
            _Height = _Codec->height;
            if (_Width <= 0 || _Height <= 0)
            {
                CLog.Error("Video file has no usable frame size: " + _FileName);
                return false;
            }

            AVRational rate = ffmpeg.av_guess_frame_rate(_Format, stream, null);
            if (rate.num > 0 && rate.den > 0)
                frameDuration = (float)(rate.den / (double)rate.num);

            if (!_AllocateFrames())
                return false;

            width = _Width;
            height = _Height;
            return true;
        }

        private bool _AllocateFrames()
        {
            _Packet = ffmpeg.av_packet_alloc();
            _Frame = ffmpeg.av_frame_alloc();
            _FrameBgra = ffmpeg.av_frame_alloc();
            if (_Packet == null || _Frame == null || _FrameBgra == null)
            {
                CLog.Error("Out of memory opening video file: " + _FileName);
                return false;
            }

            // One buffer, reused for every frame: the decoder thread copies it into its ring buffer
            // before asking for the next one.
            int size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _Width, _Height, 1);
            _BgraBuffer = (byte*)ffmpeg.av_malloc((ulong)size);
            if (_BgraBuffer == null)
            {
                CLog.Error("Out of memory allocating the frame buffer: " + _FileName);
                return false;
            }

            byte_ptrArray4 data = new byte_ptrArray4();
            int_array4 lineSizes = new int_array4();
            ffmpeg.av_image_fill_arrays(ref data, ref lineSizes, _BgraBuffer,
                                        AVPixelFormat.AV_PIX_FMT_BGRA, _Width, _Height, 1);
            for (uint i = 0; i < 4; i++)
            {
                _FrameBgra->data[i] = data[i];
                _FrameBgra->linesize[i] = lineSizes[i];
            }

            // No scaling, only a pixel format change, so the scaler choice barely matters - bilinear
            // is what Acinerella's path ends up using too.
            _Scaler = ffmpeg.sws_getContext(_Width, _Height, _Codec->pix_fmt,
                                            _Width, _Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                                            (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_Scaler == null)
            {
                CLog.Error("Error setting up the colour conversion: " + _FileName);
                return false;
            }
            return true;
        }

        #endregion opening

        public bool GetFrame()
        {
            while (true)
            {
                int res = ffmpeg.avcodec_receive_frame(_Codec, _Frame);
                if (res == 0)
                {
                    _HasFrame = true;
                    return true;
                }
                if (res != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    return false; // end of file, or a file we cannot read any further

                if (!_FeedDecoder())
                    return false;
            }
        }

        /// <summary>Pushes the next packet of our stream into the decoder. False once it is through.</summary>
        private bool _FeedDecoder()
        {
            while (true)
            {
                int res = ffmpeg.av_read_frame(_Format, _Packet);
                if (res < 0)
                {
                    ffmpeg.avcodec_send_packet(_Codec, null); // drain
                    return res == ffmpeg.AVERROR_EOF;
                }

                if (_Packet->stream_index != _StreamIndex)
                {
                    ffmpeg.av_packet_unref(_Packet);
                    continue;
                }

                res = ffmpeg.avcodec_send_packet(_Codec, _Packet);
                ffmpeg.av_packet_unref(_Packet);
                if (res < 0 && res != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    CLog.Error("Error decoding video: " + _FileName + " (" + CFFmpegLoader.ErrorText(res) + ")");
                    return false;
                }
                return true;
            }
        }

        public bool SkipFrames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!GetFrame())
                    return false;
            }
            return _HasFrame;
        }

        public bool Seek(float time, bool decodeFrame)
        {
            long target = (long)(time / ffmpeg.av_q2d(_TimeBase));
            // Land at or before the target; the decoder thread drops the rest to get exactly there.
            int res = ffmpeg.av_seek_frame(_Format, _StreamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (res < 0)
            {
                CLog.Error("Error seeking video file \"" + _FileName + "\": " + CFFmpegLoader.ErrorText(res));
                return false;
            }

            ffmpeg.avcodec_flush_buffers(_Codec);
            _HasFrame = false;

            if (!decodeFrame)
                return true;

            // Asked to arrive with a frame in hand: decode forward until we are at the target, so the
            // caller does not show something from before the seek.
            if (!GetFrame())
                return false;
            while (_FrameTime() < time - 0.001f)
            {
                if (!GetFrame())
                    return false;
            }
            return true;
        }

        public bool GetDecodedFrame(out IntPtr data, out float time)
        {
            data = IntPtr.Zero;
            time = 0;
            if (!_HasFrame)
                return false;

            int res = ffmpeg.sws_scale(_Scaler, _Frame->data, _Frame->linesize, 0, _Height,
                                       _FrameBgra->data, _FrameBgra->linesize);
            if (res <= 0)
            {
                CLog.Error("Error converting the video frame: " + _FileName);
                return false;
            }

            data = (IntPtr)_BgraBuffer;
            time = _FrameTime();
            return true;
        }

        private float _FrameTime()
        {
            long pts = _Frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                pts = _Frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                return 0f;
            // int64 until here. See the remark on the class.
            return (float)(pts * ffmpeg.av_q2d(_TimeBase));
        }

        public void Free()
        {
            _HasFrame = false;

            if (_Scaler != null)
            {
                ffmpeg.sws_freeContext(_Scaler);
                _Scaler = null;
            }
            if (_BgraBuffer != null)
            {
                ffmpeg.av_free(_BgraBuffer);
                _BgraBuffer = null;
            }
            if (_FrameBgra != null)
            {
                fixed (AVFrame** frame = &_FrameBgra)
                    ffmpeg.av_frame_free(frame);
            }
            if (_Frame != null)
            {
                fixed (AVFrame** frame = &_Frame)
                    ffmpeg.av_frame_free(frame);
            }
            if (_Packet != null)
            {
                fixed (AVPacket** packet = &_Packet)
                    ffmpeg.av_packet_free(packet);
            }
            if (_Codec != null)
            {
                fixed (AVCodecContext** codec = &_Codec)
                    ffmpeg.avcodec_free_context(codec);
            }
            if (_Format != null)
            {
                fixed (AVFormatContext** format = &_Format)
                    ffmpeg.avformat_close_input(format);
            }
            _StreamIndex = -1;
        }
    }
}
