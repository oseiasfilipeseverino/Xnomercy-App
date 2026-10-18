using System.Collections.Generic;
using System.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Aba Mercado (pedido de 18/10/2026): vendas e compras no mercado, lidas das CARTAS
/// que o jogo manda quando uma ordem conclui. Base: código-fonte do SAT (Triky313,
/// GPL-3.0), Trade/Mails/MailController e GetMailInfosResponse.
///
/// Duas respostas do jogo:
///   1. Caixa de correio aberta: listas do mesmo tamanho com o ID da carta, o TIPO
///      ("MARKETPLACE_SELLORDER_FINISHED_SUMMARY"...) e o horário (ticks UTC).
///   2. Carta aberta: [0]=ID da carta [1]=conteúdo "qtd|ITEM|total|unitário|..."
///      com preços em FixPoint (prata × 10.000).
/// O VALOR só existe na carta aberta — limitação do jogo, o SAT tem a mesma.
///
/// Reconhecido pelo FORMATO (os índices da lista já mudaram entre versões do SAT:
/// um comentário dele mostra tipo em [10] e horário em [11]; o código atual usa [11] e
/// [12]). A carta só é aceita se o ID estiver na lista de cartas de mercado já vista.
/// </summary>
public sealed class MarketMailTracker
{
    public enum Tipo { Venda, Compra, VendaExpirada, CompraExpirada }

    public sealed record Negocio(long CartaId, Tipo Tipo, string Item, int Quantidade, long Prata, System.DateTime QuandoUtc);

    private const long FixPoint = 10000L;
    private static readonly long TicksMin = new System.DateTime(2017, 1, 1).Ticks;
    private static readonly long TicksMax = new System.DateTime(2100, 1, 1).Ticks;

    private readonly object _lock = new();
    private readonly Dictionary<long, (Tipo tipo, System.DateTime quando)> _cartas = new();
    private readonly Dictionary<long, Negocio> _negocios = new();

    public event System.Action? Mudou;

    public static Tipo? TipoDe(string texto) => texto switch
    {
        "MARKETPLACE_SELLORDER_FINISHED_SUMMARY" => Tipo.Venda,
        "MARKETPLACE_BUYORDER_FINISHED_SUMMARY" => Tipo.Compra,
        "MARKETPLACE_SELLORDER_EXPIRED_SUMMARY" or "BLACKMARKET_SELLORDER_EXPIRED_SUMMARY" => Tipo.VendaExpirada,
        "MARKETPLACE_BUYORDER_EXPIRED_SUMMARY" => Tipo.CompraExpirada,
        _ => null
    };

    public void HandleOpResponse(PhotonOperationResponse op) => Handle(op.Parameters);

    /// <summary>"lista", "carta" ou null.</summary>
    public string? Handle(IReadOnlyDictionary<byte, object?> p)
    {
        if (LerLista(p)) { Mudou?.Invoke(); return "lista"; }
        if (LerCarta(p)) { Mudou?.Invoke(); return "carta"; }
        return null;
    }

    private bool LerLista(IReadOnlyDictionary<byte, object?> p)
    {
        // A lista de TIPOS: um array de texto com pelo menos um tipo de mercado conhecido.
        var tipos = p.Values.OfType<string?[]>().FirstOrDefault(a => a.Any(t => t != null && TipoDe(t) != null));
        if (tipos == null) return false;
        int n = tipos.Length;
        var longs = p.Values.Select(Longs).Where(a => a != null && a.Count == n).Select(a => a!).ToList();
        var ticks = longs.FirstOrDefault(a => a.All(x => x >= TicksMin && x <= TicksMax));
        var ids = longs.FirstOrDefault(a => a != ticks && a.All(x => x > 0 && x < TicksMin));
        if (ticks == null || ids == null) return false;
        lock (_lock)
            for (int i = 0; i < n; i++)
                if (tipos[i] != null && TipoDe(tipos[i]!) is Tipo t)
                    _cartas[ids[i]] = (t, new System.DateTime(ticks[i] - ticks[i] % System.TimeSpan.TicksPerSecond, System.DateTimeKind.Utc));
        return true;
    }

    private bool LerCarta(IReadOnlyDictionary<byte, object?> p)
    {
        if (!p.TryGetValue(0, out var idv) || PhotonParam.ToLong(idv) is not long id) return false;
        if (!p.TryGetValue(1, out var cv) || cv is not string conteudo || !conteudo.Contains('|')) return false;
        lock (_lock)
        {
            if (!_cartas.TryGetValue(id, out var info)) return false;   // não é carta de mercado vista na lista
            var partes = conteudo.Split('|');
            if (partes.Length < 4 || !int.TryParse(partes[0], out var qtd) || !long.TryParse(partes[2], out var total))
                return false;
            _negocios[id] = new Negocio(id, info.tipo, partes[1], qtd, total / FixPoint, info.quando);
        }
        return true;
    }

    public List<Negocio> Negocios() { lock (_lock) return _negocios.Values.OrderByDescending(x => x.QuandoUtc).ToList(); }

    public long Total(Tipo tipo) { lock (_lock) return _negocios.Values.Where(x => x.Tipo == tipo).Sum(x => x.Prata); }

    public int CartasSemValor()
    {
        lock (_lock) return _cartas.Keys.Count(k => !_negocios.ContainsKey(k) && _cartas[k].tipo is Tipo.Venda or Tipo.Compra);
    }

    public void Reset() { lock (_lock) { _cartas.Clear(); _negocios.Clear(); } Mudou?.Invoke(); }

    private static List<long>? Longs(object? v) => v switch
    {
        long[] a => a.ToList(),
        int[] a => a.Select(x => (long)x).ToList(),
        object?[] a when a.Length > 0 && a.All(x => PhotonParam.ToLong(x) != null) => a.Select(x => PhotonParam.ToLong(x)!.Value).ToList(),
        _ => null
    };
}
