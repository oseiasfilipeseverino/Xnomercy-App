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
}
