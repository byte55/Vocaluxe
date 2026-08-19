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

namespace Vocaluxe.Base.Server
{
    public struct SSongInfo
    {
        public string Title;
        public string Artist;
        public string Genre { get; set; }
        public string Language { get; set; }
        public string Year { get; set; }
        public bool IsDuet { get; set; }
        public int SongId { get; set; }
    }

    /// <summary>Outcome of a sign-in attempt.</summary>
    public class CSignInResult
    {
        public Guid SessionId;
        public bool WrongPin;

        /// <summary>Seconds to wait before the next try; 0 when there is no wait.</summary>
        public int RetryAfterSeconds;
    }

    /// <summary>Outcome of setting or clearing a profile PIN.</summary>
    public enum EPinResult
    {
        Ok,
        UnknownProfile,
        WrongCurrentPin,
        InvalidPin,

        /// <summary>Admin profiles must keep a PIN — clearing it would quietly disarm their rights.</summary>
        AdminNeedsPin,

        /// <summary>An admin profile without a PIN cannot give itself one; see CVocaluxeServer.</summary>
        AdminWithoutPinLocked
    }

    /// <summary>Outcome of handing a queue entry to the game.</summary>
    public enum EStartRequestResult
    {
        Started,
        UnknownRequest,
        SongUnavailable,

        /// <summary>A song is already running — starting on top of it would crash the game.</summary>
        Busy
    }

    #region web queue API (System.Text.Json, no DataContract needed)

    /// <summary>One entry of the paged song list used by the new web UI.</summary>
    public class SSongListEntry
    {
        public int SongId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public bool IsDuet { get; set; }
        public string Year { get; set; }
        public string Genre { get; set; }
        public string Language { get; set; }
    }

    public class SSongSearchResult
    {
        public int Total { get; set; }
        public SSongListEntry[] Items { get; set; }
    }

    /// <summary>Profile as shown in the "tap your name" list.</summary>
    public class SProfileListEntry
    {
        public string ProfileId { get; set; }
        public string PlayerName { get; set; }
        public bool IsGuest { get; set; }
        /// <summary>The profile is claimed: signing in needs its PIN.</summary>
        public bool HasPin { get; set; }

        /// <summary>0 = easy, 1 = normal, 2 = hard (<see cref="VocaluxeLib.EGameDifficulty" />).</summary>
        public int Difficulty { get; set; }

        /// <summary>-1 when the profile has no avatar.</summary>
        public int AvatarId { get; set; }
    }

    /// <summary>One of the avatars shipped with Vocaluxe.</summary>
    public class SAvatarListEntry
    {
        public int AvatarId { get; set; }
        public string Name { get; set; }
    }

    #endregion
}