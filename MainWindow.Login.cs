using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Login pelo site: WebView2, consentimento e sair da conta.
///
/// O WebView usa perfil proprio, separado do Chrome do usuario, pra
/// guardar a sessao do Discord entre execucoes sem mexer no navegador
/// pessoal.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    private async Task InitWebViewAsync()
    {
        // Perfil próprio do app (separado do Chrome do usuário) — guarda sessão/cookies
        // de login do Discord entre execuções, sem misturar com o navegador pessoal.
        var userDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XnomercyApp", "WebView2");

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // Sem isso, a falta do WebView2 Runtime (não vem em todo Windows 10) deixava
            // a tela de login travada em "Carregando..." pra sempre, sem explicar o motivo.
            TxtLoginLoadingStatus.Text =
                "Não foi possível carregar o login — instale o \"Microsoft Edge WebView2 Runtime\" " +
                "(Windows 10 mais antigos não vêm com ele) e reabra o app.\n\nDetalhe: " + ex.Message;
            return;
        }

        // Esconde a navbar do site dentro do app — assim o Craft mostra só o conteúdo
        // do mercado (sem Início/Dashboard/Gestão/etc), e o login fica limpo. Roda antes
        // de cada página renderizar, sem piscar.
        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            "(function(){var s=document.createElement('style');" +
            "s.textContent='.navbar{display:none !important;}';" +
            "(document.head||document.documentElement).appendChild(s);})();");

        // O WebView começa na página de mercado (que exige login). Se não estiver logado,
        // o site redireciona pro /login (Discord). Quando o login completa, a navegação
        // termina numa página do site que NÃO é /login — aí revelamos o menu lateral.
        WebView.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            LoginLoading.Visibility = Visibility.Collapsed;   // some assim que a 1ª página carrega

            // Reforça o esconder da navbar a cada página (backup do script de injeção
            // antecipada — caso ele não tenha pego a tempo em alguma navegação).
            try
            {
                await WebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){document.querySelectorAll('.navbar').forEach(function(n){n.style.display='none';});})();");
            }
            catch { }

            if (_loggedIn) return;
            var url = WebView.Source?.ToString() ?? "";
            if (!SiteConfig.EhDoSite(url)) return; // discord etc
            if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;   // ainda na tela de login

            // Pergunta ao site se está logado e o que pode acessar (/api/me). Confiamos no
            // campo logged_in — importante pro botão "Sair" funcionar (depois de deslogar,
            // a home não é /login, mas logged_in vem false e não deve reabrir a sidebar).
            //
            // SEM RESPOSTA não libera nada (desde 18/10/2026). Antes, erro ou demora
            // virava "logado, com tudo liberado" — herança de quando o site ainda não
            // tinha o /api/me. Bastava o site estar reiniciando (deploy) pra qualquer um
            // que chegasse numa página fora do /login ganhar acesso. Agora o app avisa e
            // tenta de novo sozinho.
            bool? loggedIn = null;
            bool canCraft = false;
            bool canTracker = false;
            try
            {
                // Pelo C#, com os cookies do WebView — ver MainWindow.Site.cs. (O script
                // assíncrono que rodava aqui nunca devolveu a resposta: o
                // ExecuteScriptAsync não espera promessa.)
                var (status, inner) = await ChamarSiteAsync(System.Net.Http.HttpMethod.Get, "/api/me");
                if (status == 200 && !string.IsNullOrEmpty(inner))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(inner);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("logged_in", out var li)) loggedIn = li.GetBoolean();
                    if (root.TryGetProperty("can_craft", out var cc)) canCraft = cc.GetBoolean();
                    if (root.TryGetProperty("can_tracker", out var ct)) canTracker = ct.GetBoolean();
                }
            }
            catch { /* resposta fora do formato: tratada como "sem resposta" logo abaixo */ }

            if (loggedIn is null)
            {
                await TentarLoginDeNovoAsync();
                return;
            }
            _falhasLogin = 0;

            if (loggedIn == false)
            {
                // De verdade deslogado (ex: acabou de clicar "Sair") — manda pra tela de
                // login em vez de reabrir a sidebar.
                WebView.CoreWebView2.Navigate(SiteUrl + "/login");
                return;
            }

            _loggedIn = true;
            OnLoggedIn(canTracker, canCraft);
        };
        WebView.CoreWebView2.Navigate(SiteUrl + "/login");
    }

    private int _falhasLogin;

    /// <summary>
    /// O site não respondeu se a pessoa está logada: avisa e recarrega a página depois
    /// de um tempo, o que roda a conferência de novo. As 3 primeiras tentativas são
    /// rápidas (site reiniciando num deploy leva segundos); depois, a cada 15s.
    /// </summary>
    private async Task TentarLoginDeNovoAsync()
    {
        _falhasLogin++;
        bool rapido = _falhasLogin <= 3;
        // O WebView é uma janela por cima de tudo (airspace): escondido, o aviso aparece.
        WebView.Visibility = Visibility.Collapsed;
        LoginLoading.Visibility = Visibility.Visible;
        TxtLoginLoadingStatus.Text = rapido
            ? $"Conferindo seu login com o site... (tentativa {_falhasLogin})"
            : "O site não respondeu. Tentando de novo a cada 15 segundos — confira sua internet.";
        await Task.Delay(rapido ? 3000 : 15000);
        if (_loggedIn) return;
        WebView.Visibility = Visibility.Visible;
        try { WebView.CoreWebView2?.Reload(); } catch { /* WebView fechando junto com o app */ }
    }

    // Login concluído: mostra o menu lateral. Os 4 botões ficam sempre visíveis —
    // quem não tem permissão pra uma aba (conta de teste limitada) ainda vê o botão,
    // mas clicar mostra um aviso grande de bloqueio em vez do conteúdo (ShowPanel).
    private void OnLoggedIn(bool canTracker, bool canCraft)
    {
        _canTracker = canTracker;
        _canCraft = canCraft;
        SidebarCol.Width = new GridLength(210);
        Sidebar.Visibility = Visibility.Visible;

        if (!ConsentStore.HasAsked)
        {
            // Esconde o WebView: ele é um HWND do Chromium e renderiza POR CIMA de
            // qualquer painel WPF (airspace), escondendo a tela de consentimento se
            // ficar visível. Os outros painéis já fazem isso via ShowPanel.
            WebView.Visibility = Visibility.Collapsed;
            PanelConsent.Visibility = Visibility.Visible;
            return;   // segue pro Loot Log depois que responder (ver BtnConsentYes/No)
        }
        // Já respondeu antes: se aceitou, manda o que foi coletado na sessão passada
        // (seguro aqui — a captura ainda não começou, é manual).
        DiagReporter.ReportDiagFiles();
        SetActiveNav(NavFame);
        ShowPanel("fame");
    }

    // Pergunta de consentimento pro diagnóstico (ver PanelConsent no XAML) — uma vez só,
    // fica salvo em disco (ConsentStore) e nunca mais pergunta de novo.
    private void BtnConsentYes_Click(object sender, RoutedEventArgs e)
    {
        ConsentStore.SetConsent(true);
        DiagReporter.ReportDiagFiles();   // manda o que já tiver de sessões anteriores
        PanelConsent.Visibility = Visibility.Collapsed;
        SetActiveNav(NavFame);
        ShowPanel("fame");
    }

    private void BtnConsentNo_Click(object sender, RoutedEventArgs e)
    {
        ConsentStore.SetConsent(false);
        PanelConsent.Visibility = Visibility.Collapsed;
        SetActiveNav(NavFame);
        ShowPanel("fame");
    }

    // Desloga: limpa a sessão do site (cookie) e volta pra tela de login, sem
    // precisar fechar e reabrir o app.
    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        _loggedIn = false;
        Sidebar.Visibility = Visibility.Collapsed;
        SidebarCol.Width = new GridLength(0);
        LoginLoading.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Visible;
        PanelLoot.Visibility = Visibility.Collapsed;
        PanelDamage.Visibility = Visibility.Collapsed;
        PanelFame.Visibility = Visibility.Collapsed;
        // ?to=login pula o pulo extra de cair na home e só depois navegar pro login —
        // fica perceptivelmente mais rápido na troca de tela.
        WebView.CoreWebView2?.Navigate(SiteUrl + "/logout?to=login");
    }
}
