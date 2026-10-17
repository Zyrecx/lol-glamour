# Turns an image into Glamour's raw greyscale file for tests and the augment button template: int32 width, int32 height, then one byte per pixel
# (0.299 R + 0.587 G + 0.114 B, as the desktop app greys its screen captures), gzipped when -Out ends in .gz.
# Usage: powershell -File tools/gray.ps1 -In shot.png -Out tests/Glamour.Tests/Fixtures/x.gray.gz
param([Parameter(Mandatory)][string]$In, [Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.IO.Compression; using System.Runtime.InteropServices;
public static class GlamourGray {
    public static string Convert(string input, string output) {
        using (var bmp = new Bitmap(input)) {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var src = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, src, 0, src.Length);
            bmp.UnlockBits(data);
            var gray = new byte[w * h + 8];
            BitConverter.GetBytes(w).CopyTo(gray, 0);
            BitConverter.GetBytes(h).CopyTo(gray, 4);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) {
                    int i = y * data.Stride + x * 4;
                    gray[8 + y * w + x] = (byte)Math.Min(255, (int)(0.114 * src[i] + 0.587 * src[i + 1] + 0.299 * src[i + 2] + 0.5));
                }
            if (output.EndsWith(".gz")) {
                using (var fs = File.Create(output)) using (var gz = new GZipStream(fs, CompressionLevel.Optimal)) gz.Write(gray, 0, gray.Length);
            } else File.WriteAllBytes(output, gray);
            return w + "x" + h;
        }
    }
}
'@
$size = [GlamourGray]::Convert((Resolve-Path $In).Path, [IO.Path]::GetFullPath($Out))
"$Out $size"
