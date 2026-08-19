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
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Profile;

namespace Vocaluxe.Base.Server
{
    /// <summary>
    ///     The event API used by the new web frontend: pick a profile, find a song, get in line.
    ///
    ///     Kept separate from <see cref="CWebservice" /> on purpose. That one mirrors the old WCF
    ///     contract 1:1 (everything is GET, DataContractJsonSerializer, quirky shapes) and the old
    ///     website depends on it. This one uses proper verbs and System.Text.Json, and — importantly
    ///     — only touches the main thread where it truly has to, so browsing and signing up survive a
    ///     stalled render loop.
    /// </summary>
    static class CWebQueueApi
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

        public static void MapEndpoints(WebApplication app)
        {
            _MapProfiles(app);
            _MapSongs(app);
            _MapQueue(app);
            _MapControl(app);
            _MapEvents(app);
        }

        #region profiles and sessions

        private static void _MapProfiles(WebApplication app)
        {
            // Deliberately open: the list of names is what you tap to identify yourself. It exposes
            // display names on a LAN party network, nothing more.
            app.MapGet("/api/profiles", () =>
            {
                SProfileData[] profiles = CVocaluxeServer.DoTask(CVocaluxeServer.GetProfileList);
                SProfileListEntry[] result = profiles.Select(p => new SProfileListEntry
                    {
                        ProfileId = p.ProfileId.ToString(),
                        PlayerName = p.PlayerName,
                        IsGuest = p.Type == 0,
                        NeedsPassword = !string.IsNullOrEmpty(p.Password),
                        Difficulty = p.Difficulty
                    }).ToArray();
                return _Json(result);
            });

            app.MapPost("/api/profiles", async (HttpContext ctx) =>
            {
                CCreateProfileBody body = await _ReadBody<CCreateProfileBody>(ctx);
                if (body == null || string.IsNullOrWhiteSpace(body.Name))
                    return _Error(400, "A name is required");

                Guid id = CVocaluxeServer.DoTask(CVocaluxeServer.CreateGuestProfile, body.Name);
                if (id == Guid.Empty)
                    return _Error(500, "Could not create the profile");

                Guid session = CSessionControl.OpenSessionForProfile(id);
                return _Json(new {profileId = id.ToString(), playerName = body.Name.Trim(), sessionId = session.ToString()});
            });

            // Tap-to-identify. Only works for profiles without a password; one with a password still
            // has to go through the old /login endpoint.
            app.MapPost("/api/session", async (HttpContext ctx) =>
            {
                CSessionBody body = await _ReadBody<CSessionBody>(ctx);
                Guid profileId;
                if (body == null || !Guid.TryParse(body.ProfileId, out profileId))
                    return _Error(400, "profileId is required");

                Guid session = CSessionControl.OpenSessionForProfile(profileId);
                if (session == Guid.Empty)
                    return _Error(403, "This profile is password protected");

                return _Json(new {sessionId = session.ToString(), profileId = profileId.ToString()});
            });

            app.MapGet("/api/session", (HttpContext ctx) =>
            {
                Guid session = _GetSession(ctx);
                Guid profileId = session == Guid.Empty ? Guid.Empty : CSessionControl.GetUserIdFromSession(session);
                if (profileId == Guid.Empty)
                    return _Error(401, "No session");

                bool isAdmin = CSessionControl.RequestRight(session, EUserRights.EditAllProfiles);
                SProfileListEntry me = CVocaluxeServer.DoTask(CVocaluxeServer.GetProfileSummary, profileId);
                return _Json(new
                    {
                        profileId = profileId.ToString(),
                        isAdmin,
                        playerName = me == null ? "" : me.PlayerName,
                        difficulty = me == null ? 1 : me.Difficulty
                    });
            });

            // Difficulty is a property of the profile, not of a single request, so it lives here
            // rather than on the queue entry. Only ever applies to your own profile.
            app.MapPost("/api/me/difficulty", async (HttpContext ctx) =>
            {
                Guid profileId = CSessionControl.GetUserIdFromSession(_GetSession(ctx));
                if (profileId == Guid.Empty)
                    return _Error(401, "Pick a profile first");

                CDifficultyBody body = await _ReadBody<CDifficultyBody>(ctx);
                if (body == null || body.Difficulty < 0 || body.Difficulty > 2)
                    return _Error(400, "difficulty must be 0, 1 or 2");

                bool ok = CVocaluxeServer.DoTask(CVocaluxeServer.SetProfileDifficulty, profileId, body.Difficulty);
                return ok ? _Json(new {difficulty = body.Difficulty}) : _Error(500, "Could not save the difficulty");
            });
        }

        #endregion

        #region songs

        private static void _MapSongs(WebApplication app)
        {
            app.MapGet("/api/songs", (string q, int? offset, int? limit) =>
            {
                SSongSearchResult result = CVocaluxeServer.DoTask(CVocaluxeServer.SearchSongs, q ?? "", offset ?? 0, limit ?? 50);
                return _Json(result);
            });
        }

        #endregion

        #region queue

        private static void _MapQueue(WebApplication app)
        {
            // No DoTask: the queue is our own state behind a lock, so this answers even while the
            // game window is not rendering.
            app.MapGet("/api/queue", () => _Json(new
                {
                    revision = CSongRequests.Revision,
                    entries = CSongRequests.GetAll()
                }));

            app.MapPost("/api/queue", async (HttpContext ctx) =>
            {
                Guid session = _GetSession(ctx);
                Guid ownProfile = CSessionControl.GetUserIdFromSession(session);
                if (ownProfile == Guid.Empty)
                    return _Error(401, "Pick a profile first");

                CQueueBody body = await _ReadBody<CQueueBody>(ctx);
                if (body == null || body.SongId < 0)
                    return _Error(400, "songId is required");

                // The requester is always singer 1 unless they explicitly ordered it otherwise.
                var singerIds = new List<Guid>();
                if (body.Singers != null && body.Singers.Count > 0)
                {
                    foreach (string raw in body.Singers)
                    {
                        Guid parsed;
                        if (Guid.TryParse(raw, out parsed) && !singerIds.Contains(parsed))
                            singerIds.Add(parsed);
                    }
                }
                if (singerIds.Count == 0)
                    singerIds.Add(ownProfile);

                if (singerIds.Count > 2)
                    return _Error(400, "At most two singers are supported (one per microphone)");

                SSongInfo song = CVocaluxeServer.DoTask(CVocaluxeServer.GetSongInfoForRequest, body.SongId);
                if (string.IsNullOrEmpty(song.Title))
                    return _Error(404, "Unknown song");

                List<string> names = singerIds.Select(id =>
                    {
                        CProfile p = CProfiles.GetProfile(id);
                        return p == null ? "?" : p.PlayerName;
                    }).ToList();

                CSongRequest created = CSongRequests.Add(body.SongId, song.Title, song.Artist, song.IsDuet, singerIds, names, ownProfile);
                CLog.Information("Song request " + created.RequestId + ": " + song.Artist + " - " + song.Title + " (" + string.Join(", ", names) + ")");
                return _Json(created);
            });

            app.MapDelete("/api/queue/{id:int}", (HttpContext ctx, int id) =>
            {
                Guid session = _GetSession(ctx);
                Guid ownProfile = CSessionControl.GetUserIdFromSession(session);
                if (ownProfile == Guid.Empty)
                    return _Error(401, "No session");

                CSongRequest request = CSongRequests.GetById(id);
                if (request == null)
                    return _Error(404, "Unknown entry");

                // You may always withdraw your own entry; removing someone else's needs admin rights.
                bool ownEntry = request.CreatedBy == ownProfile || request.SingerProfileIds.Contains(ownProfile);
                if (!ownEntry && !CSessionControl.RequestRight(session, EUserRights.RemoveSongsFromPlaylists))
                    return _Error(403, "Not allowed to remove someone else's entry");

                return CSongRequests.Remove(id) ? _Json(new {removed = true}) : _Error(404, "Unknown entry");
            });

            app.MapPost("/api/queue/{id:int}/position", async (HttpContext ctx, int id) =>
            {
                if (!_HasRight(ctx, EUserRights.ReorderPlaylists))
                    return _Error(403, "Not allowed to reorder the queue");

                CPositionBody body = await _ReadBody<CPositionBody>(ctx);
                if (body == null)
                    return _Error(400, "position is required");

                return CSongRequests.Move(id, body.Position) ? _Json(new {moved = true}) : _Error(404, "Unknown entry");
            });

            app.MapPost("/api/queue/{id:int}/skip", (HttpContext ctx, int id) =>
            {
                if (!_HasRight(ctx, EUserRights.ReorderPlaylists))
                    return _Error(403, "Not allowed");
                return CSongRequests.SetState(id, ESongRequestState.Skipped) ? _Json(new {skipped = true}) : _Error(404, "Unknown entry");
            });

            app.MapPost("/api/queue/clear-finished", (HttpContext ctx) =>
            {
                if (!_HasRight(ctx, EUserRights.ReorderPlaylists))
                    return _Error(403, "Not allowed");
                return _Json(new {removed = CSongRequests.ClearFinished()});
            });
        }

        #endregion

        #region control

        private static void _MapControl(WebApplication app)
        {
            app.MapGet("/api/status", (HttpContext ctx) =>
            {
                Guid session = _GetSession(ctx);
                CSongRequest playing = CSongRequests.GetPlaying();
                CSongRequest next = CSongRequests.GetNextWaiting();

                // Deliberately no DoTask here: /api/status is what the UI polls, and it must keep
                // answering even when the game side is busy or stalled.
                return _Json(new
                    {
                        revision = CSongRequests.Revision,
                        signedIn = session != Guid.Empty && CSessionControl.GetUserIdFromSession(session) != Guid.Empty,
                        isAdmin = session != Guid.Empty && CSessionControl.RequestRight(session, EUserRights.EditAllProfiles),
                        playing,
                        next,
                        waiting = CSongRequests.GetAll().Count(e => e.State == ESongRequestState.Waiting.ToString())
                    });
            });

            // The half-automatic start: the next act walks up to the microphone and confirms.
            //
            // Any signed-in guest may do this, on purpose. Starting is not an administrative act
            // here, it *is* the confirmation step the whole flow is built around — the singer is
            // standing at the mic and taps "start". Requiring a right would mean the host has to
            // hand out roles before anyone can sing, and Vocaluxe gives guests EUserRights.None by
            // default, so nothing would ever start. Rearranging or removing other people's entries
            // stays restricted.
            app.MapPost("/api/queue/start-next", (HttpContext ctx) =>
            {
                if (_GetSession(ctx) == Guid.Empty)
                    return _Error(401, "Pick a profile first");

                CSongRequest next = CSongRequests.GetNextWaiting();
                if (next == null)
                    return _Error(409, "Nobody is waiting");

                return _StartRequest(next.RequestId);
            });

            app.MapPost("/api/queue/{id:int}/start", (HttpContext ctx, int id) =>
            {
                if (_GetSession(ctx) == Guid.Empty)
                    return _Error(401, "Pick a profile first");
                return _StartRequest(id);
            });
        }

        private static IResult _StartRequest(int requestId)
        {
            try
            {
                EStartRequestResult result = CVocaluxeServer.DoTask(CVocaluxeServer.StartSongRequest, requestId);
                switch (result)
                {
                    case EStartRequestResult.Started:
                        return _Json(new {started = true, requestId});
                    case EStartRequestResult.Busy:
                        return _Error(409, "Es läuft gerade ein Song – warte, bis er zu Ende ist.");
                    case EStartRequestResult.UnknownRequest:
                        return _Error(404, "Diesen Eintrag gibt es nicht mehr.");
                    default:
                        return _Error(409, "Dieser Song lässt sich gerade nicht starten.");
                }
            }
            catch (TimeoutException)
            {
                // The render loop is not picking up work — starting a song is exactly the operation
                // that cannot be faked, so say so plainly instead of pretending it worked.
                return _Error(503, "Das Spiel antwortet nicht. Läuft Vocaluxe noch?");
            }
        }

        #endregion

        #region events (SSE)

        private static void _MapEvents(WebApplication app)
        {
            // Server-sent events so several phones stay in sync without hammering the server.
            // Long-lived and cheap: it only ever sends when the revision actually changed.
            app.MapGet("/api/events", async (HttpContext ctx) =>
            {
                ctx.Response.Headers["Content-Type"] = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                long lastSent = -1;
                CancellationToken token = ctx.RequestAborted;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        long revision = CSongRequests.Revision;
                        if (revision != lastSent)
                        {
                            lastSent = revision;
                            string payload = JsonSerializer.Serialize(new
                                {
                                    revision,
                                    entries = CSongRequests.GetAll(),
                                    playing = CSongRequests.GetPlaying(),
                                    next = CSongRequests.GetNextWaiting()
                                }, _JsonOptions);
                            await ctx.Response.WriteAsync("data: " + payload + "\n\n", token);
                            await ctx.Response.Body.FlushAsync(token);
                        }
                        else
                        {
                            // Comment frame keeps proxies and phone radios from dropping the socket.
                            await ctx.Response.WriteAsync(": ping\n\n", token);
                            await ctx.Response.Body.FlushAsync(token);
                        }
                        await Task.Delay(1000, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client navigated away or locked the phone. Nothing to do.
                }
            });
        }

        #endregion

        #region helpers

        private static Guid _GetSession(HttpContext ctx)
        {
            string header = ctx.Request.Headers["session"];
            if (string.IsNullOrEmpty(header))
                return Guid.Empty;

            Guid session;
            if (!Guid.TryParse(header, out session) || session == Guid.Empty)
                return Guid.Empty;

            CSessionControl.ResetSessionTimeout(session);
            return session;
        }

        private static bool _HasRight(HttpContext ctx, EUserRights right)
        {
            Guid session = _GetSession(ctx);
            return session != Guid.Empty && CSessionControl.RequestRight(session, right);
        }

        private static async Task<T> _ReadBody<T>(HttpContext ctx) where T : class
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, _JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IResult _Json(object payload)
        {
            return Results.Json(payload, _JsonOptions);
        }

        private static IResult _Error(int status, string message)
        {
            return Results.Json(new {error = message}, _JsonOptions, statusCode: status);
        }

        #endregion

        #region request bodies

        private class CCreateProfileBody
        {
            public string Name { get; set; }
        }

        private class CSessionBody
        {
            public string ProfileId { get; set; }
        }

        private class CQueueBody
        {
            public int SongId { get; set; }
            public List<string> Singers { get; set; }
        }

        private class CPositionBody
        {
            public int Position { get; set; }
        }

        private class CDifficultyBody
        {
            public int Difficulty { get; set; }
        }

        #endregion
    }
}
