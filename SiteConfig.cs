namespace XnomercyApp;

/// <summary>
/// Endereço do site num lugar só. Estava repetido em MainWindow.xaml.cs e em
/// Network/DiagReporter.cs — numa troca de domínio um dos dois escapa fácil, e
/// o que escapa só aparece quando alguém usa aquela tela.
///
/// O endereço antigo (nome-xnomercy-site-production.up.railway.app) continua no
/// ar de propósito: as versões já instaladas apontam pra ele. A troca vale da
/// próxima release em diante, então o antigo não deve ser removido do Railway.
/// </summary>
public static class SiteConfig
{
    public const string BaseUrl = "https://xnomercy.com";

    /// <summary>
    /// O endereço é DO SITE? Compara o domínio exato, com https.
    ///
    /// Antes era `url.StartsWith(BaseUrl)`, e "https://xnomercy.com.qualquercoisa.net"
    /// também começa com "https://xnomercy.com". O WebView pode navegar pra fora (um
    /// link do site), e a página certa importa: é nela que o app roda o script que
    /// posta o registro de energia com a sessão da gestão.
    /// </summary>
    public static bool EhDoSite(string? url) =>
        System.Uri.TryCreate(url, System.UriKind.Absolute, out var u)
        && u.Scheme == System.Uri.UriSchemeHttps
        && (u.Host.Equals("xnomercy.com", System.StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("www.xnomercy.com", System.StringComparison.OrdinalIgnoreCase));
}
