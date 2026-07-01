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
    public const int Unknown = -1;

    // OtherGrabbedLoot (GrabbedLootEvent) — o feed social de loot: quem pegou o quê
    // de quem. É a fonte certa pro Loot Log (NewSimpleItem só vê o próprio inventário).
    // [1]=de quem (corpo, "MOB" se monstro) [2]=quem pegou [3]=é prata? (bool)
    // [4]=índice do item [5]=quantidade.
    // ERA 277 — o jogo mudou o índice numa atualização e isso silenciou o Loot Log
    // por completo (nem loot de terceiros aparecia mais). Recalibrado com captura real
    // (named_events_diag.txt): toda ocorrência com essa assinatura exata veio como 279,
    // nunca 277 (zero ocorrências de 277 em várias sessões de captura).
    public static int GrabbedLoot { get; set; } = 279;      // OtherGrabbedLoot

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

    // NewMob: [0]=ObjectId. Usado pra marcar quais ObjectIds são mobs e excluir o dano
    // deles do medidor (só dano de/para jogadores conta).
    // SUSPEITA: os dados reais de calibração mostram esse código repetindo várias vezes
    // por segundo com números tipo posição/vida — não parece "mob apareceu" (que devia
    // disparar uma vez só). Mantido por ora porque ainda funciona como sinal de mob (não
    // testamos remover), mas o código 74 abaixo é uma fonte mais confiável.
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

    // PartyInviteAccepted: dispara no lado de quem CONVIDOU, quando o convidado aceita.
    // [0]=nome de quem aceitou [1]=True. Confirmado em teste real (convite + aceite).
    public static int PartyInviteAccepted { get; set; } = 240;

    // PartyMemberStatus: broadcast periódico (repete enquanto estiverem no mesmo grupo,
    // dispara nos dois sentidos — não importa quem convidou). [1]=nome do membro
    // [6]=guild dele. Confirmado em 3 testes (convidando e sendo convidado).
    public static int PartyMemberStatus { get; set; } = 229;

    // PartyMemberLeft: alguém saiu/foi removido do grupo. [2]=nome de quem saiu.
    // Confirmado: disparou exatamente quando a expulsão aconteceu.
    public static int PartyMemberLeft { get; set; } = 182;

    public static bool IsCalibrated(int code) => code != Unknown;
}
