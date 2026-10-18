namespace XnomercyApp.Network;

/// <summary>
/// O que os arquivos de diagnóstico NÃO podem levar (18/10/2026).
///
/// Os diagnósticos de calibração ficam ligados em todo build (BETA) e, com o
/// consentimento, vão pro canal de bugs do Discord da guild. Eles gravavam texto
/// cru dos eventos — e no pacote real de 12/10 havia uma mensagem de chat de um
/// desconhecido que só estava por perto. Chat de terceiros não é dado de
/// calibração e não tem por que sair do PC de ninguém.
///
/// Duas regras, aplicadas em todo arquivo de diagnóstico:
///   1. Eventos de chat (73 mensagem, 74 fala, 75 sussurro — enum do SAT) não são
///      gravados.
///   2. Texto com espaço vira "&lt;texto omitido, N caracteres&gt;". Nome de jogador, ID
///      de item, tag de mob e de mapa não têm espaço — que é o que a calibração usa.
///      Mensagem tem. Isso cobre também as operações que o próprio jogador manda
///      (chat e sussurro dele), que não têm número fixo conhecido.
/// </summary>
public static class DiagPrivacidade
{
    public static bool EhChat(int eventCode) => eventCode is 73 or 74 or 75;

    public static string Texto(string s) =>
        s.Any(char.IsWhiteSpace) ? $"<texto omitido, {s.Length} caracteres>" : s;
}
