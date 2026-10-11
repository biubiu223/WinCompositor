using System.Text;

namespace Compositor.Core.IO.Psd;

/// <summary>One value of a Photoshop action descriptor.</summary>
internal sealed class PsdValue
{
    public enum Type { Text, Number, Bool, Enum, Data, Object, List, Other }

    public Type Kind { get; init; }
    public string? Text { get; init; }
    public double Number { get; init; }
    /// <summary>A unit float's unit ("#Pxl", "#Prc", "#Ang", "#Pnt"…), or null.</summary>
    public string? Unit { get; init; }
    public bool Bool { get; init; }
    public byte[]? Data { get; init; }
    public string? ClassId { get; init; }
    public Dictionary<string, PsdValue>? Items { get; init; }
    public List<PsdValue>? List { get; init; }

    public PsdValue? this[string key] => Items is { } items && (items.TryGetValue(key, out var v) || items.TryGetValue(key.TrimEnd(), out v)) ? v : null;
}

/// <summary>Typed lookups on a descriptor's items.</summary>
internal static class PsdDescriptorItems
{
    public static string? String(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Text } v ? v.Text : null;
    public static string? Enum(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Enum } v ? v.Text : null;
    public static byte[]? Data(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Data } v ? v.Data : null;
    public static double? Number(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Number } v ? v.Number : null;
    public static bool? Bool(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Bool } v ? v.Bool : null;
    public static Dictionary<string, PsdValue>? Object(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.Object } v ? v.Items : null;
    public static List<PsdValue>? List(this Dictionary<string, PsdValue> d, string key) => d.Get(key) is { Kind: PsdValue.Type.List } v ? v.List : null;

    /// <summary>Keys are four-character codes padded with spaces ("Top ", "Rd  "); either spelling finds them.</summary>
    public static PsdValue? Get(this Dictionary<string, PsdValue> d, string key) =>
        d.TryGetValue(key, out var v) || d.TryGetValue(key.TrimEnd(), out v) || d.TryGetValue(key.PadRight(4), out v) ? v : null;

    /// <summary>A rectangle descriptor (Left, Top, Rght, Btom).</summary>
    public static (double Left, double Top, double Right, double Bottom)? Rect(this Dictionary<string, PsdValue> d, string key)
    {
        if (d.Object(key) is not { } r) return null;
        if (r.Number("Left") is not { } left || r.Number("Top ") is not { } top || r.Number("Rght") is not { } right || r.Number("Btom") is not { } bottom)
            return null;
        return new[] { left, top, right, bottom }.All(double.IsFinite) ? (left, top, right, bottom) : null;
    }
}

/// <summary>
/// Reads Photoshop's action descriptors (class and keys length-prefixed, or four bytes when the length is 0), as in
/// Adobe's Photoshop File Formats Specification. Mirrors PSDText.swift's Reader, keeping booleans and units.
/// </summary>
internal sealed class PsdDescriptorReader
{
    private readonly byte[] _data;
    public int Offset;

    public PsdDescriptorReader(byte[] data, int offset = 0) { _data = data; Offset = offset; }

    public int Remaining => _data.Length - Offset;

    /// <summary>A descriptor; <paramref name="versioned"/> reads the leading version (16) first.</summary>
    public Dictionary<string, PsdValue>? Descriptor(bool versioned)
    {
        if (versioned && U32() != 16) return null;
        return Body(out _);
    }

    private Dictionary<string, PsdValue>? Body(out string? classId)
    {
        classId = null;
        if (Unicode() is null || (classId = Identifier()) is null || U32() is not { } count || count > 10_000) return null;
        var items = new Dictionary<string, PsdValue>();
        for (int i = 0; i < count; i++)
        {
            if (Identifier() is not { } key || FourCC() is not { } type || Value(type) is not { } value) return null;
            items[key] = value;
        }
        return items;
    }

    private PsdValue? Value(string type)
    {
        switch (type)
        {
            case "doub":
                return F64() is { } d ? new PsdValue { Kind = PsdValue.Type.Number, Number = d } : null;
            case "UntF":
                if (FourCC() is not { } unit || F64() is not { } u) return null;
                return new PsdValue { Kind = PsdValue.Type.Number, Number = u, Unit = unit };
            case "long":
                return U32() is { } l ? new PsdValue { Kind = PsdValue.Type.Number, Number = unchecked((int)l) } : null;
            case "comp":
                if (Bytes(8) is not { } raw) return null;
                long bits = 0;
                foreach (var b in raw) bits = (bits << 8) | b;
                return new PsdValue { Kind = PsdValue.Type.Number, Number = bits };
            case "bool":
                return U8() is { } flag ? new PsdValue { Kind = PsdValue.Type.Bool, Bool = flag != 0 } : null;
            case "TEXT":
                return Unicode() is { } text ? new PsdValue { Kind = PsdValue.Type.Text, Text = text } : null;
            case "enum":
                if (Identifier() is null || Identifier() is not { } name) return null;
                return new PsdValue { Kind = PsdValue.Type.Enum, Text = name };
            case "tdta":
                if (U32() is not { } length || length > 8_000_000 || Bytes((int)length) is not { } data) return null;
                return new PsdValue { Kind = PsdValue.Type.Data, Data = data };
            case "Objc":
            case "GlbO":
                return Body(out var classId) is { } nested ? new PsdValue { Kind = PsdValue.Type.Object, Items = nested, ClassId = classId } : null;
            case "VlLs":
            {
                if (U32() is not { } count || count > 10_000) return null;
                var items = new List<PsdValue>();
                for (int i = 0; i < count; i++)
                {
                    if (FourCC() is not { } itemType || Value(itemType) is not { } item) return null;
                    items.Add(item);
                }
                return new PsdValue { Kind = PsdValue.Type.List, List = items };
            }
            case "UnFl":
            {
                if (FourCC() is null || U32() is not { } count || count > 100_000) return null;
                var items = new List<PsdValue>();
                for (int i = 0; i < count; i++)
                {
                    if (F64() is not { } v) return null;
                    items.Add(new PsdValue { Kind = PsdValue.Type.Number, Number = v });
                }
                return new PsdValue { Kind = PsdValue.Type.List, List = items };
            }
            case "alis":
                if (U32() is not { } aliasLength || aliasLength > 8_000_000 || Bytes((int)aliasLength) is null) return null;
                return new PsdValue { Kind = PsdValue.Type.Other };
            case "obj ":
                return Reference() ? new PsdValue { Kind = PsdValue.Type.Other } : null;
            case "type":
            case "GlbC":
                if (Unicode() is null || Identifier() is null) return null;
                return new PsdValue { Kind = PsdValue.Type.Other };
            default:
                return null;
        }
    }

    /// <summary>Skips a reference so later items can still be read.</summary>
    private bool Reference()
    {
        if (U32() is not { } count || count > 10_000) return false;
        for (int i = 0; i < count; i++)
        {
            switch (FourCC())
            {
                case "prop": if (Unicode() is null || Identifier() is null || Identifier() is null) return false; break;
                case "Clss": if (Unicode() is null || Identifier() is null) return false; break;
                case "Enmr": if (Unicode() is null || Identifier() is null || Identifier() is null || Identifier() is null) return false; break;
                case "rele": if (Unicode() is null || Identifier() is null || U32() is null) return false; break;
                case "Idnt":
                case "indx": if (U32() is null) return false; break;
                case "name": if (Unicode() is null) return false; break;
                default: return false;
            }
        }
        return true;
    }

    public string? Unicode()
    {
        if (U32() is not { } count || count > 1_000_000 || Bytes((int)count * 2) is not { } raw) return null;
        return raw.Length == 0 ? "" : Encoding.BigEndianUnicode.GetString(raw);
    }

    public string? Identifier()
    {
        if (U32() is not { } length) return null;
        if (length == 0) return FourCC();
        if (length > 10_000 || Bytes((int)length) is not { } raw) return null;
        return Encoding.ASCII.GetString(raw);
    }

    public string? FourCC() => Bytes(4) is { } raw ? Encoding.ASCII.GetString(raw) : null;

    public byte[]? Bytes(int count)
    {
        if (count < 0 || Offset + count > _data.Length) return null;
        var slice = _data.AsSpan(Offset, count).ToArray();
        Offset += count;
        return slice;
    }

    public byte? U8() => Offset < _data.Length ? _data[Offset++] : null;

    public ushort? U16()
    {
        if (Offset + 2 > _data.Length) return null;
        ushort v = (ushort)(_data[Offset] << 8 | _data[Offset + 1]);
        Offset += 2;
        return v;
    }

    public uint? U32()
    {
        if (Offset + 4 > _data.Length) return null;
        uint v = (uint)(_data[Offset] << 24 | _data[Offset + 1] << 16 | _data[Offset + 2] << 8 | _data[Offset + 3]);
        Offset += 4;
        return v;
    }

    public double? F64()
    {
        if (Offset + 8 > _data.Length) return null;
        long bits = 0;
        for (int i = 0; i < 8; i++) bits = (bits << 8) | _data[Offset + i];
        Offset += 8;
        return BitConverter.Int64BitsToDouble(bits);
    }
}
