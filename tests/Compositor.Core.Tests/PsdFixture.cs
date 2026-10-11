using System.Text;
using Compositor.Core.Model;

namespace Compositor.Core.Tests;

/// <summary>One layer record for <see cref="PsdFixture"/>: straight RGBA pixels where it sits, or a folder marker.</summary>
public sealed class PsdLayerSpec
{
    public string Name = "Layer";
    public int Left, Top, Width, Height;
    /// <summary>Straight (unpremultiplied) RGBA, Width × Height.</summary>
    public byte[]? Rgba;
    public byte Opacity = 255;
    public string Blend = "norm";
    public bool Clipping, Hidden;
    /// <summary>1 or 2: a folder (written after its children); 3: the divider opening one (written before them).</summary>
    public int? Section;
    public byte[]? Mask;
    public RectI MaskRect;
    public byte MaskDefault = 255;
    public Dictionary<string, byte[]> Extra = new();
    public bool PackBits = true;
    public bool Unicode = true;

    public static PsdLayerSpec Solid(string name, int left, int top, int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a; }
        return new PsdLayerSpec { Name = name, Left = left, Top = top, Width = width, Height = height, Rgba = rgba };
    }
}

/// <summary>Builds small Photoshop files for the reader's tests (Compositor itself never writes PSD).</summary>
public static class PsdFixture
{
    public static byte[] Build(int width, int height, IEnumerable<PsdLayerSpec> layersBottomUp, double resolution = 72)
    {
        var layers = layersBottomUp.ToList();
        var file = new Buffer();
        file.Text("8BPS"); file.U16(1); file.Zeros(6); file.U16(3); file.U32((uint)height); file.U32((uint)width); file.U16(8); file.U16(3);
        file.U32(0); // color mode data
        var resources = new Buffer();
        resources.Text("8BIM"); resources.U16(1005); resources.U16(0); resources.U32(16);
        uint fixedRes = (uint)Math.Round(resolution * 65536);
        resources.U32(fixedRes); resources.U16(1); resources.U16(1); resources.U32(fixedRes); resources.U16(1); resources.U16(1);
        file.U32((uint)resources.Length); file.Bytes(resources.ToArray());

        var records = new Buffer();
        var channelData = new Buffer();
        records.I16((short)layers.Count);
        foreach (var layer in layers)
        {
            var channels = new List<(short Id, byte[] Payload)>();
            if (layer.Rgba is { } rgba && layer.Width > 0 && layer.Height > 0)
            {
                foreach (var (id, component) in new (short, int)[] { (-1, 3), (0, 0), (1, 1), (2, 2) })
                {
                    var plane = new byte[layer.Width * layer.Height];
                    for (int i = 0; i < plane.Length; i++) plane[i] = rgba[i * 4 + component];
                    channels.Add((id, Channel(plane, layer.Width, layer.Height, layer.PackBits)));
                }
            }
            else
            {
                foreach (short id in new short[] { -1, 0, 1, 2 }) channels.Add((id, new byte[] { 0, 0 }));
            }
            if (layer.Mask is { } mask) channels.Add((-2, Channel(mask, layer.MaskRect.Width, layer.MaskRect.Height, layer.PackBits)));

            bool hasPixels = layer.Rgba is not null;
            records.I32(hasPixels ? layer.Top : 0); records.I32(hasPixels ? layer.Left : 0);
            records.I32(hasPixels ? layer.Top + layer.Height : 0); records.I32(hasPixels ? layer.Left + layer.Width : 0);
            records.U16((ushort)channels.Count);
            foreach (var (id, payload) in channels) { records.I16(id); records.U32((uint)payload.Length); channelData.Bytes(payload); }
            records.Text("8BIM"); records.Text((layer.Blend + "    ")[..4]);
            records.U8(layer.Opacity); records.U8((byte)(layer.Clipping ? 1 : 0)); records.U8((byte)(layer.Hidden ? 2 : 0)); records.U8(0);

            var extra = new Buffer();
            if (layer.Mask is not null)
            {
                extra.U32(20);
                extra.I32(layer.MaskRect.Y); extra.I32(layer.MaskRect.X); extra.I32(layer.MaskRect.Bottom); extra.I32(layer.MaskRect.Right);
                extra.U8(layer.MaskDefault); extra.U8(0); extra.U16(0);
            }
            else extra.U32(0);
            extra.U32(0); // blending ranges
            var ascii = Encoding.ASCII.GetBytes(layer.Name.Select(c => c < 128 ? c : '?').ToArray());
            extra.U8((byte)ascii.Length); extra.Bytes(ascii);
            extra.Zeros((4 - ((ascii.Length + 1) % 4)) % 4);
            var info = new Dictionary<string, byte[]>(layer.Extra);
            if (layer.Unicode)
            {
                var name = new Buffer();
                name.U32((uint)layer.Name.Length);
                foreach (char c in layer.Name) name.U16(c);
                info["luni"] = name.ToArray();
            }
            if (layer.Section is { } sectionType)
            {
                var s = new Buffer();
                s.U32((uint)sectionType);
                info["lsct"] = s.ToArray();
            }
            foreach (var (key, payload) in info)
            {
                extra.Text("8BIM"); extra.Text(key); extra.U32((uint)payload.Length); extra.Bytes(payload);
                if (payload.Length % 2 == 1) extra.U8(0);
            }
            records.U32((uint)extra.Length); records.Bytes(extra.ToArray());
        }
        var layerInfo = new Buffer();
        layerInfo.Bytes(records.ToArray());
        layerInfo.Bytes(channelData.ToArray());
        if (layerInfo.Length % 2 == 1) layerInfo.U8(0);
        var section = new Buffer();
        section.U32((uint)layerInfo.Length);
        section.Bytes(layerInfo.ToArray());
        section.U32(0); // global layer mask info
        file.U32((uint)section.Length);
        file.Bytes(section.ToArray());
        // The flattened image: raw zeros (the reader takes the layers).
        file.U16(0);
        file.Zeros(width * height * 3);
        return file.ToArray();
    }

    private static byte[] Channel(byte[] plane, int width, int height, bool packBits)
    {
        var data = new Buffer();
        if (!packBits)
        {
            data.U16(0);
            data.Bytes(plane);
            return data.ToArray();
        }
        data.U16(1);
        var rows = Enumerable.Range(0, height).Select(y => PackRow(plane.AsSpan(y * width, width))).ToList();
        foreach (var row in rows) data.U16((ushort)row.Length);
        foreach (var row in rows) data.Bytes(row);
        return data.ToArray();
    }

    /// <summary>PackBits: runs of three or more repeat; the rest go as literals.</summary>
    private static byte[] PackRow(ReadOnlySpan<byte> row)
    {
        var output = new List<byte>();
        int i = 0;
        while (i < row.Length)
        {
            int run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
            if (run >= 3)
            {
                output.Add((byte)(sbyte)(1 - run));
                output.Add(row[i]);
                i += run;
                continue;
            }
            int start = i, count = 0;
            while (i < row.Length && count < 128)
            {
                int ahead = 1;
                while (i + ahead < row.Length && ahead < 3 && row[i + ahead] == row[i]) ahead++;
                if (ahead >= 3) break;
                i++; count++;
            }
            output.Add((byte)(count - 1));
            for (int k = 0; k < count; k++) output.Add(row[start + k]);
        }
        return output.ToArray();
    }

    private sealed class Buffer
    {
        private readonly MemoryStream _stream = new();
        public int Length => (int)_stream.Length;
        public void U8(byte v) => _stream.WriteByte(v);
        public void U16(int v) { U8((byte)(v >> 8)); U8((byte)v); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        public void I32(int v) => U32((uint)v);
        public void Text(string s) => Bytes(Encoding.ASCII.GetBytes(s));
        public void Bytes(byte[] b) => _stream.Write(b);
        public void Zeros(int n) { for (int i = 0; i < n; i++) U8(0); }
        public byte[] ToArray() => _stream.ToArray();
    }
}
