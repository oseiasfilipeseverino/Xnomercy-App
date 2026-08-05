using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

public sealed class SessionHistoryEntry
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long Fame { get; set; }
    public long YellowFame { get; set; }
    public long Silver { get; set; }
    public long Damage { get; set; }
    public int LootItems { get; set; }
}

/// <summary>
/// Guarda um resumo (fama/prata/dano/itens de loot) toda vez que a captura é
/// parada — pra o jogador conseguir olhar sessões passadas sem precisar ter
/// anotado na hora. Só local, em JSON no %LocalAppData%\XnomercyApp\ (mesma
/// pasta de errors.log/diag_consent.txt — ver App.xaml.cs/DiagReporter.cs),
/// nada é enviado pro servidor.
/// </summary>
public static class SessionHistoryStore
{
    private const int MaxEntries = 50; // limite pra não crescer sem fim numa conta usada há meses

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "sessions.json");

    public static List<SessionHistoryEntry> Load() => TryLoad(out var list) ? list : new();

    /// <summary>
    /// Lê o histórico separando "não tem arquivo" de "não consegui ler". A
    /// diferença importa: sem ela, uma falha de leitura devolvia lista vazia e o
    /// Append gravava por cima — o histórico inteiro sumia de vez. Devolve false
    /// só quando o arquivo existe mas não pôde ser lido.
    /// </summary>
    private static bool TryLoad(out List<SessionHistoryEntry> list)
    {
        list = new();
        if (!File.Exists(FilePath)) return true;
        try
        {
            var json = File.ReadAllText(FilePath);
            list = JsonSerializer.Deserialize<List<SessionHistoryEntry>>(json) ?? new();
            return true;
        }
        catch (Exception ex)
        {
            LogLocal($"falha ao ler o historico: {ex.Message}");
            return false;
        }
    }

    private static void LogLocal(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SessionHistory] {msg}\n");
        }
        catch { /* não há o que fazer se nem o log grava */ }
    }

    public static void Append(SessionHistoryEntry entry)
    {
        try
        {
            if (!TryLoad(out var list))
            {
                // Não deu pra ler o que já existia. Gravar agora escreveria uma
                // lista com UMA entrada por cima de tudo. Guarda o ilegível de
                // lado — assim dá pra recuperar depois — e recomeça do zero.
                var quebrado = FilePath + ".corrompido";
                try
                {
                    if (File.Exists(quebrado)) File.Delete(quebrado);
                    File.Move(FilePath, quebrado);
                    LogLocal($"historico ilegivel movido para {quebrado}");
                }
                catch (Exception ex)
                {
                    LogLocal($"nao consegui preservar o historico ilegivel: {ex.Message}");
                    return;   // na dúvida, não escreve — melhor não gravar do que apagar
                }
            }
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
            Directory.CreateDirectory(dir);

            // Grava num temporário e só então troca pelo definitivo. WriteAllText
            // direto no arquivo real trunca antes de escrever: se o app fechasse
            // (ou faltasse energia) no meio, o sessions.json ficava cortado.
            // Com o troco atômico, o arquivo antigo continua íntegro até o novo
            // estar completo — e se mesmo assim vier ilegível, o TryLoad acima
            // guarda o original em vez de deixar a gravação apagar tudo.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
        catch
        {
            // Histórico é best-effort — nunca pode derrubar o fluxo de parar a captura.
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* ignore */ }
    }
}
