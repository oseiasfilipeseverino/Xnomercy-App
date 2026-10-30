// Descoberta de porta nao pode engolir o Join nem os primeiros eventos (29/10/2026).
//
// Com porta ainda nao aprendida (sempre o caso com ExitLag), ProcessarUdp SONDA o
// pacote e depois processa de verdade. A sondagem marcava cada comando como "ja
// visto" no dedup do ExitLag, e a passada de verdade descartava o mesmo comando
// como repetido. Pior: so evento contava como acerto na sondagem, entao um pacote
// so com o Join (op 2, que traz a guild) nunca era processado. Era a causa do
// "So minha guild" sem guild detectada com ExitLag.
//
// Roda a DLL real com pacotes montados a partir de um evento real.
using System.IO;
using XnomercyApp.Network;

Environment.SetEnvironmentVariable("XNOMERCY_DADOS", Path.Combine(Path.GetTempPath(), "xnm_desc_" + Guid.NewGuid().ToString("N")));
int falhas = 0;
void Checar(bool ok, string rotulo)
{
    if (!ok) falhas++;
    Console.WriteLine($"  {(ok ? "ok   " : "FALHA")}  {rotulo}");
}

// Evento real (pacotes_reais.txt linha 1, code=123) - payload de aplicacao Photon
var linha = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "pacotes_reais.txt")).First();
byte[] ev = Convert.FromHexString(linha.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last());
// Resposta de operacao 2 (Join) no [253]
byte[] join = Convert.FromHexString("F30301000008" + "01FD0302");

static byte[] Pacote(int challenge, int seq, params byte[][] payloads)
{
    var ms = new MemoryStream();
    void I16(short v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    void I32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
    I16(7); ms.WriteByte(0); ms.WriteByte((byte)payloads.Length); I32(12345); I32(challenge);
    foreach (var p in payloads)
    {
        ms.WriteByte(6); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(4);
        I32(12 + p.Length); I32(seq++);
        ms.Write(p);
    }
    return ms.ToArray();
}

(int eventos, int joins) Rodar(int src, int dst, IEnumerable<byte[]> pacotes, int offset = 0)
{
    EnetPacketParser.Reset();
    var svc = new PacketCaptureService();
    int e = 0, j = 0;
    svc.EventReceived += _ => e++;
    svc.OpResponseReceived += _ => j++;
    foreach (var pk in pacotes)
        svc.ProcessarUdp(src, dst, offset > 0 ? new byte[offset].Concat(pk).ToArray() : pk);
    return (e, j);
}

Console.WriteLine("\n-- controle: porta padrao 5056");
var r = Rodar(5056, 50000, Enumerable.Range(0, 5).Select(i => Pacote(111, 100 + i * 2, ev)));
Checar(r.eventos == 5, $"5 eventos em 5 pacotes ({r.eventos})");

Console.WriteLine("\n-- porta nova (ExitLag), sem embrulho");
r = Rodar(53812, 19324, Enumerable.Range(0, 5).Select(i => Pacote(222, 100 + i * 2, ev)));
Checar(r.eventos == 5, $"nenhum evento perdido na descoberta ({r.eventos}/5)");

Console.WriteLine("\n-- porta nova com embrulho de 10 bytes");
r = Rodar(53812, 19324, Enumerable.Range(0, 5).Select(i => Pacote(333, 100 + i * 2, ev)), offset: 10);
Checar(r.eventos == 5, $"nenhum evento perdido ({r.eventos}/5)");

Console.WriteLine("\n-- Join chegando antes da porta ser aprendida");
r = Rodar(53812, 19324, new[] { Pacote(9, 10, join), Pacote(9, 20, ev), Pacote(9, 30, ev), Pacote(9, 40, ev) });
Checar(r.joins == 1, $"o Join chega aos assinantes ({r.joins})");
r = Rodar(53812, 19324, new[] { Pacote(9, 10, join, ev) });
Checar(r.joins == 1 && r.eventos == 1, $"Join + evento no mesmo pacote: os dois chegam (join={r.joins}, eventos={r.eventos})");

Console.WriteLine("\n-- a copia do ExitLag continua sendo descartada");
var dup = Pacote(555, 100, ev);
var copia = (byte[])dup.Clone();
copia[7] ^= 0xFF;   // outro timestamp no cabecalho: pacote diferente, mesmo comando
r = Rodar(53812, 19324, new[] { dup, copia, Pacote(555, 102, ev) });
Checar(r.eventos == 2, $"mesmo comando em outro pacote conta uma vez ({r.eventos}/2)");

Console.WriteLine(falhas == 0 ? "\ntudo ok" : $"\n{falhas} FALHA(S)");
return falhas == 0 ? 0 : 1;
