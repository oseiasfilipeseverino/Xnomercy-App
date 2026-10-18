using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Índice do mapa → nome (18/10/2026), pro histórico de mapas. Mesma fonte e mesmo
/// cache com ETag do ItemCatalog: o world.json formatado do ao-bin-dumps
/// ([{"Index":"4000","UniqueName":"Fort Sterling"}, ...]). O SAT usa o mesmo arquivo
/// (WorldData).
///
/// Mapa instanciado não está na lista: chega como "@RANDOMDUNGEON@&lt;guid&gt;",
/// "@ISLAND@...", "@HIDEOUT@..." — esses ganham nome pelo TIPO (ver Tipo).
/// </summary>
public static class MapCatalog
{
    private const string Url = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/world.json";
    private static readonly ConcurrentDictionary<string, string> _nomes = new(StringComparer.OrdinalIgnoreCase);
    private static int _carregando;

    public static async Task EnsureLoadedAsync()
    {
        if (!_nomes.IsEmpty || Interlocked.CompareExchange(ref _carregando, 1, 0) != 0) return;
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     "XnomercyApp", "world_cache.json");
            var etagPath = cache + ".etag";
            string? json = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
            if (json != null) Carregar(json);
        }
        catch { /* sem nome de mapa: o histórico mostra o índice */ }
        finally { Interlocked.Exchange(ref _carregando, 0); }
    }

    /// <summary>Lê o world.json (público pro teste).</summary>
    public static void Carregar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var m in doc.RootElement.EnumerateArray())
            if (m.TryGetProperty("Index", out var i) && i.GetString() is string idx
                && m.TryGetProperty("UniqueName", out var n) && n.GetString() is string nome)
                _nomes[idx] = nome;
    }

    /// <summary>
    /// Tipo do mapa pelo índice, na mesma ordem de checagem do WorldData.GetMapType do
    /// SAT ("" = mundo aberto/cidade).
    /// </summary>
    public static string Tipo(string indice)
    {
        var u = indice.ToUpperInvariant();
        if (u.Contains("HELLCLUSTER")) return "Hellgate";
        if (u.Contains("RANDOMDUNGEON")) return "Dungeon";
        if (u.Contains("CORRUPTEDDUNGEON")) return "Dungeon Corrompida";
        if (u.Contains("ISLAND")) return "Ilha";
        if (u.Contains("HIDEOUT")) return "Esconderijo";
        if (u.Contains("EXPEDITION")) return "Expedição";
        if (u.Contains("ARENA")) return "Arena";
        if (u.Contains("MISTSDUNGEON")) return "Dungeon das Brumas";
        if (u.Contains("MISTS")) return "Brumas";
        if (u.Contains("HELLDUNGEON")) return "Profundezas Abissais";
        if (u.Contains("DRAGONAREA")) return "Área do Dragão";
        return "";
    }

    /// <summary>Tipos que contam como "run": entra, faz, sai.</summary>
    public static bool EhDungeon(string tipo) => tipo is "Dungeon" or "Dungeon Corrompida" or "Hellgate"
        or "Expedição" or "Dungeon das Brumas" or "Profundezas Abissais";

    public static string Nome(string indice)
    {
        string tipo = Tipo(indice);
        if (tipo.Length > 0) return tipo;
        return _nomes.TryGetValue(indice, out var n) ? n : $"Mapa {indice}";
    }
}
