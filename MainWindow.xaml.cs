using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace XnomercyApp;

public partial class MainWindow : Window
{
    private const string SiteUrl = "https://nome-xnomercy-site-production.up.railway.app";

    // Permite fechar de verdade pelo menu da bandeja (em vez de só minimizar).
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _ = InitWebViewAsync();
        SetActiveTab(BtnSite);
    }

    private async Task InitWebViewAsync()
    {
        // Perfil próprio do app (separado do Chrome do usuário) — guarda sessão/cookies
        // de login do Discord entre execuções, sem misturar com o navegador pessoal.
        var userDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XnomercyApp", "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await WebView.EnsureCoreWebView2Async(env);
        WebView.CoreWebView2.Navigate(SiteUrl);
    }

    // ── Navegação entre abas ──────────────────────────────────────────────
    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        SetActiveTab(btn);

        WebView.Visibility = Visibility.Collapsed;
        PanelLoot.Visibility = Visibility.Collapsed;
        PanelDamage.Visibility = Visibility.Collapsed;
        PanelFame.Visibility = Visibility.Collapsed;

        switch ((string)btn.Tag)
        {
            case "site":   WebView.Visibility = Visibility.Visible; break;
            case "loot":   PanelLoot.Visibility = Visibility.Visible; break;
            case "damage": PanelDamage.Visibility = Visibility.Visible; break;
            case "fame":   PanelFame.Visibility = Visibility.Visible; break;
        }
    }

    private void SetActiveTab(Button active)
    {
        foreach (var btn in new[] { BtnSite, BtnLoot, BtnDamage, BtnFame })
            btn.Foreground = (btn == active)
                ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#c9a227")!
                : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#9ca3af")!;
    }

    // ── Bandeja do sistema: minimizar fecha a janela, não o processo ────────
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            TrayIcon.ShowBalloonTip("XnoMercy", "Continuando em segundo plano.", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.None);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
        // Fechar o "X" só esconde — o app continua rodando em segundo plano
        // (loot log / fama-prata precisam continuar capturando mesmo com a janela fechada).
        e.Cancel = true;
        Hide();
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayMenu_Exit_Click(object sender, RoutedEventArgs e)
    {
        _exitRequested = true;
        Close();
        Application.Current.Shutdown();
    }
}
