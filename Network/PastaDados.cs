using System.IO;

namespace XnomercyApp.Network;

/// <summary>
/// Pasta dos dados do app: %LocalAppData%\XnomercyApp (baús, mapas, lutas, sessões,
/// caches, logs). Um lugar só desde 18/10/2026 — antes cada arquivo montava o caminho.
///
/// XNOMERCY_DADOS troca a pasta: a bateria de testes (testes\rodar.ps1) aponta pra uma
/// pasta temporária, e rodar os testes nunca mais mexe nos dados de quem desenvolve.
/// </summary>
public static class PastaDados
{
    public static string Raiz { get; } =
        System.Environment.GetEnvironmentVariable("XNOMERCY_DADOS") is string d && d.Trim().Length > 0
            ? d.Trim()
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
}
