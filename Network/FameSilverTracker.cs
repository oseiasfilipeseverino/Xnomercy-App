namespace XnomercyApp.Network;

/// <summary>
/// Acumula fama e prata ganhas na sessão atual. Códigos e parâmetros confirmados via
/// diagnóstico com dados reais:
///
/// UpdateFame (82): [1]=fama TOTAL, [2]=fama ganha ×10000 (já com zona, Premium e
///   bônus de evento [17]), [5]=Premium, [10]=fama da bolsa. Conta [2] + [10]. O SAT
///   multiplica o [2] por 1,5 com Premium, mas os dados reais mostram o total [1]
///   subindo exatamente o [2]: multiplicar dava 50% a mais.
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

    /// <summary>Quanto entrou neste evento (fama vermelha, prata líquida) — pro histórico de mapas.</summary>
    public event Action<long, long>? Ganhou;

    /// <summary>
    /// UpdateFame pelo FORMATO perto do número esperado (patch pode mexer na faixa):
    /// [0] você, [1] fama TOTAL (×10.000, sempre enorme), [2] ganho e [3]. O 84 (fama
    /// amarela) tem lista no [0]; o 81 (prata total) não tem [2]; o 86 tem [1] pequeno.
    /// </summary>
    public static bool EhFama(PhotonEvent evt)
    {
        var p = evt.Parameters;
        if (System.Math.Abs(evt.EventCode - GameEventCodes.FameGain) > 3) return false;
        if (evt.EventCode == GameEventCodes.FameGain) return Num(p, 2);
        return Num(p, 0) && p.TryGetValue(1, out var t) && PhotonParam.ToDouble(t) is double total && total >= 1e8
               && Num(p, 2) && Num(p, 3);
    }

    /// <summary>
    /// TakeSilver pelo FORMATO perto do número esperado: [0] quem, [1] hora curta (não
    /// ticks), [2] alvo, [3] prata e [8] multiplicador. O 61 (coleta) tem ticks no [1].
    /// </summary>
    public static bool EhPrata(PhotonEvent evt)
    {
        var p = evt.Parameters;
        if (System.Math.Abs(evt.EventCode - GameEventCodes.SilverTaken) > 3) return false;
        if (evt.EventCode == GameEventCodes.SilverTaken) return Num(p, 3);
        return Num(p, 0) && p.TryGetValue(1, out var h) && PhotonParam.ToDouble(h) is double hora && hora < 1e12
               && Num(p, 2) && Num(p, 3) && Num(p, 8);
    }

    private static bool Num(IReadOnlyDictionary<byte, object?> p, byte k) => p.TryGetValue(k, out var v) && PhotonParam.ToDouble(v) != null;

    public void HandleEvent(PhotonEvent evt)
    {
        // Mesmo padrão de lock do DamageMeterTracker: a thread de captura escreve aqui
        // e a UI lê TotalFame/TotalYellowFame/TotalSilver ao mesmo tempo — sem isso, os
        // totais podiam corromper ou perder incrementos sob concorrência.
        if (EhFama(evt) && TryGetParam(evt, 2, out long fameWithZoneRaw))
        {
            // Pacote malformado/corrompido podia mandar fama negativa aqui — um evento
            // de ganho de fama nunca é negativo de verdade, então descarta com segurança.
            if (fameWithZoneRaw < 0) return;
            // O [2] JÁ VEM com o bônus da Premium: numa sessão real de 18/10/2026 (24 eventos,
            // todos Premium) a fama TOTAL do [1] subiu exatamente o [2] a cada evento. A
            // fórmula antiga (× 1,5 se Premium, copiada do SAT) mostrava 50% de fama a mais.
            long satchelRaw = TryGetParam(evt, 10, out long s) && s > 0 ? s : 0;
            long gainedRaw = fameWithZoneRaw + satchelRaw;
            long antes, depois;
            lock (_lock)
            {
                antes = TotalFame;
                _fameRaw += gainedRaw;
                TotalFame = _fameRaw / 10000;
                depois = TotalFame;
            }
            Updated?.Invoke();
            if (depois > antes) Ganhou?.Invoke(depois - antes, 0);
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
        else if (EhPrata(evt) && TryGetParam(evt, 3, out long silverBruto))
        {
            if (silverBruto < 0) return;
            // O jogo manda a prata de TODO mundo do grupo/tela; o [0] é quem pegou. Sessão
            // real de 18/10/2026: na dungeon, nenhum dos eventos de prata era do jogador, e
            // o app somava tudo. Sem saber quem é você ainda (captura ligada no meio do
            // mapa), conta como antes até a próxima troca de zona te identificar.
            long? eu = PlayerRegistry.SelfObjectId;
            if (eu != null && TryGetParam(evt, 0, out long quem) && quem != eu) return;
            long silverAmt = PrataLiquida(evt, silverBruto);
            long antes, depois;
            lock (_lock)
            {
                antes = TotalSilver;
                _silverRaw += silverAmt;
                TotalSilver = _silverRaw / 10000;
                depois = TotalSilver;
            }
            Updated?.Invoke();
            if (depois > antes) Ganhou?.Invoke(0, depois - antes);
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
