using System.IO;
using System.Text.Json.Nodes;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

// Teste de regressao com DADO REAL: repassa cada sessao gravada em testes/sessoes/
// (botao "Exportar sessao p/ analise" do app, anonimizada) pela DLL REAL.
// Regras:
//   1. nenhum rastreador pode dar erro com dado real;
//   2. o que foi reconhecido quando a sessao foi gravada continua reconhecido;
//   3. nenhum alerta de "formato estranho" (seria o app lendo errado o proprio dado);
//   4. o arquivo volta do JSON com os mesmos tipos (o parser do Photon distingue
//      short de int, e o app depende disso).
// Sessao nova do jogo = arquivo novo na pasta = teste novo, sem escrever codigo.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

string? pasta = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && pasta == null; d = d.Parent)
{
    var c = Path.Combine(d.FullName, "testes", "sessoes");
    if (Directory.Exists(c)) pasta = c;
    else if (Path.GetFileName(d.FullName) == "sessoes") pasta = d.FullName;
}
Checar(pasta != null, "achei testes/sessoes");
var arquivos = pasta == null ? Array.Empty<string>() : Directory.GetFiles(pasta, "*.json").OrderBy(x => x).ToArray();
Checar(arquivos.Length > 0, $"{arquivos.Length} sessao(oes) gravada(s)");

foreach (var arq in arquivos)
{
    Console.WriteLine($"\n-- {Path.GetFileName(arq)}");
    SessionReplay.ZerarEstado();
    var r = new SessionReplay();
    var gravado = r.Arquivo(arq);
    Checar(r.Erros == 0, $"{r.Eventos} eventos e {r.Operacoes} operacoes repassados sem erro ({r.Erros} erros)");
    var agora = CaptureHealth.Linhas().ToDictionary(l => l.Recurso);
    foreach (var (recurso, vistos) in gravado.Where(g => g.Value > 0 && g.Key != "Você"))
        Checar(agora.TryGetValue(recurso, out var l) && l.Vistos > 0,
               $"{recurso}: reconhecido ao gravar ({vistos}) e continua reconhecido ({(agora.TryGetValue(recurso, out var x) ? x.Vistos : 0)})");
    var alertas = agora.Values.Where(l => l.Alerta).Select(l => l.Recurso).ToList();
    Checar(alertas.Count == 0, "nenhum alerta de formato estranho" + (alertas.Count > 0 ? ": " + string.Join(", ", alertas) : ""));

    // ida e volta do formato: o tipo de cada parametro sobrevive
    var raiz = JsonNode.Parse(File.ReadAllText(arq))!.AsObject();
    int tipos = 0, iguais = 0;
    var anon = new SessaoGravada.Anonimizador();
    foreach (var a in raiz["amostras"]!.AsArray())
        foreach (var kv in a!["p"]!.AsObject())
        {
            tipos++;
            var obj = SessaoGravada.Ler(kv.Value);
            var de_novo = SessaoGravada.Valor(obj, anon);
            if ((string?)de_novo?["t"] == (string?)kv.Value?["t"]) iguais++;
        }
    Checar(tipos == iguais, $"{iguais}/{tipos} parametros voltam do JSON com o mesmo tipo");
}

SessionReplay.ZerarEstado();
Console.WriteLine(falhas == 0 ? "\nOK: sessoes gravadas" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
