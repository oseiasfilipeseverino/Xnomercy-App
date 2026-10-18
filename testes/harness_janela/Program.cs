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
    w.Close();
    app.Shutdown();
});
t.SetApartmentState(ApartmentState.STA);
t.Start(); t.Join();
Console.WriteLine(falhas == 0 ? "\nOK: janela recebe fama e prata" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
