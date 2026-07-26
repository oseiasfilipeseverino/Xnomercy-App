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

    public static List<SessionHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<SessionHistoryEntry>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void Append(SessionHistoryEntry entry)
    {
        try
        {
            var list = Load();
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
            Directory.CreateDirectory(dir);

            // Grava num temporário e só então troca pelo definitivo. WriteAllText
            // direto no arquivo real trunca antes de escrever: se o app fechasse
            // (ou faltasse energia) no meio, o sessions.json ficava cortado e o
            // Load() — que engole erro e devolve lista vazia — apagaria TODO o
            // histórico de sessões silenciosamente. Com o troco atômico, o
            // arquivo antigo continua íntegro até o novo estar completo.
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
