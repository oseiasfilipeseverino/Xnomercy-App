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
    };

    // Operações de alto volume: 24 = movimento do próprio personagem, 22 = idem,
    // 300 = telemetria de hardware, 374 = keepalive.
    private static readonly HashSet<int> IgnoredOpCodes = new() { 22, 24, 300, 374 };

    private const int MaxPerCode = 25;
    private const int MaxTotal = 600;

    private static readonly ConcurrentDictionary<string, int> _perCode = new();
    private static int _total;
    private static readonly object _fileLock = new();

    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    public static void LogEvent(PhotonEvent evt)
    {
        int code = evt.EventCode;
        if (code < 0 || IgnoredEventCodes.Contains(code)) return;
        Write($"evt:{code}", $"code={code} {Dump(evt.Parameters)}");
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

    private static void Write(string bucket, string line)
    {
        if (_total >= MaxTotal) return;
        // Teto por código: garante espaço pros eventos raros mesmo com um código
        // comum disparando sem parar.
        int usados = _perCode.AddOrUpdate(bucket, 1, (_, v) => v + 1);
        if (usados > MaxPerCode) return;

        lock (_fileLock)
        {
            if (_total >= MaxTotal) return;
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
