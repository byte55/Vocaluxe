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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Base.Server
{
    /// <summary>
    ///     Dials out to the relay so guests can reach the queue without being on this network.
    /// </summary>
    /// <remarks>
    ///     This machine never accepts a connection from outside: it asks the relay for work, runs
    ///     each guest request against its own webserver on loopback, and posts the answer back.
    ///     Going through the local server rather than calling the endpoints directly means the
    ///     relay path and the local path are the same code - sessions, static files, the error
    ///     middleware and the render-loop timeout all behave identically, and there is no second
    ///     implementation to keep in step.
    ///     <para>
    ///         The room code is not stored here. The relay owns it and hands back whichever room
    ///         already belongs to this installation, identified by <see cref="_AgentId" />.
    ///     </para>
    /// </remarks>
    static class CRelayAgent
    {
        private static readonly HttpClient _Relay = new HttpClient();
        private static readonly HttpClient _Local = new HttpClient {Timeout = TimeSpan.FromSeconds(30)};

        private static CancellationTokenSource _Cancel;
        private static Task _Worker;
        private static Task _RevisionWatcher;
        private static string _AgentId;
        private static string _Room = "";
        private static string _Error = "";
        private static long _SentRevision = -1;

        /// <summary>The room code guests type, or an empty string while not connected.</summary>
        public static string RoomCode
        {
            get { return _Room; }
        }

        /// <summary>Why there is no connection, already translated, or an empty string.</summary>
        public static string ErrorText
        {
            get { return _Error; }
        }

        public static bool IsConnected
        {
            get { return _Room != ""; }
        }

        #region lifetime

        public static void Start()
        {
            if (_Worker != null)
                return;
            if (CConfig.Config.Server.RemoteRelay != EOffOn.TR_CONFIG_ON)
                return;

            string url = (CConfig.Config.Server.RemoteRelayUrl ?? "").Trim().TrimEnd('/');
            string token = (CConfig.Config.Server.RemoteRelayToken ?? "").Trim();
            if (url == "" || token == "")
            {
                _Error = "TR_SCREENPSERVERQR_RELAYNOTCONFIGURED";
                CLog.Error("Remote relay is on but the URL or token is missing");
                return;
            }

            _AgentId = _EnsureAgentId();
            _Cancel = new CancellationTokenSource();
            _Worker = Task.Run(() => _Run(url, token, _Cancel.Token));
            // Its own task, deliberately. Reporting a change from inside the polling loop would
            // leave it stuck behind the long poll: somebody signs up on the sofa and the phones
            // in the room hear about it up to a whole poll window later.
            _RevisionWatcher = Task.Run(() => _WatchRevision(url, _Cancel.Token));
        }

        public static void Stop()
        {
            if (_Cancel == null)
                return;
            _Cancel.Cancel();
            try
            {
                // Short: the worker is parked in a long poll and the cancellation tears that down.
                Task.WaitAll(new[] {_Worker, _RevisionWatcher}, TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // A worker that will not come back must not keep the program from closing.
            }
            _Cancel.Dispose();
            _Cancel = null;
            _Worker = null;
            _RevisionWatcher = null;
            _Room = "";
        }

        /// <summary>
        ///     The relay recognises this installation by its id, which therefore has to outlive a
        ///     restart. Created once and written to the config.
        /// </summary>
        private static string _EnsureAgentId()
        {
            string existing = (CConfig.Config.Server.RemoteAgentId ?? "").Trim();
            if (existing.Length >= 8)
                return existing;

            string created = Guid.NewGuid().ToString("N");
            CConfig.Config.Server.RemoteAgentId = created;
            CConfig.SaveConfig();
            CLog.Information("Created a relay agent id for this installation");
            return created;
        }

        #endregion lifetime

        #region worker

        private static async Task _Run(string url, string token, CancellationToken cancel)
        {
            _Relay.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _Relay.DefaultRequestHeaders.Add("X-Agent-Id", _AgentId);

            int failures = 0;
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    if (!IsConnected && !await _Hello(url, cancel))
                    {
                        // Backoff, capped: a relay that is down for the evening must not turn into a
                        // request every second for hours.
                        failures++;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 << Math.Min(failures, 5), 60)), cancel);
                        continue;
                    }
                    failures = 0;

                    await _PollOnce(url, cancel);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // Losing the relay is not losing the evening: the local server keeps serving
                    // whoever is on this network.
                    _Room = "";
                    _Error = "TR_SCREENPSERVERQR_RELAYUNREACHABLE";
                    CLog.Error(e, "Relay connection lost, retrying");
                    failures++;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 << Math.Min(failures, 5), 60)), cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            _Room = "";
        }

        private static async Task<bool> _Hello(string url, CancellationToken cancel)
        {
            var body = new StringContent(JsonSerializer.Serialize(new SHello {AgentId = _AgentId}),
                                         Encoding.UTF8, "application/json");
            using (HttpResponseMessage res = await _Relay.PostAsync(url + "/agent/hello", body, cancel))
            {
                if (!res.IsSuccessStatusCode)
                {
                    _Error = res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                 ? "TR_SCREENPSERVERQR_RELAYUNAUTHORIZED"
                                 : "TR_SCREENPSERVERQR_RELAYUNREACHABLE";
                    CLog.Error("Relay refused the connection: " + (int)res.StatusCode);
                    return false;
                }

                SHelloResult result = JsonSerializer.Deserialize<SHelloResult>(await res.Content.ReadAsStringAsync(cancel));
                _Room = result?.Room ?? "";
                _Error = "";
                _SentRevision = -1; // make sure the first poll reports where we stand
                CLog.Information("Connected to the relay, room code " + _Room);
                return IsConnected;
            }
        }

        /// <summary>
        ///     Tells the relay about queue changes so it can wake up the guests' live updates.
        /// </summary>
        /// <remarks>
        ///     Separate from the polling loop on purpose: that loop spends nearly all its time
        ///     parked in a long poll, so anything reported from inside it would arrive up to a
        ///     whole poll window late. The revision is a counter behind a plain lock, so checking
        ///     it twice a second costs nothing and never waits for the render loop.
        /// </remarks>
        private static async Task _WatchRevision(string url, CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancel);

                    long revision = CSongRequests.Revision;
                    if (!IsConnected || revision == _SentRevision)
                        continue;

                    // Send what our own event stream would send, not a summary of it. The page reads
                    // the queue straight out of the message, so both ways of reaching it have to
                    // carry the same thing - a bare revision number emptied every guest's list.
                    string queue = await _Local.GetStringAsync(
                        "http://127.0.0.1:" + CConfig.Config.Server.ServerPort + "/api/queue", cancel);

                    var body = new StringContent(JsonSerializer.Serialize(new SEvent {Revision = revision, Data = queue}),
                                                 Encoding.UTF8, "application/json");
                    using (HttpResponseMessage res = await _Relay.PostAsync(url + "/agent/event?room=" + _Room, body, cancel))
                    {
                        if (res.IsSuccessStatusCode)
                            _SentRevision = revision;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // The polling loop is the one that reports a broken connection; a missed
                    // revision only costs the guests one late update.
                }
            }
        }

        private static async Task _PollOnce(string url, CancellationToken cancel)
        {
            using (HttpResponseMessage res = await _Relay.GetAsync(url + "/agent/poll?room=" + _Room,
                                                                   HttpCompletionOption.ResponseContentRead, cancel))
            {
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // The relay forgot us (restarted, or the room expired). Ask for a room again;
                    // the code guests have will have changed, which is why this is worth logging.
                    CLog.Information("The relay no longer knows our room, asking for a new one");
                    _Room = "";
                    return;
                }
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException("poll failed with " + (int)res.StatusCode);

                SPollResult poll = JsonSerializer.Deserialize<SPollResult>(await res.Content.ReadAsStringAsync(cancel));
                if (poll?.Requests == null || poll.Requests.Length == 0)
                    return;

                // In parallel: one guest waiting on a slow request must not hold up the others.
                var running = new List<Task>(poll.Requests.Length);
                foreach (SRelayRequest request in poll.Requests)
                    running.Add(_Serve(url, request, cancel));
                await Task.WhenAll(running);
            }
        }

        #endregion worker

        #region serving

        /// <summary>Runs one guest request against our own webserver and posts the answer back.</summary>
        private static async Task _Serve(string url, SRelayRequest request, CancellationToken cancel)
        {
            SRelayResponse answer;
            try
            {
                answer = await _Execute(request, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not serve a relayed request for " + request.Path);
                answer = new SRelayResponse {Id = request.Id, Status = 502, Body = null};
            }

            try
            {
                var body = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json");
                using (await _Relay.PostAsync(url + "/agent/respond?room=" + _Room, body, cancel)) { }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                CLog.Error(e, "Could not hand a response back to the relay");
            }
        }

        private static async Task<SRelayResponse> _Execute(SRelayRequest request, CancellationToken cancel)
        {
            string target = "http://127.0.0.1:" + CConfig.Config.Server.ServerPort + request.Path;
            var message = new HttpRequestMessage(new HttpMethod(request.Method), target);

            if (request.Body != null)
            {
                message.Content = new ByteArrayContent(Convert.FromBase64String(request.Body));
                if (request.Headers != null && request.Headers.TryGetValue("content-type", out string contentType))
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (request.Headers != null)
            {
                foreach (KeyValuePair<string, string> header in request.Headers)
                {
                    if (header.Key == "content-type")
                        continue; // belongs on the content, set above
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using (HttpResponseMessage res = await _Local.SendAsync(message, cancel))
            {
                byte[] payload = await res.Content.ReadAsByteArrayAsync(cancel);
                var headers = new Dictionary<string, string>();
                foreach (var header in res.Content.Headers)
                    headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
                foreach (var header in res.Headers)
                {
                    // Hop-by-hop headers describe our loopback connection, not the guest's.
                    string name = header.Key.ToLowerInvariant();
                    if (name == "connection" || name == "keep-alive" || name == "transfer-encoding")
                        continue;
                    headers[name] = string.Join(", ", header.Value);
                }

                return new SRelayResponse
                    {
                        Id = request.Id,
                        Status = (int)res.StatusCode,
                        Headers = headers,
                        Body = payload.Length > 0 ? Convert.ToBase64String(payload) : null
                    };
            }
        }

        #endregion serving

        #region wire types

        private class SHello
        {
            [JsonPropertyName("agentId")] public string AgentId { get; set; }
        }

        private class SHelloResult
        {
            [JsonPropertyName("room")] public string Room { get; set; }
        }

        private class SEvent
        {
            [JsonPropertyName("revision")] public long Revision { get; set; }
            [JsonPropertyName("data")] public string Data { get; set; }
        }

        private class SPollResult
        {
            [JsonPropertyName("requests")] public SRelayRequest[] Requests { get; set; }
        }

        private class SRelayRequest
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("method")] public string Method { get; set; }
            [JsonPropertyName("path")] public string Path { get; set; }
            [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; }
            [JsonPropertyName("body")] public string Body { get; set; }
        }

        private class SRelayResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("status")] public int Status { get; set; }
            [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; }
            [JsonPropertyName("body")] public string Body { get; set; }
        }

        #endregion wire types
    }
}
