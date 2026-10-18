using System.Collections.Generic;
using System.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Registro de energia da guild, lido quando um oficial abre o registro no jogo
/// (pedido de 18/10/2026). Antes a gestão exportava o registro, salvava e subia a
/// planilha no site; agora o app lê a mesma resposta do jogo e manda com um clique.
/// A planilha continua valendo — o site descarta o que vier repetido.
///
/// Formato pelo código-fonte do SAT (Triky313, GPL-3.0, GetGuildAccountLogsResponse):
///   [0] = nomes  [3] = quantidades em FixPoint (valor × 10.000)  [4] = horários em ticks .NET (UTC)
///
/// Reconhecido pelo FORMATO, não pelo número da operação (que muda com patch): três
/// listas do mesmo tamanho, nomes de verdade e horários plausíveis. O envio não é
/// automático de propósito: o jogo pode mandar o registro de PRATA da guild no mesmo
/// formato, e o oficial vê o que vai antes de mandar.
/// </summary>
public static class GuildEnergyLog
{
    public sealed record Entrada(string Jogador, long Quantidade, System.DateTime QuandoUtc);

    private const long FixPoint = 10000L;
    private static readonly long TicksMin = new System.DateTime(2017, 1, 1).Ticks;   // lançamento do Albion
    private static readonly long TicksMax = new System.DateTime(2100, 1, 1).Ticks;

    /// <summary>Disparado quando chega um registro reconhecido.</summary>
    public static event System.Action<List<Entrada>>? Lido;

    public static void HandleOpResponse(PhotonOperationResponse op)
    {
        var lista = Ler(op.Parameters);
        if (lista != null) Lido?.Invoke(lista);
    }

    public static List<Entrada>? Ler(IReadOnlyDictionary<byte, object?> p)
    {
        if (!p.TryGetValue(0, out var n) || !p.TryGetValue(3, out var q) || !p.TryGetValue(4, out var t))
            return null;
        var nomes = n switch
        {
            string?[] s => s.Select(x => x ?? "").ToList(),
            object?[] o when o.All(x => x is string) => o.Select(x => (string)x!).ToList(),
            _ => null
        };
        var qtds = Longs(q);
        var ticks = Longs(t);
        if (nomes == null || qtds == null || ticks == null) return null;
        if (nomes.Count == 0 || nomes.Count != qtds.Count || nomes.Count != ticks.Count) return null;
        if (nomes.Any(x => x.Length < 2) || ticks.Any(x => x < TicksMin || x > TicksMax)) return null;

        return Enumerable.Range(0, nomes.Count)
            .Select(i => new Entrada(nomes[i],
                (long)System.Math.Round((double)qtds[i] / FixPoint),
                new System.DateTime(ticks[i] - ticks[i] % System.TimeSpan.TicksPerSecond, System.DateTimeKind.Utc)))
            .OrderBy(e => e.QuandoUtc).ToList();
    }

    /// <summary>JSON no formato que o site espera em /api/app/energia.</summary>
    public static string ParaJson(IEnumerable<Entrada> lista) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            registros = lista.Select(e => new
            {
                data = e.QuandoUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                jogador = e.Jogador,
                quantidade = e.Quantidade,
            })
        });

    private static List<long>? Longs(object? v) => v switch
    {
        byte[] a => a.Select(x => (long)x).ToList(),
        short[] a => a.Select(x => (long)x).ToList(),
        int[] a => a.Select(x => (long)x).ToList(),
        long[] a => a.ToList(),
        object?[] a when a.All(x => PhotonParam.ToLong(x) != null) => a.Select(x => PhotonParam.ToLong(x)!.Value).ToList(),
        _ => null
    };
}
