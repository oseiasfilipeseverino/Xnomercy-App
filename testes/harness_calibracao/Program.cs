using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using XnomercyApp.Network;

// Exercita a calibracao automatica dos numeros BAIXOS (EventOffset) e o Leave
// (PlayerRegistry/GroupGear) na DLL REAL. Simula um patch que insere um evento no
// comeco da lista: todos os numeros dali pra frente andam +1.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }
PhotonEvent Golpes(int code) => Ev(code, new() { [0] = 900L, [2] = new float[] { -100f, -50f }, [3] = new float[] { 900f, 850f }, [6] = new short[] { 31, 31 }, [7] = new short[] { 1, 1 } });
PhotonEvent Jogador(int code, long id, string nome) => Ev(code, new() { [0] = id, [1] = nome, [8] = "XNoMercy", [40] = new short[] { 1, 0, 2, 3, 4, 0, 5, 0, 0, 0 } });
EventOffset.Reset();

Console.WriteLine("\n-- 1. pacotes reais de 12/10 confirmam os numeros de hoje");
string? real = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && real == null; d = d.Parent)
    foreach (var c in new[] { Path.Combine(d.FullName, "pacotes_reais.txt"), Path.Combine(d.FullName, "testes", "pacotes_reais.txt") })
        if (File.Exists(c)) { real = c; break; }
foreach (var l in File.ReadAllLines(real!))
    if (PhotonMessageParser.TryParseApplicationMessage(Convert.FromHexString(l.Split("  ").Last().Trim())) is PhotonEvent e) EventOffset.Observe(e);
Checar(EventOffset.DeltaDano == 0 && GameEventCodes.HealthUpdates == 7 && GameEventCodes.HealthUpdate == 6, "faixa do dano: 6 e 7, como no SAT");

Console.WriteLine("\n-- 2. patch desloca a faixa do dano em +1");
EventOffset.Observe(Golpes(8));
EventOffset.Observe(Golpes(8));
Checar(GameEventCodes.HealthUpdates == 7, "2 vistos ainda nao trocam (evento solto nao engana)");
EventOffset.Observe(Golpes(8));
Checar(EventOffset.DeltaDano == 1 && GameEventCodes.HealthUpdates == 8 && GameEventCodes.HealthUpdate == 7 && GameEventCodes.Leave == 2,
       $"3 vistos: golpes agrupados em 8, golpe em 7, saida em 2 ({GameEventCodes.HealthUpdates}/{GameEventCodes.HealthUpdate}/{GameEventCodes.Leave})");
var reg = typeof(PlayerRegistry);
var byId = (ConcurrentDictionary<long, PlayerInfo>)reg.GetField("_byId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
byId[31] = new PlayerInfo { Name = "Arqueiro" };
var t = new DamageMeterTracker();
t.HandleEvent(Golpes(8));
Checar(t.Snapshot().FirstOrDefault(x => x.Name == "Arqueiro")?.Damage == 150, "o medidor segue o numero novo sozinho (150 de dano)");
Checar(GameEventCodes.NewCharacter == 29, "a outra faixa nao mexe");

Console.WriteLine("\n-- 3. patch desloca a faixa de nomes/fama/prata em +1");
for (int i = 0; i < 3; i++) EventOffset.Observe(Jogador(30, 500 + i, "Fulano" + i));
Checar(EventOffset.DeltaNomes == 1 && GameEventCodes.NewCharacter == 30 && GameEventCodes.FameGain == 83
       && GameEventCodes.SilverTaken == 63 && GameEventCodes.NewMob == 124 && GameEventCodes.HarvestFinished == 62
       && GatheringTracker.HarvestFinished == 62 && GameEventCodes.LootPickup == 33,
       $"nomes 30, fama 83, prata 63, mob 124, coleta 62, item 33");
Checar(!EventOffset.Observe(Ev(30, new() { [0] = 1L, [1] = "frase com espaco", [40] = new short[10] })).HasValue, "texto com espaco nao e' nome de jogador");

Console.WriteLine("\n-- 4. volta quando o jogo volta");
for (int i = 0; i < 3; i++) EventOffset.Observe(Golpes(7));
for (int i = 0; i < 3; i++) EventOffset.Observe(Jogador(29, 600 + i, "Beltrano" + i));
Checar(GameEventCodes.HealthUpdates == 7 && GameEventCodes.NewCharacter == 29 && GameEventCodes.FameGain == 82, "numeros originais de novo");

Console.WriteLine("\n-- 5. Leave: quem sai da tela e' esquecido pelo numero");
typeof(PlayerRegistry).GetProperty("SelfObjectId")!.GetSetMethod(true)!.Invoke(null, new object?[] { 1L });
PlayerRegistry.HandleEvent(Jogador(29, 700, "Saidor"));
GroupGear.HandleEvent(Jogador(29, 700, "Saidor"));
Checar(PlayerRegistry.NameOf(700) == "Saidor", "antes: 700 e' o Saidor");
PlayerRegistry.HandleEvent(Ev(1, new() { [0] = 700L }));
Checar(PlayerRegistry.NameOf(700) == "#700", "depois do Leave, o 700 fica livre (o proximo dono nao herda o nome)");
Checar(GroupGear.LinhaDe("Saidor").Fonte.StartsWith("visto antes") && GroupGear.ArmaPorNome("Saidor") != null || GroupGear.LinhaDe("Saidor").Fonte.StartsWith("visto antes"),
       $"o equipamento continua pelo nome ({GroupGear.LinhaDe("Saidor").Fonte})");
var reg2 = typeof(PlayerRegistry).GetMethod("RegisterMob", BindingFlags.NonPublic | BindingFlags.Static)!;
reg2.Invoke(null, new object?[] { 800L });
PlayerRegistry.HandleEvent(Ev(1, new() { [0] = 800L }));
Checar(!PlayerRegistry.IsMob(800), "mob que saiu tambem e' esquecido (o numero pode virar jogador)");
byId[1] = new PlayerInfo { Name = "Eu" };
PlayerRegistry.HandleEvent(Ev(1, new() { [0] = 1L }));
Checar(PlayerRegistry.NameOf(1) == "Você", "voce nunca e' esquecido");
PlayerRegistry.HandleEvent(Jogador(29, 701, "Fica"));
PlayerRegistry.HandleEvent(Ev(1, new() { [0] = 701L, [1] = 5, [2] = 6, [3] = 7, [4] = 8 }));
Checar(PlayerRegistry.NameOf(701) == "Fica", "evento 1 com muitos campos nao e' Leave");

EventOffset.Reset();
Console.WriteLine(falhas == 0 ? "\nOK: calibracao dos numeros baixos e Leave" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
