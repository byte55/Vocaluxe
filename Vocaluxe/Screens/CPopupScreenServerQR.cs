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
using SkiaSharp;
using Vocaluxe.Base;
using Vocaluxe.Base.Server;
using VocaluxeLib;
using VocaluxeLib.Log;
using VocaluxeLib.Menu;
using VocaluxeLib.Draw;
using QRCoder;

namespace Vocaluxe.Screens
{
    class CPopupScreenServerQR : CMenu
    {
        // Version number for theme files. Increment it, if you've changed something on the theme files!
        protected override int _ScreenVersion
        {
            get { return 2; }
        }

        private CTextureRef _QRServerAddress;
        // What the cached QR was made from. The relay hands out a room code after the screen has
        // already been shown once, and it changes if the relay forgets us - so the picture cannot
        // simply be generated a single time.
        private string _QRAddress = "";
        private const string _StaticQRServer = "StaticQRServer";

        private const string _TextServerAddress = "TextServerAddress";
        private const string _TextServerNotRunning = "TextServerNotRunning";

        public override void Init()
        {
            base.Init();

            _ThemeStatics = new string[] {_StaticQRServer};
            _ThemeTexts = new string[] {_TextServerAddress, _TextServerNotRunning};
        }

        public override void OnShow()
        {
            base.OnShow();
            // Only ever the relay address: guests reach the queue through it, and it is the one
            // address that works no matter whose network the machine is standing on tonight.
            string address = CVocaluxeServer.GetGuestAddress();
            bool haveAddress = !string.IsNullOrEmpty(address);

            if (_QRServerAddress == null || _QRAddress != address)
            {
                _QRAddress = address;
                _QRServerAddress = null;
                if (haveAddress)
                    _GenerateQRs();
                _Statics[_StaticQRServer].Texture = _QRServerAddress;
                _Texts[_TextServerAddress].Text = address;
            }
            _Texts[_TextServerAddress].Visible = haveAddress;
            _Statics[_StaticQRServer].Visible = haveAddress;
            _Texts[_TextServerNotRunning].Visible = !haveAddress;

            // Nothing to show a guest. Say which of the two reasons it is: the local server never
            // came up (with the actual cause, e.g. "port in use"), or it runs but the relay is not
            // connected - the usual case being that the relay is simply unreachable right now.
            if (!haveAddress)
            {
                string statusKey = CVocaluxeServer.GetStatusKey();
                if (!CVocaluxeServer.IsServerRunning())
                {
                    _Texts[_TextServerNotRunning].Text = string.IsNullOrEmpty(statusKey)
                        ? "TR_SCREENPSERVERQR_NOTRUNNING"
                        : CLanguage.Translate(statusKey).Replace("%d", CConfig.Config.Server.ServerPort.ToString());
                }
                else
                {
                    string relayError = CRelayAgent.ErrorText;
                    _Texts[_TextServerNotRunning].Text = CLanguage.Translate(
                        string.IsNullOrEmpty(relayError) ? "TR_SCREENPSERVERQR_RELAYNOTCONNECTED" : relayError);
                }
            }
        }

        public override bool HandleMouse(SMouseEvent mouseEvent)
        {
            if (mouseEvent.LB || mouseEvent.RB)
            {
                CGraphics.HidePopup(EPopupScreens.PopupServerQR);
                return true;
            }
            return false;
        }

        public override bool HandleInput(SKeyEvent keyEvent)
        {
            switch (keyEvent.Key)
            {
                case Keys.Escape:
                case Keys.Back:
                    CGraphics.HidePopup(EPopupScreens.PopupServerQR);
                    return true;
            }
            return base.HandleInput(keyEvent);
        }

        public override bool UpdateGame()
        {
            return true;
        }

        private void _GenerateQRs()
        {
            string address = _QRAddress;
            if (string.IsNullOrEmpty(address))
                return;
            try
            {
                // QRCoder's GDI+ renderer (QRCode.GetGraphic) is Windows-only; PngByteQRCode produces
                // a PNG byte[] with no System.Drawing dependency, which we decode with SkiaSharp and
                // upload as a texture.
                var generator = new QRCodeGenerator();
                QRCodeData data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.Q);
                byte[] png = new PngByteQRCode(data).GetGraphic(20);

                using (SKBitmap decoded = SKBitmap.Decode(png))
                {
                    if (decoded == null)
                        return;
                    byte[] bgra;
                    if (decoded.ColorType == SKColorType.Bgra8888)
                        bgra = decoded.Bytes;
                    else
                        using (SKBitmap converted = decoded.Copy(SKColorType.Bgra8888))
                            bgra = converted.Bytes;
                    _QRServerAddress = CDraw.AddTexture(decoded.Width, decoded.Height, bgra);
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "Could not generate the server QR code");
            }
        }
    }
}