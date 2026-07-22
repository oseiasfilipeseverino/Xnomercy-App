namespace XnomercyApp.Network;

public sealed class DamageMeterEntry
{
    public long ObjectId { get; init; }
    public long Damage { get; set; }
    public long Healing { get; set; }

    // Dano por habilidade (CausingSpellIndex, param [7] do HealthUpdate) — alimenta a
    // quebra por skill (estilo Albion Battle Analytics). Chave -1 = sem índice de
    // skill no evento (ataque básico de arma sem efeito de spell).
    public Dictionary<int, long> DamageBySpell { get; } = new();
}

public sealed class DamageBySpellEntry
{
    public int SpellIndex { get; init; }
    public string Name { get; init; } = "";
    public long Damage { get; init; }
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
            {
                var copy = new DamageMeterEntry { ObjectId = e.ObjectId, Damage = e.Damage, Healing = e.Healing };
                foreach (var kv in e.DamageBySpell) copy.DamageBySpell[kv.Key] = kv.Value;
                list.Add(copy);
            }
            return list;
        }
    }

    /// <summary>Quebra de dano por habilidade de UM jogador, ordenada do maior pro
    /// menor, com nome já resolvido via SpellCatalog (cai pro índice numérico se a
    /// habilidade ainda não foi carregada/identificada).</summary>
    public IReadOnlyList<DamageBySpellEntry> SnapshotBySpell(long objectId) => SnapshotBySpell(new[] { objectId });

    /// <summary>Igual acima, mas somando vários ObjectIds do mesmo jogador — ele troca
    /// de ObjectId a cada zona nova (dungeon/ilha), então uma linha do ranking (já
    /// agrupada por nome) pode corresponder a mais de um ObjectId na sessão.</summary>
    public IReadOnlyList<DamageBySpellEntry> SnapshotBySpell(IEnumerable<long> objectIds)
    {
        lock (_lock)
        {
            var totals = new Dictionary<int, long>();
            foreach (var id in objectIds)
            {
                if (!_entries.TryGetValue(id, out var entry)) continue;
                foreach (var kv in entry.DamageBySpell)
                    totals[kv.Key] = totals.GetValueOrDefault(kv.Key) + kv.Value;
            }
            return totals
                .Select(kv => new DamageBySpellEntry
                {
                    SpellIndex = kv.Key,
                    Name = kv.Key < 0 ? "Ataque básico" : (SpellCatalog.GetName(kv.Key) ?? $"Habilidade #{kv.Key}"),
                    Damage = kv.Value,
                })
                .OrderByDescending(x => x.Damage)
                .ToList();
        }
    }

    public void HandleEvent(PhotonEvent evt)
    {
        if (evt.EventCode != GameEventCodes.HealthUpdate) return;
        if (!evt.Parameters.TryGetValue(2, out var changeObj)) return;
        if (!evt.Parameters.TryGetValue(6, out var causerObj)) return;

        // PhotonParam cobre byte/short/int/long/float/double — sem isso, um hit/heal
        // pequeno (comum, ObjectIds e deltas baixos aparecem cedo na sessão/zona) caía
        // no default e era silenciosamente descartado, sem log nem erro.
        double change = PhotonParam.ToDouble(changeObj) ?? 0;
        if (change == 0) return;

        long? causerId = PhotonParam.ToLong(causerObj);
        if (causerId is null) return;
        if (PlayerRegistry.IsMob(causerId.Value)) return;   // desconsidera dano de mob

        // CausingSpellIndex: índice (no spells.xml) da habilidade que causou esse hit —
        // ver SpellCatalog.cs. -1 = sem efeito de spell (ataque básico de arma).
        int spellIndex = evt.Parameters.TryGetValue(7, out var spellObj)
            ? (PhotonParam.ToLong(spellObj) is long si ? (int)si : -1)
            : -1;

        lock (_lock)
        {
            if (!_entries.TryGetValue(causerId.Value, out var entry))
            {
                entry = new DamageMeterEntry { ObjectId = causerId.Value };
                _entries[causerId.Value] = entry;
            }
            if (change < 0)
            {
                long dmg = (long)Math.Round(-change);
                entry.Damage += dmg;
                entry.DamageBySpell[spellIndex] = entry.DamageBySpell.GetValueOrDefault(spellIndex) + dmg;
            }
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
