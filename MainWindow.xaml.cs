using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

// Converte a % de dano (0-100) na largura em pixels da barrinha do Medidor de Dano.
// 140 = largura total da barra definida no XAML (GridViewColumn "% Dano").
public sealed class PctToWidthConverter : System.Windows.Data.IValueConverter
{
    private const double MaxWidth = 140;
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        return Math.Clamp(pct, 0, 100) / 100.0 * MaxWidth;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LootEventRow : INotifyPropertyChanged
{
    public string Time { get; init; } = "";
    public int Code { get; init; }
    public string Summary { get; init; } = "";
    public DateTime Timestamp { get; init; }

    private bool _isNearMark;
    public bool IsNearMark
    {
        get => _isNearMark;
        set { _isNearMark = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNearMark))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LootFeedRow
{
    public string Time { get; init; } = "";
    public string Looter { get; init; } = "";
    public string Item { get; init; } = "";    // "1x Elder's Stone Hammer" ou "1.550.115 prata"
    public string From { get; init; } = "";
    public string? ItemIcon { get; init; }     // URL do render do item (miniatura)
    public bool IsSilver { get; init; }
    public bool IsMob { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class DamageRowDisplay
{
    public long ObjectId { get; init; }   // usado pra buscar a quebra por habilidade ao selecionar a linha
    public string Player { get; init; } = "";
    public long Damage { get; init; }
    public string DamagePct { get; init; } = "";
    public double DamagePctValue { get; init; }   // 0-100, pra desenhar a barra (DamagePct é só o texto formatado)
    public long Healing { get; init; }
    public string? WeaponIcon { get; init; }   // URL do render oficial do item
}

public sealed class DamageBySpellRowDisplay
{
    public string Name { get; init; } = "";
    public long Damage { get; init; }
    public string DamagePct { get; init; } = "";
    public double DamagePctValue { get; init; }
}

public sealed class MobKillRowDisplay
{
    public string Name { get; init; } = "";
    public int Kills { get; init; }
    public string KillsPerHour { get; init; } = "";
}

public partial class MainWindow : Window
{
    private const string SiteUrl = "https://nome-xnomercy-site-production.up.railway.app";
    private const int MaxLootRows = 500; // evita a lista crescer sem limite numa sessão longa

    // Versão real agora vem do pacote Velopack (definida no `vpk pack --packVersion`),
    // não precisa mais bumpar nada aqui à mão.

    // Permite fechar de verdade pelo menu da bandeja (em vez de só minimizar).
    private bool _exitRequested;

    private readonly PacketCaptureService _capture = new();
    private readonly ObservableCollection<LootEventRow> _lootRows = new();
    private readonly ObservableCollection<LootEventRow> _markedRows = new();
    private readonly ObservableCollection<LootFeedRow> _lootFeed = new();
    private System.ComponentModel.ICollectionView? _lootFeedView;
    private readonly ObservableCollection<DamageRowDisplay> _damageRows = new();
    private readonly ObservableCollection<DamageBySpellRowDisplay> _damageBySkillRows = new();
    private readonly FameSilverTracker _fameTracker = new();
    private readonly OpenWorldKillTracker _killTracker = new();
    private readonly ObservableCollection<MobKillRowDisplay> _mobKillRows = new();
    private readonly DamageMeterTracker _damageTracker = new();
    private bool _capturing;
    private bool _paused;
    private volatile bool _fameDirty;
    private volatile bool _damageDirty;
    private volatile bool _advancedVisible;   // só processa a lista crua quando o modo avançado está à vista
    private bool _loggedIn;
    private bool _canTracker = true;   // Loot Log + Medidor de Dano + Fama & Prata (vêm juntos)
    private bool _canCraft = true;

    // Janela de tempo (após o SEU cliente mandar a operação de pegar item) em que o
    // Loot Log aceita um evento GrabbedLoot (279) mesmo sem o campo "de quem" — ver
    // SelfLootDetector.cs. 3s cobre a latência normal de rede sem deixar a janela
    // aberta tempo suficiente pra confundir com o próximo pickup de outra pessoa.
    // Lock: escrito pela thread de captura (OnSelfLootDetected) e lido pela mesma
    // thread em OnPhotonEvent — mas os dois vêm de eventos diferentes da captura, sem
    // garantia de ordem/mesma thread entre versões futuras do SharpPcap, então protege
    // igual ao padrão já usado pros campos escalares de PlayerRegistry.
    private readonly object _selfLootLock = new();
    private DateTime _selfLootWindowUntil = DateTime.MinValue;
    private static readonly TimeSpan SelfLootWindow = TimeSpan.FromSeconds(3);

    // Baú/vault (ao contrário de corpo de mob) provavelmente NUNCA gera o evento social
    // GrabbedLoot (279) pro próprio looter — esse evento parece existir só pro feed de
    // "quem pegou o quê de quem" em loot de corpo. Sem isso, abrir só a janela de
    // tolerância acima não bastava: não havia NENHUM evento 279 esperando por ela, então
    // a linha nunca aparecia (bug relatado: "fiz um baú inteiro e não apareceu nada").
    // Por isso, quando o SelfLootDetector já resolve item+quantidade (via cache do
    // NewSimpleItem cruzado com o "pegar tudo"), inserimos a linha direto — não dependemos
    // mais só do 279 chegar. Guarda os últimos itens inseridos assim pra não duplicar a
    // linha nos casos (loot de mob) em que o 279 TAMBÉM chega pro mesmo pickup.
    private readonly List<(int ItemIndex, long Qty, DateTime At)> _recentSelfLootDirect = new();
    private static readonly TimeSpan SelfLootDedupWindow = TimeSpan.FromSeconds(2);

    private void OnSelfLootDetected(int? itemIndex, long? quantity)
    {
        lock (_selfLootLock) _selfLootWindowUntil = DateTime.Now + SelfLootWindow;
        if (itemIndex is not int idx || quantity is not long qty) return;

        Dispatcher.BeginInvoke(() =>
        {
            var now = DateTime.Now;
            lock (_recentSelfLootDirect)
            {
                _recentSelfLootDirect.RemoveAll(x => now - x.At > SelfLootDedupWindow);
                _recentSelfLootDirect.Add((idx, qty, now));
            }
            var name = ItemCatalog.GetName(idx) ?? $"item {idx}";
            var row = new LootFeedRow
            {
                Time = now.ToString("HH:mm:ss"),
                Timestamp = now,
                Looter = "Você",
                Item = $"{qty}x {name}",
                From = "",
                ItemIcon = IconUrl(idx),
                IsSilver = false,
                IsMob = false,
            };
            _lootFeed.Insert(0, row);
            while (_lootFeed.Count > MaxLootRows) _lootFeed.RemoveAt(_lootFeed.Count - 1);
        });
    }

    // Evita duplicar a linha quando o 279 oficial TAMBÉM chega pro mesmo pickup que já
    // inserimos direto acima (comum em loot de mob/corpo, onde o servidor confirma).
    private bool WasRecentSelfLootDirect(int itemIndex, long qty)
    {
        var now = DateTime.Now;
        lock (_recentSelfLootDirect)
        {
            var match = _recentSelfLootDirect.FindIndex(x =>
                x.ItemIndex == itemIndex && x.Qty == qty && now - x.At <= SelfLootDedupWindow);
            if (match < 0) return false;
            _recentSelfLootDirect.RemoveAt(match);
            return true;
        }
    }

    // ── Timer de fechamento de dungeon ──────────────────────────────────────
    private DateTime _dungeonClosesAt;
    private readonly System.Windows.Threading.DispatcherTimer _dungeonTimer =
        new() { Interval = TimeSpan.FromSeconds(1) };

    private void StartDungeonTimer()
    {
        _dungeonClosesAt = DateTime.UtcNow + DungeonTimerTracker.CloseDuration;
        PanelDungeonTimer.Visibility = Visibility.Visible;
        TickDungeonTimer();
        if (!_dungeonTimer.IsEnabled)
        {
            _dungeonTimer.Tick -= OnDungeonTimerTick;
            _dungeonTimer.Tick += OnDungeonTimerTick;
            _dungeonTimer.Start();
        }
    }

    private void StopDungeonTimer()
    {
        _dungeonTimer.Stop();
        PanelDungeonTimer.Visibility = Visibility.Collapsed;
    }

    private void OnDungeonTimerTick(object? sender, EventArgs e) => TickDungeonTimer();

    private void TickDungeonTimer()
    {
        var remaining = _dungeonClosesAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            TxtDungeonTimer.Text = "FECHADA";
            _dungeonTimer.Stop();
            return;
        }
        TxtDungeonTimer.Text = remaining.ToString(@"mm\:ss");
    }
    private static System.Windows.Media.Brush B(string hex) =>
        new System.Windows.Media.BrushConverter().ConvertFromString(hex) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    private static readonly System.Windows.Media.Brush NavActiveBg = B("#191306");
    private static readonly System.Windows.Media.Brush NavActiveBrush = B("#dc2626");  // vermelho do site
    private static readonly System.Windows.Media.Brush NavIdleBrush   = B("#888888");

    // Pinta a barra de título nativa (a faixa branca do Windows lá em cima) escura,
    // pra combinar com o resto do app — sem isso ela vinha sempre clara/padrão do
    // tema do Windows, independente do app ser todo dark. Funciona no Windows 10
    // 1809+/11; em versões mais antigas a chamada simplesmente falha e é ignorada.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DwmwaUseImmersiveDarkMode = 20;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            }
            catch { /* Windows antigo sem suporte — fica com a barra padrão, sem travar nada */ }
        };
        _ = InitWebViewAsync();
        _ = ItemCatalog.EnsureLoadedAsync(); // carrega em segundo plano, mesma base de nomes do site
        _ = SpellCatalog.EnsureLoadedAsync(); // idem, pra resolver nome de habilidade na quebra por skill
        _ = CheckForUpdateAsync();

        ListLootEvents.ItemsSource = _lootRows;
        ListMarkedEvents.ItemsSource = _markedRows;
        _lootFeedView = System.Windows.Data.CollectionViewSource.GetDefaultView(_lootFeed);
        _lootFeedView.Filter = LootRowVisible;
        ListCleanLoot.ItemsSource = _lootFeedView;
        ListDamage.ItemsSource = _damageRows;
        ListDamageBySkill.ItemsSource = _damageBySkillRows;
        ListMobKills.ItemsSource = _mobKillRows;

        // Mesmo stream de pacote alimenta as 3 abas — não precisa de captura separada
        // por aba, é só "quem está interessado em qual Code" (ver GameEventCodes.cs).
        // Passa tudo por um dispatcher único pra poder pausar a contagem (botão Pausar)
        // sem precisar desligar a captura de pacote em si.
        _capture.EventReceived += OnCaptureEvent;
        // Operação de Join → descobre "Você" cedo (resolve seu próprio dano sem nome).
        _capture.OpResponseReceived += PlayerRegistry.HandleOpResponse;
        // Operação de movimento/ação própria (op real 24) → descobre "Você" ainda mais
        // cedo, sem depender de já ter entrado na zona com a captura ligada ou já ter
        // ganhado fama/prata (combate puro sem loot/fama não disparava nenhuma das
        // duas fontes antigas, deixando o próprio dano escondido como "#id").
        _capture.OpRequestReceived += PlayerRegistry.HandleOpRequest;
        // Loot do PRÓPRIO jogador: detecta pela operação que o seu cliente manda ao
        // pegar um item (request, não broadcast) — não depende do servidor confirmar
        // de volta corretamente (ver SelfLootDetector.cs pro porquê disso ser preciso).
        _capture.OpRequestReceived += SelfLootDetector.HandleOpRequest;
        SelfLootDetector.SelfLootDetected += OnSelfLootDetected;
        // Timer de fechamento de dungeon — ver DungeonTimerTracker.cs.
        _capture.OpResponseReceived += DungeonTimerTracker.HandleOpResponse;
        DungeonTimerTracker.EnteredDungeon += () => Dispatcher.BeginInvoke(StartDungeonTimer);
        DungeonTimerTracker.LeftDungeon += () => Dispatcher.BeginInvoke(StopDungeonTimer);
        _capture.StatusChanged += status => Dispatcher.BeginInvoke(() => TxtCaptureStatus.Text = status);

        // Em vez de atualizar a UI a cada evento (em combate são centenas/seg, o que
        // travava a thread de captura e fazia perder pacotes), os trackers só marcam
        // "sujo" e um timer redesenha no máximo ~3x/seg, na thread da UI.
        _fameTracker.Updated += () => _fameDirty = true;
        _killTracker.Updated += () => _fameDirty = true;
        _damageTracker.Updated += () => _damageDirty = true;
        RefreshFamePanel();

        var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        uiTimer.Tick += (_, _) =>
        {
            if (_fameDirty) { _fameDirty = false; RefreshFamePanel(); }
            if (_damageDirty) { _damageDirty = false; RefreshDamagePanel(); }
        };
        uiTimer.Start();

        // Diagnóstico (calibração) — mostra a cada segundo quantos pacotes brutos
        // chegaram, pra sabermos se o problema é filtro/porta (fica 0) ou decodificação
        // (sobe mas não vira evento).
        var diagTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        diagTimer.Tick += (_, _) =>
        {
            if (!_capturing) return;
            TxtCaptureStatus.Text =
                $"Pacotes brutos: {_capture.DiagRawPackets} | Payloads extraídos: {_capture.DiagAppPayloadsExtracted} | Eventos decodificados: {_capture.DiagEventsDecoded}";
        };
        diagTimer.Start();
    }

    // ── Fama & Prata (Fase 3) ───────────────────────────────────────────────
    private void RefreshFamePanel()
    {
        TxtFameTotal.Text = _fameTracker.TotalFame.ToString("N0");
        TxtYellowFameTotal.Text = _fameTracker.TotalYellowFame.ToString("N0");
        TxtSilverTotal.Text = _fameTracker.TotalSilver.ToString("N0");
        TxtFameSessionStart.Text = $"Sessão desde {_fameTracker.SessionStart:HH:mm:ss}";

        TxtMobKillsTotal.Text = _killTracker.TotalKills.ToString("N0");
        TxtMobKillsPerHour.Text = $"{_killTracker.KillsPerHour:0.0}/hora";
        _mobKillRows.Clear();
        foreach (var (name, kills, perHour) in _killTracker.Snapshot())
            _mobKillRows.Add(new MobKillRowDisplay { Name = name, Kills = kills, KillsPerHour = perHour.ToString("0.0") });
    }

    private void BtnFameReset_Click(object sender, RoutedEventArgs e)
    {
        _fameTracker.Reset();
        _killTracker.Reset();
    }

    // ── Medidor de Dano (Fase 4) ────────────────────────────────────────────
    private void RefreshDamagePanel()
    {
        _damageRows.Clear();
        bool showUnnamed = ChkShowUnnamed.IsChecked == true;
        bool guildOnly = ChkGuildOnlyDamage.IsChecked == true;
        bool partyOnly = ChkPartyOnlyDamage.IsChecked == true;
        var entries = _damageTracker.Snapshot()
            // "Mostrar sem nome": por padrão (desmarcado) já filtra os #número (mob não
            // detectado, invocação, etc.), deixando só jogadores resolvidos. Marcando,
            // revela os #número de volta — é o oposto do que era antes ("ocultar").
            .Where(x => showUnnamed || !PlayerRegistry.NameOf(x.ObjectId).StartsWith('#'))
            // "Só minha guild": opcional, pra quando quiser ver só o desempenho da sua
            // guild — o padrão continua mostrando todo mundo por perto. A guild do
            // próprio usuário é resolvida em background (PlayerRegistry.OwnGuild); se
            // ainda não resolveu, só você mesmo passa (evita falso positivo comparando
            // "" com guild vazia de quem não tem guild).
            .Where(x => !guildOnly || x.ObjectId == PlayerRegistry.SelfObjectId
                        || (PlayerRegistry.OwnGuild.Length > 0
                            && PlayerRegistry.GuildOf(x.ObjectId) == PlayerRegistry.OwnGuild))
            // "Só meu grupo": rastreado via eventos de grupo calibrados (entrada por
            // 229/240, saída por 182, com expiração de 60s como rede de segurança).
            .Where(x => !partyOnly || PlayerRegistry.IsInPartyById(x.ObjectId))
            .OrderByDescending(x => x.Damage)
            .ToList();
        // Com o filtro ligado, a % passa a ser entre os jogadores mostrados (faz mais
        // sentido pra comparar a galera do grupo do que diluir no dano do que foi escondido).
        long total = entries.Sum(x => x.Damage);
        foreach (var e in entries)
        {
            var info = PlayerRegistry.Get(e.ObjectId);
            string name = PlayerRegistry.NameOf(e.ObjectId);   // "Você", nome real, ou #id
            string? icon = null;
            if (e.ObjectId == PlayerRegistry.SelfObjectId)
            {
                // Você nunca tem PlayerInfo (o jogo não manda NewCharacter de você
                // mesmo) — a arma vem de uma consulta separada à API pública do Albion.
                icon = PlayerRegistry.OwnWeaponIconUrl;
            }
            else if (info != null && info.MainHand >= 0)
            {
                var uniq = ItemCatalog.GetUniqueName(info.MainHand);
                if (!string.IsNullOrEmpty(uniq))
                    icon = $"https://render.albiononline.com/v1/item/{uniq}.png?size=64";
            }
            double pct = total > 0 ? e.Damage * 100.0 / total : 0;
            _damageRows.Add(new DamageRowDisplay
            {
                ObjectId = e.ObjectId,
                Player = name,
                Damage = e.Damage,
                DamagePct = pct.ToString("0.0") + "%",
                DamagePctValue = pct,
                Healing = e.Healing,
                WeaponIcon = icon,
            });
        }

        // Mantém a quebra por habilidade em sincronia com quem está selecionado — sem
        // isso ela ficaria parada na última foto enquanto o resto da tela continua
        // atualizando ao vivo durante o combate.
        if (ListDamage.SelectedItem is DamageRowDisplay selected)
            RefreshDamageBySkillPanel(selected.ObjectId);
    }

    private void BtnDamageReset_Click(object sender, RoutedEventArgs e) => _damageTracker.Reset();

    // Quebra de dano por habilidade (estilo Albion Battle Analytics) do jogador
    // selecionado na lista principal — ver DamageMeterTracker.SnapshotBySpell e
    // SpellCatalog (resolve CausingSpellIndex pro nome real da skill).
    private void ListDamage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListDamage.SelectedItem is DamageRowDisplay row)
        {
            TxtDamageBySkillTitle.Text = $"Dano por habilidade — {row.Player}";
            RefreshDamageBySkillPanel(row.ObjectId);
            PanelDamageBySkill.Visibility = Visibility.Visible;
        }
        else
        {
            _damageBySkillRows.Clear();
            PanelDamageBySkill.Visibility = Visibility.Collapsed;
        }
    }

    // Clicar de novo na linha já selecionada fecha o painel (em vez de continuar
    // selecionada sem disparar SelectionChanged, que é o comportamento padrão do
    // ListView e deixaria o painel aberto pra sempre depois do 1º clique).
    private void ListDamageItem_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true })
        {
            ListDamage.SelectedItem = null;
            e.Handled = true;
        }
    }

    private void RefreshDamageBySkillPanel(long objectId)
    {
        var bySpell = _damageTracker.SnapshotBySpell(objectId);
        long total = bySpell.Sum(x => x.Damage);
        _damageBySkillRows.Clear();
        foreach (var s in bySpell)
        {
            double pct = total > 0 ? s.Damage * 100.0 / total : 0;
            _damageBySkillRows.Add(new DamageBySpellRowDisplay
            {
                Name = s.Name,
                Damage = s.Damage,
                DamagePct = pct.ToString("0.0") + "%",
                DamagePctValue = pct,
            });
        }
    }

    // Liga/desliga "ocultar sem nome" — só redesenha (o filtro é aplicado no refresh).
    private void DamageFilter_Changed(object sender, RoutedEventArgs e) => RefreshDamagePanel();

    // Copia o ranking de dano pro clipboard, em texto — pra colar no Discord da guild.
    private void BtnDamageCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_damageRows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Medidor de Dano — XnoMercy");
        int pos = 1;
        foreach (var r in _damageRows)
            sb.AppendLine($"{pos++}. {r.Player} — {r.Damage:N0} ({r.DamagePct})" +
                          (r.Healing > 0 ? $" | cura {r.Healing:N0}" : ""));
        try { Clipboard.SetText(sb.ToString()); BtnDamageCopy.Content = "Copiado!"; }
        catch { /* clipboard ocupado por outro app — ignora */ }
    }

    // ── Loot Log (Fase 2) ────────────────────────────────────────────────────
    private void BtnAdvancedMode_Click(object sender, RoutedEventArgs e)
    {
        bool advanced = BtnAdvancedMode.IsChecked == true;
        PanelAdvanced.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        ListCleanLoot.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        _advancedVisible = advanced && PanelLoot.Visibility == Visibility.Visible;
    }

    // Recebe todo evento decodificado do pacote e repassa pros trackers — exceto
    // enquanto pausado, onde o pacote continua sendo capturado (Npcap não é desligado)
    // mas nada é contado: loot/dano/fama ficam exatamente como estavam até despausar.
    private void OnCaptureEvent(PhotonEvent evt)
    {
        if (_paused) return;
        OnPhotonEvent(evt);
        PlayerRegistry.HandleEvent(evt);
        _fameTracker.HandleEvent(evt);
        _damageTracker.HandleEvent(evt);
        _killTracker.HandleEvent(evt);
    }

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
            _paused = false;
            BtnCaptureToggle.Content = "Iniciar captura";
            BtnPauseToggle.Content = "Pausar";
            TxtCaptureStatus.Text = "Parado";
            // Com a captura parada, os arquivos de diagnóstico não estão mais sendo
            // escritos — momento seguro pra enviar pro Discord (com consentimento).
            // Assim o tester não precisa fechar e reabrir o app pra mandar os dados.
            DiagReporter.ReportDiagFiles();
        }
        UpdateCaptureIndicator();
    }

    // Pausa só a contagem (loot/dano/fama) — a captura de pacote continua rodando,
    // então despausar não perde nada que tenha chegado nesse meio tempo, só ignora.
    private void BtnPauseToggle_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        BtnPauseToggle.Content = _paused ? "Despausar" : "Pausar";
        UpdateCaptureIndicator();
    }

    // Indicador global de captura na sidebar (visível de qualquer aba)
    private void UpdateCaptureIndicator()
    {
        if (_paused)
        {
            CaptureDot.Fill = B("#facc15");
            CaptureStateLabel.Text = "Pausado";
            CaptureStateLabel.Foreground = B("#facc15");
        }
        else
        {
            CaptureDot.Fill = _capturing ? B("#22c55e") : B("#666666");
            CaptureStateLabel.Text = _capturing ? "Capturando" : "Captura parada";
            CaptureStateLabel.Foreground = _capturing ? B("#22c55e") : B("#888888");
        }
    }

    // Marcação manual de momento: usuário clica bem na hora que pega um item.
    // Marca em verde qualquer linha (já na lista ou que ainda vai chegar) dentro
    // de ±1.5s do clique — assim dá pra achar o evento certo sem precisar de
    // filtro por código (que exigia adivinhar quais códigos são "ruído").
    private static readonly TimeSpan MarkWindow = TimeSpan.FromSeconds(4);
    private readonly List<DateTime> _markers = new();

    // Confirmado pela própria estrutura repetitiva (mesmo formato em todo pacote,
    // disparando a cada poucos ms) que os códigos 1 e 3 são sincronização de
    // movimento/posição — irrelevantes pra achar o evento de loot. Excluídos só da
    // caixa de marcados (a lista principal continua mostrando tudo, sem filtro).
    private static readonly HashSet<int> MovementNoiseCodes = new() { 1, 3 };

    private void BtnMarkMoment_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _markers.Add(now);
        foreach (var row in _lootRows)
        {
            if (Math.Abs((row.Timestamp - now).TotalSeconds) <= MarkWindow.TotalSeconds && !row.IsNearMark)
            {
                row.IsNearMark = true;
                if (!MovementNoiseCodes.Contains(row.Code))
                    _markedRows.Insert(0, row);
            }
        }
    }

    private void BtnClearMarked_Click(object sender, RoutedEventArgs e)
    {
        _markedRows.Clear();
        _markers.Clear();
    }

    private void OnPhotonEvent(PhotonEvent evt)
    {
        DiagLogBigEvent(evt);   // calibração: loga eventos com números grandes (fama/prata)
        DiagLogNamedEvent(evt); // calibração: loga eventos com texto (achar o código real de NewMob)

        var now = DateTime.Now;
        bool isGrabbedLoot = evt.EventCode == GameEventCodes.GrabbedLoot;
        bool isLootCandidate = evt.EventCode == GameEventCodes.LootPickup || evt.EventCode == GameEventCodes.LootPickupEquipment;

        // Alimenta o cache de itens recém-vistos (NewSimpleItem/32) SEMPRE, não só com
        // o modo avançado visível — é o que permite ao SelfLootDetector resolver nome/
        // quantidade quando o "pegar tudo" (InventoryMoveGivenItems) chega com os
        // ObjectIds dos itens.
        if (evt.EventCode == GameEventCodes.LootPickup
            && evt.Parameters.TryGetValue(0, out var objIdObj) && evt.Parameters.TryGetValue(1, out var idxObj))
        {
            long objId = ToLong(objIdObj);
            int idx = (int)ToLong(idxObj);
            long qty = evt.Parameters.TryGetValue(2, out var qObj) ? ToLong(qObj) : 1;
            if (objId > 0) SelfLootDetector.RegisterDiscoveredItem(objId, idx, qty);
        }

        // Feed normal = SÓ loot com origem (de quem). O evento 279 (saque de corpo/mob)
        // sempre traz a origem; os pickups do seu inventário (login, troca de zona, baú
        // via code 32) NÃO têm origem, então não poluem o normal — ficam só no avançado.
        // Regra do usuário: "se não preencher 'de quem', não aparece" — EXCETO quando
        // sabemos, pela própria operação que SEU cliente mandou (SelfLootDetector), que
        // foi você quem pegou: nesse caso mostramos mesmo sem "de quem", porque é
        // exatamente o cenário da regressão (servidor não confirma "de quem" de volta
        // pro próprio looter, e o item pego de verdade desaparecia do Loot Log).
        LootFeedRow? feedRow = null;
        bool withinSelfLootWindow;
        lock (_selfLootLock) withinSelfLootWindow = now <= _selfLootWindowUntil;
        if (isGrabbedLoot && TryParseGrabbedLoot(evt, now, out var fr, out var grabbedItemIdx, out var grabbedQty)
            && !fr.IsSilver && (fr.From.Length > 0 || withinSelfLootWindow))
        {
            // Já mostramos esse mesmo pickup direto via SelfLootDetector (ver
            // OnSelfLootDetected) — não duplica quando o servidor confirma depois.
            if (!WasRecentSelfLootDirect(grabbedItemIdx, grabbedQty))
                feedRow = fr;
        }

        // PERFORMANCE: quando o modo avançado não está à vista, não montamos a lista crua
        // (são milhares de eventos/seg — movimento, sync). Só o feed limpo importa. E usamos
        // BeginInvoke (assíncrono) pra NUNCA bloquear a thread de captura — bloquear fazia o
        // buffer do Npcap encher e DESCARTAR pacotes (perdendo fama/loot).
        if (feedRow == null && !_advancedVisible) return;

        string? summary = null;
        bool nearMark = false;
        if (_advancedVisible)
        {
            nearMark = _markers.Any(m => Math.Abs((now - m).TotalSeconds) <= MarkWindow.TotalSeconds);
            if (isLootCandidate && TryDescribeLoot(evt, out var lootSummary)) summary = lootSummary;
            else
            {
                summary = string.Join("  ", evt.Parameters.Select(kv => $"[{kv.Key}]={Describe(kv.Value, isLootCandidate)}"));
                if (isLootCandidate) summary = "🎯 POSSÍVEL LOOT — " + summary;
            }
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (feedRow != null)
            {
                _lootFeed.Insert(0, feedRow);
                while (_lootFeed.Count > MaxLootRows) _lootFeed.RemoveAt(_lootFeed.Count - 1);
            }
            if (summary != null)   // só no modo avançado
            {
                var row = new LootEventRow
                {
                    Time = now.ToString("HH:mm:ss.fff"),
                    Timestamp = now,
                    Code = evt.EventCode,
                    Summary = summary,
                    IsNearMark = nearMark,
                };
                _lootRows.Insert(0, row);
                if (isLootCandidate || isGrabbedLoot || (nearMark && !MovementNoiseCodes.Contains(evt.EventCode)))
                    _markedRows.Insert(0, row);
                while (_lootRows.Count > MaxLootRows) _lootRows.RemoveAt(_lootRows.Count - 1);
                while (_markedRows.Count > MaxLootRows) _markedRows.RemoveAt(_markedRows.Count - 1);
            }
        });
    }

    // Diagnóstico de calibração: grava num arquivo os eventos que carregam números
    // grandes (candidatos a fama/prata — a fama total tem 13 dígitos). Assim eu acho
    // o código real de UpdateFame/UpdateMoney lendo dados reais, sem depender de print
    // no momento exato. Arquivo: %LocalAppData%\XnomercyApp\events_diag.txt
    private static int _diagCount;
    private static readonly object _diagLock = new();
    // [Conditional("DEBUG")]: no build Release (produção, o que a guild vai usar), as
    // chamadas a este método são removidas pelo compilador — zero custo. Só roda no
    // build Debug, pra calibração de novos códigos de evento.
    // DEBUG (dev) OU BETA (build de teste mandado pra guild): roda em ambos. Em Release
    // final (produção), some — é removido pelo compilador, custo zero.
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogBigEvent(PhotonEvent evt)
    {
        if (evt.EventCode < 0 || _diagCount >= 800) return;
        bool hasBig = evt.Parameters.Values.Any(v =>
            (v is long l && Math.Abs(l) > 100000) || (v is int i && Math.Abs(i) > 100000));
        if (!hasBig) return;
        lock (_diagLock)
        {
            if (_diagCount >= 800) return;
            _diagCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "events_diag.txt"),
                    $"code={evt.EventCode} {parms}\n");
            }
            catch { }
        }
    }

    private static string DiagVal(object? v) => v switch
    {
        null => "null",
        byte[] b => $"byte[{b.Length}]",
        System.Array a => $"arr[{a.Length}]",
        _ => v.ToString() ?? ""
    };

    // Diagnóstico de calibração: grava eventos que carregam TEXTO de verdade num
    // parâmetro (não número, não byte[]) — é a assinatura de "alguém/algo apareceu
    // com nome" (NewCharacter, e o que suspeito ser o NewMob real, já que o código
    // 123 que mapeamos parece ser na verdade um sync de posição/vida repetitivo, sem
    // nome nenhum — não bate com o padrão de "mob spawnou"). Pula o 29 (NewCharacter,
    // já calibrado) pra focar no que falta achar. Arquivo: %LocalAppData%\XnomercyApp\named_events_diag.txt
    private static int _diagNamedCount;
    private static readonly object _diagNamedLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogNamedEvent(PhotonEvent evt)
    {
        // Pula 29 (NewCharacter) e 30 (Move) — já calibrados e de alto volume; entupiam
        // o log e escondiam códigos raros (ex: o evento de party que ainda procuramos).
        if (evt.EventCode < 0 || evt.EventCode == GameEventCodes.NewCharacter
            || evt.EventCode == GameEventCodes.Move || _diagNamedCount >= 500) return;
        bool hasText = evt.Parameters.Values.Any(v => v is string s && s.Length > 1 && !s.All(c => char.IsDigit(c) || c == ',' || c == '.' || c == '-'));
        if (!hasText) return;
        lock (_diagNamedLock)
        {
            if (_diagNamedCount >= 500) return;
            _diagNamedCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "named_events_diag.txt"),
                    $"code={evt.EventCode} {parms}\n");
            }
            catch { }
        }
    }

    // OtherGrabbedLoot (279): [1]=de quem (corpo) [2]=quem pegou [3]=é prata? (bool)
    // [4]=índice do item [5]=quantidade. Os nomes já vêm como texto no evento.
    // itemIdx/qty saem pro chamador conseguir checar duplicata contra o que já foi
    // inserido direto pelo SelfLootDetector (ver WasRecentSelfLootDirect).
    private static bool TryParseGrabbedLoot(PhotonEvent evt, DateTime now, out LootFeedRow row, out int itemIdx, out long amount)
    {
        row = null!;
        itemIdx = -1;
        string from   = evt.Parameters.TryGetValue(1, out var f) ? (f?.ToString() ?? "") : "";
        string looter = evt.Parameters.TryGetValue(2, out var l) ? (l?.ToString() ?? "") : "";
        if (looter.Length == 0 && from.Length == 0) { amount = 0; return false; }

        bool isSilver = evt.Parameters.TryGetValue(3, out var s) && s is bool sb && sb;
        amount = evt.Parameters.TryGetValue(5, out var q) ? ToLong(q) : 1;

        string item;
        string? icon = null;
        if (isSilver)
            // Prata/fama vêm ×10000 no protocolo (ponto fixo do Albion). 106.717.500 → 10.671.
            item = $"{amount / 10000:N0} prata";
        else
        {
            itemIdx = evt.Parameters.TryGetValue(4, out var ii) ? (int)ToLong(ii) : -1;
            string name = ItemCatalog.GetName(itemIdx) ?? $"item {itemIdx}";
            item = $"{amount}x {name}";
            icon = IconUrl(itemIdx);
        }
        // Heurística provisória de monstro: corpo vazio ou com "_" (ID interno do jogo).
        // Vamos calibrar com dados reais (a estrutura exata de loot de mob a gente vê no
        // modo avançado quando estiver jogando).
        bool isMob = !isSilver && (from.Length == 0 || from.Contains('_'));
        row = new LootFeedRow
        {
            Time = now.ToString("HH:mm:ss"),
            Timestamp = now,
            Looter = looter,
            Item = item,
            From = isSilver ? "" : (isMob ? "MOB" : from),
            ItemIcon = icon,
            IsSilver = isSilver,
            IsMob = isMob,
        };
        return true;
    }

    private static long ToLong(object? v) => v switch
    {
        int i => i, long l => l, short s => s, byte b => b, float fl => (long)fl, double d => (long)d, _ => 0
    };

    // Monta a URL do render oficial pra mostrar a miniatura do item. O unique_name
    // já inclui tier e encantamento (ex: T5_2H_AXE@2), então o ícone vem correto.
    private static string? IconUrl(int itemIndex)
    {
        if (itemIndex < 0) return null;
        var uniq = ItemCatalog.GetUniqueName(itemIndex);
        return string.IsNullOrEmpty(uniq) ? null : $"https://render.albiononline.com/v1/item/{uniq}.png?size=64";
    }

    // Filtro do feed limpo — reavaliado quando os checkboxes mudam.
    private bool LootRowVisible(object obj)
    {
        if (obj is not LootFeedRow r) return true;
        if (r.IsMob && ChkHideMob.IsChecked == true) return false;       // esconde mob se marcado
        // "Só minha guild": opcional — mostra só pickups feitos por gente da sua guild
        // (resolvida em background via PlayerRegistry.OwnGuild). Padrão é desmarcado
        // (mostra todo mundo por perto, igual sempre foi).
        if (ChkGuildOnlyLoot.IsChecked == true && r.Looter != PlayerRegistry.SelfName
            && (PlayerRegistry.OwnGuild.Length == 0 || PlayerRegistry.GuildOfName(r.Looter) != PlayerRegistry.OwnGuild))
            return false;
        // "Só meu grupo": idem, mas usando o rastreamento de grupo (229/240/182).
        if (ChkPartyOnlyLoot.IsChecked == true && !PlayerRegistry.IsInParty(r.Looter)) return false;
        return true;
    }

    private void LootFilter_Changed(object sender, RoutedEventArgs e) => _lootFeedView?.Refresh();

    // Limpa o feed limpo e o modo avançado (eventos crus + marcados) — não para a
    // captura, só esvazia o que já foi mostrado, igual o "Reiniciar sessão" do Medidor.
    private void BtnLootReset_Click(object sender, RoutedEventArgs e)
    {
        _lootFeed.Clear();
        _lootRows.Clear();
        _markedRows.Clear();
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"loot_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Hora;Quem pegou;Item;De quem");
        foreach (var r in _lootFeed)
            sb.AppendLine($"{r.Time};{Csv(r.Looter)};{Csv(r.Item)};{Csv(r.From)}");
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
        TxtCaptureStatus.Text = $"Exportado: {System.IO.Path.GetFileName(dlg.FileName)}";
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') ? '"' + s.Replace("\"", "\"\"") + '"' : s;

    // NewSimpleItem (GameEventCodes.LootPickup): [0]=ObjectId [1]=índice do item
    // [2]=quantidade [4]=valor estimado [7]=durabilidade.
    private static bool TryDescribeLoot(PhotonEvent evt, out string summary)
        => TryDescribeLoot(evt, out summary, out _, out _);

    private static bool TryDescribeLoot(PhotonEvent evt, out string summary, out string itemName, out string qty)
    {
        summary = "";
        itemName = "";
        qty = "";
        if (!evt.Parameters.TryGetValue(1, out var idxObj)) return false;

        int? itemIndex = idxObj switch { int i => i, short s => s, long l => (int)l, _ => null };
        if (itemIndex is null) return false;

        itemName = ItemCatalog.GetName(itemIndex.Value) ?? $"item desconhecido (índice {itemIndex})";
        qty = evt.Parameters.TryGetValue(2, out var q) ? Describe(q) : "?";
        summary = $"🎯 LOOT: {qty}x {itemName}  [unique_name={ItemCatalog.GetUniqueName(itemIndex.Value) ?? "?"}]";
        return true;
    }

    // Resolver nome de item em TODO número causava muito falso positivo (qualquer
    // delta de posição/movimento pode coincidir com um índice de item real, já que
    // tem mais de 10 mil itens). Agora só tenta resolver nome quando o evento já é
    // candidato a loot (Code == GameEventCodes.LootPickup) — bem mais específico.
    private static string Describe(object? value, bool tryResolveItemName = false)
    {
        string text = value switch
        {
            null => "null",
            byte[] bytes => $"byte[{bytes.Length}]",
            object?[] arr => $"array[{arr.Length}]",
            System.Collections.IDictionary => "dict{...}",
            Protocol16Deserializer.UnknownValue u => $"?type{u.TypeCode}",
            _ => value.ToString() ?? "",
        };

        if (tryResolveItemName)
        {
            int? maybeIndex = value switch
            {
                int i => i,
                short s => s,
                long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                _ => null,
            };
            if (maybeIndex is int idx)
            {
                var name = ItemCatalog.GetName(idx);
                if (name != null) text += $" 🎯({name})";
            }
        }
        return text;
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
            if (!url.StartsWith(SiteUrl, StringComparison.OrdinalIgnoreCase)) return; // discord etc
            if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;   // ainda na tela de login

            // Pergunta ao site se está logado e se pode acessar o Craft. /api/me pode não
            // existir ainda (site antigo sem o sistema de login novo) — nesse caso (erro/
            // resposta vazia) assume logado com Craft liberado, já que chegamos numa página
            // que não é /login. Quando a rota existir, confiamos no campo logged_in de
            // verdade — importante pro botão "Sair" funcionar (depois de deslogar, a home
            // não é /login, mas logged_in vem false e não deve reabrir a sidebar).
            bool loggedIn = true;
            bool canCraft = true;
            bool canTracker = true;
            try
            {
                var raw = await WebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){try{var x=new XMLHttpRequest();x.open('GET','/api/me',false);x.send();" +
                    "if(x.status!==200)return '';return x.responseText;}catch(e){return '';}})()");
                var inner = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
                if (!string.IsNullOrEmpty(inner))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(inner);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("logged_in", out var li)) loggedIn = li.GetBoolean();
                    if (root.TryGetProperty("can_craft", out var cc)) canCraft = cc.GetBoolean();
                    if (root.TryGetProperty("can_tracker", out var ct)) canTracker = ct.GetBoolean();
                }
            }
            catch { /* /api/me ainda não existe no site ou deu erro — segue com loggedIn=true */ }

            if (!loggedIn)
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
        PanelBlocked.Visibility = Visibility.Collapsed;
        _advancedVisible = false;

        bool isTrackerTab = tag is "loot" or "damage" or "fame";
        if ((isTrackerTab && !_canTracker) || (tag == "craft" && !_canCraft))
        {
            PanelBlocked.Visibility = Visibility.Visible;
            return;
        }

        switch (tag)
        {
            case "loot":   PanelLoot.Visibility = Visibility.Visible; break;
            case "damage": PanelDamage.Visibility = Visibility.Visible; break;
            case "fame":   PanelFame.Visibility = Visibility.Visible; break;
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
        foreach (var btn in new[] { NavLoot, NavDamage, NavFame, NavCraft })
        {
            bool isActive = btn == active;
            btn.Foreground = isActive ? NavActiveBrush : NavIdleBrush;
            btn.Background  = isActive ? NavActiveBg    : System.Windows.Media.Brushes.Transparent;
        }
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

    private async void TrayMenu_Exit_Click(object sender, RoutedEventArgs e)
    {
        // Sair pela bandeja matava o processo direto, sem esperar o diagnóstico
        // (fire-and-forget) terminar de enviar — perdia os dados de calibração de quem
        // fechava o app em vez de clicar "Parar captura". Espera até 5s pelo envio.
        await Task.WhenAny(DiagReporter.ReportDiagFilesAsync(), Task.Delay(5000));
        _exitRequested = true;
        _capture.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    // ── Atualização (Velopack + GitHub Releases) ─────────────────────────────
    // Atualização de verdade: baixa em segundo plano e aplica num reinício — não é só
    // um aviso com link manual. Pra publicar: `vpk pack` gera o instalador/pacotes,
    // `vpk upload github` sobe pra Releases do Xnomercy-App. Quem já tem o app instalado
    // baixa sozinho na próxima abertura e só precisa clicar "Reiniciar e atualizar".
    private UpdateManager? _updateMgr;
    private Velopack.UpdateInfo? _pendingUpdate;

    private async Task CheckForUpdateAsync()
    {
        try
        {
            _updateMgr = new UpdateManager(new Velopack.Sources.GithubSource(
                "https://github.com/oseiasfilipeseverino/Xnomercy-App", null, false));
            if (!_updateMgr.IsInstalled) return;   // rodando direto do dotnet build (dev) — nada a checar

            var info = await _updateMgr.CheckForUpdatesAsync();
            if (info == null) return;              // já está na última versão

            await _updateMgr.DownloadUpdatesAsync(info);
            _pendingUpdate = info;
            Dispatcher.BeginInvoke(() =>
            {
                UpdateBannerText.Text = $"Nova versão baixada: v{info.TargetFullRelease.Version} — reinicie pra aplicar";
                UpdateBanner.Visibility = Visibility.Visible;
            });
        }
        catch
        {
            // Sem internet, rate-limit do GitHub, etc. — o app funciona normal sem o aviso.
        }
    }

    private void BtnUpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_updateMgr == null || _pendingUpdate == null) return;
        _exitRequested = true;
        _capture.Dispose();
        _updateMgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    private void BtnUpdateDismiss_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;
}
