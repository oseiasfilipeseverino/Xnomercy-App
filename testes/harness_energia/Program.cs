using XnomercyApp;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita o GuildEnergyLog REAL (a DLL compilada): a resposta do jogo quando o
// oficial abre o registro de energia, no formato que o SAT (Triky313) documenta
// em GetGuildAccountLogsResponse: [0]=nomes [3]=quantidades FixPoint [4]=ticks UTC.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

long Ticks(int a, int m, int d, int h, int mi, int s, int ms = 0) =>
    new DateTime(a, m, d, h, mi, s, ms, DateTimeKind.Utc).Ticks;

Console.WriteLine("\n-- o registro de energia como o jogo manda");
var resp = new Dictionary<byte, object?>
{
    [0] = new string?[] { "SrAmok", "Criminouso" },
    [3] = new long[] { 1_500_000, -400_000 },                                  // 150 e -40 (FixPoint × 10.000)
    [4] = new long[] { Ticks(2026, 10, 18, 2, 22, 56, 731), Ticks(2026, 10, 18, 2, 20, 51) },
};
var lista = GuildEnergyLog.Ler(resp);
Checar(lista != null && lista.Count == 2, "reconhece o registro (3 listas do mesmo tamanho)");
var amok = lista!.First(e => e.Jogador == "SrAmok");
Checar(amok.Quantidade == 150 && lista.First(e => e.Jogador == "Criminouso").Quantidade == -40,
       "quantidade sai do FixPoint (÷10.000), retirada negativa");
Checar(amok.QuandoUtc == new DateTime(2026, 10, 18, 2, 22, 56, DateTimeKind.Utc),
       "horario em UTC sem os milissegundos (igual ao da planilha do jogo)");
var json = GuildEnergyLog.ParaJson(lista);
Checar(json.Contains("\"data\":\"2026-10-18 02:22:56\"") && json.Contains("\"jogador\":\"SrAmok\"") && json.Contains("\"quantidade\":150"),
       "JSON no formato que o site espera");
Checar(lista[0].Jogador == "Criminouso", "ordenado do mais antigo pro mais novo");

Console.WriteLine("\n-- o que NAO e' o registro de energia");
Checar(GuildEnergyLog.Ler(new Dictionary<byte, object?> { [0] = new string?[] { "A1", "B2" }, [3] = new long[] { 1 }, [4] = new long[] { Ticks(2026, 1, 1, 0, 0, 0), 0 } }) == null,
       "listas de tamanhos diferentes: ignora");
Checar(GuildEnergyLog.Ler(new Dictionary<byte, object?> { [0] = new string?[] { "SrAmok" }, [3] = new long[] { 10 }, [4] = new long[] { 1234 } }) == null,
       "horario impossivel (nao e' tick de data): ignora");
Checar(GuildEnergyLog.Ler(new Dictionary<byte, object?> { [0] = "SrAmok", [3] = 10L, [4] = 5L }) == null,
       "valores soltos em vez de listas: ignora");

Console.WriteLine("\n-- o que o oficial le depois de enviar");
Checar(MainWindow.ResumoEnvioEnergia("{\"status\":200,\"novos\":2,\"repetidos\":5,\"fora_da_guild\":1}", out var ok) ==
       "Enviado: 2 novo(s), 5 já estavam no site, 1 fora da guild." && ok, "sucesso: diz quantos novos e repetidos");
Checar(MainWindow.ResumoEnvioEnergia("{\"status\":403}", out ok).Contains("Só a gestão") && !ok, "membro comum: explica que e' so gestao");
Checar(MainWindow.ResumoEnvioEnergia("{\"erro\":\"login\"}", out ok).Contains("login") && !ok, "deslogado: pede login");
Checar(!string.IsNullOrEmpty(MainWindow.ResumoEnvioEnergia("", out ok)) && !ok, "site sem resposta: avisa, nao some");

Console.WriteLine("\n-- envio automatico so com a aba de ENERGIA confirmada");
bool? confirmada = null;
Action<List<GuildEnergyLog.Entrada>, bool> ouvir = (_, c) => confirmada = c;
GuildEnergyLog.Lido += ouvir;
var respEnergia = new PhotonOperationResponse(1, 0, null, new() { [0] = new string?[] { "SrAmok" }, [3] = new long[] { -100000 }, [4] = new long[] { Ticks(2026, 10, 18, 20, 0, 0) }, [253] = (short)159 });
GuildEnergyLog.HandleOpResponse(respEnergia);
Checar(confirmada == false, "resposta sem ver o pedido da aba: NAO vai sozinha (pode ser a aba de prata)");
GuildEnergyLog.Pedido(new Dictionary<byte, object?> { [1] = (byte)2, [2] = 0, [3] = 101, [253] = (short)159 });
GuildEnergyLog.HandleOpResponse(respEnergia);
Checar(confirmada == true, "pedido da aba 2 (energia) visto: vai sozinha");
GuildEnergyLog.Pedido(new Dictionary<byte, object?> { [1] = (byte)0, [2] = 0, [3] = 101, [253] = (short)159 });
confirmada = null;
GuildEnergyLog.HandleOpResponse(respEnergia);
Checar(confirmada == null, "pedido de OUTRA aba: a resposta nem e' lida como energia");
GuildEnergyLog.Lido -= ouvir;

Console.WriteLine(falhas == 0 ? "\nOK: registro de energia da guild" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
