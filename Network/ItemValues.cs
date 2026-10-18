using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Valor estimado de item, em prata por unidade (18/10/2026) — o que dá o valor do
/// loot no Loot Log.
///
/// Duas fontes, nesta ordem:
///
///   1. O PRÓPRIO JOGO. Quando um item aparece (no seu inventário, num baú, no chão),
///      o evento de item novo traz o valor estimado que o jogo calcula pro item:
///        30 NewEquipmentItem / 31 NewSiegeBannerItem / 32 NewSimpleItem
///        [0]=ObjectId [1]=índice do item [2]=quantidade [4]=valor estimado ×10.000
///      É o que o SAT faz (EstimatedMarketValueController), e guarda em disco pra a
///      próxima sessão já começar sabendo. Conferido no pacote real de 12/10:
///      T4_MEAL_STEW_AVALON, [4]=159.022.995 → 15.902 de prata por unidade.
///
///   2. O MERCADO (Albion Online Data, servidor Americas — o mesmo que o site usa),
///      pro que o jogo ainda não mostrou. Pega a MEDIANA do menor preço de venda das
///      cidades, só com preço dos últimos 7 dias: um anúncio absurdo numa cidade só
///      não puxa o valor (o caso do omelete de 33 milhões que o site já pegou).
///
/// Nenhuma das duas é o "preço que você vai vender", e a tela diz "estimado".
/// </summary>
public static class ItemValues
{
    private const long FixPoint = 10_000;
    private const string Api = "https://west.albion-online-data.com/api/v2/stats/prices/";
    private static readonly TimeSpan ValidadeMercado = TimeSpan.FromHours(6);

    // índice do item -> prata por unidade (do jogo)
    private static readonly ConcurrentDictionary<int, long> _jogo = new();
    // unique name -> (prata por unidade, quando buscou) — do mercado; 0 = mercado sem preço
    private static readonly ConcurrentDictionary<string, (long Valor, DateTime Quando)> _mercado = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _naFila = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static int _buscando;
    private static volatile bool _sujo;
    private static Timer? _timerSalvar;

    /// <summary>Algum valor novo chegou (do jogo ou do mercado). Dispara fora da UI.</summary>
    public static event Action? Mudou;

    /// <summary>Pra testes: a busca no mercado pode ser trocada por um dicionário fixo.</summary>
    public static Func<IReadOnlyList<string>, Task<Dictionary<string, long>>>? BuscarMercado { get; set; }

    private static string Arquivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "valores_itens.json");

    /// <summary>Lê o que o jogo já mostrou em sessões anteriores e liga o salvamento periódico.</summary>
    public static void Carregar()
    {
        try
        {
            if (File.Exists(Arquivo))
            {
                var d = JsonSerializer.Deserialize<Dictionary<int, long>>(File.ReadAllText(Arquivo));
                if (d != null) foreach (var kv in d) _jogo.TryAdd(kv.Key, kv.Value);
            }
        }
        catch { /* arquivo estragado: começa do zero, o jogo repõe */ }
        _timerSalvar ??= new Timer(_ => Salvar(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static void Salvar()
    {
        if (!_sujo) return;
        _sujo = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Arquivo)!);
            var tmp = Arquivo + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_jogo.ToDictionary(kv => kv.Key, kv => kv.Value)));
            File.Move(tmp, Arquivo, overwrite: true);
        }
        catch { _sujo = true; }
    }

    /// <summary>Evento de item novo com valor? Guarda. true se aprendeu algo.</summary>
    public static bool Observar(PhotonEvent evt)
    {
        int c = evt.EventCode;
        int simples = GameEventCodes.LootPickup;   // 32 (NewSimpleItem); 30 e 31 vêm logo antes
        if (c < simples - 2 || c > simples) return false;
        var p = evt.Parameters;
        // Formato de item: [1] índice de item que existe no catálogo, [2] quantidade,
        // [4] valor. Nada de texto no [5] — o 30 já foi lido como movimento de
        // jogador (nome no [5]) e não pode virar valor de item.
        if (p.TryGetValue(5, out var t5) && t5 is string) return false;
        if (!p.TryGetValue(1, out var i1) || PhotonParam.ToLong(i1) is not long idx || idx < 0 || idx > int.MaxValue) return false;
        if (!p.TryGetValue(4, out var v4) || PhotonParam.ToLong(v4) is not long bruto || bruto <= 0) return false;
        if (p.TryGetValue(2, out var q2) && PhotonParam.ToLong(q2) is long q && (q <= 0 || q > 100_000)) return false;
        if (ItemCatalog.GetUniqueName((int)idx) == null) return false;
        long valor = bruto / FixPoint;
        if (valor <= 0 || valor > 100_000_000_000) return false;
        if (_jogo.TryGetValue((int)idx, out var antigo) && antigo == valor) return false;
        _jogo[(int)idx] = valor;
        _sujo = true;
        Mudou?.Invoke();
        return true;
    }

    /// <summary>Prata por unidade, ou null se ainda não sabemos (aí pede ao mercado).</summary>
    public static long? Valor(int itemIndex)
    {
        if (_jogo.TryGetValue(itemIndex, out var v)) return v;
        var uniq = ItemCatalog.GetUniqueName(itemIndex);
        if (uniq == null) return null;
        if (_mercado.TryGetValue(uniq, out var m) && DateTime.UtcNow - m.Quando < ValidadeMercado)
            return m.Valor > 0 ? m.Valor : null;
        Pedir(uniq);
        return null;
    }

    /// <summary>De onde veio o valor (pra dica da tela).</summary>
    public static string Fonte(int itemIndex) =>
        _jogo.ContainsKey(itemIndex) ? "valor estimado pelo jogo" : "preço do mercado (Americas)";

    private static void Pedir(string uniq)
    {
        if (!_naFila.TryAdd(uniq, 0)) return;
        if (Interlocked.CompareExchange(ref _buscando, 1, 0) != 0) return;   // a busca em andamento pega a fila
        _ = Task.Run(BuscarFilaAsync);
    }

    private static async Task BuscarFilaAsync()
    {
        try
        {
            await Task.Delay(400);   // junta os pedidos de uma rajada de loot numa consulta só
            while (!_naFila.IsEmpty)
            {
                var lote = _naFila.Keys.Take(40).ToList();
                foreach (var u in lote) _naFila.TryRemove(u, out _);
                Dictionary<string, long> precos;
                try { precos = await (BuscarMercado ?? ConsultarAodpAsync)(lote); }
                catch { precos = new(); }
                var agora = DateTime.UtcNow;
                foreach (var u in lote)
                    _mercado[u] = (precos.TryGetValue(u, out var p) ? p : 0, agora);
                if (precos.Count > 0) Mudou?.Invoke();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _buscando, 0);
            if (!_naFila.IsEmpty && Interlocked.CompareExchange(ref _buscando, 1, 0) == 0)
                _ = Task.Run(BuscarFilaAsync);
        }
    }

    private static async Task<Dictionary<string, long>> ConsultarAodpAsync(IReadOnlyList<string> itens)
    {
        var url = Api + string.Join(",", itens.Select(Uri.EscapeDataString)) + ".json?qualities=1";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return MedianaPorItem(doc.RootElement, DateTime.UtcNow);
    }

    /// <summary>Mediana do sell_price_min das cidades com preço dos últimos 7 dias.</summary>
    public static Dictionary<string, long> MedianaPorItem(JsonElement lista, DateTime agoraUtc)
    {
        var porItem = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in lista.EnumerateArray())
        {
            if (!e.TryGetProperty("item_id", out var id) || id.GetString() is not string item) continue;
            // Mercado Negro não é anúncio de venda que alguém possa comprar: fica fora.
            if (e.TryGetProperty("city", out var cid) && cid.GetString() is string cidade
                && cidade.Contains("Black Market", StringComparison.OrdinalIgnoreCase)) continue;
            if (!e.TryGetProperty("sell_price_min", out var pr) || !pr.TryGetInt64(out long preco) || preco <= 0) continue;
            if (e.TryGetProperty("sell_price_min_date", out var dt) && DateTime.TryParse(dt.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var quando)
                && agoraUtc - quando > TimeSpan.FromDays(7)) continue;
            (porItem.TryGetValue(item, out var l) ? l : porItem[item] = new()).Add(preco);
        }
        var r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, precos) in porItem)
        {
            precos.Sort();
            int n = precos.Count;
            r[item] = n % 2 == 1 ? precos[n / 2] : (precos[n / 2 - 1] + precos[n / 2]) / 2;
        }
        return r;
    }

    /// <summary>Pra testes.</summary>
    public static void Limpar() { _jogo.Clear(); _mercado.Clear(); _naFila.Clear(); }
}
