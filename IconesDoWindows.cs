using System.Runtime.InteropServices;

namespace XnomercyApp;

/// <summary>
/// Ícone velho depois da atualização (24/11/2026). Na 1.1.13 a cobra nova entrou no
/// ícone, mas a barra de tarefas e o atalho fixado continuaram com a cabeça de cobra
/// antiga: o Windows guarda os ícones num cache e só relê quando alguém avisa. Desafixar
/// e fixar de novo não resolvia.
///
/// Chamado no gancho de pós-atualização do Velopack (Program.cs) e, de reserva, na 1ª
/// abertura de uma versão nova (Novidades.MostrarSeHouver) — o gancho roda numa janela
/// curta e, se falhar, ninguém vê.
/// </summary>
public static class IconesDoWindows
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_FLUSHNOWAIT = 0x3000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Quantas vezes pediu (o teste confere que o gancho chama).</summary>
    public static int Pedidos { get; private set; }

    /// <summary>Pede pro Windows reler os ícones (barra de tarefas, atalhos). Nunca lança.</summary>
    public static void Atualizar()
    {
        Pedidos++;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero); }
        catch { /* sem o aviso, fica como antes: o ícone novo aparece depois de reiniciar */ }
    }
}
