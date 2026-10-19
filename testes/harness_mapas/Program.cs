using System.IO;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita o MapHistory e o MapCatalog REAIS: visitas por zona, runs de dungeon,
// troca de zona pela operacao Join e o historico salvo em disco.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
Dictionary<byte, object?> Join(object mapa) => new() { [0] = 42L, [2] = "gayzaoviadao", [8] = mapa, [253] = (short)2 };

MapCatalog.Carregar("[{\"Index\":\"4000\",\"UniqueName\":\"Fort Sterling\"},{\"Index\":\"1301\",\"UniqueName\":\"Thetford Portal\"}]");
var t0 = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);

Console.WriteLine("\n-- 1. nome e tipo do mapa");
Checar(MapCatalog.Nome("4000") == "Fort Sterling", "indice da lista vira nome (world.json do ao-bin-dumps)");
Checar(MapCatalog.Nome("@RANDOMDUNGEON@fe968505-9771-4653-8ade-29a1bd6ddb56") == "Dungeon", "dungeon instanciada vira pelo tipo");
Checar(MapCatalog.Tipo("@HELLCLUSTER@x") == "Hellgate" && MapCatalog.Tipo("@MISTSDUNGEON@x") == "Dungeon das Brumas"
       && MapCatalog.Tipo("@MISTS@x") == "Brumas", "tipos na ordem do SAT (MISTSDUNGEON antes de MISTS)");
Checar(MapCatalog.Nome("9999") == "Mapa 9999", "indice desconhecido nao some: mostra o numero");

Console.WriteLine("\n-- 2. visitas e run de dungeon");
var h = new MapHistory(null);
Checar(!h.HandleJoin(new Dictionary<byte, object?> { [8] = "4000", [253] = (short)24 }, t0), "outra operacao com [8] nao e' troca de zona");
Checar(h.HandleJoin(Join("4000"), t0), "Join (op 2) com o mapa no [8]");
h.Somar(fama: 1000);                                            // antes da dungeon
h.HandleJoin(Join("@RANDOMDUNGEON@aaa"), t0.AddMinutes(5));
h.Somar(fama: 50_000, prata: 3000, mobs: 10);
h.HandleJoin(Join("@RANDOMDUNGEON@bbb"), t0.AddMinutes(9));    // proxima sala
h.Somar(fama: 20_000, loot: 150_000, itens: 2, mobs: 4);
h.HandleJoin(Join("@RANDOMDUNGEON@bbb"), t0.AddMinutes(10));   // Join repetido na mesma sala
h.HandleJoin(Join("1301"), t0.AddMinutes(20));                 // saiu
h.Somar(fama: 7);
var runs = h.Runs();
Checar(runs.Count == 1, $"uma run ({runs.Count})");
var r = runs[0];
Checar(r.Salas == 2 && r.Fama == 70_000 && r.Prata == 3000 && r.Loot == 150_000 && r.Mobs == 14,
       $"run com 2 salas: fama {r.Fama}, prata {r.Prata}, loot {r.Loot}, mobs {r.Mobs}");
Checar(r.FimUtc == t0.AddMinutes(20), "a run acaba quando sai pro mundo aberto");
var v = h.Visitas();
Checar(v.Count == 4 && v[0].Nome == "Thetford Portal" && v[0].FimUtc == null, $"4 visitas, a atual primeiro ({v.Count}: {v[0].Nome})");
Checar(v[3].Nome == "Fort Sterling" && v[3].Fama == 1000, "a fama de antes ficou em Fort Sterling, fora da run");
Checar(h.MapaAtual == "Thetford Portal", "mapa atual pro menu lateral");

Console.WriteLine("\n-- 3. tipos diferentes seguidos sao runs diferentes");
h.HandleJoin(Join("@CORRUPTEDDUNGEON@c"), t0.AddMinutes(30));
h.HandleJoin(Join("@HELLCLUSTER@d"), t0.AddMinutes(40));
Checar(h.Runs().Count == 3 && h.Runs()[0].Tipo == "Hellgate" && h.Runs()[1].Tipo == "Dungeon Corrompida", "Corrompida e Hellgate separadas");

Console.WriteLine("\n-- 4. salvo em disco");
var arq = Path.Combine(Path.GetTempPath(), $"mapas_teste_{Guid.NewGuid():N}.json");
var h2 = new MapHistory(arq);
h2.HandleJoin(Join("@RANDOMDUNGEON@x"), t0);
h2.Somar(fama: 123);
h2.Salvar();
var h3 = new MapHistory(arq);
Checar(h3.Runs().Count == 1 && h3.Runs()[0].Fama == 123, "reabrindo o app, a run continua la");
Checar(h3.Visitas()[0].FimUtc != null && h3.MapaAtual == null, "visita aberta da sessao anterior volta fechada");
File.Delete(arq);

Console.WriteLine("\n-- 5. timer da dungeon tambem pela Join");
int entrou = 0, saiu = 0;
DungeonTimerTracker.EnteredDungeon += () => entrou++;
DungeonTimerTracker.LeftDungeon += () => saiu++;
DungeonTimerTracker.HandleOpResponse(new PhotonOperationResponse(1, 0, null, Join("@RANDOMDUNGEON@z")));
DungeonTimerTracker.HandleOpResponse(new PhotonOperationResponse(1, 0, null, new() { [0] = "@RANDOMDUNGEON@z", [253] = (short)36 }));
Checar(entrou == 1, $"entrou na dungeon pela Join; a ChangeCluster junto nao dispara de novo ({entrou})");
DungeonTimerTracker.HandleOpResponse(new PhotonOperationResponse(1, 0, null, Join("4000")));
Checar(saiu == 1, "saiu pela Join");

Console.WriteLine("\n-- 6. troca de mapa (op 41) tambem conta");
var h41 = new MapHistory(null);
Checar(h41.HandleJoin(new Dictionary<byte, object?> { [0] = "1311", [253] = (short)41 }, t0), "op 41 com o mapa no [0] (sessao real de 18/10)");
Checar(h41.MapaAtual == "Mapa 1311", $"mapa atual ({h41.MapaAtual})");
Checar(!h41.HandleJoin(new Dictionary<byte, object?> { [0] = 340, [253] = (short)41 }, t0), "op 41 com numero no [0] (e' o PEDIDO, nao a resposta): ignora");
Console.WriteLine(falhas == 0 ? "\nOK: mapas e dungeons" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
