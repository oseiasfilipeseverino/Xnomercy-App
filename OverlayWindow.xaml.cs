using System.Windows;
using System.Windows.Input;

namespace XnomercyApp;

/// <summary>
/// Mini painel flutuante "sempre no topo" com fama/prata/mobs por hora — pra
/// acompanhar o farm sem alt-tab. Aberto/fechado pelo botão "Overlay" na sidebar;
/// os números são atualizados pelo mesmo timer de 1s do painel Fama & Prata.
/// Fechar aqui só esconde (Hide) — a MainWindow é a dona do ciclo de vida.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        // Canto superior direito da tela primária, com uma folga — longe do
        // minimapa do Albion (canto inferior direito) e da barra de habilidades.
        Left = SystemParameters.WorkArea.Right - Width - 16;
        Top = SystemParameters.WorkArea.Top + 16;
    }

    public void UpdateStats(long fame, double famePerHour, long silver, double silverPerHour,
                            int mobKills, double mobsPerHour, TimeSpan elapsed)
    {
        TxtOvFame.Text = $"{fame:N0} · {famePerHour:N0}/h";
        TxtOvSilver.Text = $"{silver:N0} · {silverPerHour:N0}/h";
        TxtOvMobs.Text = $"{mobKills:N0} · {mobsPerHour:0.0}/h";
        TxtOvTime.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Hide();

    // Fechar de verdade (Alt+F4) também vira Hide — senão a janela era destruída
    // e o botão "Overlay" da sidebar quebrava ao tentar reabrir uma janela morta.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
