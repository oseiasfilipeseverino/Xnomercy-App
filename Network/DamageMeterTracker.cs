namespace XnomercyApp.Network;

public sealed class DamageMeterEntry
{
    public long ObjectId { get; init; }
    public long Damage { get; set; }
    public long Healing { get; set; }
}

/// <summary>
/// Ranking de dano/cura por jogador durante a sessão atual. O Albion não tem um
/// evento dedicado de "dano" — tudo é derivado de HealthUpdate (code 6): toda vez
/// que a vida de alguém muda, vem junto quem causou a mudança (CauserId) e o
/// objeto afetado (AffectedObjectId). Negativo = dano, positivo = cura.
///
/// Guardamos por ObjectId (CauserId); o nome é resolvido no display via
/// PlayerRegistry (evento NewCharacter).
///
/// THREAD-SAFE: HandleEvent roda na thread de captura (background) e Snapshot na
/// thread da UI. Um lock protege o dicionário — sem ele, iterar na UI enquanto a
/// captura escreve dava "Collection was modified" (crash) em combate.
/// </summary>
public sealed class DamageMeterTracker
{
    private readonly Dictionary<long, DamageMeterEntry> _entries = new();
    private readonly object _lock = new();

    public event Action? Updated;

    /// <summary>Cópia imutável das entradas atuais — segura pra ler na thread da UI.</summary>
    public IReadOnlyList<DamageMeterEntry> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<DamageMeterEntry>(_entries.Count);
            foreach (var e in _entries.Values)
                list.Add(new DamageMeterEntry { ObjectId = e.ObjectId, Damage = e.Damage, Healing = e.Healing });
            return list;
        }
    }

    public void HandleEvent(PhotonEvent evt)
    {
        if (evt.EventCode != GameEventCodes.HealthUpdate) return;
        if (!evt.Parameters.TryGetValue(2, out var changeObj)) return;
        if (!evt.Parameters.TryGetValue(6, out var causerObj)) return;

        double change = changeObj switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            _ => 0,
        };
        if (change == 0) return;

        long? causerId = causerObj switch { int i => i, long l => l, short s => s, _ => null };
        if (causerId is null) return;
        if (PlayerRegistry.IsMob(causerId.Value)) return;   // desconsidera dano de mob

        lock (_lock)
        {
            if (!_entries.TryGetValue(causerId.Value, out var entry))
            {
                entry = new DamageMeterEntry { ObjectId = causerId.Value };
                _entries[causerId.Value] = entry;
            }
            if (change < 0) entry.Damage += (long)Math.Round(-change);
            else entry.Healing += (long)Math.Round(change);
        }
        Updated?.Invoke();
    }

    public void Reset()
    {
        lock (_lock) _entries.Clear();
        Updated?.Invoke();
    }
}
