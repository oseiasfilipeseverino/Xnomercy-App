using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace XnomercyApp;

/// <summary>
/// Pendências do audit do Impeccable de 23/11/2026 (nota 12/20):
///
/// 1. Menu recolhido escondia TODA a navegação — sobrava só o botão do menu, e pra
///    trocar de aba era preciso abrir o menu de novo. Agora recolhido mostra os
///    ícones das abas (nome no ToolTip e pro leitor de tela).
/// 2. As letras tinham tamanho fixo e não acompanhavam o "Tamanho do texto" do
///    Windows (Configurações → Acessibilidade). Agora o app inteiro aumenta junto.
///
/// Parte do MainWindow, dividido por assunto (classe parcial).
/// </summary>
public partial class MainWindow
{
    private Button[] BotoesDoMenu => new[] { NavFame, NavLoot, NavDamage, NavCraft, NavGathering,
                                             NavVendas, NavGrupo, NavMapas, NavBaus, NavSessions };

    /// <summary>Recolhido: só ícones (56 px: 3 da faixa + 17 + ícone 15 + 17 da margem do botão;
    /// com 48 o ícone saía cortado). Aberto: ícone e nome (210 px).</summary>
    private void AplicarMenuRecolhido(bool recolhido)
    {
        SidebarCol.Width = new GridLength(recolhido ? 56 : 210);
        SidebarTop.Visibility = Visibility.Visible;
        SidebarMarca.Visibility = recolhido ? Visibility.Collapsed : Visibility.Visible;
        SidebarLinha.Visibility = recolhido ? Visibility.Collapsed : Visibility.Visible;
        SidebarBottom.Visibility = recolhido ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in BotoesDoMenu)
        {
            if (b.Content is not StackPanel sp) continue;
            var nome = sp.Children.OfType<TextBlock>().FirstOrDefault();
            var icone = sp.Children.OfType<Path>().FirstOrDefault();
            if (nome == null) continue;
            nome.Visibility = recolhido ? Visibility.Collapsed : Visibility.Visible;
            if (icone != null) icone.Margin = new Thickness(0, 0, recolhido ? 0 : 10, 0);
            b.ToolTip = recolhido ? nome.Text : null;
            AutomationProperties.SetName(b, nome.Text);
        }
    }

    // ── Tamanho do texto do Windows ─────────────────────────────────────────

    /// <summary>
    /// "Tamanho do texto" do Windows (100 a 225%), em fator: 1,0 a 2,25. Fica em
    /// HKCU\Software\Microsoft\Accessibility\TextScaleFactor; sem a chave, 100%.
    /// </summary>
    public static double FatorDoTextoDoWindows()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (k?.GetValue("TextScaleFactor") is int pct && pct >= 100 && pct <= 225)
                return pct / 100.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>
    /// Aumenta o app inteiro pelo fator do Windows. WPF não segue essa opção sozinho
    /// (só apps UWP/WinUI). LayoutTransform, não RenderTransform: com ele o layout
    /// se refaz no tamanho novo e as barras de rolagem aparecem onde precisar.
    /// </summary>
    private void AplicarTamanhoDoTexto()
    {
        if (Content is not FrameworkElement raiz) return;
        double f = FatorDoTextoDoWindows();
        raiz.LayoutTransform = f > 1.0 ? new ScaleTransform(f, f) : Transform.Identity;
    }

    private void IniciarAcessibilidade()
    {
        AplicarTamanhoDoTexto();
        // Mudou o tamanho com o app aberto: acompanha sem reiniciar.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.Accessibility
                or Microsoft.Win32.UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(AplicarTamanhoDoTexto);
        };
    }
}
