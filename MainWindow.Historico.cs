using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Historico de sessao: salvar ao parar a captura, listar, limpar e exportar.
///
/// "Sessao" aqui e o periodo do FameSilverTracker, nao deste start/stop —
/// e o mesmo recorte que a aba Fama & Prata mostra.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // Resumo da sessão (fama/prata/dano/itens) salvo localmente ao parar a captura —
    // "sessão" aqui é o mesmo período do FameSilverTracker (desde o último "Reiniciar
    // sessão" em Fama & Prata, não desde este start/stop específico), pra bater com o
    // que a aba Fama & Prata já mostra como "Sessão desde HH:mm:ss".
    private void SaveSessionToHistory()
    {
        // DamageMeterTracker já resolve e agrupa por nome no momento do hit — a
        // entrada "Você" soma tudo que já foi atribuído a você durante a sessão.
        long selfDamage = _damageTracker.Snapshot()
            .FirstOrDefault(d => d.Name == "Você")?.Damage ?? 0;
        // Só o SEU loot — o feed mostra pickups de todo mundo por perto, e contar
        // tudo inflava o número da sessão com itens que outros jogadores pegaram.
        // "Você" = linha inserida direto pelo SelfLootDetector; SelfName = seu nome
        // real quando o evento 279 do servidor confirma o pickup.
        int lootItems = _lootFeed.Count(x => !x.IsSilver &&
            (x.Looter == "Você" || (PlayerRegistry.SelfName is string sn && x.Looter == sn)));

        // Sem isso, qualquer toque acidental em "Iniciar/Parar captura" gerava uma
        // linha vazia no histórico.
        if (_fameTracker.TotalFame == 0 && _fameTracker.TotalYellowFame == 0 &&
            _fameTracker.TotalSilver == 0 && selfDamage == 0 && lootItems == 0)
            return;

        SessionHistoryStore.Append(new SessionHistoryEntry
        {
            StartTime = _fameTracker.SessionStart,
            EndTime = DateTime.Now,
            Fame = _fameTracker.TotalFame,
            YellowFame = _fameTracker.TotalYellowFame,
            Silver = _fameTracker.TotalSilver,
            Damage = selfDamage,
            LootItems = lootItems,
        });
        RefreshSessionHistory();
    }

    private void RefreshSessionHistory()
    {
        _sessionRows.Clear();
        foreach (var s in SessionHistoryStore.Load())
        {
            var duration = s.EndTime - s.StartTime;
            _sessionRows.Add(new SessionHistoryRowDisplay
            {
                Period = $"{s.StartTime:dd/MM HH:mm} — {s.EndTime:HH:mm}",
                Duration = duration.TotalHours >= 1
                    ? $"{(int)duration.TotalHours}h{duration.Minutes:D2}m"
                    : $"{duration.Minutes}m{duration.Seconds:D2}s",
                Fame = s.Fame.ToString("N0"),
                YellowFame = s.YellowFame.ToString("N0"),
                Silver = s.Silver.ToString("N0"),
                Damage = s.Damage.ToString("N0"),
                LootItems = s.LootItems.ToString("N0"),
            });
        }
    }

    private void BtnClearSessionHistory_Click(object sender, RoutedEventArgs e)
    {
        SessionHistoryStore.Clear();
        RefreshSessionHistory();
    }

    // Mesmo padrão do Exportar CSV do Loot Log: ; como separador (Excel PT-BR) e
    // feedback visível de sucesso/falha (arquivo aberto no Excel, sem permissão...).
    private void BtnExportSessionsCsv_Click(object sender, RoutedEventArgs e)
    {
        var sessions = SessionHistoryStore.Load();
        if (sessions.Count == 0)
        {
            TxtSessionsStatus.Text = "Nada pra exportar ainda.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"sessoes_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Início;Fim;Duração (min);Fama;Fama Amarela;Prata;Dano;Itens de Loot");
        foreach (var s in sessions)
            sb.AppendLine($"{s.StartTime:dd/MM/yyyy HH:mm};{s.EndTime:dd/MM/yyyy HH:mm};" +
                          $"{(int)(s.EndTime - s.StartTime).TotalMinutes};" +
                          $"{s.Fame};{s.YellowFame};{s.Silver};{s.Damage};{s.LootItems}");
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            TxtSessionsStatus.Text = $"Exportado: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            TxtSessionsStatus.Text = $"Falha ao exportar: {ex.Message}";
        }
    }
}
