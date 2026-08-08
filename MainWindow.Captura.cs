using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Captura de pacotes: ligar/desligar, pausar, o funil dos eventos do Photon
/// e os diagnosticos.
///
/// Concentra tudo que decide SE e COMO um pacote vira dado na tela — inclusive
/// o aviso de Npcap faltando (AvisarNpcapFaltando), que existe porque sem a
/// opcao "WinPcap API-compatible Mode" o app fechava sozinho na abertura, sem
/// dizer o motivo.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua sendo
/// uma classe so, nada muda de nome e o XAML continua achando os handlers — que
/// e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // Diagnóstico de rede — escuta sem filtro de porta e diz onde o tráfego do jogo
    // realmente está. Serve pra descobrir por que a captura não pega nada com
    // acelerador de rota (ExitLag): ver PacketCaptureService.Diagnose.
    private async void BtnNetDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            MessageBox.Show("Pare a captura antes de rodar o diagnóstico de rede.",
                            "Diagnóstico", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnNetDiag.IsEnabled = false;
        BtnNetDiag.Content = "Escutando 20s...";
        TxtCaptureStatus.Text = "Diagnóstico de rede rodando — deixe o Albion ABERTO e EM PARTIDA por 20s...";
        try
        {
            // Em thread separada: Diagnose() dorme os 20s de escuta e travaria a UI.
            string report = await Task.Run(() => _capture.Diagnose(20));

            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "net_diag.txt"), report);
                // Vai junto com os outros diagnósticos pro Discord (se consentido),
                // pra liderança conseguir ajudar sem pedir print de nada.
                DiagReporter.Report("net_diag", report);
            }
            catch { /* salvar/enviar é best-effort */ }

            TxtCaptureStatus.Text = "Diagnóstico concluído — salvo em net_diag.txt";
            var win = new Window
            {
                Title = "Diagnóstico de rede",
                Width = 760,
                Height = 560,
                Owner = this,
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#1a1a1a")!,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBox
                    {
                        Text = report,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.NoWrap,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        Background = System.Windows.Media.Brushes.Transparent,
                        Foreground = System.Windows.Media.Brushes.White,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(14),
                    }
                }
            };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Falha no diagnóstico: {ex.Message}", "Diagnóstico",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnNetDiag.IsEnabled = true;
            BtnNetDiag.Content = "Diagnóstico de rede";
        }
    }

    // ── Loot Log (Fase 2) ────────────────────────────────────────────────────
    private void BtnAdvancedMode_Click(object sender, RoutedEventArgs e)
    {
        bool advanced = BtnAdvancedMode.IsChecked == true;
        PanelAdvanced.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        ListCleanLoot.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        _advancedVisible = advanced && PanelLoot.Visibility == Visibility.Visible;
    }

    // Recebe todo evento decodificado do pacote e repassa pros trackers — exceto
    // enquanto pausado, onde o pacote continua sendo capturado (Npcap não é desligado)
    // mas nada é contado: loot/dano/fama ficam exatamente como estavam até despausar.
    private void OnCaptureEvent(PhotonEvent evt)
    {
        if (_paused) return;
        OnPhotonEvent(evt);
        PlayerRegistry.HandleEvent(evt);
        _fameTracker.HandleEvent(evt);
        _damageTracker.HandleEvent(evt);
        _killTracker.HandleEvent(evt);
    }

    /// <summary>
    /// Explica que falta o Npcap, em vez de deixar o app fechar.
    ///
    /// O texto diz O QUE instalar, ONDE, e a opção que precisa estar marcada —
    /// "WinPcap API-compatible Mode". Sem ela o Npcap instala mas o wpcap.dll
    /// não aparece, e o sintoma volta idêntico.
    /// </summary>
    private void AvisarNpcapFaltando()
    {
        TxtCaptureStatus.Text = "Npcap não encontrado — a captura precisa dele.";
        BtnCaptureToggle.Content = "Iniciar captura";
        _capturing = false;

        var r = MessageBox.Show(
            "A captura precisa do Npcap, que não está instalado nesta máquina.\n\n" +
            "Baixe em npcap.com e, durante a instalação, MARQUE a opção\n" +
            "\"Install Npcap in WinPcap API-compatible Mode\".\n\n" +
            "Sem essa opção ele instala mas o app continua não achando.\n\n" +
            "Abrir a página de download agora?",
            "Npcap não encontrado",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r != MessageBoxResult.Yes)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download")
                          { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Sem navegador padrão configurado, por exemplo. O texto acima já
            // trouxe o endereço, então não vale insistir.
            System.Diagnostics.Debug.WriteLine($"[npcap] abrir download: {ex.Message}");
        }
    }

    private void BtnCaptureToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_capturing)
        {
            // Sem o Npcap instalado, o SharpPcap estoura DllNotFoundException ao
            // procurar wpcap.dll — e o app FECHAVA. O único rastro era um arquivo
            // de crash que o tester tinha que nos mandar; aconteceu 14 vezes com a
            // mesma pessoa antes de alguém entender o que era.
            //
            // Faltar o Npcap é situação normal (instalação nova, ou ele foi
            // removido por outro programa), não erro do app. Merece instrução, não
            // tela fechando.
            try
            {
                _capturing = _capture.Start();
            }
            catch (DllNotFoundException)
            {
                AvisarNpcapFaltando();
                return;
            }
            catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException)
            {
                // O SharpPcap às vezes embrulha a falha na inicialização do tipo.
                AvisarNpcapFaltando();
                return;
            }
            BtnCaptureToggle.Content = _capturing ? "Parar captura" : "Iniciar captura";
        }
        else
        {
            _capture.Stop();
            _capturing = false;
            _paused = false;
            BtnCaptureToggle.Content = "Iniciar captura";
            BtnPauseToggle.Content = "Pausar";
            TxtCaptureStatus.Text = "Parado";
            SaveSessionToHistory();
            // Com a captura parada, os arquivos de diagnóstico não estão mais sendo
            // escritos — momento seguro pra enviar pro Discord (com consentimento).
            // Assim o tester não precisa fechar e reabrir o app pra mandar os dados.
            DiagReporter.ReportDiagFiles();
        }
        UpdateCaptureIndicator();
    }

    // Pausa só a contagem (loot/dano/fama) — a captura de pacote continua rodando,
    // então despausar não perde nada que tenha chegado nesse meio tempo, só ignora.
    private void BtnPauseToggle_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        BtnPauseToggle.Content = _paused ? "Despausar" : "Pausar";
        UpdateCaptureIndicator();
    }

    // Indicador global de captura na sidebar (visível de qualquer aba)
    private void UpdateCaptureIndicator()
    {
        string trayState;
        if (_paused)
        {
            CaptureDot.Fill = B("#facc15");
            CaptureStateLabel.Text = "Pausado";
            CaptureStateLabel.Foreground = B("#facc15");
            trayState = "Pausado";
        }
        else
        {
            CaptureDot.Fill = _capturing ? B("#22c55e") : B("#57FFFFFF");
            CaptureStateLabel.Text = _capturing ? "Capturando" : "Captura parada";
            CaptureStateLabel.Foreground = _capturing ? B("#22c55e") : B("#8CFFFFFF");
            trayState = _capturing ? "Capturando" : "Captura parada";
        }
        // Único jeito de saber o estado com a janela escondida (fechar pelo "X" só
        // esconde, não para a captura) era abrir a janela de novo — agora aparece
        // passando o mouse no ícone da bandeja, sem precisar reabrir nada.
        TrayIcon.ToolTipText = $"XnoMercy — {trayState}";
    }

    // Marcação manual de momento: usuário clica bem na hora que pega um item.
    // Marca em verde qualquer linha (já na lista ou que ainda vai chegar) dentro
    // de ±1.5s do clique — assim dá pra achar o evento certo sem precisar de
    // filtro por código (que exigia adivinhar quais códigos são "ruído").
    private static readonly TimeSpan MarkWindow = TimeSpan.FromSeconds(4);
    // Lock: a UI adiciona/limpa (BtnMarkMoment/BtnClearMarked) enquanto a thread de
    // captura enumera em OnPhotonEvent — sem proteção, um "Collection was modified"
    // estourava na captura e o catch do OnPacketArrival descartava o pacote INTEIRO
    // em silêncio (perdendo fama/loot daquele pacote).
    private readonly object _markersLock = new();
    private readonly List<DateTime> _markers = new();

    // Confirmado pela própria estrutura repetitiva (mesmo formato em todo pacote,
    // disparando a cada poucos ms) que os códigos 1 e 3 são sincronização de
    // movimento/posição — irrelevantes pra achar o evento de loot. Excluídos só da
    // caixa de marcados (a lista principal continua mostrando tudo, sem filtro).
    private static readonly HashSet<int> MovementNoiseCodes = new() { 1, 3 };

    private void BtnMarkMoment_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        lock (_markersLock) _markers.Add(now);
        foreach (var row in _lootRows)
        {
            if (Math.Abs((row.Timestamp - now).TotalSeconds) <= MarkWindow.TotalSeconds && !row.IsNearMark)
            {
                row.IsNearMark = true;
                if (!MovementNoiseCodes.Contains(row.Code))
                    _markedRows.Insert(0, row);
            }
        }
    }

    private void BtnClearMarked_Click(object sender, RoutedEventArgs e)
    {
        _markedRows.Clear();
        lock (_markersLock) _markers.Clear();
    }

    private void OnPhotonEvent(PhotonEvent evt)
    {
        DiagLogBigEvent(evt);   // calibração: loga eventos com números grandes (fama/prata)
        DiagLogNamedEvent(evt); // calibração: loga eventos com texto (achar o código real de NewMob)
        PartyCalibrationLog.LogEvent(evt);   // calibração: achar quem carrega o roster do grupo

        var now = DateTime.Now;
        bool isGrabbedLoot = evt.EventCode == GameEventCodes.GrabbedLoot;
        bool isLootCandidate = evt.EventCode == GameEventCodes.LootPickup || evt.EventCode == GameEventCodes.LootPickupEquipment;

        // Alimenta o cache de itens recém-vistos (NewSimpleItem/32) SEMPRE, não só com
        // o modo avançado visível — é o que permite ao SelfLootDetector resolver nome/
        // quantidade quando o "pegar tudo" (InventoryMoveGivenItems) chega com os
        // ObjectIds dos itens.
        if (evt.EventCode == GameEventCodes.LootPickup
            && evt.Parameters.TryGetValue(0, out var objIdObj) && evt.Parameters.TryGetValue(1, out var idxObj))
        {
            long objId = ToLong(objIdObj);
            int idx = (int)ToLong(idxObj);
            long qty = evt.Parameters.TryGetValue(2, out var qObj) ? ToLong(qObj) : 1;
            if (objId > 0) SelfLootDetector.RegisterDiscoveredItem(objId, idx, qty);
        }

        // Feed normal = SÓ loot com origem (de quem). O evento 279 (saque de corpo/mob)
        // sempre traz a origem; os pickups do seu inventário (login, troca de zona, baú
        // via code 32) NÃO têm origem, então não poluem o normal — ficam só no avançado.
        // Regra do usuário: "se não preencher 'de quem', não aparece" — EXCETO quando
        // sabemos, pela própria operação que SEU cliente mandou (SelfLootDetector), que
        // foi você quem pegou: nesse caso mostramos mesmo sem "de quem", porque é
        // exatamente o cenário da regressão (servidor não confirma "de quem" de volta
        // pro próprio looter, e o item pego de verdade desaparecia do Loot Log).
        LootFeedRow? feedRow = null;
        bool withinSelfLootWindow;
        lock (_selfLootLock) withinSelfLootWindow = now <= _selfLootWindowUntil;
        if (isGrabbedLoot && TryParseGrabbedLoot(evt, now, out var fr, out var grabbedItemIdx, out var grabbedQty)
            && !fr.IsSilver && (fr.From.Length > 0 || withinSelfLootWindow))
        {
            // Já mostramos esse mesmo pickup direto via SelfLootDetector (ver
            // OnSelfLootDetected) — não duplica quando o servidor confirma depois.
            if (!WasRecentSelfLootDirect(grabbedItemIdx, grabbedQty))
                feedRow = fr;
        }

        // PERFORMANCE: quando o modo avançado não está à vista, não montamos a lista crua
        // (são milhares de eventos/seg — movimento, sync). Só o feed limpo importa. E usamos
        // BeginInvoke (assíncrono) pra NUNCA bloquear a thread de captura — bloquear fazia o
        // buffer do Npcap encher e DESCARTAR pacotes (perdendo fama/loot).
        if (feedRow == null && !_advancedVisible) return;

        string? summary = null;
        bool nearMark = false;
        if (_advancedVisible)
        {
            lock (_markersLock)
                nearMark = _markers.Any(m => Math.Abs((now - m).TotalSeconds) <= MarkWindow.TotalSeconds);
            if (isLootCandidate && TryDescribeLoot(evt, out var lootSummary)) summary = lootSummary;
            else
            {
                summary = string.Join("  ", evt.Parameters.Select(kv => $"[{kv.Key}]={Describe(kv.Value, isLootCandidate)}"));
                if (isLootCandidate) summary = "🎯 POSSÍVEL LOOT — " + summary;
            }
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (feedRow != null)
            {
                _lootFeed.Insert(0, feedRow);
                while (_lootFeed.Count > MaxLootRows) _lootFeed.RemoveAt(_lootFeed.Count - 1);
            }
            if (summary != null)   // só no modo avançado
            {
                var row = new LootEventRow
                {
                    Time = now.ToString("HH:mm:ss.fff"),
                    Timestamp = now,
                    Code = evt.EventCode,
                    Summary = summary,
                    IsNearMark = nearMark,
                };
                _lootRows.Insert(0, row);
                if (isLootCandidate || isGrabbedLoot || (nearMark && !MovementNoiseCodes.Contains(evt.EventCode)))
                    _markedRows.Insert(0, row);
                while (_lootRows.Count > MaxLootRows) _lootRows.RemoveAt(_lootRows.Count - 1);
                while (_markedRows.Count > MaxLootRows) _markedRows.RemoveAt(_markedRows.Count - 1);
            }
        });
    }

    // Diagnóstico de calibração: grava num arquivo os eventos que carregam números
    // grandes (candidatos a fama/prata — a fama total tem 13 dígitos). Assim eu acho
    // o código real de UpdateFame/UpdateMoney lendo dados reais, sem depender de print
    // no momento exato. Arquivo: %LocalAppData%\XnomercyApp\events_diag.txt
    private static int _diagCount;
    private static readonly object _diagLock = new();
    // [Conditional("DEBUG")]: no build Release (produção, o que a guild vai usar), as
    // chamadas a este método são removidas pelo compilador — zero custo. Só roda no
    // build Debug, pra calibração de novos códigos de evento.
    // DEBUG (dev) OU BETA (build de teste mandado pra guild): roda em ambos. Em Release
    // final (produção), some — é removido pelo compilador, custo zero.
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogBigEvent(PhotonEvent evt)
    {
        if (evt.EventCode < 0 || _diagCount >= 800) return;
        bool hasBig = evt.Parameters.Values.Any(v =>
            (v is long l && Math.Abs(l) > 100000) || (v is int i && Math.Abs(i) > 100000));
        if (!hasBig) return;
        lock (_diagLock)
        {
            if (_diagCount >= 800) return;
            _diagCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "events_diag.txt"),
                    $"code={evt.EventCode} {parms}\n");
            }
            catch { }
        }
    }

    private static string DiagVal(object? v) => v switch
    {
        null => "null",
        byte[] b => $"byte[{b.Length}]",
        System.Array a => $"arr[{a.Length}]",
        _ => v.ToString() ?? ""
    };

    // Diagnóstico de calibração: grava eventos que carregam TEXTO de verdade num
    // parâmetro (não número, não byte[]) — é a assinatura de "alguém/algo apareceu
    // com nome" (NewCharacter, e o que suspeito ser o NewMob real, já que o código
    // 123 que mapeamos parece ser na verdade um sync de posição/vida repetitivo, sem
    // nome nenhum — não bate com o padrão de "mob spawnou"). Pula o 29 (NewCharacter,
    // já calibrado) pra focar no que falta achar. Arquivo: %LocalAppData%\XnomercyApp\named_events_diag.txt
    private static int _diagNamedCount;
    private static readonly object _diagNamedLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogNamedEvent(PhotonEvent evt)
    {
        // Pula 29 (NewCharacter) e 30 (Move) — já calibrados e de alto volume; entupiam
        // o log e escondiam códigos raros (ex: o evento de party que ainda procuramos).
        if (evt.EventCode < 0 || evt.EventCode == GameEventCodes.NewCharacter
            || evt.EventCode == GameEventCodes.Move || _diagNamedCount >= 500) return;
        bool hasText = evt.Parameters.Values.Any(v => v is string s && s.Length > 1 && !s.All(c => char.IsDigit(c) || c == ',' || c == '.' || c == '-'));
        if (!hasText) return;
        lock (_diagNamedLock)
        {
            if (_diagNamedCount >= 500) return;
            _diagNamedCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "named_events_diag.txt"),
                    $"code={evt.EventCode} {parms}\n");
            }
            catch { }
        }
    }
}
