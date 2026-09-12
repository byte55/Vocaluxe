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
using Vocaluxe.Lib.Database;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using VocaluxeLib.Songs;

namespace Vocaluxe.Base
{
    static class CDataBase
    {
        private static CHighscoreDB _HighscoreDB;
        private static CCoverDB _CoverDB;
        private static CSongInfoDB _SongInfoDB;

        public static bool Init()
        {
            _HighscoreDB = new CHighscoreDB(CConfig.FileHighscoreDB);
            _CoverDB = new CCoverDB(Path.Combine(CSettings.DataFolder, CSettings.FileNameCoverDB));

            if (!_HighscoreDB.Init())
            {
                CLog.Fatal("Error initializing Highscore-DB");
                return false;
            }
            if (!_CoverDB.Init())
            {
                CLog.Fatal("Error initializing Cover-DB");
                return false;
            }

            // A miss here is not fatal - without it the songs are simply read in full, as before.
            _SongInfoDB = new CSongInfoDB(Path.Combine(CSettings.DataFolder, CSettings.FileNameSongInfoDB));
            if (!_SongInfoDB.Init())
            {
                CLog.Error("Error initializing SongInfo-DB, songs will be read in full");
                _SongInfoDB = null;
            }
            return true;
        }

        public static void Close()
        {
            if (_HighscoreDB != null)
            {
                _HighscoreDB.Close();
                _HighscoreDB = null;
            }
            if (_CoverDB != null)
            {
                _CoverDB.Close();
                _CoverDB = null;
            }
            if (_SongInfoDB != null)
            {
                _SongInfoDB.Close();
                _SongInfoDB = null;
            }
        }

        #region song info cache
        public static void PreloadSongInfos()
        {
            if (_SongInfoDB != null)
                _SongInfoDB.Preload();
        }

        public static void DropSongInfoPreload()
        {
            if (_SongInfoDB != null)
                _SongInfoDB.DropPreload();
        }

        public static bool GetSongInfo(string path, long mTime, long size, out SSongInfo info)
        {
            if (_SongInfoDB != null)
                return _SongInfoDB.TryGet(path, mTime, size, out info);
            info = default(SSongInfo);
            return false;
        }

        public static void StoreSongInfos(IEnumerable<KeyValuePair<string, SSongInfo>> entries)
        {
            if (_SongInfoDB != null)
                _SongInfoDB.Store(entries);
        }

        public static void RemoveMissingSongInfos(ICollection<string> presentPaths)
        {
            if (_SongInfoDB != null)
                _SongInfoDB.RemoveMissing(presentPaths);
        }
        #endregion song info cache

        public static bool GetDataBaseSongInfos(string artist, string title, out int numPlayed, out DateTime dateAdded, out int highscoreID)
        {
            if (_HighscoreDB == null)
            {
                numPlayed = 0;
                dateAdded = new DateTime();
                highscoreID = 0;
                return false;
            }
            return _HighscoreDB.GetDataBaseSongInfos(artist, title, out numPlayed, out dateAdded, out highscoreID);
        }

        public static List<SDBScoreEntry> LoadScore(int songID, EGameMode gameMode, EHighscoreStyle style)
        {
            return _HighscoreDB == null ? null : _HighscoreDB.LoadScore(songID, gameMode, style);
        }

        public static int AddScore(SPlayer player)
        {
            return _HighscoreDB == null ? -1 : _HighscoreDB.AddScore(player);
        }

        public static void IncreaseSongCounter(int dataBaseSongID)
        {
            if (_HighscoreDB != null)
                _HighscoreDB.IncreaseSongCounter(dataBaseSongID);
        }

        public static bool GetCover(string fileName, ref CTextureRef tex, int maxSize)
        {
            return _CoverDB != null && _CoverDB.GetCover(fileName, ref tex, maxSize);
        }

        public static void CommitCovers()
        {
            if (_CoverDB != null)
                _CoverDB.CommitCovers();
        }
    }
}
