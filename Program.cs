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

        var app = new App();
        app.InitializeComponent();
        app.Run();
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
