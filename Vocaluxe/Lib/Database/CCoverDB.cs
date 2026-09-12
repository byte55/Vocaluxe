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
using System.Drawing;
using System.IO;
using SkiaSharp;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using Microsoft.Data.Sqlite;

namespace Vocaluxe.Lib.Database
{
    public class CCoverDB : CDatabaseBase
    {
        private SqliteTransaction _TransactionCover;
        private SqliteCommand _LookupCommand;

        public CCoverDB(string filePath) : base(filePath) {}

        public override bool Init()
        {
            lock (_Mutex)
            {
                if (!base.Init())
                    return false;

                if (_Version < 0)
                    return _CreateCoverDB();
                if (_Version == CSettings.DatabaseCoverVersion)
                    _CreateIndices();
                if (_Version < CSettings.DatabaseCoverVersion)
                {
                    // This database is a cache and nothing else - every entry can be rebuilt from the
                    // song folders. Throwing on an older version, as this did, turns a format change
                    // into a program that will not start.
                    CLog.Information("Cover cache is from an older version, building it again");
                    return _RecreateCoverDB();
                }
            }
            return true;
        }

        public override void Close()
        {
            //Do commit and close atomicly otherwhise we may loose changes
            lock (_Mutex)
            {
                _CommitCovers();

                if (_LookupCommand != null)
                {
                    _LookupCommand.Dispose();
                    _LookupCommand = null;
                }

                base.Close();
            }
        }

        public bool GetCover(string coverPath, ref CTextureRef tex, int maxSize)
        {
            if (_Connection == null)
                return false;
            if (!File.Exists(coverPath))
            {
                CLog.Error("Can't find File: " + coverPath);
                return false;
            }

            // Only the database access belongs under the lock. Decoding the image used to happen
            // in here too, and since every cover in the library goes through this one mutex, the
            // four threads loading them spent their time queuing instead of working. The bytes
            // come out of the database locked, everything after that runs in parallel.
            int cacheId = -1;
            int cacheW = 0, cacheH = 0;
            byte[] compressed = null;

            bool missingData = false;
            lock (_Mutex)
            {
                //Double check here because we may have just closed our connection
                if (_Connection == null)
                    return false;
                // One statement instead of two, prepared once instead of parsed per cover - this
                // runs for every song in the library, under the lock, so both count.
                SqliteCommand command = _GetLookupCommand();
                command.Parameters["@path"].Value = coverPath;
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        cacheId = reader.GetInt32(0);
                        cacheW = reader.GetInt32(1);
                        cacheH = reader.GetInt32(2);
                        if (reader.IsDBNull(3))
                            missingData = true;
                        else
                            compressed = _GetBytes(reader, 3);
                    }
                }
                if (missingData)
                    _DeleteCover(cacheId);
            }

            if (compressed != null)
            {
                byte[] cached = _Decode(compressed, cacheW, cacheH);
                if (cached != null)
                {
                    tex = CDraw.EnqueueTexture(cacheW, cacheH, cached);
                    return true;
                }
                // Unreadable entry - drop it and load from the file below.
                lock (_Mutex)
                    _DeleteCover(cacheId);
            }

            // At this point we do not have a mathing entry in the CoverDB (either no Data found and deleted or nothing at all)
            // We break out of the lock to do the bitmap loading and resizing here to allow multithreaded loading
            // Cross-platform decode + resize via SkiaSharp (the former GDI+ Bitmap path is Windows-only).

            if (!File.Exists(coverPath))
            {
                CLog.Error("Can't find File: " + coverPath);
                return false;
            }

            Size size;
            byte[] data;    // raw BGRA, what the renderer wants
            byte[] stored;  // what goes into the database
            try
            {
                using (var codec = SKCodec.Create(coverPath))
                {
                    if (codec == null)
                    {
                        CLog.Error("Error loading bitmap: " + coverPath);
                        return false;
                    }
                    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    using (var origin = new SKBitmap(info))
                    {
                        SKCodecResult res = codec.GetPixels(info, origin.GetPixels());
                        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                        {
                            CLog.Error("Error loading bitmap: " + coverPath);
                            return false;
                        }

                        size = new Size(info.Width, info.Height);
                        if (size.Width > maxSize || size.Height > maxSize)
                        {
                            size = CHelper.FitInBounds(new SRectF(0, 0, maxSize, maxSize, 0), (float)size.Width / size.Height, EAspect.LetterBox).SizeI;
                            var scaledInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                            using (var scaled = origin.Resize(scaledInfo, SKSamplingOptions.Default))
                            {
                                if (scaled == null)
                                {
                                    CLog.Error("Error resizing bitmap: " + coverPath);
                                    return false;
                                }
                                data = scaled.Bytes;
                                stored = _Encode(scaled, coverPath);
                            }
                        }
                        else
                        {
                            data = origin.Bytes;
                            stored = _Encode(origin, coverPath);
                        }
                        if (stored == null)
                            return false;
                    }
                }
            }
            catch (Exception)
            {
                CLog.Error("Error loading bitmap: " + coverPath);
                return false;
            }

            tex = CDraw.EnqueueTexture(size.Width, size.Height, data);

            lock (_Mutex)
            {
                //Double check here because we may have just closed our connection
                if (_Connection == null)
                    return false;
                if (_TransactionCover == null)
                    _TransactionCover = _Connection.BeginTransaction();
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.Transaction = _TransactionCover;
                    command.CommandText = "INSERT INTO Cover (Path, width, height) VALUES (@path, @w, @h)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@w", size.Width);
                    command.Parameters.AddWithValue("@h", size.Height);
                    command.Parameters.AddWithValue("@path", coverPath);
                    command.ExecuteNonQuery();

                    command.CommandText = "SELECT id FROM Cover WHERE [Path] = @path";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@path", coverPath);
                    SqliteDataReader reader = command.ExecuteReader();

                    if (reader != null)
                    {
                        reader.Read();
                        int id = reader.GetInt32(0);
                        reader.Dispose();
                        command.CommandText = "INSERT INTO CoverData (CoverID, Data) VALUES (@id, @data)";
                        command.Parameters.Clear();
                        command.Parameters.AddWithValue("@id", id);
                        command.Parameters.AddWithValue("@data", stored);
                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        ///     There were none. Looking a cover up by its path scanned the whole Cover table, and
        ///     fetching its data scanned the whole of CoverData - which is the 39 MB of image data
        ///     itself, once per song in the library. Cheap to add, and existing databases get them
        ///     here rather than being thrown away.
        /// </summary>
        private void _CreateIndices()
        {
            try
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.CommandText =
                        "CREATE INDEX IF NOT EXISTS IX_Cover_Path ON Cover (Path);" +
                        "CREATE INDEX IF NOT EXISTS IX_CoverData_CoverID ON CoverData (CoverID);";
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not create the cover database indices");
            }
        }

        /// <summary>
        ///     The lookup used for every cover, prepared once. Recreated if the connection changed.
        ///     You have to hold the mutex when calling this.
        /// </summary>
        private SqliteCommand _GetLookupCommand()
        {
            if (_LookupCommand != null && _LookupCommand.Connection == _Connection
                && _LookupCommand.Transaction == _TransactionCover)
                return _LookupCommand;

            if (_LookupCommand != null)
                _LookupCommand.Dispose();

            _LookupCommand = new SqliteCommand(
                "SELECT c.id, c.width, c.height, d.Data FROM Cover c " +
                "LEFT JOIN CoverData d ON d.CoverID = c.id WHERE c.[Path] = @path", _Connection);
            // Commands on a connection with an open transaction must carry it.
            if (_TransactionCover != null)
                _LookupCommand.Transaction = _TransactionCover;
            _LookupCommand.Parameters.Add("@path", SqliteType.Text);
            _LookupCommand.Prepare();
            return _LookupCommand;
        }

        /// <summary>You have to hold the mutex when calling this.</summary>
        private void _DeleteCover(int id)
        {
            if (_Connection == null || id < 0)
                return;
            using (var command = new SqliteCommand())
            {
                command.Connection = _Connection;
                if (_TransactionCover != null)
                    command.Transaction = _TransactionCover;
                command.CommandText = "DELETE FROM Cover WHERE id = @id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        public void CommitCovers()
        {
            lock (_Mutex)
            {
                _CommitCovers();
            }
        }

        /// <summary>
        ///     You have to hold the CoverMutex when calling this!
        /// </summary>
        private void _CommitCovers()
        {
            if (_TransactionCover == null)
                return;
            _TransactionCover.Commit();
            _TransactionCover.Dispose();
            _TransactionCover = null;
        }

        /// <summary>
        ///     Turns a cover into what is kept in the database.
        /// </summary>
        /// <remarks>
        ///     WebP rather than the raw pixels this used to store. Raw is 4 bytes per pixel, so at the
        ///     default cover size of 512 every song cost a megabyte: a library of 2800 songs made a
        ///     2.2 GB cache file, and every single cover was a megabyte of disk traffic on a machine
        ///     whose disk is the slowest thing it has. Encoded, a cover is tens of kilobytes.
        /// </remarks>
        private static byte[] _Encode(SKBitmap bitmap, string coverPath)
        {
            try
            {
                using (SKData encoded = bitmap.Encode(SKEncodedImageFormat.Webp, 85))
                {
                    if (encoded != null)
                        return encoded.ToArray();
                }
                CLog.Error("Error encoding cover: " + coverPath);
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error encoding cover: " + coverPath);
            }
            return null;
        }

        /// <summary>
        ///     Back to the tightly packed BGRA the renderer uploads. Null if the entry cannot be read,
        ///     which the caller treats as a miss rather than an error.
        /// </summary>
        private static byte[] _Decode(byte[] stored, int width, int height)
        {
            if (stored == null || stored.Length == 0)
                return null;
            try
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using (var bitmap = new SKBitmap(info))
                {
                    using (SKData data = SKData.CreateCopy(stored))
                    using (var codec = SKCodec.Create(data))
                    {
                        if (codec == null)
                            return null;
                        SKCodecResult res = codec.GetPixels(info, bitmap.GetPixels());
                        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                            return null;
                    }
                    return bitmap.Bytes;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Throws the cache away and starts over, e.g. after a format change.</summary>
        private bool _RecreateCoverDB()
        {
            try
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.CommandText = "DROP TABLE IF EXISTS CoverData; DROP TABLE IF EXISTS Cover; DROP TABLE IF EXISTS Version;";
                    command.ExecuteNonQuery();
                    // Dropping tables does not shrink the file - without this the 2.2 GB the old
                    // format grew to would just sit there as free pages.
                    command.CommandText = "VACUUM;";
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error clearing the cover cache " + e);
                return false;
            }
            return _CreateCoverDB();
        }

        private bool _CreateCoverDB()
        {
            try
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = _Connection;
                    command.CommandText = "CREATE TABLE IF NOT EXISTS Version (Value INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "INSERT INTO Version (Value) VALUES(@Value)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("@Value", CSettings.DatabaseCoverVersion);
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS Cover ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "Path TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL);";
                    command.ExecuteNonQuery();

                    command.CommandText = "CREATE TABLE IF NOT EXISTS CoverData ( id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                                          "CoverID INTEGER NOT NULL, Data BLOB NOT NULL);";
                    command.ExecuteNonQuery();
                }
                _CreateIndices();
            }
            catch (Exception e)
            {
                CLog.Error("Error creating Cover DB " + e);
                return false;
            }
            return true;
        }
    }
}