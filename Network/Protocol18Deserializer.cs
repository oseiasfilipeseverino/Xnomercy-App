namespace XnomercyApp.Network;

/// <summary>
/// Decodificador de valores tipados do Photon "Protocol18" — implementação própria.
/// Estrutura confirmada por descrição em prosa de um parser de referência MIT
/// (sem copiar nenhuma linha de código, só os fatos do formato: tamanhos de campo,
/// ordem de leitura e constantes numéricas de protocolo). O algoritmo de inteiro
/// "comprimido" é varint+zigzag — técnica genérica e pública (mesma do Protocol
/// Buffers do Google), não é segredo nem invenção específica do Photon/Albion.
/// </summary>
public static class Protocol18Deserializer
{
    public static object? ReadValue(PhotonReader r)
    {
        byte type = r.ReadByte();
        return ReadValueOfType(r, type);
    }

    public static object? ReadValueOfType(PhotonReader r, byte type)
    {
        switch (type)
        {
            case Protocol18Type.Null: return null;
            case Protocol18Type.BooleanFalse: return false;
            case Protocol18Type.BooleanTrue: return true;
            case Protocol18Type.ShortZero: return (short)0;
            case Protocol18Type.IntZero: return 0;
            case Protocol18Type.LongZero: return 0L;
            case Protocol18Type.FloatZero: return 0f;
            case Protocol18Type.DoubleZero: return 0d;
            case Protocol18Type.ByteZero: return (byte)0;

            case Protocol18Type.Boolean: return r.ReadBool();
            case Protocol18Type.Byte: return r.ReadByte();
            case Protocol18Type.Short: return r.ReadInt16LE();
            case Protocol18Type.Float: return r.ReadSingleLE();
            case Protocol18Type.Double: return r.ReadDoubleLE();
            case Protocol18Type.String: return r.ReadStringP18();

            case Protocol18Type.CompressedInt: return ReadZigZagInt32(r);
            case Protocol18Type.CompressedLong: return ReadZigZagInt64(r);

            case Protocol18Type.Int1: return (int)r.ReadByte();
            case Protocol18Type.Int1Negative: return -(int)r.ReadByte();
            case Protocol18Type.Int2: return (int)r.ReadUInt16LE();
            case Protocol18Type.Int2Negative: return -(int)r.ReadUInt16LE();
            case Protocol18Type.Long1: return (long)r.ReadByte();
            case Protocol18Type.Long1Negative: return -(long)r.ReadByte();
            case Protocol18Type.Long2: return (long)r.ReadUInt16LE();
            case Protocol18Type.Long2Negative: return -(long)r.ReadUInt16LE();

            case Protocol18Type.ByteArray:
            {
                int len = ReadVarUInt32(r);
                return r.ReadBytesRaw(len);
            }
            case Protocol18Type.Array:
            case Protocol18Type.ObjectArray:
            {
                int count = ReadVarUInt32(r);
                byte elementType = type == Protocol18Type.Array ? r.ReadByte() : (byte)0;
                var arr = new object?[count];
                for (int i = 0; i < count; i++)
                    arr[i] = type == Protocol18Type.Array ? ReadValueOfType(r, elementType) : ReadValue(r);
                return arr;
            }
            case Protocol18Type.StringArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new string?[count];
                for (int i = 0; i < count; i++)
                    arr[i] = r.ReadStringP18();
                return arr;
            }
            case Protocol18Type.BooleanArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new bool[count];
                for (int i = 0; i < count; i++) arr[i] = r.ReadBool();
                return arr;
            }
            case Protocol18Type.ShortArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new short[count];
                for (int i = 0; i < count; i++) arr[i] = r.ReadInt16LE();
                return arr;
            }
            case Protocol18Type.FloatArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new float[count];
                for (int i = 0; i < count; i++) arr[i] = r.ReadSingleLE();
                return arr;
            }
            case Protocol18Type.DoubleArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new double[count];
                for (int i = 0; i < count; i++) arr[i] = r.ReadDoubleLE();
                return arr;
            }
            case Protocol18Type.CompressedIntArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new int[count];
                for (int i = 0; i < count; i++) arr[i] = ReadZigZagInt32(r);
                return arr;
            }
            case Protocol18Type.CompressedLongArray:
            {
                int count = ReadVarUInt32(r);
                var arr = new long[count];
                for (int i = 0; i < count; i++) arr[i] = ReadZigZagInt64(r);
                return arr;
            }
            case Protocol18Type.Dictionary:
            {
                byte keyType = r.ReadByte();
                byte valType = r.ReadByte();
                int count = ReadVarUInt32(r);
                var dict = new Dictionary<object, object?>();
                for (int i = 0; i < count; i++)
                {
                    object key = keyType == 0 ? ReadValue(r)! : ReadValueOfType(r, keyType)!;
                    object? val = valType == 0 ? ReadValue(r) : ReadValueOfType(r, valType);
                    dict[key] = val;
                }
                return dict;
            }
            case Protocol18Type.Hashtable:
            {
                int count = ReadVarUInt32(r);
                var table = new Dictionary<object, object?>();
                for (int i = 0; i < count; i++)
                {
                    object? key = ReadValue(r);
                    object? val = ReadValue(r);
                    if (key != null) table[key] = val;
                }
                return table;
            }
            case Protocol18Type.EventData:
                return DeserializeEventData(r);
            case Protocol18Type.OperationRequest:
                return DeserializeOperationRequest(r);
            case Protocol18Type.OperationResponse:
                return DeserializeOperationResponse(r);

            case Protocol18Type.Custom:
            case Protocol18Type.CustomTypeSlim:
                // Tipo customizado do jogo — não temos o catálogo de structs binárias
                // específicas do Albion. Não dá pra saber o tamanho sem ele, então não
                // tentamos pular "no escuro" (arriscaria desalinhar o resto do stream).
                throw new NotSupportedException($"Tipo customizado ({type}) ainda não suportado.");

            default:
                throw new NotSupportedException($"Código de tipo Protocol18 não mapeado: {type}");
        }
    }

    public static PhotonEvent DeserializeEventData(PhotonReader r)
    {
        byte code = r.ReadByte();
        var parameters = ReadParameterTable(r);
        return new PhotonEvent(code, parameters);
    }

    public static PhotonOperationRequest DeserializeOperationRequest(PhotonReader r)
    {
        byte opCode = r.ReadByte();
        var parameters = ReadParameterTable(r);
        return new PhotonOperationRequest(opCode, parameters);
    }

    public static PhotonOperationResponse DeserializeOperationResponse(PhotonReader r)
    {
        byte opCode = r.ReadByte();
        short returnCode = r.ReadInt16LE();
        string? debugMsg = ReadValue(r) as string;
        var parameters = ReadParameterTable(r);
        return new PhotonOperationResponse(opCode, returnCode, debugMsg, parameters);
    }

    // Tabela de parâmetros de Evento/Operação: contador de 1 BYTE (não 2 como no
    // Protocol16 antigo) seguido de pares chave(1 byte) + tipo(1 byte) + valor.
    public static Dictionary<byte, object?> ReadParameterTable(PhotonReader r)
    {
        byte count = r.ReadByte();
        var dict = new Dictionary<byte, object?>();
        for (int i = 0; i < count; i++)
        {
            byte key = r.ReadByte();
            object? val = ReadValue(r);
            dict[key] = val;
        }
        return dict;
    }

    // ── Varint + zigzag (mesmo algoritmo do Protocol Buffers — público e genérico) ──
    private static int ReadVarUInt32(PhotonReader r)
    {
        int result = 0, shift = 0;
        byte b;
        do
        {
            b = r.ReadByte();
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    private static int ReadZigZagInt32(PhotonReader r)
    {
        int raw = ReadVarUInt32(r);
        return (raw >> 1) ^ -(raw & 1);
    }

    private static long ReadZigZagInt64(PhotonReader r)
    {
        long result = 0; int shift = 0;
        byte b;
        do
        {
            b = r.ReadByte();
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return (result >> 1) ^ -(result & 1);
    }
}
