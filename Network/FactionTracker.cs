namespace XnomercyApp.Network;

/// <summary>
/// Pontos de facção e Might &amp; Favor ganhos na sessão (18/10/2026; SAT:
/// UpdateCurrencyEvent, UpdateFactionStandingEvent, MightAndFavorReceivedEvent).
///
///   85 UpdateCurrency         [2]=cidade (byte) [3]=moedas da facção [4]=pontos de rank
///   86 UpdateFactionStanding  [0]=cidade (byte) [1]=pontos de bandeira ganhos
///   496 MightAndFavorReceived [1]=Might [4]=Favor [7]=Favor total
/// Tudo em FixPoint (×10.000). Os dois primeiros estão na faixa de números que bate
/// com o SAT (seguem a calibração de nomes/fama); o Might &amp; Favor fica na faixa de
/// cima (SAT + deslocamento). Além do número, cada um só conta com o formato certo.
/// </summary>
public sealed class FactionTracker
{
    private const double FixPoint = 10_000;
    public const int SatMightAndFavor = 496;
    private readonly object _lock = new();
    private double _pontos, _moedas, _bandeira, _might, _favor;

    public event Action? Mudou;

    public (long Pontos, long Moedas, long Bandeira, long Might, long Favor) Totais()
    {
        lock (_lock) return ((long)_pontos, (long)_moedas, (long)_bandeira, (long)_might, (long)_favor);
    }

    public bool HandleEvent(PhotonEvent evt)
    {
        var p = evt.Parameters;
        int c = evt.EventCode;
        double V(byte k) => p.TryGetValue(k, out var v) && PhotonParam.ToDouble(v) is double d && d > 0 ? d / FixPoint : 0;
        bool Num(byte k) => p.TryGetValue(k, out var v) && PhotonParam.ToDouble(v) != null;

        // Cidade: número pequeno (o SAT converte qualquer número; não exigir byte).
        bool Cidade(byte k) => p.TryGetValue(k, out var v) && PhotonParam.ToLong(v) is long x && x is >= 0 and <= 20;
        // O 85 dos diagnósticos reais (27/08 a 12/09) vem com [0] [2] [3] [5] [9] — SEM
        // o [4] que o SAT descreve. Exigir o [4] fazia a facção nunca contar. Pontos =
        // [4] quando vier; senão o [3] (o que foi ganho neste evento).
        if (c == GameEventCodes.UpdateCurrency && Cidade(2) && Num(3))
        {
            lock (_lock)
            {
                if (Num(4)) { _moedas += V(3); _pontos += V(4); }
                else _pontos += V(3);
            }
        }
        else if (c == GameEventCodes.UpdateFactionStanding && Cidade(0) && Num(1))
        {
            lock (_lock) _bandeira += V(1);
        }
        // Might & Favor pelo FORMATO, numa faixa: nos diagnósticos ele era 496 até
        // 30/08 e 497 desde 01/09 (um patch mexeu só nessa faixa). Formato: [1] Might,
        // [4] Favor, [7] Favor total e dois campos finais [10]/[11] (dicionários).
        else if (Math.Abs(c - EventOffset.Live(SatMightAndFavor)) <= 3 && Num(1) && Num(7)
                 && p.ContainsKey(10) && p.ContainsKey(11))
        {
            lock (_lock) { _might += V(1); _favor += V(4); }
        }
        else return false;
        Mudou?.Invoke();
        CaptureHealth.Marcar("Facção e Might & Favor");
        return true;
    }

    public void Reset()
    {
        lock (_lock) _pontos = _moedas = _bandeira = _might = _favor = 0;
        Mudou?.Invoke();
    }
}
