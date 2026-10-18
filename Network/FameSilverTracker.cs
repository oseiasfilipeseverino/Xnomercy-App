namespace XnomercyApp.Network;

/// <summary>
/// Acumula fama e prata ganhas na sessão atual. Códigos e parâmetros confirmados via
/// diagnóstico com dados reais:
///
/// UpdateFame (82): [2]=fama ganha com o bônus da zona ×10000, [5]=Premium,
///   [10]=fama da bolsa. Mesma fórmula do UpdateFameEvent do SAT: [2] × 1,5 se
///   Premium + [10]. O [17] (bônus de evento de fama) NÃO multiplica: o SAT lê, mas
///   volta o fator pra 1 quando o bônus está ativo — o jogo já manda o [2] com ele.
///
/// TakeSilver (62): [3]=prata ANTES das taxas ×10000, [4]=taxa da zona,
///   [5]=taxa da guild, [6]=penalidade de aliança (todas ×10000).
///   Desde 18/10/2026 soma o LÍQUIDO ([3] − [4] − [5] − [6]), que é o que entra na
///   carteira. Antes somava o [3] bruto: a tela mostrava mais prata do que a pessoa
///   tinha ganho de verdade. Nomes e índices: TakeSilverEvent do SAT.
/// </summary>
public sealed class FameSilverTracker
{
    public long TotalFame { get; private set; }
    public long TotalYellowFame { get; private set; }
    public long TotalSilver { get; private set; }
    public DateTime SessionStart { get; private set; } = DateTime.Now;

    // Taxa por hora — mesmo padrão do OpenWorldKillTracker.KillsPerHour (mínimo de
    // 1 minuto de sessão, senão o primeiro ganho já mostraria um número absurdo
    // projetado, tipo "50 milhões de fama/hora" com 10 segundos de sessão).
    private double SessionHours => Math.Max((DateTime.Now - SessionStart).TotalHours, 1.0 / 60);
    public double FamePerHour => TotalFame / SessionHours;
    public double YellowFamePerHour => TotalYellowFame / SessionHours;
    public double SilverPerHour => TotalSilver / SessionHours;

    private long _fameRaw;         // soma da fama vermelha (já com premium/bolsa) ×10000
    private long _yellowRaw;       // soma da fama amarela ×10000
    private long _silverRaw;       // soma dos ganhos de prata ×10000
    private readonly object _lock = new();

    public event Action? Updated;

    public void HandleEvent(PhotonEvent evt)
    {
        // Mesmo padrão de lock do DamageMeterTracker: a thread de captura escreve aqui
        // e a UI lê TotalFame/TotalYellowFame/TotalSilver ao mesmo tempo — sem isso, os
        // totais podiam corromper ou perder incrementos sob concorrência.
        if (evt.EventCode == GameEventCodes.FameGain && TryGetParam(evt, 2, out long fameWithZoneRaw))
        {
            // Pacote malformado/corrompido podia mandar fama negativa aqui — um evento
            // de ganho de fama nunca é negativo de verdade, então descarta com segurança.
            if (fameWithZoneRaw < 0) return;
            // Fórmula do jogo (mesma do Statistics Analysis Tool):
            //   fama exibida = fama_com_zona [2] × (1,5 se Premium [5]) + fama_da_bolsa [10]
            // Assim batemos com o número que o jogo mostra (com o bônus de +50% da Premium
            // e o bônus de bolsa de visão), em vez de só a fama base creditada.
            bool premium = evt.Parameters.TryGetValue(5, out var p) && p is bool pb && pb;
            long satchelRaw = TryGetParam(evt, 10, out long s) && s > 0 ? s : 0;
            long gainedRaw = (premium ? fameWithZoneRaw * 3 / 2 : fameWithZoneRaw) + satchelRaw;
            lock (_lock)
            {
                _fameRaw += gainedRaw;
                TotalFame = _fameRaw / 10000;
            }
            Updated?.Invoke();
        }
        else if (evt.EventCode == GameEventCodes.YellowFame && TryGetParam(evt, 2, out long yellowGain))
        {
            if (yellowGain < 0) return;
            lock (_lock)
            {
                _yellowRaw += yellowGain;
                TotalYellowFame = _yellowRaw / 10000;
            }
            Updated?.Invoke();
        }
        else if (evt.EventCode == GameEventCodes.SilverTaken && TryGetParam(evt, 3, out long silverBruto))
        {
            if (silverBruto < 0) return;
            long silverAmt = PrataLiquida(evt, silverBruto);
            lock (_lock)
            {
                _silverRaw += silverAmt;
                TotalSilver = _silverRaw / 10000;
            }
            Updated?.Invoke();
        }
    }

    /// <summary>[3] − taxa da zona [4] − taxa da guild [5] − penalidade de aliança [6], nunca negativo.</summary>
    public static long PrataLiquida(PhotonEvent evt, long bruto)
    {
        long taxas = 0;
        foreach (byte k in new byte[] { 4, 5, 6 })
            if (TryGetParam(evt, k, out long t) && t > 0) taxas += t;
        return Math.Max(0, bruto - taxas);
    }

    public void Reset()
    {
        lock (_lock)
        {
            TotalFame = 0;
            TotalYellowFame = 0;
            TotalSilver = 0;
            _fameRaw = 0;
            _yellowRaw = 0;
            _silverRaw = 0;
        }
        SessionStart = DateTime.Now;
        Updated?.Invoke();
    }

    private static bool TryGetParam(PhotonEvent evt, byte key, out long value)
    {
        if (evt.Parameters.TryGetValue(key, out var v) && PhotonParam.ToDouble(v) is double d)
        {
            value = (long)d;
            return true;
        }
        value = 0;
        return false;
    }
}
