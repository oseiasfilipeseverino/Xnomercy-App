using Velopack;

namespace XnomercyApp;

public static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        // Tem que ser a PRIMEIRA coisa que o app faz — antes de qualquer UI. É o Velopack
        // que intercepta os argumentos especiais que o instalador/updater usa (pós-instalação,
        // pós-atualização, etc). O Velopack recomenda Main() em vez de App.OnStartup porque
        // alguns desses hooks (criar atalho, por exemplo) precisam rodar bem no início.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
