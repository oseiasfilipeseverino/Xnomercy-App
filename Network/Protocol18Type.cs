namespace XnomercyApp.Network;

/// <summary>
/// Códigos de tipo do Photon "Protocol18" — versão mais compacta do protocolo que o
/// Albion realmente usa (diferente do "Protocol16" mais antigo, que usa letras ASCII
/// como código de tipo). Aqui os códigos são números pequenos, e há atalhos especiais
/// pra valores comuns (zero, booleano) que não gastam bytes extras de payload, além de
/// inteiros "comprimidos" em formato varint+zigzag (mesma técnica do Protocol Buffers
/// do Google — algoritmo genérico e público, não específico do Photon/Albion).
/// </summary>
public static class Protocol18Type
{
    public const byte Boolean = 2;
    public const byte Byte = 3;
    public const byte Short = 4;
    public const byte Float = 5;
    public const byte Double = 6;
    public const byte String = 7;
    public const byte Null = 8;
    public const byte CompressedInt = 9;
    public const byte CompressedLong = 10;
    public const byte Int1 = 11;
    public const byte Int1Negative = 12;
    public const byte Int2 = 13;
    public const byte Int2Negative = 14;
    public const byte Long1 = 15;
    public const byte Long1Negative = 16;
    public const byte Long2 = 17;
    public const byte Long2Negative = 18;
    public const byte Custom = 19;
    public const byte Dictionary = 20;
    public const byte Hashtable = 21;
    public const byte ObjectArray = 23;
    public const byte OperationRequest = 24;
    public const byte OperationResponse = 25;
    public const byte EventData = 26;
    public const byte BooleanFalse = 27;
    public const byte BooleanTrue = 28;
    public const byte ShortZero = 29;
    public const byte IntZero = 30;
    public const byte LongZero = 31;
    public const byte FloatZero = 32;
    public const byte DoubleZero = 33;
    public const byte ByteZero = 34;
    public const byte Array = 64;
    public const byte BooleanArray = 66;
    public const byte ByteArray = 67;
    public const byte ShortArray = 68;
    public const byte FloatArray = 69;
    public const byte DoubleArray = 70;
    public const byte StringArray = 71;
    public const byte CompressedIntArray = 73;
    public const byte CompressedLongArray = 74;
    public const byte CustomTypeArray = 83;
    public const byte DictionaryArray = 84;
    public const byte HashtableArray = 85;
    public const byte CustomTypeSlim = 128;
}
