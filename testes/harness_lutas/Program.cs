using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita o FightHistory REAL (lutas salvas do medidor) e a compatibilidade do
// historico de sessoes com os campos novos.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Golpe(long causador, long alvo, int dano) => new(0, new Dictionary<byte, object?>
    { [0] = alvo, [2] = (float)(-dano), [3] = 1000f, [6] = causador, [252] = (short)6 });

var byId = (ConcurrentDictionary<long, PlayerInfo>)typeof(PlayerRegistry)
    .GetField("_byId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
byId[10] = new PlayerInfo { Name = "Arqueiro" };
byId[11] = new PlayerInfo { Name = "Tanque" };

Console.WriteLine("\n-- 1. a luta guarda ranking, duracao e mapa");
var t = new DamageMeterTracker();
var t0 = new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc);
t.HandleEvent(Golpe(10, 11, 500), t0);
t.HandleEvent(Golpe(10, 11, 300), t0.AddSeconds(2));
t.HandleEvent(Golpe(11, 10, 100), t0.AddSeconds(95));
t.HandleEvent(Golpe(99, 11, 7000), t0.AddSeconds(96));          // #99: sem nome
Checar(t.PrimeiroGolpeUtc == t0 && t.UltimoGolpeUtc == t0.AddSeconds(96), "o medidor sabe o primeiro e o ultimo golpe");
var arq = Path.Combine(Path.GetTempPath(), $"lutas_teste_{Guid.NewGuid():N}.json");
var h = new FightHistory(arq);
var luta = h.Salvar(t.Snapshot(), t.PrimeiroGolpeUtc, t.UltimoGolpeUtc, "Thetford Portal")!;
Checar(luta.Jogadores.Count == 2 && luta.Jogadores[0].Nome == "Arqueiro" && luta.DanoTotal == 900,
       $"so jogadores com nome, do maior pro menor ({string.Join(", ", luta.Jogadores.Select(j => j.Nome + " " + j.Dano))})");
Checar(luta.Duracao() == "1 min 36s" && luta.Titulo.Contains("Thetford Portal"), $"duracao e mapa ({luta.Titulo})");
Checar(luta.Texto().Contains("1. Arqueiro — 800 (88,9%)") || luta.Texto().Contains("1. Arqueiro — 800 (88.9%)"), "texto pra colar no Discord");
Checar(luta.Csv().Split('\n')[1].StartsWith("1;Arqueiro;800;"), "CSV");

Console.WriteLine("\n-- 2. guardado em disco, mais nova primeiro, com limite");
var h2 = new FightHistory(arq);
Checar(h2.Lutas().Count == 1 && h2.Lutas()[0].Jogadores[0].Nome == "Arqueiro", "reabrindo o app, a luta continua la");
Checar(h2.Salvar(new List<DamageMeterEntry> { new() { Name = "#5", Damage = 10 } }, null, null, null) == null, "medidor so com #numero: nada a salvar");
for (int i = 0; i < 35; i++) h2.Salvar(new List<DamageMeterEntry> { new() { Name = $"J{i}", Damage = i + 1 } }, null, null, null);
Checar(h2.Lutas().Count == FightHistory.MaxLutas && h2.Lutas()[0].Jogadores[0].Nome == "J34", $"no maximo {FightHistory.MaxLutas}, a mais nova primeiro");
h2.Apagar(h2.Lutas()[0]);
Checar(new FightHistory(arq).Lutas()[0].Jogadores[0].Nome == "J33", "apagar some do disco tambem");
File.Delete(arq);

Console.WriteLine("\n-- 3. historico de sessoes antigo continua abrindo");
var antigo = "[{\"StartTime\":\"2026-07-30T20:00:00\",\"EndTime\":\"2026-07-30T21:00:00\",\"Fame\":100,\"YellowFame\":0,\"Silver\":5,\"Damage\":9,\"LootItems\":2}]";
var lista = JsonSerializer.Deserialize<List<SessionHistoryEntry>>(antigo)!;
Checar(lista.Count == 1 && lista[0].Fame == 100 && lista[0].LootValue == 0 && lista[0].Dps == 0, "sessao sem os campos novos le com zero");

Console.WriteLine(falhas == 0 ? "\nOK: lutas salvas e sessoes" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
