using System.IO;

namespace XnomercyApp.Network;

/// <summary>
/// Anexa uma linha aos logs do app em %LocalAppData%\XnomercyApp (29/10/2026, A-10).
///
/// errors.log e adaptadores.log só cresciam: cada erro e cada início de captura
/// somava linhas, sem fim, num arquivo que a pessoa manda pra liderança quando
/// algo dá errado. Passando de <see cref="TetoBytes"/>, o atual vira "nome.1"
/// (substituindo o .1 anterior) e o log recomeça — fica no máximo o dobro do teto.
/// Nunca levanta: log não pode derrubar quem está logando.
/// </summary>
public static class LogArquivo
{
    public static long TetoBytes { get; set; } = 1024 * 1024;
    private static readonly object _trava = new();

    public static void Anexar(string nome, string texto)
    {
        try
        {
            lock (_trava)
            {
                var dir = PastaDados.Raiz;
                Directory.CreateDirectory(dir);
                var caminho = Path.Combine(dir, nome);
                var info = new FileInfo(caminho);
                if (info.Exists && info.Length > TetoBytes)
                    File.Move(caminho, caminho + ".1", overwrite: true);
                File.AppendAllText(caminho, texto);
            }
        }
        catch { /* sem log não há o que fazer */ }
    }

    /// <summary>Linha do errors.log no formato de sempre: [data hora] [origem] mensagem.</summary>
    public static void Erro(string origem, string msg) =>
        Anexar("errors.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{origem}] {msg}\n");
}
