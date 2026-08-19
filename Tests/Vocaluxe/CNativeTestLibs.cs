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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     libacinerella.so is built into Output/, not next to the test binary. This points the loader
    ///     at it so the comparison tests run from a plain "dotnet test".
    /// </summary>
    static class CNativeTestLibs
    {
        private static bool _Tried;
        private static bool _Available;

        public static bool AcinerellaAvailable(Assembly assembly)
        {
            if (_Tried)
                return _Available;
            _Tried = true;

            string dir = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "Output", "libacinerella.so");
                if (File.Exists(candidate))
                {
                    NativeLibrary.SetDllImportResolver(assembly,
                                                       (name, asm, path) =>
                                                           name.IndexOf("acinerella", StringComparison.OrdinalIgnoreCase) >= 0
                                                               ? NativeLibrary.Load(candidate)
                                                               : IntPtr.Zero);
                    _Available = true;
                    return true;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
    }
}
