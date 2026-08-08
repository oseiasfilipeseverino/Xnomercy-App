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

    // NewMob: [0]=ObjectId. DESLIGADO em PlayerRegistry.HandleEvent (código 53-59):
    // sem tag de confirmação, repete tipo sync de posição/vida várias vezes por segundo —
    // provavelmente dispara pra QUALQUER entidade próxima, jogador incluso, o que marcava
    // gente como mob por engano e sumia com o dano dela no meio da luta. Mantido mapeado
    // aqui só de referência; MobSpeak (74) e MobKilled (166) cobrem mob com tag verificada.
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

    // PartyMemberStatus: entrada de membro no grupo.
    //   [0]=uuid  [1]=nome  [2]=True  [3]=timestamp
    //
    // RECALIBRADO em 07/08/2026 com 1.800 linhas de captura real (3 sessões).
    // Estava mapeado como 229, que NÃO APARECE uma única vez nos dados — nem o
    // 240 do PartyInviteAccepted. O roster nunca era montado, em silêncio.
    //
    // A prova de que 104 é o certo está na composição: na sessão das 22:58 os 10
    // nomes emitidos são exatamente membros da XnoMercy que estavam na CTA
    // (SirTrovao, PretinhaDMacumba, Shakgusha, SushlLover, BestinhaCR7, Carabit6,
    // OttoMikelekethh, Xovonska, Montagus, Leanziin). Numa outra sessão, outro
    // grupo coerente. Não é gente passando na tela — é composição.
    public static int PartyMemberStatus { get; set; } = 104;

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
