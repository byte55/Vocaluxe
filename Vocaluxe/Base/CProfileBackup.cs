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
using System.Globalization;
using System.IO;
using System.Linq;
using VocaluxeLib.Log;

namespace Vocaluxe.Base
{
    /// <summary>
    ///     Keeps dated copies of the user's profiles, made once per day at startup.
    ///
    ///     Profiles are the one thing here that cannot be rebuilt: guests create them from their
    ///     phones, and anyone signed in can rename or re-picture the profile they are using. A joker
    ///     going through the open guest profiles is annoying rather than dangerous — as long as
    ///     yesterday's state is still around.
    /// </summary>
    static class CProfileBackup
    {
        private const string _FolderName = "ProfileBackups";
        private const string _DateFormat = "yyyy-MM-dd";

        /// <summary>
        ///     Backups to keep. They are a few KB each, so this is about not letting the folder grow
        ///     forever, not about disk space.
        /// </summary>
        private const int _KeepCount = 30;

        /// <summary>
        ///     Copies the profile folder into a dated subfolder, unless today's copy already exists.
        ///     Call before <see cref="CProfiles.Init" /> so what gets saved is the state the last
        ///     session left behind.
        /// </summary>
        public static void Run()
        {
            try
            {
                // [0] is the user folder — the only one that ever changes. [1] ships with the build.
                string source = CConfig.ProfileFolders.FirstOrDefault();
                if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
                    return;

                if (!Directory.EnumerateFileSystemEntries(source).Any())
                    return;

                string root = Path.Combine(CSettings.DataFolder, _FolderName);
                string target = Path.Combine(root, DateTime.Now.ToString(_DateFormat, CultureInfo.InvariantCulture));

                // Dating the folder is what makes this work: the copy made the morning after an
                // incident lands next to yesterday's good one instead of overwriting it.
                if (Directory.Exists(target))
                    return;

                _CopyTree(source, target);
                CLog.Information("Profile backup written to " + target);

                _RemoveOldBackups(root);
            }
            catch (Exception e)
            {
                // A failed backup must never keep the game from starting.
                CLog.Error(e, "Could not back up the profiles");
            }
        }

        private static void _CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);

            foreach (string dir in Directory.GetDirectories(source))
                _CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        private static void _RemoveOldBackups(string root)
        {
            // Sort by the folder name, which is the date — no need to trust file timestamps.
            string[] backups = Directory.GetDirectories(root)
                                        .Select(Path.GetFileName)
                                        .Where(_IsBackupName)
                                        .OrderByDescending(name => name, StringComparer.Ordinal)
                                        .ToArray();

            foreach (string old in backups.Skip(_KeepCount))
            {
                try
                {
                    Directory.Delete(Path.Combine(root, old), true);
                    CLog.Information("Removed old profile backup " + old);
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Could not remove old profile backup " + old);
                }
            }
        }

        /// <summary>Only touch folders this class made, never anything else living there.</summary>
        private static bool _IsBackupName(string name)
        {
            DateTime parsed;
            return DateTime.TryParseExact(name, _DateFormat, CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out parsed);
        }
    }
}
