using System.IO;

namespace XnomercyApp.Network;

/// <summary>
/// Leitor sequencial de bytes big-endian — formato usado pelo protocolo Photon
/// (implementação própria, não copiada de nenhum projeto de terceiros).
/// </summary>
public sealed class PhotonReader
{
    private readonly byte[] _buf;
    private int _pos;

    public PhotonReader(byte[] buffer, int offset = 0)
    {
        _buf = buffer;
        _pos = offset;
    }

    public int Position => _pos;
    public bool HasMore => _pos < _buf.Length;
    public int Remaining => _buf.Length - _pos;

    public byte ReadByte() => _buf[_pos++];

    public bool ReadBool() => ReadByte() != 0;

    public short ReadInt16()
    {
        short v = (short)((_buf[_pos] << 8) | _buf[_pos + 1]);
        _pos += 2;
        return v;
    }

    public ushort ReadUInt16() => (ushort)ReadInt16();

    public int ReadInt32()
    {
        int v = (_buf[_pos] << 24) | (_buf[_pos + 1] << 16) | (_buf[_pos + 2] << 8) | _buf[_pos + 3];
        _pos += 4;
        return v;
    }

    public uint ReadUInt32() => (uint)ReadInt32();

    public long ReadInt64()
    {
        long hi = (uint)ReadInt32();
        long lo = (uint)ReadInt32();
        return (hi << 32) | lo;
    }

    public float ReadSingle()
    {
        var bytes = ReadBytesRaw(4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    public double ReadDouble()
    {
        var bytes = ReadBytesRaw(8);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToDouble(bytes, 0);
    }

    public byte[] ReadBytesRaw(int count)
    {
        // Segunda camada de proteção: nunca aloca/lê além do que o buffer realmente
        // tem, mesmo que algum cálculo upstream tenha saído errado (pacote malformado).
        // Isso evita que um valor de tamanho corrompido vire uma alocação gigante.
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"Leitura de {count} bytes excede o buffer (restam {Remaining}).");

        var result = new byte[count];
        Array.Copy(_buf, _pos, result, 0, count);
        _pos += count;
        return result;
    }

    public string ReadString()
    {
        int len = ReadUInt16();
        if (len == 0) return string.Empty;
        var bytes = ReadBytesRaw(len);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public void Skip(int count) => _pos += count;
}
