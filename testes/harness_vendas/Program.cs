using XnomercyApp.Network;

// Exercita o MarketMailTracker REAL: lista da caixa de correio + carta aberta,
// nos dois formatos de lista que o SAT ja usou (tipo em [11]/hora em [12], e o
// antigo tipo em [10]/hora em [11]).

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

long T(int d, int h) => new DateTime(2026, 10, d, h, 0, 0, DateTimeKind.Utc).Ticks;

Console.WriteLine("\n-- caixa de correio (formato atual do SAT) e cartas abertas");
var m = new MarketMailTracker();
Checar(m.Handle(new Dictionary<byte, object?>
{
    [0] = new byte[16],
    [3] = new long[] { 501, 502, 503, 504 },
    [7] = new string?[] { "a", "b", "c", "d" },
    [11] = new string?[] { "MARKETPLACE_SELLORDER_FINISHED_SUMMARY", "MARKETPLACE_BUYORDER_FINISHED_SUMMARY", "GUILD_INVITE", "MARKETPLACE_SELLORDER_EXPIRED_SUMMARY" },
    [12] = new long[] { T(17, 20), T(17, 21), T(17, 22), T(18, 1) },
}) == "lista", "lista de cartas reconhecida");
Checar(m.Handle(new Dictionary<byte, object?> { [0] = 501L, [1] = "5|T8_MAIN_MACE@2|1234560000|246912000" }) == "carta",
       "carta de venda aberta reconhecida");
m.Handle(new Dictionary<byte, object?> { [0] = 502L, [1] = "100|T6_PLANKS|5000000|50000" });
var vendas = m.Negocios();
var maca = vendas.First(v => v.CartaId == 501);
Checar(maca.Tipo == MarketMailTracker.Tipo.Venda && maca.Quantidade == 5 && maca.Prata == 123456 && maca.Item == "T8_MAIN_MACE@2",
       $"venda: 5x T8_MAIN_MACE@2 por 123.456 de prata (FixPoint /10.000) ({maca.Prata})");
Checar(m.Total(MarketMailTracker.Tipo.Venda) == 123456 && m.Total(MarketMailTracker.Tipo.Compra) == 500, "totais de venda e compra");
Checar(maca.QuandoUtc == new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc), "horario da carta vem da lista");

Console.WriteLine("\n-- o que NAO entra");
Checar(m.Handle(new Dictionary<byte, object?> { [0] = 503L, [1] = "1|X|1|1" }) == null, "carta que nao e' de mercado (convite) nao entra");
Checar(m.Handle(new Dictionary<byte, object?> { [0] = 999L, [1] = "1|T4_BAG|10000|10000" }) == null, "carta que nao estava na lista nao entra");
Checar(m.Handle(new Dictionary<byte, object?> { [0] = 501L, [1] = "texto sem separador" }) == null, "conteudo sem o formato nao entra");
Checar(m.CartasSemValor() == 0, "venda e compra abertas: nenhuma carta pendente");

Console.WriteLine("\n-- formato antigo da lista (tipo em [10], hora em [11])");
var velho = new MarketMailTracker();
Checar(velho.Handle(new Dictionary<byte, object?>
{
    [3] = new long[] { 7, 8 },
    [10] = new string?[] { "MARKETPLACE_BUYORDER_FINISHED_SUMMARY", "MARKETPLACE_SELLORDER_FINISHED_SUMMARY" },
    [11] = new long[] { T(16, 10), T(16, 11) },
    [12] = new bool[] { false, false },
}) == "lista" && velho.CartasSemValor() == 2, "lista no formato antigo tambem e' lida; 2 cartas ainda fechadas");

Console.WriteLine(falhas == 0 ? "\nOK: aba Vendas" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
