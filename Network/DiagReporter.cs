namespace XnomercyApp.Network;

/// <summary>
/// O app NÃO manda nada sozinho pra lugar nenhum (desde 18/10/2026).
///
/// Até a 1.0.70 ele mandava em segundo plano, depois de um consentimento único, os
/// logs de calibração de toda sessão pro canal do Discord da guild. Ninguém lia —
/// centenas de KB por sessão por tester. Saiu tudo: o envio, o consentimento e os
/// arquivos de calibração. Quando a liderança precisa ver o que o jogo está mandando,
/// o player usa "Exportar sessão p/ análise" (SessaoGravada, sem nomes nem chat) e
/// manda o arquivo do jeito que quiser.
///
/// O que sobrou aqui: apagar os arquivos de calibração que as versões antigas
/// deixaram no PC de cada player.
/// </summary>
public static class DiagReporter
{
    private static string Pasta => XnomercyApp.Network.PastaDados.Raiz;

    /// <summary>Apaga os logs de calibração das versões antigas. Chamado uma vez na abertura.</summary>
    public static void LimparArquivosAntigos()
    {
        foreach (var f in new[] { "events_diag.txt", "named_events_diag.txt", "newchar_diag.txt", "ops_diag.txt",
                                  "party_diag.txt", "diag_consent.txt", "diag_send.log" })
            foreach (var c in new[] { f, f + ".sending" })
                try { var p = System.IO.Path.Combine(Pasta, c); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
    }
}
