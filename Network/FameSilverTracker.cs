namespace XnomercyApp.Network;

/// <summary>
/// Acumula fama e prata ganhas na sessão atual. Códigos e parâmetros confirmados via
/// diagnóstico com dados reais:
///
/// UpdateFame (82): [1]=fama total ×10000, [2]=fama GANHA neste evento ×10000.
///   Usamos a diferença do total — robusto mesmo se algum evento do meio for perdido
///   na captura (somar [2] subcontava quando faltava um). O baseline é o total ANTES
///   do primeiro ganho ([1]-[2]), então o primeiro evento também conta.
///
/// TakeSilver (62): [3]=prata ganha neste evento ×10000 (mesmo valor do popup do jogo).
///   Somamos [3] direto — é o ganho bruto por evento, sem perder o primeiro.
/// </summary>
public sealed class FameSilverTracker
{
    public long TotalFame { get; private set; }
    public long TotalYellowFame { get; private set; }
    public long TotalSilver { get; private set; }
    public DateTime SessionStart { get; private set; } = DateTime.Now;

    private long _fameRaw;         // soma da fama vermelha (já com premium/bolsa) ×10000
    private long _yellowRaw;       // soma da fama amarela ×10000
    private long _silverRaw;       // soma dos ganhos de prata ×10000

    public event Action? Updated;

    public void HandleEvent(PhotonEvent evt)
    {
        if (evt.EventCode == GameEventCodes.FameGain && TryGetParam(evt, 2, out long fameWithZoneRaw))
        {
            // Fórmula do jogo (mesma do Statistics Analysis Tool):
            //   fama exibida = fama_com_zona [2] × (1,5 se Premium [5]) + fama_da_bolsa [10]
            // Assim batemos com o número que o jogo mostra (com o bônus de +50% da Premium
            // e o bônus de bolsa de visão), em vez de só a fama base creditada.
            bool premium = evt.Parameters.TryGetValue(5, out var p) && p is bool pb && pb;
            long satchelRaw = TryGetParam(evt, 10, out long s) ? s : 0;
            long gainedRaw = (premium ? fameWithZoneRaw * 3 / 2 : fameWithZoneRaw) + satchelRaw;
            _fameRaw += gainedRaw;
            TotalFame = _fameRaw / 10000;
            Updated?.Invoke();
        }
        else if (evt.EventCode == GameEventCodes.YellowFame && TryGetParam(evt, 2, out long yellowGain))
        {
            _yellowRaw += yellowGain;
            TotalYellowFame = _yellowRaw / 10000;
            Updated?.Invoke();
        }
        else if (evt.EventCode == GameEventCodes.SilverTaken && TryGetParam(evt, 3, out long silverAmt))
        {
            _silverRaw += silverAmt;
            TotalSilver = _silverRaw / 10000;
            Updated?.Invoke();
        }
    }

    public void Reset()
    {
        TotalFame = 0;
        TotalYellowFame = 0;
        TotalSilver = 0;
        _fameRaw = 0;
        _yellowRaw = 0;
        _silverRaw = 0;
        SessionStart = DateTime.Now;
        Updated?.Invoke();
    }

    private static bool TryGetParam(PhotonEvent evt, byte key, out long value)
    {
        if (evt.Parameters.TryGetValue(key, out var v))
        {
            switch (v)
            {
                case int i: value = i; return true;
                case long l: value = l; return true;
                case short s: value = s; return true;
                case byte b: value = b; return true;
                case float f: value = (long)f; return true;
                case double d: value = (long)d; return true;
            }
        }
        value = 0;
        return false;
    }
}
