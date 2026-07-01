using System;

namespace XnomercyApp.Network;

public sealed record PhotonEvent(byte Code, Dictionary<byte, object?> Parameters)
{
    // O código REAL do evento Albion vem no parâmetro 252 (lido como short, então
    // passa de 255 — ex: OtherGrabbedLoot = 279). O byte Code do header do Photon é
    // ignorado pra roteamento, exatamente como o AlbionParser do SAP faz. Retorna -1
    // quando não há param 252 (eventos internos/transporte que não são do jogo).
    public int EventCode
    {
        get
        {
            if (Parameters.TryGetValue(252, out var v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { return -1; }
            }
            return -1;
        }
    }
}
public sealed record PhotonOperationRequest(byte OperationCode, Dictionary<byte, object?> Parameters);
public sealed record PhotonOperationResponse(byte OperationCode, short ReturnCode, string? DebugMessage, Dictionary<byte, object?> Parameters);

/// <summary>
/// Decodifica as três mensagens Photon de nível de aplicação (Event, OperationRequest,
/// OperationResponse) a partir de um PhotonReader já posicionado no corpo da mensagem.
/// Implementação própria — estrutura pública do Protocol16 do Photon.
/// </summary>
public static class PhotonMessageParser
{
    // Albion usa o Protocol18 do Photon (confirmado empiricamente — o Protocol16
    // "clássico" não decodificava nada real; ver Network/README.md).
    public static PhotonEvent ReadEventData(PhotonReader r) => Protocol18Deserializer.DeserializeEventData(r);

    public static PhotonOperationRequest ReadOperationRequest(PhotonReader r) => Protocol18Deserializer.DeserializeOperationRequest(r);

    public static PhotonOperationResponse ReadOperationResponse(PhotonReader r) => Protocol18Deserializer.DeserializeOperationResponse(r);

    /// <summary>
    /// Tipo da mensagem de nível de aplicação, primeiro byte do payload Photon
    /// (depois do cabeçalho ENet de cada "command"). Valores documentados publicamente
    /// pelo SDK do Photon.
    /// </summary>
    public enum MessageType : byte
    {
        OperationRequest = 2,
        OperationResponse = 3,
        EventData = 4,
        InternalOperationRequest = 6,
        InternalOperationResponse = 7,
    }

    /// <summary>Resultado de tentar decodificar um payload de comando ENet como mensagem Photon.</summary>
    public static object? TryParseApplicationMessage(byte[] payload)
    {
        if (payload.Length < 2) return null;
        var r = new PhotonReader(payload);
        r.Skip(1); // primeiro byte: flag interna do Photon (geralmente 0xF3), não usamos
        byte msgType = r.ReadByte();
        try
        {
            return msgType switch
            {
                (byte)MessageType.EventData => ReadEventData(r),
                (byte)MessageType.OperationRequest => ReadOperationRequest(r),
                (byte)MessageType.OperationResponse => ReadOperationResponse(r),
                _ => null,
            };
        }
        catch
        {
            // Pacote truncado/fragmentado ou código de tipo que ainda não calibramos —
            // descartamos esse pacote em vez de derrubar a captura inteira.
            return null;
        }
    }
}
