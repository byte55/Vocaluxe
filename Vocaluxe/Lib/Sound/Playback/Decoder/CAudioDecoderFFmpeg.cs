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

namespace Vocaluxe.Lib.Sound.Playback.Decoder
{
    /// <summary>
    ///     Audio decoding straight against ffmpeg, without the Acinerella C wrapper in between.
    ///     Outputs interleaved 16 bit PCM at the file's own sample rate and channel count, which is
    ///     what <see cref="PortAudio.CPortAudioStream" /> opens its output stream with.
    /// </summary>
    /// <remarks>
    ///     Timestamps stay in ffmpeg's own int64 units until the very last step. The wrapper this
    ///     replaces cast them to int, which overflowed after INT32_MAX / 14112000 = 152 seconds with
    ///     the time base ffmpeg uses for MP3 - the picture froze while the sound played on. Anything
    ///     that touches time here should keep that in mind.
    /// </remarks>
    unsafe class CAudioDecoderFFmpeg : IAudioDecoder, IDisposable
    {
        private AVFormatContext* _Format;
        private AVCodecContext* _Codec;
        private SwrContext* _Resampler;
        private AVPacket* _Packet;
        private AVFrame* _Frame;

        private int _StreamIndex = -1;
        private AVRational _TimeBase;
        private SFormatInfo _FormatInfo;
        private double _CurrentTime;
        private double _Length;

        private string _FileName;
        private bool _FileOpened;

        #region opening

        public bool Open(string fileName)
        {
            _FileName = fileName;

            // Don't rely on the caller having done this: binding to the ffmpeg libraries is this
            // decoder's own precondition, and it is a no-op after the first time.
            if (!CFFmpegLoader.IsAvailable)
                return false;

            try
            {
                if (!_OpenInput() || !_OpenDecoder() || !_OpenResampler())
                {
                    Close();
                    return false;
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error opening audio file: " + _FileName);
                Close();
                return false;
            }

            _Packet = ffmpeg.av_packet_alloc();
            _Frame = ffmpeg.av_frame_alloc();
            if (_Packet == null || _Frame == null)
            {
                CLog.Error("Out of memory opening audio file: " + _FileName);
                Close();
                return false;
            }

            _CurrentTime = 0;
            _FileOpened = true;
            return true;
        }

        private bool _OpenInput()
        {
            fixed (AVFormatContext** format = &_Format)
            {
                int res = ffmpeg.avformat_open_input(format, _FileName, null, null);
                if (res < 0)
                {
                    CLog.Error("Error opening audio file: " + _FileName + " (" + _ErrorText(res) + ")");
                    return false;
                }
            }

            if (ffmpeg.avformat_find_stream_info(_Format, null) < 0)
            {
                CLog.Error("Error reading stream info: " + _FileName);
                return false;
            }

            // The duration ffmpeg reports for the container, in its own microsecond base.
            _Length = _Format->duration > 0 ? _Format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            return true;
        }

        private bool _OpenDecoder()
        {
            AVCodec* codec = null;
            _StreamIndex = ffmpeg.av_find_best_stream(_Format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (_StreamIndex < 0 || codec == null)
            {
                CLog.Error("No audio stream in file: " + _FileName);
                return false;
            }

            AVStream* stream = _Format->streams[_StreamIndex];
            _TimeBase = stream->time_base;

            _Codec = ffmpeg.avcodec_alloc_context3(codec);
            if (_Codec == null || ffmpeg.avcodec_parameters_to_context(_Codec, stream->codecpar) < 0)
            {
                CLog.Error("Error setting up the audio decoder: " + _FileName);
                return false;
            }

            // Let ffmpeg spread the work; on this class of machine the decode thread is the one that
            // has to keep up with playback.
            _Codec->thread_count = 0;

            if (ffmpeg.avcodec_open2(_Codec, codec, null) < 0)
            {
                CLog.Error("Error opening the audio decoder: " + _FileName);
                return false;
            }

            // Some containers only give a duration per stream.
            if (_Length <= 0 && stream->duration > 0)
                _Length = stream->duration * ffmpeg.av_q2d(_TimeBase);
            return true;
        }

        private bool _OpenResampler()
        {
            // The output side is fixed: interleaved signed 16 bit, same rate and channel count as the
            // file. Everything the codec hands us (float planar, as most modern decoders do) is
            // converted here.
            AVChannelLayout outLayout;
            if (_Codec->ch_layout.nb_channels > 0)
                outLayout = _Codec->ch_layout;
            else
                ffmpeg.av_channel_layout_default(&outLayout, 2);

            _FormatInfo = new SFormatInfo
                {
                    ChannelCount = outLayout.nb_channels,
                    SamplesPerSecond = _Codec->sample_rate,
                    BitDepth = 16
                };

            fixed (SwrContext** resampler = &_Resampler)
            {
                int res = ffmpeg.swr_alloc_set_opts2(resampler,
                                                    &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, _Codec->sample_rate,
                                                    &_Codec->ch_layout, _Codec->sample_fmt, _Codec->sample_rate,
                                                    0, null);
                if (res < 0)
                {
                    CLog.Error("Error setting up the resampler: " + _FileName + " (" + _ErrorText(res) + ")");
                    return false;
                }
            }

            if (ffmpeg.swr_init(_Resampler) < 0)
            {
                CLog.Error("Error initialising the resampler: " + _FileName);
                return false;
            }
            return true;
        }

        #endregion opening

        public void Close()
        {
            _FileOpened = false;

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
            if (_Resampler != null)
            {
                fixed (SwrContext** resampler = &_Resampler)
                    ffmpeg.swr_free(resampler);
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

        public SFormatInfo GetFormatInfo()
        {
            return _FileOpened ? _FormatInfo : new SFormatInfo();
        }

        public float GetLength()
        {
            return _FileOpened ? (float)_Length : 0f;
        }

        public float GetPosition()
        {
            return _FileOpened ? (float)_CurrentTime : 0f;
        }

        public void SetPosition(float time)
        {
            if (!_FileOpened)
                return;

            long target = (long)(time / ffmpeg.av_q2d(_TimeBase));
            int flags = time < _CurrentTime ? ffmpeg.AVSEEK_FLAG_BACKWARD : 0;

            int res = ffmpeg.av_seek_frame(_Format, _StreamIndex, target, flags);
            if (res < 0)
            {
                CLog.Error("Error seeking in file: " + _FileName + " (" + _ErrorText(res) + ")");
                return;
            }

            // Whatever the decoder still holds belongs to the old position.
            ffmpeg.avcodec_flush_buffers(_Codec);
            _CurrentTime = time;
        }

        public void Decode(out byte[] buffer, out float timeStamp)
        {
            buffer = null;
            timeStamp = 0f;
            if (!_FileOpened)
                return;

            while (true)
            {
                int res = ffmpeg.avcodec_receive_frame(_Codec, _Frame);
                if (res == 0)
                {
                    buffer = _Convert(_Frame, out timeStamp);
                    ffmpeg.av_frame_unref(_Frame);
                    if (buffer != null)
                        return;
                    continue; // empty frame, try the next one
                }
                if (res != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    return; // end of stream or a broken file: nothing more to hand out

                if (!_FeedDecoder())
                    return;
            }
        }

        /// <summary>
        ///     Pushes the next packet of our stream into the decoder. False once the file is through.
        /// </summary>
        private bool _FeedDecoder()
        {
            while (true)
            {
                int res = ffmpeg.av_read_frame(_Format, _Packet);
                if (res < 0)
                {
                    ffmpeg.avcodec_send_packet(_Codec, null); // drain what is still buffered
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
                    CLog.Error("Error decoding audio: " + _FileName + " (" + _ErrorText(res) + ")");
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        ///     Turns one decoded frame into interleaved 16 bit PCM and reads its timestamp.
        /// </summary>
        private byte[] _Convert(AVFrame* frame, out float timeStamp)
        {
            timeStamp = 0f;

            long pts = frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                pts = frame->pts;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                // int64 all the way, narrowed only here. See the remark on the class.
                _CurrentTime = pts * ffmpeg.av_q2d(_TimeBase);
            }
            else
            {
                // No timestamp on this frame: advance by what we are about to output.
                _CurrentTime += frame->nb_samples / (double)_FormatInfo.SamplesPerSecond;
            }
            timeStamp = (float)_CurrentTime;

            int outSamples = (int)ffmpeg.swr_get_out_samples(_Resampler, frame->nb_samples);
            if (outSamples <= 0)
                return null;

            int size = outSamples * _FormatInfo.ChannelCount * 2;
            var managed = new byte[size];
            fixed (byte* target = managed)
            {
                byte** targets = stackalloc byte*[1];
                targets[0] = target;

                int written = ffmpeg.swr_convert(_Resampler, targets, outSamples,
                                                 frame->extended_data, frame->nb_samples);
                if (written < 0)
                {
                    CLog.Error("Error converting audio: " + _FileName + " (" + _ErrorText(written) + ")");
                    return null;
                }
                if (written == 0)
                    return null;

                int actual = written * _FormatInfo.ChannelCount * 2;
                if (actual != size)
                    Array.Resize(ref managed, actual);
            }
            return managed;
        }

        private static string _ErrorText(int error)
        {
            const int bufferSize = 256;
            byte* buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, bufferSize);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString();
        }

        public void Dispose()
        {
            Close();
        }
    }
}
