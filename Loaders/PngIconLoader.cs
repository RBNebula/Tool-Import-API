using System.Drawing;
using System.IO;
using UnityEngine;

namespace MineMogul.ToolImportApi.Loaders
{
    internal sealed class PngIconLoader
    {
        public bool TryLoadIcon(byte[] pngBytes, string spriteName, out Sprite icon, out string error)
        {
            icon = null;
            error = null;

            if (pngBytes == null || pngBytes.Length == 0)
            {
                error = "PNG byte array is empty.";
                return false;
            }

            Color32[] pixels;
            int width;
            int height;
            if (!TryDecodePng(pngBytes, out pixels, out width, out height, out error))
            {
                return false;
            }

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            texture.name = string.IsNullOrEmpty(spriteName) ? "ImportedToolIcon" : spriteName;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            Rect rect = new Rect(0f, 0f, texture.width, texture.height);
            icon = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            icon.name = texture.name + "_Sprite";
            return true;
        }

        private static bool TryDecodePng(byte[] pngBytes, out Color32[] pixels, out int width, out int height, out string error)
        {
            pixels = null;
            width = 0;
            height = 0;
            error = null;

            try
            {
                using (MemoryStream ms = new MemoryStream(pngBytes, writable: false))
                using (Bitmap bitmap = new Bitmap(ms))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    pixels = new Color32[width * height];

                    for (int y = 0; y < height; y++)
                    {
                        int flippedY = height - 1 - y;
                        for (int x = 0; x < width; x++)
                        {
                            System.Drawing.Color c = bitmap.GetPixel(x, y);
                            pixels[flippedY * width + x] = new Color32(c.R, c.G, c.B, c.A);
                        }
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                error = "PNG decode failed: " + ex.Message;
                return false;
            }
        }
    }
}
