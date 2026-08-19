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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VocaluxeLib;
using VocaluxeLib.Log;
using SkiaSharp;
using VocaluxeLib.Profile;

namespace Vocaluxe.Base.Server
{
    /// <summary>
    ///     The event API used by the new web frontend: pick a profile, find a song, get in line.
    ///
    ///     Grew up next to the old WCF-shaped API (CWebservice), which is why it is a separate class;
    ///     that one has since been removed. Uses proper verbs and System.Text.Json, and — importantly
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
            // Surface endpoint exceptions in the game log. ASP.NET's own logging is disabled
            // (ClearProviders), so without this an unhandled handler exception is just a silent 500.
            app.Use(async (HttpContext ctx, Func<Task> next) =>
            {
                try
                {
                    await next();
                }
                catch (TimeoutException)
                {
                    // The game loop did not pick the task up in time (a stalled render loop does
                    // this). Report it as "temporarily unavailable" instead of hanging the client,
                    // and keep it out of the error log -- it is a state, not a bug.
                    CLog.Information("Webserver: main thread timeout on " + ctx.Request.Method + " " + ctx.Request.Path);
                    if (!ctx.Response.HasStarted)
                        ctx.Response.StatusCode = 503;
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Webserver request failed: " + ctx.Request.Method + " " + ctx.Request.Path);
                    if (!ctx.Response.HasStarted)
                        ctx.Response.StatusCode = 500;
                }
            });

            _MapProfiles(app);
            _MapSongs(app);
            _MapQueue(app);
            _MapControl(app);
            _MapRemote(app);
            _MapEvents(app);
        }

        #region profiles and sessions

        private static void _MapProfiles(WebApplication app)
        {
            // Deliberately open: the list of names is what you tap to identify yourself. It exposes
            // display names on a LAN party network, nothing more.
            app.MapGet("/api/profiles", () => _Json(CVocaluxeServer.DoTask(CVocaluxeServer.GetProfileListForWeb)));

            app.MapPost("/api/profiles", async (HttpContext ctx) =>
            {
                CCreateProfileBody body = await _ReadBody<CCreateProfileBody>(ctx);
                if (body == null || string.IsNullOrWhiteSpace(body.Name))
                    return _Error(400, "A name is required");

                Guid id = CVocaluxeServer.DoTask(CVocaluxeServer.CreateGuestProfile, body.Name, body.AvatarId);
                if (id == Guid.Empty)
                    return _Error(500, "Could not create the profile");

                CSignInResult signIn = CSessionControl.OpenSessionForProfile(id, null);
                return _Json(new {profileId = id.ToString(), playerName = body.Name.Trim(), sessionId = signIn.SessionId.ToString()});
            });

            // Tap-to-identify. A profile without a PIN needs nothing but the tap; one with a PIN
            // sends it along in the body of this same request.
            app.MapPost("/api/session", async (HttpContext ctx) =>
            {
                CSessionBody body = await _ReadBody<CSessionBody>(ctx);
                Guid profileId;
                if (body == null || !Guid.TryParse(body.ProfileId, out profileId))
                    return _Error(400, "profileId is required");

                CSignInResult result = CSessionControl.OpenSessionForProfile(profileId, body.Pin);

                if (result.SessionId != Guid.Empty)
                    return _Json(new {sessionId = result.SessionId.ToString(), profileId = profileId.ToString()});

                // Answer immediately with the remaining wait instead of holding the request open —
                // a hanging request looks like a dead app and ties up a thread for nothing.
                if (result.RetryAfterSeconds > 0 && !result.WrongPin)
                    return _Error(429, "Zu viele Fehlversuche. Warte " + result.RetryAfterSeconds + " Sekunden.");

                if (result.WrongPin)
                {
                    return result.RetryAfterSeconds > 0
                        ? _Error(429, "PIN falsch. Nächster Versuch in " + result.RetryAfterSeconds + " Sekunden.")
                        : _Error(403, "PIN falsch.");
                }

                return _Error(403, "Anmeldung nicht möglich.");
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
                        difficulty = me == null ? 1 : me.Difficulty,
                        avatarId = me == null ? -1 : me.AvatarId,
                        hasPin = me != null && me.HasPin
                    });
            });

            app.MapPost("/api/me/pin", async (HttpContext ctx) =>
            {
                Guid session = _GetSession(ctx);
                Guid profileId = CSessionControl.GetUserIdFromSession(session);
                if (profileId == Guid.Empty)
                    return _Error(401, "Pick a profile first");

                CPinBody body = await _ReadBody<CPinBody>(ctx);
                if (body == null)
                    return _Error(400, "Body fehlt");

                EPinResult result = CVocaluxeServer.DoTask(CVocaluxeServer.SetProfilePin, profileId, body.CurrentPin, body.NewPin);
                switch (result)
                {
                    case EPinResult.Ok:
                        // Claiming the profile ends everyone else's session on it — that is the whole
                        // point when somebody else is sitting in it right now.
                        CSessionControl.InvalidateOtherSessionsForProfile(profileId, session);
                        return _Json(new {hasPin = !string.IsNullOrEmpty(body.NewPin)});
                    case EPinResult.WrongCurrentPin:
                        return _Error(403, "Die bisherige PIN stimmt nicht.");
                    case EPinResult.InvalidPin:
                        return _Error(400, "Die PIN muss aus 4 bis 10 Ziffern bestehen.");
                    case EPinResult.AdminNeedsPin:
                        return _Error(403, "Admin-Profile brauchen eine PIN, sie lässt sich nicht entfernen.");
                    case EPinResult.AdminWithoutPinLocked:
                        return _Error(403, "Dieses Admin-Profil hat keine PIN und kann sich selbst keine geben. "
                                           + "Das muss am Karaoke-Rechner passieren.");
                    default:
                        return _Error(404, "Profil nicht gefunden.");
                }
            });

            // Only the bundled avatars are on offer — there is no upload path in this API on
            // purpose, so nothing unvetted can end up on a profile or on the beamer.
            app.MapGet("/api/avatars", () => _Json(CVocaluxeServer.DoTask(CVocaluxeServer.GetAvatarList)));

            app.MapGet("/api/avatars/{id:int}/image", (HttpContext ctx, int id) =>
            {
                string path = CVocaluxeServer.DoTask(CVocaluxeServer.GetAvatarFilePath, id);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return Results.NotFound();

                byte[] png = _AvatarThumbnail(id, path);
                if (png == null)
                    return Results.NotFound();

                // The bundled avatars never change while the game runs, and a phone should fetch
                // each one once even when the queue screen is reopened all evening.
                ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
                return Results.Bytes(png, "image/webp");
            });

            app.MapPost("/api/me/avatar", async (HttpContext ctx) =>
            {
                Guid profileId = CSessionControl.GetUserIdFromSession(_GetSession(ctx));
                if (profileId == Guid.Empty)
                    return _Error(401, "Pick a profile first");

                CAvatarBody body = await _ReadBody<CAvatarBody>(ctx);
                if (body == null)
                    return _Error(400, "avatarId is required");

                bool ok = CVocaluxeServer.DoTask(CVocaluxeServer.SetProfileAvatar, profileId, body.AvatarId);
                return ok ? _Json(new {avatarId = body.AvatarId}) : _Error(404, "Diesen Avatar gibt es nicht.");
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
            // No DoTask: the search reads an immutable snapshot of the library and only falls back
            // to the main thread when that snapshot has to be (re)built.
            app.MapGet("/api/songs", (string q, int? offset, int? limit) =>
            {
                SSongSearchResult result = CVocaluxeServer.SearchSongs(q ?? "", offset ?? 0, limit ?? 50);
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
            // Starting is not an administrative act here, it *is* that confirmation — so no special
            // right is needed. But it takes two conditions, and both matter: the entry has to be
            // yours (otherwise you start other people's songs while they are still at the buffet),
            // and it has to be the one that is actually next (otherwise you jump the queue with your
            // own entry from position five). Whoever may reorder the queue may ignore both, because
            // somebody has to be able to skip an act that never shows up.
            app.MapPost("/api/queue/start-next", (HttpContext ctx) =>
            {
                CSongRequest next = CSongRequests.GetNextWaiting();
                if (next == null)
                    return _Error(409, "Es wartet gerade niemand.");

                IResult denied = _CheckMayStart(ctx, next.RequestId);
                return denied ?? _StartRequest(next.RequestId);
            });

            app.MapPost("/api/queue/{id:int}/start", (HttpContext ctx, int id) =>
            {
                IResult denied = _CheckMayStart(ctx, id);
                return denied ?? _StartRequest(id);
            });
        }

        #region remote control

        private static void _MapRemote(WebApplication app)
        {
            // Taken over from the old /sendKeyEvent so the host keeps a way to drive the game from a
            // phone when nobody is at the keyboard. Same right as before (UseKeyboard), which admins
            // have; unlike the old endpoint it is not reachable without a claimed profile.
            app.MapPost("/api/remote/key", async (HttpContext ctx) =>
            {
                if (!_HasRight(ctx, EUserRights.UseKeyboard))
                    return _Error(403, "Dafür fehlen dir die Rechte.");

                CKeyBody body = await _ReadBody<CKeyBody>(ctx);
                if (body == null || string.IsNullOrEmpty(body.Key))
                    return _Error(400, "key fehlt");

                bool ok = CVocaluxeServer.DoTask(CVocaluxeServer.SendKeyEvent, body.Key);
                return ok ? _Json(new {sent = body.Key}) : _Error(400, "Unbekannte Taste: " + body.Key);
            });

            app.MapGet("/api/remote/state", (HttpContext ctx) =>
            {
                if (!_HasRight(ctx, EUserRights.UseKeyboard))
                    return _Error(403, "Dafür fehlen dir die Rechte.");
                return _Json(new {screen = CVocaluxeServer.DoTask(CVocaluxeServer.GetCurrentScreenName)});
            });

            // "The singer gave up" / "nobody came to the microphone".
            app.MapPost("/api/queue/abort-current", (HttpContext ctx) =>
            {
                if (!_HasRight(ctx, EUserRights.ReorderPlaylists))
                    return _Error(403, "Nur wer die Warteliste verwaltet, kann einen Song abbrechen.");

                return CVocaluxeServer.DoTask(CVocaluxeServer.AbortCurrentSong)
                    ? _Json(new {aborted = true})
                    : _Error(409, "Gerade läuft kein Song.");
            });
        }

        #endregion

        /// <summary>
        ///     Returns an error result when this session must not start <paramref name="requestId" />,
        ///     or null when it may.
        /// </summary>
        private static IResult _CheckMayStart(HttpContext ctx, int requestId)
        {
            Guid session = _GetSession(ctx);
            Guid ownProfile = CSessionControl.GetUserIdFromSession(session);
            if (ownProfile == Guid.Empty)
                return _Error(401, "Wähle zuerst dein Profil.");

            if (CSessionControl.RequestRight(session, EUserRights.ReorderPlaylists))
                return null;

            CSongRequest request = CSongRequests.GetById(requestId);
            if (request == null)
                return _Error(404, "Diesen Eintrag gibt es nicht mehr.");

            bool mine = request.CreatedBy == ownProfile || request.SingerProfileIds.Contains(ownProfile);
            if (!mine)
                return _Error(403, "Das ist nicht dein Song — starten darf ihn, wer ihn eingetragen hat.");

            CSongRequest next = CSongRequests.GetNextWaiting();
            if (next == null || next.RequestId != requestId)
                return _Error(409, "Du bist noch nicht dran. Warte, bis dein Song oben steht.");

            return null;
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

                // React to the game shutting down as well, not just to the client leaving. Without
                // this the loop below keeps a shutdown waiting for its full timeout.
                IHostApplicationLifetime lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping))
                {
                CancellationToken token = stopping.Token;

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
                    // Client navigated away, locked the phone, or the game is shutting down.
                }
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

        // Rendered thumbnails, kept in memory. The source files are ~60 KB each and there are two
        // dozen of them; sending the originals to every phone would be several megabytes for
        // pictures displayed at thumb size.
        private static readonly ConcurrentDictionary<int, byte[]> _AvatarCache = new ConcurrentDictionary<int, byte[]>();
        private const int _AvatarSize = 192;

        private static byte[] _AvatarThumbnail(int avatarId, string path)
        {
            byte[] cached;
            if (_AvatarCache.TryGetValue(avatarId, out cached))
                return cached;

            try
            {
                using (SKBitmap source = SKBitmap.Decode(path))
                {
                    if (source == null)
                        return null;

                    int size = Math.Min(_AvatarSize, Math.Max(source.Width, source.Height));
                    float scale = (float)size / Math.Max(source.Width, source.Height);
                    var info = new SKImageInfo(Math.Max(1, (int)(source.Width * scale)),
                                               Math.Max(1, (int)(source.Height * scale)));

                    using (SKBitmap scaled = source.Resize(info, SKSamplingOptions.Default))
                    using (SKImage image = SKImage.FromBitmap(scaled ?? source))
                    // WebP rather than PNG: these are photographic portraits, which PNG stores badly
                    // (~28 KB each, ~660 KB for the whole gallery on a phone). WebP keeps the alpha
                    // channel some of the avatars use and is a fraction of the size.
                    using (SKData data = image.Encode(SKEncodedImageFormat.Webp, 82))
                    {
                        byte[] bytes = data.ToArray();
                        _AvatarCache[avatarId] = bytes;
                        return bytes;
                    }
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not render avatar " + avatarId);
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
            public int AvatarId { get; set; }
        }

        private class CAvatarBody
        {
            public int AvatarId { get; set; }
        }

        private class CSessionBody
        {
            public string ProfileId { get; set; }
            public string Pin { get; set; }
        }

        private class CPinBody
        {
            public string CurrentPin { get; set; }
            public string NewPin { get; set; }
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

        private class CKeyBody
        {
            public string Key { get; set; }
        }

        #endregion
    }
}
