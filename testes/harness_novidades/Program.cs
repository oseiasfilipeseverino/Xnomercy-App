using XnomercyApp;

// Exercita o Novidades REAL: quais notas aparecem depois de uma atualizacao.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}
var versoes = Novidades.Notas.Select(n => Version.Parse(n.Versao)).ToList();
Checar(versoes.SequenceEqual(versoes.OrderByDescending(v => v)) && versoes.Distinct().Count() == versoes.Count,
       "notas da mais nova pra mais velha, sem repetir versao");
Checar(Novidades.Notas.All(n => n.Itens.Length > 0 && n.Itens.All(i => i.Length > 0)), "toda versao tem pelo menos um item");
var de66 = Novidades.Pendentes("1.0.66", "1.1.1");
Checar(de66.Select(p => p.Versao).SequenceEqual(new[] { "1.1.1", "1.1.0", "1.0.70", "1.0.69", "1.0.68", "1.0.67" }),
       $"quem estava na 1.0.66 ve as 6 versoes que perdeu ({string.Join(", ", de66.Select(p => p.Versao))})");
Checar(Novidades.Pendentes("1.1.1", "1.1.1").Count == 0, "ja viu esta versao: nada");
Checar(Novidades.Pendentes(null, "1.1.1").Select(p => p.Versao).SequenceEqual(new[] { "1.1.1" }), "instalacao nova: so a versao atual");
Checar(Novidades.Pendentes("1.0.68", "1.0.69").Select(p => p.Versao).SequenceEqual(new[] { "1.0.69" }), "nota de versao mais nova que a instalada nao aparece");
Console.WriteLine(falhas == 0 ? "\nOK: o que mudou" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
