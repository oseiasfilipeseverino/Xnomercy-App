namespace XnomercyApp.Network;

/// <summary>
/// Conversao comum de parametros de evento do Photon pro tipo numerico esperado.
/// O protocolo codifica o mesmo valor logico no tipo compacto que couber
/// (byte/short/int/long, e float/double pra fama/dano) — um switch que nao cobre
/// todos eles descarta o valor silenciosamente. Ja aconteceu duas vezes em lugares
/// diferentes (ObjectId pequeno em PlayerRegistry, delta de dano/cura pequeno em
/// DamageMeterTracker) por dois switches inline levemente diferentes. Centralizado
/// aqui pra nao reimplementar o mesmo switch incompleto a cada novo handler.
/// </summary>
public static class PhotonParam
{
    public static long? ToLong(object? v) => v switch
    {
        byte b => b, short s => s, int i => i, long l => l, _ => null
    };

    public static double? ToDouble(object? v) => v switch
    {
        byte b => b, short s => s, int i => i, long l => l, float f => f, double d => d, _ => (double?)null
    };
}
