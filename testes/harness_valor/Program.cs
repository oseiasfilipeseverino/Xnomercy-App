using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using XnomercyApp;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita o ItemValues REAL (valor do loot) e a linha do Loot Log que o usa.
// O catalogo de itens e a busca no mercado sao trocados por dados fixos, pra o
// teste nao depender de internet.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

// Catalogo minimo (o real vem do ao-bin-dumps pela internet).
var cat = (ConcurrentDictionary<int, (string NamePt, string UniqueName)>)typeof(ItemCatalog)
    .GetField("_byIndex", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
cat[860] = ("Ensopado Avaloniano", "T4_MEAL_STEW_AVALON");
cat[1001] = ("Espada Larga do Adepto", "T4_MAIN_SWORD");
cat[1002] = ("Tabua de Pinho", "T4_PLANKS");

// Mercado falso: so a espada tem preco.
int consultas = 0;
ItemValues.BuscarMercado = async itens =>
{
    consultas++;
    await Task.Yield();
    return itens.Contains("T4_MAIN_SWORD") ? new Dictionary<string, long> { ["T4_MAIN_SWORD"] = 5000 } : new();
};
ItemValues.Limpar();

// ── 1. Valor que o proprio jogo manda (pacote real de 12/10) ──────────────────
Console.WriteLine("\n-- 1. valor estimado vindo do jogo (evento 32 real)");
string? real = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && real == null; d = d.Parent)
    foreach (var c in new[] { Path.Combine(d.FullName, "pacotes_reais.txt"), Path.Combine(d.FullName, "testes", "pacotes_reais.txt") })
        if (File.Exists(c)) { real = c; break; }
Checar(real != null, "achei testes/pacotes_reais.txt");
var linha32 = File.ReadAllLines(real!).First(l => l.StartsWith("code=32"));
var ev32 = (PhotonEvent)PhotonMessageParser.TryParseApplicationMessage(Convert.FromHexString(linha32.Split("  ").Last().Trim()))!;
Checar(ItemValues.Observar(ev32), "o 32 real foi reconhecido como item com valor");
Checar(ItemValues.Valor(860) == 15902, $"Ensopado Avaloniano T4: 159.022.995 / 10.000 = 15.902 por unidade ({ItemValues.Valor(860)})");
Checar(ItemValues.Fonte(860).Contains("jogo"), "e a dica diz que veio do jogo");

// ── 2. O que NAO e' valor de item ────────────────────────────────────────────
Console.WriteLine("\n-- 2. o que nao vira valor");
Checar(!ItemValues.Observar(Ev(30, new() { [0] = 5L, [1] = 1002, [4] = 50_000L, [5] = "LapreoTheKing" })),
       "30 com NOME no [5] (formato de movimento) nao vira valor");
Checar(!ItemValues.Observar(Ev(32, new() { [0] = 5L, [1] = 99999, [2] = 1, [4] = 50_000L })), "indice que nao existe no catalogo: ignora");
Checar(!ItemValues.Observar(Ev(6, new() { [0] = 5L, [1] = 1002, [2] = 1, [4] = 50_000L })), "outro evento com o mesmo formato: ignora");

// ── 3. Mercado pro que o jogo nao mostrou ────────────────────────────────────
Console.WriteLine("\n-- 3. preco do mercado pro que o jogo ainda nao mostrou");
int mudou = 0;
ItemValues.Mudou += () => Interlocked.Increment(ref mudou);
Checar(ItemValues.Valor(1001) == null, "na primeira vez ainda nao sabe (e pede ao mercado)");
ItemValues.Valor(1001); ItemValues.Valor(1002);
for (int i = 0; i < 50 && ItemValues.Valor(1001) == null; i++) Thread.Sleep(50);
Checar(ItemValues.Valor(1001) == 5000 && mudou > 0, $"a espada ganhou o preco do mercado e avisou a tela ({ItemValues.Valor(1001)})");
Checar(consultas == 1, $"os pedidos da mesma rajada viraram UMA consulta ({consultas})");
Checar(ItemValues.Valor(1002) == null, "item sem preco no mercado continua sem valor (nao inventa)");

// ── 4. Mediana do mercado ignora anuncio absurdo e preco velho ───────────────
Console.WriteLine("\n-- 4. mediana das cidades");
var agora = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
string Linha(string id, string cidade, long preco, int diasAtras) =>
    $"{{\"item_id\":\"{id}\",\"city\":\"{cidade}\",\"quality\":1,\"sell_price_min\":{preco},\"sell_price_min_date\":\"{agora.AddDays(-diasAtras):yyyy-MM-ddTHH:mm:ss}\"}}";
var json = "[" + string.Join(",",
    Linha("T4_BAG", "Martlock", 1000, 0), Linha("T4_BAG", "Lymhurst", 1100, 1), Linha("T4_BAG", "Thetford", 33_000_000, 0),
    Linha("T4_BAG", "Bridgewatch", 900, 2), Linha("T4_BAG", "Caerleon", 5, 30),   // velho: fora
    Linha("T4_CAPE", "Martlock", 0, 0), Linha("T4_BAG", "Black Market", 90_000, 0)) + "]";
var med = ItemValues.MedianaPorItem(JsonDocument.Parse(json).RootElement, agora);
Checar(med["T4_BAG"] == 1050, $"mediana de 900/1000/1100/33mi = 1.050; o anuncio de 33 milhoes nao puxa e o Mercado Negro fica fora ({med["T4_BAG"]})");
Checar(!med.ContainsKey("T4_CAPE"), "preco zero (sem anuncio) nao entra");

// ── 5. A linha do Loot Log ganha o valor ─────────────────────────────────────
Console.WriteLine("\n-- 5. Loot Log: valor = quantidade x preco por unidade");
var parse = typeof(MainWindow).GetMethod("TryParseGrabbedLoot", BindingFlags.NonPublic | BindingFlags.Static)!;
object?[] arg = { Ev(279, new() { [0] = 1L, [1] = "Inimigo", [2] = "SrAmok", [3] = false, [4] = 860, [5] = 3 }), DateTime.Now, null, null, null };
Checar((bool)parse.Invoke(null, arg)!, "loot de jogador reconhecido");
var row = (LootFeedRow)arg[2]!;
Checar(row.Valor == 3 * 15902 && row.ValorTexto.Contains("47.706"), $"3 ensopados = 47.706 ({row.ValorTexto})");
string? avisou = null;
row.PropertyChanged += (_, e) => avisou ??= e.PropertyName;
row.Valor = 1;
Checar(avisou == "Valor", "valor que chega depois avisa a tela");

Console.WriteLine(falhas == 0 ? "\nOK: valor do loot" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
