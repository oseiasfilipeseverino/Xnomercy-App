using System.Linq;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Aba Coleta (18/10/2026): o que VOCÊ coletou na sessão, por recurso, com total,
// ritmo por hora e quanto veio de bônus (coletor + Premium). Ver GatheringTracker.
public partial class MainWindow
{
    private readonly GatheringTracker _gatherTracker = new();
    private volatile bool _coletaDirty;

    public sealed class ColetaLinha
    {
        public string Nome { get; init; } = "";
        public string Detalhe { get; init; } = "";
        public string Total { get; init; } = "";
        public string? Icone { get; init; }
    }

    private static string Num(long n) => n.ToString("N0", new System.Globalization.CultureInfo("pt-BR"));

    private void RefreshColeta()
    {
        _coletaDirty = false;
        var linhas = _gatherTracker.Snapshot();
        long total = linhas.Sum(l => l.Total);
        long bonus = linhas.Sum(l => l.BonusColetor + l.BonusPremium);
        TxtColetaTotal.Text = Num(total);
        TxtColetaHora.Text = $"{Num((long)_gatherTracker.PorHora(total))}/hora";
        TxtColetaBonus.Text = Num(bonus);
        TxtColetaBonusDet.Text = $"coletor {Num(linhas.Sum(l => l.BonusColetor))} · Premium {Num(linhas.Sum(l => l.BonusPremium))}";
        TxtColetaVezes.Text = Num(linhas.Sum(l => l.Coletas));
        if (_gatherTracker.Inicio is System.DateTime ini)
            TxtColetaInicio.Text = $"Sessão de coleta desde {ini.ToLocalTime():HH:mm}";
        ListColeta.ItemsSource = linhas.Select(l => new ColetaLinha
        {
            Nome = ItemCatalog.GetName(l.Item) ?? $"item {l.Item}",
            Detalhe = $"{Num(l.Coletas)} coleta{(l.Coletas > 1 ? "s" : "")} · normal {Num(l.Normal)}"
                      + (l.BonusColetor + l.BonusPremium > 0 ? $" · bônus {Num(l.BonusColetor + l.BonusPremium)}" : ""),
            Total = Num(l.Total),
            Icone = IconUrl(l.Item),
        }).ToList();
    }

    private void BtnColetaReset_Click(object sender, RoutedEventArgs e)
    {
        _gatherTracker.Reset();
        TxtColetaInicio.Text = "Comece a coletar com a captura ligada — cada recurso que você pegar aparece aqui.";
        RefreshColeta();
    }
}
