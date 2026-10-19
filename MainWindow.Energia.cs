using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Registro de energia da guild. O oficial abre o registro no jogo com o app aberto e
// o app lê (GuildEnergyLog). A planilha continua valendo; o site descarta repetido.
//
// Desde 18/10/2026 (pedido do dono da guild) o envio é AUTOMÁTICO quando o app tem
// certeza de que é a aba de ENERGIA (o pedido do registro com [1]=2 foi visto): o app
// junta as páginas, manda sozinho e mostra só o resultado. O que não foi aceito ainda
// (site fora do ar, sem login) fica guardado em energia_pendente.json e é reenviado de
// 2 em 2 minutos, inclusive depois de fechar e abrir o app. Sem certeza da aba (o app
// não viu o pedido), continua o aviso pra conferir e mandar com um clique — a aba de
// prata vem no mesmo formato e não pode ir pro site como energia.
public partial class MainWindow
{
    // Sem certeza de que é energia: espera o clique em "Enviar ao site".
    private readonly Dictionary<string, GuildEnergyLog.Entrada> _energiaPendente = new();
    // Aba de energia confirmada: vai sozinho, guardado em disco até o site aceitar.
    private readonly Dictionary<string, GuildEnergyLog.Entrada> _energiaAuto = new();
    private bool _enviandoEnergia;
    // O site respondeu 403 (quem está logado não é da gestão): o site nunca vai aceitar,
    // então não insiste nesta sessão e não fica avisando.
    private bool _energiaSemPermissao;
    private bool _energiaAvisouLogin;   // "entre no site" aparece uma vez por sessão, não a cada 2 min
    private System.Windows.Threading.DispatcherTimer? _energiaJuntar, _energiaRepetir, _energiaEsconderAviso;
    private const int MaxEnergiaPorEnvio = 3000;
    // Só pros testes: troca o envio de verdade ao site (ver harness_janela).
    private System.Func<IReadOnlyCollection<GuildEnergyLog.Entrada>, Task<(int Status, string Texto)>>? _postarEnergiaTeste;   // o mesmo teto do site (/api/app/energia)

    private static string ArquivoEnergia => System.IO.Path.Combine(XnomercyApp.Network.PastaDados.Raiz, "energia_pendente.json");

    private static string Chave(GuildEnergyLog.Entrada e) => $"{e.QuandoUtc:O}|{e.Jogador}|{e.Quantidade}";

    /// <summary>Chamado ao abrir: retoma o que ficou sem enviar e liga a repetição.</summary>
    private void IniciarEnvioDeEnergia()
    {
        if (ArquivoSeguro.Ler<List<GuildEnergyLog.Entrada>>(ArquivoEnergia, out var salvas) && salvas != null)
            foreach (var e in salvas) _energiaAuto[Chave(e)] = e;

        // O jogo manda o registro em páginas seguidas (101 linhas cada): espera as
        // páginas pararem de chegar e manda tudo num envio só.
        _energiaJuntar = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(4) };
        _energiaJuntar.Tick += (_, _) => { _energiaJuntar.Stop(); _ = EnviarEnergiaAutomaticoAsync(); };

        _energiaRepetir = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMinutes(2) };
        _energiaRepetir.Tick += (_, _) => _ = EnviarEnergiaAutomaticoAsync();
        _energiaRepetir.Start();

        _energiaEsconderAviso = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(10) };
        _energiaEsconderAviso.Tick += (_, _) =>
        {
            _energiaEsconderAviso.Stop();
            if (_energiaPendente.Count == 0) EnergiaBanner.Visibility = Visibility.Collapsed;
        };
    }

    private void OnEnergiaLida(List<GuildEnergyLog.Entrada> lista, bool abaDeEnergia)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (abaDeEnergia)
            {
                foreach (var e in lista) _energiaAuto[Chave(e)] = e;
                GuardarEnergiaPendente();
                _energiaJuntar?.Stop();
                _energiaJuntar?.Start();
            }
            else
            {
                // O jogo manda o registro em páginas: junta o que já estava pendente.
                foreach (var e in lista) _energiaPendente[Chave(e)] = e;
                MostrarEnergiaPendente();
            }
        });
    }

    private void GuardarEnergiaPendente()
    {
        if (_energiaAuto.Count == 0) { try { File.Delete(ArquivoEnergia); } catch { } return; }
        ArquivoSeguro.GravarJson(ArquivoEnergia, _energiaAuto.Values.OrderBy(e => e.QuandoUtc).ToList());
    }

    /// <summary>Manda sozinho o que veio da aba de energia. Falhou por rede/login: tenta de novo depois.</summary>
    private async Task EnviarEnergiaAutomaticoAsync()
    {
        if (_enviandoEnergia || _energiaSemPermissao || _energiaAuto.Count == 0) return;
        _enviandoEnergia = true;
        try
        {
            var lote = _energiaAuto.Values.OrderBy(e => e.QuandoUtc).Take(MaxEnergiaPorEnvio).ToList();
            var (status, texto) = await (_postarEnergiaTeste ?? PostarEnergiaAsync)(lote);
            string resumo = ResumoEnvioEnergia(texto, out bool ok);
            if (ok || status == 400)
            {
                // Aceito — ou recusado por dado inválido, que nunca vai passar: tira da fila.
                foreach (var e in lote) _energiaAuto.Remove(Chave(e));
                GuardarEnergiaPendente();
                AvisoEnergia(ok ? "Registro de energia enviado ao site sozinho. " + resumo : resumo);
                if (_energiaAuto.Count > 0) _energiaJuntar?.Start();   // sobrou mais que um envio
            }
            else if (status == 403)
            {
                _energiaSemPermissao = true;   // não é da gestão: o site não aceita de quem não é
                _energiaAuto.Clear();
                GuardarEnergiaPendente();
            }
            // 401 (sem login no site) ou sem resposta: fica na fila, o timer tenta de novo.
            else if (status == 401 && !_energiaAvisouLogin)
            {
                _energiaAvisouLogin = true;
                AvisoEnergia($"Registro de energia guardado ({_energiaAuto.Count} entrada(s)): entre no site pelo app que ele vai sozinho.");
            }
        }
        catch (System.Exception ex)
        {
            LogEnergia("envio automatico falhou: " + ex.Message);   // fica na fila
        }
        finally { _enviandoEnergia = false; }
    }

    private void AvisoEnergia(string texto)
    {
        if (_energiaPendente.Count > 0) return;   // o aviso de conferir tem prioridade
        TxtEnergiaBanner.Text = texto;
        BtnEnergiaEnviar.Visibility = Visibility.Collapsed;
        BtnEnergiaIgnorar.Visibility = Visibility.Collapsed;
        EnergiaBanner.Visibility = Visibility.Visible;
        _energiaEsconderAviso?.Stop();
        _energiaEsconderAviso?.Start();
    }

    private void MostrarEnergiaPendente()
    {
        var itens = _energiaPendente.Values.OrderBy(e => e.QuandoUtc).ToList();
        if (itens.Count == 0) { EnergiaBanner.Visibility = Visibility.Collapsed; return; }
        var de = itens.First().QuandoUtc.ToLocalTime();
        var ate = itens.Last().QuandoUtc.ToLocalTime();
        TxtEnergiaBanner.Text = $"Registro de energia da guild lido: {itens.Count} entrada{(itens.Count > 1 ? "s" : "")} " +
                                $"({de:dd/MM HH:mm} a {ate:dd/MM HH:mm}). Confira se é o registro de ENERGIA e envie ao site.";
        BtnEnergiaEnviar.Visibility = Visibility.Visible;
        BtnEnergiaIgnorar.Visibility = Visibility.Visible;
        BtnEnergiaEnviar.IsEnabled = true;
        BtnEnergiaEnviar.Content = "Enviar ao site";
        _energiaEsconderAviso?.Stop();
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
        _enviandoEnergia = true;
        BtnEnergiaEnviar.IsEnabled = false;
        BtnEnergiaEnviar.Content = "Enviando...";
        try
        {
            var (_, texto) = await PostarEnergiaAsync(_energiaPendente.Values.Take(MaxEnergiaPorEnvio).ToList());
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

    /// <summary>
    /// Posta no site com o login do WebView (ver MainWindow.Site.cs). Devolve o código
    /// HTTP (0 = sem resposta) e o JSON da resposta com "status" — o que ResumoEnvioEnergia lê.
    /// </summary>
    private async Task<(int Status, string Texto)> PostarEnergiaAsync(IReadOnlyCollection<GuildEnergyLog.Entrada> lote)
    {
        if (WebView.CoreWebView2 == null) return (0, "");   // o site ainda não abriu: sem login pra usar
        var (stCsrf, csrfJson) = await ChamarSiteAsync(System.Net.Http.HttpMethod.Get, "/api/app/csrf");
        if (stCsrf == 0) return (0, "");
        if (stCsrf != 200) return (401, "{\"erro\":\"login\"}");
        string? token = null;
        try { using var d = System.Text.Json.JsonDocument.Parse(csrfJson); token = d.RootElement.GetProperty("token").GetString(); }
        catch { return (401, "{\"erro\":\"login\"}"); }   // sem token não há envio: tratar como sem login, não como enviado
        var (st, resp) = await ChamarSiteAsync(System.Net.Http.HttpMethod.Post, "/api/app/energia", GuildEnergyLog.ParaJson(lote), token);
        if (st == 0) return (0, "");
        var obj = TryParseObj(resp) ?? new System.Text.Json.Nodes.JsonObject();
        obj["status"] = st;
        return (st, obj.ToJsonString());
    }

    private static void LogEnergia(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(ArquivoEnergia)!, "errors.log"),
                $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Energia] {msg}\n");
        }
        catch { }
    }

    private static System.Text.Json.Nodes.JsonObject? TryParseObj(string s)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(s) as System.Text.Json.Nodes.JsonObject; } catch { return null; }
    }

    public static string ResumoEnvioEnergia(string json, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(json)) return "O site não respondeu a tempo. Tente de novo.";
        System.Text.Json.JsonDocument doc;
        // Página de erro do servidor (HTML) não é "enviado": antes estourava aqui.
        try { doc = System.Text.Json.JsonDocument.Parse(json); }
        catch { return "O site respondeu algo inesperado. Tente de novo."; }
        using (doc)
        {
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
}
