namespace XnomercyApp.Network;

/// <summary>
/// Decodificador de valores tipados do Photon "Protocol16" — implementação própria,
/// baseada na estrutura pública do protocolo (cada valor é prefixado por 1 byte que
/// indica o tipo, seguido pelos dados). Não copiado de nenhum projeto de terceiros.
///
/// Os códigos de tipo abaixo são os mais comuns documentados publicamente pelo SDK do
/// Photon. Se algum pacote real trouxer um código que não reconhecemos, devolvemos um
/// objeto "Unknown" em vez de derrubar o parser — isso é esperado e normal: o protocolo
/// específico de cada jogo (Albion incluso) evolui a cada patch, então o ajuste fino
/// desses códigos é feito observando tráfego real (ver Network/README.md).
/// </summary>
public static class Protocol16Deserializer
{
    private const byte TypeNull = 42;       // 0x2A
    private const byte TypeDictionary = 68;  // 'D'
    private const byte TypeStringArray = 97; // 'a'
    private const byte TypeByte = 98;        // 'b'
    private const byte TypeCustom = 99;      // 'c'
    private const byte TypeDouble = 100;     // 'd'
    private const byte TypeEventData = 101;  // 'e'
    private const byte TypeFloat = 102;      // 'f'
    private const byte TypeHashtable = 104;  // 'h'
    private const byte TypeInteger = 105;    // 'i'
    private const byte TypeShort = 107;      // 'k'
    private const byte TypeLong = 108;       // 'l'
    private const byte TypeBoolean = 111;    // 'o'
    private const byte TypeOperationResponse = 112; // 'p'
    private const byte TypeOperationRequest = 113;  // 'q'
    private const byte TypeString = 115;     // 's'
    private const byte TypeByteArray = 120;  // 'x'
    private const byte TypeArray = 121;      // 'y'
    private const byte TypeObjectArray = 122; // 'z'

    public static object? ReadValue(PhotonReader r)
    {
        byte type = r.ReadByte();
        return ReadValueOfType(r, type);
    }

    public static object? ReadValueOfType(PhotonReader r, byte type)
    {
        switch (type)
        {
            case TypeNull: return null;
            case TypeByte: return r.ReadByte();
            case TypeBoolean: return r.ReadBool();
            case TypeShort: return r.ReadInt16();
            case TypeInteger: return r.ReadInt32();
            case TypeLong: return r.ReadInt64();
            case TypeFloat: return r.ReadSingle();
            case TypeDouble: return r.ReadDouble();
            case TypeString: return r.ReadString();
            case TypeByteArray:
            {
                int len = r.ReadInt32();
                return r.ReadBytesRaw(len);
            }
            case TypeArray:
            {
                short count = r.ReadInt16();
                byte elementType = r.ReadByte();
                var arr = new object?[count];
                for (int i = 0; i < count; i++)
                    arr[i] = ReadValueOfType(r, elementType);
                return arr;
            }
            case TypeObjectArray:
            case TypeStringArray:
            {
                short count = r.ReadInt16();
                var arr = new object?[count];
                for (int i = 0; i < count; i++)
                    arr[i] = ReadValue(r);
                return arr;
            }
            case TypeDictionary:
            {
                byte keyType = r.ReadByte();
                byte valType = r.ReadByte();
                short count = r.ReadInt16();
                var dict = new Dictionary<object, object?>();
                for (int i = 0; i < count; i++)
                {
                    object key = keyType == 0 ? ReadValue(r)! : ReadValueOfType(r, keyType)!;
                    object? val = valType == 0 ? ReadValue(r) : ReadValueOfType(r, valType);
                    dict[key] = val;
                }
                return dict;
            }
            case TypeHashtable:
            {
                short count = r.ReadInt16();
                var table = new Dictionary<object, object?>();
                for (int i = 0; i < count; i++)
                {
                    object? key = ReadValue(r);
                    object? val = ReadValue(r);
                    if (key != null) table[key] = val;
                }
                return table;
            }
            case TypeEventData:
                return PhotonMessageParser.ReadEventData(r);
            case TypeOperationRequest:
                return PhotonMessageParser.ReadOperationRequest(r);
            case TypeOperationResponse:
                return PhotonMessageParser.ReadOperationResponse(r);
            case TypeCustom:
                // Tipo customizado do jogo (struct binário próprio) — não decodificamos
                // o conteúdo, só pulamos os bytes para não perder sincronia do stream.
                byte customTypeCode = r.ReadByte();
                short customLen = r.ReadInt16();
                return new UnknownValue(customTypeCode, r.ReadBytesRaw(customLen));
            default:
                // Tipo não mapeado — registramos como desconhecido em vez de derrubar
                // o parser. Ver Network/README.md sobre como calibrar isso.
                return new UnknownValue(type, Array.Empty<byte>());
        }
    }

    public record UnknownValue(byte TypeCode, byte[] Raw);

    public static Dictionary<byte, object?> ReadParameterTable(PhotonReader r)
    {
        short count = r.ReadInt16();
        var dict = new Dictionary<byte, object?>();
        for (int i = 0; i < count; i++)
        {
            byte key = r.ReadByte();
            object? val = ReadValue(r);
            dict[key] = val;
        }
        return dict;
    }
}
