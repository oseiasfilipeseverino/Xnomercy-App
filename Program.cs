using System.Diagnostics;
using System.Security.Principal;
using Velopack;

namespace XnomercyApp;

public static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        // Tem que ser a PRIMEIRA coisa que o app faz — antes de qualquer UI. É o Velopack
        // que intercepta os argumentos especiais que o instalador/updater usa (pós-instalação,
        // pós-atualização, etc) e ENCERRA o processo sozinho nesses casos — por isso isso
        // precisa rodar mesmo sem ser administrador (senão o instalador trava pedindo UAC
        // numa hora que ninguém está olhando).
        VelopackApp.Build().Run();

        // A captura de pacote (Npcap) exige admin. Em vez de forçar isso no manifest (o que
        // quebrava o instalador do Velopack — ele tenta abrir o app recém-instalado sem ser
        // admin), o app se relança como admin sozinho aqui, depois que os ganchos acima já
        // passaram. Resultado pro usuário é o mesmo: sempre abre pedindo UAC.
        if (!IsRunningAsAdministrator())
        {
            RelaunchAsAdministrator(args);
            return;
        }

        // UMA cópia só (18/10/2026). O "X" esconde o app na bandeja; abrir de novo pelo
        // atalho subia uma SEGUNDA cópia: duas capturas, e as duas gravando os mesmos
        // arquivos (baús, mapas, lutas) — a última a gravar apagava o que a outra tinha.
        // Agora a segunda só pede pra primeira aparecer e fecha. Na troca de versão, a
        // antiga ainda pode estar saindo: espera ela soltar antes de desistir.
        using var unica = new System.Threading.Mutex(false, @"Local\XnomercyApp.UmaCopia");
        bool minha;
        try { minha = unica.WaitOne(0); }
        catch (System.Threading.AbandonedMutexException) { minha = true; }   // a outra morreu sem soltar
        if (!minha)
        {
            try
            {
                if (System.Threading.EventWaitHandle.TryOpenExisting(@"Local\XnomercyApp.Mostrar", out var aparecer))
                    using (aparecer) aparecer.Set();
            }
            catch { /* sem como avisar: segue esperando */ }
            try { minha = unica.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (System.Threading.AbandonedMutexException) { minha = true; }
            if (!minha) return;   // a outra cópia está aberta e já apareceu
        }
        using var pedidoMostrar = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\XnomercyApp.Mostrar");

        var app = new App();
        app.InitializeComponent();
        new System.Threading.Thread(() =>
        {
            while (pedidoMostrar.WaitOne())
                app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.MostrarJanela());
        }) { IsBackground = true, Name = "uma-copia" }.Start();
        app.Run();
        try { unica.ReleaseMutex(); } catch { }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdministrator(string[] args)
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args),
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Usuário cancelou o UAC — só fecha, sem o app pela metade.
        }
    }
}
