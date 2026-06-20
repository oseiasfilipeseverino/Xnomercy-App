using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using XnomercyApp.Network;

namespace XnomercyApp;

public sealed class LootEventRow
{
    public string Time { get; init; } = "";
    public byte Code { get; init; }
    public string Summary { get; init; } = "";
}

public partial class MainWindow : Window
{
    private const string SiteUrl = "https://nome-xnomercy-site-production.up.railway.app";
    private const int MaxLootRows = 500; // evita a lista crescer sem limite numa sessão longa

    // Permite fechar de verdade pelo menu da bandeja (em vez de só minimizar).
    private bool _exitRequested;

    private readonly PacketCaptureService _capture = new();
    private readonly ObservableCollection<LootEventRow> _lootRows = new();
    private readonly ObservableCollection<DamageMeterEntry> _damageRows = new();
    private readonly FameSilverTracker _fameTracker = new();
    private readonly DamageMeterTracker _damageTracker = new();
    private bool _capturing;

    public MainWindow()
    {
        InitializeComponent();
        _ = InitWebViewAsync();
        SetActiveTab(BtnSite);

        ListLootEvents.ItemsSource = _lootRows;
        ListDamage.ItemsSource = _damageRows;

        // Mesmo stream de pacote alimenta as 3 abas — não precisa de captura separada
        // por aba, é só "quem está interessado em qual Code" (ver GameEventCodes.cs).
        _capture.EventReceived += OnPhotonEvent;
        _capture.EventReceived += _fameTracker.HandleEvent;
        _capture.EventReceived += _damageTracker.HandleEvent;
        _capture.StatusChanged += status => Dispatcher.Invoke(() => TxtCaptureStatus.Text = status);

        _fameTracker.Updated += () => Dispatcher.Invoke(RefreshFamePanel);
        _damageTracker.Updated += () => Dispatcher.Invoke(RefreshDamagePanel);
        RefreshFamePanel();

        // Diagnóstico (calibração) — mostra a cada segundo quantos pacotes brutos
        // chegaram, pra sabermos se o problema é filtro/porta (fica 0) ou decodificação
        // (sobe mas não vira evento).
        var diagTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        diagTimer.Tick += (_, _) =>
        {
            if (!_capturing) return;
            TxtCaptureStatus.Text =
                $"Pacotes brutos: {_capture.DiagRawPackets} | Payloads extraídos: {_capture.DiagAppPayloadsExtracted} | Eventos decodificados: {_capture.DiagEventsDecoded}";
            TxtHexSample.Text = string.Join("\n", _capture.DiagSampleHex);
        };
        diagTimer.Start();
    }

    // ── Fama & Prata (Fase 3) ───────────────────────────────────────────────
    private void RefreshFamePanel()
    {
        TxtFameTotal.Text = _fameTracker.TotalFame.ToString("N0");
        TxtSilverTotal.Text = _fameTracker.TotalSilver.ToString("N0");
        TxtFameSessionStart.Text = $"Sessão desde {_fameTracker.SessionStart:HH:mm:ss}";
    }

    private void BtnFameReset_Click(object sender, RoutedEventArgs e) => _fameTracker.Reset();

    // ── Medidor de Dano (Fase 4) ────────────────────────────────────────────
    private void RefreshDamagePanel()
    {
        _damageRows.Clear();
        foreach (var entry in _damageTracker.Entries.OrderByDescending(x => x.Damage))
            _damageRows.Add(entry);
    }

    private void BtnDamageReset_Click(object sender, RoutedEventArgs e) => _damageTracker.Reset();

    // ── Loot Log (modo calibração — Fase 2) ─────────────────────────────────
    private void BtnCaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_capturing)
        {
            _capturing = _capture.Start();
            BtnCaptureToggle.Content = _capturing ? "Parar captura" : "Iniciar captura";
        }
        else
        {
            _capture.Stop();
            _capturing = false;
            BtnCaptureToggle.Content = "Iniciar captura";
            TxtCaptureStatus.Text = "Parado";
        }
    }

    private void OnPhotonEvent(PhotonEvent evt)
    {
        var summary = string.Join("  ", evt.Parameters.Select(kv => $"[{kv.Key}]={Describe(kv.Value)}"));
        Dispatcher.Invoke(() =>
        {
            _lootRows.Insert(0, new LootEventRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Code = evt.Code,
                Summary = summary,
            });
            while (_lootRows.Count > MaxLootRows)
                _lootRows.RemoveAt(_lootRows.Count - 1);
        });
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        byte[] bytes => $"byte[{bytes.Length}]",
        object?[] arr => $"array[{arr.Length}]",
        System.Collections.IDictionary => "dict{...}",
        Protocol16Deserializer.UnknownValue u => $"?type{u.TypeCode}",
        _ => value.ToString() ?? "",
    };

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

    // ── Bandeja do sistema: só o "X" esconde a janela, minimizar é normal ──
    // (antes minimizar também escondia pra bandeja, o que confundia — agora
    // minimizar só manda pra barra de tarefas, do jeito que o Windows já faz).
    private void Window_StateChanged(object? sender, EventArgs e)
    {
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
        _capture.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
