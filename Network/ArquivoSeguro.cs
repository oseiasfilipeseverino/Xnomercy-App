using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Leitura e gravação dos arquivos do app em %LocalAppData%\XnomercyApp (18/10/2026).
///
/// Antes cada arquivo (baús, mapas, lutas, mortes, valores, numeração) fazia
/// "não consegui ler → começa vazio" e, na próxima gravação, escrevia o vazio por
/// cima do arquivo de verdade: um erro de leitura virava perda de dados. No mesmo
/// dia o baus.json de um usuário foi parar em "[]".
///
/// Agora:
///  - cada gravação troca o arquivo de uma vez (temporário + File.Replace) e guarda
///    a versão anterior em ".bak";
///  - se o arquivo não dá pra ler, tenta o ".bak"; se nem ele, guarda o ilegível
///    como ".ilegivel-AAAAMMDD-HHMMSS" antes de deixar o app recomeçar do zero;
///  - se nem guardar de lado der, devolve false e quem chamou NÃO grava por cima.
/// </summary>
public static class ArquivoSeguro
{
    /// <summary>
    /// Lê o JSON do arquivo. Devolve false só quando o arquivo existe, não dá pra ler
    /// (nem pelo .bak) e nem deu pra guardá-lo de lado — aí quem chamou não pode gravar.
    /// </summary>
    public static bool Ler<T>(string caminho, out T? valor)
    {
        valor = default;
        string bak = caminho + ".bak";
        if (!File.Exists(caminho))
        {
            // Sem o principal mas com o .bak (o app fechou no meio de uma troca): usa o .bak.
            if (File.Exists(bak) && TentarLer(bak, out valor)) Log($"{Path.GetFileName(caminho)} sumiu; usando o .bak");
            return true;
        }
        if (TentarLer(caminho, out valor)) return true;
        if (File.Exists(bak) && TentarLer(bak, out valor))
        {
            Log($"{Path.GetFileName(caminho)} ilegivel; usando o .bak");
            return true;
        }
        try
        {
            string ilegivel = $"{caminho}.ilegivel-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(caminho, ilegivel);
            Log($"{Path.GetFileName(caminho)} ilegivel; guardado como {Path.GetFileName(ilegivel)}");
            valor = default;
            return true;
        }
        catch (Exception ex)
        {
            Log($"{Path.GetFileName(caminho)} ilegivel e nao deu pra guardar de lado ({ex.Message}): nao vou gravar por cima");
            valor = default;
            return false;
        }
    }

    /// <summary>Grava trocando o arquivo de uma vez; a versão anterior fica em ".bak" (guardarAnterior).</summary>
    public static bool Gravar(string caminho, string conteudo, bool guardarAnterior = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
            string tmp = caminho + ".tmp";
            File.WriteAllText(tmp, conteudo);
            if (File.Exists(caminho)) File.Replace(tmp, caminho, guardarAnterior ? caminho + ".bak" : null, ignoreMetadataErrors: true);
            else File.Move(tmp, caminho);
            return true;
        }
        catch (Exception ex)
        {
            Log($"nao gravei {Path.GetFileName(caminho)}: {ex.Message}");
            return false;
        }
    }

    public static bool GravarJson<T>(string caminho, T valor) => Gravar(caminho, JsonSerializer.Serialize(valor));

    private static bool TentarLer<T>(string caminho, out T? valor)
    {
        valor = default;
        try
        {
            valor = JsonSerializer.Deserialize<T>(File.ReadAllText(caminho));
            return true;
        }
        catch { return false; }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = XnomercyApp.Network.PastaDados.Raiz;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Arquivo] {msg}\n");
        }
        catch { /* sem log não há o que fazer */ }
    }
}
