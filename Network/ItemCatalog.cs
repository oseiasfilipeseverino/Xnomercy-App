using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Resolve o "Index" numérico de item (usado pelo jogo internamente, inclusive nos
/// eventos de rede) para o nome em PT-BR — mesma fonte de dados que o site já usa
/// (ao-bin-dumps "formatted/items.json"), assim os nomes ficam consistentes entre
/// o site e o app.
/// </summary>
public static class ItemCatalog
{
    private const string ItemsUrl = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/items.json";
    // ConcurrentDictionary: é populado numa task de fundo e lido pela thread de captura
    // e pela UI ao mesmo tempo — com Dictionary normal, ler durante a carga podia corromper.
    private static readonly ConcurrentDictionary<int, (string NamePt, string UniqueName)> _byIndex = new();
    private static bool _loaded;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;

            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XnomercyApp", "items_cache.json");

            string? json = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                json = await http.GetStringAsync(ItemsUrl);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, json);
            }
            catch
            {
                // Sem internet/erro de rede — usa o cache local da última vez que funcionou.
                if (File.Exists(cachePath))
                    json = await File.ReadAllTextAsync(cachePath);
            }

            if (json is null) return;

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("Index", out var indexProp)) continue;
                if (!int.TryParse(indexProp.GetString(), out int index)) continue;
                if (!item.TryGetProperty("UniqueName", out var uniqueNameProp)) continue;

                string uniqueName = uniqueNameProp.GetString() ?? "";
                string namePt = uniqueName;
                if (item.TryGetProperty("LocalizedNames", out var namesProp) && namesProp.ValueKind == JsonValueKind.Object)
                {
                    if (namesProp.TryGetProperty("PT-BR", out var ptProp))
                        namePt = ptProp.GetString() ?? uniqueName;
                    else if (namesProp.TryGetProperty("EN-US", out var enProp))
                        namePt = enProp.GetString() ?? uniqueName;
                }

                _byIndex[index] = (namePt, uniqueName);
            }
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Retorna o nome em PT-BR do item, ou null se o índice não for um item conhecido.</summary>
    public static string? GetName(int index) => _byIndex.TryGetValue(index, out var v) ? v.NamePt : null;

    public static string? GetUniqueName(int index) => _byIndex.TryGetValue(index, out var v) ? v.UniqueName : null;
}
