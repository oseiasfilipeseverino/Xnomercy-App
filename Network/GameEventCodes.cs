namespace XnomercyApp.Network;

/// <summary>
/// Códigos de evento específicos do Albion (qual número de PhotonEvent.Code significa
/// "pegou loot", "ganhou fama", "dano causado", etc). Estes são internos do jogo e
/// mudam entre atualizações — diferente do protocolo Photon genérico (esse é fixo).
///
/// STATUS: ainda não calibrados. Use a aba Loot Log ("modo calibração") jogando uma
/// sessão curta pra descobrir os números reais, e preencha as constantes abaixo.
/// Até lá, os painéis de Fama/Prata e Medidor de Dano ficam "zerados" — é esperado,
/// não é bug: eles só reagem aos códigos certos quando soubermos quais são.
/// </summary>
public static class GameEventCodes
{
    public const byte Unknown = 0;

    public static byte LootPickup { get; set; } = Unknown;
    public static byte FameGain { get; set; } = Unknown;
    public static byte SilverGain { get; set; } = Unknown;
    public static byte DamageDealt { get; set; } = Unknown;
    public static byte HealingDone { get; set; } = Unknown;

    public static bool IsCalibrated(byte code) => code != Unknown;
}
