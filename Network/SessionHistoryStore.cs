using System.IO;

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
    // Desde 18/10/2026 (sessões antigas leem 0): valor estimado do SEU loot, seu DPS
    // e o dano que você recebeu.
    public long LootValue { get; set; }
    public long Dps { get; set; }
    public long Taken { get; set; }
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

    private static string FilePath => System.IO.Path.Combine(XnomercyApp.Network.PastaDados.Raiz, "sessions.json");

    public static List<SessionHistoryEntry> Load() => TryLoad(out var list) ? list : new();

    /// <summary>
    /// Lê pelo ArquivoSeguro, como baús, mapas e lutas (revisão de 29/10, A-08).
    /// Antes qualquer falha de leitura, até o antivírus segurando o arquivo por um
    /// instante, era "corrompido": o histórico ia pra sessions.json.corrompido e
    /// recomeçava vazio, e não havia .bak pra voltar. Devolve false quando não deu
    /// pra ler e o arquivo continua lá — aí ninguém grava por cima.
    /// </summary>
    private static bool TryLoad(out List<SessionHistoryEntry> list)
    {
        bool ok = ArquivoSeguro.Ler<List<SessionHistoryEntry>>(FilePath, out var lida);
        list = lida ?? new();
        return ok;
    }

    private static void LogLocal(string msg)
    {
        LogArquivo.Erro("SessionHistory", msg);
    }

    public static void Append(SessionHistoryEntry entry)
    {
        try
        {
            if (!TryLoad(out var list))
            {
                // Não deu pra ler o que já existia (ocupado, ou ilegível sem ter
                // como guardar de lado). Gravar agora escreveria UMA entrada por
                // cima de tudo: na dúvida, esta sessão fica sem registro.
                LogLocal("historico nao lido; esta sessao nao foi gravada");
                return;
            }
            // Mesma sessão (mesmo início) guardada de novo — parar e religar a captura, ou
            // fechar o app depois de já ter parado: substitui, não duplica (o sessions.json
            // de um usuário tinha duas linhas da mesma sessão de 30/07).
            list.RemoveAll(x => x.StartTime == entry.StartTime);
            list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            // Troca de uma vez e guarda a versão anterior em .bak (ArquivoSeguro).
            ArquivoSeguro.GravarJson(FilePath, list);
        }
        catch
        {
            // Histórico é best-effort — nunca pode derrubar o fluxo de parar a captura.
        }
    }

    public static void Clear()
    {
        // O .bak junto: sem o principal, o ArquivoSeguro.Ler volta pelo .bak e o
        // histórico apagado reapareceria.
        foreach (var f in new[] { FilePath, FilePath + ".bak" })
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch { /* ignore */ }
        }
    }
}
