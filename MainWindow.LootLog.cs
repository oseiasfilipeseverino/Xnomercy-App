using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Loot Log: leitura dos eventos de loot, filtro da lista e as exportacoes.
///
/// Concentra o que traduz evento cru do Photon em linha da tela
/// (TryParseGrabbedLoot, TryDescribeLoot, Describe) e as duas saidas em
/// arquivo (CSV e o formato do AO Loot Logger).
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // OtherGrabbedLoot (279): [1]=de quem (corpo) [2]=quem pegou [3]=é prata? (bool)
    // [4]=índice do item [5]=quantidade. Os nomes já vêm como texto no evento.
    // itemIdx/qty saem pro chamador conseguir checar duplicata contra o que já foi
    // inserido direto pelo SelfLootDetector (ver WasRecentSelfLootDirect).
    private static bool TryParseGrabbedLoot(PhotonEvent evt, DateTime now, out LootFeedRow row, out int itemIdx, out long amount)
    {
        row = null!;
        itemIdx = -1;
        string from   = evt.Parameters.TryGetValue(1, out var f) ? (f?.ToString() ?? "") : "";
        string looter = evt.Parameters.TryGetValue(2, out var l) ? (l?.ToString() ?? "") : "";
        if (looter.Length == 0 && from.Length == 0) { amount = 0; return false; }

        bool isSilver = evt.Parameters.TryGetValue(3, out var s) && s is bool sb && sb;
        amount = evt.Parameters.TryGetValue(5, out var q) ? ToLong(q) : 1;

        string item;
        string? icon = null;
        string plainName, uniqueName = "";
        if (isSilver)
        {
            // Prata/fama vêm ×10000 no protocolo (ponto fixo do Albion). 106.717.500 → 10.671.
            item = $"{amount / 10000:N0} prata";
            plainName = "Silver";
        }
        else
        {
            itemIdx = evt.Parameters.TryGetValue(4, out var ii) ? (int)ToLong(ii) : -1;
            string name = ItemCatalog.GetName(itemIdx) ?? $"item {itemIdx}";
            item = $"{amount}x {name}";
            icon = IconUrl(itemIdx);
            plainName = name;
            uniqueName = ItemCatalog.GetUniqueName(itemIdx) ?? "";
        }
        // CALIBRADO com dados reais (várias sessões de captura, arquivos named_events):
        // o campo "de quem" traz SEMPRE a tag interna com prefixo "@MOB_" quando o loot
        // vem de monstro (@MOB_MORGANA_..., @MOB_UNDEAD_..., @MOB_T4_MOB_TN_AVALON_...),
        // e o nick puro quando vem de jogador (Tortuga780, CherryB00m, koynoy...).
        //
        // Antes checava `from.Contains('_')`, que era provisório e furado: pegava a tag
        // de mob só por acidente (ela tem "_"), mas marcava como MOB qualquer JOGADOR
        // com "_" no nick — escondendo o loot dele do feed limpo (filtro "Esconder saque
        // de monstro"). Mesmo prefixo já usado no PlayerRegistry pros códigos 74/166.
        // Corpo sem "de quem" continua tratado como mob (não há nada pra atribuir).
        bool isMob = !isSilver && (from.Length == 0
                                   || from.StartsWith("@MOB_", StringComparison.OrdinalIgnoreCase));
        row = new LootFeedRow
        {
            Time = now.ToString("HH:mm:ss"),
            Timestamp = now,
            Looter = looter,
            Item = item,
            From = isSilver ? "" : (isMob ? "MOB" : from),
            ItemIcon = icon,
            IsSilver = isSilver,
            IsMob = isMob,
            ItemUniqueName = uniqueName,
            ItemPlainName = plainName,
            ItemIndex = itemIdx,
            Valor = !isSilver && itemIdx >= 0 && ItemValues.Valor(itemIdx) is long unit ? unit * amount : null,
            Quantity = isSilver ? amount / 10000 : amount,
            LooterGuild = PlayerRegistry.GuildOfName(looter),
            FromGuild = isMob ? "" : PlayerRegistry.GuildOfName(from),
            DoGrupo = PlayerRegistry.IsInParty(looter),
        };
        return true;
    }

    private static long ToLong(object? v) => (long)(PhotonParam.ToDouble(v) ?? 0);

    // Monta a URL do render oficial pra mostrar a miniatura do item. O unique_name
    // já inclui tier e encantamento (ex: T5_2H_AXE@2), então o ícone vem correto.
    private static string? IconUrl(int itemIndex)
    {
        if (itemIndex < 0) return null;
        var uniq = ItemCatalog.GetUniqueName(itemIndex);
        return string.IsNullOrEmpty(uniq) ? null : $"https://render.albiononline.com/v1/item/{uniq}.png?size=64";
    }

    // Filtro do feed limpo — reavaliado quando os checkboxes mudam.
    private bool LootRowVisible(object obj)
    {
        if (obj is not LootFeedRow r) return true;
        if (r.IsMob && ChkHideMob.IsChecked == true) return false;       // esconde mob se marcado
        // "Só minha guild": opcional — mostra só pickups feitos por gente da sua guild
        // (resolvida em background via PlayerRegistry.OwnGuild). Padrão é desmarcado
        // (mostra todo mundo por perto, igual sempre foi).
        // "Você" é o seu loot visto pela sua própria ação (SelfLootDetector) — antes o
        // filtro o escondia, porque "Você" não é o nome do personagem (18/10/2026).
        if (ChkGuildOnlyLoot.IsChecked == true && r.Looter != "Você"
            && !string.Equals(r.Looter, PlayerRegistry.SelfName, StringComparison.OrdinalIgnoreCase)
            && !PlayerRegistry.MesmaGuild(r.Looter))
            return false;
        // "Só meu grupo": idem, mas usando o rastreamento de grupo (229/240/182).
        // Com o roster vazio isto esconde TUDO. Ver AtualizarRotuloDoGrupo: o
        // rótulo da caixa avisa quando não há ninguém detectado, senão a tela em
        // branco é indistinguível de "não caiu loot".
        if (ChkPartyOnlyLoot.IsChecked == true && !r.DoGrupo && !PlayerRegistry.IsInParty(r.Looter)) return false;
        return true;
    }

    private void LootFilter_Changed(object sender, RoutedEventArgs e)
    {
        _lootFeedView?.Refresh();
        AtualizarRotuloDoGrupo();
        AtualizarTotalDoLoot();
    }

    /// <summary>Preço novo chegou: completa as linhas que ainda estavam sem valor.</summary>
    private void PreencherValoresDoLoot()
    {
        foreach (var r in _lootFeed)
            if (r.Valor == null && r.ItemIndex >= 0 && ItemValues.Valor(r.ItemIndex) is long unit)
                r.Valor = unit * r.Quantity;
    }

    /// <summary>Soma do que está À VISTA (respeita os filtros): "o grupo pegou X".</summary>
    private void AtualizarTotalDoLoot()
    {
        long total = 0; int com = 0, sem = 0;
        if (_lootFeedView != null)
            foreach (var o in _lootFeedView)
                if (o is LootFeedRow r && !r.IsSilver)
                {
                    if (r.Valor is long v) { total += v; com++; } else sem++;
                }
        string texto = com == 0 && sem == 0 ? ""
            : $"Valor estimado à vista: {total:N0} prata" + (sem > 0 ? $" ({sem} item(ns) ainda sem preço)" : "");
        if (TxtLootTotal.Text != texto) TxtLootTotal.Text = texto;
    }

    /// <summary>Mostra no rótulo das caixas quantos do grupo o app conhece.</summary>
    /// <remarks>
    /// Em 14/08 o Loot Log ficou em branco com "Só meu grupo" marcado e a captura
    /// funcionando (8.508 eventos decodificados). Não havia como saber, olhando o
    /// app, se não tinha caído loot ou se o roster estava vazio — o filtro
    /// escondia tudo sem dizer nada.
    ///
    /// E roster vazio é o caso NORMAL quando a captura começa com o grupo já
    /// formado: o servidor anuncia companheiro na ENTRADA, e quem já estava lá
    /// não é anunciado de novo. Este rótulo transforma um app "quebrado" numa
    /// instrução.
    /// </remarks>
    private int _versaoGuildasVista = -1;
    private string? _grupoVisto;

    /// <summary>
    /// "Só meu grupo": quem entra no grupo depois aparece sem precisar desmarcar e marcar
    /// a caixa (o filtro só era refeito no clique — mesmo defeito do "Só minha guild").
    /// </summary>
    private void RefiltrarSeOGrupoMudou()
    {
        bool loot = ChkPartyOnlyLoot.IsChecked == true, dano = ChkPartyOnlyDamage.IsChecked == true;
        if (!loot && !dano) { _grupoVisto = null; return; }
        string agora = PlayerRegistry.AssinaturaDoGrupo();
        if (agora == _grupoVisto) return;
        _grupoVisto = agora;
        if (loot) { _lootFeedView?.Refresh(); AtualizarTotalDoLoot(); }
        if (dano) _damageDirty = true;   // o medidor só redesenhava no próximo golpe
    }

    /// <summary>
    /// "Só minha guild": mostra qual guild o app está usando, avisa quando ainda não
    /// sabe (a lista ficava vazia sem explicação) e refiltra quando chega guild nova.
    /// </summary>
    private void AtualizarFiltroDeGuild()
    {
        string minha = PlayerRegistry.OwnGuild;
        object alvo = minha.Length > 0 ? $"Só minha guild ({minha})" : "Só minha guild — guild ainda não detectada";
        if (!Equals(ChkGuildOnlyLoot.Content, alvo)) ChkGuildOnlyLoot.Content = alvo;
        if (!Equals(ChkGuildOnlyDamage.Content, alvo)) ChkGuildOnlyDamage.Content = alvo;
        object? dica = minha.Length > 0 ? null
            : "O app ainda não sabe a sua guild: ela vem do jogo quando você entra num mapa. "
              + "Troque de mapa (ou entre/saia de uma casa) com a captura ligada.";
        if (!Equals(ChkGuildOnlyLoot.ToolTip, dica)) { ChkGuildOnlyLoot.ToolTip = dica; ChkGuildOnlyDamage.ToolTip = dica; }

        int v = PlayerRegistry.VersaoGuildas;
        if (v == _versaoGuildasVista) return;
        _versaoGuildasVista = v;
        if (ChkGuildOnlyLoot.IsChecked == true) { _lootFeedView?.Refresh(); AtualizarTotalDoLoot(); }
        if (ChkGuildOnlyDamage.IsChecked == true) _damageDirty = true;
    }

    private void AtualizarRotuloDoGrupo()
    {
        int n = PlayerRegistry.MembrosNoGrupo();
        object alvo = "Só meu grupo" + (n > 0 ? $" ({n})" : " — ninguém detectado");

        if (!Equals(ChkPartyOnlyLoot.Content, alvo)) ChkPartyOnlyLoot.Content = alvo;
        if (!Equals(ChkPartyOnlyDamage.Content, alvo)) ChkPartyOnlyDamage.Content = alvo;

        // Só explica quando o filtro está LIGADO e não há ninguém: aí a lista
        // vazia é consequência do filtro, não do jogo.
        bool avisar = n == 0 && (ChkPartyOnlyLoot.IsChecked == true
                                 || ChkPartyOnlyDamage.IsChecked == true);
        string dica = avisar
            ? "O filtro de grupo está ligado e o app não detectou ninguém — por isso a "
              + "lista está vazia.\n\nO jogo só anuncia companheiro na ENTRADA do grupo: "
              + "ligue a captura ANTES de formar o grupo, ou desmarque esta caixa."
            : "";
        object? tt = string.IsNullOrEmpty(dica) ? null : dica;
        if (!Equals(ChkPartyOnlyLoot.ToolTip, tt))
        {
            ChkPartyOnlyLoot.ToolTip = tt;
            ChkPartyOnlyDamage.ToolTip = tt;
        }
    }

    // Limpa o feed limpo e o modo avançado (eventos crus + marcados) — não para a
    // captura, só esvazia o que já foi mostrado, igual o "Reiniciar sessão" do Medidor.
    private void BtnLootReset_Click(object sender, RoutedEventArgs e)
    {
        _lootFeed.Clear();
        _lootRows.Clear();
        _markedRows.Clear();
    }

    // Exporta no formato do AO Loot Logger (matheussampaio/ao-loot-logger), pra abrir
    // no Loot Logger Viewer. Cabeçalho e ordem das colunas copiados do loot-logger.js
    // do projeto — o Viewer casa as colunas por nome, então o nosso formato próprio
    // (cabeçalho em português, 4 colunas) era recusado com "No matches for <arquivo>".
    //
    // Diferenças assumidas e por quê:
    //  - aliança fica vazia: o app não rastreia aliança, só guild. O Viewer trata
    //    campo vazio normalmente (o próprio logger grava '' quando não sabe).
    //  - prata é ignorada: o logger original também descarta (`if (isSilver) return`),
    //    então incluir criaria linha que o Viewer não espera.
    //  - loot de mob é ignorado: "looted_from" precisa ser um JOGADOR; a tag interna
    //    (@MOB_...) não é nome de conta e poluiria o rateio no Viewer.
    private void BtnExportLootLogger_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            // O logger original nomeia assim; o Viewer aceita .txt e .csv.
            FileName = $"loot-events-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt",
            Filter = "Loot Logger (*.txt)|*.txt|CSV (*.csv)|*.csv",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("timestamp_utc;looted_by__alliance;looted_by__guild;looted_by__name;")
          .Append("item_id;item_name;quantity;")
          .AppendLine("looted_from__alliance;looted_from__guild;looted_from__name");

        int exportadas = 0;
        foreach (var r in _lootFeed)
        {
            if (!LootRowVisible(r)) continue;                     // exporta o que está na tela ("Só minha guild" etc.)
            if (r.IsSilver || r.IsMob) continue;                  // ver comentário acima
            if (r.ItemUniqueName.Length == 0) continue;            // sem item_id o Viewer não resolve
            if (r.Looter.Length == 0 || r.From.Length == 0) continue;
            sb.Append(r.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append(';')
              .Append(';')                                         // looted_by__alliance
              .Append(Csv(GuildAtual(r.Looter, r.LooterGuild))).Append(';')
              .Append(Csv(PlayerRegistry.NomeParaFora(r.Looter))).Append(';')
              .Append(Csv(r.ItemUniqueName)).Append(';')
              .Append(Csv(r.ItemPlainName)).Append(';')
              .Append(r.Quantity).Append(';')
              .Append(';')                                         // looted_from__alliance
              .Append(Csv(GuildAtual(r.From, r.FromGuild))).Append(';')
              .AppendLine(Csv(r.From));
            exportadas++;
        }

        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(false));
            TxtCaptureStatus.Text = exportadas > 0
                ? $"Exportado pro Loot Logger: {exportadas} linha(s) — {System.IO.Path.GetFileName(dlg.FileName)}"
                : "Nada pra exportar: o Loot Logger só aceita loot de JOGADOR (prata e saque de mob ficam fora).";
        }
        catch (Exception ex)
        {
            TxtCaptureStatus.Text = $"Falha ao exportar: {ex.Message}";
        }
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"loot_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog() != true) return;
        string csv = MontarCsvLoot(out int linhas);
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, csv, System.Text.Encoding.UTF8);
            TxtCaptureStatus.Text = $"Exportado: {linhas} linha(s) — {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            // Antes, falha aqui (arquivo aberto no Excel, sem permissão de escrita)
            // não dava nenhum feedback — parecia que o botão "Exportar CSV" não fazia nada.
            TxtCaptureStatus.Text = $"Falha ao exportar: {ex.Message}";
        }
    }

    /// <summary>CSV do Loot Log com os mesmos filtros da tela ("Só minha guild", "Só meu grupo", mobs).</summary>
    private string MontarCsvLoot(out int linhas)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Hora;Quem pegou;Guild;Item;De quem;Valor estimado (prata)");
        linhas = 0;
        foreach (var r in _lootFeed)
        {
            if (!LootRowVisible(r)) continue;
            sb.AppendLine($"{r.Time};{Csv(PlayerRegistry.NomeParaFora(r.Looter))};{Csv(GuildAtual(r.Looter, r.LooterGuild))};{Csv(r.Item)};{Csv(r.From)};{(r.Valor is long v ? v.ToString() : "")}");
            linhas++;
        }
        return sb.ToString();
    }

    // A guild de quem pegou pode chegar DEPOIS do loot (o jogador entra na tela, ou
    // só aparece após a troca de mapa): na hora de exportar, usa a mais nova que souber.
    private static string GuildAtual(string nome, string guildDaLinha)
    {
        string g = nome == "Você" ? PlayerRegistry.OwnGuild : PlayerRegistry.GuildOfName(nome);
        return string.IsNullOrWhiteSpace(g) ? guildDaLinha : g;
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') ? '"' + s.Replace("\"", "\"\"") + '"' : s;

    // NewSimpleItem (GameEventCodes.LootPickup): [0]=ObjectId [1]=índice do item
    // [2]=quantidade [4]=valor estimado [7]=durabilidade.
    private static bool TryDescribeLoot(PhotonEvent evt, out string summary)
        => TryDescribeLoot(evt, out summary, out _, out _);

    private static bool TryDescribeLoot(PhotonEvent evt, out string summary, out string itemName, out string qty)
    {
        summary = "";
        itemName = "";
        qty = "";
        if (!evt.Parameters.TryGetValue(1, out var idxObj)) return false;

        int? itemIndex = PhotonParam.ToLong(idxObj) is long il ? (int)il : null;
        if (itemIndex is null) return false;

        itemName = ItemCatalog.GetName(itemIndex.Value) ?? $"item desconhecido (índice {itemIndex})";
        qty = evt.Parameters.TryGetValue(2, out var q) ? Describe(q) : "?";
        summary = $"🎯 LOOT: {qty}x {itemName}  [unique_name={ItemCatalog.GetUniqueName(itemIndex.Value) ?? "?"}]";
        return true;
    }

    // Resolver nome de item em TODO número causava muito falso positivo (qualquer
    // delta de posição/movimento pode coincidir com um índice de item real, já que
    // tem mais de 10 mil itens). Agora só tenta resolver nome quando o evento já é
    // candidato a loot (Code == GameEventCodes.LootPickup) — bem mais específico.
    private static string Describe(object? value, bool tryResolveItemName = false)
    {
        string text = value switch
        {
            null => "null",
            byte[] bytes => $"byte[{bytes.Length}]",
            object?[] arr => $"array[{arr.Length}]",
            System.Collections.IDictionary => "dict{...}",
            Protocol16Deserializer.UnknownValue u => $"?type{u.TypeCode}",
            _ => value.ToString() ?? "",
        };

        if (tryResolveItemName)
        {
            int? maybeIndex = PhotonParam.ToLong(value) is long ml && ml >= int.MinValue && ml <= int.MaxValue
                ? (int)ml : null;
            if (maybeIndex is int idx)
            {
                var name = ItemCatalog.GetName(idx);
                if (name != null) text += $" 🎯({name})";
            }
        }
        return text;
    }
}
