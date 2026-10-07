using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AionDps.Protocol;

namespace AionDps.Meter;

public sealed record ArmoryProfile(uint? ItemLevel, ulong? CombatPower);

/// <summary>
/// Busca Item Level (gear score) e Combat Power no site oficial de personagens da NCSoft (aion2.plaync.com), os
/// mesmos endpoints públicos que a página "Characters" usa. Só GETs, com cache de 10 min e no máximo ~1 requisição/s.
/// </summary>
public sealed partial class Armory(CombatMeter meter) : IDisposable
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryTime = TimeSpan.FromMinutes(2);
    private static readonly string[] AllRegions = ["nae", "naw", "eu", "la", "as"];

    private readonly HttpClient _http = CreateClient();
    private readonly Dictionary<string, DateTime> _nextTry = new();
    private readonly CancellationTokenSource _cts = new();

    public void Start() => Task.Run(() => Loop(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var who in meter.PlayersNeedingProfile())
            {
                if (_nextTry.TryGetValue(who.Name, out var next) && DateTime.UtcNow < next) continue;
                ArmoryProfile? profile = null;
                try { profile = await Lookup(who.Name, who.Server, who.Class, ct); }
                catch (OperationCanceledException) { return; }
                catch { /* rede/site fora: tenta de novo depois */ }
                _nextTry[who.Name] = DateTime.UtcNow + (profile is null ? RetryTime : CacheTime);
                if (profile is not null) meter.SetProfile(who.Name, profile);
                await Task.Delay(1000, ct);
            }
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Busca pelo nome (no servidor, se conhecido) e lê o perfil do resultado exato.</summary>
    private async Task<ArmoryProfile?> Lookup(string name, int server, GameClass cls, CancellationToken ct)
    {
        var regions = server > 0 && RegionOf(server) is { } r ? [r] : AllRegions;
        var found = new List<(string Region, string Id, int Server, int PcId, int Level)>();
        foreach (var region in regions)
        {
            var url = "https://api-search.plaync.com/aion2global/search/v2/character" +
                      $"?keyword={Uri.EscapeDataString(name)}&page=1&size=40&region={region}&localeInfo=en-US" +
                      (server > 0 ? $"&serverId={server}" : "");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("list", out var list)) continue;
            foreach (var x in list.EnumerateArray())
            {
                var n = TagRegex().Replace(x.GetProperty("name").GetString() ?? "", "");
                if (!n.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var sid = x.GetProperty("serverId").GetInt32();
                if (server > 0 && sid != server) continue;
                found.Add((region, Uri.UnescapeDataString(x.GetProperty("characterId").GetString() ?? ""), sid,
                    x.TryGetProperty("pcId", out var pc) ? pc.GetInt32() : 0,
                    x.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0));
            }
            if (regions.Length > 1) await Task.Delay(400, ct);
        }
        if (found.Count == 0) return null;

        // Nome repetido em outros servidores: prefere a mesma classe, depois o maior nível.
        var best = found
            .OrderByDescending(f => cls != GameClass.Unknown && PacketParser.ClassFromWire((uint)f.PcId) == cls)
            .ThenByDescending(f => f.Level)
            .First();

        var info = "https://aion2.plaync.com/api/character/info" +
                   $"?characterId={Uri.EscapeDataString(best.Id)}&serverId={best.Server}&region={best.Region}&lang=en-US";
        using var profileDoc = JsonDocument.Parse(await _http.GetStringAsync(info, ct));
        var root = profileDoc.RootElement;
        if (!root.TryGetProperty("profile", out var p)) return null;

        ulong? cp = p.TryGetProperty("combatPower", out var cpe) && cpe.TryGetUInt64(out var cpv) && cpv > 0 ? cpv : null;
        uint? il = p.TryGetProperty("itemLevel", out var ile) && ile.TryGetUInt32(out var ilv) && ilv > 0 ? ilv : null;
        if (il is null && root.TryGetProperty("stat", out var stat) && stat.TryGetProperty("statList", out var stats))
            foreach (var s in stats.EnumerateArray())
                if (s.GetProperty("type").GetString() == "ItemLevel" && s.GetProperty("value").TryGetUInt32(out var v) && v > 0)
                    il = v;
        return cp is null && il is null ? null : new ArmoryProfile(il, cp);
    }

    /// <summary>IDs de servidor Global = raça×1000 + região×100 + n.</summary>
    private static string? RegionOf(int server) => (server / 100 % 10) switch
    {
        1 => "nae",
        2 => "naw",
        3 => "eu",
        4 => "la",
        5 => "as",
        _ => null,
    };

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AtreiaDPS/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
