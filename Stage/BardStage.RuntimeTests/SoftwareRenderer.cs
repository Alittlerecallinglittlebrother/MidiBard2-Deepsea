using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

internal static unsafe class SoftwareRenderer
{
    public static void Save(string path)
    {
        var draw = ImGui.GetDrawData();
        var width = (int)draw.DisplaySize.X; var height = (int)draw.DisplaySize.Y;
        byte* atlas = null; int texWidth = 0, texHeight = 0;
        ImGui.GetIO().Fonts.GetTexDataAsRGBA32(0, ref atlas, ref texWidth, ref texHeight);
        var output = new byte[width * height * 4];
        for (var i = 3; i < output.Length; i += 4) output[i] = 255;
        for (var listIndex = 0; listIndex < draw.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = draw.CmdLists[listIndex];
            for (var commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var cmd = list.CmdBuffer[commandIndex];
                if (cmd.UserCallback != null) continue;
                for (uint triangle = 0; triangle + 2 < cmd.ElemCount; triangle += 3)
                {
                    var a = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + triangle)] + cmd.VtxOffset)];
                    var b = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + triangle + 1)] + cmd.VtxOffset)];
                    var c = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + triangle + 2)] + cmd.VtxOffset)];
                    Triangle(a, b, c, cmd.ClipRect, output, width, height, atlas, texWidth, texHeight);
                }
            }
        }
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(output, 0, data.Scan0, output.Length); }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, ImageFormat.Png);
        if (output.Where((_, index) => index % 4 != 3).Distinct().Count() < 20) throw new InvalidOperationException("render pixels are blank");
    }

    private static void Triangle(ImDrawVert a, ImDrawVert b, ImDrawVert c, Vector4 clip, byte[] pixels, int width, int height, byte* texture, int tw, int th)
    {
        var determinant = Cross(b.Pos - a.Pos, c.Pos - a.Pos);
        if (Math.Abs(determinant) < 0.001f) return;
        var x0 = Math.Max(0, (int)MathF.Ceiling(Math.Max(clip.X, Math.Min(a.Pos.X, Math.Min(b.Pos.X, c.Pos.X)))));
        var x1 = Math.Min(width, (int)MathF.Ceiling(Math.Min(clip.Z, Math.Max(a.Pos.X, Math.Max(b.Pos.X, c.Pos.X)))));
        var y0 = Math.Max(0, (int)MathF.Ceiling(Math.Max(clip.Y, Math.Min(a.Pos.Y, Math.Min(b.Pos.Y, c.Pos.Y)))));
        var y1 = Math.Min(height, (int)MathF.Ceiling(Math.Min(clip.W, Math.Max(a.Pos.Y, Math.Max(b.Pos.Y, c.Pos.Y)))));
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            var wa = Cross(b.Pos - p, c.Pos - p) / determinant;
            var wb = Cross(c.Pos - p, a.Pos - p) / determinant;
            var wc = 1 - wa - wb;
            if (wa < 0 || wb < 0 || wc < 0) continue;
            var uv = a.Uv * wa + b.Uv * wb + c.Uv * wc;
            var tx = Math.Clamp((int)(uv.X * tw), 0, tw - 1); var ty = Math.Clamp((int)(uv.Y * th), 0, th - 1);
            var tex = texture + (ty * tw + tx) * 4;
            var alpha = (Component(a.Col, 24) * wa + Component(b.Col, 24) * wb + Component(c.Col, 24) * wc) * tex[3] / 65025f;
            var offset = (y * width + x) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var color = (Component(a.Col, channel * 8) * wa + Component(b.Col, channel * 8) * wb + Component(c.Col, channel * 8) * wc) * tex[channel] / 255f;
                var index = offset + 2 - channel;
                pixels[index] = (byte)Math.Clamp(color * alpha + pixels[index] * (1 - alpha), 0, 255);
            }
        }
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static float Component(uint color, int shift) => (color >> shift) & 255;
}
