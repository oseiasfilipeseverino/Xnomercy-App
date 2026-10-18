using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using XnomercyApp.Network;

// Exercita na DLL REAL os quatro recursos vindos do SAT: pesca (GatheringTracker),
// faccao e Might & Favor (FactionTracker), bau e banco (StorageTracker) e perdas
// na morte (DeathLog). Numeros do SAT; a faixa de cima com o deslocamento +1.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }
EventOffset.Reset();   // faixa de cima +1, faixa de baixo 0

var cat = (ConcurrentDictionary<int, (string NamePt, string UniqueName)>)typeof(ItemCatalog)
    .GetField("_byIndex", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
cat[50] = ("Vara de Pesca", "T5_2H_TOOL_FISHINGROD");
cat[51] = ("Truta", "T5_FISH_FRESHWATER_ALL_COMMON");
cat[52] = ("Tabua", "T6_PLANKS");
cat[53] = ("Espada", "T6_MAIN_SWORD@1");
typeof(PlayerRegistry).GetProperty("SelfName")!.GetSetMethod(true)!.Invoke(null, new object?[] { "gayzaoviadao" });
typeof(PlayerRegistry).GetProperty("SelfObjectId")!.GetSetMethod(true)!.Invoke(null, new object?[] { 1L });
var t0 = new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc);

Console.WriteLine("\n-- 1. pesca na aba Coleta");
var g = new GatheringTracker();
PhotonEvent Recompensa(int item, int qtd) => Ev(267, new() { [0] = 1L, [1] = item, [3] = qtd });   // RewardGranted: SAT 266 + 1
Checar(!g.HandleEvent(Recompensa(51, 1), agoraUtc: t0), "recompensa sem estar pescando nao conta (bau, missao...)");
Checar(!g.HandleOpRequest(new Dictionary<byte, object?> { [2] = 52 }, t0), "pedido com outro item no [2] nao e' pesca");
Checar(g.HandleOpRequest(new Dictionary<byte, object?> { [0] = 9L, [2] = 50, [253] = (short)316 }, t0), "jogou a linha: o [2] e' uma vara de pesca");
Checar(g.HandleEvent(Recompensa(51, 2), agoraUtc: t0.AddSeconds(20)), "peixe dentro da janela conta");
g.HandleEvent(Recompensa(51, 1), agoraUtc: t0.AddSeconds(40));
var peixe = g.Snapshot().First();
Checar(peixe.Pesca && peixe.Total == 3 && peixe.Coletas == 2, $"truta: 3 peixes em 2 pescadas ({peixe.Total}/{peixe.Coletas})");
Checar(!g.HandleEvent(Recompensa(51, 1), agoraUtc: t0.AddSeconds(200)), "passou 90s sem jogar a linha: fecha");
g.HandleOpRequest(new Dictionary<byte, object?> { [2] = 50 }, t0.AddSeconds(300));
g.EncerrarPesca();
Checar(!g.HandleEvent(Recompensa(51, 1), agoraUtc: t0.AddSeconds(301)), "trocou de zona: fecha (o inventario e' reenviado)");

Console.WriteLine("\n-- 2. faccao e Might & Favor");
var f = new FactionTracker();
Checar(f.HandleEvent(Ev(85, new() { [2] = (byte)3, [3] = 1_500_000L, [4] = 2_000_000L, [9] = 99_000_000L })), "UpdateCurrency (85)");
Checar(f.HandleEvent(Ev(86, new() { [0] = (byte)3, [1] = 4_000_000L, [3] = 1L })), "UpdateFactionStanding (86)");
Checar(f.HandleEvent(Ev(497, new() { [1] = 300_000L, [4] = 120_000L, [7] = 9_000_000L })), "Might & Favor (SAT 496 + 1)");
var tot = f.Totais();
Checar(tot.Pontos == 200 && tot.Moedas == 150 && tot.Bandeira == 400 && tot.Might == 30 && tot.Favor == 12,
       $"pontos 200, moedas 150, bandeira 400, Might 30, Favor 12 ({tot})");
Checar(!f.HandleEvent(Ev(85, new() { [2] = "texto", [3] = 1L, [4] = 1L })), "85 com texto no lugar da cidade: nao e' faccao");
f.Reset();
Checar(f.Totais().Pontos == 0, "reiniciar zera");

Console.WriteLine("\n-- 3. bau e banco");
var arq = Path.Combine(Path.GetTempPath(), $"baus_teste_{Guid.NewGuid():N}.json");
var s = new StorageTracker(arq);
byte[] G(int n) { var b = new byte[16]; b[0] = (byte)n; return b; }
s.HandleEvent(Ev(414, new() { [0] = 7L, [1] = "abc@3008", [2] = G(1).Concat(G(2)).ToArray(), [3] = new[] { "Recursos", "Armas" } }));   // BankVaultInfo SAT 413 + 1
s.HandleEvent(Ev(32, new() { [0] = 1001L, [1] = 52, [2] = 999 }));
s.HandleEvent(Ev(30, new() { [0] = 1002L, [1] = 53, [2] = 1 }));
s.HandleEvent(Ev(32, new() { [0] = 1003L, [1] = 52, [2] = 1 }));
s.HandleEvent(Ev(99, new() { [0] = 7L, [1] = G(1), [3] = new long[] { 1001, 0, 1003, 1002 } }));     // abriu a aba "Recursos"
var b0 = s.Baus().FirstOrDefault();
Checar(b0 != null && b0.Nome == "Recursos" && b0.Lugar == "Mapa 3008",
       $"aba reconhecida pelo GUID, com o lugar ({b0?.Nome} em {b0?.Lugar})");
Checar(b0 != null && b0.Itens.First(i => i.Indice == 52).Quantidade == 1000 && b0.Itens.Any(i => i.Unico == "T6_MAIN_SWORD@1"),
       "itens somados por tipo: 999 + 1 tabuas e a espada");
s.HandleEvent(Ev(99, new() { [0] = 1L, [1] = G(9), [3] = new long[] { 1001 } }));
Checar(s.Baus().Count == 1, "o SEU inventario nao entra");
Checar(new StorageTracker(arq).Baus().Count == 1, "salvo em disco: reabrindo o app o banco continua la");
s.Apagar(b0!.Guid);
Checar(s.Baus().Count == 0, "esquecer apaga");
File.Delete(arq);

Console.WriteLine("\n-- 4. perdas na morte");
var arqM = Path.Combine(Path.GetTempPath(), $"mortes_teste_{Guid.NewGuid():N}.json");
var m = new DeathLog(arqM);
Checar(!m.Registrar("OutroCara", "Inimigo", "", 52, 10, 100, "Thetford", t0), "loot do corpo de OUTRO nao e' perda sua");
Checar(m.Registrar("gayzaoviadao", "Inimigo", "Black Order", 53, 1, 250_000, "Thetford", t0), "pegaram a espada do seu corpo");
m.Registrar("gayzaoviadao", "Inimigo2", "", 52, 100, null, "Thetford", t0.AddMinutes(1));
Checar(!m.Registrar("gayzaoviadao", "gayzaoviadao", "", 52, 1, 5, "Thetford", t0.AddMinutes(2)), "voce pegando de volta nao e' perda");
m.Registrar("gayzaoviadao", "Inimigo3", "", 52, 5, 50, "Martlock", t0.AddMinutes(30));            // morreu de novo
var mortes = m.Mortes();
Checar(mortes.Count == 2 && mortes[1].Itens.Count == 2 && mortes[1].Valor == 250_000 && mortes[0].Mapa == "Martlock",
       $"duas mortes (intervalo de 30 min); a primeira com 2 itens ({mortes.Count}, {mortes.LastOrDefault()?.Itens.Count})");
Checar(new DeathLog(arqM).Mortes().Count == 2, "salvo em disco");
File.Delete(arqM);

EventOffset.Reset();
Console.WriteLine(falhas == 0 ? "\nOK: pesca, faccao, bau e banco, perdas" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
