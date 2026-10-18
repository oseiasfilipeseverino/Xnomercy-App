using System.Collections.Generic;
using System.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Aba Coleta (pedido de 18/10/2026; estava na lista de pendências do app).
///
/// Evento HarvestFinished, pelo código-fonte do SAT (Triky313, GPL-3.0):
///   código 61 — [0]=quem coletou (ObjectId) [1]=ação [2]=hora [3]=recurso no mapa
///               [4]=item (índice) [5]=quantidade normal [6]=bônus de coletor [7]=bônus Premium
/// O 61 está na faixa em que a numeração do SAT bate com a nossa, calibrada no jogo
/// (NewCharacter 29, HealthUpdate 6, NewMob 123...), então não precisa de candidato +1.
/// Mesmo assim confere o formato: [4] item e [5] quantidade presentes.
///
/// Só conta a SUA coleta: se o seu ObjectId já é conhecido (vem dos eventos de fama e
/// prata), [0] tem que ser ele.
/// </summary>
public sealed class GatheringTracker
{
    // Número vivo (segue a calibração automática — ver EventOffset.ObserveBaixo).
    public static int HarvestFinished => GameEventCodes.HarvestFinished;

    public sealed class Linha
    {
        public int Item;
        public long Normal, BonusColetor, BonusPremium;
        public int Coletas;
        public long Total => Normal + BonusColetor + BonusPremium;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Linha> _porItem = new();
    private System.DateTime? _inicio;

    public System.DateTime? Inicio { get { lock (_lock) return _inicio; } }

    /// <summary>true se o evento era uma coleta sua e foi contado.</summary>
    public bool HandleEvent(PhotonEvent evt, long? self = null)
    {
        if (evt.EventCode != HarvestFinished) return false;
        var p = evt.Parameters;
        if (!p.TryGetValue(4, out var it) || PhotonParam.ToLong(it) is not long item || item < 0) return false;
        if (!p.TryGetValue(5, out var n) || PhotonParam.ToLong(n) is not long normal || normal < 0) return false;
        long euId = self ?? PlayerRegistry.SelfObjectId ?? -1;
        if (euId > 0 && p.TryGetValue(0, out var quem) && PhotonParam.ToLong(quem) is long q && q != euId)
            return false;   // coleta de outro jogador por perto
        long Bonus(byte k) => p.TryGetValue(k, out var v) && PhotonParam.ToLong(v) is long b && b > 0 ? b : 0;

        lock (_lock)
        {
            _inicio ??= System.DateTime.UtcNow;
            if (!_porItem.TryGetValue((int)item, out var l)) _porItem[(int)item] = l = new Linha { Item = (int)item };
            l.Normal += normal;
            l.BonusColetor += Bonus(6);
            l.BonusPremium += Bonus(7);
            l.Coletas++;
        }
        return true;
    }

    public List<Linha> Snapshot()
    {
        lock (_lock)
            return _porItem.Values.Select(l => new Linha
            {
                Item = l.Item, Normal = l.Normal, BonusColetor = l.BonusColetor,
                BonusPremium = l.BonusPremium, Coletas = l.Coletas
            }).OrderByDescending(l => l.Total).ToList();
    }

    /// <summary>Unidades por hora desde a primeira coleta da sessão.</summary>
    public double PorHora(long total, System.DateTime? agora = null)
    {
        var ini = Inicio;
        if (ini == null) return 0;
        var horas = ((agora ?? System.DateTime.UtcNow) - ini.Value).TotalHours;
        return horas < 1.0 / 60 ? 0 : total / horas;
    }

    public void Reset() { lock (_lock) { _porItem.Clear(); _inicio = null; } }
}
