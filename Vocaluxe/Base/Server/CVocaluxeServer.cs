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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vocaluxe.Lib.Input;
using Vocaluxe.Lib.Playlist;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;
using VocaluxeLib.Profile;
using VocaluxeLib.Songs;

namespace Vocaluxe.Base.Server
{
    static class CVocaluxeServer
    {
        // Request threads enqueue here and the main thread (ProcessServerTasks) dequeues; must be
        // thread-safe. A plain Queue raced under concurrent web requests and crashed the game loop.
        private static readonly ConcurrentQueue<Task> _ServerTaskQueue = new ConcurrentQueue<Task>();

        // ASP.NET Core (Kestrel) host for the browser remote control (S2; replaces the old WCF host).
        private static WebApplication _App;
        private static bool _Running;
        private static string _Address = "";
        // Translation key for the reason the server is enabled but not running (e.g. the port is in
        // use); the UI translates it. Empty when running or when no specific reason is known.
        private static string _StatusKey = "";

        private class CServerController : CControllerFramework
        {
            public override string GetName()
            {
                return "App controller";
            }

            public override void Connect() {}

            public override void Disconnect() {}

            public override bool IsConnected()
            {
                return true;
            }

            public override void SetRumble(float duration) {}
        }

        public static readonly CControllerFramework Controller = new CServerController();


        #region server control

        public static void Init()
        {
            if (CConfig.Config.Server.ServerActive != EOffOn.TR_CONFIG_ON)
                return;
            try
            {
                int port = CConfig.Config.Server.ServerPort;
                if (CConfig.Config.Server.ServerEncryption == EOffOn.TR_CONFIG_ON)
                    CLog.Information("Webserver: HTTPS is not yet supported on the cross-platform build; falling back to HTTP.");

                // Every endpoint marshals its work onto the main thread via DoTask, and that queue is
                // drained exactly once per rendered frame (CDrawBase.MainLoop -> ProcessServerTasks).
                // With VSync on, SwapBuffers waits for a compositor frame callback -- and a minimized
                // window never gets one on Wayland, so the render loop parks forever and the server
                // stops answering *every* client until the window is restored. GLFW cannot help here:
                // xdg-shell never tells the client it was minimized, so WindowState stays Fullscreen.
                // Measured on this machine; see docs/web-queue.md for the full trace.
                if (CConfig.Config.Graphics.VSync == EOffOn.TR_CONFIG_ON)
                {
                    CLog.Information("Webserver: VSync is enabled. If the game window is minimized, the render loop "
                                     + "can block and the webserver will stop responding until it is restored. "
                                     + "Turn VSync off (Options -> Graphics) when the server is used during an event.");
                }

                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://0.0.0.0:" + port + "/");
                // The REST API reads request bodies and writes responses synchronously via
                // DataContractJsonSerializer. Kestrel disallows synchronous I/O by default, which made
                // every POST with a body (create profile, upload photo, edit playlist) fail with HTTP
                // 500 ("Synchronous operations are disallowed").
                builder.WebHost.ConfigureKestrel(options => options.AllowSynchronousIO = true);
                _App = builder.Build();

                // Serve the new frontend straight from Kestrel. The old per-directory handlers in
                // CWebservice route every file through DoTask and therefore through the render loop,
                // which is both pointless for static bytes and fragile when the loop stalls.
                string webRoot = Path.Combine(CSettings.ProgramFolder, "Website", "app");
                if (Directory.Exists(webRoot))
                {
                    var fileProvider = new PhysicalFileProvider(webRoot);
                    _App.UseDefaultFiles(new DefaultFilesOptions {FileProvider = fileProvider, RequestPath = ""});
                    _App.UseStaticFiles(new StaticFileOptions {FileProvider = fileProvider, RequestPath = ""});
                }
                else
                    CLog.Error("Web frontend not found at " + webRoot + "; only the legacy page will be available");

                CWebservice.MapEndpoints(_App);
                CWebQueueApi.MapEndpoints(_App);
                CSongRequests.Load();

                _Address = "http://" + Dns.GetHostName() + ":" + port + "/";
                Start();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not initialize the webserver");
                if (string.IsNullOrEmpty(_StatusKey))
                    _StatusKey = "TR_SCREENPSERVERQR_STARTFAILED";
                _App = null;
            }
        }

        public static void Start()
        {
            if (_App == null || _Running)
                return;
            try
            {
                _App.Start();
                _Running = true;
                _StatusKey = "";
                CLog.Information("Webserver running at " + _Address);
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not start the webserver");
                string reason = (e.Message + " " + (e.InnerException != null ? e.InnerException.Message : "")).ToLowerInvariant();
                _StatusKey = (reason.Contains("in use") || reason.Contains("address already") || reason.Contains("bind"))
                    ? "TR_SCREENPSERVERQR_PORTINUSE"
                    : "TR_SCREENPSERVERQR_STARTFAILED";
            }
        }

        public static void Close()
        {
            if (_App == null)
                return;
            try
            {
                _App.StopAsync().GetAwaiter().GetResult();
                ((IDisposable)_App).Dispose();
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error stopping the webserver");
            }
            _App = null;
            _Running = false;
        }

        public static string GetServerAddress()
        {
            return _Address;
        }

        public static bool IsServerRunning()
        {
            return _Running;
        }

        /// <summary>
        ///     Translation key for why the server is enabled but not running (e.g. the port is in use),
        ///     or an empty string when it is running or no specific reason is known. The caller
        ///     translates it (the "%d" placeholder is the configured server port).
        /// </summary>
        public static string GetStatusKey()
        {
            return _StatusKey;
        }

        #endregion

        #region task control

        public static void ProcessServerTasks()
        {
            //Serial processing - one by one, on the main thread (invoked by the render loop).
            Task task;
            while (_ServerTaskQueue.TryDequeue(out task))
            {
                //Run inline on the current (main) thread. The old code used
                //TaskScheduler.FromCurrentSynchronizationContext(), which required a
                //SynchronizationContext (provided by the former WinForms message loop). The OpenTK
                //GameWindow main thread has none, so we run on the default scheduler instead.
                try
                {
                    task.RunSynchronously();
                }
                catch (Exception e)
                {
                    // A faulting server task must never crash the game loop. The exception is also
                    // observed by the waiting request thread (task.Wait()), which turns it into a 500.
                    CLog.Error(e, "A webserver task threw an exception");
                }
            }
        }


        /// <summary>
        ///     How long a request waits for the main thread before giving up.
        ///
        ///     This used to be an unbounded Wait(), which turned any stall of the render loop into a
        ///     dead webserver: the queue below is only drained once per rendered frame, so a stalled
        ///     loop meant every request blocked forever, each one holding a thread pool thread until
        ///     the pool was empty and even endpoints that never touch the main thread stopped
        ///     answering. Failing after a few seconds keeps the damage local to the request.
        /// </summary>
        private const int _MainThreadTimeoutMs = 5000;

        private static TReturnType _RunOnMainThread<TReturnType>(Task<TReturnType> task)
        {
            _ServerTaskQueue.Enqueue(task);
            if (!task.Wait(_MainThreadTimeoutMs))
                throw new TimeoutException("The game loop did not pick up the request within " + _MainThreadTimeoutMs + " ms");
            return task.Result;
        }

        private static void _RunOnMainThread(Task task)
        {
            _ServerTaskQueue.Enqueue(task);
            if (!task.Wait(_MainThreadTimeoutMs))
                throw new TimeoutException("The game loop did not pick up the request within " + _MainThreadTimeoutMs + " ms");
        }

        public static TReturnType DoTask<TReturnType>(Func<TReturnType> action)
        {
            var task = new Task<TReturnType>(action);
            return _RunOnMainThread(task);
        }

        public static TReturnType DoTask<TReturnType, TParameterType>(Func<TParameterType, TReturnType> action, TParameterType parameter)
        {
            var task = new Task<TReturnType>(() => action(parameter));
            return _RunOnMainThread(task);
        }

        public static TReturnType DoTask<TReturnType, TParameterType1, TParameterType2>(Func<TParameterType1, TParameterType2, TReturnType> action, TParameterType1 parameter1, TParameterType2 parameter2)
        {
            var task = new Task<TReturnType>(() => action(parameter1, parameter2));
            return _RunOnMainThread(task);
        }

        public static TReturnType DoTask<TReturnType, TParameterType1, TParameterType2, TParameterType3>(Func<TParameterType1, TParameterType2, TParameterType3, TReturnType> action, TParameterType1 parameter1, TParameterType2 parameter2, TParameterType3 parameter3)
        {
            var task = new Task<TReturnType>(() => action(parameter1, parameter2, parameter3));
            return _RunOnMainThread(task);
        }

        public static TReturnType DoTask<TReturnType, TParameterType1, TParameterType2, TParameterType3, TParameterType4>(Func<TParameterType1, TParameterType2, TParameterType3, TParameterType4, TReturnType> action, TParameterType1 parameter1, TParameterType2 parameter2, TParameterType3 parameter3, TParameterType4 parameter4)
        {
            var task = new Task<TReturnType>(() => action(parameter1, parameter2, parameter3, parameter4));
            return _RunOnMainThread(task);
        }

        
        public static void DoTaskWithoutReturn(Action action)
        {
            var task = new Task(action);
            _RunOnMainThread(task);
        }

        public static void DoTaskWithoutReturn<TParameterType>(Action<TParameterType> action, TParameterType parameter)
        {
            var task = new Task(() => action(parameter));
            _RunOnMainThread(task);
        }

        public static void DoTaskWithoutReturn<TParameterType1, TParameterType2>(Action<TParameterType1, TParameterType2> action, TParameterType1 parameter1, TParameterType2 parameter2)
        {
            var task = new Task(() => action(parameter1, parameter2));
            _RunOnMainThread(task);
        }

        public static void DoTaskWithoutReturn<TParameterType1, TParameterType2, TParameterType3>(Action<TParameterType1, TParameterType2, TParameterType3> action, TParameterType1 parameter1, TParameterType2 parameter2, TParameterType3 parameter3)
        {
            var task = new Task(() => action(parameter1, parameter2, parameter3));
            _RunOnMainThread(task);
        }

        public static void DoTaskWithoutReturn<TParameterType1, TParameterType2, TParameterType3, TParameterType4>(Action<TParameterType1, TParameterType2, TParameterType3, TParameterType4> action, TParameterType1 parameter1, TParameterType2 parameter2, TParameterType3 parameter3, TParameterType4 parameter4)
        {
            var task = new Task(() => action(parameter1, parameter2, parameter3, parameter4));
            _RunOnMainThread(task);
        }

        #endregion

        public static bool SendKeyEvent(string key)
        {
            bool result = false;
            string lowerKey = key.ToLower();

            if (!string.IsNullOrEmpty(lowerKey))
            {
                switch (lowerKey)
                {
                    case "up":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Up));
                        result = true;
                        break;
                    case "down":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Down));
                        result = true;
                        break;
                    case "left":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Left));
                        result = true;
                        break;
                    case "right":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Right));
                        result = true;
                        break;
                    case "escape":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Escape));
                        result = true;
                        break;
                    case "return":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Return));
                        result = true;
                        break;
                    case "tab":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Tab));
                        result = true;
                        break;
                    case "backspace":
                        Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, Keys.Back));
                        result = true;
                        break;
                    default:
                        if (lowerKey.StartsWith("f"))
                        {
                            string numberString = lowerKey.Substring(1);
                            int number;
                            Keys fKey;

                            if (Int32.TryParse(numberString, out number) && number >= 1
                                && number <= 12
                                && Enum.TryParse("F" + number, true, out fKey))
                            {
                                Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, false, false, false, false, Char.MinValue, fKey));
                                result = true;
                            }
                        }
                        break;
                }
            }

            return result;
        }

        public static bool SendKeyStringEvent(string keyString, bool isShiftPressed, bool isAltPressed, bool isCtrlPressed)
        {
            bool result = false;

            foreach (char key in keyString)
            {
                Controller.AddKeyEvent(new SKeyEvent(ESender.Keyboard, isAltPressed,
                                                     Char.IsUpper(key) || isShiftPressed,
                                                     isCtrlPressed, true,
                                                     isShiftPressed ? Char.ToUpper(key) : key,
                                                     _ParseKeys(key)));
                result = true;
            }

            return result;
        }

        private static Keys _ParseKeys(char keyText)
        {
            Keys key;

            if (!Enum.TryParse(keyText.ToString(), true, out key))
            {
                switch (keyText)
                {
                    case ' ':
                        key = Keys.Space;
                        break;
                    default:
                        key = Keys.None;
                        break;
                }
            }

            return key;
        }

        #region profile
        public static SProfileData GetProfileData(Guid profileId, bool isReadonly)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return new SProfileData();
            return _CreateProfileData(profile, isReadonly);
        }

        public static bool SendProfileData(SProfileData profile)
        {
            CProfile newProfile;
            CProfile existingProfile = CProfiles.GetProfile(profile.ProfileId);

            if (existingProfile != null)
            {
                newProfile = new CProfile
                    {
                        ID = existingProfile.ID,
                        FilePath = existingProfile.FilePath,
                        Active = existingProfile.Active,
                        Avatar = existingProfile.Avatar,
                        Difficulty = existingProfile.Difficulty,
                        UserRole = existingProfile.UserRole,
                        PlayerName = existingProfile.PlayerName
                    };
            }
            else
            {
                newProfile = new CProfile
                    {
                        Active = EOffOn.TR_CONFIG_ON,
                        UserRole = EUserRole.TR_USERROLE_NORMAL
                    };
            }

            // Uploaded pictures are no longer accepted. Profiles choose from the avatars that ship
            // with Vocaluxe (GET /api/avatars); an uploaded one would be shown on the profile and,
            // through the score screen slideshow, on the beamer -- with nobody having seen it first.
            // This endpoint accepted a picture from anyone without a session as long as ProfileId was
            // empty, so it was the widest hole of the two.
            if (newProfile.Avatar == null || newProfile.Avatar.ID == -1)
                newProfile.Avatar = CProfiles.GetAvatars().First();

            if (!string.IsNullOrEmpty(profile.PlayerName))
                newProfile.PlayerName = profile.PlayerName;
            else if (!string.IsNullOrEmpty(newProfile.PlayerName))
                newProfile.PlayerName = "DummyName";

            if (profile.Difficulty >= 0 && profile.Difficulty <= 2)
                newProfile.Difficulty = (EGameDifficulty)profile.Difficulty;

            if (profile.Type >= 0 && profile.Type <= 1)
            {
                EUserRole option = profile.Type == 0 ? EUserRole.TR_USERROLE_GUEST : EUserRole.TR_USERROLE_NORMAL;
                //Only allow the change of TR_USERROLE_GUEST and TR_USERROLE_NORMAL
                const EUserRole mask = EUserRole.TR_USERROLE_NORMAL;
                newProfile.UserRole = (newProfile.UserRole & mask) | option;
            }

            if (!string.IsNullOrEmpty(profile.Password))
            {
                if (profile.Password == "***__CLEAR_PASSWORD__***")
                {
                    newProfile.PasswordSalt = null;
                    newProfile.PasswordHash = null;
                }
                else
                {
                    byte[] buffer = new byte[32];
                    using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                        rng.GetNonZeroBytes(buffer);
                    byte[] salt = buffer;
                    byte[] hashedPassword = _Hash((new UTF8Encoding()).GetBytes(profile.Password), salt);

                    newProfile.PasswordSalt = salt;
                    newProfile.PasswordHash = hashedPassword;
                }
            }

            if (existingProfile != null)
            {
                CProfiles.EditProfile(newProfile);
                CProfiles.Update();
                CProfiles.SaveProfiles();
            }
            else
                CProfiles.AddProfile(newProfile);

            return true;
        }

        public static SProfileData[] GetProfileList()
        {
            List<SProfileData> result = new List<SProfileData>(CProfiles.NumProfiles);

            result.AddRange(CProfiles.GetProfiles().Select(profile => _CreateProfileData(profile, true)));

            return result.ToArray();
        }

        private static SProfileData _CreateProfileData(CProfile profile, bool isReadonly)
        {
            SProfileData profileData = new SProfileData
                {
                    IsEditable = !isReadonly,
                    ProfileId = profile.ID,
                    PlayerName = profile.PlayerName,
                    //Is TR_USERROLE_GUEST or TR_USERROLE_NORMAL?
                    Type = (profile.UserRole.HasFlag(EUserRole.TR_USERROLE_NORMAL) ? 1 : 0),
                    Difficulty = (int)profile.Difficulty
                };

            CAvatar avatar = profile.Avatar;
            if (avatar != null)
            {
                if (File.Exists(avatar.FileName))
                    profileData.Avatar = new CBase64Image(_CreateDelayedImage(avatar.FileName));
            }
            return profileData;
        }

        /// <summary>
        ///     Currently unused: accepting uploaded avatars was removed (see SendProfileData). Kept so
        ///     the change is a one-line revert if an installation ever wants uploads back.
        /// </summary>
        private static CAvatar _AddAvatar(CBase64Image avatarData)
        {
            try
            {
                string filename = _SaveImage(avatarData, "snapshot", CConfig.ProfileFolders[0]);

                CAvatar avatar = CAvatar.GetAvatar(filename);
                if (avatar != null)
                {
                    CProfiles.AddAvatar(avatar);
                    return avatar;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
        #endregion

        #region photo
        private static readonly List<string> _PhotosOfThisRound = new List<string>();

        public static bool SendPhoto(SPhotoData photoData)
        {
            if (photoData.Photo == null)
                return false;

            string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string filePath = _SaveImage(photoData.Photo, name, CSettings.FolderNamePhotos);
            if (!string.IsNullOrEmpty(filePath))
            {
                _PhotosOfThisRound.Add(filePath);
                return true;
            }

            return false;
        }

        internal static string[] GetPhotosOfThisRound()
        {
            string[] result = _PhotosOfThisRound.ToArray();
            _PhotosOfThisRound.Clear();
            return result;
        }
        #endregion

        #region website
        private static readonly Dictionary<string, string> _DelayedImagePath = new Dictionary<string, string>();

        public static byte[] GetSiteFile(string filename)
        {
            string path = "Website/" + filename;
            path = path.Replace("..", "");

            if (!File.Exists(path))
                return null;

            /*string content = File.ReadAllText(path);

            content = content.Replace("%SERVER%", System.Net.Dns.GetHostName() + ":" + CConfig.ServerPort);


            return Encoding.UTF8.GetBytes(content);*/

            return File.ReadAllBytes(path);
        }

        private static string _CreateDelayedImage(string filename)
        {
            byte[] by = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(filename));
            var sb = new StringBuilder();
            foreach (byte b in by)
                sb.Append(b.ToString("x2"));

            string hashedFilename = sb.ToString();

            if (!_DelayedImagePath.ContainsKey(hashedFilename))
                _DelayedImagePath.Add(hashedFilename, filename);
            return hashedFilename;
        }

        public static string GetServerVersion()
        {
            return CSettings.FullVersion;
        }

        public static CBase64Image GetDelayedImage(string hashedFilename)
        {
            if (!_DelayedImagePath.ContainsKey(hashedFilename))
                throw new FileNotFoundException("Image not found");

            string fileName = _DelayedImagePath[hashedFilename];

            if (File.Exists(fileName))
                return CBase64Image.FromFile(fileName);
            throw new FileNotFoundException("Image not found");
        }
        #endregion

        #region songs
        public static SSongInfo GetSong(int songId)
        {
            CSong song = CSongs.GetSong(songId);
            return _GetSongInfo(song, true);
        }


        private static SSongInfo[] _SongInfoCache = null;
        public static SSongInfo[] GetAllSongs()
        {
            bool sendCovers = CConfig.Config.Server.SongCountCoverThreshold == -1 || CConfig.Config.Server.SongCountCoverThreshold > CSongs.Songs.Count;

            if (_SongInfoCache == null)
            {
                List<CSong> songs = CSongs.Songs;
                _SongInfoCache = (from s in songs
                    select _GetSongInfo(s, sendCovers)).AsParallel().ToArray<SSongInfo>();
            }
            
            return _SongInfoCache;
        }

        public static string GetMp3Path(int songId)
        {
            CSong song = CSongs.GetSong(songId);
            return song.GetMP3();
        }

        public static int GetCurrentSongId()
        {
            CSong song = CGame.GetSong();
            if (song == null)
                return -1;
            return song.ID;
        }

        private static SSongInfo _GetSongInfo(CSong song, bool includeCover)
        {
            SSongInfo result = new SSongInfo();
            if (song != null)
            {
                result.Title = song.Title;
                result.Artist = song.Artist;
                result.Genre = song.Genres.FirstOrDefault();
                result.Language = song.Languages.FirstOrDefault();
                result.Year = song.Year;
                result.IsDuet = song.IsDuet;
                result.SongId = song.ID;
                if (includeCover)
                {
                    if (song.Cover == "")
                    {
                        result.Cover = new CBase64Image(_CreateDelayedImage(Path.Combine("Website", "img", "noCover.png")));
                    }
                    else
                    {
                        result.Cover = new CBase64Image(_CreateDelayedImage(Path.Combine(song.Folder, song.Cover)));
                    }
                }
                    
            }
            return result;
        }
        #endregion

        #region song requests (web queue)

        /// <summary>
        ///     Hands a queue entry to the game: loads the song, seats the singers and jumps into the
        ///     sing screen. <b>Main thread only</b> — call it through DoTask.
        /// </summary>
        public static EStartRequestResult StartSongRequest(int requestId)
        {
            CSongRequest request = CSongRequests.GetById(requestId);
            if (request == null)
                return EStartRequestResult.UnknownRequest;

            // Never start on top of a running song. Replacing CGame's queue while the sing screen is
            // live makes it fail to load the "current" song, call _FinishedSinging and fade again
            // from inside CGraphics._FinishScreenFading -- a re-entrant fade that dies with a
            // NullReferenceException and takes the whole game down. Checking NextScreen too covers
            // an impatient second tap while the first start is still fading in.
            IMenu singScreen = CGraphics.GetScreen(EScreen.Sing);
            if (CGraphics.CurrentScreen == singScreen || CGraphics.NextScreen == singScreen)
                return EStartRequestResult.Busy;

            CSong song = CSongs.GetSong(request.SongId);
            if (song == null)
            {
                CLog.Error("Song request " + requestId + " refers to unknown song id " + request.SongId);
                return EStartRequestResult.SongUnavailable;
            }

            // A duet only works if we actually have one singer per voice; otherwise sing it normally.
            bool asDuet = song.IsDuet && request.SingerProfileIds.Count >= 2;
            EGameMode mode = asDuet ? EGameMode.TR_GAMEMODE_DUET : EGameMode.TR_GAMEMODE_NORMAL;

            CGame.Reset();
            CGame.ClearSongs();
            if (!CGame.AddSongById(request.SongId, mode))
            {
                CLog.Error("Song " + request.SongId + " does not support game mode " + mode);
                return EStartRequestResult.SongUnavailable;
            }

            int numPlayers = request.SingerProfileIds.Count;
            if (numPlayers < 1)
                numPlayers = 1;
            if (numPlayers > CSettings.MaxNumPlayer)
                numPlayers = CSettings.MaxNumPlayer;

            CGame.NumPlayers = numPlayers;
            CConfig.Config.Game.NumPlayers = numPlayers;
            CGame.ResetPlayer();

            for (int i = 0; i < numPlayers; i++)
            {
                Guid profileId = i < request.SingerProfileIds.Count ? request.SingerProfileIds[i] : Guid.Empty;
                CGame.Players[i].ProfileID = CProfiles.IsProfileIDValid(profileId) ? profileId : Guid.Empty;
                // Alternate the voices so singer 1 gets voice 1, singer 2 gets voice 2.
                CGame.Players[i].VoiceNr = asDuet ? i % song.Notes.VoiceCount : 0;
            }

            CSongRequests.MarkPlaying(requestId);
            CGraphics.FadeTo(EScreen.Sing);
            CLog.Information("Started song request " + requestId + " (" + request.Artist + " - " + request.Title + ") for " + numPlayers + " player(s)");
            return EStartRequestResult.Started;
        }

        /// <summary>Song info for a request, resolved on the main thread.</summary>
        public static SSongInfo GetSongInfoForRequest(int songId)
        {
            return _GetSongInfo(CSongs.GetSong(songId), false);
        }

        /// <summary>
        ///     Paged, filtered song list for the web UI. Replaces shipping the entire library on
        ///     every page load.
        /// </summary>
        public static SSongSearchResult SearchSongs(string query, int offset, int limit)
        {
            IEnumerable<CSong> songs = CSongs.AllSongs;

            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                songs = songs.Where(song =>
                                        (song.Title != null && song.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                        || (song.Artist != null && song.Artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            List<CSong> matches = songs.OrderBy(song => song.Artist, StringComparer.OrdinalIgnoreCase)
                                       .ThenBy(song => song.Title, StringComparer.OrdinalIgnoreCase)
                                       .ToList();

            if (offset < 0)
                offset = 0;
            if (limit <= 0 || limit > 200)
                limit = 50;

            return new SSongSearchResult
                {
                    Total = matches.Count,
                    Items = matches.Skip(offset).Take(limit).Select(song => new SSongListEntry
                        {
                            SongId = song.ID,
                            Title = song.Title,
                            Artist = song.Artist,
                            IsDuet = song.IsDuet,
                            Year = song.Year,
                            Genre = song.Genres.FirstOrDefault(),
                            Language = song.Languages.FirstOrDefault()
                        }).ToArray()
                };
        }

        /// <summary>Creates a passwordless guest profile so a visitor can sign up without setup.</summary>
        public static Guid CreateGuestProfile(string playerName, int avatarId)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                return Guid.Empty;

            var profile = new CProfile
                {
                    Active = EOffOn.TR_CONFIG_ON,
                    // Guest, not Normal: these are throwaway profiles created from a phone at a party.
                    // Marking them keeps them distinguishable from the host's real profiles later.
                    UserRole = EUserRole.TR_USERROLE_GUEST,
                    PlayerName = playerName.Trim(),
                    Difficulty = EGameDifficulty.TR_CONFIG_NORMAL
                };

            // Take the picked avatar; fall back to the first one so a profile is never left without
            // a picture (CProfile.AvatarFileName dereferences it when saving).
            IEnumerable<CAvatar> avatars = CProfiles.GetAvatars();
            if (avatars != null)
            {
                CAvatar[] all = avatars.ToArray();
                profile.Avatar = all.FirstOrDefault(a => a.ID == avatarId) ?? all.FirstOrDefault();
            }

            CProfiles.AddProfile(profile);
            CProfiles.Update();
            CProfiles.SaveProfiles();

            // AddProfile goes through a queue and assigns the ID on Update(), so look it up by name.
            CProfile created = CProfiles.GetProfiles()
                                        .LastOrDefault(p => p.PlayerName == profile.PlayerName);
            return created == null ? Guid.Empty : created.ID;
        }

        /// <summary>
        ///     Changes a profile's difficulty. CGame reads it live per note
        ///     (<see cref="CProfiles.GetDifficulty" />), so this takes effect immediately -- even for a
        ///     song that is already running.
        /// </summary>
        public static bool SetProfileDifficulty(Guid profileId, int difficulty)
        {
            if (difficulty < 0 || difficulty > 2)
                return false;

            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return false;

            // Copy every field across. EditProfile replaces the stored profile wholesale, so anything
            // left out here is lost -- including the password hash, which would silently unlock a
            // protected profile. (The old SendProfileData path has exactly that bug.)
            var updated = new CProfile
                {
                    ID = profile.ID,
                    FilePath = profile.FilePath,
                    PlayerName = profile.PlayerName,
                    Avatar = profile.Avatar,
                    UserRole = profile.UserRole,
                    Active = profile.Active,
                    PasswordHash = profile.PasswordHash,
                    PasswordSalt = profile.PasswordSalt,
                    Difficulty = (EGameDifficulty)difficulty
                };

            CProfiles.EditProfile(updated);
            CProfiles.Update();
            CProfiles.SaveProfiles();
            CLog.Information("Profile " + profile.PlayerName + ": difficulty set to " + (EGameDifficulty)difficulty);
            return true;
        }

        #region PIN

        /// <summary>Whether a profile is claimed by a PIN.</summary>
        public static bool HasPin(Guid profileId)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            return profile != null && profile.PasswordHash != null;
        }

        public static bool IsAdminProfile(Guid profileId)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            return profile != null && profile.UserRole.HasFlag(EUserRole.TR_USERROLE_ADMIN);
        }

        /// <summary>Digits only, so phones show a number pad and nobody types a real password here.</summary>
        private static bool _IsValidPin(string pin)
        {
            return !string.IsNullOrEmpty(pin) && pin.Length >= 4 && pin.Length <= 10 && pin.All(char.IsDigit);
        }

        /// <summary>
        ///     Sets, changes or clears the PIN of a profile. An empty <paramref name="newPin" /> clears it.
        /// </summary>
        public static EPinResult SetProfilePin(Guid profileId, string currentPin, string newPin)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return EPinResult.UnknownProfile;

            bool hasPin = profile.PasswordHash != null;
            bool isAdmin = profile.UserRole.HasFlag(EUserRole.TR_USERROLE_ADMIN);

            // The double floor: an admin profile without a PIN cannot give itself one. Otherwise
            // anyone who walks into such a profile could claim it and inherit the rights with it.
            // Admin profiles get their first PIN before the role is granted, by hand.
            if (isAdmin && !hasPin)
                return EPinResult.AdminWithoutPinLocked;

            // Changing an existing PIN requires the old one — a stolen session must not be enough.
            if (hasPin && !ValidatePassword(profileId, currentPin ?? ""))
                return EPinResult.WrongCurrentPin;

            if (string.IsNullOrEmpty(newPin))
            {
                // Clearing would silently disarm the rights, which nobody would ever connect to the
                // missing PIN. Refuse it outright instead.
                if (isAdmin)
                    return EPinResult.AdminNeedsPin;

                profile.PasswordHash = null;
                profile.PasswordSalt = null;
            }
            else
            {
                if (!_IsValidPin(newPin))
                    return EPinResult.InvalidPin;

                byte[] salt = new byte[32];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                    rng.GetNonZeroBytes(salt);

                profile.PasswordSalt = salt;
                profile.PasswordHash = _Hash(new UTF8Encoding().GetBytes(newPin), salt);
            }

            CProfiles.SaveProfiles();
            CLog.Information("Profile " + profile.PlayerName + ": PIN "
                             + (string.IsNullOrEmpty(newPin) ? "cleared" : (hasPin ? "changed" : "set")));
            return EPinResult.Ok;
        }

        #endregion

        #region avatars

        /// <summary>
        ///     The avatars shipped with Vocaluxe. Deliberately the only source the web UI offers:
        ///     guests pick from these instead of uploading their own, so nothing ends up on the
        ///     karaoke box (or on the beamer) that nobody vetted.
        /// </summary>
        public static SAvatarListEntry[] GetAvatarList()
        {
            IEnumerable<CAvatar> avatars = CProfiles.GetAvatars();
            if (avatars == null)
                return new SAvatarListEntry[0];

            return avatars.Select(a => new SAvatarListEntry
                {
                    AvatarId = a.ID,
                    Name = a.GetDisplayName()
                }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>File behind an avatar id, or null. The client never names a path itself.</summary>
        public static string GetAvatarFilePath(int avatarId)
        {
            IEnumerable<CAvatar> avatars = CProfiles.GetAvatars();
            if (avatars == null)
                return null;

            CAvatar avatar = avatars.FirstOrDefault(a => a.ID == avatarId);
            return avatar == null ? null : avatar.FileName;
        }

        public static bool SetProfileAvatar(Guid profileId, int avatarId)
        {
            if (!CProfiles.IsProfileIDValid(profileId) || !CProfiles.IsAvatarIDValid(avatarId))
                return false;

            CProfiles.SetAvatar(profileId, avatarId);
            CProfiles.SaveProfiles();
            return true;
        }

        #endregion

        /// <summary>
        ///     Profile list for the web UI. Separate from <see cref="GetProfileList" />, which builds a
        ///     base64 image reference per profile — the new UI only needs an avatar id and fetches the
        ///     picture itself, cached.
        /// </summary>
        public static SProfileListEntry[] GetProfileListForWeb()
        {
            return CProfiles.GetProfiles()
                            .Where(p => p.Active == EOffOn.TR_CONFIG_ON)
                            .Select(p => new SProfileListEntry
                                {
                                    ProfileId = p.ID.ToString(),
                                    PlayerName = p.PlayerName,
                                    IsGuest = !p.UserRole.HasFlag(EUserRole.TR_USERROLE_NORMAL),
                                    HasPin = p.PasswordHash != null,
                                    Difficulty = (int)p.Difficulty,
                                    AvatarId = p.Avatar == null ? -1 : p.Avatar.ID
                                })
                            .OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
        }

        /// <summary>Name and difficulty of one profile, without loading its avatar.</summary>
        public static SProfileListEntry GetProfileSummary(Guid profileId)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return null;

            return new SProfileListEntry
                {
                    ProfileId = profile.ID.ToString(),
                    PlayerName = profile.PlayerName,
                    IsGuest = !profile.UserRole.HasFlag(EUserRole.TR_USERROLE_NORMAL),
                    HasPin = profile.PasswordHash != null,
                    Difficulty = (int)profile.Difficulty,
                    AvatarId = profile.Avatar == null ? -1 : profile.Avatar.ID
                };
        }

        public static bool IsSongLibraryReady()
        {
            return CSongs.AllSongs.Count > 0;
        }

        #endregion

        #region playlist
        public static SPlaylistData[] GetPlaylists()
        {
            return (from p in CPlaylists.Playlists
                    select _GetPlaylistInfo(p)).ToArray();
        }

        public static SPlaylistData GetPlaylist(int playlistId)
        {
            if (CPlaylists.Get(playlistId) == null)
                throw new ArgumentException("invalid playlistId");
            return _GetPlaylistInfo(CPlaylists.Get(playlistId));
        }

        public static void AddSongToPlaylist(int songId, int playlistId, bool allowDuplicates)
        {
            if (CPlaylists.Get(playlistId) == null)
                throw new ArgumentException("invalid playlistId");

            if (allowDuplicates || !PlaylistContainsSong(songId, playlistId))
            {
                CPlaylists.AddSong(playlistId, songId);
                CPlaylists.Save(playlistId);
            }
            else
                throw new ArgumentException("song exists in this playlist");
        }

        public static void RemoveSongFromPlaylist(int position, int playlistId, int songId)
        {
            CPlaylistFile pl = CPlaylists.Get(playlistId);
            if (pl == null)
                throw new ArgumentException("invalid playlistId");
            if (!PlaylistContainsSong(songId, playlistId))
                throw new ArgumentException("invalid songId");
            if (position < 0 || pl.Songs.Count <= position
                || pl.Songs[position].SongID != songId)
                throw new ArgumentException("invalid position");
            pl.DeleteSong(position);
            pl.Save();
        }

        public static void MoveSongInPlaylist(int newPosition, int playlistId, int songId)
        {
            CPlaylistFile pl = CPlaylists.Get(playlistId);
            if (pl == null)
                throw new ArgumentException("invalid playlistId");
            if (!PlaylistContainsSong(songId, playlistId))
                throw new ArgumentException("invalid songId");

            if (pl.Songs.Count < newPosition)
                throw new ArgumentException("invalid newPosition");

            int oldPosition = pl.Songs.FindIndex(s => s.SongID == songId);
            pl.MoveSong(oldPosition, newPosition);
            pl.Save();
        }

        public static bool PlaylistContainsSong(int songId, int playlistId)
        {
            CPlaylistFile pl = CPlaylists.Get(playlistId);
            if (pl == null)
                throw new ArgumentException("invalid playlistId");
            return pl.Songs.Any(s => s.SongID == songId);
        }

        public static SPlaylistSongInfo[] GetPlaylistSongs(int playlistId)
        {
            CPlaylistFile pl = CPlaylists.Get(playlistId);
            if (pl == null)
                throw new ArgumentException("invalid playlistId");

            return _GetPlaylistSongInfos(pl);
        }

        private static SPlaylistSongInfo _GetPlaylistSongInfo(CPlaylistSong playlistSong, int playlistId, int playlistPos)
        {
            SPlaylistSongInfo result = new SPlaylistSongInfo();
            if (playlistSong != null)
            {
                result.PlaylistId = playlistId;
                result.GameMode = (int)playlistSong.GameMode;
                result.PlaylistPosition = playlistPos;
                result.Song = _GetSongInfo(CSongs.GetSong(playlistSong.SongID), true);
            }
            return result;
        }

        private static SPlaylistSongInfo[] _GetPlaylistSongInfos(CPlaylistFile playlist)
        {
            SPlaylistSongInfo[] result = new SPlaylistSongInfo[playlist.Songs.Count];
            for (int i = 0; i < playlist.Songs.Count; i++)
                result[i] = _GetPlaylistSongInfo(playlist.Songs[i], playlist.Id, i);
            return result;
        }

        private static SPlaylistData _GetPlaylistInfo(CPlaylistFile playlist)
        {
            return new SPlaylistData
                {
                    PlaylistId = playlist.Id,
                    PlaylistName = playlist.Name,
                    SongCount = playlist.Songs.Count,
                    LastChanged = DateTime.Now.ToLongDateString()
                };
        }

        public static void RemovePlaylist(int playlistId)
        {
            if (CPlaylists.Get(playlistId) == null)
                throw new ArgumentException("invalid playlistId");
            CPlaylists.Delete(playlistId);
        }

        public static int AddPlaylist(string playlistName)
        {
            int newPlaylistId = CPlaylists.NewPlaylist(playlistName);
            CPlaylists.Save(newPlaylistId);

            return newPlaylistId;
        }
        #endregion

        #region user management
        public static bool ValidatePassword(Guid profileId, string password)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return false;

            if (profile.PasswordHash == null)
            {
                if (string.IsNullOrEmpty(password))
                    return true; //Allow empty passwords
                return false;
            }

            byte[] salt = profile.PasswordSalt;
            return _Hash((new UTF8Encoding()).GetBytes(password), salt).SequenceEqual(profile.PasswordHash);
        }

        public static bool ValidatePassword(Guid profileId, byte[] hashedPassword)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                return false;

            if (profile.PasswordHash == null)
            {
                if (hashedPassword == null)
                    return true; //Allow empty passwords
                return false;
            }

            //byte[] salt = profile.PasswordSalt;
            return hashedPassword.SequenceEqual(profile.PasswordHash);
        }

        private static byte[] _GetPasswordSalt(Guid profileId)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                throw new ArgumentException("Invalid profileId");

            if (profile.PasswordHash == null)
                throw new ArgumentException("Empty password");

            return profile.PasswordSalt;
        }

        public static int GetUserRole(Guid profileId)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                throw new ArgumentException("Invalid profileId");

            //Hide TR_USERROLE_GUEST and TR_USERROLE_NORMAL
            //const EUserRole mask = (EUserRole.TR_USERROLE_GUEST | EUserRole.TR_USERROLE_NORMAL);

            return (int)(profile.UserRole);
        }

        public static void SetUserRole(Guid profileId, int userRole)
        {
            CProfile profile = CProfiles.GetProfile(profileId);
            if (profile == null)
                throw new ArgumentException("Invalid profileId");

            var option = (EUserRole)userRole;

            //Only allow the change of all options exept TR_USERROLE_GUEST and TR_USERROLE_NORMAL
            const EUserRole mask = (EUserRole.TR_USERROLE_GUEST | EUserRole.TR_USERROLE_NORMAL);
            option &= ~mask;

            profile.UserRole = (profile.UserRole & mask) | option;

            CProfiles.EditProfile(profile);
            CProfiles.Update();
            CProfiles.SaveProfiles();
        }

        public static Guid GetUserIdFromUsername(string username)
        {
            IEnumerable<Guid> playerIds = (from p in CProfiles.GetProfiles()
                                          where String.Equals(p.PlayerName, username, StringComparison.OrdinalIgnoreCase)
                                          select p.ID);
            return playerIds.FirstOrDefault();
        }

        private static byte[] _Hash(byte[] password, byte[] salt)
        {
            byte[] data = new byte[password.Length + salt.Length];

            password.CopyTo(data, 0);
            salt.CopyTo(data, password.Length);

            return SHA256.HashData(data);
        }
        #endregion

        private static string _SaveImage(CBase64Image imageDate, string name, string folder)
        {
            string extension = imageDate.GetImageType();

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string file = CHelper.GetUniqueFileName(folder, name + "." + extension);

            imageDate.SaveTo(file);
            return file;
        }
    }
}