using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace XnomercyApp.Network;

/// <summary>
/// Guarda, durante a sessão, quem é quem: ObjectId -> nome, guild e arma principal.
/// Alimentado pelo evento NewCharacter (quando um jogador aparece na cena, o jogo
/// manda nome + guild + equipamento). Serve pra trocar "#73667" pelo nome real no
/// medidor de dano e pra filtrar loot por guild.
/// </summary>
public sealed class PlayerInfo
{
    public string Name = "";
    public string Guild = "";
    public int MainHand = -1;   // índice do item da mão principal (arma)
}

public static class PlayerRegistry
{
    private static readonly ConcurrentDictionary<long, PlayerInfo> _byId = new();
    private static readonly ConcurrentDictionary<long, byte> _mobs = new();
    // Nome -> guild, alimentado junto com _byId no NewCharacter. Loot Log só tem o nome
    // (o evento 277 já manda texto, não ObjectId), então precisa desse caminho separado
    // pro filtro "só guild" do Loot Log.
    private static readonly ConcurrentDictionary<string, string> _nameToGuild = new();

    // Ordem de inserção de _byId/_nameToGuild — usada só pra podar os mais antigos
    // (ver TrimOldest) em vez de zerar tudo de uma vez ao bater o teto. _mobs segue
    // com Clear() total, e nesse caso tudo bem: mob muda de ObjectId a cada zona
    // mesmo. _partyMembers agora poda por expiração (ver MarcarNoGrupo).
    private static readonly ConcurrentQueue<long> _byIdOrder = new();
    private static readonly ConcurrentQueue<string> _nameToGuildOrder = new();

    // Remove só os mais antigos até sobrar ~75% do teto — zerar tudo de uma vez
    // (comportamento antigo) podia apagar bem no meio de um combate o nome de um
    // jogador que está ativo AGORA, fazendo ele voltar a aparecer como "#12345"
    // no Medidor de Dano no pior momento possível (sessão longa, zona cheia).
    private static void TrimOldest<TValue>(ConcurrentDictionary<long, TValue> dict, ConcurrentQueue<long> order, int cap)
    {
        if (dict.Count <= cap) return;
        int target = cap * 3 / 4;
        while (dict.Count > target && order.TryDequeue(out var key))
            dict.TryRemove(key, out _);
    }
    private static void TrimOldest(ConcurrentDictionary<string, string> dict, ConcurrentQueue<string> order, int cap)
    {
        if (dict.Count <= cap) return;
        int target = cap * 3 / 4;
        while (dict.Count > target && order.TryDequeue(out var key))
            dict.TryRemove(key, out _);
    }

    // Lock só pra esses 3 campos escalares: são escritos pela thread de captura
    // (HandleOpResponse/ResolveOwnGuildAsync) e lidos pela UI ao mesmo tempo. O resto
    // da classe já usa ConcurrentDictionary, que cobre a si mesmo — esses três campos
    // simples não tinham proteção nenhuma antes.
    private static readonly object _selfLock = new();
    private static long? _selfObjectId;
    private static string? _selfName;
    private static string _ownGuild = "";

    // Seu próprio ObjectId — descoberto pelos eventos de fama/prata (que são do SEU
    // personagem). O jogo não manda NewCharacter de você mesmo, então é assim que a
    // gente sabe quem é "você" no medidor de dano.
    //
    // IMPORTANTE: só o valor ATUAL conta como "você" — já tentamos guardar todo
    // ObjectId que já foi self numa sessão (pra não duplicar "Você" ao trocar de
    // zona), mas o Albion recicla esses números pequenos por zona/sala: o mesmo
    // ObjectId que foi você numa sala pode virar OUTRO jogador de verdade na sala
    // seguinte, e marcar ele como "você" permanentemente soma o dano dele no seu
    // total (e some com a linha dele no ranking) — bug real, já aconteceu.
    public static long? SelfObjectId
    {
        get { lock (_selfLock) return _selfObjectId; }
        private set { lock (_selfLock) _selfObjectId = value; }
    }

    // Nome e guild do PRÓPRIO jogador. O Move/NewCharacter nunca trazem isso de você
    // mesmo (o jogo não manda esses eventos do seu próprio personagem), então a guild
    // do usuário é resolvida à parte: nome vem da operação Join (igual o ObjectId acima),
    // e a guild é então consultada na API pública do Albion (a mesma que o site já usa
    // pra buscar membros da guild) — assim o filtro "só minha guild" funciona pra
    // qualquer jogador que abrir o app, não só pra quem está na XnoMercy.
    public static string? SelfName
    {
        get { lock (_selfLock) return _selfName; }
        private set { lock (_selfLock) _selfName = value; }
    }

    public static string OwnGuild
    {
        get { lock (_selfLock) return _ownGuild; }
        private set { lock (_selfLock) { if (_ownGuild != value) System.Threading.Interlocked.Increment(ref _versaoGuildas); _ownGuild = value; } }
    }

    // Sobe a cada guild nova (a sua ou de alguém): a tela refiltra sozinha. Antes o
    // "Só minha guild" só refiltrava ao clicar na caixa — a linha escondida enquanto a
    // guild era desconhecida continuava escondida depois que ela chegava (18/10/2026).
    private static int _versaoGuildas;
    public static int VersaoGuildas => System.Threading.Volatile.Read(ref _versaoGuildas);

    // URL do render oficial da SUA arma equipada — resolvida junto com a guild (mesma
    // consulta de nome), mas precisa de uma 2ª chamada à API (a busca por nome não traz
    // equipamento, só o perfil completo do jogador traz). Fica null até resolver, ou se
    // o personagem estiver desarmado.
    private static string? _ownWeaponIconUrl;
    public static string? OwnWeaponIconUrl
    {
        get { lock (_selfLock) return _ownWeaponIconUrl; }
        private set { lock (_selfLock) _ownWeaponIconUrl = value; }
    }

    // Timeout curto: o default do HttpClient é 100s — se a API pública ficar lenta
    // (sem cair, só sem responder), a resolução de guild/arma ficava presa até esse
    // tempo todo antes do catch entrar em ação. 10s já é generoso pra uma API pública.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static int _resolvingOwnGuildFlag;   // 0=livre, 1=ocupado (ver ResolveOwnGuildAsync)

    // Nome -> hora em que vimos o último sinal de que esse jogador está no SEU grupo.
    //
    // Hoje quem alimenta isto é o rateio de fama (279 no formato de rateio),
    // calibrado em 01/09 — ver GameEventCodes.PartyFameShare. O PartyMemberStatus
    // (104) saiu: era proximidade, não grupo.
    //
    // Ninguém é removido por evento, porque nenhum código de "saiu" está confirmado:
    // a entrada expira na leitura (ver IsInParty), o que cobre saída e expulsão com
    // atraso. A remoção física do dicionário é só higiene de memória e acontece em
    // MarcarNoGrupo, sempre depois desse mesmo prazo.
    private static readonly ConcurrentDictionary<string, DateTime> _partyMembers = new();

    // 120s, e o número é MEDIDO, não escolhido.
    //
    // O rateio de fama só inclui quem estava por perto na hora da morte — não é o
    // roster completo do grupo. Quem ficou pra trás numa passagem, morreu, ou entrou
    // em combate separado simplesmente não aparece naquela rajada, e reaparece na
    // seguinte. Nas capturas de 01-05/09 o ShootingStarBR some e volta 4 vezes em 2
    // minutos por isso — não entrou e saiu do grupo 4 vezes.
    //
    // Então a janela precisa cobrir o INTERVALO ENTRE APARIÇÕES da mesma pessoa.
    // Medido sobre 928 intervalos reais das 8 capturas:
    //
    //     p50  6s      janela de  60s cobre 87,2%   <- era esta
    //     p75  28s     janela de  90s cobre 95,4%
    //     p90  68s     janela de 120s cobre 99,5%   <- e esta
    //     p95  73s     janela de 180s cobre 100%
    //     p99  95s
    //
    // Com 60s, 1 em cada 8 intervalos passava do prazo e a pessoa PISCAVA pra fora
    // da lista estando no grupo — que é a queixa original ("o grupo continua sem
    // aparecer sendo que o Lapreo está no meu grupo"), só que intermitente.
    //
    // O preço de 120s é que quem sai de verdade fica na lista até 2 minutos. Entre
    // esconder o loot de um membro real e mostrar por 2 minutos o de quem saiu, o
    // primeiro é pior: o filtro existe pra a pessoa VER o grupo dela.
    //
    // 180s cobriria 100%, mas o ganho sobre 120s é 0,5% e o custo é dobrar a
    // permanência de quem saiu. Não vale.
    private static readonly TimeSpan PartyMemberTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Registra presença no grupo, podando o que já expirou.
    /// </summary>
    /// <remarks>
    /// Antes o teto de segurança era <c>Clear()</c> no dicionário inteiro. Um grupo
    /// tem no máximo 20 pessoas, então na prática nunca disparava — mas se
    /// disparasse, derrubaria o grupo legítimo junto e o filtro "Só meu grupo"
    /// esvaziaria sem aviso, que é exatamente o sintoma que já perseguimos antes.
    /// Remover só quem passou do timeout resolve o crescimento sem esse risco:
    /// essas entradas já eram ignoradas na leitura (ver IsInParty).
    /// </remarks>
    /// <summary>
    /// O 279 chegou no formato de RATEIO DE FAMA (grupo) e não no de loot de mob?
    ///
    ///   loot de mob      [0]=fonte [1]=@MOB_KEEPER_GIANT [2]=nome [4]=199 [5]=1
    ///   rateio de fama   [0]=fonte           [2]=nome [3]=True    [5]=12159299
    ///
    /// O que separa: o loot de mob traz [1] com o nome do mob e [4] com a
    /// quantidade; o rateio não traz nenhum dos dois, e traz [3].
    ///
    /// Discriminar pela AUSÊNCIA de [1] sozinho seria frágil — um formato novo sem
    /// [1] entraria como grupo. Por isso exige também o [3] presente e um [5]
    /// grande: fama de rateio vem em milhões, quantidade de loot vem em unidades.
    /// </summary>
    private static bool EhRateioDeGrupo(PhotonEvent evt)
    {
        if (evt.Parameters.ContainsKey(1)) return false;    // tem mob = é loot
        if (evt.Parameters.ContainsKey(4)) return false;    // tem quantidade = é loot
        if (!evt.Parameters.ContainsKey(3)) return false;   // rateio sempre traz [3]
        // [5] é o valor rateado. Na captura de 01/09 os menores foram ~345.000 e
        // os maiores ~14 milhões; loot de mob traz 1. Mil é folga de sobra pros
        // dois lados.
        return evt.Parameters.TryGetValue(5, out var v) && ToLong(v) >= 1000;
    }

    /// <summary>Formato do NewMob: [1] índice numérico (não texto), [7] posição em lista, [14] vida máxima &gt; 0.</summary>
    public static bool EhNovoMob(PhotonEvent evt)
    {
        var p = evt.Parameters;
        return p.TryGetValue(1, out var idx) && ToLong(idx) is long i && i >= 0
            && p.TryGetValue(7, out var pos) && pos is Array { Length: 2 }
            && p.TryGetValue(14, out var hp) && PhotonParam.ToDouble(hp) is double max && max > 0;
    }

    private static void MarcarNoGrupo(string nome)
    {
        var agora = DateTime.UtcNow;
        _partyMembers[nome] = agora;
        if (_partyMembers.Count <= 64) return;
        foreach (var kv in _partyMembers)
            if (agora - kv.Value > PartyMemberTimeout)
                _partyMembers.TryRemove(kv.Key, out _);
    }

    public static void HandleEvent(PhotonEvent evt)
    {
        // Saiu da tela (Leave, SAT 1: [0]=ObjectId). O jogo REAPROVEITA o número pra
        // outra coisa que entrar depois — sem esquecer aqui, o próximo dono do número
        // herdava o nome (e o dano dele ia pra linha de quem já tinha saído) ou ficava
        // marcado como mob. Desde 18/10/2026. Você mesmo nunca é esquecido.
        if (evt.EventCode == GameEventCodes.Leave)
        {
            if (evt.Parameters.Count <= 3 && evt.Parameters.TryGetValue(0, out var lv) && ToLong(lv) is long saiu
                && saiu != SelfObjectId)
            {
                _byId.TryRemove(saiu, out _);
                _mobs.TryRemove(saiu, out _);
                GroupGear.Saiu(saiu);
            }
            return;
        }
        if ((evt.EventCode == GameEventCodes.FameGain || evt.EventCode == GameEventCodes.SilverGain
             || evt.EventCode == GameEventCodes.SilverTaken)
            && evt.Parameters.TryGetValue(0, out var sid) && ToLong(sid) is long self && self > 0)
        {
            // ObjectId muda de zona (e pode ser pequeno, tipo 112). Atualizamos sempre,
            // pra refletir o ObjectId atual do seu personagem na zona em que você está —
            // é o mesmo que aparece como causador no medidor de dano.
            SelfObjectId = self;
            return;
        }
        if (evt.EventCode == GameEventCodes.NewMob)
        {
            // Ficou desligado até 18/10/2026: a suspeita era que disparava pra QUALQUER
            // entidade, jogador incluso, e marcava gente como mob (o dano dela sumia do
            // medidor). O pacote real de 12/10 mostra o formato de mob do SAT
            // (NewMobEvent): [0]=ObjectId [1]=índice do mob [7]/[8]=posição
            // [13]=vida [14]=vida máxima — e jogador nunca vem nesse evento, vem no
            // NewCharacter (29), com nome em texto no [1]. Por isso agora liga, mas só
            // com o formato inteiro: índice numérico, posição em lista e vida máxima.
            if (EhNovoMob(evt) && evt.Parameters.TryGetValue(0, out var nm) && ToLong(nm) is long mobId)
                RegisterMob(mobId);
            return;
        }
        if (evt.EventCode == GameEventCodes.MobSpeak)
        {
            // Código 74 é "chat" genérico — dispara pra QUALQUER personagem que fala por
            // perto, mob ou jogador (confirmado: um jogador colou texto comum no chat e
            // disparou esse mesmo código). Só é sinal confiável de mob quando [0] é a tag
            // interna do mob (ex: "@MOB_UNDEAD_PULLER_VETERAN"), não um nome de jogador —
            // sem esse filtro, qualquer jogador que falasse no chat virava "mob" e o dano
            // dele desaparecia do Medidor de Dano.
            // "Começa com @" sozinho não basta — um jogador mencionando alguém no chat
            // (ex: "@fulano confere isso") também começa com @ e cairia aqui por engano.
            // Exigir o prefixo real da tag interna do mob evita esse falso positivo.
            if (evt.Parameters.TryGetValue(0, out var who) && who is string tag
                && tag.StartsWith("@MOB_", StringComparison.OrdinalIgnoreCase)
                && evt.Parameters.TryGetValue(4, out var mid) && ToLong(mid) is long m)
            {
                RegisterMob(m);
            }
            return;
        }
        if (evt.EventCode == GameEventCodes.MobKilled)
        {
            // Código 166 também é genérico — "alguém morreu", mob OU jogador (confirmado:
            // disparou pra um jogador real morrendo, com [3]=nome dele, não tag de mob).
            // Só é sinal confiável de mob quando [3] é a tag interna (ex: "@MOB_..."),
            // senão um jogador que morresse virava "mob" e o dano dele desaparecia do
            // Medidor de Dano — mesmo problema que já corrigimos no código 74 (chat).
            if (evt.Parameters.TryGetValue(3, out var tagObj) && tagObj is string mobTag
                && mobTag.StartsWith("@MOB_", StringComparison.OrdinalIgnoreCase)
                && evt.Parameters.TryGetValue(0, out var mid) && ToLong(mid) is long m)
            {
                RegisterMob(m);
            }
            return;
        }
        if (evt.EventCode == GameEventCodes.Move)
        {
            // Move dispara várias vezes/seg pra cada jogador por perto, trazendo
            // [0]=ObjectId e [5]=Nome. Resolve o nome de quem já estava na cena antes do
            // app abrir (NewCharacter só dispara na entrada), eliminando os "#12345".
            HandleMoveName(evt);
            return;
        }
        if (GameEventCodes.IsCalibrated(GameEventCodes.PartyInviteAccepted)
            && evt.EventCode == GameEventCodes.PartyInviteAccepted)
        {
            // Também não roda hoje (Unknown). O IsCalibrated na frente é o que impede
            // este bloco de voltar a casar com pacote de transporte se alguém devolver
            // o Unknown pra -1 sem perceber a colisão.
            if (evt.Parameters.TryGetValue(0, out var p1) && p1 is string acceptedName && acceptedName.Length > 0
                && evt.Parameters.TryGetValue(1, out var acc) && acc is bool ok && ok)
            {
                MarcarNoGrupo(acceptedName);
            }
            return;
        }
        // Rateio de fama do grupo (279 no formato de rateio, não no de loot).
        // Calibrado em 01/09/2026 — ver GameEventCodes.PartyFameShare pra prova.
        // Fica ANTES do PartyMemberStatus porque hoje é a ÚNICA fonte de roster
        // que funciona; o 104 está Unknown desde que se mostrou proximidade.
        if (GameEventCodes.IsCalibrated(GameEventCodes.PartyFameShare)
            && evt.EventCode == GameEventCodes.PartyFameShare
            && EhRateioDeGrupo(evt)
            && evt.Parameters.TryGetValue(2, out var pf) && pf is string nomeRateio
            && nomeRateio.Length > 0)
        {
            MarcarNoGrupo(nomeRateio);
            // Sem return: o mesmo evento ainda é o feed de loot social, e quem
            // consome GrabbedLoot precisa continuar recebendo.
        }

        // IsCalibrated na frente, como nos outros dois blocos de party. Era o único
        // sem a guarda: enquanto PartyMemberStatus valia 104 isso não fazia diferença,
        // mas em 29/08 ele voltou pra Unknown (104 é proximidade, não grupo — ver
        // GameEventCodes) e sem a guarda este bloco viraria a armadilha que o próprio
        // comentário do Unknown descreve: se alguém devolvesse o sentinela pra -1,
        // TODO pacote de transporte do Photon casaria aqui e o roster voltaria a se
        // encher de gente que só passou na tela.
        //
        // O índice [1]=nome é herança do 104 e NÃO vale pro evento real de grupo,
        // que ainda não foi encontrado. Conferir antes de tirar o Unknown.
        if (GameEventCodes.IsCalibrated(GameEventCodes.PartyMemberStatus)
            && evt.EventCode == GameEventCodes.PartyMemberStatus)
        {
            if (evt.Parameters.TryGetValue(1, out var p2) && p2 is string statusName && statusName.Length > 0)
            {
                MarcarNoGrupo(statusName);
            }
            return;
        }
        if (GameEventCodes.IsCalibrated(GameEventCodes.PartyMemberLeft)
            && evt.EventCode == GameEventCodes.PartyMemberLeft)
        {
            // NÃO RODA HOJE: PartyMemberLeft está Unknown (ver GameEventCodes). Estava
            // mapeado como 182, e a captura de 07/08 mostrou que 182 é MOVIMENTO —
            // coordenadas x,y com o nome do próprio jogador, repetidas dezenas de vezes.
            //
            // O corpo antigo chamava MarcarNoGrupo, ou seja, marcava PRESENÇA num
            // evento chamado "saiu". Aquilo era a resposta certa pro 182 (que não é
            // saída nenhuma), mas virava armadilha pra quem calibrasse o código de
            // verdade depois: o evento de saída marcaria a pessoa como presente, e o
            // filtro "Só meu grupo" ficaria preso em quem já saiu. Por isso o corpo
            // agora faz o que o nome diz, atrás do IsCalibrated.
            //
            // O índice [2]=nome é herança do 182 e NÃO está confirmado pro evento real.
            // Quando o party_diag.txt mostrar uma expulsão, conferir o índice antes de
            // tirar o Unknown.
            if (evt.Parameters.TryGetValue(2, out var p3) && p3 is string saiu && saiu.Length > 0)
            {
                _partyMembers.TryRemove(saiu, out _);
            }
            return;
        }
        if (evt.EventCode != GameEventCodes.NewCharacter) return;
        if (!evt.Parameters.TryGetValue(0, out var idObj) || ToLong(idObj) is not long id) return;

        var info = new PlayerInfo();
        if (evt.Parameters.TryGetValue(1, out var n)) info.Name = n?.ToString() ?? "";
        if (evt.Parameters.TryGetValue(8, out var g)) info.Guild = g?.ToString() ?? "";
        if (evt.Parameters.TryGetValue(40, out var eq) && eq is not null) info.MainHand = FirstEquip(eq);
        DiagLogNewCharacter(evt, id, info);   // calibração: ver se [1]/[8]/[40] batem com o jogo real
        if (info.Name.Length > 0)
        {
            bool isNewId = !_byId.ContainsKey(id);
            _byId[id] = info;
            if (isNewId) _byIdOrder.Enqueue(id);
            TrimOldest(_byId, _byIdOrder, 20000);
            if (info.Guild.Length > 0)
            {
                bool isNewName = !_nameToGuild.ContainsKey(info.Name);
                if (!_nameToGuild.TryGetValue(info.Name, out var antiga) || antiga != info.Guild)
                    System.Threading.Interlocked.Increment(ref _versaoGuildas);
                _nameToGuild[info.Name] = info.Guild;
                if (isNewName) _nameToGuildOrder.Enqueue(info.Name);
                TrimOldest(_nameToGuild, _nameToGuildOrder, 20000);
            }
        }
    }

    // Diagnóstico de calibração: grava TODO NewCharacter recebido (até 300), com todos os
    // parâmetros crus, pra comparar com quem realmente apareceu na tela. Se o nome de um
    // amigo visível não aparecer aqui, o evento não chegou (perda de pacote/filtro errado);
    // se aparecer com [1] vazio/estranho, o índice do nome mudou e precisa ser recalibrado.
    // Arquivo: %LocalAppData%\XnomercyApp\newchar_diag.txt
    private static int _diagCount;
    private static readonly object _diagLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogNewCharacter(PhotonEvent evt, long id, PlayerInfo info)
    {
        if (_diagCount >= 300) return;
        lock (_diagLock)
        {
            if (_diagCount >= 300) return;
            _diagCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "newchar_diag.txt"),
                    $"id={id} resolvedName=\"{info.Name}\" resolvedGuild=\"{info.Guild}\" {parms}\n");
            }
            catch { }
        }
    }

    private static string DiagVal(object? v) => v switch
    {
        null => "null",
        byte[] b => $"byte[{b.Length}]={Convert.ToHexString(b)}",
        object?[] a => $"arr[{a.Length}]={{{string.Join(",", a.Select(x => x is string s ? DiagPrivacidade.Texto(s) : x?.ToString() ?? "null"))}}}",
        string s => DiagPrivacidade.Texto(s),
        _ => v.ToString() ?? ""
    };

    // Resolve nome a partir do evento Move (código 30). Mantém guild/arma se o
    // NewCharacter já tiver preenchido (o Move não traz esses) — só garante o nome.
    private static void HandleMoveName(PhotonEvent evt)
    {
        if (!evt.Parameters.TryGetValue(0, out var idObj) || ToLong(idObj) is not long id) return;
        // Caminho rápido: Move repete muito; se já temos o nome desse id, não faz nada.
        if (_byId.TryGetValue(id, out var existing) && existing.Name.Length > 0) return;
        if (!evt.Parameters.TryGetValue(5, out var n) || n is not string name || name.Length == 0) return;

        if (existing != null) existing.Name = name;   // tinha entrada sem nome — completa
        else
        {
            _byId[id] = new PlayerInfo { Name = name };
            _byIdOrder.Enqueue(id);
            TrimOldest(_byId, _byIdOrder, 20000);
        }
    }

    // Operação de Join na zona (código real em [253]==2): traz o SEU ObjectId em [0] e
    // seu nome em [2]. É a forma mais cedo e confiável de saber quem é "Você" — antes só
    // descobríamos no 1º evento de fama/prata, então no começo da sessão seu próprio dano
    // aparecia como "#id" (ex: o causador com a maior fatia de dano sem nome).
    public static void HandleOpResponse(PhotonOperationResponse op)
    {
        if (op.Parameters.TryGetValue(253, out var rc) && ToLong(rc) == 2
            && op.Parameters.TryGetValue(0, out var sid) && ToLong(sid) is long self && self > 0)
        {
            SelfObjectId = self;
            // A SUA guild vem no próprio Join, no [58] (conferido nos diagnósticos de
            // setembro, "XNoMercy", e na sessão de 18/10/2026). Antes vinha só da API
            // pública do Albion — lenta, às vezes fora do ar — e enquanto não respondia o
            // filtro "Só minha guild" mostrava só você. A API continua de plano B.
            if (op.Parameters.TryGetValue(58, out var g) && g is string guilda && guilda.Trim().Length > 0)
                OwnGuild = guilda.Trim();
            if (op.Parameters.TryGetValue(2, out var n) && n is string name && name.Length > 0
                && name != SelfName)
            {
                SelfName = name;
                _ = ResolveOwnGuildAsync(name);   // dispara em background, não bloqueia a captura
            }
        }
    }

    // Operação real 24 (movimento/ação do próprio personagem): dispara constantemente
    // durante combate/movimento, sempre carregando SEU ObjectId em [5] quando presente
    // — confirmado cruzando várias sessões reais (o valor bate exatamente com o
    // SelfObjectId já conhecido por outras fontes, e muda certo quando você troca de
    // zona). É a fonte MAIS CEDO de "Você": diferente do Join (só na entrada da zona,
    // perdido se a captura começar depois) e da fama/prata (só após o 1º ganho), essa
    // operação dispara assim que você se move ou ataca, independente de quando a
    // captura começou. Sem isso, jogador que entra em combate antes de ganhar
    // fama/prata aparecia inteiro como "#id" — escondido por padrão no Medidor de Dano
    // (checkbox "Mostrar sem nome" desmarcado).
    public static void HandleOpRequest(PhotonOperationRequest op)
    {
        if (op.Parameters.TryGetValue(253, out var rc) && ToLong(rc) == 24
            && op.Parameters.TryGetValue(5, out var sid) && ToLong(sid) is long self && self > 0)
        {
            SelfObjectId = self;
        }
    }

    // Consulta a API pública do Albion (gameinfo.albiononline.com — sem chave, mesma
    // usada pelo site pra buscar membros de guild) pra descobrir a guild E a arma
    // equipada do PRÓPRIO personagem pelo nome. Roda uma vez por nome (troca de
    // personagem/zona repete o mesmo nome, então não refaz a consulta).
    private static async Task ResolveOwnGuildAsync(string name)
    {
        // CompareExchange em vez de "if (flag) return; flag = true": com volatile bool
        // havia uma janela teórica onde duas chamadas quase simultâneas liam flag=false
        // antes de qualquer uma escrever true, disparando 2 resoluções em paralelo.
        if (System.Threading.Interlocked.CompareExchange(ref _resolvingOwnGuildFlag, 1, 0) != 0) return;
        try
        {
            var url = $"https://gameinfo.albiononline.com/api/gameinfo/search?q={Uri.EscapeDataString(name)}";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("players", out var players)) return;
            foreach (var p in players.EnumerateArray())
            {
                if (!p.TryGetProperty("Name", out var pn) || pn.GetString() != name) continue;
                // Plano B: só preenche se o Join ainda não trouxe a guild.
                if (OwnGuild.Length == 0 && p.TryGetProperty("GuildName", out var gn)) OwnGuild = gn.GetString() ?? "";
                if (p.TryGetProperty("Id", out var idProp) && idProp.GetString() is string playerId)
                    await ResolveOwnWeaponAsync(playerId).ConfigureAwait(false);
                break;
            }
        }
        catch { /* sem internet ou API fora do ar — filtro "só minha guild" fica sem efeito até resolver */ }
        finally { System.Threading.Interlocked.Exchange(ref _resolvingOwnGuildFlag, 0); }
    }

    // 2ª chamada: a busca por nome não traz equipamento, só o perfil completo do
    // jogador traz (Equipment.MainHand.Type = unique_name do item, ex: "T6_2H_AXE").
    // Usa o mesmo render oficial que o resto do app já usa pros ícones.
    private static async Task ResolveOwnWeaponAsync(string playerId)
    {
        try
        {
            var url = $"https://gameinfo.albiononline.com/api/gameinfo/players/{Uri.EscapeDataString(playerId)}";
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            // O equipamento inteiro vai pro painel do grupo (GroupGear) — é a única
            // fonte do SEU equipamento até você trocar algum item no jogo.
            if (doc.RootElement.TryGetProperty("Equipment", out var todos) && todos.ValueKind == JsonValueKind.Object)
            {
                string? Tipo(string slot) => todos.TryGetProperty(slot, out var s) && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("Type", out var t) ? t.GetString() : null;
                GroupGear.DefinirMeuEquipamentoPelaApi(new[]
                    { Tipo("MainHand"), Tipo("OffHand"), Tipo("Head"), Tipo("Armor"), Tipo("Shoes"), Tipo("Bag"), Tipo("Cape") });
            }
            if (doc.RootElement.TryGetProperty("Equipment", out var eq)
                && eq.TryGetProperty("MainHand", out var mainHand)
                && mainHand.ValueKind == JsonValueKind.Object
                && mainHand.TryGetProperty("Type", out var typeProp)
                && typeProp.GetString() is string uniq && uniq.Length > 0)
            {
                OwnWeaponIconUrl = $"https://render.albiononline.com/v1/item/{uniq}.png?size=64";
            }
        }
        catch { /* desarmado, API fora do ar, etc. — fica sem ícone, não trava nada */ }
    }

    public static PlayerInfo? Get(long objectId) => _byId.TryGetValue(objectId, out var v) ? v : null;

    public static string GuildOf(long objectId) => Get(objectId)?.Guild ?? "";

    // Usado pelo Loot Log, que só tem o nome de texto do evento 277 (sem ObjectId).
    public static string GuildOfName(string name) => _nameToGuild.TryGetValue(name, out var g) ? g : "";

    /// <summary>Esse jogador é da SUA guild? Sem diferenciar maiúscula; sem guild conhecida, não.</summary>
    public static bool MesmaGuild(string name)
    {
        string minha = OwnGuild;
        return minha.Length > 0 && string.Equals(GuildOfName(name).Trim(), minha, StringComparison.OrdinalIgnoreCase);
    }

    public static string NameOf(long objectId)
    {
        if (objectId == SelfObjectId) return "Você";
        return _byId.TryGetValue(objectId, out var v) && v.Name.Length > 0 ? v.Name : $"#{objectId}";
    }

    public static bool IsMob(long objectId) => _mobs.ContainsKey(objectId);

    // "Você" sempre conta como estando no seu próprio grupo. Pra qualquer outro nome,
    // só vale enquanto o último PartyMemberStatus (104) daquele jogador não passou do
    // timeout. É esse prazo que faz as vezes do evento de saída que não temos: quem sai
    // ou é expulso some da lista em até 60s, em vez de ficar preso nela pra sempre.
    //
    // Desde 18/10/2026: com o grupo EXATO ativo (PartyTracker, eventos de grupo do
    // jogo), vale ele — inclusive pra quem SAIU, que sai na hora. O rateio de fama
    // abaixo fica de plano B pra quando o app abriu com o grupo já formado e o jogo
    // ainda não mandou a lista.
    public static bool IsInParty(string name) =>
        name == "Você" || string.Equals(name, SelfName, StringComparison.OrdinalIgnoreCase)
        || (PartyTracker.Ativo
            ? PartyTracker.Contem(name)
            : _partyMembers.TryGetValue(name, out var last) && DateTime.UtcNow - last < PartyMemberTimeout);

    public static bool IsInPartyById(long objectId) => objectId == SelfObjectId || IsInParty(NameOf(objectId));

    /// <summary>Quantos companheiros de grupo (fora você) estão dentro do prazo.</summary>
    /// <remarks>
    /// Existe pra tela poder DIZER que o grupo está vazio. Em 14/08 o Oseias
    /// ficou com o Loot Log em branco com "Só meu grupo" marcado, e não havia
    /// como saber, olhando o app, se não tinha loot ou se o roster não tinha
    /// ninguém — o filtro escondia tudo calado.
    ///
    /// O roster só se monta com a captura LIGADA no momento em que alguém entra
    /// no grupo. Quem já estava lá antes de a captura começar nunca é anunciado
    /// pelo servidor de novo, então o filtro fica vazio pra sempre naquela
    /// sessão. Sem esta contagem, isso é indistinguível de "não caiu loot".
    /// </remarks>
    /// <summary>Quem o rateio de fama marcou como do grupo, dentro do prazo (plano B do grupo exato).</summary>
    public static List<string> NomesNoGrupoPeloRateio()
    {
        var agora = DateTime.UtcNow;
        return _partyMembers.Where(kv => agora - kv.Value < PartyMemberTimeout).Select(kv => kv.Key).ToList();
    }

    public static int MembrosNoGrupo()
    {
        if (PartyTracker.Ativo)
            return PartyTracker.Nomes().Count(n => !string.Equals(n, SelfName, StringComparison.OrdinalIgnoreCase));
        var agora = DateTime.UtcNow;
        int n = 0;
        foreach (var kv in _partyMembers)
            if (agora - kv.Value < PartyMemberTimeout) n++;
        return n;
    }

    private static void RegisterMob(long id)
    {
        _mobs[id] = 1;
        // Teto de segurança: numa sessão longa (várias horas, muitas zonas), isso
        // cresceria sem limite. Limpa e deixa repopular — ObjectIds de mob mudam de
        // zona mesmo assim, então não perde nada de relevante mantendo isso pequeno.
        if (_mobs.Count > 20000) _mobs.Clear();
    }

    public static void Clear() { _byId.Clear(); _mobs.Clear(); }

    // O equipamento vem como array; a posição 0 é a mão principal (arma).
    private static int FirstEquip(object eq) => eq switch
    {
        int[] a when a.Length > 0 => a[0],
        short[] a when a.Length > 0 => a[0],
        byte[] a when a.Length > 0 => a[0],
        object?[] a when a.Length > 0 && ToLong(a[0]) is long l => (int)l,
        _ => -1
    };

    private static long? ToLong(object? v) => PhotonParam.ToLong(v);
}
