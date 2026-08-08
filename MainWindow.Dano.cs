using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Medidor de Dano: painel principal, quebra por habilidade e o copiar.
///
/// A selecao do ListView e fragil aqui — ver o comentario do
/// DamageRowDisplay sobre por que as linhas sao atualizadas no lugar em
/// vez de recriadas.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // ── Medidor de Dano (Fase 4) ────────────────────────────────────────────
    private void RefreshDamagePanel()
    {
        bool showUnnamed = ChkShowUnnamed.IsChecked == true;
        bool guildOnly = ChkGuildOnlyDamage.IsChecked == true;
        bool partyOnly = ChkPartyOnlyDamage.IsChecked == true;
        // DamageMeterTracker já agrupa por nome resolvido no momento do hit (ver
        // comentário na classe) — cada entrada aqui já é um jogador único e estável,
        // sem precisar re-agrupar por ObjectId (que o Albion recicla por zona/sala).
        var entries = _damageTracker.Snapshot()
            // "Mostrar sem nome": por padrão (desmarcado) já filtra os #número (mob não
            // detectado, invocação, etc.), deixando só jogadores resolvidos. Marcando,
            // revela os #número de volta — é o oposto do que era antes ("ocultar").
            //
            // Esse filtro também é a rede de segurança pro caso de um mob NUNCA disparar
            // MobSpeak(74)/MobKilled(166) durante a sessão observada (ex: você chega numa
            // luta que já estava rolando e o mob morre por outro grupo antes de falar ou
            // morrer perto de você) — PlayerRegistry.IsMob só sabe filtrar mob confirmado
            // por uma dessas duas fontes. Mobs nunca aparecem em NewCharacter(29)/Move(30)
            // (só jogadores), então o nome nunca resolve e fica com "#id" — que já cai
            // fora da lista por padrão aqui, mesmo sem confirmação de mob. Equivalente
            // ao filtro "@MOB_" do Loot Log, só que via ausência de nome em vez de tag.
            .Where(x => showUnnamed || !x.Name.StartsWith('#'))
            // "Só minha guild": opcional, pra quando quiser ver só o desempenho da sua
            // guild — o padrão continua mostrando todo mundo por perto. A guild do
            // próprio usuário é resolvida em background (PlayerRegistry.OwnGuild); se
            // ainda não resolveu, só você mesmo passa (evita falso positivo comparando
            // "" com guild vazia de quem não tem guild).
            .Where(x => !guildOnly || x.Name == "Você"
                        || (PlayerRegistry.OwnGuild.Length > 0
                            && PlayerRegistry.GuildOfName(x.Name) == PlayerRegistry.OwnGuild))
            // "Só meu grupo": rastreado via eventos de grupo calibrados (entrada por
            // 229/240, saída por 182, com expiração de 60s como rede de segurança).
            // x.Name == "Você" precisa passar explicitamente: a linha do proprio
            // jogador se chama literalmente "Você", nao o nome do personagem, entao
            // IsInParty("Você") comparava com SelfName e dava falso — o filtro
            // escondia ATE VOCE e a lista ficava completamente vazia. O filtro de
            // guild logo acima ja tratava isso; este ficou pra tras quando o painel
            // passou a ser indexado por nome (v1.0.39).
            .Where(x => !partyOnly || x.Name == "Você" || PlayerRegistry.IsInParty(x.Name))
            .OrderByDescending(x => x.Damage)
            .ToList();

        // Com o filtro ligado, a % passa a ser entre os jogadores mostrados (faz mais
        // sentido pra comparar a galera do grupo do que diluir no dano do que foi escondido).
        long total = entries.Sum(x => x.Damage);

        // Atualiza NO LUGAR em vez de limpar e recriar: a coleção mantém a mesma
        // instância de linha por jogador, então a seleção do ListView (e o painel
        // "Dano por habilidade" que depende dela) sobrevive aos ~3 refreshes por
        // segundo durante o combate. Ver comentário em DamageRowDisplay.
        var existing = new Dictionary<string, DamageRowDisplay>(_damageRows.Count);
        foreach (var r in _damageRows) existing[r.Player] = r;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            string? icon = null;
            if (e.Name == "Você")
            {
                // Você nunca tem PlayerInfo (o jogo não manda NewCharacter de você
                // mesmo) — a arma vem de uma consulta separada à API pública do Albion.
                icon = PlayerRegistry.OwnWeaponIconUrl;
            }
            else
            {
                var info = PlayerRegistry.Get(e.LastObjectId);
                if (info != null && info.MainHand >= 0)
                {
                    var uniq = ItemCatalog.GetUniqueName(info.MainHand);
                    if (!string.IsNullOrEmpty(uniq))
                        icon = $"https://render.albiononline.com/v1/item/{uniq}.png?size=64";
                }
            }
            double pct = total > 0 ? e.Damage * 100.0 / total : 0;

            if (!existing.TryGetValue(e.Name, out var row))
            {
                row = new DamageRowDisplay { Player = e.Name };
                _damageRows.Insert(Math.Min(i, _damageRows.Count), row);
            }
            row.Damage = e.Damage;
            row.DamagePct = pct.ToString("0.0") + "%";
            row.DamagePctValue = pct;
            row.Healing = e.Healing;
            row.WeaponIcon = icon;

            // Reordena só quando a posição realmente mudou (Move preserva a seleção,
            // diferente de remover+inserir).
            int current = _damageRows.IndexOf(row);
            if (current != i && i < _damageRows.Count) _damageRows.Move(current, i);
        }

        // Tira quem saiu do filtro/ranking (do fim pro começo, pra não bagunçar índices).
        var keep = new HashSet<string>(entries.Select(x => x.Name));
        for (int i = _damageRows.Count - 1; i >= 0; i--)
            if (!keep.Contains(_damageRows[i].Player)) _damageRows.RemoveAt(i);

        // Mantém a quebra por habilidade em sincronia com quem está selecionado — sem
        // isso ela ficaria parada na última foto enquanto o resto da tela continua
        // atualizando ao vivo durante o combate.
        if (ListDamage.SelectedItem is DamageRowDisplay selected)
            RefreshDamageBySkillPanel(selected.Player);
    }

    private void BtnDamageReset_Click(object sender, RoutedEventArgs e) => _damageTracker.Reset();

    // Quebra de dano por habilidade (estilo Albion Battle Analytics) do jogador
    // selecionado na lista principal — ver DamageMeterTracker.SnapshotBySpell e
    // SpellCatalog (resolve CausingSpellIndex pro nome real da skill).
    private void ListDamage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListDamage.SelectedItem is DamageRowDisplay row)
        {
            TxtDamageBySkillTitle.Text = $"Dano por habilidade — {row.Player}";
            RefreshDamageBySkillPanel(row.Player);
            PanelDamageBySkill.Visibility = Visibility.Visible;
        }
        else
        {
            _damageBySkillRows.Clear();
            PanelDamageBySkill.Visibility = Visibility.Collapsed;
        }
    }

    // Clicar de novo na linha já selecionada fecha o painel (em vez de continuar
    // selecionada sem disparar SelectionChanged, que é o comportamento padrão do
    // ListView e deixaria o painel aberto pra sempre depois do 1º clique).
    private void ListDamageItem_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true })
        {
            ListDamage.SelectedItem = null;
            e.Handled = true;
        }
    }

    private void RefreshDamageBySkillPanel(string playerName)
    {
        var bySpell = _damageTracker.SnapshotBySpell(playerName);
        long total = bySpell.Sum(x => x.Damage);
        _damageBySkillRows.Clear();
        foreach (var s in bySpell)
        {
            double pct = total > 0 ? s.Damage * 100.0 / total : 0;
            _damageBySkillRows.Add(new DamageBySpellRowDisplay
            {
                Name = s.Name,
                Damage = s.Damage,
                DamagePct = pct.ToString("0.0") + "%",
                DamagePctValue = pct,
            });
        }
    }

    // Liga/desliga "ocultar sem nome" — só redesenha (o filtro é aplicado no refresh).
    private void DamageFilter_Changed(object sender, RoutedEventArgs e) => RefreshDamagePanel();

    // Copia o ranking de dano pro clipboard, em texto — pra colar no Discord da guild.
    private async void BtnDamageCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_damageRows.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Medidor de Dano — XnoMercy");
        int pos = 1;
        foreach (var r in _damageRows)
            sb.AppendLine($"{pos++}. {r.Player} — {r.Damage:N0} ({r.DamagePct})" +
                          (r.Healing > 0 ? $" | cura {r.Healing:N0}" : ""));
        // Antes, falha (clipboard ocupado por outro app) era engolida em silêncio — o
        // usuário clicava e nada visível acontecia, parecia que o botão não funcionou.
        var original = BtnDamageCopy.Content;
        try { Clipboard.SetText(sb.ToString()); BtnDamageCopy.Content = "Copiado!"; }
        catch { BtnDamageCopy.Content = "Falha ao copiar — tente de novo"; }
        await Task.Delay(2000);
        BtnDamageCopy.Content = original;
    }
}
