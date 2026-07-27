using System.Collections.Concurrent;

namespace XnomercyApp.Network;

/// <summary>
/// Decodifica o envelope de transporte do Photon (estilo ENet) que embrulha cada
/// datagrama UDP antes da mensagem de aplicação (Event/OperationRequest/Response).
/// Implementação própria, baseada na estrutura pública de transporte do Photon
/// (usada por qualquer jogo feito com o motor Photon, não é específica do Albion).
///
/// Suporta comandos fragmentados (SendFragment): eventos grandes demais pra caber
/// num datagrama chegam picados e são remontados aqui antes de virar mensagem.
/// Isso importa mais do que parece — NewCharacter (o evento que traz NOME, guild e
/// equipamento de cada jogador) é justamente um dos maiores. Enquanto fragmento era
/// descartado, ninguém era identificado: o Medidor de Dano mostrava todo mundo como
/// "#12345" e o filtro padrão escondia esses, deixando só "Você" na lista. O sintoma
/// apareceu ao usar acelerador de rota, que come parte do espaço útil do pacote com
/// o próprio encapsulamento e faz o jogo fragmentar bem mais.
/// </summary>
public static class EnetPacketParser
{
    private const byte CommandSendReliable = 6;
    private const byte CommandSendUnreliable = 7;
    private const byte CommandSendFragment = 8;
    private const int PacketHeaderSize = 12;
    private const int CommandHeaderSize = 12;

    // ── Remontagem de fragmentos ──────────────────────────────────────────────
    private sealed class Reassembly
    {
        public byte[] Buffer = Array.Empty<byte>();
        public bool[] Received = Array.Empty<bool>();
        public int RemainingParts;
        public long UpdatedAtTicks;
    }

    // Chave: canal + sequência inicial (o par que identifica a mensagem picada).
    private static readonly ConcurrentDictionary<(byte Channel, int StartSeq), Reassembly> _pending = new();
    // Teto de memória: mensagem incompleta que nunca fecha (fragmento perdido na rede)
    // não pode ficar acumulando pra sempre.
    private const int MaxPendingReassemblies = 512;
    private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(10);
    private const int MaxMessageBytes = 4 * 1024 * 1024;   // sanidade contra header corrompido

    public static void Reset() => _pending.Clear();

    private static void PrunePending(long nowTicks)
    {
        if (_pending.Count <= MaxPendingReassemblies) return;
        foreach (var kv in _pending)
        {
            if (nowTicks - kv.Value.UpdatedAtTicks > PendingTtl.Ticks)
                _pending.TryRemove(kv.Key, out _);
        }
        // Se ainda estourou (muita coisa recente), esvazia: preferimos perder
        // remontagens em andamento a crescer sem limite.
        if (_pending.Count > MaxPendingReassemblies) _pending.Clear();
    }

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
            byte channelId = r.ReadByte();
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

            if (commandType == CommandSendFragment)
            {
                // Cabeçalho do fragmento: 20 bytes depois do cabeçalho de comando.
                if (payloadLen < 20) { r.Skip(Math.Max(payloadLen, 0)); continue; }
                int startSeq       = r.ReadInt32();
                int fragmentCount  = r.ReadInt32();
                int fragmentNumber = r.ReadInt32();
                int totalLength    = r.ReadInt32();
                int fragmentOffset = r.ReadInt32();
                int fragLen = payloadLen - 20;

                if (fragLen <= 0 || fragmentCount <= 0 || fragmentNumber < 0
                    || fragmentNumber >= fragmentCount
                    || totalLength <= 0 || totalLength > MaxMessageBytes
                    || fragmentOffset < 0 || (long)fragmentOffset + fragLen > totalLength)
                {
                    // Cabeçalho incoerente — pula este comando sem tentar remontar.
                    r.Skip(Math.Max(fragLen, 0));
                    continue;
                }

                var fragData = r.ReadBytesRaw(fragLen);
                long now = DateTime.UtcNow.Ticks;
                PrunePending(now);

                var entry = _pending.GetOrAdd((channelId, startSeq), _ => new Reassembly
                {
                    Buffer = new byte[totalLength],
                    Received = new bool[fragmentCount],
                    RemainingParts = fragmentCount,
                });

                byte[]? completo = null;
                lock (entry)
                {
                    // Proteção contra reuso de sequência com tamanhos diferentes.
                    if (entry.Buffer.Length != totalLength || entry.Received.Length != fragmentCount)
                    {
                        entry.Buffer = new byte[totalLength];
                        entry.Received = new bool[fragmentCount];
                        entry.RemainingParts = fragmentCount;
                    }
                    if (!entry.Received[fragmentNumber])
                    {
                        Array.Copy(fragData, 0, entry.Buffer, fragmentOffset, fragLen);
                        entry.Received[fragmentNumber] = true;
                        entry.RemainingParts--;
                    }
                    entry.UpdatedAtTicks = now;
                    if (entry.RemainingParts == 0)
                    {
                        completo = entry.Buffer;
                        _pending.TryRemove((channelId, startSeq), out _);
                    }
                }

                if (completo != null) yield return completo;
                continue;
            }

            if ((commandType == CommandSendReliable || commandType == CommandSendUnreliable) && payloadLen > 0)
                yield return r.ReadBytesRaw(payloadLen);
            else
                r.Skip(Math.Max(payloadLen, 0));
        }
    }
}
