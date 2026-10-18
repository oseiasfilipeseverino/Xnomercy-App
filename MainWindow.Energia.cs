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
        if (WebView.CoreWebView2 == null || !SiteConfig.EhDoSite(url))
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
            // Com a sessão do login do WebView, pelo C# (ver MainWindow.Site.cs): pega o
            // token CSRF e posta. O script assíncrono de antes nunca devolvia a resposta.
            string texto;
            var (stCsrf, csrfJson) = await ChamarSiteAsync(System.Net.Http.HttpMethod.Get, "/api/app/csrf");
            if (stCsrf == 0) texto = "";
            else if (stCsrf != 200) texto = "{\"erro\":\"login\"}";
            else
            {
                string? token = null;
                try { using var d = System.Text.Json.JsonDocument.Parse(csrfJson); token = d.RootElement.GetProperty("token").GetString(); }
                catch { }
                var (st, resp) = await ChamarSiteAsync(System.Net.Http.HttpMethod.Post, "/api/app/energia", corpo, token);
                var obj = st == 0 ? null : (TryParseObj(resp) ?? new System.Text.Json.Nodes.JsonObject());
                if (obj != null) obj["status"] = st;
                texto = obj?.ToJsonString() ?? "";
            }
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

    private static System.Text.Json.Nodes.JsonObject? TryParseObj(string s)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(s) as System.Text.Json.Nodes.JsonObject; } catch { return null; }
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
