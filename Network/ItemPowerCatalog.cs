using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Item power de cada item (18/10/2026), pro painel do grupo.
///
/// Vem do items.json BRUTO do ao-bin-dumps — o formatado, que o ItemCatalog usa, não
/// tem IP. No bruto cada item de equipamento traz "@itempower" (o .0) e a lista
/// "enchantments" com o IP de cada encantamento, além de "@twohanded" nas armas.
/// É mais exato que a fórmula de tier do SAT (ItemController.GetBasicItemPower),
/// porque arma de artefato tem IP próprio.
///
/// O que NÃO entra: qualidade e maestria/especialização — o jogo não manda isso no
/// evento de equipamento. Por isso a tela chama de "IP do equipamento". O IP real
/// só vem quando alguém é INSPECIONADO no jogo (ver PlayerRegistry.HandleInspecao).
/// </summary>
public static class ItemPowerCatalog
{
    private const string Url = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/items.json";

    // unique name sem @ -> (IP por nível de encantamento 0..4, é de duas mãos)
    private static readonly ConcurrentDictionary<string, (int[] Ip, bool DuasMaos)> _itens = new(StringComparer.OrdinalIgnoreCase);
    private static int _carregando;

    public static bool Carregado => !_itens.IsEmpty;

    public static async Task EnsureLoadedAsync()
    {
        if (Carregado || Interlocked.CompareExchange(ref _carregando, 1, 0) != 0) return;
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "XnomercyApp", "items_raw_cache.json");
            var etagPath = cache + ".etag";
            string? json = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var req = new HttpRequestMessage(HttpMethod.Get, Url);
                if (File.Exists(etagPath) && File.Exists(cache))
                    req.Headers.TryAddWithoutValidation("If-None-Match", (await File.ReadAllTextAsync(etagPath)).Trim());
                using var resp = await http.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && File.Exists(cache))
                    json = await File.ReadAllTextAsync(cache);
                else
                {
                    resp.EnsureSuccessStatusCode();
                    json = await resp.Content.ReadAsStringAsync();
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    await File.WriteAllTextAsync(cache, json);
                    if (resp.Headers.ETag is not null) await File.WriteAllTextAsync(etagPath, resp.Headers.ETag.ToString());
                }
            }
            catch
            {
                if (File.Exists(cache)) json = await File.ReadAllTextAsync(cache);
            }
            if (json != null) await Task.Run(() => Carregar(json));
        }
        catch { /* sem IP: o painel do grupo mostra só a arma */ }
        finally { Interlocked.Exchange(ref _carregando, 0); }
    }

    /// <summary>Lê o items.json bruto (público pro teste).</summary>
    public static void Carregar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var itens)) return;
        foreach (var categoria in itens.EnumerateObject())
        {
            var lista = categoria.Value;
            if (lista.ValueKind == JsonValueKind.Object) Ler(lista);
            else if (lista.ValueKind == JsonValueKind.Array)
                foreach (var item in lista.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) Ler(item);
        }
    }

    private static void Ler(JsonElement item)
    {
        if (!item.TryGetProperty("@uniquename", out var u) || u.GetString() is not string nome) return;
        if (!item.TryGetProperty("@itempower", out var p) || !int.TryParse(p.GetString(), out int ip0)) return;
        var ip = new int[5];
        for (int i = 0; i < 5; i++) ip[i] = ip0 + 100 * i;   // padrão do jogo, se o encantamento não vier listado
        if (item.TryGetProperty("enchantments", out var enc) && enc.TryGetProperty("enchantment", out var lista))
        {
            foreach (var e in lista.ValueKind == JsonValueKind.Array ? lista.EnumerateArray().ToArray() : new[] { lista })
                if (e.TryGetProperty("@enchantmentlevel", out var lv) && int.TryParse(lv.GetString(), out int nivel)
                    && nivel is >= 1 and <= 4
                    && e.TryGetProperty("@itempower", out var ipe) && int.TryParse(ipe.GetString(), out int v))
                    ip[nivel] = v;
        }
        bool duasMaos = item.TryGetProperty("@twohanded", out var th) && th.GetString() == "true";
        _itens[nome] = (ip, duasMaos);
    }

    /// <summary>IP do item pelo unique name ("T6_2H_AXE@2"), ou null se não for equipamento.</summary>
    public static int? Ip(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return null;
        int arroba = uniqueName.IndexOf('@');
        string baseNome = arroba < 0 ? uniqueName : uniqueName[..arroba];
        int nivel = arroba < 0 ? 0 : int.TryParse(uniqueName[(arroba + 1)..], out var n) ? n : 0;
        if (!_itens.TryGetValue(baseNome, out var info) || nivel is < 0 or > 4) return null;
        return info.Ip[nivel];
    }

    public static bool DuasMaos(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return false;
        int arroba = uniqueName.IndexOf('@');
        return _itens.TryGetValue(arroba < 0 ? uniqueName : uniqueName[..arroba], out var info) && info.DuasMaos;
    }

    /// <summary>
    /// IP médio do equipamento, pela regra do jogo (a mesma do SAT): mão principal,
    /// mão secundária, cabeça, peito, pés e capa; arma de duas mãos conta nas duas
    /// mãos; espaço vazio conta zero. Bolsa e montaria ficam fora.
    /// Recebe os unique names na ordem do evento (0 principal, 1 secundária,
    /// 2 cabeça, 3 peito, 4 pés, 5 bolsa, 6 capa). null se nem a arma tem IP conhecido.
    /// </summary>
    public static double? IpMedio(IReadOnlyList<string?> slots)
    {
        string? arma = slots.Count > 0 ? slots[0] : null;
        if (Ip(arma) is not int ipArma) return null;
        var usados = new[] { 0, 1, 2, 3, 4, 6 };
        double soma = 0;
        foreach (int s in usados)
        {
            if (s == 1 && DuasMaos(arma)) { soma += ipArma; continue; }
            soma += s < slots.Count && Ip(slots[s]) is int v ? v : 0;
        }
        return soma / usados.Length;
    }

    /// <summary>Pra testes.</summary>
    public static void Limpar() => _itens.Clear();
}
