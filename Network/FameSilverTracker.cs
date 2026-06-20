namespace XnomercyApp.Network;

/// <summary>
/// Acumula fama e prata ganhas durante a sessão atual do app, a partir dos eventos
/// decodificados pelo PacketCaptureService. Zera ao clicar em "Reiniciar sessão".
/// Só conta algo de fato depois que GameEventCodes.FameGain/SilverGain forem
/// calibrados (ver Network/GameEventCodes.cs) — até lá fica em 0, como esperado.
/// </summary>
public sealed class FameSilverTracker
{
    public long TotalFame { get; private set; }
    public long TotalSilver { get; private set; }
    public DateTime SessionStart { get; private set; } = DateTime.Now;

    public event Action? Updated;

    public void HandleEvent(PhotonEvent evt)
    {
        if (evt.Code == GameEventCodes.FameGain && GameEventCodes.IsCalibrated(GameEventCodes.FameGain))
        {
            if (TryGetLong(evt, out long fame))
            {
                TotalFame += fame;
                Updated?.Invoke();
            }
        }
        else if (evt.Code == GameEventCodes.SilverGain && GameEventCodes.IsCalibrated(GameEventCodes.SilverGain))
        {
            if (TryGetLong(evt, out long silver))
            {
                TotalSilver += silver;
                Updated?.Invoke();
            }
        }
    }

    public void Reset()
    {
        TotalFame = 0;
        TotalSilver = 0;
        SessionStart = DateTime.Now;
        Updated?.Invoke();
    }

    // Os eventos de fama/prata do Albion guardam o valor num parâmetro numérico do
    // dicionário — qual chave exata também faz parte da calibração. Por ora pegamos
    // o primeiro valor numérico que aparecer no payload do evento.
    private static bool TryGetLong(PhotonEvent evt, out long value)
    {
        foreach (var v in evt.Parameters.Values)
        {
            switch (v)
            {
                case int i: value = i; return true;
                case long l: value = l; return true;
                case float f: value = (long)f; return true;
                case double d: value = (long)d; return true;
            }
        }
        value = 0;
        return false;
    }
}
