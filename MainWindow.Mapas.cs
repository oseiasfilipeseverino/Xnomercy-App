using System.Linq;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Aba Mapas e Dungeons (18/10/2026): o histórico de zonas e as runs de dungeon,
// com o que você ganhou em cada uma. Os dados vêm do Network/MapHistory.cs.
public partial class MainWindow
{
    public sealed class MapaLinha
    {
        public string Titulo { get; init; } = "";
        public string Detalhe { get; init; } = "";
        public string Numeros { get; init; } = "";
    }

    private void RefreshMapas()
    {
        var runs = _mapas.Runs();
        TxtRunsVazio.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListRuns.ItemsSource = runs.Select(r => new MapaLinha
        {
            Titulo = $"{r.Tipo} · {r.Salas} sala{(r.Salas == 1 ? "" : "s")}" + (r.FimUtc == null ? " · EM ANDAMENTO" : ""),
            Detalhe = Periodo(r.InicioUtc, r.FimUtc) + (r.Mobs > 0 ? $" · {r.Mobs} mobs" : ""),
            Numeros = Ganhos(r.Fama, r.Prata, r.Loot, r.Itens, r.InicioUtc, r.FimUtc),
        }).ToList();

        ListMapas.ItemsSource = _mapas.Visitas().Take(80).Select(v => new MapaLinha
        {
            Titulo = v.Nome + (v.FimUtc == null ? " · você está aqui" : ""),
            Detalhe = Periodo(v.InicioUtc, v.FimUtc) + (v.Mobs > 0 ? $" · {v.Mobs} mobs" : ""),
            Numeros = Ganhos(v.Fama, v.Prata, v.Loot, v.Itens, v.InicioUtc, v.FimUtc),
        }).ToList();
    }

    private static string Periodo(DateTime inicioUtc, DateTime? fimUtc)
    {
        var dur = (fimUtc ?? DateTime.UtcNow) - inicioUtc;
        string tempo = dur.TotalMinutes < 1 ? $"{Math.Max(0, (int)dur.TotalSeconds)}s"
                     : dur.TotalHours < 1 ? $"{(int)dur.TotalMinutes} min"
                     : $"{(int)dur.TotalHours}h{dur.Minutes:00}";
        return $"{inicioUtc.ToLocalTime():dd/MM HH:mm} · {tempo}";
    }

    private static string Ganhos(long fama, long prata, long loot, int itens, DateTime inicioUtc, DateTime? fimUtc)
    {
        var partes = new List<string>();
        if (fama > 0) partes.Add($"{Num(fama)} fama");
        if (prata > 0) partes.Add($"{Num(prata)} prata");
        if (itens > 0) partes.Add(loot > 0 ? $"loot ≈ {Num(loot)}" : $"{itens} itens");
        double horas = ((fimUtc ?? DateTime.UtcNow) - inicioUtc).TotalHours;
        if (fama > 0 && horas >= 1.0 / 60) partes.Add($"{Num((long)(fama / horas))} fama/h");
        return partes.Count == 0 ? "—" : string.Join("   ", partes);
    }

    private void AtualizarMapaAtual()
    {
        var nome = _mapas.MapaAtual;
        TxtMapaAtual.Text = nome == null ? "" : $"Mapa: {nome}";
    }

    private void BtnMapasLimpar_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Apagar o histórico de mapas e runs?", "Mapas e Dungeons",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _mapas.Limpar();
        RefreshMapas();
    }
}
