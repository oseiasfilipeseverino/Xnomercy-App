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
        private set { lock (_selfLock) _ownGuild = value; }
    }

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
    // Alimentado por PartyInviteAccepted (240, quando alguém aceita seu convite) e
    // PartyMemberStatus (229, broadcast periódico enquanto estiverem juntos — funciona
    // nos dois sentidos, não importa quem convidou). Removido por PartyMemberLeft (182).
    // Também expira sozinho (ver IsInParty) pra cobrir o caso de você ser expulso, que
    // não tem confirmação na calibração: só vimos o 182 disparar de quem expulsa.
    private static readonly ConcurrentDictionary<string, DateTime> _partyMembers = new();
    private static readonly TimeSpan PartyMemberTimeout = TimeSpan.FromSeconds(60);

    public static void HandleEvent(PhotonEvent evt)
    {
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
            if (evt.Parameters.TryGetValue(0, out var mid) && ToLong(mid) is long m) RegisterMob(m);
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
            if (evt.Parameters.TryGetValue(0, out var who) && who is string tag && tag.StartsWith('@')
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
            if (evt.Parameters.TryGetValue(3, out var tagObj) && tagObj is string mobTag && mobTag.StartsWith('@')
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
        if (evt.EventCode == GameEventCodes.PartyInviteAccepted)
        {
            if (evt.Parameters.TryGetValue(0, out var p1) && p1 is string acceptedName && acceptedName.Length > 0
                && evt.Parameters.TryGetValue(1, out var acc) && acc is bool ok && ok)
            {
                _partyMembers[acceptedName] = DateTime.UtcNow;
                // Mesmo teto de segurança que _byId/_mobs/_nameToGuild: sem o 182 (saída),
                // nomes antigos só expiram na leitura (IsInParty), nunca são removidos do
                // dicionário — numa sessão muito longa isso cresceria sem limite.
                if (_partyMembers.Count > 20000) _partyMembers.Clear();
            }
            return;
        }
        if (evt.EventCode == GameEventCodes.PartyMemberStatus)
        {
            if (evt.Parameters.TryGetValue(1, out var p2) && p2 is string statusName && statusName.Length > 0)
            {
                _partyMembers[statusName] = DateTime.UtcNow;
                if (_partyMembers.Count > 20000) _partyMembers.Clear();
            }
            return;
        }
        if (evt.EventCode == GameEventCodes.PartyMemberLeft)
        {
            if (evt.Parameters.TryGetValue(2, out var p3) && p3 is string leftName && leftName.Length > 0)
                _partyMembers.TryRemove(leftName, out _);
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
            _byId[id] = info;
            if (_byId.Count > 20000) _byId.Clear();   // mesmo teto de segurança que os mobs
            if (info.Guild.Length > 0)
            {
                _nameToGuild[info.Name] = info.Guild;
                if (_nameToGuild.Count > 20000) _nameToGuild.Clear();
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
        object?[] a => $"arr[{a.Length}]={{{string.Join(",", a.Select(x => x?.ToString() ?? "null"))}}}",
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
            if (_byId.Count > 20000) _byId.Clear();
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
                if (p.TryGetProperty("GuildName", out var gn)) OwnGuild = gn.GetString() ?? "";
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

    public static string NameOf(long objectId)
    {
        if (objectId == SelfObjectId) return "Você";
        return _byId.TryGetValue(objectId, out var v) && v.Name.Length > 0 ? v.Name : $"#{objectId}";
    }

    public static bool IsMob(long objectId) => _mobs.ContainsKey(objectId);

    // "Você" sempre conta como estando no seu próprio grupo. Pra qualquer outro nome,
    // só vale enquanto o último sinal (229/240) não passou do timeout — assim, se você
    // for expulso sem o app ver o 182 (só vimos disparar do lado de quem expulsa), o
    // filtro se autocorrige em até 60s em vez de prender alguém pra sempre na lista.
    public static bool IsInParty(string name) =>
        name == SelfName || (_partyMembers.TryGetValue(name, out var last) && DateTime.UtcNow - last < PartyMemberTimeout);

    public static bool IsInPartyById(long objectId) => objectId == SelfObjectId || IsInParty(NameOf(objectId));

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

    private static long? ToLong(object? v) => v switch
    {
        int i => i, long l => l, short s => s, byte b => b, _ => null
    };
}
