using System.Collections.Generic;
using System.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Grupo EXATO, pelos eventos de grupo do próprio jogo (pedido de 18/10/2026).
///
/// Até aqui o grupo era adivinhado pelo rateio de fama (PlayerRegistry.MarcarNoGrupo):
/// só aparecia quando alguém matava algo, e ninguém saía da lista antes de 2 min.
/// O código-fonte do Statistics Analysis Tool (Triky313, GPL-3.0 — a mesma licença
/// deste projeto, que já se baseia nele) mostra os eventos que o jogo manda:
///
///   PartyJoined        [8]=GUIDs (16 bytes cada)  [9]=nomes   — você entrou: lista completa
///   PartyPlayerJoined  [1]=GUID  [2]=nome                      — alguém entrou
///   PartyPlayerLeft    [1]=GUID                                — alguém saiu
///
/// No SAT esses são 230, 232 e 234; no jogo valem SAT + EventOffset.Delta (+1 nos
/// pacotes de 12/10: 231, 233, 235), conferido ao vivo pelo EventOffset. Além do
/// número, o evento tem que ter o formato exato. Os dois juntos: o número sozinho já errou duas vezes
/// neste projeto (o 104 era proximidade, o 182 era movimento — ver GameEventCodes),
/// e o formato sozinho casaria com o CONVITE de grupo (228/229), que também traz
/// GUID + nome — por isso o convite fica fora das faixas.
///
/// Quem consome: PlayerRegistry.IsInParty — com grupo exato ativo, vale ele; sem
/// (app aberto com o grupo já formado, o jogo ainda não mandou a lista), continua o
/// rateio de fama como antes.
/// </summary>
public static class PartyTracker
{
    // Números da lista do SAT; o código vivo é SAT + EventOffset.Delta (calibrado
    // no jogo — ver EventOffset). Em 12/10 o deslocamento era +1: 231..235.
    //
    // Antes (1.0.63) cada evento aceitava os DOIS números (SAT e SAT+1). Com +1
    // confirmado isso era perigoso: o 234 vira "grupo mudou de ordem" (tirava
    // gente do grupo por engano) e o 232 vira "grupo desfeito", que nem era tratado.
    public const int SatJoined = 230, SatDisbanded = 231, SatPlayerJoined = 232, SatPlayerLeft = 234;
    private const int MaxGrupo = 20;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _membros = new();   // guid (hex) -> nome

    /// <summary>Último evento reconhecido — pro diagnóstico e pro teste.</summary>
    public static string UltimoReconhecido { get; private set; } = "";

    public static bool Ativo { get { lock (_lock) return _membros.Count > 0; } }

    public static List<string> Nomes() { lock (_lock) return _membros.Values.ToList(); }

    public static bool Contem(string nome)
    {
        lock (_lock)
            return _membros.Values.Any(n => string.Equals(n, nome, System.StringComparison.OrdinalIgnoreCase));
    }

    public static void Limpar() { lock (_lock) _membros.Clear(); }

    /// <summary>"entrou_no_grupo", "membro_entrou", "membro_saiu" ou null (não é evento de grupo).</summary>
    public static string? Handle(PhotonEvent evt)
    {
        var r = Reconhecer(evt);
        if (r != null) CaptureHealth.Marcar("Grupo");
        return r;
    }

    private static string? Reconhecer(PhotonEvent evt)
    {
        var p = evt.Parameters;
        int code = evt.EventCode;
        // Grupo desfeito: o SAT zera o grupo sem olhar campo nenhum. Aqui, por
        // segurança, só se o evento não trouxer membro (GUID/nome/lista).
        if (code == EventOffset.Live(SatDisbanded)
            && !p.ContainsKey(1) && !p.ContainsKey(2) && !p.ContainsKey(8) && !p.ContainsKey(9))
        {
            lock (_lock) _membros.Clear();
            return UltimoReconhecido = "grupo_desfeito";
        }
        if (code == EventOffset.Live(SatJoined)
            && p.TryGetValue(8, out var g) && p.TryGetValue(9, out var n))
        {
            var guids = Guids(g);
            var nomes = Strings(n);
            if (guids != null && nomes != null && guids.Count == nomes.Count
                && guids.Count is >= 1 and <= MaxGrupo && nomes.All(x => x.Length > 0))
            {
                lock (_lock)
                {
                    _membros.Clear();
                    for (int i = 0; i < guids.Count; i++) _membros[guids[i]] = nomes[i];
                }
                return UltimoReconhecido = "entrou_no_grupo";
            }
        }
        if (code == EventOffset.Live(SatPlayerJoined)
            && p.TryGetValue(1, out var g1) && Guid(g1) is string guid
            && p.TryGetValue(2, out var nm) && nm is string nome && nome.Length > 0)
        {
            lock (_lock)
            {
                if (_membros.Count >= MaxGrupo) return null;   // grupo cheio: não é entrada
                _membros[guid] = nome;
            }
            return UltimoReconhecido = "membro_entrou";
        }
        if (code == EventOffset.Live(SatPlayerLeft)
            && p.TryGetValue(1, out var g2) && Guid(g2) is string saiu
            && !(p.TryGetValue(2, out var x2) && x2 is string))
        {
            lock (_lock)
            {
                if (!_membros.TryGetValue(saiu, out var nomeSaiu)) return null;   // não era do grupo: não é saída
                _membros.Remove(saiu);
                // Quem saiu fui EU: o grupo inteiro deixa de ser meu.
                if (PlayerRegistry.SelfName is string eu
                    && string.Equals(eu, nomeSaiu, System.StringComparison.OrdinalIgnoreCase))
                    _membros.Clear();
            }
            return UltimoReconhecido = "membro_saiu";
        }
        return null;
    }

    private static string? Guid(object? v) =>
        v is byte[] b && b.Length == 16 ? System.Convert.ToHexString(b) : null;

    /// <summary>Lista de GUIDs: um byte[] com 16*N bytes, ou um array de byte[16].</summary>
    private static List<string>? Guids(object? v)
    {
        if (v is byte[] b && b.Length > 0 && b.Length % 16 == 0)
            return Enumerable.Range(0, b.Length / 16)
                .Select(i => System.Convert.ToHexString(b, i * 16, 16)).ToList();
        if (v is object?[] arr)
        {
            var lista = arr.Select(Guid).ToList();
            return lista.All(x => x != null) ? lista.Select(x => x!).ToList() : null;
        }
        return null;
    }

    private static List<string>? Strings(object? v) => v switch
    {
        string?[] s => s.Select(x => x ?? "").ToList(),
        object?[] o when o.All(x => x is string) => o.Select(x => (string)x!).ToList(),
        _ => null
    };
}
