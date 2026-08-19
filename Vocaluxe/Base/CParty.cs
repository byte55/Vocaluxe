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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vocaluxe.Base.ThemeSystem;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;
using VocaluxeLib.PartyModes;
using VocaluxeLib.Xml;

namespace Vocaluxe.Base
{
    static class CParty
    {
        private const int _PartyModeSystemVersion = 2;

        private static readonly Dictionary<int, SPartyMode> _PartyModes = new Dictionary<int, SPartyMode>();
        private static int _NextID;
        private static IPartyMode _CurrentPartyMode;

        #region public stuff
        public static bool Init(Action<float> onProgress = null)
        {
            if (_PartyModes.Count > 0)
                return false; //Already initialized
            SPartyMode pm = new SPartyMode
                {
                    Info = new SPartyModeInfos {Author = "Vocaluxe Team", Description = "Normal game", Name = "Normal", TargetAudience = "Just a normal game for everyone"},
                    PartyMode = new CPartyModeNormal(-1),
                    PartyModeSystemVersion = _PartyModeSystemVersion,
                    ScreenFiles = new List<string>()
                };
            _PartyModes.Add(-1, pm);
            _CurrentPartyMode = pm.PartyMode;
            Debug.Assert(_CurrentPartyMode != null && _CurrentPartyMode.ID == -1);

            //load other party modes
            _LoadPartyModes(onProgress);
            return _CurrentPartyMode.Init();
        }

        public static int CurrentPartyModeID
        {
            get { return _CurrentPartyMode.ID; }
            set
            {
                if (_CurrentPartyMode.ID == value) {
                    _CurrentPartyMode.SetDefaults();
                    return;
                }
                if (value != -1 && !_PartyModes.ContainsKey(value))
                    throw new ArgumentException("Partymode with ID=" + value + " does not exist!");
                IPartyMode pm = _PartyModes[value].PartyMode;
                if (pm.Init())
                    _CurrentPartyMode = pm;
                else
                {
                    CLog.Error("Could not init PartyMode \"" + _PartyModes[value].Info.Name + "\"! Removing...", true);
                    _PartyModes.Remove(value);
                }
            }
        }

        public static int Count
        {
            get { return _PartyModes.Count; }
        }

        public static void ReloadTheme()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.ReloadTheme();
        }

        public static void ReloadSkin()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.ReloadSkin();
        }

        public static void SaveThemes()
        {
            foreach (SPartyMode pm in _PartyModes.Values)
                pm.PartyMode.SaveScreens();
        }

        public static List<SPartyModeInfos> GetPartyModeInfos()
        {
            List<SPartyModeInfos> list = new List<SPartyModeInfos>();
            foreach (SPartyMode pm in _PartyModes.Values)
            {
                if (pm.PartyMode.ID >= 0)
                    list.Add(pm.Info);
            }
            return list;
        }

        public static void SetNormalGameMode()
        {
            CSongs.ResetPartySongSung();
            CurrentPartyModeID = -1;
        }

        public static void SetPartyMode(int partyModeID)
        {
            CurrentPartyModeID = partyModeID;
        }
        #endregion public stuff

        #region Interface
        public static void UpdateGame()
        {
            _CurrentPartyMode.UpdateGame();
        }

        public static IMenu GetStartScreen()
        {
            return _CurrentPartyMode.GetStartScreen();
        }

        public static SScreenSongOptions GetSongSelectionOptions()
        {
            return _CurrentPartyMode.GetScreenSongOptions();
        }

        public static void OnSongChange(int songIndex, ref SScreenSongOptions screenSongOptions)
        {
            _CurrentPartyMode.OnSongChange(songIndex, ref screenSongOptions);
        }

        public static void OnCategoryChange(int categoryIndex, ref SScreenSongOptions screenSongOptions)
        {
            _CurrentPartyMode.OnCategoryChange(categoryIndex, ref screenSongOptions);
        }

        public static void SetSearchString(string searchString, bool visible)
        {
            _CurrentPartyMode.SetSearchString(searchString, visible);
        }

        public static void JokerUsed(int teamNr)
        {
            _CurrentPartyMode.JokerUsed(teamNr);
        }

        public static void SongSelected(int songID)
        {
            _CurrentPartyMode.SongSelected(songID);
        }

        public static void FinishedSinging()
        {
            _CurrentPartyMode.FinishedSinging();
        }

        public static void LeavingScore()
        {
            _CurrentPartyMode.LeavingScore();
        }

        public static void LeavingHighscore()
        {
            _CurrentPartyMode.LeavingHighscore();
        }
        #endregion Interface

        #region private stuff
        private static void _LoadPartyModes(Action<float> onProgress = null)
        {
            var files = new List<string>();
            files.AddRange(CHelper.ListFiles(CSettings.FolderNamePartyModes, "*.xml", false, true));

            for (int i = 0; i < files.Count; i++)
            {
                // Each party mode is compiled from source (Roslyn) here, which is the slowest part of
                // startup - report progress per mode so the splash bar advances instead of freezing.
                if (onProgress != null)
                    onProgress(files.Count == 0 ? 1f : i / (float)files.Count);

                SPartyMode pm;
                if (_LoadPartyMode(files[i], out pm))
                    _PartyModes.Add(pm.PartyMode.ID, pm);
            }
            if (onProgress != null)
                onProgress(1f);
        }

        private static bool _LoadPartyMode(string filePath, out SPartyMode pm)
        {
            CXmlDeserializer deser = new CXmlDeserializer();
            try
            {
                pm = deser.Deserialize<SPartyMode>(filePath);
                if (pm.PartyModeSystemVersion != _PartyModeSystemVersion)
                    throw new Exception("Wrong PartyModeSystemVersion " + pm.PartyModeSystemVersion + " expected: " + _PartyModeSystemVersion);

                if (pm.ScreenFiles.Count == 0)
                    throw new Exception("No ScreenFiles found");
            }
            catch (Exception e)
            {
                pm = new SPartyMode();
                CLog.Error("Error loading PartyMode file " + filePath + ": " + e.Message);
                return false;
            }

            string pathToPm = Path.Combine(CSettings.ProgramFolder, CSettings.FolderNamePartyModes, pm.Info.Folder);
            string pathToCode = Path.Combine(pathToPm, CSettings.FolderNamePartyModeCode);

            var filesToCompile = new List<string>();
            filesToCompile.AddRange(CHelper.ListFiles(pathToCode, "*.cs", false, true));

            Assembly output = _CompileFiles(filesToCompile.ToArray());
            if (output == null)
                return false;

            object instance = output.CreateInstance(typeof(IPartyMode).Namespace + "." + pm.Info.Folder + "." + pm.Info.PartyModeFile, false,
                                                    BindingFlags.Public | BindingFlags.Instance, null, new object[] {_NextID++}, null, null);
            if (instance == null)
            {
                CLog.Error("Error creating Instance of PartyMode file: " + filePath);
                return false;
            }

            try
            {
                pm.PartyMode = (IPartyMode)instance;
            }
            catch (Exception e)
            {
                CLog.Error("Error casting PartyMode file: " + filePath + "; " + e.Message);
                return false;
            }

            if (!CLanguage.LoadPartyLanguageFiles(pm.PartyMode.ID, Path.Combine(pathToPm, CSettings.FolderNamePartyModeLanguages)))
            {
                CLog.Error("Error loading language files for PartyMode: " + filePath);
                return false;
            }

            if (!CThemes.ReadThemesFromFolder(Path.Combine(pathToPm, CSettings.FolderNameThemes), pm.PartyMode.ID))
                return false;

            if (!CThemes.LoadPartymodeTheme(pm.PartyMode.ID))
                return false;

            foreach (string screenfile in pm.ScreenFiles)
            {
                CMenuParty screen = _GetPartyScreenInstance(output, screenfile, pm.Info.Folder);

                if (screen != null)
                {
                    screen.AssignPartyMode(pm.PartyMode);
                    pm.PartyMode.AddScreen(screen, screenfile);
                }
                else
                    return false;
            }
            pm.PartyMode.LoadTheme();
            pm.Info.ExtInfo = pm.PartyMode;
            return true;
        }

        // Built once and shared by every party mode: collecting the reference set walks the whole
        // trusted-platform list and opens each assembly, which is pointless to repeat per mode.
        private static List<MetadataReference> _References;

        /// <summary>
        ///     Where a compiled party mode is kept so the next start can skip Roslyn. Keyed by the
        ///     sources themselves, so editing a party mode simply misses the cache.
        /// </summary>
        private static string _CacheFile(string key)
        {
            return Path.Combine(CSettings.DataFolder, "PartyModeCache", key + ".dll");
        }

        private static string _SourceKey(string[] files)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                // The runtime version matters as much as the sources: an assembly emitted against a
                // different framework must not be reused.
                sb.Append(Environment.Version).Append('\n');
                sb.Append(typeof(CParty).Assembly.ManifestModule.ModuleVersionId).Append('\n');
                foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
                {
                    sb.Append(file).Append('\n');
                    try
                    {
                        sb.Append(File.ReadAllText(file)).Append('\n');
                    }
                    catch (Exception)
                    {
                        return null; // unreadable source -> no caching, the compile step will report it
                    }
                }
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");
            }
        }

        private static Assembly _CompileFiles(string[] files)
        {
            if (files == null || files.Length == 0)
                return null;

            string cacheKey = _SourceKey(files);
            if (cacheKey != null)
            {
                try
                {
                    string cached = _CacheFile(cacheKey);
                    if (File.Exists(cached))
                        return Assembly.Load(File.ReadAllBytes(cached));
                }
                catch (Exception e)
                {
                    // A broken cache entry must never keep a party mode from loading.
                    CLog.Error(e, "Could not use the cached party mode; compiling from source");
                }
            }

            // Parse all party-mode source files.
            var syntaxTrees = new List<SyntaxTree>();
            foreach (string file in files)
            {
                try
                {
                    syntaxTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file));
                }
                catch (Exception e)
                {
                    CLog.Error("Error reading party-mode source '" + file + "': " + e.Message);
                    return null;
                }
            }

            // Reference the same assemblies the host already has (framework + VocaluxeLib + ...). Use
            // TRUSTED_PLATFORM_ASSEMBLIES so this also works for the self-contained publish, and add the
            // loaded assemblies' locations on top. Dedup by path to avoid duplicate-identity errors.
            if (_References != null)
                return _Emit(files, syntaxTrees, cacheKey);

            var refPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            {
                foreach (string p in tpa.Split(Path.PathSeparator))
                {
                    if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        refPaths.Add(p);
                }
            }
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                    refPaths.Add(asm.Location);
            }

            var references = new List<MetadataReference>();
            foreach (string path in refPaths)
            {
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch (Exception) { /* skip unreadable reference */ }
            }

            _References = references;
            return _Emit(files, syntaxTrees, cacheKey);
        }

        /// <summary>
        ///     Every rebuild of Vocaluxe invalidates the old entries, so without this the folder would
        ///     just keep growing. Two modes per build, so ten files cover the last few builds.
        /// </summary>
        private static void _PruneCache(string folder)
        {
            const int keep = 10;
            FileInfo[] entries = new DirectoryInfo(folder).GetFiles("*.dll");
            if (entries.Length <= keep)
                return;
            foreach (FileInfo old in entries.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                try { old.Delete(); }
                catch (Exception) { /* still in use or gone already - not worth reporting */ }
            }
        }

        private static Assembly _Emit(string[] files, List<SyntaxTree> syntaxTrees, string cacheKey)
        {
            var compilation = CSharpCompilation.Create(
                "VocaluxePartyMode_" + _NextID,
                syntaxTrees,
                _References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                if (!result.Success)
                {
                    foreach (Diagnostic d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                        CLog.Error("Error compiling party-mode source (" + CHelper.ListStrings(files) + "): " + d);
                    return null;
                }
                byte[] assemblyData = ms.ToArray();

                if (cacheKey != null)
                {
                    try
                    {
                        string cached = _CacheFile(cacheKey);
                        Directory.CreateDirectory(Path.GetDirectoryName(cached));
                        File.WriteAllBytes(cached + ".tmp", assemblyData);
                        File.Move(cached + ".tmp", cached, true);
                        _PruneCache(Path.GetDirectoryName(cached));
                    }
                    catch (Exception e)
                    {
                        // Only costs us the shortcut on the next start.
                        CLog.Error(e, "Could not cache the compiled party mode");
                    }
                }
                return Assembly.Load(assemblyData);
            }
        }

        private static CMenuParty _GetPartyScreenInstance(Assembly assembly, string screenName, string partyModeName)
        {
            if (assembly == null)
                return null;

            object instance = assembly.CreateInstance(typeof(IPartyMode).Namespace + "." + partyModeName + "." + screenName);
            if (instance == null)
            {
                CLog.Error("Error creating Instance of PartyScreen: " + screenName);
                return null;
            }

            CMenuParty screen;
            try
            {
                screen = (CMenuParty)instance;
            }
            catch (Exception e)
            {
                CLog.Error("Error casting PartyScreen: " + screenName + "; " + e.Message);
                return null;
            }
            return screen;
        }
        #endregion private stuff
    }
}