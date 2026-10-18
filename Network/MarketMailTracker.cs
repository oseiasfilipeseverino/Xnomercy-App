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

    /// <summary>CartaId: ID da carta, ou um ID interno negativo pra negociação instantânea.</summary>
    public sealed record Negocio(long CartaId, Tipo Tipo, string Item, int Quantidade, long Prata, System.DateTime QuandoUtc, bool Instantanea = false);

    // Anúncios e ordens vistos ao olhar o mercado (resposta do jogo com uma lista de
    // JSON). Compra/venda INSTANTÂNEA não gera carta: o pedido só traz o ID do anúncio
    // e a quantidade, e o preço sai daqui (mesmo método do MarketController do SAT).
    private sealed record Anuncio(long Id, long PrecoUnitario, int Quantidade, string Item, bool Compra);
    private readonly Dictionary<long, Anuncio> _anuncios = new();
    private long _proximoIdInstantaneo = -1;

    // Negócio na hora esperando a resposta do jogo: (código da operação, ID interno,
    // quando). A compra entra na tela no PEDIDO, e sai se a resposta da MESMA operação
    // vier com erro (o item já tinha sido vendido, faltou prata...). Sem resposta em
    // EsperaResposta, fica valendo. Até 18/10/2026 todo pedido virava compra — o SAT
    // tem o mesmo problema (AuctionBuyOfferRequestHandler registra no pedido).
    private readonly List<(int Op, long Id, System.DateTime Quando)> _aguardando = new();
    private static readonly System.TimeSpan EsperaResposta = System.TimeSpan.FromSeconds(15);

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

    public void HandleOpResponse(PhotonOperationResponse op)
    {
        if (op.Parameters.TryGetValue(253, out var c) && PhotonParam.ToLong(c) is long codigo
            && ConfirmarResposta((int)codigo, op.ReturnCode) == "recusada")
            Mudou?.Invoke();
        if (Handle(op.Parameters) != null) CaptureHealth.Marcar("Vendas");
    }

    public void HandleOpRequest(PhotonOperationRequest op)
    {
        if (HandleRequest(op.Parameters) == null) return;
        CaptureHealth.Marcar("Vendas");
        Mudou?.Invoke();
    }

    /// <summary>
    /// Resposta do jogo a uma operação. Se havia negócio na hora esperando ESSA
    /// operação: código 0 confirma ("confirmada"), qualquer outro desfaz ("recusada").
    /// null quando não havia nada esperando.
    /// </summary>
    public string? ConfirmarResposta(int operacao, short codigoRetorno)
    {
        lock (_lock)
        {
            var agora = System.DateTime.UtcNow;
            _aguardando.RemoveAll(x => agora - x.Quando > EsperaResposta);   // sem resposta: fica valendo
            int i = _aguardando.FindIndex(x => x.Op == operacao);
            if (i < 0) return null;
            var (_, id, _) = _aguardando[i];
            _aguardando.RemoveAt(i);
            if (codigoRetorno == 0) return "confirmada";
            _negocios.Remove(id);
            return "recusada";
        }
    }

    /// <summary>"lista", "carta" ou null.</summary>
    public string? Handle(IReadOnlyDictionary<byte, object?> p)
    {
        if (LerAnuncios(p)) return "anuncios";
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

    /// <summary>Lista do mercado aberta no jogo: [0] = textos JSON com Id, UnitPriceSilver, Amount, ItemTypeId, AuctionType.</summary>
    private bool LerAnuncios(IReadOnlyDictionary<byte, object?> p)
    {
        if (!p.TryGetValue(0, out var v) || v is not string?[] textos || textos.Length == 0) return false;
        if (!textos.Any(t => t != null && t.Contains("UnitPriceSilver") && t.Contains("ItemTypeId"))) return false;
        int n = 0;
        foreach (var t in textos)
        {
            if (t == null) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(t);
                var r = doc.RootElement;
                var tipo = r.TryGetProperty("AuctionType", out var at) ? at.GetString() ?? "" : "";
                var a = new Anuncio(r.GetProperty("Id").GetInt64(), r.GetProperty("UnitPriceSilver").GetInt64(),
                    r.TryGetProperty("Amount", out var am) ? am.GetInt32() : 0,
                    r.TryGetProperty("ItemTypeId", out var it) ? it.GetString() ?? "" : "",
                    tipo.Equals("request", System.StringComparison.OrdinalIgnoreCase));
                lock (_lock)
                {
                    if (_anuncios.Count > 5000) _anuncios.Clear();   // teto: só importa o que está na tela agora
                    _anuncios[a.Id] = a;
                }
                n++;
            }
            catch (System.Exception) { /* texto que não é anúncio: ignora */ }
        }
        return n > 0;
    }

    /// <summary>
    /// Pedido de compra/venda instantânea: "compra" ([1]=qtd [2]=ID do anúncio de venda)
    /// ou "venda" ([1]=ID da ordem de compra [4]=qtd). null se o ID não é de anúncio visto.
    /// </summary>
    public string? HandleRequest(IReadOnlyDictionary<byte, object?> p)
    {
        lock (_lock)
        {
            if (p.TryGetValue(2, out var a2) && PhotonParam.ToLong(a2) is long idOferta
                && _anuncios.TryGetValue(idOferta, out var oferta) && !oferta.Compra
                && p.TryGetValue(1, out var q1) && PhotonParam.ToLong(q1) is long qtdC && qtdC > 0 && qtdC <= oferta.Quantidade)
            {
                Aguardar(p, Registrar(Tipo.Compra, oferta, (int)qtdC));
                return "compra";
            }
            if (p.TryGetValue(1, out var a1) && PhotonParam.ToLong(a1) is long idOrdem
                && _anuncios.TryGetValue(idOrdem, out var ordem) && ordem.Compra
                && p.TryGetValue(4, out var q4) && PhotonParam.ToLong(q4) is long qtdV && qtdV > 0 && qtdV <= ordem.Quantidade)
            {
                Aguardar(p, Registrar(Tipo.Venda, ordem, (int)qtdV));
                return "venda";
            }
        }
        return null;
    }

    private long Registrar(Tipo tipo, Anuncio a, int qtd)
    {
        long id = _proximoIdInstantaneo--;
        _negocios[id] = new Negocio(id, tipo, a.Item, qtd, a.PrecoUnitario * qtd / FixPoint, System.DateTime.UtcNow, Instantanea: true);
        return id;
    }

    /// <summary>Guarda o negócio esperando a resposta da operação [253] do pedido. Chamar com o _lock.</summary>
    private void Aguardar(IReadOnlyDictionary<byte, object?> pedido, long id)
    {
        if (pedido.TryGetValue(253, out var c) && PhotonParam.ToLong(c) is long op)
            _aguardando.Add(((int)op, id, System.DateTime.UtcNow));
    }

    public List<Negocio> Negocios() { lock (_lock) return _negocios.Values.OrderByDescending(x => x.QuandoUtc).ToList(); }

    public long Total(Tipo tipo) { lock (_lock) return _negocios.Values.Where(x => x.Tipo == tipo).Sum(x => x.Prata); }

    public int CartasSemValor()
    {
        lock (_lock) return _cartas.Keys.Count(k => !_negocios.ContainsKey(k) && _cartas[k].tipo is Tipo.Venda or Tipo.Compra);
    }

    public void Reset() { lock (_lock) { _cartas.Clear(); _negocios.Clear(); _anuncios.Clear(); _aguardando.Clear(); } Mudou?.Invoke(); }

    private static List<long>? Longs(object? v) => v switch
    {
        long[] a => a.ToList(),
        int[] a => a.Select(x => (long)x).ToList(),
        object?[] a when a.Length > 0 && a.All(x => PhotonParam.ToLong(x) != null) => a.Select(x => PhotonParam.ToLong(x)!.Value).ToList(),
        _ => null
    };
}
