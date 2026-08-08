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

    // Campos crus, guardados separados do texto já formatado de `Item` — são o que o
    // formato do AO Loot Logger (ao-loot-logger-viewer) exige em colunas próprias.
    public string ItemUniqueName { get; init; } = "";   // ex: T4_MAIN_MACE_HELL@4
    public string ItemPlainName { get; init; } = "";    // nome sem o "3x " na frente
    public long Quantity { get; init; }
    public string LooterGuild { get; init; } = "";
    public string FromGuild { get; init; } = "";
}

/// <summary>
/// Linha do Medidor de Dano. Notifica mudanças (INotifyPropertyChanged) porque as
/// linhas são ATUALIZADAS NO LUGAR, não recriadas: o painel antes fazia
/// _damageRows.Clear() + re-Add a cada tick (350ms), e o Clear() dispara um Reset
/// que faz o ListView perder a seleção — quem clicava num jogador pra ver a quebra
/// por habilidade tinha o painel fechado na cara quase 3x por segundo durante a
/// luta. Mantendo a MESMA instância por jogador, a seleção sobrevive.
/// </summary>
public sealed class DamageRowDisplay : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    public string Player { get; init; } = "";

    private long _damage;
    public long Damage
    {
        get => _damage;
        set { if (_damage != value) { _damage = value; Notify(nameof(Damage)); } }
    }

    private string _damagePct = "";
    public string DamagePct
    {
        get => _damagePct;
        set { if (_damagePct != value) { _damagePct = value; Notify(nameof(DamagePct)); } }
    }

    private double _damagePctValue;   // 0-100, pra desenhar a barra (DamagePct é só o texto formatado)
    public double DamagePctValue
    {
        get => _damagePctValue;
        set { if (_damagePctValue != value) { _damagePctValue = value; Notify(nameof(DamagePctValue)); } }
    }

    private long _healing;
    public long Healing
    {
        get => _healing;
        set { if (_healing != value) { _healing = value; Notify(nameof(Healing)); } }
    }

    private string? _weaponIcon;   // URL do render oficial do item
    public string? WeaponIcon
    {
        get => _weaponIcon;
        set { if (_weaponIcon != value) { _weaponIcon = value; Notify(nameof(WeaponIcon)); } }
    }
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

public sealed class SessionHistoryRowDisplay
{
    public string Period { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Fame { get; init; } = "";
    public string YellowFame { get; init; } = "";
    public string Silver { get; init; } = "";
    public string Damage { get; init; } = "";
    public string LootItems { get; init; } = "";
}

public partial class MainWindow : Window
{
    private const string SiteUrl = SiteConfig.BaseUrl;
    private const int MaxLootRows = 500; // evita a lista crescer sem limite numa sessão longa

    // Versão real agora vem do pacote Velopack (definida no `vpk pack --packVersion`),
    // não precisa mais bumpar nada aqui à mão.

    // Permite fechar de verdade pelo menu da bandeja (em vez de só minimizar).
    private bool _exitRequested;

    // Handlers de eventos ESTÁTICOS guardados pra poder desassinar na saída — ver
    // DesassinarEventosEstaticos() e o comentário na assinatura, no construtor.
    private Action? _onEnteredDungeon;
    private Action? _onLeftDungeon;

    private void DesassinarEventosEstaticos()
    {
        try
        {
            SelfLootDetector.SelfLootDetected -= OnSelfLootDetected;
            if (_onEnteredDungeon is not null) DungeonTimerTracker.EnteredDungeon -= _onEnteredDungeon;
            if (_onLeftDungeon is not null) DungeonTimerTracker.LeftDungeon -= _onLeftDungeon;
        }
        catch { /* saída em andamento: não vale derrubar o fechamento por causa disso */ }
    }

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
    private readonly ObservableCollection<SessionHistoryRowDisplay> _sessionRows = new();
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
    // Mesmo modelo do site redesenhado: dourado = ação/navegação ativa, vermelho
    // reservado pra semântica (perigo/perdas). Tokens iguais aos do pg_base.html.
    private static readonly System.Windows.Media.Brush NavActiveBg = B("#292213");
    private static readonly System.Windows.Media.Brush NavActiveBrush = B("#c9a227");
    private static readonly System.Windows.Media.Brush NavIdleBrush   = B("#8CFFFFFF");

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
        ListSessionHistory.ItemsSource = _sessionRows;
        RefreshSessionHistory();

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
        // Estes três são eventos ESTÁTICOS, e os handlers capturam esta janela. Sem
        // desassinar, a MainWindow fica presa na memória pelo resto do processo —
        // hoje inofensivo (a janela vive tanto quanto o app), mas vira vazamento no
        // dia em que ela for recriada. Guardados em campo pra dar pra remover em
        // DesassinarEventosEstaticos(), chamada na saída.
        _onEnteredDungeon = () => Dispatcher.BeginInvoke(StartDungeonTimer);
        _onLeftDungeon    = () => Dispatcher.BeginInvoke(StopDungeonTimer);
        SelfLootDetector.SelfLootDetected += OnSelfLootDetected;
        // Timer de fechamento de dungeon — ver DungeonTimerTracker.cs.
        _capture.OpResponseReceived += DungeonTimerTracker.HandleOpResponse;
        DungeonTimerTracker.EnteredDungeon += _onEnteredDungeon;
        DungeonTimerTracker.LeftDungeon += _onLeftDungeon;
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
        var captureStartedAt = DateTime.MinValue;
        diagTimer.Tick += (_, _) =>
        {
            if (!_capturing) { captureStartedAt = DateTime.MinValue; return; }
            if (captureStartedAt == DateTime.MinValue) captureStartedAt = DateTime.Now;

            // O sinal de "não está funcionando" é EVENTO DECODIFICADO em zero, não
            // pacote bruto: o filtro agora é "udp" (a porta do jogo é descoberta pelo
            // conteúdo, porque acelerador de rota sorteia porta), então sempre chega
            // algum UDP da máquina e o contador bruto nunca fica em 0. O que importa
            // é se algo virou evento do Albion de verdade.
            if (_capture.DiagEventsDecoded == 0 && (DateTime.Now - captureStartedAt).TotalSeconds > 45)
            {
                TxtCaptureStatus.Text =
                    $"⚠️ Capturando ({_capture.DiagRawPackets} pacotes vistos), mas nenhum evento do Albion foi " +
                    "reconhecido ainda. Confira se o jogo está aberto e em partida. Se continuar assim, use " +
                    "\"Diagnóstico de rede\" e mande o resultado pra liderança.";
                return;
            }

            var learned = _capture.LearnedPorts;
            string extra = learned.Count > 0
                ? $" | porta detectada: {string.Join(", ", learned)}"
                : "";
            TxtCaptureStatus.Text =
                $"Pacotes brutos: {_capture.DiagRawPackets} | Payloads extraídos: {_capture.DiagAppPayloadsExtracted} | Eventos decodificados: {_capture.DiagEventsDecoded}{extra}";
        };
        diagTimer.Start();

        // Duração da sessão e taxa/hora mudam mesmo sem nenhum evento novo chegar
        // (ex: parado sem farmar) — sem um tick próprio, o painel só atualizava
        // quando um ganho de fama/prata disparava _fameDirty, deixando o relógio
        // e a taxa parados na tela até o próximo evento.
        var famePanelTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        famePanelTimer.Tick += (_, _) =>
        {
            SampleSessionChart();
            if (PanelFame.Visibility == Visibility.Visible) RefreshFamePanel();
            if (_overlay?.IsVisible == true)
                _overlay.UpdateStats(_fameTracker.TotalFame, _fameTracker.FamePerHour,
                                     _fameTracker.TotalSilver, _fameTracker.SilverPerHour,
                                     _killTracker.TotalKills, _killTracker.KillsPerHour,
                                     DateTime.Now - _fameTracker.SessionStart);
        };
        famePanelTimer.Start();
    }

    // ── Mini-overlay flutuante (sempre no topo) ─────────────────────────────
    private OverlayWindow? _overlay;

    private void BtnOverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        _overlay ??= new OverlayWindow { Owner = this };
        if (_overlay.IsVisible) _overlay.Hide();
        else _overlay.Show();
    }


}
