using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Menu lateral, troca de painel e bandeja do sistema.
///
/// O "X" da janela esconde em vez de fechar (ver Window_Closing) — quem
/// fecha de verdade e o menu da bandeja, que ainda espera o envio do
/// diagnostico terminar.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // ── Menu lateral ──────────────────────────────────────────────────────
    private bool _sidebarCollapsed;
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarCol.Width = new GridLength(_sidebarCollapsed ? 48 : 210);
        SidebarTop.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBottom.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        SetActiveNav(btn);
        ShowPanel((string)btn.Tag);
    }

    private void ShowPanel(string tag)
    {
        WebView.Visibility = Visibility.Collapsed;
        PanelLoot.Visibility = Visibility.Collapsed;
        PanelDamage.Visibility = Visibility.Collapsed;
        PanelFame.Visibility = Visibility.Collapsed;
        PanelSessions.Visibility = Visibility.Collapsed;
        PanelGathering.Visibility = Visibility.Collapsed;
        PanelVendas.Visibility = Visibility.Collapsed;
        PanelMapas.Visibility = Visibility.Collapsed;
        PanelGrupo.Visibility = Visibility.Collapsed;
        PanelBaus.Visibility = Visibility.Collapsed;
        PanelBlocked.Visibility = Visibility.Collapsed;
        _advancedVisible = false;

        bool isTrackerTab = tag is "loot" or "damage" or "fame" or "sessions" or "gathering" or "vendas" or "mapas" or "grupo" or "baus";
        if ((isTrackerTab && !_canTracker) || (tag == "craft" && !_canCraft))
        {
            PanelBlocked.Visibility = Visibility.Visible;
            return;
        }

        switch (tag)
        {
            case "loot":     PanelLoot.Visibility = Visibility.Visible; break;
            case "damage":   PanelDamage.Visibility = Visibility.Visible; RefreshDamagePanel(); break;
            case "fame":     PanelFame.Visibility = Visibility.Visible; RedrawSessionChart(); break;
            case "sessions": PanelSessions.Visibility = Visibility.Visible; break;
            case "gathering": PanelGathering.Visibility = Visibility.Visible; RefreshColeta(); break;
            case "vendas":   PanelVendas.Visibility = Visibility.Visible; RefreshVendas(); break;
            case "mapas":    PanelMapas.Visibility = Visibility.Visible; RefreshMapas(); break;
            case "baus":     PanelBaus.Visibility = Visibility.Visible; RefreshBaus(); break;
            case "grupo":    PanelGrupo.Visibility = Visibility.Visible; _ = ItemPowerCatalog.EnsureLoadedAsync(); RefreshGrupo(); break;
            case "craft":
                // Craft = página de mercado do site embutida (reaproveita a calculadora pronta).
                if (WebView.CoreWebView2 != null && !(WebView.Source?.ToString().Contains("/mercado") ?? false))
                    WebView.CoreWebView2.Navigate(SiteUrl + "/mercado");
                WebView.Visibility = Visibility.Visible;
                break;
        }
        _advancedVisible = tag == "loot" && BtnAdvancedMode.IsChecked == true;
    }

    private void SetActiveNav(Button active)
    {
        foreach (var btn in new[] { NavLoot, NavDamage, NavFame, NavCraft, NavGathering, NavVendas, NavMapas, NavGrupo, NavBaus, NavSessions })
        {
            bool isActive = btn == active;
            btn.Foreground = isActive ? NavActiveBrush : NavIdleBrush;
            btn.Background  = isActive ? NavActiveBg    : System.Windows.Media.Brushes.Transparent;
        }
    }

    // ── Bandeja do sistema: só o "X" esconde a janela, minimizar é normal ──
    // (minimizar manda pra barra de tarefas, do jeito que o Windows já faz — por
    // isso não há handler de StateChanged; ele existia vazio desde que minimizar
    // deixou de esconder pra bandeja.)
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
        // Fechar o "X" só esconde — o app continua rodando em segundo plano
        // (loot log / fama-prata precisam continuar capturando mesmo com a janela fechada).
        e.Cancel = true;
        Hide();
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => MostrarJanela();

    /// <summary>Traz a janela de volta (da bandeja ou de trás de outras) — também quando o app é aberto de novo.</summary>
    public void MostrarJanela()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // vem pra frente mesmo com o jogo em tela cheia por trás
    }

    private async void TrayMenu_Exit_Click(object sender, RoutedEventArgs e)
    {
        // Sair pela bandeja matava o processo direto, sem esperar o diagnóstico
        // (fire-and-forget) terminar de enviar — perdia os dados de calibração de quem
        // fechava o app em vez de clicar "Parar captura". Espera até 5s pelo envio.
        await Task.CompletedTask;   // nada mais é enviado ao sair (ver DiagReporter)
        _exitRequested = true;
        GuardarSessaoAoSair();
        DesassinarEventosEstaticos();
        _capture.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
