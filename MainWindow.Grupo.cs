using System.Linq;
using System.Windows;
using XnomercyApp.Network;

namespace XnomercyApp;

// Aba Grupo (18/10/2026): arma e IP de cada pessoa do grupo. Dados em
// Network/GroupGear.cs; IP de cada item em Network/ItemPowerCatalog.cs.
public partial class MainWindow
{
    public sealed class GrupoLinha
    {
        public string Nome { get; init; } = "";
        public string Detalhe { get; init; } = "";
        public string Ip { get; init; } = "";
        public string IpDica { get; init; } = "";
        public string? Icone { get; init; }
    }

    private string _grupoAssinatura = "";

    private void RefreshGrupo()
    {
        var linhas = GroupGear.Grupo();
        var itens = linhas.Select(l => new GrupoLinha
        {
            Nome = l.Nome,
            Detalhe = (l.Arma != null ? (ItemCatalog.GetNameByUnique(l.Arma) ?? l.Arma) + " · " : "") + l.Fonte,
            Ip = l.IpReal is double r ? $"{r:N0}" : l.IpEquip is double e ? $"{e:N0}" : "—",
            IpDica = l.IpReal != null ? "IP real (inspeção)" : l.IpEquip != null ? "IP do equipamento" : "",
            Icone = l.Arma != null ? $"https://render.albiononline.com/v1/item/{l.Arma}.png?size=64" : null,
        }).ToList();

        // Redesenha só quando muda (roda a cada 350ms com a aba aberta).
        string assinatura = string.Join("|", itens.Select(i => i.Nome + i.Detalhe + i.Ip + i.IpDica));
        if (assinatura == _grupoAssinatura) return;
        _grupoAssinatura = assinatura;
        ListGrupo.ItemsSource = itens;

        var comIp = linhas.Select(l => l.IpReal ?? l.IpEquip).OfType<double>().ToList();
        TxtGrupoResumo.Text = linhas.Count <= 1 && !PartyTracker.Ativo
            ? "Nenhum grupo detectado — entre num grupo com a captura ligada."
            : $"{linhas.Count} no grupo" + (comIp.Count > 0 ? $" · IP médio {comIp.Average():N0} ({comIp.Count} com equipamento conhecido)" : "")
              + (ItemPowerCatalog.Carregado ? "" : " · carregando a tabela de IP...");
    }
}
