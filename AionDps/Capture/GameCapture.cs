using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using AionDps.Protocol;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AionDps.Capture;

/// <summary>
/// Captura passiva do AION 2 via Npcap. Acha as conexões TCP do processo do jogo na tabela TCP do Windows,
/// abre só o adaptador que as carrega (loopback se usar ExitLag etc.) e filtra para esses servidores.
/// Cada fluxo servidor→cliente é observado até mostrar heartbeats do jogo; aí é "travado" e decodificado.
/// </summary>
public sealed class GameCapture : IDisposable
{
    private const ushort GamePort = 13328;
    private static readonly string[] ProcessNames = ["AION2", "Aion2", "AION2-Win64-Shipping", "Aion2-Win64-Shipping"];

    private readonly PacketParser _parser;
    private readonly object _gate = new();
    private readonly Dictionary<(IPAddress, ushort, IPAddress, ushort), Flow> _flows = new();
    private CancellationTokenSource? _cts;
    private LibPcapLiveDevice? _device;
    private string? _filter;

    public GameCapture(PacketParser parser) => _parser = parser;

    public string Status { get; private set; } = "Iniciando…";
    public long LastGameDataMs { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { Refresh(); }
                catch (DllNotFoundException) { Status = "Npcap não instalado (npcap.com)"; }
                catch (TypeInitializationException) { Status = "Npcap não instalado (npcap.com)"; }
                catch (Exception ex) { Status = "Erro: " + ex.Message; CloseDevice(); }
                try { await Task.Delay(2_000, ct); } catch (TaskCanceledException) { break; }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        CloseDevice();
    }

    private void Refresh()
    {
        var pids = new HashSet<int>();
        foreach (var name in ProcessNames)
            foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }

        if (pids.Count == 0)
        {
            CloseDevice();
            Status = "AION 2 não está aberto";
            return;
        }

        var conns = TcpTable.Connections(pids);
        if (conns.Count == 0)
        {
            Status = "Esperando o jogo conectar…";
            return;
        }

        var devices = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().ToList();
        LibPcapLiveDevice? device = null;
        foreach (var c in conns.OrderByDescending(c => c.RemotePort == GamePort))
        {
            device = IPAddress.IsLoopback(c.RemoteIp) || IPAddress.IsLoopback(c.LocalIp)
                ? devices.FirstOrDefault(IsLoopback)
                : devices.FirstOrDefault(d => d.Addresses.Any(a => c.LocalIp.Equals(a.Addr?.ipAddress)));
            if (device is not null) break;
        }
        if (device is null)
        {
            Status = "Nenhum adaptador Npcap carrega a conexão do jogo";
            return;
        }

        var hosts = conns.Select(c => $"(host {c.RemoteIp} and port {c.RemotePort})").Distinct().OrderBy(s => s);
        EnsureDevice(device, "tcp and (" + string.Join(" or ", hosts) + ")");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Status = now - LastGameDataMs < 10_000 ? "Capturando" : "Escutando… (troque de mapa/canal se não aparecer nada)";
    }

    private void EnsureDevice(LibPcapLiveDevice device, string filter)
    {
        lock (_gate)
        {
            if (_device is not null && _device.Name != device.Name) CloseDeviceLocked();
            if (_device is null)
            {
                device.OnPacketArrival += OnPacketArrival;
                device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100 });
                device.Filter = filter;
                _filter = filter;
                _device = device;
                device.StartCapture();
            }
            else if (_filter != filter)
            {
                _device.Filter = filter;
                _filter = filter;
            }
        }
    }

    private void CloseDevice()
    {
        lock (_gate) CloseDeviceLocked();
    }

    private void CloseDeviceLocked()
    {
        if (_device is null) return;
        try { _device.StopCapture(); } catch (PcapException) { }
        _device.OnPacketArrival -= OnPacketArrival;
        _device.Close();
        _device = null;
        _filter = null;
    }

    private static bool IsLoopback(LibPcapLiveDevice d) =>
        d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        (d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false);

    // ------------------------------------------------------------ segmentos → fluxos

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ip = packet.Extract<IPPacket>();
            var tcp = packet.Extract<TcpPacket>();
            if (ip is null || tcp is null) return;
            var ms = (long)raw.Timeval.Seconds * 1000 + (long)raw.Timeval.MicroSeconds / 1000;
            OnSegment(ms, (ip.SourceAddress, tcp.SourcePort, ip.DestinationAddress, tcp.DestinationPort),
                tcp.SequenceNumber, tcp.Synchronize, tcp.PayloadData ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex); // pacote malformado nunca derruba a captura
        }
    }

    private void OnSegment(long timeMs, (IPAddress, ushort, IPAddress, ushort) key, uint seq, bool syn, byte[] payload)
    {
        if (!_flows.TryGetValue(key, out var flow))
        {
            if (payload.Length == 0 && !syn) return;
            _flows[key] = flow = new Flow();
        }
        if (syn)
        {
            flow.SynNext = seq + 1;
            return;
        }
        if (payload.Length == 0 || flow.IsTls) return;

        if (flow.Reassembler is null)
        {
            // TLS no mesmo servidor? ignora.
            if (flow.PreLock.Count == 0 && payload.Length >= 5 && payload[0] is >= 0x14 and <= 0x17 && payload[1] == 0x03)
            {
                flow.IsTls = true;
                return;
            }
            flow.Heartbeats += CountHeartbeats(payload);
            flow.PreLock.Add((seq, payload));
            flow.PreLockBytes += payload.Length;
            while (flow.PreLockBytes > 512 * 1024 && flow.PreLock.Count > 1)
            {
                flow.PreLockBytes -= flow.PreLock[0].Data.Length;
                flow.PreLock.RemoveAt(0);
                flow.SynNext = null;
            }
            if (flow.Heartbeats < 3) return;

            // Trava como fluxo do jogo e reprocessa o que estava em buffer (inclui nosso próprio personagem).
            var first = flow.PreLock[0];
            var decoder = new FrameDecoder(_parser.Handle, flow.SynNext == first.Seq);
            flow.Reassembler = new TcpReassembler(d => decoder.Feed(d), decoder.Reset);
            flow.Reassembler.Start(first.Seq);
            var buffered = flow.PreLock;
            flow.PreLock = new();
            foreach (var (s, d) in buffered) Feed(flow, s, d, timeMs);
            return;
        }

        Feed(flow, seq, payload, timeMs);
    }

    private void Feed(Flow flow, uint seq, byte[] payload, long timeMs)
    {
        LastGameDataMs = timeMs;
        _parser.TimeMs = timeMs;
        flow.Reassembler!.Push(seq, payload, timeMs);
    }

    private static int CountHeartbeats(ReadOnlySpan<byte> payload)
    {
        var count = 0;
        foreach (var sig in FrameDecoder.HeartbeatSignatures)
        {
            var span = payload;
            int i;
            while ((i = span.IndexOf(sig)) >= 0)
            {
                var end = i + 11; // heartbeat tem exatamente 11 bytes
                if (end == span.Length || (end < span.Length && span[end] != 0xFF)) count++;
                span = span[(i + 3)..];
            }
        }
        return count;
    }

    private sealed class Flow
    {
        public bool IsTls;
        public int Heartbeats;
        public uint? SynNext;
        public List<(uint Seq, byte[] Data)> PreLock = new();
        public int PreLockBytes;
        public TcpReassembler? Reassembler;
    }
}

/// <summary>Conexões TCP IPv4 estabelecidas de um conjunto de processos (exceto web 80/443).</summary>
internal static class TcpTable
{
    public readonly record struct Conn(IPAddress LocalIp, ushort LocalPort, IPAddress RemoteIp, ushort RemotePort);

    public static List<Conn> Connections(IReadOnlySet<int> pids)
    {
        var result = new List<Conn>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return result;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = Marshal.SizeOf<Row>();
            for (var i = 0; i < count; i++, rowPtr += rowSize)
            {
                var row = Marshal.PtrToStructure<Row>(rowPtr);
                if (row.State != 5 || !pids.Contains((int)row.OwningPid)) continue; // 5 = ESTABLISHED
                var remotePort = Port(row.RemotePort);
                if (remotePort is 80 or 443) continue;
                result.Add(new Conn(new IPAddress(row.LocalAddr), Port(row.LocalPort), new IPAddress(row.RemoteAddr), remotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static ushort Port(uint n) => (ushort)((n & 0xFF) << 8 | (n >> 8) & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}
