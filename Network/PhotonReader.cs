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

    // ── Little-endian: valores de aplicação do Protocol18 ───────────────────────
    // O Protocol18 do Albion usa little-endian para escalares (diferente do
    // transporte ENet, que é big-endian — por isso métodos separados).
    public short ReadInt16LE()
    {
        short v = (short)(_buf[_pos] | (_buf[_pos + 1] << 8));
        _pos += 2;
        return v;
    }

    public ushort ReadUInt16LE() => (ushort)ReadInt16LE();

    public float ReadSingleLE()
    {
        var bytes = ReadBytesRaw(4);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToSingle(bytes, 0);
    }

    public double ReadDoubleLE()
    {
        var bytes = ReadBytesRaw(8);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToDouble(bytes, 0);
    }

    // String do Protocol18: tamanho em varint (não 2 bytes fixos), depois UTF-8.
    public string ReadStringP18()
    {
        int len = (int)ReadVarUInt32();
        if (len == 0) return string.Empty;
        var bytes = ReadBytesRaw(len);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public uint ReadVarUInt32()
    {
        uint value = 0; int shift = 0;
        while (shift != 35)
        {
            byte cur = ReadByte();
            value |= (uint)(cur & 0x7F) << shift;
            shift += 7;
            if ((cur & 0x80) == 0) break;
        }
        return value;
    }

    public void Skip(int count) => _pos += count;
}
