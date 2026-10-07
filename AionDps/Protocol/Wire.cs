using System.Buffers.Binary;
using System.Text;

namespace AionDps.Protocol;

/// <summary>Leitores primitivos do protocolo do AION 2. Inteiros multi-byte são little-endian.</summary>
public static class Wire
{
    /// <summary>Varint LEB128 sem sinal, até 5 bytes.</summary>
    public static bool TryVarint(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        var shift = 0;
        for (var i = offset; i < data.Length && i - offset < 5; i++)
        {
            var b = data[i];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                offset = i + 1;
                return true;
            }
            shift += 7;
        }
        value = 0;
        return false;
    }

    public static bool TryVarint(ReadOnlySpan<byte> data, int offset, out uint value, out int length)
    {
        var o = offset;
        if (TryVarint(data, ref o, out value))
        {
            length = o - offset;
            return true;
        }
        length = 0;
        return false;
    }

    public static bool TryU8(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        if (offset < data.Length)
        {
            value = data[offset++];
            return true;
        }
        value = 0;
        return false;
    }

    public static bool TryU32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        if (offset + 4 <= data.Length)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            offset += 4;
            return true;
        }
        value = 0;
        return false;
    }

    public static uint U24(ReadOnlySpan<byte> d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16);
    public static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Nome de personagem: 1–36 bytes UTF-8 válidos, só letras/dígitos, ao menos uma letra, até 16 chars.</summary>
    public static bool TryName(ReadOnlySpan<byte> field, out string name)
    {
        name = "";
        if (field.Length is < 1 or > 36) return false;
        string s;
        try { s = StrictUtf8.GetString(field); }
        catch (DecoderFallbackException) { return false; }
        var letters = 0;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) letters++;
            else if (!char.IsDigit(ch)) return false;
        }
        if (letters == 0 || s.Length > 16) return false;
        name = s;
        return true;
    }

    public static bool IsText(ReadOnlySpan<byte> field)
    {
        try
        {
            foreach (var ch in StrictUtf8.GetString(field))
                if (char.IsControl(ch)) return false;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
