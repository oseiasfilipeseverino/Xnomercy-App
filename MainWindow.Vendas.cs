using System.Linq;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Aba Vendas (18/10/2026): vendas e compras no mercado, das cartas que o jogo manda.
// O valor só existe na carta ABERTA no jogo — ver Network/MarketMailTracker.cs.
public partial class MainWindow
{
    private readonly MarketMailTracker _vendas = new();

    public sealed class VendaLinha
    {
        public string Nome { get; init; } = "";
        public string Detalhe { get; init; } = "";
        public string Valor { get; init; } = "";
        public string Cor { get; init; } = "#e2e8f0";
        public string? Icone { get; init; }
    }

    private void RefreshVendas()
    {
        long vendido = _vendas.Total(MarketMailTracker.Tipo.Venda);
        long comprado = _vendas.Total(MarketMailTracker.Tipo.Compra);
        TxtVendido.Text = Num(vendido);
        TxtComprado.Text = Num(comprado);
        TxtSaldoVendas.Text = (vendido - comprado >= 0 ? "+" : "") + Num(vendido - comprado);
        int semValor = _vendas.CartasSemValor();
        TxtCartasSemValor.Text = semValor > 0
            ? $"{semValor} carta{(semValor > 1 ? "s" : "")} ainda fechada{(semValor > 1 ? "s" : "")}"
            : "vendido − comprado";
        ListVendas.ItemsSource = _vendas.Negocios().Select(n => new VendaLinha
        {
            Nome = $"{n.Quantidade}× {ItemCatalog.GetNameByUnique(n.Item) ?? n.Item}",
            Detalhe = n.Tipo switch
            {
                MarketMailTracker.Tipo.Venda => "vendido",
                MarketMailTracker.Tipo.Compra => "comprado",
                MarketMailTracker.Tipo.VendaExpirada => "ordem de venda expirou",
                _ => "ordem de compra expirou",
            } + $" · {n.QuandoUtc.ToLocalTime():dd/MM HH:mm}",
            Valor = n.Tipo is MarketMailTracker.Tipo.Venda or MarketMailTracker.Tipo.Compra ? Num(n.Prata) : "—",
            Cor = n.Tipo == MarketMailTracker.Tipo.Venda ? "#86efac" : n.Tipo == MarketMailTracker.Tipo.Compra ? "#f87171" : "#8CFFFFFF",
            Icone = $"https://render.albiononline.com/v1/item/{n.Item}.png?size=64",
        }).ToList();
    }

    private void BtnVendasReset_Click(object sender, RoutedEventArgs e) { _vendas.Reset(); RefreshVendas(); }
}
