using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Xml.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Resolve o "CausingSpellIndex" do evento HealthUpdate (param [7]) pro nome da
/// habilidade — base pra quebra de dano por skill no Medidor de Dano.
///
/// O Albion não numera habilidades com um índice explícito em lugar nenhum dos dados
/// públicos: o índice usado pelo protocolo de rede é a POSIÇÃO do elemento dentro do
/// arquivo spells.xml do jogo (ao-bin-dumps), pulando &lt;colortag&gt; e contando uma
/// posição extra pra cada &lt;activespell&gt; que tenha &lt;channelingspell&gt; dentro
/// (a variante "canalizada" ocupa o próximo índice). Esse algoritmo de indexação foi
/// replicado fielmente do projeto AlbionOnline-StatisticsAnalysis (Triky313,
/// GPL-3.0 — GameFileData/SpellData.cs), que é a referência usada por toda a
/// comunidade pra decodificar esse índice. Qualquer desvio no algoritmo desalinha
/// TODOS os índices depois do ponto do erro, então a ordem de iteração e a regra de
/// "só conta se tiver uniquename" precisam ser exatamente iguais.
/// </summary>
public static class SpellCatalog
{
    private const string SpellsUrl = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/spells.xml";
    private static readonly ConcurrentDictionary<int, string> _byIndex = new();
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
                "XnomercyApp", "spells_cache.xml");

            string? xml = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                xml = await http.GetStringAsync(SpellsUrl);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, xml);
            }
            catch
            {
                if (File.Exists(cachePath))
                    xml = await File.ReadAllTextAsync(cachePath);
            }

            if (xml is null) return;

            var doc = XDocument.Parse(xml);
            int index = 0;
            foreach (var element in doc.Root!.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "colortag":
                        continue;   // não ocupa índice
                    case "passivespell":
                    case "togglespell":
                        AddIfNamed(index++, element);
                        break;
                    case "activespell":
                        AddIfNamed(index++, element);
                        if (element.Element("channelingspell") != null)
                            AddIfNamed(index++, element);   // variante canalizada ocupa o próximo índice
                        break;
                    // Outros elementos de topo (se o jogo adicionar algum novo tipo) não
                    // ocupam índice — só os 4 tipos acima geram entrada no protocolo.
                }
            }
            _loaded = true;
        }
        catch
        {
            // Sem internet e sem cache local na 1ª execução: fica sem nomes de skill,
            // o Medidor de Dano continua funcionando só com os índices numéricos.
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void AddIfNamed(int index, XElement element)
    {
        var uniqueName = element.Attribute("uniquename")?.Value;
        if (!string.IsNullOrEmpty(uniqueName))
            _byIndex[index] = uniqueName;
    }

    /// <summary>Nome da habilidade (UniqueName do jogo, ex: "FIRE_FIREBALL"), ou null se
    /// o índice for desconhecido (dados ainda não carregados, ou índice 0 = "sem skill"/
    /// dano básico de arma).</summary>
    public static string? GetName(int spellIndex) =>
        _byIndex.TryGetValue(spellIndex, out var name) ? name : null;
}
