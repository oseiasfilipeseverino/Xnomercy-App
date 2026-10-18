using System.Linq;
using System.Net.Http;
using System.Text;

namespace XnomercyApp;

/// <summary>
/// Chamada ao site COM o login do WebView, feita pelo C# (18/10/2026).
///
/// Antes o app rodava um script assíncrono dentro do WebView (fetch) e lia o
/// resultado do ExecuteScriptAsync. Só que o ExecuteScriptAsync NÃO espera promessa:
/// devolve "{}" na hora. A conferência de login (/api/me) nunca recebeu a resposta
/// de verdade — caía sempre no "assume logado". Quando a 1.0.67 parou de liberar
/// sem resposta, o app ficou preso em "O site não respondeu" pra todo mundo. O envio
/// do registro de energia tinha o mesmo defeito (sempre "não consegui enviar").
///
/// Agora: o C# pega os cookies do WebView (a sessão do login no site) e chama o site
/// com HttpClient. Referer vai junto porque o CSRF do Flask confere o Referer em HTTPS.
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient _site = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>(código HTTP, corpo). Código 0 = sem resposta (rede, tempo esgotado).</summary>
    private async Task<(int Status, string Corpo)> ChamarSiteAsync(HttpMethod metodo, string caminho, string? json = null, string? csrf = null)
    {
        try
        {
            var cookies = WebView.CoreWebView2 == null
                ? new List<Microsoft.Web.WebView2.Core.CoreWebView2Cookie>()
                : await WebView.CoreWebView2.CookieManager.GetCookiesAsync(SiteUrl);
            using var req = new HttpRequestMessage(metodo, SiteUrl + caminho);
            if (cookies.Count > 0)
                req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
            req.Headers.TryAddWithoutValidation("Referer", SiteUrl + "/");
            if (csrf != null) req.Headers.TryAddWithoutValidation("X-CSRFToken", csrf);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _site.SendAsync(req);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch
        {
            return (0, "");
        }
    }
}
