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
    public static int GrabbedLoot { get; set; } = 277;      // OtherGrabbedLoot

    // NewSimpleItem: [0]=ObjectId [1]=índice do item (tipo) [2]=quantidade
    // [4]=valor estimado [7]=durabilidade. (Mantido só pro modo avançado/diagnóstico.)
    public static int LootPickup { get; set; } = 32;        // NewSimpleItem
    public static int LootPickupEquipment { get; set; } = 30; // NewEquipmentItem (armas/armaduras, estrutura parecida)
    public static int FameGain { get; set; } = 82;          // UpdateFame (fama vermelha/combate)
    public static int YellowFame { get; set; } = 84;        // UpdateReSpecPoints (jogo mostra como "🟡", [2]=ganho)
    public static int SilverGain { get; set; } = 81;        // UpdateMoney
    public static int SilverTaken { get; set; } = 62;       // TakeSilver

    // NewCharacter: [0]=ObjectId [1]=Nome [8]=Guild [40]=equipamento (arma=índice 0).
    // Usado pra resolver ObjectId -> nome no medidor de dano e filtrar loot por guild.
    public static int NewCharacter { get; set; } = 29;

    // NewMob: [0]=ObjectId. Usado pra marcar quais ObjectIds são mobs e excluir o dano
    // deles do medidor (só dano de/para jogadores conta).
    public static int NewMob { get; set; } = 123;

    // HealthUpdate: [0]=ObjectId afetado [2]=variação de vida (negativo=dano,
    // positivo=cura) [6]=ObjectId de quem causou. Não existe evento dedicado de
    // "dano" no Albion — é tudo derivado daqui.
    public static int HealthUpdate { get; set; } = 6;

    public static bool IsCalibrated(int code) => code != Unknown;
}
