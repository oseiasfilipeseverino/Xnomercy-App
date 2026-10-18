namespace XnomercyApp.Network;

/// <summary>
/// Calibração automática da numeração de eventos (18/10/2026).
///
/// O código-fonte do SAT (Triky313) traz a lista de eventos do jogo, mas comparando
/// com pacotes REAIS que este app gravou em 12/10 (diag_samples.txt), a numeração
/// do jogo estava +1 em relação à do SAT de algum ponto entre 123 e 209 em diante:
///   104 GuildPlayerUpdated, 123 NewMob ................ iguais ao SAT
///   210 lista de acesso (@_Owner/@_Guild/@_Everyone) .. SAT diz 209
///   242 6 marcadores do grupo vazios .................. SAT diz 241
///   244 GUID + marca de função no grupo ............... SAT diz 243
///   279 loot de outro jogador (calibrado 01/09) ....... SAT diz 278
/// Um patch que acrescenta um evento no meio desloca tudo depois dele — foi isso que
/// deixou o Loot Log mudo antes (277 -> 279) sem ninguém perceber.
///
/// Em vez de fixar o número, o app CONFERE no jogo: dois eventos de formato
/// inconfundível servem de âncora e dão o deslocamento real (o mesmo papel do
/// EventValidator do SAT, que só avisa; este corrige):
///   - lista de acesso: [4] e [5] listas de texto do mesmo tamanho, [4] com "@_Owner"  (SAT 209)
///   - loot de outro jogador: [2] quem pegou (texto), [4] item, [5] quantidade, [3] bool (SAT 278)
/// Começa em +1 (o que os pacotes de 12/10 mostram).
/// </summary>
public static class EventOffset
{
    public const int AnchorAccessStatus = 209;
    public const int AnchorOtherGrabbedLoot = 278;
    // MobKilled está na faixa de cima: o nosso 166 (calibrado no jogo) é o SAT 165 + 1.
    public const int AnchorMobKilled = 165;

    private static readonly object _lock = new();
    private static int _delta = 1;
    private static System.DateTime? _confirmadoEm;
    private static string _fonte = "pacotes de 12/10";

    public static int Delta { get { lock (_lock) return _delta; } }
    public static System.DateTime? ConfirmadoEm { get { lock (_lock) return _confirmadoEm; } }
    public static string Fonte { get { lock (_lock) return _fonte; } }

    /// <summary>Código vivo de um evento da lista do SAT (vale de ~209 em diante).</summary>
    public static int Live(int satCode) => satCode + Delta;

    /// <summary>Disparado quando o deslocamento confirmado MUDA (patch novo).</summary>
    public static event System.Action<int>? Mudou;

    // ── Números BAIXOS (18/10/2026) ──────────────────────────────────────────
    // Até aqui só a faixa de 209 em diante se recalibrava. Se um patch inserir um
    // evento no começo da lista, dano, nomes, fama e prata param juntos — e a Saúde
    // da captura só AVISA. Agora duas âncoras de formato inconfundível ajustam
    // sozinhas, cada uma a sua faixa (ver GameEventCodes.AplicarDeltasBaixos):
    //   HealthUpdates (SAT 7):  [0] número, [2] LISTA de variações, [6] LISTA de
    //                           causadores do mesmo tamanho — nenhum outro evento
    //                           perto disso manda duas listas paralelas assim
    //   NewCharacter (SAT 29):  [0] número, [1] nome sem espaço, [40] lista de
    //                           equipamento com 8+ espaços
    // Como evento solto pode enganar, a troca só acontece depois de 3 vistos
    // seguidos com o MESMO deslocamento (as âncoras de cima trocam na hora porque
    // têm texto fixo do jogo, "@_Owner").
    public const int AnchorHealthUpdates = 7, AnchorNewCharacter = 29;
    private const int ConfirmacoesBaixas = 3;
    private static int _deltaDano, _deltaNomes;
    private static (int Delta, int Vistos) _candDano, _candNomes;

    public static int DeltaDano { get { lock (_lock) return _deltaDano; } }
    public static int DeltaNomes { get { lock (_lock) return _deltaNomes; } }

    private static int? ObserveBaixo(PhotonEvent evt)
    {
        int code = evt.EventCode;
        var p = evt.Parameters;
        bool dano = false;
        int visto;
        if (code is >= AnchorHealthUpdates - 3 and <= AnchorHealthUpdates + 3
            && p.TryGetValue(0, out var a0) && PhotonParam.ToLong(a0) != null
            && p.TryGetValue(2, out var l2) && l2 is Array mud && mud.Length > 0 && !(l2 is string?[])
            && p.TryGetValue(6, out var l6) && l6 is Array cau && cau.Length == mud.Length && !(l6 is string?[]))
        {
            dano = true;
            visto = code - AnchorHealthUpdates;
        }
        else if (code is >= AnchorNewCharacter - 3 and <= AnchorNewCharacter + 3
                 && p.TryGetValue(0, out var b0) && PhotonParam.ToLong(b0) != null
                 && p.TryGetValue(1, out var n1) && n1 is string nome && nome.Length is >= 2 and <= 20 && !nome.Any(char.IsWhiteSpace)
                 && p.TryGetValue(40, out var e40) && e40 is Array { Length: >= 8 } && !(e40 is string?[]))
        {
            visto = code - AnchorNewCharacter;
        }
        else return null;

        bool mudou = false;
        lock (_lock)
        {
            ref var cand = ref dano ? ref _candDano : ref _candNomes;
            int atual = dano ? _deltaDano : _deltaNomes;
            if (visto == atual) { cand = (atual, 0); return visto; }
            cand = cand.Delta == visto ? (visto, cand.Vistos + 1) : (visto, 1);
            if (cand.Vistos >= ConfirmacoesBaixas)
            {
                if (dano) _deltaDano = visto; else _deltaNomes = visto;
                cand = (visto, 0);
                _confirmadoEm = System.DateTime.Now;
                _fonte = dano ? "golpes agrupados" : "jogador novo na tela";
                mudou = true;
            }
        }
        if (!mudou) return null;
        GameEventCodes.AplicarDeltasBaixos(DeltaDano, DeltaNomes);
        Mudou?.Invoke(visto);
        return visto;
    }

    /// <summary>Confere o evento contra as âncoras. Devolve o deslocamento visto, ou null.</summary>
    public static int? Observe(PhotonEvent evt)
    {
        int code = evt.EventCode;
        if (code >= 0 && code < 60) return ObserveBaixo(evt);
        if (code < 150 || code > 400) return null;
        var p = evt.Parameters;
        int? visto = null;
        string fonte = "";

        if (p.TryGetValue(4, out var a) && a is string?[] quem && p.TryGetValue(5, out var b) && b is string?[] acesso
            && quem.Length == acesso.Length && quem.Length > 0 && quem.Contains("@_Owner"))
        {
            visto = code - AnchorAccessStatus; fonte = "lista de acesso";
        }
        else if (p.TryGetValue(1, out var de) && de is string && p.TryGetValue(2, out var l) && l is string looter
                 && looter.Length > 0 && p.TryGetValue(3, out var pr) && pr is bool
                 && p.TryGetValue(4, out var it) && PhotonParam.ToLong(it) != null
                 && p.TryGetValue(5, out var q) && PhotonParam.ToLong(q) != null)
        {
            visto = code - AnchorOtherGrabbedLoot; fonte = "loot de outro jogador";
        }
        if (visto is not int d || d < -3 || d > 3) return null;

        bool mudou;
        lock (_lock)
        {
            mudou = d != _delta;
            _delta = d;
            _confirmadoEm = System.DateTime.Now;
            _fonte = fonte;
        }
        if (mudou)
        {
            GameEventCodes.GrabbedLoot = AnchorOtherGrabbedLoot + d;
            GameEventCodes.PartyFameShare = AnchorOtherGrabbedLoot + d;
            GameEventCodes.MobKilled = AnchorMobKilled + d;
            Mudou?.Invoke(d);
        }
        return d;
    }

    /// <summary>Só pro teste: volta ao estado inicial.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _delta = 1; _confirmadoEm = null; _fonte = "pacotes de 12/10";
            _deltaDano = _deltaNomes = 0; _candDano = _candNomes = (0, 0);
        }
        GameEventCodes.GrabbedLoot = AnchorOtherGrabbedLoot + 1;
        GameEventCodes.PartyFameShare = AnchorOtherGrabbedLoot + 1;
        GameEventCodes.MobKilled = AnchorMobKilled + 1;
        GameEventCodes.AplicarDeltasBaixos(0, 0);
    }
}
