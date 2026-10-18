using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XnomercyApp.Network;

namespace XnomercyApp;

// Aba Baús e Banco e a janela "Minhas perdas" (18/10/2026). Dados em
// Network/StorageTracker.cs e Network/DeathLog.cs; valores em Network/ItemValues.cs.
public partial class MainWindow
{
    public sealed class BauLinha
    {
        public string Guid { get; init; } = "";
        public string Titulo { get; init; } = "";
        public string Detalhe { get; init; } = "";
        public string Itens { get; init; } = "";
        public string Valor { get; init; } = "";
    }

    private static long? ValorDe(int indice, long qtd) => ItemValues.Valor(indice) is long u ? u * qtd : null;

    private void RefreshBaus()
    {
        var baus = _baus.Baus();
        long total = 0;
        ListBaus.ItemsSource = baus.Select(b =>
        {
            long valor = b.Itens.Sum(i => ValorDe(i.Indice, i.Quantidade) ?? 0);
            total += valor;
            string tipo = b.Lugar.Length == 0 ? "Baú" : b.DaGuild ? "Banco da guild" : "Banco";
            return new BauLinha
            {
                Guid = b.Guid,
                Titulo = $"{tipo}" + (b.Lugar.Length > 0 ? $" — {b.Lugar}" : "") + (b.Nome.Length > 0 && b.Nome != "Baú" ? $" · aba {b.Nome}" : ""),
                Detalhe = $"visto em {b.VistoUtc.ToLocalTime():dd/MM HH:mm} · {b.Itens.Count} tipo(s) de item · {b.Itens.Sum(i => i.Quantidade):N0} unidades",
                Itens = string.Join("   ", b.Itens.Take(14).Select(i => $"{i.Quantidade:N0}× {ItemCatalog.GetName(i.Indice) ?? i.Unico}"))
                        + (b.Itens.Count > 14 ? $"   (+{b.Itens.Count - 14})" : ""),
                Valor = valor > 0 ? $"≈ {valor:N0}" : "—",
            };
        }).ToList();
        TxtBausResumo.Text = baus.Count == 0
            ? "Nada ainda — abra o banco ou um baú no jogo com a captura ligada."
            : $"{baus.Count} aba(s) guardada(s) · valor estimado somado ≈ {total:N0} prata";
    }

    private void BtnBauEsquecer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string guid }) _baus.Apagar(guid);
        RefreshBaus();
    }

    // Janela "Minhas perdas": cada morte com o que levaram, quem levou e quanto valia.
    private void BtnMinhasPerdas_Click(object sender, RoutedEventArgs e)
    {
        var texto = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12, Background = B("#1b1b1b"), Foreground = B("#DEFFFFFF"), BorderThickness = new Thickness(0),
            Padding = new Thickness(14), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        void Montar()
        {
            _mortes.PreencherValores();
            var mortes = _mortes.Mortes();
            if (mortes.Count == 0)
            {
                texto.Text = "Nenhuma perda registrada.\n\nQuando você morre e alguém saqueia o seu corpo com a captura ligada,\n" +
                             "cada item que levaram aparece aqui, com quem levou e o valor estimado.";
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{mortes.Count} morte(s) · perda estimada total ≈ {mortes.Sum(m => m.Valor):N0} prata");
            foreach (var m in mortes)
            {
                sb.AppendLine();
                sb.AppendLine($"{m.InicioUtc.ToLocalTime():dd/MM HH:mm}" + (m.Mapa.Length > 0 ? $" — {m.Mapa}" : "") +
                              $" · {m.Itens.Count} item(ns) · ≈ {m.Valor:N0} prata");
                foreach (var g in m.Itens.GroupBy(i => (i.Quem, i.Guild)))
                {
                    sb.AppendLine($"   {g.Key.Quem}" + (g.Key.Guild.Length > 0 ? $" [{g.Key.Guild}]" : "") + $" levou ≈ {g.Sum(i => i.Valor):N0}:");
                    foreach (var i in g)
                        sb.AppendLine($"      {i.Quantidade}× {ItemCatalog.GetName(i.Indice) ?? i.Unico}" + (i.Valor > 0 ? $"  (≈ {i.Valor:N0})" : ""));
                }
            }
            texto.Text = sb.ToString();
        }
        var limpar = new Button
        {
            Content = "Apagar histórico de perdas", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0),
            Background = B("#2e2e2e"), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        limpar.Click += (_, _) =>
        {
            if (MessageBox.Show("Apagar o histórico de perdas?", "Minhas perdas", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _mortes.Limpar();
            Montar();
        };
        var painel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(limpar, Dock.Bottom);
        painel.Children.Add(limpar);
        painel.Children.Add(texto);
        Montar();
        new Window { Title = "Minhas perdas", Width = 760, Height = 560, Owner = this, Background = B("#1a1a1a"), Content = painel }.Show();
    }
}
