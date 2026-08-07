using System.Windows;
using Velopack;

namespace XnomercyApp;

/// <summary>
/// Atualização automática, separada do resto do MainWindow.
///
/// Primeiro corte do arquivo de 1.723 linhas. Classe PARCIAL de propósito: o
/// MainWindow continua sendo uma classe só, apenas dividida em arquivos. Nada
/// muda de nome, nenhuma referência se quebra, e o XAML continua achando os
/// handlers — que é justamente o que `test_xaml.py` confere a cada passo.
///
/// Escolhido primeiro por ser o bloco mais isolado: só conversa com o
/// UpdateManager do Velopack e com o banner na tela.
/// </summary>
public partial class MainWindow
{
    // ── Atualização (Velopack + GitHub Releases) ─────────────────────────────
    // Atualização de verdade: baixa em segundo plano e aplica num reinício — não é só
    // um aviso com link manual. Pra publicar: `vpk pack` gera o instalador/pacotes,
    // `vpk upload github` sobe pra Releases do Xnomercy-App. Quem já tem o app instalado
    // baixa sozinho na próxima abertura e só precisa clicar "Reiniciar e atualizar".
    private UpdateManager? _updateMgr;
    private Velopack.UpdateInfo? _pendingUpdate;

    private async Task CheckForUpdateAsync()
    {
        try
        {
            _updateMgr = new UpdateManager(new Velopack.Sources.GithubSource(
                "https://github.com/oseiasfilipeseverino/Xnomercy-App", null, false));
            if (!_updateMgr.IsInstalled) return;   // rodando direto do dotnet build (dev) — nada a checar

            var info = await _updateMgr.CheckForUpdatesAsync();
            if (info == null) return;              // já está na última versão

            await _updateMgr.DownloadUpdatesAsync(info);
            _pendingUpdate = info;
            _ = Dispatcher.BeginInvoke(() =>
            {
                UpdateBannerText.Text = $"Nova versão baixada: v{info.TargetFullRelease.Version} — reinicie pra aplicar";
                UpdateBanner.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            // Sem internet, rate-limit do GitHub, etc. — o app funciona normal sem o aviso
            // (não é erro grave o bastante pra mandar pro Discord como "crash", isso
            // aconteceria toda vez que alguém abrisse o app sem internet). Mas registra
            // localmente — antes, se o update quebrasse de forma PERSISTENTE (token/URL
            // do GitHub mudou, repo virou privado), não havia como diagnosticar nem
            // localmente nem remotamente por que ninguém recebia atualização.
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "update_check.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* não há o que fazer se nem o log grava */ }
        }
    }

    private void BtnUpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_updateMgr == null || _pendingUpdate == null) return;
        _exitRequested = true;
        DesassinarEventosEstaticos();
        _capture.Dispose();
        _updateMgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    private void BtnUpdateDismiss_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;
}
