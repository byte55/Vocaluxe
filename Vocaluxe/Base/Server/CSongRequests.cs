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
using System.Linq;
using System.Text.Json;
using VocaluxeLib.Log;

namespace Vocaluxe.Base.Server
{
    public enum ESongRequestState
    {
        Waiting,
        Playing,
        Done,
        Skipped
    }

    /// <summary>
    ///     One "I want to sing this" entry: a song plus the people who want to sing it.
    /// </summary>
    public class CSongRequest
    {
        public int RequestId { get; set; }
        public int SongId { get; set; }

        // Denormalized so the web UI can render the queue without a lookup per entry, and so a
        // restored queue still shows something sensible if the library changed in the meantime.
        public string Title { get; set; }
        public string Artist { get; set; }
        public bool IsDuet { get; set; }

        /// <summary>
        ///     Singers in playing order. <b>The index is the player number and therefore the
        ///     microphone channel</b>: index 0 becomes player 1 on MIC 1, index 1 becomes player 2
        ///     on MIC 2. See the audio section in CLAUDE.md for the mixer side of this.
        /// </summary>
        public List<Guid> SingerProfileIds { get; set; } = new List<Guid>();

        public List<string> SingerNames { get; set; } = new List<string>();

        public string State { get; set; } = ESongRequestState.Waiting.ToString();
        public string CreatedAt { get; set; }

        /// <summary>When the song was handed to the game; null while it has not been started.</summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>Profile that created the entry — may remove it again without admin rights.</summary>
        public Guid CreatedBy { get; set; }
    }

    /// <summary>
    ///     The event queue: who wants to sing what, in which order.
    ///
    ///     Deliberately <b>not</b> routed through <see cref="CVocaluxeServer.DoTask" />. That queue is
    ///     drained once per rendered frame, so anything going through it stops working as soon as the
    ///     render loop stalls (a minimized window with VSync on does exactly that — see
    ///     docs/web-queue.md). This class owns plain managed state, so a lock is enough and browsing,
    ///     signing up and viewing the queue keep working even when the game window is not drawing.
    ///     Only actually starting a song needs the main thread, and that is the one place we marshal.
    /// </summary>
    static class CSongRequests
    {
        private static readonly object _Mutex = new object();
        private static readonly List<CSongRequest> _Requests = new List<CSongRequest>();
        private static int _NextId = 1;

        /// <summary>Bumped on every change so clients can poll cheaply / SSE can push.</summary>
        private static long _Revision;

        private static string _StorageFile
        {
            get { return Path.Combine(CSettings.DataFolder, "SongRequests.json"); }
        }

        public static long Revision
        {
            get
            {
                lock (_Mutex)
                    return _Revision;
            }
        }

        #region reading

        public static CSongRequest[] GetAll()
        {
            lock (_Mutex)
                return _Requests.Select(_Copy).ToArray();
        }

        public static CSongRequest GetById(int requestId)
        {
            lock (_Mutex)
            {
                CSongRequest r = _Requests.FirstOrDefault(x => x.RequestId == requestId);
                return r == null ? null : _Copy(r);
            }
        }

        /// <summary>The entry that would be started next, or null if nobody is waiting.</summary>
        public static CSongRequest GetNextWaiting()
        {
            lock (_Mutex)
            {
                CSongRequest r = _Requests.FirstOrDefault(x => x.State == ESongRequestState.Waiting.ToString());
                return r == null ? null : _Copy(r);
            }
        }

        public static CSongRequest GetPlaying()
        {
            lock (_Mutex)
            {
                CSongRequest r = _Requests.FirstOrDefault(x => x.State == ESongRequestState.Playing.ToString());
                return r == null ? null : _Copy(r);
            }
        }

        #endregion

        #region writing

        public static CSongRequest Add(int songId, string title, string artist, bool isDuet, IList<Guid> singerIds, IList<string> singerNames, Guid createdBy)
        {
            CSongRequest request;
            lock (_Mutex)
            {
                request = new CSongRequest
                    {
                        RequestId = _NextId++,
                        SongId = songId,
                        Title = title,
                        Artist = artist,
                        IsDuet = isDuet,
                        SingerProfileIds = singerIds.ToList(),
                        SingerNames = singerNames.ToList(),
                        State = ESongRequestState.Waiting.ToString(),
                        CreatedAt = DateTime.Now.ToString("o"),
                        CreatedBy = createdBy
                    };
                _Requests.Add(request);
                _Touch();
            }
            _Save();
            return _Copy(request);
        }

        public static bool Remove(int requestId)
        {
            bool removed;
            lock (_Mutex)
            {
                removed = _Requests.RemoveAll(x => x.RequestId == requestId) > 0;
                if (removed)
                    _Touch();
            }
            if (removed)
                _Save();
            return removed;
        }

        /// <summary>Moves an entry to <paramref name="newIndex" /> among the waiting entries.</summary>
        public static bool Move(int requestId, int newIndex)
        {
            bool moved = false;
            lock (_Mutex)
            {
                int oldIndex = _Requests.FindIndex(x => x.RequestId == requestId);
                if (oldIndex >= 0)
                {
                    CSongRequest request = _Requests[oldIndex];
                    _Requests.RemoveAt(oldIndex);
                    if (newIndex < 0)
                        newIndex = 0;
                    if (newIndex > _Requests.Count)
                        newIndex = _Requests.Count;
                    _Requests.Insert(newIndex, request);
                    _Touch();
                    moved = true;
                }
            }
            if (moved)
                _Save();
            return moved;
        }

        public static bool SetState(int requestId, ESongRequestState state)
        {
            bool changed = false;
            lock (_Mutex)
            {
                CSongRequest request = _Requests.FirstOrDefault(x => x.RequestId == requestId);
                if (request != null)
                {
                    request.State = state.ToString();
                    _Touch();
                    changed = true;
                }
            }
            if (changed)
                _Save();
            return changed;
        }

        /// <summary>
        ///     Marks the entry as playing and closes whatever was playing before it. Called when a
        ///     request is actually handed to the game.
        /// </summary>
        public static void MarkPlaying(int requestId)
        {
            lock (_Mutex)
            {
                foreach (CSongRequest r in _Requests.Where(x => x.State == ESongRequestState.Playing.ToString()))
                    r.State = ESongRequestState.Done.ToString();

                CSongRequest request = _Requests.FirstOrDefault(x => x.RequestId == requestId);
                if (request != null)
                {
                    request.State = ESongRequestState.Playing.ToString();
                    request.StartedAt = DateTime.Now;
                }
                _Touch();
            }
            _Save();
        }

        /// <summary>
        ///     How long a song has to have run to count as sung. Below that it was a false start --
        ///     wrong song, nobody at the microphone -- and the entry goes back into the queue instead
        ///     of being marked off. Above it, cutting the endless outro short with Escape is a normal
        ///     way to end a song and must not cost the entry.
        /// </summary>
        public const int MinSecondsToCount = 30;

        /// <summary>
        ///     Closes the running entry: sung if it ran long enough, back in line if not.
        ///     Called when the score screen comes up and when a song is aborted.
        /// </summary>
        public static void FinishPlaying()
        {
            bool changed = false;
            lock (_Mutex)
            {
                foreach (CSongRequest r in _Requests.Where(x => x.State == ESongRequestState.Playing.ToString()))
                {
                    double seconds = r.StartedAt.HasValue
                        ? (DateTime.Now - r.StartedAt.Value).TotalSeconds
                        : double.MaxValue;

                    if (seconds >= MinSecondsToCount)
                        r.State = ESongRequestState.Done.ToString();
                    else
                    {
                        r.State = ESongRequestState.Waiting.ToString();
                        r.StartedAt = null;
                        CLog.Information("Song request " + r.RequestId + " ran only " + (int)seconds
                                         + "s and goes back into the queue");
                    }
                    changed = true;
                }
                if (changed)
                    _Touch();
            }
            if (changed)
                _Save();
        }

        /// <summary>Drops everything that is already sung or skipped.</summary>
        public static int ClearFinished()
        {
            int removed;
            lock (_Mutex)
            {
                removed = _Requests.RemoveAll(x => x.State == ESongRequestState.Done.ToString()
                                                   || x.State == ESongRequestState.Skipped.ToString());
                if (removed > 0)
                    _Touch();
            }
            if (removed > 0)
                _Save();
            return removed;
        }

        public static void ClearAll()
        {
            lock (_Mutex)
            {
                _Requests.Clear();
                _Touch();
            }
            _Save();
        }

        #endregion

        #region persistence

        /// <summary>
        ///     How many sung/skipped entries to keep. They are only interesting as recent history, and
        ///     an evening of karaoke would otherwise grow the file forever.
        /// </summary>
        private const int _MaxFinishedKept = 40;

        /// <summary>
        ///     Marks the queue as changed and drops stale history.
        ///     Must be called with <see cref="_Mutex" /> held.
        /// </summary>
        private static void _Touch()
        {
            _Revision++;

            int finished = _Requests.Count(x => x.State == ESongRequestState.Done.ToString()
                                                || x.State == ESongRequestState.Skipped.ToString());
            for (int i = 0; i < _Requests.Count && finished > _MaxFinishedKept; i++)
            {
                if (_Requests[i].State != ESongRequestState.Done.ToString()
                    && _Requests[i].State != ESongRequestState.Skipped.ToString())
                    continue;
                _Requests.RemoveAt(i--);
                finished--;
            }
        }

        private static CSongRequest _Copy(CSongRequest r)
        {
            return new CSongRequest
                {
                    RequestId = r.RequestId,
                    SongId = r.SongId,
                    Title = r.Title,
                    Artist = r.Artist,
                    IsDuet = r.IsDuet,
                    SingerProfileIds = r.SingerProfileIds.ToList(),
                    SingerNames = r.SingerNames.ToList(),
                    State = r.State,
                    CreatedAt = r.CreatedAt,
                    CreatedBy = r.CreatedBy
                };
        }

        /// <summary>
        ///     Restores the queue from disk. An event that is interrupted by a crash or a restart
        ///     should not cost everybody their place in line.
        /// </summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(_StorageFile))
                    return;

                string json = File.ReadAllText(_StorageFile);
                List<CSongRequest> loaded = JsonSerializer.Deserialize<List<CSongRequest>>(json);
                if (loaded == null)
                    return;

                lock (_Mutex)
                {
                    _Requests.Clear();
                    _Requests.AddRange(loaded);
                    _NextId = _Requests.Count > 0 ? _Requests.Max(x => x.RequestId) + 1 : 1;
                    _Touch();
                }
                CLog.Information("Song requests: restored " + loaded.Count + " entries from " + _StorageFile);
            }
            catch (Exception e)
            {
                // A broken queue file must never keep the game from starting.
                CLog.Error(e, "Could not restore the song request queue; starting empty");
            }
        }

        private static void _Save()
        {
            try
            {
                CSongRequest[] snapshot;
                lock (_Mutex)
                    snapshot = _Requests.Select(_Copy).ToArray();

                Directory.CreateDirectory(Path.GetDirectoryName(_StorageFile) ?? ".");
                File.WriteAllText(_StorageFile, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions {WriteIndented = true}));
            }
            catch (Exception e)
            {
                // Losing persistence is bad but must not break the running event.
                CLog.Error(e, "Could not save the song request queue");
            }
        }

        #endregion
    }
}
