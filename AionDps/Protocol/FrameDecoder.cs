using System.Buffers;
using K4os.Compression.LZ4;

namespace AionDps.Protocol;

public delegate void PacketHandler(ushort opcode, ReadOnlySpan<byte> body);

/// <summary>
/// Quebra o stream servidor→cliente em pacotes do jogo.
/// <code>
/// frame   = varint len, [byte 0xF0..0xFE opcional], payload      (tamanho do frame = len + bytesDoVarint − 4)
/// payload = opcode(2) corpo | FF FF u32 tamanhoBruto bloco-lz4    (o bloco contém mais frames, possivelmente aninhados)
/// </code>
/// Bytes 0x00 entre frames são padding. Depois de perder dados, procura o próximo heartbeat (<c>0E 00 36</c>,
/// ~19/s) para se realinhar em vez de chutar varints (o que geraria dano fantasma).
/// </summary>
public sealed class FrameDecoder
{
    private const int MaxFrame = 65_535;
    private const int MaxUnbundledWait = 16_384;
    public const int MaxBundle = 8 * 1024 * 1024;
    private const int MaxHuntBytes = 256 * 1024;

    public static readonly byte[][] HeartbeatSignatures = [[0x0E, 0x00, 0x36], [0x06, 0x00, 0x36]];

    private readonly PacketHandler _onPacket;
    private byte[] _buf = new byte[128 * 1024];
    private int _len;
    private bool _hunting;
    private int _huntedBytes;

    public FrameDecoder(PacketHandler onPacket, bool startsAligned)
    {
        _onPacket = onPacket;
        _hunting = !startsAligned;
    }

    /// <summary>Chamado quando o TCP pulou bytes perdidos: o próximo byte não é início de frame.</summary>
    public void Reset()
    {
        _len = 0;
        _hunting = true;
        _huntedBytes = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (_len + data.Length > _buf.Length)
        {
            var bigger = new byte[Math.Max(_buf.Length * 2, _len + data.Length)];
            Buffer.BlockCopy(_buf, 0, bigger, 0, _len);
            _buf = bigger;
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        Drain();
    }

    private void Drain()
    {
        var pos = 0;
        while (pos < _len)
        {
            var span = _buf.AsSpan(pos, _len - pos);

            if (_hunting)
            {
                var hb = FindHeartbeat(span);
                if (hb < 0)
                {
                    var skip = span.Length - Math.Min(span.Length, 2);
                    pos += skip;
                    _huntedBytes += skip;
                    if (_huntedBytes > MaxHuntBytes) _hunting = false;
                    break;
                }
                pos += hb;
                _hunting = false;
                continue;
            }

            if (span[0] == 0x00)
            {
                pos++;
                continue;
            }

            if (!Wire.TryVarint(span, 0, out var len, out var vlen))
            {
                if (span.Length < 5) break;
                pos += Resync();
                continue;
            }

            var size = (long)len + vlen - 4;
            if (len < 6 || size > MaxFrame)
            {
                pos += Resync();
                continue;
            }

            if (size > span.Length)
            {
                if (size > MaxUnbundledWait && !IsBundle(span, vlen))
                {
                    pos += Resync();
                    continue;
                }
                break; // espera o resto do frame
            }

            HandleFrame(span[..(int)size], vlen, 0);
            pos += (int)size;
        }

        if (pos > 0)
        {
            _len -= pos;
            if (_len > 0) Buffer.BlockCopy(_buf, pos, _buf, 0, _len);
        }
    }

    private int Resync()
    {
        if (_huntedBytes <= MaxHuntBytes)
        {
            _hunting = true;
            _huntedBytes = 0;
        }
        return 1;
    }

    private static int FindHeartbeat(ReadOnlySpan<byte> span)
    {
        var best = -1;
        foreach (var sig in HeartbeatSignatures)
        {
            var i = span.IndexOf(sig);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    private static bool IsBundle(ReadOnlySpan<byte> frame, int vlen)
    {
        var o = vlen;
        if (o < frame.Length && frame[o] is >= 0xF0 and <= 0xFE) o++;
        return o + 1 < frame.Length && frame[o] == 0xFF && frame[o + 1] == 0xFF;
    }

    private void HandleFrame(ReadOnlySpan<byte> frame, int vlen, int depth)
    {
        var o = vlen;
        if (o < frame.Length && frame[o] is >= 0xF0 and <= 0xFE) o++;
        if (o + 2 > frame.Length) return;

        if (frame[o] == 0xFF && frame[o + 1] == 0xFF)
        {
            o += 2;
            if (!Wire.TryU32(frame, ref o, out var rawSize) || rawSize == 0 || rawSize > MaxBundle || depth > 4) return;
            var target = ArrayPool<byte>.Shared.Rent((int)rawSize);
            try
            {
                var n = LZ4Codec.Decode(frame[o..], target.AsSpan(0, (int)rawSize));
                if (n > 0) WalkInner(target.AsSpan(0, n), depth + 1);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(target);
            }
            return;
        }

        var opcode = (ushort)(frame[o] << 8 | frame[o + 1]);
        _onPacket(opcode, frame[(o + 2)..]);
    }

    /// <summary>Frames dentro de um bundle descomprimido. Um tamanho inválido encerra o bundle.</summary>
    private void WalkInner(ReadOnlySpan<byte> data, int depth)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            if (data[pos] == 0x00)
            {
                pos++;
                continue;
            }
            var span = data[pos..];
            if (!Wire.TryVarint(span, 0, out var len, out var vlen)) return;
            var size = (long)len + vlen - 4;
            if (len < 6 || size > span.Length) return;
            HandleFrame(span[..(int)size], vlen, depth);
            pos += (int)size;
        }
    }
}
