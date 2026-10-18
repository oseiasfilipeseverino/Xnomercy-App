namespace XnomercyApp.Network;

/// <summary>
/// Códigos de evento específicos do Albion (qual número de evento significa "pegou
/// loot", "ganhou fama", "dano causado", etc). Estes são internos do jogo e mudam
/// entre atualizações — diferente do protocolo Photon genérico (esse é fixo).
///
/// IMPORTANTE: o código real do evento NÃO é o byte do header do Photon — vem no
/// parâmetro 252 (lido como short, então passa de 255). Use PhotonEvent.EventCode,
/// não PhotonEvent.Code. Por isso estes valores são int, não byte.
///
/// Valores = posição sequencial do enum "EventCodes" do projeto
/// AlbionOnline-StatisticsAnalysis (Triky313, GPL-3.0 — por isso este projeto também é
/// GPL-3.0, ver LICENSE).
/// </summary>
public static class GameEventCodes
{
    // NÃO troque por -1. O PhotonEvent.EventCode devolve -1 pra todo pacote SEM o
    // parâmetro 252 — os eventos internos de transporte do Photon, que chegam o tempo
    // todo durante a captura.
    //
    // Enquanto Unknown era -1, "não calibrado" e "não é evento do jogo" eram o MESMO
    // número, e toda comparação `evt.EventCode == GameEventCodes.<não calibrado>`
    // casava com o tráfego de transporte inteiro. Marcar um código como desconhecido
    // não o desligava — ligava ele em tudo. Dois lugares pagavam por isso:
    //
    //   PlayerRegistry (PartyInviteAccepted): qualquer pacote de transporte com
    //   [0]=texto e [1]=true entrava no roster do grupo como se fosse um jogador.
    //
    //   MainWindow.Captura (LootPickupEquipment): todo pacote de transporte era
    //   marcado "🎯 POSSÍVEL LOOT" e empurrado pra lista de eventos marcados —
    //   justamente a tela usada pra calibrar os códigos.
    //
    // int.MinValue não é alcançável pelo parser (o código real vem de um short),
    // então o sentinela volta a significar só uma coisa.
    public const int Unknown = int.MinValue;

    // OtherGrabbedLoot (GrabbedLootEvent) — o feed social de loot: quem pegou o quê
    // de quem. É a fonte certa pro Loot Log (NewSimpleItem só vê o próprio inventário).
    // [1]=de quem (corpo, "MOB" se monstro) [2]=quem pegou [3]=é prata? (bool)
    // [4]=índice do item [5]=quantidade.
    // ERA 277 — o jogo mudou o índice numa atualização e isso silenciou o Loot Log
    // por completo (nem loot de terceiros aparecia mais). Recalibrado com captura real
    // (named_events_diag.txt): toda ocorrência com essa assinatura exata veio como 279,
    // nunca 277 (zero ocorrências de 277 em várias sessões de captura).
    public static int GrabbedLoot { get; set; } = 279;      // OtherGrabbedLoot

    // PartyFameShare: MESMO código 279, outro FORMATO. Calibrado em 01/09/2026.
    //
    // O 279 chega de duas formas, e só uma é loot:
    //
    //   loot de mob      [0]=fonte [1]=@MOB_KEEPER_GIANT [2]=nome [4]=199 [5]=1
    //   rateio de fama   [0]=fonte           [2]=nome [3]=True    [5]=12159299
    //
    // A segunda vem em RAJADA: no mesmo segundo, da mesma fonte, uma linha por
    // pessoa, todas com o mesmo valor em [5]. Isso é o rateio de fama de uma
    // morte — e no Albion fama é dividida com o SEU GRUPO, não com quem está por
    // perto. É por isso que serve de roster.
    //
    // A PROVA, na captura de 01/09 (sessao 22), é a que os dois candidatos
    // errados anteriores não teriam passado:
    //
    //     proximidade (code=104):  24 nomes
    //     rateio      (code=279):  20 nomes
    //     em comum:                 0
    //
    // Zero. As 20 pessoas que recebem o rateio não são NENHUMA das 24 que
    // apareceram por perto. Bystander não recebe fama.
    //
    // E o conjunto é estável, que é o que um grupo faz e proximidade não:
    //     sessao 24 e 25 — as MESMAS 20 pessoas em todas as rajadas
    //     sessao 15/16/17 — as MESMAS 5 pessoas em 15 rajadas seguidas
    //     sessao 26 — 18 estáveis, caindo pra 11 quando o grupo mudou
    //
    // 20 é o teto de grupo do Albion, e é onde as sessões cheias param.
    //
    // O 279 continua sendo GrabbedLoot também — quem consome tem que olhar o
    // FORMATO, não só o código. Ver PlayerRegistry.EhRateioDeGrupo.
    public static int PartyFameShare { get; set; } = 279;

    // NewSimpleItem: [0]=ObjectId [1]=índice do item (tipo) [2]=quantidade
    // [4]=valor estimado [7]=durabilidade. (Mantido só pro modo avançado/diagnóstico.)
    public static int LootPickup { get; set; } = 32;        // NewSimpleItem
    // ERA 30 por engano — a calibração com dados reais mostrou que 30 é o Move do
    // jogador (ver Move abaixo), não pickup de equipamento. Desligado (não sabemos o
    // código real do NewEquipmentItem e ele só servia pro modo avançado).
    public static int LootPickupEquipment { get; set; } = Unknown;

    // Move: posição/movimento de um jogador. Dispara continuamente pra cada jogador
    // por perto. [0]=ObjectId [5]=Nome [7]/[8]=posição. É a fonte CONTÍNUA de
    // ObjectId -> nome: diferente do NewCharacter (29, só dispara quando o jogador
    // entra na tela), o Move resolve o nome de quem já estava na cena, acabando com os
    // "#12345" no medidor de dano. Confirmado na calibração (232 amostras, todas com nome).
    public static int Move { get; set; } = 30;
    public static int FameGain { get; set; } = 82;          // UpdateFame (fama vermelha/combate)
    public static int YellowFame { get; set; } = 84;        // UpdateReSpecPoints (jogo mostra como "🟡", [2]=ganho)
    public static int SilverGain { get; set; } = 81;        // UpdateMoney
    public static int SilverTaken { get; set; } = 62;       // TakeSilver

    // NewCharacter: [0]=ObjectId [1]=Nome [8]=Guild [40]=equipamento (arma=índice 0).
    // Usado pra resolver ObjectId -> nome no medidor de dano e filtrar loot por guild.
    public static int NewCharacter { get; set; } = 29;

    // NewMob: [0]=ObjectId [1]=índice do mob [7]/[8]=posição [13]=vida [14]=vida máxima.
    // Esteve desligado (suspeita de pegar jogador). O pacote real de 12/10 mostrou o
    // formato de mob do SAT, e desde 18/10/2026 liga em PlayerRegistry — só quando o
    // formato inteiro bate (PlayerRegistry.EhNovoMob).
    public static int NewMob { get; set; } = 123;

    // MobSpeak (mob solta uma fala/provocação, ex: ao puxar agro): [0]=tipo do mob em
    // texto (ex: "@MOB_UNDEAD_PULLER_VETERAN") [4]=ObjectId do mob. Confirmação de alta
    // confiança de que aquele ObjectId é mob — usado como 2ª fonte pro filtro, além do
    // NewMob acima (só soma cobertura, não substitui).
    public static int MobSpeak { get; set; } = 74;

    // MobKilled (mob foi abatido): [0] e [4]=ObjectId do mob (repetido) [3]=tag do tipo
    // (ex: "@MOB_UNDEAD_ARCHER_STANDARD") [5]=nome de quem deu o último hit. Confirmado
    // na calibração junto com dungeon: o causador de dano que sobrava como #número era
    // exatamente o ObjectId que apareceu aqui como mob morto. 3ª fonte do filtro de mob.
    public static int MobKilled { get; set; } = 166;

    // HealthUpdate: [0]=ObjectId afetado [2]=variação de vida (negativo=dano,
    // positivo=cura) [6]=ObjectId de quem causou. Não existe evento dedicado de
    // "dano" no Albion — é tudo derivado daqui.
    public static int HealthUpdate { get; set; } = 6;

    // HealthUpdates: o MESMO conteúdo do HealthUpdate, mas em LISTAS — vários golpes
    // num pacote só. [0]=afetado [2]=variações [3]=vidas novas [6]=causadores
    // [7]=habilidades. Até a 1.0.66 o app ignorava e o dano dele sumia do medidor.
    // Conferido nos pacotes reais de 12/10: [2]=Single[]{-370,-73} [6]=Int16[]{4962,4962}.
    public static int HealthUpdates { get; set; } = 7;

    // Regeneração: [0]=objeto [2]=vida [3]=vida máxima. Chegam quando a regeneração
    // muda (inclusive quando para porque a vida encheu). O medidor usa só pra saber
    // a vida atual do alvo e separar cura efetiva de overheal.
    public static int RegenerationHealthChanged { get; set; } = 91;

    // CharacterEquipmentChanged: [0]=ObjectId [2]=equipamento (índices de item, mesma
    // ordem do [40] do NewCharacter). Alguém trocou de item. Pacote real de 12/10:
    // [2]=Int16[10]. Usado no painel do grupo (GroupGear).
    public static int CharacterEquipmentChanged { get; set; } = 90;
    public static int RegenerationPlayerCombo { get; set; } = 96;

    // PartyInviteAccepted: NÃO CALIBRADO.
    //
    // 240 não aparece nenhuma vez nas 1.800 linhas de captura de 07/08. Pode ser
    // que o evento só dispare no cliente de quem CONVIDA, e a captura foi feita
    // por quem foi convidado — mas sem dado que confirme, fica Unknown em vez de
    // um número que parece calibrado e não é.
    //
    // Na prática o roster não depende dele: o PartyMemberStatus (104) já emite
    // cada membro que entra.
    public static int PartyInviteAccepted { get; set; } = Unknown;

    // PartyMemberStatus: entrada de membro no grupo. NÃO CALIBRADO.
    //
    // Esteve em 104 de 07/08 a 29/08/2026, e 104 ESTÁ ERRADO. A captura de 27-29/08
    // (33.807 linhas, 15 sessões, 3 máquinas) mostra o que ele é de verdade:
    //
    //     2.990 linhas, 81 nomes DISTINTOS, um a cada ~2 segundos,
    //     cada nome repetindo 20-44 vezes
    //
    //     19:27:34 code=104 [1]="TilapiaBeisuda"
    //     19:27:36 code=104 [1]="ThiagoCabralz"
    //     19:27:38 code=104 [1]="Mathzz13"
    //
    // Grupo de 81 pessoas que fica re-adicionando os mesmos nomes não existe. É
    // jogador entrando no ALCANCE DE RENDERIZAÇÃO. Era isso que enchia a lista
    // "Só meu grupo" de estranhos.
    //
    // A justificativa antiga era: "na sessão das 22:58 os 10 nomes são exatamente
    // membros da XnoMercy que estavam na CTA". O argumento parecia forte e era
    // circular — numa CTA, quem está PERTO de você são seus companheiros de guild.
    // Proximidade e composição de grupo têm exatamente a mesma cara ali. Uma
    // sessão pequena não distingue as duas; 81 nomes distingue na hora.
    //
    // Fica Unknown de propósito. O filtro passa a dizer "ninguém detectado" (ver
    // MainWindow.LootLog.AtualizarRotuloDoGrupo) em vez de apresentar 81 estranhos
    // como se fossem o seu grupo. Uma lista errada com cara de certa é pior que
    // lista nenhuma — é a razão de metade das correções deste repositório.
    //
    // PRA CALIBRAR DE VERDADE é preciso uma captura com contexto: alguém em grupo
    // que anote "estava com fulano e sicrano das 20:00 às 20:10". Aí o código de
    // grupo é o que emite ESSES nomes NESSA janela e mais ninguém. Sem o horário
    // anotado do lado de fora, qualquer código que emita nome de guildmate passa
    // por candidato — foi assim que o 104 entrou.
    //
    // JÁ DESCARTADOS, pra ninguém refazer o caminho:
    //   104  proximidade (81 nomes, acima)
    //   182  movimento (coordenadas x,y + nome, dezenas de repetições)
    //   619  troféu de kill na ilha (UNIQUE_FURNITUREITEM_KILLTROPHY_*)
    //   210  lista de acesso de baú/ilha (nome + owner/noaccess/visitor)
    //   140  {aliança, guild}
    //   144-147  ranking/leaderboard (arrays de 20+ nomes com repetição)
    //   op 2  o Join do PRÓPRIO jogador — é de onde sai o SelfName
    //         (uma sessão inteira só tem UM nome nele; grupo teria vários)
    public static int PartyMemberStatus { get; set; } = Unknown;

    // PartyMemberLeft: NÃO CALIBRADO.
    //
    // Estava mapeado como 182, e a captura de 07/08 mostrou que 182 é MOVIMENTO:
    //   [0]=Single[2]={-132,18, -175,23}  [1]=1  [2]="gayzaoviadao"
    // São coordenadas x,y com o nome do PRÓPRIO jogador, repetidas dezenas de
    // vezes. Ninguém sai do próprio grupo 29 vezes andando pelo mapa.
    //
    // Fica em Unknown de propósito: sem o código certo, é melhor o roster manter
    // alguém que já saiu do que remover quem está — a poda por tempo em
    // MarcarNoGrupo já limpa quem parou de aparecer.
    public static int PartyMemberLeft { get; set; } = Unknown;

    public static bool IsCalibrated(int code) => code != Unknown;
}
