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

namespace VocaluxeLib.Songs
{
    /// <summary>
    ///     Everything reading the notes of a song tells us that is not a note.
    /// </summary>
    /// <remarks>
    ///     This is the complete list: CSongLoader.ReadNotes sets IsRap and the voices, and the three
    ///     calculations it runs afterwards fill in medley, preview and short end. Anything added
    ///     there has to be added here too, and the cache version in CSettings bumped with it -
    ///     otherwise songs loaded from the cache quietly differ from songs read from disk.
    /// </remarks>
    public struct SSongInfo
    {
        public long MTime;
        public long Size;

        public int VoiceCount;
        public string[] VoiceNames;
        public bool IsRap;

        public int MedleySource;
        public int MedleyStart;
        public int MedleyEnd;
        public float MedleyFadeIn;
        public float MedleyFadeOut;

        public int PreviewSource;
        public float PreviewStart;

        public int ShortEndSource;
        public int ShortEndBeat;
    }
}
