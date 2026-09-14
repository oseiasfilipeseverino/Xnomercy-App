namespace XnomercyApp.Network;

public sealed class DamageMeterEntry
{
    public string Name { get; init; } = "";
    // ObjectId mais recente visto pra esse nome — só pra ícone de arma/filtro de
    // guild no momento atual, NÃO é chave de agrupamento (ver comentário da classe).
    public long LastObjectId { get; set; }
    public long Damage { get; set; }
    public long Healing { get; set; }

    // Dano por habilidade (CausingSpellIndex, param [7] do HealthUpdate) — alimenta a
    // quebra por skill (estilo Albion Battle Analytics). Chave -1 = sem índice de
    // skill no evento (ataque básico de arma sem efeito de spell).
    public Dictionary<int, long> DamageBySpell { get; } = new();

    public void Absorver(DamageMeterEntry outra)
    {
        Damage += outra.Damage;
        Healing += outra.Healing;
        foreach (var kv in outra.DamageBySpell)
            DamageBySpell[kv.Key] = DamageBySpell.GetValueOrDefault(kv.Key) + kv.Value;
    }
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
/// Guardamos por NOME resolvido NO MOMENTO do evento (PlayerRegistry.NameOf), não
/// por ObjectId. O Albion recicla ObjectIds pequenos por zona/sala (dungeon, ilha
/// etc.) — guardar por ObjectId e só resolver o nome no display (jeito antigo)
/// misturava dano de pessoas diferentes: se o ObjectId que era "Fulano" numa sala
/// virasse "Você" ou "Beltrano" na sala seguinte, TODO o histórico daquele ObjectId
/// (de antes da troca) passava a aparecer com o nome novo.
///
/// ── O DANO QUE SE PERDIA ANTES DO NOME CHEGAR (corrigido em 14/09/2026) ────────
///
/// Esse "carimbo na hora" tinha um efeito colateral que ninguém via. Quando o hit
/// chega ANTES de o nome do causador ser conhecido, NameOf devolve "#12345" — e o
/// dano ia pra uma linha chamada "#12345". Quando o nome chegava, os hits SEGUINTES
/// iam pra linha "Fulano", mas os anteriores ficavam presos na "#12345" pra sempre.
/// E a "#12345" é ESCONDIDA por padrão ("Mostrar sem nome" desmarcado).
///
/// Resultado: o começo da luta sumia do ranking. Justamente onde entra mais dano —
/// e justamente quando alguém abre o app com a luta já rolando. Valia pra VOCÊ
/// também: antes de o app descobrir seu SelfObjectId, seu dano ia pra "#seuId", e o
/// histórico da sessão (MainWindow.Historico, que lê a linha "Você") salvava um
/// número menor que o real. "O medidor está meio bugado" é, em boa parte, isso.
///
/// Agora dano de causador sem nome fica PENDENTE, guardado por ObjectId, e é
/// incorporado à linha certa assim que o nome resolve. A proteção contra reciclagem
/// continua valendo por causa da ÉPOCA: o pendente só é incorporado se ainda estamos
/// na mesma zona em que ele foi registrado. Pendente de zona anterior nunca é
/// incorporado — aquele ObjectId pode ser outra pessoa agora — e continua visível
/// como "#id" pra quem marcar "Mostrar sem nome".
///
/// THREAD-SAFE: HandleEvent roda na thread de captura (background) e Snapshot na
/// thread da UI. Um lock protege os dicionários — sem ele, iterar na UI enquanto a
/// captura escreve dava "Collection was modified" (crash) em combate.
/// </summary>
public sealed class DamageMeterTracker
{
    private readonly Dictionary<string, DamageMeterEntry> _entries = new();

    // Dano de causador ainda sem nome, por (ObjectId, época). Ver o comentário da classe.
    private readonly Dictionary<(long Id, int Epoca), DamageMeterEntry> _pendentes = new();

    private readonly object _lock = new();

    // Teto de segurança pra sessão longa: numa cidade cheia passam milhares de
    // jogadores diferentes, e cada um vira uma entrada (com o dicionário de dano
    // por habilidade junto). Sem limite, isso cresce a sessão inteira. Ao bater o
    // teto, descarta quem tem menos dano — quem interessa no ranking fica.
    private const int MaxEntries = 3000;
    private const int MaxPendentes = 3000;

    // ── Época de zona ──────────────────────────────────────────────────────────
    // O SelfObjectId muda quando você troca de zona (ver PlayerRegistry). É o sinal
    // mais barato de "os ObjectIds daqui pra frente podem ser outras pessoas".
    //
    // O cuidado está em NÃO contar a primeira descoberta como troca de zona: o
    // SelfObjectId sai de nulo pro seu id quando o app finalmente te identifica, e
    // se isso incrementasse a época, o SEU dano pendente (acumulado antes da
    // identificação) nunca seria incorporado à linha "Você" — que é justamente o
    // caso mais importante de todos. Só incrementa quando troca de um id CONHECIDO
    // pra outro id CONHECIDO.
    private long? _ultimoSelf;
    private int _epoca;

    private int EpocaAtual()
    {
        long? s = PlayerRegistry.SelfObjectId;
        if (s.HasValue && _ultimoSelf.HasValue && s.Value != _ultimoSelf.Value) _epoca++;
        if (s.HasValue) _ultimoSelf = s;
        return _epoca;
    }

    public event Action? Updated;

    /// <summary>Cópia imutável das entradas atuais — segura pra ler na thread da UI.</summary>
    public IReadOnlyList<DamageMeterEntry> Snapshot()
    {
        lock (_lock)
        {
            // Incorpora pendentes cujo nome resolveu DEPOIS do último hit. Sem isto,
            // quem teve o nome resolvido e parou de bater (morreu, saiu da luta)
            // ficaria com o dano preso no pendente — o conserto só cobriria quem
            // continua batendo.
            IncorporarResolvidos(EpocaAtual());

            var list = new List<DamageMeterEntry>(_entries.Count + _pendentes.Count);
            foreach (var e in _entries.Values) list.Add(Copiar(e, e.Name));
            // Pendentes continuam aparecendo como "#id" — o filtro "Mostrar sem nome"
            // da tela depende disso, e é o único lugar onde dano de zona anterior
            // (que nunca é incorporado) continua visível.
            foreach (var kv in _pendentes)
            {
                string rotulo = $"#{kv.Key.Id}";
                var existente = list.FirstOrDefault(x => x.Name == rotulo);
                if (existente != null) existente.Absorver(kv.Value);   // mesmo id em épocas diferentes
                else list.Add(Copiar(kv.Value, rotulo));
            }
            return list;
        }
    }

    private static DamageMeterEntry Copiar(DamageMeterEntry e, string nome)
    {
        var copy = new DamageMeterEntry { Name = nome, LastObjectId = e.LastObjectId, Damage = e.Damage, Healing = e.Healing };
        foreach (var kv in e.DamageBySpell) copy.DamageBySpell[kv.Key] = kv.Value;
        return copy;
    }

    /// <summary>Quebra de dano por habilidade de UM jogador (por nome), ordenada do
    /// maior pro menor, com nome já resolvido via SpellCatalog (cai pro índice
    /// numérico se a habilidade ainda não foi carregada/identificada).</summary>
    public IReadOnlyList<DamageBySpellEntry> SnapshotBySpell(string name)
    {
        lock (_lock)
        {
            Dictionary<int, long>? porSkill = null;
            if (_entries.TryGetValue(name, out var entry)) porSkill = entry.DamageBySpell;
            else if (name.StartsWith('#') && long.TryParse(name.AsSpan(1), out long id))
            {
                // Linha "#id" é soma de pendentes (possivelmente de mais de uma época).
                porSkill = new Dictionary<int, long>();
                foreach (var kv in _pendentes)
                    if (kv.Key.Id == id)
                        foreach (var s in kv.Value.DamageBySpell)
                            porSkill[s.Key] = porSkill.GetValueOrDefault(s.Key) + s.Value;
            }
            if (porSkill == null) return Array.Empty<DamageBySpellEntry>();
            return porSkill
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

        // Resolvido AGORA — ver comentário da classe sobre por que isso importa mais
        // do que parece (reciclagem de ObjectId entre zonas).
        string name = PlayerRegistry.NameOf(causerId.Value);

        // CausingSpellIndex: índice (no spells.xml) da habilidade que causou esse hit —
        // ver SpellCatalog.cs. -1 = sem efeito de spell (ataque básico de arma).
        int spellIndex = evt.Parameters.TryGetValue(7, out var spellObj)
            ? (PhotonParam.ToLong(spellObj) is long si ? (int)si : -1)
            : -1;

        lock (_lock)
        {
            int epoca = EpocaAtual();
            DamageMeterEntry entry;

            if (name.StartsWith('#'))
            {
                // Sem nome ainda: pendente, por ObjectId e época.
                var chave = (causerId.Value, epoca);
                if (!_pendentes.TryGetValue(chave, out entry!))
                {
                    if (_pendentes.Count >= MaxPendentes) TrimPendentes();
                    entry = new DamageMeterEntry { Name = name };
                    _pendentes[chave] = entry;
                }
            }
            else
            {
                if (!_entries.TryGetValue(name, out entry!))
                {
                    if (_entries.Count >= MaxEntries) TrimLowest();
                    entry = new DamageMeterEntry { Name = name };
                    _entries[name] = entry;
                }
                // O nome acabou de resolver pra este ObjectId: traz o que ele causou
                // antes, NESTA zona, pra linha certa.
                if (_pendentes.Remove((causerId.Value, epoca), out var pendente))
                    entry.Absorver(pendente);
            }

            entry.LastObjectId = causerId.Value;
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

    /// <summary>Incorpora pendentes da época atual cujo nome já resolveu.
    /// Chamar só com o _lock tomado.</summary>
    private void IncorporarResolvidos(int epoca)
    {
        if (_pendentes.Count == 0) return;
        List<(long, int)>? feitos = null;
        foreach (var kv in _pendentes)
        {
            if (kv.Key.Epoca != epoca) continue;   // zona anterior: ObjectId pode ser outra pessoa
            string nome = PlayerRegistry.NameOf(kv.Key.Id);
            if (nome.StartsWith('#')) continue;
            if (!_entries.TryGetValue(nome, out var alvo))
            {
                alvo = new DamageMeterEntry { Name = nome, LastObjectId = kv.Key.Id };
                _entries[nome] = alvo;
            }
            alvo.Absorver(kv.Value);
            (feitos ??= new()).Add(kv.Key);
        }
        if (feitos != null) foreach (var k in feitos) _pendentes.Remove(k);
    }

    /// <summary>Descarta o quarto inferior (menor dano+cura) quando bate o teto.
    /// Tira de uma vez em vez de 1 por 1 pra não reordenar a cada novo jogador.
    /// Chamar só com o _lock tomado.</summary>
    private void TrimLowest()
    {
        foreach (var key in _entries.Values
                     .OrderBy(e => e.Damage + e.Healing)
                     .Take(Math.Max(1, MaxEntries / 4))
                     .Select(e => e.Name)
                     .ToList())
            _entries.Remove(key);
    }

    /// <summary>Mesmo critério pros pendentes, mas descarta primeiro os de ÉPOCA
    /// ANTIGA: esses nunca vão ser incorporados, então são os menos úteis.</summary>
    private void TrimPendentes()
    {
        foreach (var key in _pendentes
                     .OrderBy(kv => kv.Key.Epoca == _epoca ? 1 : 0)
                     .ThenBy(kv => kv.Value.Damage + kv.Value.Healing)
                     .Take(Math.Max(1, MaxPendentes / 4))
                     .Select(kv => kv.Key)
                     .ToList())
            _pendentes.Remove(key);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _entries.Clear();
            _pendentes.Clear();
        }
        Updated?.Invoke();
    }
}
