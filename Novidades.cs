using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XnomercyApp;

/// <summary>
/// "O que mudou" (18/10/2026): na primeira abertura depois de uma atualização, o app
/// mostra as novidades das versões que a pessoa ainda não viu — assim a guild não
/// depende só do anúncio no Discord. A última versão vista fica em versao_vista.txt.
///
/// AO PUBLICAR UMA VERSÃO: acrescente a entrada dela em Notas (a mais nova em cima).
/// O teste harness_novidades reprova a release se a versão do projeto não tiver nota.
/// </summary>
public static class Novidades
{
    public static readonly (string Versao, string[] Itens)[] Notas =
    {
        ("1.1.4", new[]
        {
            "Usa ExitLag, VPN ou acelerador? Na janela \"Saúde\" tem o \"Diagnóstico de rede\": rode com o acelerador ligado e mande o resultado pra liderança, pra gente fazer o app funcionar junto com ele.",
        }),
        ("1.1.3", new[]
        {
            "Registro de energia da guild conferido com o jogo de verdade: só a aba de energia é enviada pro site (a de prata fica de fora).",
        }),
        ("1.1.2", new[]
        {
            "O Albion teve um patch que mudou a numeração dos eventos. O app já se ajusta sozinho e agora lembra o ajuste entre uma sessão e outra.",
            "Aviso na tela quando quase nada do jogo chega: ExitLag, VPN (TunnelBear) ou outro acelerador ligados impedem o app de ler o jogo.",
            "Fama e prata mais resistentes a patch. Mapas também pela troca de mapa.",
        }),
        ("1.1.1", new[]
        {
            "Menu mais enxuto: Pausar e Overlay lado a lado, Saúde e Exportar lado a lado (passe o mouse para ver o que cada um faz).",
        }),
        ("1.1.0", new[]
        {
            "O app não manda mais nada sozinho pro Discord. Se a liderança pedir, use \"Exportar sessão p/ análise\" no menu e mande o arquivo.",
            "Saiu o aviso de envio automático de dados.",
            "Mobs mortos: derrubar um jogador não conta mais como mob.",
            "Corrigidos com dados reais do jogo: pontos de facção, Might & Favor, grupo desfeito e abas do banco.",
        }),
        ("1.0.70", new[]
        {
            "Botão \"Exportar sessão p/ análise\" no menu: salva o formato dos eventos do jogo (sem nomes nem chat) pra liderança conferir o que o app reconhece.",
            "Esta janela: depois de cada atualização, o app mostra o que mudou.",
        }),
        ("1.0.69", new[]
        {
            "Corrigido: o app ficava preso em \"O site não respondeu\" e não entrava.",
            "Corrigido: o botão de enviar o registro de energia pro site.",
        }),
        ("1.0.68", new[]
        {
            "Medidor de Dano: \"Salvar luta\" e \"Lutas salvas\" (Reiniciar sessão também guarda a luta).",
            "Baús e Banco: o que tem em cada aba do banco que você abriu, com o valor.",
            "Loot Log: botão \"Minhas perdas\" — o que levaram do seu corpo, quem levou e quanto valia.",
            "Coleta conta pesca. Fama & Prata mostra pontos de facção e Might & Favor.",
            "Overlay e histórico de sessões com DPS e valor do loot.",
            "O app se ajusta sozinho se um patch mudar a numeração dos eventos.",
        }),
        ("1.0.67", new[]
        {
            "Valor do loot em cada item e o total no Loot Log.",
            "Mapas e Dungeons: cada mapa e cada run de dungeon com fama, prata e loot.",
            "Grupo: arma e IP de cada membro.",
            "Medidor de Dano mais preciso: DPS, dano recebido e cura efetiva.",
            "Prata real (já sem as taxas). Saúde da captura no menu.",
            "Captura sem Npcap (não funciona com ExitLag).",
        }),
    };

    private static string Arquivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "versao_vista.txt");

    public static string VersaoAtual =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Notas das versões mais novas que "vista" e no máximo a atual, da mais nova pra mais velha.</summary>
    public static List<(string Versao, string[] Itens)> Pendentes(string? vista, string atual)
    {
        Version? v = Version.TryParse(vista, out var x) ? x : null;
        var a = Version.TryParse(atual, out var y) ? y : new Version(0, 0, 0);
        return Notas.Where(n => Version.TryParse(n.Versao, out var nv) && nv <= a && (v == null || nv > v))
                    .Take(v == null ? 1 : int.MaxValue)   // sem registro (instalação nova): só a atual
                    .ToList();
    }

    /// <summary>Mostra a janela se houver novidade não vista. Grava a versão atual como vista.</summary>
    public static void MostrarSeHouver(Window dono)
    {
        string? vista = null;
        try { if (File.Exists(Arquivo)) vista = File.ReadAllText(Arquivo).Trim(); } catch { }
        string atual = VersaoAtual;
        if (vista == atual) return;
        var pend = Pendentes(vista, atual);
        try { Directory.CreateDirectory(Path.GetDirectoryName(Arquivo)!); File.WriteAllText(Arquivo, atual); } catch { }
        if (pend.Count == 0) return;

        var B = new System.Windows.Media.BrushConverter();
        var lista = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        lista.Children.Add(new TextBlock
        {
            Text = "O que mudou", FontSize = 26, FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)B.ConvertFrom("#c9a227")!, Margin = new Thickness(0, 0, 0, 12),
        });
        foreach (var (versao, itens) in pend)
        {
            lista.Children.Add(new TextBlock
            {
                Text = $"Versão {versao}", FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)B.ConvertFrom("#DEFFFFFF")!, Margin = new Thickness(0, 8, 0, 4),
            });
            foreach (var i in itens)
                lista.Children.Add(new TextBlock
                {
                    Text = "•  " + i, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(6, 2, 0, 2),
                    Foreground = (System.Windows.Media.Brush)B.ConvertFrom("#B3FFFFFF")!,
                });
        }
        var ok = new Button
        {
            Content = "Entendi", Padding = new Thickness(22, 7, 22, 7), Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(0),
            Background = (System.Windows.Media.Brush)B.ConvertFrom("#c9a227")!, Foreground = System.Windows.Media.Brushes.Black,
            FontWeight = FontWeights.Bold,
        };
        lista.Children.Add(ok);
        var janela = new Window
        {
            Title = "O que mudou no app", Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 700,
            Owner = dono, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)B.ConvertFrom("#1a1a1a")!,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = lista },
        };
        ok.Click += (_, _) => janela.Close();
        janela.ShowDialog();
    }
}
