namespace XnomercyApp.Network;

/// <summary>
/// Decodifica o envelope de transporte do Photon (estilo ENet) que embrulha cada
/// datagrama UDP antes da mensagem de aplicação (Event/OperationRequest/Response).
/// Implementação própria, baseada na estrutura pública de transporte do Photon
/// (usada por qualquer jogo feito com o motor Photon, não é específica do Albion).
///
/// LIMITAÇÃO CONHECIDA: comandos do tipo "SendFragment" (eventos grandes demais para
/// caber em 1 pacote) não são reconstituídos nesta versão — só processamos comandos
/// que chegam completos num único datagrama. Suficiente para a maioria dos eventos
/// de jogo (loot, fama, prata), que são pequenos.
/// </summary>
public static class EnetPacketParser
{
    private const byte CommandSendReliable = 6;
    private const byte CommandSendUnreliable = 7;
    private const int PacketHeaderSize = 12;
    private const int CommandHeaderSize = 12;

    public static IEnumerable<byte[]> ExtractApplicationPayloads(byte[] udpData)
    {
        if (udpData.Length < PacketHeaderSize)
            yield break;

        var r = new PhotonReader(udpData);
        r.Skip(2);                    // PeerId
        r.Skip(1);                    // Flags
        byte commandCount = r.ReadByte();
        r.Skip(4);                    // Timestamp
        r.Skip(4);                    // Challenge

        for (int i = 0; i < commandCount && r.HasMore; i++)
        {
            if (r.Remaining < CommandHeaderSize) yield break;

            int cmdStart = r.Position;
            byte commandType = r.ReadByte();
            r.Skip(1);                 // ChannelId
            r.Skip(1);                 // CommandFlags
            r.Skip(1);                 // Reserved
            int commandLength = r.ReadInt32();
            r.Skip(4);                 // ReliableSequenceNumber

            // IMPORTANTE: usa aritmética em long pra checar os limites. commandLength
            // vem de bytes do pacote — se a estrutura estiver desalinhada (pacote fora
            // do formato esperado), pode vir como um número gigante/negativo; somar
            // direto em int (cmdStart + commandLength) pode estourar e "dar a volta",
            // passando a checagem por engano e tentando alocar um array de bilhões de
            // bytes (foi isso que causou o consumo de memória disparar). Em long isso
            // nunca estoura, então a checagem sempre pega o caso malformado de verdade.
            long payloadLenLong = (long)commandLength - CommandHeaderSize;
            if (commandLength <= 0 || payloadLenLong < 0 || (long)cmdStart + commandLength > udpData.Length)
                yield break; // pacote truncado/mal formado — para a leitura aqui

            int payloadLen = (int)payloadLenLong;

            if (commandType == CommandSendUnreliable)
            {
                r.Skip(4);             // UnreliableSequenceNumber
                payloadLen -= 4;
            }

            if ((commandType == CommandSendReliable || commandType == CommandSendUnreliable) && payloadLen > 0)
                yield return r.ReadBytesRaw(payloadLen);
            else
                r.Skip(Math.Max(payloadLen, 0));
        }
    }
}
