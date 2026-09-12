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

//#define TEST_PITCH

using System;
using Vocaluxe.Base;
using Vocaluxe.Lib.Sound.Record.PitchTracker;

namespace Vocaluxe.Lib.Sound.Record
{
    class CBuffer : IDisposable
    {
        private CPitchTracker _PitchTracker = new CPtAKF();

        private float _MaxVolume;

        public CBuffer()
        {
            VolTreshold = 0.02f;
            ToneWeigths = new float[GetNumHalfTones()];
            Reset();
#if TEST_PITCH
            CPitchTrackerTest tester = new CPitchTrackerTest();
            tester.AddAnalyzer(new CAnalyzer());
            tester.AddAnalyzer(new CPtAKF());
            tester.AddAnalyzer(new CPtDyWa());
            tester.AddAnalyzer(new CPtSharp());
            tester.RunTest();
#endif
        }

        ~CBuffer()
        {
            _Dispose(false);
        }

        public int GetNumHalfTones()
        {
            return _PitchTracker.GetNumHalfTones();
        }

        public int ToneAbs { get; private set; }

        public int Tone { get; set; }

        public float MaxVolume
        {
            get { return _MaxVolume; }
        }

        public bool ToneValid { get; private set; }

        public float[] ToneWeigths { get; private set; }

        /// <summary>
        ///     Minimum volume for a tone to be valid
        /// </summary>
        public float VolTreshold
        {
            get { return _PitchTracker.VolumeTreshold; }
            set { _PitchTracker.VolumeTreshold = value; }
        }

        public void Reset()
        {
            ToneValid = false;
            ToneAbs = 0;
            Tone = 0;
            for (int i = 0; i < ToneWeigths.Length; i++)
                ToneWeigths[i] = 0f;
        }

        public void ProcessNewBuffer(byte[] buffer)
        {
            // apply software boost
            _BoostBuffer(buffer);

            // voice passthrough (send data to playback-device)
            //if (assigned(fVoiceStream)) then
            //fVoiceStream.WriteData(Buffer, BufferSize);
            _PitchTracker.Input(buffer);
        }

        /// <summary>
        ///     Amplifies the Int16 samples in place by CConfig.Config.Record.MicAmplify dB.
        ///     Needed when the mixer cannot be turned up any further without feedback:
        ///     the analog gain stays low and the level is made up here instead.
        ///     Saturates rather than wrapping around - an overdriven sample must clip,
        ///     not flip sign, or the pitch detection sees garbage.
        /// </summary>
        private static void _BoostBuffer(byte[] buffer)
        {
            int db = CConfig.Config.Record.MicAmplify;
            if (db <= 0)
                return;

            float factor = (float)Math.Pow(10.0, db / 20.0);
            for (int i = 0; i + 1 < buffer.Length; i += 2)
            {
                int sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sample = (int)(sample * factor);
                if (sample > short.MaxValue)
                    sample = short.MaxValue;
                else if (sample < short.MinValue)
                    sample = short.MinValue;
                buffer[i] = (byte)(sample & 0xFF);
                buffer[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        public void AnalyzeBuffer()
        {
            int tone = _PitchTracker.GetNote(out _MaxVolume, ToneWeigths);
            if (tone >= 0)
            {
                ToneAbs = tone;
                Tone = ToneAbs % 12;
                ToneValid = true;
            }
            else
                ToneValid = false;
        }

        public void Dispose()
        {
            _Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void _Dispose(bool disposing)
        {
            if (disposing && _PitchTracker != null)
            {
                _PitchTracker.Dispose();
                _PitchTracker = null;
            }
        }
    }
}