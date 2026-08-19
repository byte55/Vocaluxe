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
using System.Timers;

namespace Vocaluxe.Base.Server
{
    static class CSessionControl
    {
        private static readonly Dictionary<Guid, CSession> _ActiveSessions;

        /// <summary>
        ///     Failed PIN attempts per profile. Counted per profile rather than per client: at a party
        ///     everybody shares one WLAN address, so per-IP counting would punish the wrong people.
        ///     Lives in memory only — a restart clearing it is fine.
        /// </summary>
        private static readonly Dictionary<Guid, CPinAttempts> _PinAttempts = new Dictionary<Guid, CPinAttempts>();

        private const int _FreeAttempts = 3;
        private const int _MaxBlockSeconds = 30;

        private class CPinAttempts
        {
            public int Failures;
            public DateTime BlockedUntil;
        }
        private const int _UserTimeoutCheckIntervall = 120000;

        /// <summary>
        ///     Idle time before a session is dropped. Two minutes (the old value) is far too short
        ///     for the intended use: a guest picks a profile, puts the phone in a pocket, and is
        ///     logged out before the song they signed up for even starts.
        /// </summary>
        private const int _UserTimeout = 4 * 60 * 60 * 1000;

        static CSessionControl()
        {
            _ActiveSessions = new Dictionary<Guid, CSession>();
            Timer timer = new Timer(_UserTimeoutCheckIntervall) {AutoReset = true, Enabled = true};
            timer.Elapsed += _CheckForUserTimeouts;
            timer.Start();
        }

        private static void _CheckForUserTimeouts(object sender, ElapsedEventArgs e)
        {
            var sessionIdsToRemove = _ActiveSessions.Where(pair => (DateTime.Now-pair.Value.LastSeen).TotalMilliseconds > _UserTimeout)
                         .Select(pair => pair.Key)
                         .ToList();

            foreach (var sessionToRemove in sessionIdsToRemove)
            {
                InvalidateSessionByID(sessionToRemove);
            }
        }

        public static Guid OpenSession(string userName, string password)
        {
            if (!_ValidateUserAndPassword(userName, password))
                return Guid.Empty;

            Guid newId = Guid.NewGuid();
            Guid id = _GetProfileIdFormUsername(userName);
            EUserRoles roles = _GetUserRoles(id);
            CSession session = new CSession(newId, id, roles);
            //InvalidateSessions(id);
            _ActiveSessions.Add(newId, session);

            return newId;
        }

        /// <summary>
        ///     Signs in to a profile. A profile without a PIN needs nothing but a tap — that low bar is
        ///     the point of the whole flow. One with a PIN needs it, and repeated wrong guesses make
        ///     the next try wait a little longer each time.
        /// </summary>
        public static CSignInResult OpenSessionForProfile(Guid profileId, string pin)
        {
            var result = new CSignInResult();
            if (profileId == Guid.Empty)
                return result;

            if (!CVocaluxeServer.HasPin(profileId))
            {
                result.SessionId = _CreateSession(profileId);
                return result;
            }

            lock (_PinAttempts)
            {
                CPinAttempts attempts;
                if (_PinAttempts.TryGetValue(profileId, out attempts) && attempts.BlockedUntil > DateTime.Now)
                {
                    result.RetryAfterSeconds = (int)Math.Ceiling((attempts.BlockedUntil - DateTime.Now).TotalSeconds);
                    return result;
                }
            }

            if (!CVocaluxeServer.ValidatePassword(profileId, pin ?? ""))
            {
                result.WrongPin = true;
                result.RetryAfterSeconds = _RegisterFailedAttempt(profileId);
                return result;
            }

            lock (_PinAttempts)
                _PinAttempts.Remove(profileId);

            result.SessionId = _CreateSession(profileId);
            return result;
        }

        private static Guid _CreateSession(Guid profileId)
        {
            Guid newId = Guid.NewGuid();
            _ActiveSessions.Add(newId, new CSession(newId, profileId, _GetUserRoles(profileId)));
            return newId;
        }

        /// <summary>
        ///     Grows the wait after each miss: three free tries, then 2, 4, 8 … seconds up to half a
        ///     minute. Slows a joker down without ever locking anybody out for good.
        /// </summary>
        private static int _RegisterFailedAttempt(Guid profileId)
        {
            lock (_PinAttempts)
            {
                CPinAttempts attempts;
                if (!_PinAttempts.TryGetValue(profileId, out attempts))
                {
                    attempts = new CPinAttempts();
                    _PinAttempts[profileId] = attempts;
                }

                attempts.Failures++;
                if (attempts.Failures <= _FreeAttempts)
                    return 0;

                int seconds = Math.Min(_MaxBlockSeconds, (int)Math.Pow(2, attempts.Failures - _FreeAttempts));
                attempts.BlockedUntil = DateTime.Now.AddSeconds(seconds);
                return seconds;
            }
        }

        private static bool _ValidateUserAndPassword(string userName, string password)
        {
            return CVocaluxeServer.ValidatePassword(_GetProfileIdFormUsername(userName), password);
        }

        private static Guid _GetProfileIdFormUsername(string username)
        {
            return CVocaluxeServer.GetUserIdFromUsername(username);
        }

        private static EUserRoles _GetUserRoles(Guid profileId)
        {
            return (EUserRoles)CVocaluxeServer.GetUserRole(profileId);
        }

        internal static void InvalidateSessionByProfile(Guid profileId)
        {
            foreach (KeyValuePair<Guid, CSession> s in (from kv in _ActiveSessions
                                                        where kv.Value.ProfileId == profileId
                                                        select kv).ToList())
                _ActiveSessions.Remove(s.Key);
        }

        /// <summary>Drops every session on a profile except <paramref name="keep" />.</summary>
        internal static void InvalidateOtherSessionsForProfile(Guid profileId, Guid keep)
        {
            foreach (KeyValuePair<Guid, CSession> s in (from kv in _ActiveSessions
                                                        where kv.Value.ProfileId == profileId && kv.Key != keep
                                                        select kv).ToList())
                _ActiveSessions.Remove(s.Key);
        }

        internal static void InvalidateSessionByID(Guid sessionId)
        {
            if (_ActiveSessions.ContainsKey(sessionId))
            {
                _ActiveSessions.Remove(sessionId);
            }
        }

        internal static bool RequestRight(Guid sessionId, EUserRights requestedRight)
        {
            // Unknown/empty/expired session has no rights. (Previously indexing _ActiveSessions[sessionId]
            // directly threw KeyNotFoundException -> HTTP 500, e.g. on /getProfile without a valid session.)
            CSession session;
            if (!_ActiveSessions.TryGetValue(sessionId, out session))
                return false;

            // Rights only count for a profile that is actually claimed. Without this an admin profile
            // left without a PIN would be a free pass: anyone could tap the name and inherit every
            // right it has. One check here covers every endpoint at once.
            if (!CVocaluxeServer.HasPin(session.ProfileId))
                return false;

            return CUserRoleControl.GetUserRightsFromUserRole(session.Roles).HasFlag(requestedRight);
        }

        internal static Guid GetUserIdFromSession(Guid sessionId)
        {
            if (!_ActiveSessions.ContainsKey(sessionId))
                return Guid.Empty;
            return _ActiveSessions[sessionId].ProfileId;
        }

        internal static void ResetSessionTimeout(Guid sessionId)
        {
            CSession value;
            if (_ActiveSessions.TryGetValue(sessionId, out value))
            {
                value.LastSeen = DateTime.Now;
            }
        }
    }
}