using System.Collections.Concurrent;

namespace XnomercyApp.Network;

/// <summary>
/// Saúde da captura (18/10/2026): para cada parte do app, quando o evento dela foi
/// reconhecido pela última vez — e se um número de evento passou a chegar com outro
/// formato, que é o sinal de que um patch do jogo mexeu na numeração.
///
/// Por que existe: "às vezes o app falha" não tinha como ser respondido de dentro do
/// app. O Loot Log já ficou mudo semanas por um patch (277 virou 279) sem nada na tela
/// dizer que o evento tinha sumido. A ideia vem do EventValidator do SAT, que confere
/// os parâmetros de cada evento contra o formato esperado.
///
/// Dois jeitos de alimentar:
///   - Observar(evt): confere os eventos de NÚMERO FIXO (dano, nomes, fama, prata,
///     loot, mob) contra o formato esperado. Número certo + formato certo = ok;
///     número certo + formato errado = "formato estranho".
///   - Marcar(recurso): quem já reconhece pelo formato (grupo, coleta, vendas,
///     energia) avisa quando reconheceu.
/// </summary>
public static class CaptureHealth
{
    public sealed record Linha(string Recurso, string Situacao, bool Alerta, long Vistos, long Estranhos, DateTime? UltimoUtc);

    // Ordem e textos da tela. O que é "normal não aparecer" (grupo sem grupo, coleta
    // sem coletar) não vira alerta — só fica "ainda não apareceu".
    private static readonly (string Recurso, string Dica)[] Recursos =
    {
        ("Dano e cura",       "aparece em qualquer combate"),
        ("Nomes de jogadores","aparece quando alguém entra na tela"),
        ("Você",              "o app descobre quem é você ao trocar de zona ou ganhar fama/prata"),
        ("Fama",              "aparece ao ganhar fama"),
        ("Prata",             "aparece ao pegar prata"),
        ("Loot",              "aparece quando alguém pega item de um corpo"),
        ("Mobs",              "aparece perto de monstros"),
        ("Mapa",              "aparece ao trocar de zona"),
        ("Grupo",             "aparece ao entrar, sair ou desfazer grupo"),
        ("Coleta",            "aparece ao coletar recurso"),
        ("Vendas",            "aparece ao abrir o mercado ou a caixa de correio"),
        ("Energia da guild",  "aparece ao abrir o registro de energia da guild"),
    };

    private sealed class Contador
    {
        public long Vistos;
        public long Estranhos;
        public long UltimoTicks;   // DateTime.UtcNow.Ticks; 0 = nunca
    }

    private static readonly ConcurrentDictionary<string, Contador> _cont = new();

    /// <summary>Formato estranho só vira alerta depois disso e se for maioria — um
    /// evento solto fora do padrão não é patch.</summary>
    public const int MinEstranhosParaAlerta = 20;

    /// <summary>Disparado (na thread de captura) na primeira vez que um recurso entra em alerta.</summary>
    public static event Action<string>? Alertou;
    private static readonly ConcurrentDictionary<string, byte> _jaAlertou = new();

    public static void Reset()
    {
        _cont.Clear();
        _jaAlertou.Clear();
    }

    public static void Marcar(string recurso) => Somar(recurso, ok: true);

    private static void Somar(string recurso, bool ok)
    {
        var c = _cont.GetOrAdd(recurso, _ => new Contador());
        if (ok)
        {
            Interlocked.Increment(ref c.Vistos);
            Interlocked.Exchange(ref c.UltimoTicks, DateTime.UtcNow.Ticks);
        }
        else
        {
            long estranhos = Interlocked.Increment(ref c.Estranhos);
            if (EmAlerta(Interlocked.Read(ref c.Vistos), estranhos) && _jaAlertou.TryAdd(recurso, 0))
                Alertou?.Invoke(recurso);
        }
    }

    private static bool EmAlerta(long vistos, long estranhos) =>
        estranhos >= MinEstranhosParaAlerta && estranhos > vistos;

    /// <summary>Confere os eventos de número fixo. Chamado pra todo evento decodificado.</summary>
    public static void Observar(PhotonEvent evt)
    {
        var p = evt.Parameters;
        int c = evt.EventCode;
        if (c == GameEventCodes.HealthUpdate)
            Somar("Dano e cura", Num(p, 2) && Num(p, 6));
        else if (c == GameEventCodes.HealthUpdates)
            Somar("Dano e cura", p.TryGetValue(2, out var l) && l is Array);
        else if (c == GameEventCodes.NewCharacter)
            Somar("Nomes de jogadores", Num(p, 0) && p.TryGetValue(1, out var n) && n is string { Length: > 0 });
        else if (c == GameEventCodes.FameGain)
            Somar("Fama", Num(p, 2));
        else if (c == GameEventCodes.SilverTaken)
            Somar("Prata", Num(p, 3));
        else if (c == GameEventCodes.NewMob)
            Somar("Mobs", PlayerRegistry.EhNovoMob(evt));
        else if (c == GameEventCodes.GrabbedLoot)
        {
            // O 279 também chega no formato de rateio (sem item): não é estranho, só
            // não é loot. Só conta como estranho se não tiver nem quem pegou.
            if (p.TryGetValue(2, out var q) && q is string { Length: > 0 }) Somar("Loot", ok: true);
            else Somar("Loot", ok: false);
        }
    }

    private static bool Num(IReadOnlyDictionary<byte, object?> p, byte k) =>
        p.TryGetValue(k, out var v) && PhotonParam.ToDouble(v) != null;

    /// <summary>Retrato pra tela, na ordem de Recursos.</summary>
    public static List<Linha> Linhas()
    {
        var agora = DateTime.UtcNow;
        var lista = new List<Linha>();
        foreach (var (recurso, dica) in Recursos)
        {
            if (recurso == "Você")
            {
                string? nome = PlayerRegistry.SelfName;
                bool sabe = PlayerRegistry.SelfObjectId != null;
                lista.Add(new Linha(recurso,
                    sabe ? (nome != null ? $"identificado: {nome}" : "identificado (nome ainda não veio)")
                         : "ainda não identificado — " + dica,
                    false, sabe ? 1 : 0, 0, null));
                continue;
            }
            _cont.TryGetValue(recurso, out var c);
            long vistos = c == null ? 0 : Interlocked.Read(ref c.Vistos);
            long estranhos = c == null ? 0 : Interlocked.Read(ref c.Estranhos);
            long ticks = c == null ? 0 : Interlocked.Read(ref c.UltimoTicks);
            DateTime? ultimo = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

            string situacao;
            bool alerta = EmAlerta(vistos, estranhos);
            if (alerta)
                situacao = $"ALERTA: {estranhos} eventos com formato diferente do esperado — o jogo pode ter mudado num patch";
            else if (ultimo is DateTime u)
                situacao = $"ok — último há {Tempo(agora - u)} ({vistos} nesta sessão)";
            else
                situacao = "ainda não apareceu — " + dica;
            lista.Add(new Linha(recurso, situacao, alerta, vistos, estranhos, ultimo));
        }
        return lista;
    }

    private static string Tempo(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min"
        : $"{(int)t.TotalHours}h{t.Minutes:00}";
}
