using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Registro de energia da guild (18/10/2026): o oficial abre o registro no jogo com
// o app aberto, o app lê (GuildEnergyLog) e mostra um aviso pra conferir e mandar
// ao site com um clique. A planilha continua valendo; o site descarta repetido.
public partial class MainWindow
{
    private readonly Dictionary<string, GuildEnergyLog.Entrada> _energiaPendente = new();
    private bool _enviandoEnergia;

    private void OnEnergiaLida(List<GuildEnergyLog.Entrada> lista)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // O jogo manda o registro em páginas: junta o que já estava pendente.
            foreach (var e in lista)
                _energiaPendente[$"{e.QuandoUtc:O}|{e.Jogador}|{e.Quantidade}"] = e;
            MostrarEnergiaPendente();
        });
    }

    private void MostrarEnergiaPendente()
    {
        var itens = _energiaPendente.Values.OrderBy(e => e.QuandoUtc).ToList();
        if (itens.Count == 0) { EnergiaBanner.Visibility = Visibility.Collapsed; return; }
        var de = itens.First().QuandoUtc.ToLocalTime();
        var ate = itens.Last().QuandoUtc.ToLocalTime();
        TxtEnergiaBanner.Text = $"Registro de energia da guild lido: {itens.Count} entrada{(itens.Count > 1 ? "s" : "")} " +
                                $"({de:dd/MM HH:mm} a {ate:dd/MM HH:mm}). Confira se é o registro de ENERGIA e envie ao site.";
        BtnEnergiaEnviar.IsEnabled = true;
        BtnEnergiaEnviar.Content = "Enviar ao site";
        EnergiaBanner.Visibility = Visibility.Visible;
    }

    private void BtnEnergiaIgnorar_Click(object sender, RoutedEventArgs e)
    {
        _energiaPendente.Clear();
        EnergiaBanner.Visibility = Visibility.Collapsed;
    }

    private async void BtnEnergiaEnviar_Click(object sender, RoutedEventArgs e)
    {
        if (_enviandoEnergia || _energiaPendente.Count == 0) return;
        var url = WebView.Source?.ToString() ?? "";
        if (WebView.CoreWebView2 == null || !url.StartsWith(SiteUrl, System.StringComparison.OrdinalIgnoreCase))
        {
            TxtEnergiaBanner.Text = "Abra o site no app (com login da gestão) e clique em Enviar de novo.";
            return;
        }
        _enviandoEnergia = true;
        BtnEnergiaEnviar.IsEnabled = false;
        BtnEnergiaEnviar.Content = "Enviando...";
        try
        {
            var corpo = GuildEnergyLog.ParaJson(_energiaPendente.Values);
            // Mesma origem do site (a sessão de login é a dele): pega o token CSRF e posta.
            var js = "(function(){return (async function(){try{" +
                     "var t=await fetch('/api/app/csrf');if(t.status!==200)return JSON.stringify({erro:'login'});" +
                     "var tk=(await t.json()).token;" +
                     "var r=await fetch('/api/app/energia',{method:'POST',headers:{'Content-Type':'application/json','X-CSRFToken':tk},body:" +
                     System.Text.Json.JsonSerializer.Serialize(corpo) + "});" +
                     "var d=await r.json().catch(function(){return {};});d.status=r.status;return JSON.stringify(d);" +
                     "}catch(e){return JSON.stringify({erro:String(e)});}})();})()";
            var tarefa = WebView.CoreWebView2.ExecuteScriptAsync(js);
            var bruto = await Task.WhenAny(tarefa, Task.Delay(60000)) == tarefa ? await tarefa : "\"\"";
            var texto = System.Text.Json.JsonSerializer.Deserialize<string>(bruto) ?? "";
            TxtEnergiaBanner.Text = ResumoEnvioEnergia(texto, out var ok);
            if (ok) _energiaPendente.Clear();
        }
        catch (System.Exception ex)
        {
            TxtEnergiaBanner.Text = "Não consegui enviar: " + ex.Message;
        }
        finally
        {
            _enviandoEnergia = false;
            BtnEnergiaEnviar.IsEnabled = _energiaPendente.Count > 0;
            BtnEnergiaEnviar.Content = _energiaPendente.Count > 0 ? "Tentar de novo" : "Enviado";
        }
    }

    public static string ResumoEnvioEnergia(string json, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(json)) return "O site não respondeu a tempo. Tente de novo.";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        int Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0;
        if (r.TryGetProperty("erro", out _) || Num("status") == 401) return "Faça login no site pelo app e tente de novo.";
        if (Num("status") == 403) return "Só a gestão pode enviar o registro de energia.";
        if (Num("status") != 200)
            return r.TryGetProperty("error", out var er) ? "O site recusou: " + er.GetString() : "O site recusou o envio.";
        ok = true;
        return $"Enviado: {Num("novos")} novo(s), {Num("repetidos")} já estavam no site" +
               (Num("fora_da_guild") > 0 ? $", {Num("fora_da_guild")} fora da guild" : "") + ".";
    }
}
