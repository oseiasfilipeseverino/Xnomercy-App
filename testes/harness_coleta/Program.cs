using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Exercita o GatheringTracker REAL (a DLL compilada): evento HarvestFinished (61),
// formato do SAT: [0]=quem [4]=item [5]=normal [6]=bonus coletor [7]=bonus Premium.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

PhotonEvent Coleta(long quem, int item, int normal, int coletor = 0, int premium = 0, int code = 61) => new(1, new()
{
    [0] = quem, [1] = 9L, [2] = 0L, [3] = 777L, [4] = item, [5] = normal, [6] = coletor, [7] = premium, [252] = (short)code
});

const long EU = 4242;
var t = new GatheringTracker();

Console.WriteLine("\n-- as suas coletas");
Checar(t.HandleEvent(Coleta(EU, 1001, 3, coletor: 1, premium: 1), EU), "coleta sua e' contada");
t.HandleEvent(Coleta(EU, 1001, 4), EU);
t.HandleEvent(Coleta(EU, 2002, 2, premium: 2), EU);
var s = t.Snapshot();
var fibra = s.First(l => l.Item == 1001);
Checar(fibra.Total == 9 && fibra.Normal == 7 && fibra.BonusColetor == 1 && fibra.BonusPremium == 1 && fibra.Coletas == 2,
       $"soma normal + bonus por recurso ({fibra.Total} = {fibra.Normal} + {fibra.BonusColetor} + {fibra.BonusPremium})");
Checar(s.Count == 2 && s[0].Item == 1001, "recursos diferentes ficam separados, o maior primeiro");

Console.WriteLine("\n-- o que nao conta");
Checar(!t.HandleEvent(Coleta(5555, 1001, 50), EU) && t.Snapshot().First(l => l.Item == 1001).Total == 9,
       "coleta de OUTRO jogador por perto nao conta");
Checar(!t.HandleEvent(Coleta(EU, 1001, 50, code: 62), EU), "outro evento com formato parecido nao conta");
Checar(!t.HandleEvent(new PhotonEvent(1, new() { [0] = EU, [252] = (short)61 }), EU), "61 sem item/quantidade nao conta");

Console.WriteLine("\n-- ritmo e reinicio");
var umaHoraDepois = t.Inicio!.Value.AddHours(1);
Checar(Math.Abs(t.PorHora(13, umaHoraDepois) - 13) < 0.01, "13 unidades em 1 hora = 13/hora");
t.Reset();
Checar(t.Snapshot().Count == 0 && t.Inicio == null, "reiniciar zera a sessao");

Console.WriteLine(falhas == 0 ? "\nOK: aba Coleta" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
