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
using System.Runtime.InteropServices;
using System.Threading;
using Vocaluxe.Base;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.Acinerella
{
    //This class describes a thread decoding a video
    //All public methods are meant to be called from "reader" thread only
    //Most others are to be called by this thread  (_Thread instance) only!
    class CDecoderThread
    {
        private const float _LoopedRequestTime = -0.001f; //Magic const to detect if decoder looped automaticly

        private Thread _Thread;
        private readonly Object _BufferMutex = new Object();

        // Which backend does the decoding. Everything below - the thread, the ring buffer, dropping
        // frames to catch up, looping - is the same either way.
        private readonly IVideoStreamDecoder _Stream;
        private bool _Opened;

        private readonly CFramebuffer _Framebuffer;
        private float _LastDecodedTime; // time of last decoded frame
        private float _LastShownTime = -1f; // time if the last shown frame in s IMPORTANT: Write only in context of reader
        // time of last requested frame aka current should-be position (_LastDecodedTime should be >=_RequestTime)
        //HAS to be < Length
        public float RequestTime { get; private set; }
        private bool _Paused;

        private String _FileName;
        private float _FrameDuration; // frame time in s
        private int _Width;
        private int _Height;

        private bool _RequestSkip;
        private bool _Terminated;
        private bool _FrameAvailable;
        private bool _NoMoreFrames;
        private readonly AutoResetEvent _EvWakeUp = new AutoResetEvent(false);
        private readonly AutoResetEvent _EvNoMoreFrames = new AutoResetEvent(false);
        private bool _IsSleeping;
        private int _WaitCount;
        private bool _DropSeekEnabled = true; // Used to fallback to frame skipping if seek is failing once on this file

        public float Length { get; private set; }
        public bool Loop { get; set; }

        public CDecoderThread(IVideoStreamDecoder stream)
        {
            _Stream = stream;
            _Framebuffer = new CFramebuffer(10);
            _FrameDuration = 0.02f; // Set a reasonable standard till correct value is set
        }

        //Open the file and get the length.
        public bool LoadFile(String fileName)
        {
            _FileName = fileName;
            _DropSeekEnabled = true;
            _Opened = _Stream.Open(fileName);
            if (_Opened)
                Length = _Stream.Length;
            return _Opened;
        }

        public bool Start()
        {
            if (!_Opened)
            {
                CLog.Error("Tried to start a video file that is not open: " + _FileName);
                return false;
            }
            if (_Thread != null)
            {
                CLog.Error("Tried to start a video file that is already started: " + _FileName);
                return false;
            }
            RequestTime = 0f;
            _Thread = new Thread(_Execute) {Priority = ThreadPriority.Normal, Name = Path.GetFileName(_FileName)};
            _Thread.Start();
            return true;
        }

        public void Stop()
        {
            _Terminated = true;
            _EvWakeUp.Set();
            _EvNoMoreFrames.Set();
        }

        public void Pause()
        {
            if (_Paused)
                return;
            _Paused = true;
        }

        public void Resume()
        {
            if (!_Paused)
                return;
            _Paused = false;
        }

        //Sets the position to the given time discarding all decoded frames
        public void Skip(float time)
        {
            //Problem: This is not threadsave
            //Scenario: Clear buffer from main thread, decoder is in _Decode or _Copy
            //Result: Cleared buffer contains 1 old item. This is a problem when skipping back: E.g. Loop:
            //Old item has time=Length->Will not get removed
            //Workaround: Check in _FindFrame to remove first old item
            //Fix: Use mutex (TODO: Check overhead)
            lock (_BufferMutex) //Cover clear, RequestTime=, _RequestSkip=
            {
                _Framebuffer.Clear();
                RequestTime = time;
                _LastShownTime = time - _FrameDuration; //Set this to time to detect overflow of time in FindFrame but subtract FrameDuration so GetFrame will get the first frame
                _RequestSkip = true;
            }
            _EvNoMoreFrames.Set();
        }

        private CFramebuffer.CFrame _FindFrame(float now)
        {
            CFramebuffer.CFrame result = null;
            CFramebuffer.CFrame frame;
            _Framebuffer.ResetStack();
            while ((frame = _Framebuffer.Pop()) != null)
            {
                //float frameEnd = frame.Time + _FrameDuration;
                float frameTime = frame.Time;

                if (frameTime > now)
                {
                    //2 Cases: all following frames are after this one, or we have a loop and 'now' wrapped over
                    //First case is if we have no loop or we did not wrap or frame is before last one (last is the case if frame is already one of the new iterations, e.g. Last=19 now=1 frame=2)
                    if (!Loop || _LastShownTime <= now || frameTime < _LastShownTime)
                        break; //Following frames (incl this one) are after now, so do not consider any of them
                }
                    // ReSharper disable CompareOfFloatsByEqualityOperator
                else if (Loop && RequestTime == _LoopedRequestTime)
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                {
                    //Frame time might have wrapped but now did not
                    if (frameTime < _LastShownTime && _LastShownTime <= now)
                        break; //Following frames (incl this one) are after now, so do not consider any of them
                }
                //Get the last(newest) possible frame and skip the rest
                if (result != null)
                {
                    //Frame is to old -> Discard
                    result.SetRead();
                }
                result = frame;
                if (_Paused)
                    break; //Just get 1 frame if paused otherwise a paused movie could move a bit
            }
            return result;
        }

        /// <summary>
        ///     Gets a frame
        /// </summary>
        /// <param name="frame">Referenz to texture where frame should be put in (can be null, then texture is created)</param>
        /// <param name="time">Maximum start time for frame</param>
        /// <param name="finished">Set to whether there are no more frames (stream finished, no loop, future calls will always return false)</param>
        /// <returns>True if a new frame was gotten</returns>
        public bool GetFrame(ref CTextureRef frame, ref float time, out bool finished)
        {
            bool result;
            if (Math.Abs(_LastShownTime - time) < _FrameDuration && frame != null) //Check 1 frame difference
            {
                time = _LastShownTime;
                result = false;
            }
            else
            {
                CFramebuffer.CFrame curFrame = _FindFrame(time);
                if (curFrame != null)
                {
                    if (frame == null)
                        frame = CDraw.AddTexture(_Width, _Height, curFrame.Data);
                    else
                        CDraw.UpdateTexture(frame, _Width, _Height, curFrame.Data);
                    if (!_Paused)
                        curFrame.SetRead();
                    time = curFrame.Time;
                    _LastShownTime = time;
                    result = frame != null;
                }
                else
                    result = false;

                if (_IsSleeping)
                {
                    _IsSleeping = false;
                    _EvWakeUp.Set();
                }
            }
            finished = _NoMoreFrames && _Framebuffer.IsEmpty();

            return result;
        }

        //Time should be < Length
        public void SyncTime(float time)
        {
            if (_Thread == null)
                return; //Not initialized

            if (RequestTime - time >= _FrameDuration)
            {
                //Jump back more than 1 frame. To guarantee the order in the buffer use Skip() which clears the buffer
                Skip(time);
            }
            else if (time - RequestTime >= _FrameDuration)
            {
                //we were more than 1 frame to slow -> Jump forward (This is save, as the decoder will skip frames if necessary)
                // ReSharper disable CompareOfFloatsByEqualityOperator
                if (Loop && RequestTime == _LoopedRequestTime)
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                {
                    //In a loop our decoder may have reset RequestTime to 0 but we want a frame from the end of the video
                    //Skipping forward is fatal as it resets the decoder to decode already decoded frames causing lags
                    //So first check if we have a valid frame in our buffer
                    _Framebuffer.ResetStack();
                    CFramebuffer.CFrame frame;
                    while ((frame = _Framebuffer.Pop()) != null)
                    {
                        if (frame.Time + _FrameDuration >= time)
                            return;
                    }
                    //If we don't the Length might be inaccurate (e.g. last frame ends at 19.98 but Length=20)
                    if (time >= Length - 2 * _FrameDuration)
                        return;
                }

                RequestTime = time;
            }
        }

        private bool _OpenVideoStream()
        {
            float frameDuration;
            if (!_Stream.OpenVideoStream(out _Width, out _Height, out frameDuration))
                return false;

            if (frameDuration > 0)
                _FrameDuration = frameDuration;

            _Framebuffer.Init(_Width * _Height * 4);
            _FrameAvailable = false;
            return true;
        }

        //Just call this if thread is not alive
        private void _Free()
        {
            _Stream.Free();
            _Opened = false;
        }

        // Skip to a given time (in s)
        private void _Skip()
        {
            float skipTime = RequestTime; //Copy to variable to have consistent checks
            if (skipTime < 0 || skipTime >= Length)
                skipTime = 0;

            _Stream.Seek(skipTime, false);
            _LastDecodedTime = skipTime;

            _FrameAvailable = false;
        }

        private void _Decode()
        {
            const int minFrameDropCount = 4;
            // With => seekThreshold frames to drop use seek instead of skip
            const int seekThreshold = 25; // 25 frames = 0.5 second with _FrameDuration = 0.02f

            if (_NoMoreFrames)
                return;

            float videoTime = RequestTime;
            float timeDifference = videoTime - _LastDecodedTime;

            bool dropFrame = timeDifference >= (minFrameDropCount - 1) * _FrameDuration;

            bool hasFrameDecoded = false;
            if (dropFrame)
            {
                
                    var frameDropCount = (int)Math.Ceiling(timeDifference / _FrameDuration);
                    if (!_DropSeekEnabled || frameDropCount < seekThreshold)
                    {
                        hasFrameDecoded = _DropWithSkip(frameDropCount);
                    }
                    else
                    {
                        hasFrameDecoded = _DropWithSeek(videoTime, frameDropCount);
                    }
            }

            if (!hasFrameDecoded)
                hasFrameDecoded = _Stream.GetFrame();
            if (hasFrameDecoded)
                _FrameAvailable = true;
            else
            {
                if (Loop)
                {
                    RequestTime = _LoopedRequestTime;
                    _Skip();
                }
                else
                    _NoMoreFrames = true;
            }
        }

        private bool _DropWithSeek(float videoTime, int frameDropCount)
        {
            bool hasFrameDecoded = _Stream.Seek(videoTime, true);

            if (!hasFrameDecoded)
            {
                // Fallback to frame skipping
                _DropSeekEnabled = false;
                hasFrameDecoded = _DropWithSkip(frameDropCount);
            }

            return hasFrameDecoded;
        }

        private bool _DropWithSkip(int frameDropCount)
        {
            // Add 1 dropped frame per 16 frames (Power of 2 -> Div is fast) as skipping takes time too and we don't want to skip again
            frameDropCount += frameDropCount / 16;
            return _Stream.SkipFrames(frameDropCount);
        }

        //Copies a frame to the buffer but does not set it as 'written'
        //Returns true if frame data is now in buffer
        private bool _CopyDecodedFrameToBuffer()
        {
            IntPtr data;
            float time;
            bool result;
            if (_Stream.GetDecodedFrame(out data, out time))
            {
                _LastDecodedTime = time;
                result = _Framebuffer.Put(data, _LastDecodedTime);
            }
            else
                result = false;
            _FrameAvailable = false;
            return result;
        }

        private void _Execute()
        {
            if (!_OpenVideoStream())
            {
                _Free();
                Stop();
                return;
            }

            while (!_Terminated)
            {
                if (_NoMoreFrames)
                    _EvNoMoreFrames.WaitOne();
                if (_RequestSkip)
                {
                    _RequestSkip = false;
                    _NoMoreFrames = false;
                    _Skip();
                }

                if (!_FrameAvailable)
                    _Decode();

                //Bail out if we want to skip
                if (!_RequestSkip && _FrameAvailable)
                {
                    if (!_Framebuffer.IsFull())
                    {
                        if (_CopyDecodedFrameToBuffer())
                        {
                            //Do not write to buffer if we want to skip. So check and write have to be done atomicly
                            lock (_BufferMutex)
                            {
                                if (_RequestSkip)
                                    continue; //Frame is invalid if we want to skip
                                _Framebuffer.SetWritten();
                            }
                        }
                        _WaitCount = 0;
                        Thread.Sleep(5); //Sleep for a bit to give other threads an opportunity to run
                    }
                    else if (_WaitCount > 3)
                    {
                        _IsSleeping = true;
                        _EvWakeUp.WaitOne();
                    }
                    else
                    {
                        Thread.Sleep((int)(_Framebuffer.Size * _FrameDuration * 1000 / 2));
                        _WaitCount++;
                    }
                }
            }

            _Free();
        }
    }
}