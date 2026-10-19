using System.IO;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita a captura SEM Npcap REAL: o datagrama IP cru (como o raw socket do
// Windows entrega) vira UDP e segue o mesmo caminho do Npcap ate virar evento.
// Um evento de dano REAL (testes/pacotes_reais.txt) e' embrulhado em ENet + UDP + IP.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

byte[] Ip(byte protocolo, int src, int dst, byte[] dados, int flagsFrag = 0)
{
    int udpLen = 8 + dados.Length, total = 20 + udpLen;
    var b = new List<byte> { 0x45, 0, (byte)(total >> 8), (byte)total, 0, 1, (byte)(flagsFrag >> 8), (byte)flagsFrag, 64, protocolo, 0, 0,
                             192, 168, 0, 10, 5, 188, 125, 1 };
    b.AddRange(new[] { (byte)(src >> 8), (byte)src, (byte)(dst >> 8), (byte)dst, (byte)(udpLen >> 8), (byte)udpLen, (byte)0, (byte)0 });
    b.AddRange(dados);
    return b.ToArray();
}

Console.WriteLine("\n-- 1. datagrama IP cru -> UDP");
var conteudo = new byte[] { 1, 2, 3, 4, 5 };
Checar(PacketCaptureService.ExtrairUdpDeIpv4(Ip(17, 5056, 50123, conteudo), out int s, out int d, out var p)
       && s == 5056 && d == 50123 && p.SequenceEqual(conteudo), $"portas {s}->{d} e conteudo certos");
var comLixo = Ip(17, 5056, 1, conteudo).Concat(new byte[] { 9, 9, 9 }).ToArray();   // buffer maior que o datagrama
Checar(PacketCaptureService.ExtrairUdpDeIpv4(comLixo, out _, out _, out var p2) && p2.SequenceEqual(conteudo), "sobra no buffer nao entra no conteudo");
Checar(!PacketCaptureService.ExtrairUdpDeIpv4(Ip(6, 5056, 1, conteudo), out _, out _, out _), "TCP (protocolo 6) nao e' UDP");
Checar(!PacketCaptureService.ExtrairUdpDeIpv4(Ip(17, 5056, 1, conteudo, flagsFrag: 0x2000), out _, out _, out _), "fragmento de IP e' descartado");
Checar(!PacketCaptureService.ExtrairUdpDeIpv4(new byte[12], out _, out _, out _), "pedaco curto demais: descarta");

Console.WriteLine("\n-- 2. evento REAL de dano pelo caminho inteiro (IP -> UDP -> ENet -> Photon)");
string? real = null;
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && real == null; dir = dir.Parent)
    foreach (var c in new[] { Path.Combine(dir.FullName, "pacotes_reais.txt"), Path.Combine(dir.FullName, "testes", "pacotes_reais.txt") })
        if (File.Exists(c)) { real = c; break; }
var app = Convert.FromHexString(File.ReadAllLines(real!).First(l => l.StartsWith("code=6")).Split("  ").Last().Trim());
var enet = new List<byte> { 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 7 };                       // peer, flags, 1 comando, hora, challenge
int len = 12 + app.Length;
enet.AddRange(new byte[] { 6, 0, 0, 0, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len, 0, 0, 0, 1 });   // SendReliable
enet.AddRange(app);
var cap = new PacketCaptureService();
var recebidos = new List<PhotonEvent>();
cap.EventReceived += e => recebidos.Add(e);
Checar(PacketCaptureService.ExtrairUdpDeIpv4(Ip(17, 5056, 50123, enet.ToArray()), out s, out d, out var udp), "o datagrama do jogo vira UDP");
cap.ProcessarUdp(s, d, udp);
Checar(recebidos.Count == 1 && recebidos[0].EventCode == 6, $"chegou o evento 6 (dano) decodificado ({recebidos.Count}: {recebidos.FirstOrDefault()?.EventCode})");

Console.WriteLine("\n-- 3. abrir o raw socket de verdade (precisa de administrador)");
bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
var viva = new PacketCaptureService();
bool abriu = viva.StartSemNpcap();
Console.WriteLine($"  info   administrador: {admin}; abriu: {abriu}; modo sem Npcap: {viva.ModoSemNpcap}");
if (admin) Checar(abriu && viva.ModoSemNpcap, "como administrador, o modo sem Npcap abre");
else Checar(!abriu && !viva.ModoSemNpcap, "sem administrador nao abre — e diz que nao abriu, sem fingir");
viva.Stop();
Checar(!viva.ModoSemNpcap, "parar desliga o modo");

Console.WriteLine(falhas == 0 ? "\nOK: captura sem Npcap" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
