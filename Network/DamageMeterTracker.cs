namespace XnomercyApp.Network;

public sealed class DamageMeterEntry
{
    public string Name { get; init; } = "";
    // ObjectId mais recente visto pra esse nome — só pra ícone de arma/filtro de
    // guild no momento atual, NÃO é chave de agrupamento (ver comentário da classe).
    public long LastObjectId { get; set; }
    public long Damage { get; set; }
    // Cura EFETIVA: o que de fato subiu a vida de alguém. A cura que caiu em quem já
    // estava com a vida cheia vai pra Overheal (ver DamageMeterTracker.CuraEfetiva).
    public long Healing { get; set; }
    public long Overheal { get; set; }
    // Dano que este jogador RECEBEU (de jogador ou de mob).
    public long Taken { get; set; }

    // Tempo em combate, pra DPS: soma dos intervalos entre golpes seguidos que
    // ficaram a até JanelaCombate um do outro. Golpe isolado conta 1 segundo.
    public double ActiveSeconds { get; set; }
    public DateTime? LastHitUtc { get; set; }
    public double Dps => Damage / Math.Max(ActiveSeconds, 1);

    // Dano por habilidade (CausingSpellIndex, param [7] do HealthUpdate) — alimenta a
    // quebra por skill (estilo Albion Battle Analytics). Chave -1 = sem índice de
    // skill no evento (ataque básico de arma sem efeito de spell).
    public Dictionary<int, long> DamageBySpell { get; } = new();

    public void Absorver(DamageMeterEntry outra)
    {
        Damage += outra.Damage;
        Healing += outra.Healing;
        Overheal += outra.Overheal;
        Taken += outra.Taken;
        ActiveSeconds += outra.ActiveSeconds;
        if (outra.LastHitUtc is DateTime u && (LastHitUtc is null || u > LastHitUtc)) LastHitUtc = u;
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
/// evento dedicado de "dano" — tudo é derivado da variação de vida: toda vez que a
/// vida de alguém muda, vem junto quem causou a mudança (CauserId) e o objeto
/// afetado (AffectedObjectId). Negativo = dano, positivo = cura.
///
/// ── DOIS EVENTOS, NÃO UM (corrigido em 18/10/2026) ───────────────────────────
///
///   6  HealthUpdate   um golpe:          [0]=afetado [2]=variação [3]=vida nova
///                                        [6]=causador [7]=habilidade
///   7  HealthUpdates  VÁRIOS golpes no   [0]=afetado e, nos mesmos índices,
///                     mesmo pacote       LISTAS: [2] variações [3] vidas novas
///                                        [6] causadores [7] habilidades
///
/// Até a 1.0.66 só o 6 era lido. O 7 é o jeito que o servidor junta golpes que
/// caem no mesmo alvo quase juntos — e todo esse dano sumia. Nos pacotes reais de
/// 12/10 (diag_samples.txt) o medidor somou 88 de 531: o resto veio num 7. Nomes
/// e índices: HealthUpdatesEvent do SAT (Triky313, GPL-3.0), conferidos no pacote.
///
/// Também desde 18/10: dano em si mesmo não conta (o SAT descarta igual), a cura
/// que só "enche" quem já estava cheio vai pra Overheal, e cada jogador acumula o
/// dano que RECEBEU e o tempo em combate (DPS).
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

    // Última vida conhecida de cada objeto, pra separar cura efetiva de overheal.
    // Vem do [3] (vida depois da mudança) de todo HealthUpdate e dos eventos de
    // regeneração (91/96, [2]=vida [3]=vida máxima) — esses chegam quando a
    // regeneração para, ou seja, quando o alvo encheu a vida sozinho.
    private readonly Dictionary<long, (double Vida, DateTime Quando, int Epoca)> _vida = new();
    private static readonly TimeSpan VidaValidaPor = TimeSpan.FromSeconds(30);
    private const int MaxVidas = 5000;

    // Golpes a até 5s um do outro contam como o mesmo trecho de combate. Ataque
    // básico sai a cada ~1s e habilidade com recarga raramente deixa buraco maior;
    // quem fica 5s sem bater (kitando, correndo) para de somar tempo.
    public static readonly TimeSpan JanelaCombate = TimeSpan.FromSeconds(5);

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
        var copy = new DamageMeterEntry
        {
            Name = nome, LastObjectId = e.LastObjectId, Damage = e.Damage, Healing = e.Healing,
            Overheal = e.Overheal, Taken = e.Taken, ActiveSeconds = e.ActiveSeconds, LastHitUtc = e.LastHitUtc,
        };
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

    public void HandleEvent(PhotonEvent evt) => HandleEvent(evt, DateTime.UtcNow);

    /// <summary>Mesmo que HandleEvent(evt), com o relógio injetado (o teste usa pra DPS).</summary>
    public void HandleEvent(PhotonEvent evt, DateTime agoraUtc)
    {
        var p = evt.Parameters;
        bool mudou = false;

        if (evt.EventCode == GameEventCodes.HealthUpdate)
        {
            if (!p.TryGetValue(2, out var changeObj) || !p.TryGetValue(6, out var causerObj)) return;
            long? afetado = p.TryGetValue(0, out var a0) ? PhotonParam.ToLong(a0) : null;
            double? vidaNova = p.TryGetValue(3, out var v3) ? PhotonParam.ToDouble(v3) : null;
            int skill = p.TryGetValue(7, out var s7) && PhotonParam.ToLong(s7) is long si ? (int)si : -1;
            mudou = Registrar(afetado, PhotonParam.ToLong(causerObj), PhotonParam.ToDouble(changeObj) ?? 0,
                              vidaNova, skill, agoraUtc);
        }
        else if (evt.EventCode == GameEventCodes.HealthUpdates)
        {
            // Listas paralelas: o golpe i é ([2][i], [3][i], [6][i], [7][i]).
            if (!p.TryGetValue(2, out var mudancas) || mudancas is not Array lista) return;
            long? afetado = p.TryGetValue(0, out var a0) ? PhotonParam.ToLong(a0) : null;
            p.TryGetValue(3, out var vidas);
            p.TryGetValue(6, out var causadores);
            p.TryGetValue(7, out var skills);
            for (int i = 0; i < lista.Length; i++)
            {
                long? causador = Item(causadores, i) is double c ? (long)c : null;
                int skill = Item(skills, i) is double s ? (int)s : -1;
                mudou |= Registrar(afetado, causador, Item(mudancas, i) ?? 0, Item(vidas, i), skill, agoraUtc);
            }
        }
        else if (evt.EventCode == GameEventCodes.RegenerationHealthChanged
                 || evt.EventCode == GameEventCodes.RegenerationPlayerCombo)
        {
            // RegenerationHealthChanged (91) / RegenerationPlayerComboChanged (96):
            // [0]=objeto [2]=vida [3]=vida máxima. Conferido nos pacotes reais de
            // 12/10 (2279/2279, 3109/3109 — alvo de vida cheia). Só guarda a vida.
            if (p.TryGetValue(0, out var o) && PhotonParam.ToLong(o) is long obj
                && p.TryGetValue(2, out var a) && PhotonParam.ToDouble(a) is double vida
                && p.TryGetValue(3, out var b) && PhotonParam.ToDouble(b) is double max
                && vida >= 0 && max > 0 && vida <= max)
            {
                lock (_lock) GuardarVida(obj, vida, agoraUtc, EpocaAtual());
            }
            return;
        }
        else return;

        if (mudou) Updated?.Invoke();
    }

    /// <summary>Elemento i de uma lista do Photon (float[], short[], object[]...) como número.</summary>
    private static double? Item(object? lista, int i) =>
        lista is Array a && i < a.Length ? PhotonParam.ToDouble(a.GetValue(i)) : null;

    /// <summary>Um golpe ou uma cura. Devolve true se mudou algo no ranking.</summary>
    private bool Registrar(long? afetado, long? causerId, double change, double? vidaNova, int spellIndex, DateTime agoraUtc)
    {
        if (change == 0 || causerId is null) return false;

        // Dano em si mesmo (habilidade que custa vida, por exemplo) não é dano
        // causado nem recebido de ninguém — o SAT descarta igual.
        if (change < 0 && afetado == causerId) return false;

        // Resolvido AGORA — ver comentário da classe sobre por que isso importa mais
        // do que parece (reciclagem de ObjectId entre zonas).
        string causador = PlayerRegistry.NameOf(causerId.Value);
        string? alvo = afetado is long af ? PlayerRegistry.NameOf(af) : null;
        bool causadorEhMob = PlayerRegistry.IsMob(causerId.Value);

        lock (_lock)
        {
            int epoca = EpocaAtual();

            double? vidaAntes = null;
            if (afetado is long idAfetado)
            {
                if (_vida.TryGetValue(idAfetado, out var v) && v.Epoca == epoca && agoraUtc - v.Quando <= VidaValidaPor)
                    vidaAntes = v.Vida;
                if (vidaNova is double vn) GuardarVida(idAfetado, vn, agoraUtc, epoca);
            }

            // Dano RECEBIDO: vale pra jogador com nome (inclusive você), não importa
            // se quem bateu foi mob ou jogador.
            if (change < 0 && alvo != null && !alvo.StartsWith('#'))
            {
                var recebeu = Entrada(alvo, afetado!.Value);
                recebeu.Taken += (long)Math.Round(-change);
            }

            if (causadorEhMob) return true;   // dano/cura de mob não entra no ranking de quem causa

            DamageMeterEntry entry;
            if (causador.StartsWith('#'))
            {
                // Sem nome ainda: pendente, por ObjectId e época.
                var chave = (causerId.Value, epoca);
                if (!_pendentes.TryGetValue(chave, out entry!))
                {
                    if (_pendentes.Count >= MaxPendentes) TrimPendentes();
                    entry = new DamageMeterEntry { Name = causador };
                    _pendentes[chave] = entry;
                }
            }
            else
            {
                entry = Entrada(causador, causerId.Value);
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
                // Tempo em combate pro DPS.
                if (entry.LastHitUtc is DateTime ult && agoraUtc - ult <= JanelaCombate && agoraUtc >= ult)
                    entry.ActiveSeconds += (agoraUtc - ult).TotalSeconds;
                else
                    entry.ActiveSeconds += 1;
                entry.LastHitUtc = agoraUtc;
            }
            else
            {
                double efetiva = CuraEfetiva(change, vidaAntes, vidaNova);
                entry.Healing += (long)Math.Round(efetiva);
                entry.Overheal += (long)Math.Round(change - efetiva);
            }
        }
        return true;
    }

    /// <summary>
    /// Quanto da cura subiu a vida de verdade.
    ///
    /// Com a vida de antes e a de depois conhecidas, a cura efetiva é a diferença,
    /// limitada ao valor curado: cura em quem já estava cheio dá 0 e vira overheal.
    /// É um teto, nunca um piso — a regeneração entre um evento e outro sobe a vida
    /// sem avisar, então a vida "de antes" que temos pode ser menor que a real, e o
    /// erro é sempre pra contar cura a MAIS, nunca a menos. Sem uma das duas vidas,
    /// conta a cura inteira (comportamento antigo).
    /// </summary>
    public static double CuraEfetiva(double cura, double? vidaAntes, double? vidaDepois)
    {
        if (cura <= 0) return 0;
        if (vidaAntes is not double antes || vidaDepois is not double depois) return cura;
        return Math.Clamp(depois - antes, 0, cura);
    }

    /// <summary>Chamar só com o _lock tomado.</summary>
    private DamageMeterEntry Entrada(string nome, long objectId)
    {
        if (!_entries.TryGetValue(nome, out var entry))
        {
            if (_entries.Count >= MaxEntries) TrimLowest();
            entry = new DamageMeterEntry { Name = nome, LastObjectId = objectId };
            _entries[nome] = entry;
        }
        return entry;
    }

    /// <summary>Chamar só com o _lock tomado.</summary>
    private void GuardarVida(long obj, double vida, DateTime agoraUtc, int epoca)
    {
        if (_vida.Count >= MaxVidas && !_vida.ContainsKey(obj))
        {
            foreach (var k in _vida.Where(kv => agoraUtc - kv.Value.Quando > VidaValidaPor || kv.Value.Epoca != epoca)
                                   .Select(kv => kv.Key).ToList())
                _vida.Remove(k);
            if (_vida.Count >= MaxVidas) _vida.Clear();
        }
        _vida[obj] = (vida, agoraUtc, epoca);
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
            Entrada(nome, kv.Key.Id).Absorver(kv.Value);
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
                     .OrderBy(e => e.Damage + e.Healing + e.Taken)
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
            _vida.Clear();
        }
        Updated?.Invoke();
    }
}
