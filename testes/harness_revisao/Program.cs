using System.IO;
using System.Reflection;
using XnomercyApp;
using XnomercyApp.Network;

// Exercita as correcoes da revisao de 18/10/2026 na DLL REAL do app:
// prata liquida, fama, remontagem de fragmentos por conexao, compra na hora
// confirmada pela resposta, saude da captura, privacidade do diagnostico,
// dominio do site e reconhecimento de mob pelo formato.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

// ── 1. Prata liquida e fama ──────────────────────────────────────────────────
Console.WriteLine("\n-- 1. Fama & Prata");
var f = new FameSilverTracker();
// TakeSilver: 100 de prata bruta, 5 de taxa da zona, 10 de taxa da guild (x10.000)
f.HandleEvent(Ev(62, new() { [0] = 1L, [3] = 1_000_000L, [4] = 50_000L, [5] = 100_000L }));
Checar(f.TotalSilver == 85, $"prata entra LIQUIDA: 100 - 5 - 10 = 85 ({f.TotalSilver}; a 1.0.66 mostrava 100)");
// UpdateFame: 100 de fama com Premium e bonus [17]=0,2 -> 150 (o [17] ja vem dentro do [2])
f.HandleEvent(Ev(82, new() { [0] = 1L, [2] = 1_000_000L, [5] = true, [17] = 0.2f }));
Checar(f.TotalFame == 150, $"fama: 100 x 1,5 (Premium) = 150, sem multiplicar o [17] de novo ({f.TotalFame})");

// ── 2. Fragmentos de conexoes diferentes nao se misturam ─────────────────────
Console.WriteLine("\n-- 2. pacote picado: cada conexao remonta o seu");
byte[] Frag(short peer, int challenge, int startSeq, int numero, byte[] mensagem, int tamanhoPedaco)
{
    int offset = numero * tamanhoPedaco;
    var pedaco = mensagem.Skip(offset).Take(tamanhoPedaco).ToArray();
    int total = (mensagem.Length + tamanhoPedaco - 1) / tamanhoPedaco;
    var b = new List<byte>();
    void I16(short v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    void I32(int v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
    I16(peer); b.Add(0); b.Add(1); I32(0); I32(challenge);            // cabecalho do pacote
    b.Add(8); b.Add(0); b.Add(0); b.Add(0); I32(12 + 20 + pedaco.Length); I32(numero + 1);   // comando SendFragment
    I32(startSeq); I32(total); I32(numero); I32(mensagem.Length); I32(offset);
    b.AddRange(pedaco);
    return b.ToArray();
}
EnetPacketParser.Reset();
var msgA = Enumerable.Repeat((byte)0xAA, 20).ToArray();
var msgB = Enumerable.Repeat((byte)0xBB, 20).ToArray();
var saidas = new List<byte[]>();
foreach (var pacote in new[] {
    Frag(1, 111, 5, 0, msgA, 10),      // conexao A, pedaco 1
    Frag(2, 222, 5, 1, msgB, 10),      // conexao B, pedaco 2 (mesma sequencia!)
    Frag(2, 222, 5, 0, msgB, 10),      // conexao B, pedaco 1
    Frag(1, 111, 5, 1, msgA, 10) })    // conexao A, pedaco 2
    saidas.AddRange(EnetPacketParser.ExtractApplicationPayloads(pacote));
Checar(saidas.Count == 2, $"duas mensagens inteiras ({saidas.Count})");
Checar(saidas.Count == 2 && saidas.Any(x => x.SequenceEqual(msgA)) && saidas.Any(x => x.SequenceEqual(msgB)),
       "cada uma so com os proprios bytes (antes A1+B2 fechavam uma mensagem misturada)");

// ── 3. Compra na hora confirmada pela resposta do jogo ───────────────────────
Console.WriteLine("\n-- 3. compra na hora: vale so se o jogo aceitar");
var m = new MarketMailTracker();
string Anuncio(long id, long unit, int qtd) =>
    $"{{\"Id\":{id},\"UnitPriceSilver\":{unit},\"Amount\":{qtd},\"AuctionType\":\"offer\",\"ItemTypeId\":\"T6_PLANKS\"}}";
m.Handle(new Dictionary<byte, object?> { [0] = new string?[] { Anuncio(1, 10_000_000, 50), Anuncio(2, 20_000_000, 50) } });
m.HandleRequest(new Dictionary<byte, object?> { [1] = 5, [2] = 1L, [253] = (short)80 });
Checar(m.Negocios().Count == 1, "o pedido ja aparece na hora");
Checar(m.ConfirmarResposta(80, 7) == "recusada" && m.Negocios().Count == 0, "resposta com erro (ja vendido, sem prata): sai da lista");
m.HandleRequest(new Dictionary<byte, object?> { [1] = 2, [2] = 2L, [253] = (short)80 });
Checar(m.ConfirmarResposta(80, 0) == "confirmada" && m.Total(MarketMailTracker.Tipo.Compra) == 4000, "resposta ok: fica (2 x 2.000)");
Checar(m.ConfirmarResposta(80, 0) == null, "resposta sem pedido esperando nao mexe em nada");
m.HandleRequest(new Dictionary<byte, object?> { [1] = 1, [2] = 2L });   // pedido sem codigo de operacao
Checar(m.Negocios().Count == 2, "pedido sem codigo de operacao conta como antes");

// ── 4. Saude da captura ──────────────────────────────────────────────────────
Console.WriteLine("\n-- 4. saude da captura avisa quando o formato muda");
CaptureHealth.Reset();
string? alertou = null;
CaptureHealth.Alertou += r => alertou = r;
for (int i = 0; i < 5; i++) CaptureHealth.Observar(Ev(6, new() { [0] = 1L, [2] = -10f, [6] = 2L }));
var dano = CaptureHealth.Linhas().First(l => l.Recurso == "Dano e cura");
Checar(!dano.Alerta && dano.Situacao.StartsWith("ok"), $"dano no formato certo: {dano.Situacao}");
Checar(CaptureHealth.Linhas().First(l => l.Recurso == "Loot").Situacao.StartsWith("ainda nao") ||
       CaptureHealth.Linhas().First(l => l.Recurso == "Loot").Situacao.StartsWith("ainda não"), "loot que ainda nao veio nao e' alerta");
for (int i = 0; i < 30; i++) CaptureHealth.Observar(Ev(6, new() { [0] = 1L, [1] = "outra coisa" }));   // patch: o 6 virou outro evento
dano = CaptureHealth.Linhas().First(l => l.Recurso == "Dano e cura");
Checar(dano.Alerta && alertou == "Dano e cura", $"30 eventos 6 fora do formato: alerta ({dano.Situacao})");
CaptureHealth.Marcar("Grupo");
Checar(CaptureHealth.Linhas().First(l => l.Recurso == "Grupo").Vistos == 1, "quem reconhece pelo formato marca direto");

// ── 5. Diagnostico sem chat ──────────────────────────────────────────────────
Console.WriteLine("\n-- 5. diagnostico nao leva chat");
Checar(DiagPrivacidade.EhChat(73) && DiagPrivacidade.EhChat(74) && DiagPrivacidade.EhChat(75) && !DiagPrivacidade.EhChat(29),
       "73/74/75 sao chat; 29 (NewCharacter) nao");
Checar(DiagPrivacidade.Texto("hola alguien q quiera pertenecer a un gremio").StartsWith("<texto omitido"), "frase vira <texto omitido>");
Checar(DiagPrivacidade.Texto("LapreoTheKing") == "LapreoTheKing" && DiagPrivacidade.Texto("@MOB_UNDEAD_ARCHER") == "@MOB_UNDEAD_ARCHER",
       "nome de jogador e tag de mob continuam (e' o que a calibracao usa)");

// ── 6. Dominio do site ───────────────────────────────────────────────────────
Console.WriteLine("\n-- 6. o app so roda script no site de verdade");
Checar(SiteConfig.EhDoSite("https://xnomercy.com/gestao") && SiteConfig.EhDoSite("https://www.xnomercy.com/"), "xnomercy.com e www");
Checar(!SiteConfig.EhDoSite("https://xnomercy.com.golpe.net/x"), "xnomercy.com.golpe.net NAO (passava no StartsWith antigo)");
Checar(!SiteConfig.EhDoSite("http://xnomercy.com/") && !SiteConfig.EhDoSite("https://discord.com/") && !SiteConfig.EhDoSite(null),
       "sem https, outro dominio ou vazio: nao");

// ── 7. Mob pelo formato (pacote real) ────────────────────────────────────────
Console.WriteLine("\n-- 7. NewMob (123) do pacote real marca mob; jogador nao");
string? real = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && real == null; d = d.Parent)
    foreach (var c in new[] { Path.Combine(d.FullName, "pacotes_reais.txt"), Path.Combine(d.FullName, "testes", "pacotes_reais.txt") })
        if (File.Exists(c)) { real = c; break; }
Checar(real != null, "achei testes/pacotes_reais.txt");
if (real != null)
{
    var linha = File.ReadAllLines(real).First(l => l.StartsWith("code=123"));
    var ev = (PhotonEvent)PhotonMessageParser.TryParseApplicationMessage(Convert.FromHexString(linha.Split("  ").Last().Trim()))!;
    long id = PhotonParam.ToLong(ev.Parameters[0])!.Value;
    Checar(!PlayerRegistry.IsMob(id), "antes do evento, nao e' mob");
    PlayerRegistry.HandleEvent(ev);
    Checar(PlayerRegistry.IsMob(id), $"depois do 123 real, #{id} e' mob");
}
var falso = Ev(123, new() { [0] = 777L, [1] = "Fulano", [7] = new float[] { 1, 2 }, [14] = 100f });
PlayerRegistry.HandleEvent(falso);
Checar(!PlayerRegistry.IsMob(777), "123 com NOME no [1] (formato de jogador) nao marca mob");

Console.WriteLine("\n-- 8. mob morto (dados reais de 27/08 a 12/09)");
typeof(PlayerRegistry).GetProperty("SelfName")!.GetSetMethod(true)!.Invoke(null, new object?[] { "LKMAJOR" });
var kills = new OpenWorldKillTracker();
kills.HandleEvent(Ev(166, new() { [0] = 5802L, [1] = 11462549L, [2] = 5750L, [3] = "@MOB_UNDEAD_SOLDIER_CHAMPION", [4] = 5802L, [5] = "LKMAJOR" }));
kills.HandleEvent(Ev(166, new() { [0] = 1008680L, [1] = 46283685L, [2] = 1008680L, [3] = "xXPreedoXx", [4] = 1008680L, [5] = "LKMAJOR" }));
Checar(kills.TotalKills == 1 && kills.Snapshot().Single().Name == "Undead Soldier Champion",
       $"o mob conta; o JOGADOR derrubado (nome no [3]) nao conta ({kills.TotalKills})");

Console.WriteLine("\n-- 9. fama e prata pelo formato se um patch mexer no numero");
var f2 = new FameSilverTracker();
f2.HandleEvent(Ev(83, new() { [0] = 13080L, [1] = 12080356254972L, [2] = 101149216L, [3] = 16248000L, [4] = 68322840L }));
f2.HandleEvent(Ev(63, new() { [0] = 68979L, [1] = 44551702, [2] = 69518L, [3] = 3855000L, [4] = 192750L, [5] = 366225L, [8] = 10000 }));
Checar(f2.TotalFame == 10114 && f2.TotalSilver == 329, $"fama no 83 e prata no 63 (formato real) ainda contam ({f2.TotalFame}/{f2.TotalSilver})");
var f3 = new FameSilverTracker();
f3.HandleEvent(Ev(81, new() { [0] = 1L, [1] = 167961577008L }));                                     // prata total
f3.HandleEvent(Ev(84, new() { [0] = new long[5], [1] = 1L, [2] = 600000L }));                       // fama amarela
f3.HandleEvent(Ev(61, new() { [0] = 1L, [1] = 639234671876950609L, [2] = 639234671884009375L, [3] = 48916L, [4] = 1015, [5] = 1, [6] = 0, [7] = 1, [8] = 26 }));   // coleta
Checar(f3.TotalFame == 0 && f3.TotalSilver == 0, "vizinhos (81 prata total, 84 fama amarela, 61 coleta) nao viram fama/prata");

Console.WriteLine("\n-- 10. filtro 'So minha guild': a sua guild vem do Join [58]");
PlayerRegistry.HandleOpResponse(new PhotonOperationResponse(1, 0, null, new() { [0] = 15280L, [2] = "LKMAJOR", [8] = "1313", [58] = "XNoMercy", [253] = (short)2 }));
Checar(PlayerRegistry.OwnGuild == "XNoMercy", $"guild do proprio jogador lida do jogo, sem API ({PlayerRegistry.OwnGuild})");
PlayerRegistry.HandleEvent(Ev(29, new() { [0] = 9001L, [1] = "Aliado", [8] = "xnomercy", [40] = new short[10] }));
PlayerRegistry.HandleEvent(Ev(29, new() { [0] = 9002L, [1] = "Inimigo", [8] = "Black Order", [40] = new short[10] }));
Checar(PlayerRegistry.MesmaGuild("Aliado"), "mesma guild mesmo com maiusculas diferentes");
Checar(!PlayerRegistry.MesmaGuild("Inimigo") && !PlayerRegistry.MesmaGuild("Desconhecido"), "outra guild e guild desconhecida: fora do filtro");

Console.WriteLine(falhas == 0 ? "\nOK: correcoes da revisao" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
