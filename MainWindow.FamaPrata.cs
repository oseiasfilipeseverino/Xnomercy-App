using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Velopack;
using XnomercyApp.Network;

namespace XnomercyApp;

/// <summary>
/// Fama & Prata: o grafico da sessao e o painel de totais.
///
/// Os campos do grafico (_sessionSamples, _lastSampleAt) moram aqui junto
/// com quem os usa — classe parcial compartilha membro, entao nao ha
/// diferenca pro compilador, so pra quem le.
///
/// Parte do MainWindow, dividido por assunto. Classe PARCIAL: continua
/// sendo uma classe so, nada muda de nome e o XAML continua achando os
/// handlers — que e o que test_xaml.py confere a cada passo.
/// </summary>
public partial class MainWindow
{
    // ── Gráfico da sessão (Fama & Prata) ─────────────────────────────────────
    // Amostra 1x/min os totais acumulados (só com captura ligada e não pausada —
    // parado/pausado não gera ponto, senão o gráfico virava um platô gigante).
    // Cap de 720 pontos = 12h de sessão, mais que qualquer farm real.
    private readonly List<(DateTime At, long Fame, long Silver)> _sessionSamples = new();
    private DateTime _lastSampleAt = DateTime.MinValue;
    private const int MaxSessionSamples = 720;

    private void SampleSessionChart()
    {
        if (!_capturing || _paused) return;
        var now = DateTime.Now;
        if ((now - _lastSampleAt).TotalSeconds < 60) return;
        _lastSampleAt = now;
        _sessionSamples.Add((now, _fameTracker.TotalFame, _fameTracker.TotalSilver));
        if (_sessionSamples.Count > MaxSessionSamples) _sessionSamples.RemoveAt(0);
        if (PanelFame.Visibility == Visibility.Visible) RedrawSessionChart();
    }

    private void RedrawSessionChart()
    {
        if (_sessionSamples.Count < 2)
        {
            PanelSessionChart.Visibility = Visibility.Collapsed;
            return;
        }
        PanelSessionChart.Visibility = Visibility.Visible;
        var canvas = SessionChartCanvas;
        canvas.Children.Clear();
        double W = canvas.Width, H = canvas.Height;
        long maxFame = Math.Max(_sessionSamples.Max(s => s.Fame), 1);
        long maxSilver = Math.Max(_sessionSamples.Max(s => s.Silver), 1);
        int n = _sessionSamples.Count;

        System.Windows.Shapes.Polyline MakeLine(Func<(DateTime At, long Fame, long Silver), long> sel, long max, string hex)
        {
            var pl = new System.Windows.Shapes.Polyline
            {
                Stroke = B(hex),
                StrokeThickness = 1.6,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            };
            for (int i = 0; i < n; i++)
            {
                double x = W * i / (n - 1);
                double y = H - 3 - (H - 6) * sel(_sessionSamples[i]) / (double)max;
                pl.Points.Add(new System.Windows.Point(x, y));
            }
            return pl;
        }
        canvas.Children.Add(MakeLine(s => s.Fame, maxFame, "#f87171"));
        canvas.Children.Add(MakeLine(s => s.Silver, maxSilver, "#e2e8f0"));
        var span = _sessionSamples[^1].At - _sessionSamples[0].At;
        TxtSessionChartHint.Text =
            $"{n} amostras · {span.TotalMinutes:0}min · pico fama {maxFame:N0} · pico prata {maxSilver:N0}";
    }

    // ── Fama & Prata (Fase 3) ───────────────────────────────────────────────
    private void RefreshFamePanel()
    {
        TxtFameTotal.Text = _fameTracker.TotalFame.ToString("N0");
        TxtYellowFameTotal.Text = _fameTracker.TotalYellowFame.ToString("N0");
        TxtSilverTotal.Text = _fameTracker.TotalSilver.ToString("N0");
        // Taxa por hora — transforma o painel num medidor de eficiência de farm de
        // verdade ("esse spot vale a pena?"), não só um contador de total acumulado.
        TxtFamePerHour.Text = $"{_fameTracker.FamePerHour:N0}/hora";
        TxtYellowFamePerHour.Text = $"{_fameTracker.YellowFamePerHour:N0}/hora";
        TxtSilverPerHour.Text = $"{_fameTracker.SilverPerHour:N0}/hora";
        var elapsed = DateTime.Now - _fameTracker.SessionStart;
        TxtFameSessionStart.Text = $"Sessão desde {_fameTracker.SessionStart:HH:mm:ss} · {elapsed:hh\\:mm\\:ss}";

        TxtMobKillsTotal.Text = _killTracker.TotalKills.ToString("N0");
        TxtMobKillsPerHour.Text = $"{_killTracker.KillsPerHour:0.0}/hora";
        _mobKillRows.Clear();
        foreach (var (name, kills, perHour) in _killTracker.Snapshot())
            _mobKillRows.Add(new MobKillRowDisplay { Name = name, Kills = kills, KillsPerHour = perHour.ToString("0.0") });
    }

    private void BtnFameReset_Click(object sender, RoutedEventArgs e)
    {
        _fameTracker.Reset();
        _killTracker.Reset();
        // Gráfico acompanha a mesma "sessão" do tracker — reiniciar limpa os dois.
        _sessionSamples.Clear();
        _lastSampleAt = DateTime.MinValue;
        RedrawSessionChart();
    }
}
