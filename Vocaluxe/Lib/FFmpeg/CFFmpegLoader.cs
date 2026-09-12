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
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.FFmpeg
{
    /// <summary>
    ///     Finds the ffmpeg shared libraries and points FFmpeg.AutoGen at them.
    /// </summary>
    /// <remarks>
    ///     Nothing is shipped with Vocaluxe: on Linux these come from the distribution, on macOS
    ///     from Homebrew. AutoGen wants a directory rather than letting the system loader do its job,
    ///     so we look for the exact sonames it was generated against - a different major version would
    ///     be an ABI mismatch, not something to paper over.
    /// </remarks>
    static class CFFmpegLoader
    {
        private static bool _Tried;
        private static bool _Available;

        /// <summary>
        ///     True once the libraries were located. Safe to call repeatedly; the search happens once.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_Tried)
                {
                    _Tried = true;
                    _Available = _Locate();
                }
                return _Available;
            }
        }

        private static bool _Locate()
        {
            string expected = _SharedLibraryName("avformat");

            foreach (string dir in _Candidates())
            {
                try
                {
                    if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, expected)))
                        continue;
                }
                catch (Exception)
                {
                    continue; // unreadable path, just try the next one
                }

                ffmpeg.RootPath = dir;
                try
                {
                    // Touching a function forces the bindings to bind, so a mismatch shows up here
                    // and not somewhere in the middle of a song.
                    string version = ffmpeg.av_version_info();
                    _RedirectLog();
                    CLog.Information("Using ffmpeg " + version + " from " + dir);
                    return true;
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Found " + expected + " in " + dir + " but could not bind to it");
                    return false;
                }
            }

            CLog.Error("No usable ffmpeg found (looking for " + expected + "). Falling back to the Acinerella decoder.");
            return false;
        }

        /// <summary>Turns an ffmpeg return code into the message ffmpeg has for it.</summary>
        public static unsafe string ErrorText(int error)
        {
            const int bufferSize = 256;
            byte* buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, bufferSize);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString();
        }

        // Held in a field: ffmpeg keeps calling this for the rest of the process, so the delegate
        // must not be collected.
        private static av_log_set_callback_callback _LogCallback;

        /// <summary>
        ///     Sends ffmpeg's own messages to the Vocaluxe log. By default the C library writes them
        ///     to stderr, where nobody sees them - and this program's log is the only place anyone
        ///     ever looks.
        /// </summary>
        private static unsafe void _RedirectLog()
        {
            _LogCallback = (p0, level, format, vl) =>
                {
                    if (level > ffmpeg.av_log_get_level())
                        return;

                    const int bufferSize = 1024;
                    byte* buffer = stackalloc byte[bufferSize];
                    int printPrefix = 1;
                    ffmpeg.av_log_format_line2(p0, level, format, vl, buffer, bufferSize, &printPrefix);
                    string message = Marshal.PtrToStringAnsi((IntPtr)buffer);
                    if (string.IsNullOrWhiteSpace(message))
                        return;
                    message = "ffmpeg: " + message.TrimEnd();

                    if (level <= ffmpeg.AV_LOG_ERROR)
                        CLog.Error(message);
                    else if (level <= ffmpeg.AV_LOG_WARNING)
                        CLog.Warning(message);
                    else
                        CLog.Information(message);
                };

            // Below warning it is a running commentary on every frame.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            ffmpeg.av_log_set_callback(_LogCallback);
        }

        /// <summary>
        ///     The file name to look for: a versioned soname on Linux, a versioned dylib on macOS.
        ///     Same spelling FFmpeg.AutoGen's own loader uses, so finding the file means it can bind.
        /// </summary>
        private static string _SharedLibraryName(string library)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                       ? "lib" + library + "." + ffmpeg.LibraryVersionMap[library] + ".dylib"
                       : "lib" + library + ".so." + ffmpeg.LibraryVersionMap[library];
        }

        private static IEnumerable<string> _Candidates()
        {
            // Anything the user set explicitly wins.
            string configured = Environment.GetEnvironmentVariable("VOCALUXE_FFMPEG_PATH");
            if (!string.IsNullOrEmpty(configured))
                yield return configured;

            // The dynamic loader's search path variable goes by a different name on macOS.
            bool osx = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string ldPath = Environment.GetEnvironmentVariable(osx ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH");
            if (!string.IsNullOrEmpty(ldPath))
            {
                foreach (string dir in ldPath.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrEmpty(dir))
                        yield return dir;
                }
            }

            if (osx)
            {
                // Homebrew, which is where a Mac gets ffmpeg from: /opt/homebrew on Apple Silicon,
                // /usr/local on Intel. ffmpeg is not keg-only, so the versioned dylibs are symlinked
                // into the prefix's lib directory; the opt/ffmpeg paths are the fallback for a
                // keg-only install (e.g. a pinned version).
                yield return "/opt/homebrew/lib";
                yield return "/opt/homebrew/opt/ffmpeg/lib";
                yield return "/usr/local/lib";
                yield return "/usr/local/opt/ffmpeg/lib";
                yield break;
            }

            // Debian/Ubuntu put them in a per-architecture directory, everyone else in lib64 or lib.
            string triplet = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                                 ? "aarch64-linux-gnu"
                                 : "x86_64-linux-gnu";
            yield return "/usr/lib/" + triplet;
            yield return "/usr/lib64";
            yield return "/usr/lib";
            yield return "/usr/local/lib/" + triplet;
            yield return "/usr/local/lib";
        }
    }
}
