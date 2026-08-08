using System.Collections.Concurrent;

namespace XnomercyApp.Network;

/// <summary>
/// Log dedicado pra descobrir QUAL evento/operação do Albion carrega a lista de
/// membros do grupo (party roster).
///
/// Por que não dá pra usar o named_events_diag: ele tem teto global de 500 linhas e
/// o chat do jogo (código 73) o inunda — numa captura real vieram dezenas de anúncios
/// de recrutamento de guild, e evento de grupo é raro. O que é raro nunca cabe.
///
/// A diferença aqui é o teto POR CÓDIGO: cada código de evento pode gravar no máximo
/// MaxPerCode linhas. Assim o chat gasta a cota do 73 e para, deixando espaço garantido
/// pros códigos raros — que é exatamente o que a gente precisa ver.
///
/// Também registra OPERAÇÕES (request/response), porque o roster pode vir como
/// resposta de operação e não como evento broadcast.
///
/// Uso: ligar a captura ANTES de formar o grupo, convidar/aceitar com a captura
/// rodando, jogar alguns minutos e mandar o party_diag.txt.
/// </summary>
public static class PartyCalibrationLog
{
    // Códigos que já sabemos o que são e/ou que aparecem em altíssimo volume. Ficam
    // de fora pra não gastar espaço: combate, movimento, loot, chat, info de guild.
    private static readonly HashSet<int> IgnoredEventCodes = new()
    {
        6,    // HealthUpdate (dano/cura) — o mais frequente de todos
        8, 13, 14, 17, 18, 19, 113, 123,   // sync de combate/posição/vida
        29,   // NewCharacter (já calibrado, e é enorme)
        30,   // Move (dispara várias vezes por segundo)
        73,   // Chat — é o que inundava o named_events
        279,  // GrabbedLoot (já calibrado)
        103,  // Info de guild (repetitivo)
        81, 82, 84, 85, 91, 96, 92,        // fama/prata (já calibrados)
        160, 141, 543, 540, 558,           // sync/telemetria de alto volume

        // Acrescentados em 07/08 depois de medir as 3 capturas: cada um destes
        // batia o teto de 25 sozinho, e os três juntos comiam um oitavo do
        // orçamento total do arquivo sem informação nenhuma sobre grupo.
        1, 11, 21, 22, 45, 98, 361, 602,
    };

    // Códigos que NUNCA são cortados, nem pelo teto por código nem pelo total.
    // O evento de grupo é raro por natureza: quem sai do grupo sai uma vez. Se ele
    // dividir orçamento com sync de posição, perde sempre.
    private static readonly HashSet<int> PrioridadeEventos = new()
    {
        104,  // PartyMemberStatus — o único que monta o roster hoje
        182,  // era o "saiu do grupo"; a captura mostrou ser movimento. Fica em
              // observação porque carrega nome e ainda não foi explicado.
    };

    // Operações de alto volume: 24 = movimento do próprio personagem, 22 = idem,
    // 300 = telemetria de hardware, 374 = keepalive.
    private static readonly HashSet<int> IgnoredOpCodes = new() { 22, 24, 300, 374 };

    private const int MaxPerCode = 25;

    // Era 600. Nas três capturas de 07/08 os três arquivos bateram o teto exato
    // (600, 599, 601) — ou seja, o log MORREU no meio da sessão. O party_diag.txt
    // cobre das 22:57:27 às 23:01:59: quatro minutos e meio. Um evento de saída de
    // grupo que aconteça depois disso simplesmente nunca é gravado, e foi por isso
    // que nenhuma das capturas achou o PartyMemberLeft. A 600 linhas ~ 90 KB, 6000
    // dá menos de 1 MB por arquivo — barato pro que resolve.
    private const int MaxTotal = 6000;

    private static readonly ConcurrentDictionary<string, int> _perCode = new();
    private static int _total;
    private static readonly object _fileLock = new();

    /// <summary>
    /// Marca o começo de uma sessão de captura no arquivo.
    /// </summary>
    /// <remarks>
    /// Os arquivos de diagnóstico ACUMULAM entre execuções do app, e até 07/08 não
    /// havia nada separando uma sessão da outra. Analisando depois, era impossível
    /// saber se 55 nomes distintos num arquivo eram 55 pessoas de uma vez (o que
    /// derrubaria a leitura de que o 104 é grupo, já que grupo tem teto de 20) ou
    /// cinco sessões de 11. As duas explicações cabiam no mesmo arquivo, então o
    /// dado não decidia nada. Esta linha é o que torna o arquivo interpretável.
    /// </remarks>
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    public static void IniciarSessao()
    {
        // Zera as cotas: cada sessão tem o orçamento inteiro pra si, senão a segunda
        // captura do dia já nasce sem espaço por causa da primeira.
        _perCode.Clear();
        _total = 0;
        var versao = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "?";
        Write("sessao", $"=== sessao {DateTime.Now:yyyy-MM-dd HH:mm:ss} app {versao} ===");
    }

    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    public static void LogEvent(PhotonEvent evt)
    {
        int code = evt.EventCode;
        if (code < 0 || IgnoredEventCodes.Contains(code)) return;
        Write($"evt:{code}", $"code={code} {Dump(evt.Parameters)}",
              prioridade: PrioridadeEventos.Contains(code));
    }

    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    public static void LogOperation(string kind, Dictionary<byte, object?> parms)
    {
        // O código "real" da operação vem no parâmetro 253 (o OperationCode do
        // header é sempre 1 no Albion) — mesma convenção do resto do app.
        int real = -1;
        if (parms.TryGetValue(253, out var rc) && rc is not null)
        {
            try { real = Convert.ToInt32(rc); } catch { return; }
        }
        if (real < 0 || IgnoredOpCodes.Contains(real)) return;
        Write($"{kind}:{real}", $"{kind} op={real} {Dump(parms)}");
    }

    private static void Write(string bucket, string line, bool prioridade = false)
    {
        // Prioridade passa por cima dos dois tetos: é o que garante que o evento de
        // grupo, que é raro, não seja cortado por sync de posição, que é constante.
        if (!prioridade)
        {
            if (_total >= MaxTotal) return;
            // Teto por código: garante espaço pros eventos raros mesmo com um código
            // comum disparando sem parar.
            int usados = _perCode.AddOrUpdate(bucket, 1, (_, v) => v + 1);
            if (usados > MaxPerCode) return;
        }

        lock (_fileLock)
        {
            if (!prioridade && _total >= MaxTotal) return;
            _total++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "party_diag.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {line}\n");
            }
            catch { /* diagnóstico nunca derruba a captura */ }
        }
    }

    /// <summary>Despeja os parâmetros de forma legível, destacando texto (nome de
    /// jogador é string) e listas — é onde o roster do grupo estaria.</summary>
    private static string Dump(Dictionary<byte, object?> parms)
    {
        var partes = new List<string>();
        foreach (var kv in parms.OrderBy(k => k.Key))
        {
            if (kv.Key == 252 || kv.Key == 253) continue;   // já vai no code=
            partes.Add($"[{kv.Key}]={Val(kv.Value)}");
        }
        return string.Join(' ', partes);
    }

    private static string Val(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        byte[] b when b.Length <= 16 => $"byte[{b.Length}]={Convert.ToHexString(b)}",
        byte[] b => $"byte[{b.Length}]",
        // Listas de string sao o alvo principal: roster de grupo tende a vir assim.
        string[] a => $"str[{a.Length}]={{{string.Join(',', a)}}}",
        object?[] a => $"arr[{a.Length}]={{{string.Join(',', a.Select(x => x?.ToString() ?? "null"))}}}",
        Array a => $"{a.GetType().GetElementType()?.Name}[{a.Length}]={{{string.Join(',', a.Cast<object?>().Select(x => x?.ToString() ?? "null"))}}}",
        System.Collections.IDictionary d => $"dict[{d.Count}]",
        _ => v.ToString() ?? "",
    };
}
