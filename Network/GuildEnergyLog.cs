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
/// listas do mesmo tamanho, nomes de verdade e horários plausíveis. O registro de
/// PRATA da guild vem no mesmo formato, então o envio só é automático quando o app viu
/// o pedido da aba de ENERGIA ([1]=2); sem ver o pedido, o oficial confere antes.
/// </summary>
public static class GuildEnergyLog
{
    public sealed record Entrada(string Jogador, long Quantidade, System.DateTime QuandoUtc);

    private const long FixPoint = 10000L;
    private static readonly long TicksMin = new System.DateTime(2017, 1, 1).Ticks;   // lançamento do Albion
    private static readonly long TicksMax = new System.DateTime(2100, 1, 1).Ticks;

    /// <summary>Registro reconhecido; o bool diz se o app VIU o pedido da aba de energia (pode ir sozinho pro site).</summary>
    public static event System.Action<List<Entrada>, bool>? Lido;

    // ── Qual ABA do registro foi aberta (sessão real de 18/10/2026) ─────────────
    // O pedido do registro (op 159) leva [1]=aba, [2]=a partir de qual linha (200,
    // 300...) e [3]=101 linhas. A aba de ENERGIA é a 2: a resposta trouxe [3] com
    // ±100000 (= ±10 de energia: saque negativo, depósito positivo), [1] com o tipo
    // de cada linha (3 saque, 2 depósito) e [4] as horas. A aba de prata responde
    // no mesmo formato — sem esta trava, prata seria oferecida pro site como energia.
    public const int AbaEnergia = 2;
    private static int? _ultimaAba;

    public static void HandleOpRequest(PhotonOperationRequest op) => Pedido(op.Parameters);

    public static void Pedido(IReadOnlyDictionary<byte, object?> p)
    {
        if (p.TryGetValue(253, out var c) && PhotonParam.ToLong(c) == 159
            && p.TryGetValue(1, out var a) && PhotonParam.ToLong(a) is long aba)
            _ultimaAba = (int)aba;
    }

    public static void HandleOpResponse(PhotonOperationResponse op)
    {
        // Resposta do registro de OUTRA aba (prata, itens...): não é energia.
        if (op.Parameters.TryGetValue(253, out var c) && PhotonParam.ToLong(c) == 159
            && _ultimaAba is int aba && aba != AbaEnergia) return;
        var lista = Ler(op.Parameters);
        if (lista == null) return;
        CaptureHealth.Marcar("Energia da guild");
        Lido?.Invoke(lista, _ultimaAba == AbaEnergia);
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
        // Nos diagnósticos de setembro o [3] vinha como byte[] (0 a 255): era OUTRA aba
        // (pedido com [1]=0 ou 3). Na aba de energia (sessão de 18/10) o [3] é int[]
        // com a quantidade em FixPoint. byte[] não é quantidade de energia.
        if (q is byte[]) return null;
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
