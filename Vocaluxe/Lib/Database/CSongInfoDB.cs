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
using Microsoft.Data.Sqlite;
using Vocaluxe.Base;
using VocaluxeLib.Log;
using VocaluxeLib.Songs;

namespace Vocaluxe.Lib.Database
{
    /// <summary>
    ///     Remembers what reading the notes of a song told us, so it does not have to be read again.
    /// </summary>
    /// <remarks>
    ///     Parsing the notes is by far the most expensive part of loading the library - measured over
    ///     2809 songs it was 2.1 of the 2.7 seconds, and it builds well over two million note objects
    ///     that the song list itself never looks at. Everything the song list does need out of the
    ///     notes is a handful of numbers, and those are what this keeps.
    ///
    ///     A song is a cache hit when its text file has the same modification time and size as when
    ///     the entry was written. That is not a proof of equal content, but for the way song files
    ///     actually change - edited, replaced, copied in - it is one, and the alternative is hashing
    ///     twenty megabytes on every start.
    ///
    ///     Reads happen from many threads at once, so the whole table is pulled into memory in one
    ///     query before loading starts, and new entries are collected and written in one transaction
    ///     afterwards. No locking on the hot path.
    /// </remarks>
    public class CSongInfoDB : CDatabaseBase
    {
        private Dictionary<string, SSongInfo> _Cache = new Dictionary<string, SSongInfo>(StringComparer.Ordinal);

        public CSongInfoDB(string filePath) : base(filePath) {}

        public override bool Init()
        {
            lock (_Mutex)
            {
                if (!base.Init())
                    return false;

                if (_Version < 0)
                    return _CreateDB();
                if (_Version < CSettings.DatabaseSongInfoVersion)
                {
                    // Pure cache - every entry can be rebuilt from the song files.
                    CLog.Information("Song info cache is from an older version, building it again");
                    return _RecreateDB();
                }
            }
            return true;
        }

        /// <summary>Pulls the whole table into memory. Call once, before loading songs.</summary>
        public void Preload()
        {
            var cache = new Dictionary<string, SSongInfo>(StringComparer.Ordinal);
            lock (_Mutex)
            {
                if (_Connection == null)
                {
                    _Cache = cache;
                    return;
                }
                try
                {
                    using (var command = new SqliteCommand(
                        "SELECT Path, MTime, Size, VoiceCount, VoiceNames, IsRap, " +
                        "MedleySource, MedleyStart, MedleyEnd, MedleyFadeIn, MedleyFadeOut, " +
                        "PreviewSource, PreviewStart, ShortEndSource, ShortEndBeat FROM SongInfo", _Connection))
                    {
                        using (SqliteDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var info = new SSongInfo
                                    {
                                        MTime = reader.GetInt64(1),
                                        Size = reader.GetInt64(2),
                                        VoiceCount = reader.GetInt32(3),
                                        VoiceNames = reader.IsDBNull(4) ? new string[0] : reader.GetString(4).Split('\n'),
                                        IsRap = reader.GetInt32(5) != 0,
                                        MedleySource = reader.GetInt32(6),
                                        MedleyStart = reader.GetInt32(7),
                                        MedleyEnd = reader.GetInt32(8),
                                        MedleyFadeIn = (float)reader.GetDouble(9),
                                        MedleyFadeOut = (float)reader.GetDouble(10),
                                        PreviewSource = reader.GetInt32(11),
                                        PreviewStart = (float)reader.GetDouble(12),
                                        ShortEndSource = reader.GetInt32(13),
                                        ShortEndBeat = reader.GetInt32(14)
                                    };
                                cache[reader.GetString(0)] = info;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Could not read the song info cache, starting from scratch");
                    cache.Clear();
                }
            }
            _Cache = cache;
        }

        /// <summary>Frees the preloaded table. The entries are of no use once the songs are loaded.</summary>
        public void DropPreload()
        {
            _Cache = new Dictionary<string, SSongInfo>(StringComparer.Ordinal);
        }

        /// <summary>Thread safe once Preload has run - the dictionary is only read.</summary>
        public bool TryGet(string path, long mTime, long size, out SSongInfo info)
        {
            if (_Cache.TryGetValue(path, out info) && info.MTime == mTime && info.Size == size)
                return true;
            info = default(SSongInfo);
            return false;
        }

        /// <summary>Writes the given entries, replacing whatever was stored for those paths.</summary>
        public void Store(IEnumerable<KeyValuePair<string, SSongInfo>> entries)
        {
            lock (_Mutex)
            {
                if (_Connection == null)
                    return;
                SqliteTransaction transaction = null;
                try
                {
                    transaction = _Connection.BeginTransaction();
                    using (var command = new SqliteCommand())
                    {
                        command.Connection = _Connection;
                        command.Transaction = transaction;
                        command.CommandText =
                            "INSERT OR REPLACE INTO SongInfo (Path, MTime, Size, VoiceCount, VoiceNames, IsRap, " +
                            "MedleySource, MedleyStart, MedleyEnd, MedleyFadeIn, MedleyFadeOut, " +
                            "PreviewSource, PreviewStart, ShortEndSource, ShortEndBeat) VALUES " +
                            "(@path, @mtime, @size, @vc, @vn, @rap, @ms, @mst, @men, @mfi, @mfo, @ps, @pst, @ss, @seb)";
                        foreach (KeyValuePair<string, SSongInfo> entry in entries)
                        {
                            SSongInfo i = entry.Value;
                            command.Parameters.Clear();
                            command.Parameters.AddWithValue("@path", entry.Key);
                            command.Parameters.AddWithValue("@mtime", i.MTime);
                            command.Parameters.AddWithValue("@size", i.Size);
                            command.Parameters.AddWithValue("@vc", i.VoiceCount);
                            command.Parameters.AddWithValue("@vn", String.Join("\n", i.VoiceNames ?? new string[0]));
                            command.Parameters.AddWithValue("@rap", i.IsRap ? 1 : 0);
                            command.Parameters.AddWithValue("@ms", i.MedleySource);
                            command.Parameters.AddWithValue("@mst", i.MedleyStart);
                            command.Parameters.AddWithValue("@men", i.MedleyEnd);
                            command.Parameters.AddWithValue("@mfi", i.MedleyFadeIn);
                            command.Parameters.AddWithValue("@mfo", i.MedleyFadeOut);
                            command.Parameters.AddWithValue("@ps", i.PreviewSource);
                            command.Parameters.AddWithValue("@pst", i.PreviewStart);
                            command.Parameters.AddWithValue("@ss", i.ShortEndSource);
                            command.Parameters.AddWithValue("@seb", i.ShortEndBeat);
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Could not write the song info cache");
                    if (transaction != null)
                    {
                        try
                        {
                            transaction.Rollback();
                        }
                        catch (Exception) {}
                    }
                }
                finally
                {
                    if (transaction != null)
                        transaction.Dispose();
                }
            }
        }

        /// <summary>Drops entries for songs that are no longer in the library.</summary>
        public void RemoveMissing(ICollection<string> presentPaths)
        {
            lock (_Mutex)
            {
                if (_Connection == null)
                    return;
                var gone = new List<string>();
                foreach (string path in _Cache.Keys)
                {
                    if (!presentPaths.Contains(path))
                        gone.Add(path);
                }
                if (gone.Count == 0)
                    return;
                SqliteTransaction transaction = null;
                try
                {
                    transaction = _Connection.BeginTransaction();
                    using (var command = new SqliteCommand())
                    {
                        command.Connection = _Connection;
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM SongInfo WHERE Path = @path";
                        foreach (string path in gone)
                        {
                            command.Parameters.Clear();
                            command.Parameters.AddWithValue("@path", path);
                            command.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Could not clean the song info cache");
                    if (transaction != null)
                    {
                        try
                        {
                            transaction.Rollback();
                        }
                        catch (Exception) {}
                    }
                }
                finally
                {
                    if (transaction != null)
                        transaction.Dispose();
                }
            }
        }

        private bool _CreateDB()
        {
            try
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS Version (Value INTEGER NOT NULL);" +
                        "CREATE TABLE IF NOT EXISTS SongInfo (" +
                        "Path TEXT NOT NULL PRIMARY KEY, MTime INTEGER NOT NULL, Size INTEGER NOT NULL, " +
                        "VoiceCount INTEGER NOT NULL, VoiceNames TEXT, IsRap INTEGER NOT NULL, " +
                        "MedleySource INTEGER NOT NULL, MedleyStart INTEGER NOT NULL, MedleyEnd INTEGER NOT NULL, " +
                        "MedleyFadeIn REAL NOT NULL, MedleyFadeOut REAL NOT NULL, " +
                        "PreviewSource INTEGER NOT NULL, PreviewStart REAL NOT NULL, " +
                        "ShortEndSource INTEGER NOT NULL, ShortEndBeat INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "DELETE FROM Version; INSERT INTO Version (Value) VALUES (@value)";
                    command.Parameters.AddWithValue("@value", CSettings.DatabaseSongInfoVersion);
                    command.ExecuteNonQuery();
                }
                _Version = CSettings.DatabaseSongInfoVersion;
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not create the song info cache");
                return false;
            }
            return true;
        }

        private bool _RecreateDB()
        {
            try
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.CommandText = "DROP TABLE IF EXISTS SongInfo; DROP TABLE IF EXISTS Version;";
                    command.ExecuteNonQuery();
                    command.CommandText = "VACUUM;";
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not clear the song info cache");
                return false;
            }
            return _CreateDB();
        }
    }
}
