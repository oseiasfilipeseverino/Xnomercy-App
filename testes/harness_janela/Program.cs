using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using XnomercyApp;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Abre a JANELA REAL do app (sem mostrar), manda eventos de fama e prata no formato
// dos diagnosticos reais de agosto/setembro pelo mesmo metodo que a captura usa
// (OnCaptureEvent) e confere o numero que aparece na aba Fama & Prata.
int falhas = 0;
void Checar(bool cond, string rotulo) { if (!cond) falhas++; Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}"); }
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

// catalogo de itens (cache local; baixa so se nao tiver) pro teste da arma
try { ItemCatalog.EnsureLoadedAsync().GetAwaiter().GetResult(); } catch { }
ItemValues.BuscarMercado = _ => Task.FromResult(new Dictionary<string, long>());   // sem consultar o mercado de verdade

var t = new Thread(() =>
{
    var app = new App();
    app.InitializeComponent();
    var w = new MainWindow();
    var on = typeof(MainWindow).GetMethod("OnCaptureEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var refresh = typeof(MainWindow).GetMethod("RefreshFamePanel", BindingFlags.NonPublic | BindingFlags.Instance)!;
    TextBlock Txt(string n) => (TextBlock)w.FindName(n);

    // UpdateFame real: [0]=voce [1]=total [2]=ganho (x10.000) [3] [4] [6] [15]
    on.Invoke(w, new object[] { Ev(82, new() { [0] = 13080L, [1] = 12080356254972L, [2] = 101149216L, [3] = 16248000L, [4] = 68322840L, [6] = 0.15f, [15] = true }) });
    // TakeSilver real: [0] [1] [2] [3]=bruto [4] [5] taxas [8]=10000
    on.Invoke(w, new object[] { Ev(62, new() { [0] = 68979L, [1] = 44551702, [2] = 69518L, [3] = 3855000L, [4] = 192750L, [5] = 366225L, [8] = 10000 }) });
    refresh.Invoke(w, null);
    Console.WriteLine($"  info   tela: fama '{Txt("TxtFameTotal").Text}', prata '{Txt("TxtSilverTotal").Text}'");
    Checar(Txt("TxtFameTotal").Text.Replace(".", "").Replace(",", "") == "10114", "fama do evento real aparece na aba (10.114)");
    Checar(Txt("TxtSilverTotal").Text.Replace(".", "").Replace(",", "") == "329", "prata liquida do evento real aparece (385 - 19 - 36 = 329)");

    // "So minha guild" no Loot Log, do jeito que aconteceu (18/10/2026): filtro LIGADO
    // antes de o app saber as guilds. Quando elas chegam, a lista tem que se refazer sozinha.
    var feed = (System.Collections.ObjectModel.ObservableCollection<LootFeedRow>)typeof(MainWindow)
        .GetField("_lootFeed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(feed);
    int Visiveis() => view.Cast<object>().Count();
    var tick = typeof(MainWindow).GetMethod("AtualizarFiltroDeGuild", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var chkGuild = (CheckBox)w.FindName("ChkGuildOnlyLoot");
    chkGuild.IsChecked = true;
    view.Refresh();
    foreach (var (quem, item) in new[] { ("Aliado", "Bolsa"), ("Inimigo", "Capa"), ("Você", "Espada") })
        feed.Add(new LootFeedRow { Time = "12:00:00", Timestamp = DateTime.Now, Looter = quem, Item = item, From = "Morto", ItemUniqueName = "T4_BAG", ItemPlainName = item, Quantity = 1 });
    tick.Invoke(w, null);
    Checar(Visiveis() == 1 && ((string)chkGuild.Content).Contains("não detectada"),
           $"guild desconhecida: so o seu loot e a caixa AVISA que a guild nao foi detectada ({Visiveis()}, '{chkGuild.Content}')");
    PlayerRegistry.HandleOpResponse(new PhotonOperationResponse(1, 0, null, new() { [0] = 64284L, [2] = "Dono", [8] = "1313", [58] = "XNoMercy", [253] = (short)2 }));
    on.Invoke(w, new object[] { Ev(29, new() { [0] = 9001L, [1] = "Aliado", [8] = "xnomercy", [40] = new short[10] }) });
    on.Invoke(w, new object[] { Ev(29, new() { [0] = 9002L, [1] = "Inimigo", [8] = "Black Order", [40] = new short[10] }) });
    tick.Invoke(w, null);                                   // o timer da tela faz isso 3x/s
    Checar(Visiveis() == 2 && (string)chkGuild.Content == "Só minha guild (XNoMercy)",
           $"guild chegou DEPOIS: a lista se refaz sozinha, sem desmarcar a caixa ({Visiveis()}, '{chkGuild.Content}')");
    chkGuild.IsChecked = false;
    var chkGrupo = (CheckBox)w.FindName("ChkPartyOnlyLoot");
    var exMembro = new LootFeedRow { Time = "11:59:00", Timestamp = DateTime.Now, Looter = "ExMembro", Item = "Bota", From = "Morto", ItemUniqueName = "T4_SHOES", ItemPlainName = "Bota", Quantity = 1, DoGrupo = true };
    feed.Add(exMembro);
    chkGrupo.IsChecked = true;
    view.Refresh();
    Checar(view.Cast<LootFeedRow>().Any(r => r.Looter == "Você"), "'So meu grupo': o SEU loot continua aparecendo");
    Checar(view.Cast<LootFeedRow>().Any(r => r.Looter == "ExMembro"),
           "'So meu grupo': quem era do grupo NA HORA do loot continua (antes sumia quando saia do grupo)");
    chkGrupo.IsChecked = false;
    feed.Remove(exMembro);
    view.Refresh();

    var csv = typeof(MainWindow).GetMethod("MontarCsvLoot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    ((CheckBox)w.FindName("ChkGuildOnlyLoot")).IsChecked = false;
    object[] a0 = { 0 };
    csv.Invoke(w, a0);
    Checar((int)a0[0] == 3, $"sem filtro: exporta todo mundo ({a0[0]})");
    ((CheckBox)w.FindName("ChkGuildOnlyLoot")).IsChecked = true;
    object[] a1 = { 0 };
    var texto = (string)csv.Invoke(w, a1)!;
    Checar((int)a1[0] == 2 && texto.Contains("Aliado;xnomercy") && texto.Contains("Dono;XNoMercy") && !texto.Contains("Você") && !texto.Contains("Inimigo"),
           $"'So minha guild' marcado: CSV so com a guild (voce com o NOME do personagem + aliado, sem o inimigo) ({a1[0]})");

    // Arma no medidor: a sua vem do jogo (90), e quem ja estava na tela tambem
    on.Invoke(w, new object[] { Ev(90, new() { [0] = 64284L, [2] = new short[] { 9233, 0, 0, 0, 0, 0, 0, 0, 0, 0 } }) });
    Checar(!string.IsNullOrEmpty(GroupGear.MinhaArma()), $"sua arma lida do jogo, sem depender da API ({GroupGear.MinhaArma()})");

    void Bombear()
    {
        var f = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => f.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(f);
    }
    object Campo(string n) => typeof(MainWindow).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;

    // Seu loot visto direto (SelfLootDetector): com o item cru, pro Loot Logger
    typeof(MainWindow).GetMethod("OnSelfLootDetected", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object?[] { 9233, 1L });
    Bombear();
    var meu = feed.FirstOrDefault(r => r.Looter == "Você" && r.ItemIndex == 9233);
    Checar(meu != null && meu.ItemUniqueName.Length > 0 && meu.Quantity == 1,
           $"seu loot guarda o item cru (o Loot Logger pulava todo o seu loot) ('{meu?.ItemUniqueName}')");

    // Energia: aba de energia CONFIRMADA vai sozinha (fica guardada ate o site aceitar)
    var ent = new List<GuildEnergyLog.Entrada> { new("Aliado", -10, new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc)), new("Outro", 10, new DateTime(2026, 10, 18, 20, 1, 0, DateTimeKind.Utc)) };
    var lida = typeof(MainWindow).GetMethod("OnEnergiaLida", BindingFlags.NonPublic | BindingFlags.Instance)!;
    lida.Invoke(w, new object[] { ent, true });
    Bombear();
    string arqEnergia = System.IO.Path.Combine(PastaDados.Raiz, "energia_pendente.json");
    var banner = (FrameworkElement)w.FindName("EnergiaBanner");
    Checar(System.IO.File.Exists(arqEnergia) && System.IO.File.ReadAllText(arqEnergia).Contains("Aliado")
           && ((System.Collections.ICollection)Campo("_energiaAuto")).Count == 2 && banner.Visibility != Visibility.Visible,
           "aba de energia confirmada: entra na fila automatica, guardada em disco, SEM pedir clique");
    // Sem certeza da aba (pedido nao visto): continua pedindo pra conferir
    lida.Invoke(w, new object[] { new List<GuildEnergyLog.Entrada> { new("Terceiro", 5, new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc)) }, false });
    Bombear();
    Checar(banner.Visibility == Visibility.Visible && ((Button)w.FindName("BtnEnergiaEnviar")).Visibility == Visibility.Visible,
           "sem ver o pedido da aba (pode ser a de prata): mostra pra conferir e enviar com um clique");

    // O envio sozinho: aceito tira da fila; sem login fica; 403 (nao e' gestao) para de tentar
    var postar = typeof(MainWindow).GetField("_postarEnergiaTeste", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var enviar = typeof(MainWindow).GetMethod("EnviarEnergiaAutomaticoAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    int Fila() => ((System.Collections.ICollection)Campo("_energiaAuto")).Count;
    void EnviarCom(int status, string json)
    {
        postar.SetValue(w, new Func<IReadOnlyCollection<GuildEnergyLog.Entrada>, Task<(int, string)>>(_ => Task.FromResult((status, json))));
        var tarefa = (Task)enviar.Invoke(w, null)!;
        while (!tarefa.IsCompleted) Bombear();
    }
    EnviarCom(401, "{\"erro\":\"login\",\"status\":401}");
    Checar(Fila() == 2 && System.IO.File.Exists(arqEnergia), $"sem login no site: continua na fila e no disco pra tentar de novo ({Fila()})");
    EnviarCom(200, "{\"novos\":2,\"repetidos\":0,\"status\":200}");
    Checar(Fila() == 0 && !System.IO.File.Exists(arqEnergia), $"site aceitou: sai da fila e do disco ({Fila()})");
    lida.Invoke(w, new object[] { ent, true });
    Bombear();
    EnviarCom(403, "{\"error\":\"So a gestao\",\"status\":403}");
    Checar(Fila() == 0 && (bool)Campo("_energiaSemPermissao"), "403 (quem esta logado nao e' da gestao): para de tentar, sem ficar avisando");

    // Fechar o app com a captura ligada guarda a sessao (antes so "Parar captura" guardava)
    typeof(MainWindow).GetField("_capturing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, true);
    typeof(MainWindow).GetMethod("GuardarSessaoAoSair", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
    typeof(MainWindow).GetField("_capturing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(w, false);
    var hist = SessionHistoryStore.Load();
    Checar(hist.Any(h => h.Fame == 10114), $"sair com a captura ligada guarda a sessao ({hist.Count} sessao(oes), fama {string.Join('/', hist.Select(h => h.Fame))})");
    w.Close();
    app.Shutdown();
});
t.SetApartmentState(ApartmentState.STA);
t.Start(); t.Join();
Console.WriteLine(falhas == 0 ? "\nOK: janela: fama e prata, filtros e exports, sua arma, energia automatica, sessao ao sair" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
