namespace AionDps.Capture;

/// <summary>
/// Entrega em ordem de uma direção TCP. Duplicatas são cortadas, segmentos fora de ordem esperam num buffer,
/// e um buraco que não se preenche em 1,5 s (ou buffer &gt; 2 MB) é pulado, avisando o decoder para ressincronizar.
/// </summary>
public sealed class TcpReassembler(Action<byte[]> onData, Action onGap)
{
    private const int MaxPendingBytes = 2 * 1024 * 1024;
    private const int GapTimeoutMs = 1_500;

    private readonly List<(uint Seq, byte[] Data, long TimeMs)> _pending = new();
    private int _pendingBytes;
    private uint _next;
    private bool _started;

    public void Start(uint nextSeq)
    {
        _next = nextSeq;
        _started = true;
    }

    public void Push(uint seq, ReadOnlySpan<byte> payload, long timeMs)
    {
        if (payload.IsEmpty) return;
        if (!_started) Start(seq);

        var diff = (int)(seq - _next);
        if (diff < 0)
        {
            if (-diff >= payload.Length) return; // duplicata
            payload = payload[-diff..];
            seq = _next;
            diff = 0;
        }

        if (diff == 0)
        {
            Deliver(payload.ToArray());
            DrainPending();
        }
        else
        {
            var length = payload.Length;
            if (!_pending.Exists(p => p.Seq == seq && p.Data.Length >= length))
            {
                _pending.Add((seq, payload.ToArray(), timeMs));
                _pendingBytes += payload.Length;
            }
        }

        while (_pending.Count > 0 && (_pendingBytes > MaxPendingBytes || timeMs - _pending.Min(p => p.TimeMs) > GapTimeoutMs))
        {
            var best = 0;
            for (var i = 1; i < _pending.Count; i++)
                if ((int)(_pending[i].Seq - _pending[best].Seq) < 0) best = i;
            _next = _pending[best].Seq;
            onGap();
            DrainPending();
        }
    }

    private void DrainPending()
    {
        bool progressed;
        do
        {
            progressed = false;
            for (var i = 0; i < _pending.Count; i++)
            {
                var (seq, data, _) = _pending[i];
                var diff = (int)(seq - _next);
                if (diff > 0) continue;
                _pending.RemoveAt(i);
                _pendingBytes -= data.Length;
                if (-diff < data.Length) Deliver(diff == 0 ? data : data[-diff..]);
                progressed = true;
                break;
            }
        } while (progressed);
    }

    private void Deliver(byte[] data)
    {
        _next += (uint)data.Length;
        onData(data);
    }
}
