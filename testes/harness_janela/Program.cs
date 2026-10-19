using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using XnomercyApp;
using XnomercyApp.Network;

// Abre a JANELA REAL do app (sem mostrar), manda eventos de fama e prata no formato
// dos diagnosticos reais de agosto/setembro pelo mesmo metodo que a captura usa
// (OnCaptureEvent) e confere o numero que aparece na aba Fama & Prata.
int falhas = 0;
void Checar(bool cond, string rotulo) { if (!cond) falhas++; Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}"); }
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

// catalogo de itens (cache local; baixa so se nao tiver) pro teste da arma
try { ItemCatalog.EnsureLoadedAsync().GetAwaiter().GetResult(); } catch { }

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
    chkGrupo.IsChecked = true;
    view.Refresh();
    Checar(view.Cast<LootFeedRow>().Any(r => r.Looter == "Você"), "'So meu grupo': o SEU loot continua aparecendo");
    chkGrupo.IsChecked = false;
    view.Refresh();

    var csv = typeof(MainWindow).GetMethod("MontarCsvLoot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    ((CheckBox)w.FindName("ChkGuildOnlyLoot")).IsChecked = false;
    object[] a0 = { 0 };
    csv.Invoke(w, a0);
    Checar((int)a0[0] == 3, $"sem filtro: exporta todo mundo ({a0[0]})");
    ((CheckBox)w.FindName("ChkGuildOnlyLoot")).IsChecked = true;
    object[] a1 = { 0 };
    var texto = (string)csv.Invoke(w, a1)!;
    Checar((int)a1[0] == 2 && texto.Contains("Aliado;xnomercy") && texto.Contains("Você;XNoMercy") && !texto.Contains("Inimigo"),
           $"'So minha guild' marcado: CSV so com a guild (voce + aliado, sem o inimigo) ({a1[0]})");

    // Arma no medidor: a sua vem do jogo (90), e quem ja estava na tela tambem
    on.Invoke(w, new object[] { Ev(90, new() { [0] = 64284L, [2] = new short[] { 9233, 0, 0, 0, 0, 0, 0, 0, 0, 0 } }) });
    Checar(!string.IsNullOrEmpty(GroupGear.MinhaArma()), $"sua arma lida do jogo, sem depender da API ({GroupGear.MinhaArma()})");
    w.Close();
    app.Shutdown();
});
t.SetApartmentState(ApartmentState.STA);
t.Start(); t.Join();
Console.WriteLine(falhas == 0 ? "\nOK: janela recebe fama e prata; export do filtro de guild; sua arma" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
