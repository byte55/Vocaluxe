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

using System.IO;
using NUnit.Framework;
using SkiaSharp;

namespace Tests.Vocaluxe
{
    /// <summary>
    ///     Headless coverage for the GDI+ -> SkiaSharp image migration (#768). Proves the native
    ///     libSkiaSharp loads and rasterizes - the unit suite otherwise only exercises
    ///     platform-independent VocaluxeLib logic, none of the ported subsystems.
    ///     The two CBase64Image round-trip tests went with the old web API: that type existed only to
    ///     ship covers and uploaded photos as base64, and neither path is left.
    /// </summary>
    [TestFixture]
    public class CImagingTest
    {
        private static byte[] _MakePng(int width, int height, SKColor fill)
        {
            using (var bmp = new SKBitmap(width, height))
            {
                bmp.Erase(fill);
                using (var img = SKImage.FromBitmap(bmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                    return data.ToArray();
            }
        }

        [Test]
        public void SkiaSharp_RasterizesText_ProducesVisiblePixels()
        {
            // Mirrors what the font renderer (CGlyph/CFontStyle) relies on: rasterizing text via SkiaSharp.
            // A successful render proves the SkiaSharp font stack works on this OS (fontconfig/CoreText/GDI).
            using (var bmp = new SKBitmap(160, 48))
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
                using (var font = new SKFont(SKTypeface.Default, 28))
                    canvas.DrawText("Vocaluxe", 4, 34, SKTextAlign.Left, font, paint);

                bool drewSomething = false;
                for (int y = 0; y < bmp.Height && !drewSomething; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y) != SKColors.White)
                        {
                            drewSomething = true;
                            break;
                        }

                Assert.IsTrue(drewSomething, "drawing text should produce non-background pixels");
            }
        }
    }
}
