using System.Collections.Concurrent;
using System.Reflection;
using XnomercyApp.Network;

// Exercita o GroupGear e o ItemPowerCatalog REAIS: IP de item pelo items.json
// bruto, IP medio pela regra do jogo, equipamento pelos eventos 29/90, IP real da
// inspecao e o grupo vindo do PartyTracker.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

// Trecho do items.json BRUTO, no mesmo formato do ao-bin-dumps.
ItemPowerCatalog.Carregar("""
{"items":{
 "weapon":[
  {"@uniquename":"T4_MAIN_SWORD","@itempower":"700","@twohanded":"false",
   "enchantments":{"enchantment":[{"@enchantmentlevel":"1","@itempower":"800"},{"@enchantmentlevel":"2","@itempower":"900"}]}},
  {"@uniquename":"T6_2H_AXE_HELL","@itempower":"975","@twohanded":"true",
   "enchantments":{"enchantment":{"@enchantmentlevel":"1","@itempower":"1075"}}}],
 "equipmentitem":[
  {"@uniquename":"T6_HEAD_PLATE_SET1","@itempower":"900"},
  {"@uniquename":"T6_ARMOR_PLATE_SET1","@itempower":"900"},
  {"@uniquename":"T6_SHOES_PLATE_SET1","@itempower":"900"},
  {"@uniquename":"T6_CAPE","@itempower":"900"},
  {"@uniquename":"T6_BAG","@itempower":"900"}],
 "simpleitem":{"@uniquename":"T4_PLANKS"}
}}
""");

Console.WriteLine("\n-- 1. IP de cada item (items.json bruto)");
Checar(ItemPowerCatalog.Ip("T4_MAIN_SWORD") == 700 && ItemPowerCatalog.Ip("T4_MAIN_SWORD@2") == 900, "espada .0 = 700, .2 = 900 (do arquivo)");
Checar(ItemPowerCatalog.Ip("T4_MAIN_SWORD@3") == 1000, "encantamento fora da lista: +100 por nivel");
Checar(ItemPowerCatalog.Ip("T6_2H_AXE_HELL@1") == 1075 && ItemPowerCatalog.DuasMaos("T6_2H_AXE_HELL@1"), "artefato com IP proprio e de duas maos");
Checar(ItemPowerCatalog.Ip("T4_PLANKS") == null && ItemPowerCatalog.Ip(null) == null, "o que nao e' equipamento nao tem IP");

Console.WriteLine("\n-- 2. IP medio pela regra do jogo");
// 2 maos 1075 conta nas duas maos; bolsa fica fora; tudo o resto 900
var slots = new string?[] { "T6_2H_AXE_HELL@1", null, "T6_HEAD_PLATE_SET1", "T6_ARMOR_PLATE_SET1", "T6_SHOES_PLATE_SET1", "T6_BAG", "T6_CAPE" };
Checar(Math.Abs(ItemPowerCatalog.IpMedio(slots)!.Value - (1075 * 2 + 900 * 4) / 6.0) < 0.01, $"(1075x2 + 900x4) / 6 = {ItemPowerCatalog.IpMedio(slots):0.0}");
var semCapa = new string?[] { "T4_MAIN_SWORD", null, "T6_HEAD_PLATE_SET1", null, null, null, null };
Checar(Math.Abs(ItemPowerCatalog.IpMedio(semCapa)!.Value - (700 + 900) / 6.0) < 0.01, "espaco vazio conta zero (igual o SAT)");

Console.WriteLine("\n-- 3. equipamento do grupo pelos eventos");
var cat = (ConcurrentDictionary<int, (string NamePt, string UniqueName)>)typeof(ItemCatalog)
    .GetField("_byIndex", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
cat[10] = ("Machado Infernal", "T6_2H_AXE_HELL@1"); cat[11] = ("Elmo", "T6_HEAD_PLATE_SET1");
cat[12] = ("Armadura", "T6_ARMOR_PLATE_SET1"); cat[13] = ("Botas", "T6_SHOES_PLATE_SET1");
cat[14] = ("Capa", "T6_CAPE"); cat[15] = ("Espada", "T4_MAIN_SWORD");
typeof(PlayerRegistry).GetProperty("SelfName")!.GetSetMethod(true)!.Invoke(null, new object?[] { "gayzaoviadao" });
typeof(PlayerRegistry).GetProperty("SelfObjectId")!.GetSetMethod(true)!.Invoke(null, new object?[] { 1L });
GroupGear.Limpar();
var guid = new byte[16]; guid[0] = 7;
GroupGear.HandleEvent(Ev(29, new() { [0] = 500L, [1] = "LapreoTheKing", [7] = guid,
    [40] = new short[] { 10, 0, 11, 12, 13, 0, 14, 0, 0, 0 } }));
byte[] G(int n) { var b = new byte[16]; b[0] = (byte)n; return b; }
PartyTracker.Limpar();
PartyTracker.Handle(Ev(231, new() { [8] = G(1).Concat(G(2)).Concat(G(3)).ToArray(), [9] = new string?[] { "gayzaoviadao", "LapreoTheKing", "SrAmok" } }));
var grupo = GroupGear.Grupo();
Checar(grupo.Count == 3 && grupo[0].Nome == "gayzaoviadao", $"voce primeiro, depois o grupo ({string.Join(", ", grupo.Select(g => g.Nome))})");
var lapreo = grupo.First(g => g.Nome == "LapreoTheKing");
Checar(lapreo.Arma == "T6_2H_AXE_HELL@1" && Math.Abs(lapreo.IpEquip!.Value - (1075 * 2 + 900 * 4) / 6.0) < 0.01,
       $"Lapreo: machado .1, IP do equipamento {lapreo.IpEquip:0}");
Checar(grupo.First(g => g.Nome == "SrAmok").Fonte.StartsWith("fora de alcance"), "quem nao apareceu na tela: 'fora de alcance'");

GroupGear.HandleEvent(Ev(90, new() { [0] = 500L, [2] = new short[] { 15, 0, 11, 12, 13, 0, 14, 0, 0, 0 } }));
Checar(GroupGear.LinhaDe("LapreoTheKing").Arma == "T4_MAIN_SWORD", "trocou de arma (evento 90): atualiza");

Checar(GroupGear.HandleInspecao(new Dictionary<byte, object?> { [0] = guid, [1] = new short[10], [3] = 1342.5 }),
       "resposta de inspecao reconhecida");
Checar(GroupGear.LinhaDe("LapreoTheKing").IpReal == 1342.5, "IP real da inspecao (com maestria) aparece");
Checar(!GroupGear.HandleInspecao(new Dictionary<byte, object?> { [0] = guid, [1] = new short[10], [3] = 12_000_000L }),
       "numero fora da faixa de IP nao e' inspecao");

Console.WriteLine("\n-- 4. o seu equipamento");
Checar(GroupGear.LinhaDe("gayzaoviadao").Fonte.Contains("ainda nao") || GroupGear.LinhaDe("gayzaoviadao").Fonte.Contains("ainda não"),
       "sem nada: avisa que o jogo ainda nao mostrou");
GroupGear.DefinirMeuEquipamentoPelaApi(new string?[] { "T4_MAIN_SWORD@2", "T6_CAPE", null, null, null, null, null });
Checar(GroupGear.LinhaDe("gayzaoviadao").Arma == "T4_MAIN_SWORD@2" && GroupGear.LinhaDe("gayzaoviadao").Fonte.Contains("API"), "pela API do Albion");
GroupGear.HandleEvent(Ev(90, new() { [0] = 1L, [2] = new short[] { 10, 0, 11, 12, 13, 0, 14, 0, 0, 0 } }));
Checar(GroupGear.LinhaDe("gayzaoviadao").Arma == "T6_2H_AXE_HELL@1" && GroupGear.LinhaDe("gayzaoviadao").Fonte.StartsWith("do jogo"),
       "trocou de item no jogo: o evento vale mais que a API");

Console.WriteLine(falhas == 0 ? "\nOK: grupo e IP" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
