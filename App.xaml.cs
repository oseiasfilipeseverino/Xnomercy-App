using System.IO;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace XnomercyApp;

/// <summary>
/// Interaction logic for App.xaml.
/// Registra tratadores globais de exceção: um app que lê pacotes de rede o tempo todo
/// não pode morrer por causa de um pacote fora do padrão. Em vez de crashar, registra
/// o erro num log e segue.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // VelopackApp.Build().Run() agora roda em Program.Main (recomendação do Velopack —
        // precisa ser a 1ª coisa do processo, antes até da inicialização do WPF).
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;   // não derruba a UI
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log(ex);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = XnomercyApp.Network.PastaDados.Raiz;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* não há o que fazer se nem o log grava */ }

        // Não vai sozinho pra lugar nenhum (desde 18/10/2026): fica no errors.log e o
        // player manda pelo botão "Enviar erro p/ liderança" se quiser.
    }
}
