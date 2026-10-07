using AionDps.Capture;
using AionDps.Meter;
using AionDps.Protocol;
using AionDps.UI;

namespace AionDps;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length >= 2 && args[0] == "--preview")
        {
            SavePreview(args[1], args.Length > 2 ? args[2] : "");
            return;
        }

        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        var meter = new CombatMeter(data);
        var parser = new PacketParser(meter.OnEvent);
        parser.DotSkills.UnionWith(data.DotSkills);

        using var capture = new GameCapture(parser);
        capture.Start();

        // GS/CP pelo site oficial quando não vêm da lista do grupo
        using var armory = new Armory(meter);
        armory.Start();

        if (args.Contains("--debug")) StartDebugLogs(parser, meter, capture);
        if (args.Contains("--dump")) StartDump(parser);

        Application.Run(new OverlayForm(meter, capture));
    }

    /// <summary>
    /// Com <c>--dump</c>: grava todo pacote recebido (menos heartbeat e dano) em dump.log, para descobrir campos novos.
    /// Até 2000 por opcode e 100 MB no total.
    /// </summary>
    private static void StartDump(PacketParser parser)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dump.log");
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var counts = new Dictionary<ushort, int>();
        long bytes = 0;
        var previous = parser.Tap;
        parser.Tap = (op, body) =>
        {
            previous?.Invoke(op, body);
            if (op is PacketParser.OpHeartbeat or PacketParser.OpDamage or PacketParser.OpDot) return;
            // Status só do próprio personagem (00 8D com formato bit 0) sempre; o resto até 2000 por opcode.
            var selfStats = op == PacketParser.OpEntityStats && IsSelfStats(body);
            if (!selfStats && (counts[op] = counts.GetValueOrDefault(op) + 1) > 2000) return;
            if (bytes > 200_000_000) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {op:X4} {Convert.ToHexString(body)}";
            bytes += line.Length;
            lock (writer) writer.WriteLine(line);
        };
    }

    private static bool IsSelfStats(byte[] body)
    {
        var o = 0;
        while (o < body.Length && (body[o] & 0x80) != 0) o++;
        o++;
        return o < body.Length && (body[o] & 0x01) != 0;
    }

    /// <summary>Com <c>--debug</c>: grava aiondps.log (estado a cada 3 s) e packets.log (pacotes de identidade/HP em hex).</summary>
    private static void StartDebugLogs(PacketParser parser, CombatMeter meter, GameCapture capture)
    {
        // Pacotes de identidade/HP em hex, no máximo 300 por opcode
        var pktLog = Path.Combine(AppContext.BaseDirectory, "packets.log");
        File.WriteAllText(pktLog, "");
        var watched = new HashSet<ushort> { 0x0297, 0x2036, 0x3336, 0x4536, 0x4436, 0x008D, 0x1B92, 0x4136 };
        var counts = new Dictionary<ushort, int>();
        parser.Tap = (op, body) =>
        {
            if (!watched.Contains(op) || (counts[op] = counts.GetValueOrDefault(op) + 1) > 300) return;
            try { File.AppendAllText(pktLog, $"{DateTime.Now:HH:mm:ss.fff} {op:X4} {Convert.ToHexString(body)}\n"); } catch { }
        };

        // Log de diagnóstico a cada 3 s em aiondps.log (ao lado do .exe)
        var log = Path.Combine(AppContext.BaseDirectory, "aiondps.log");
        File.WriteAllText(log, "");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(3_000);
                var snap = meter.Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var top = snap is null ? "-" : string.Join(" | ", snap.Rows.Take(5).Select(r => $"{r.Name} {r.Damage} ({r.Dps:0}/s)"));
                File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} status='{capture.Status}' pacotes={parser.Packets} " +
                    $"dano={parser.DamagePackets} eventos={meter.EventCount} eu='{meter.SelfName}' grupo={meter.PartySize} lutas={snap?.Fights ?? 0} top: {top}\n  {meter.DebugState()}\n");
            }
        });
    }

    /// <summary>Renderiza o overlay com uma luta fictícia num PNG (para conferir o layout sem o jogo).</summary>
    private static void SavePreview(string path, string mode)
    {
        if (mode == "settings")
        {
            using var dlg = new SettingsDialog(new Settings());
            dlg.Show();
            Application.DoEvents();
            using var sb = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(sb, new Rectangle(0, 0, dlg.Width, dlg.Height));
            sb.Save(path);
            return;
        }
        var skills = new List<SkillRow>
        {
            new(1, "Explosão de Chamas", 1_420_000, 38, 0.42, 61_000, 0.39),
            new(2, "Lança de Gelo", 980_000, 52, 0.31, 33_000, 0.27),
            new(3, "Ataque Básico", 610_000, 140, 0.22, 9_800, 0.17),
            new(4, "Tempestade Arcana", 410_000, 12, 0.5, 52_000, 0.11),
            new(5, "Chuva de Meteoros", 230_000, 6, 0.33, 61_000, 0.06),
        };
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        using var capture = new GameCapture(new PacketParser(_ => { }));
        PlayerRow Row(uint id, string name, GameClass c, long dmg, double share, uint gs, ulong cp, bool self = false) =>
            new(id, name, c, self, dmg, dmg / 154.0, share, 300, 0.31, dmg / 40, self ? skills : [], gs, cp);
        var rows = new List<PlayerRow>
        {
            Row(1, "Pencilgon", GameClass.Assassin, 4_210_000, 0.34, 452, 22667),
            Row(2, "Voce", GameClass.Sorcerer, 3_650_000, 0.29, 441, 21980, self: true),
            Row(3, "Aahz", GameClass.Gladiator, 2_980_000, 0.24, 433, 22242),
            Row(4, "Boahancook", GameClass.Cleric, 1_020_000, 0.08, 417, 20132),
            Row(5, "Caramelly", GameClass.Chanter, 640_000, 0.05, 368, 18695),
        };
        var form = new OverlayForm(new CombatMeter(data), capture)
        {
            PreviewDetail = mode == "detail" ? 2u : null,
            Preview = new Snapshot("Total", 3, true, 154, 154, 12_500_000, "Boss de exemplo", 37.5, rows, 62.5),
        };
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.Refresh();
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save(path);
        form.Close();
    }
}
