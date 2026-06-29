namespace XnomercyApp.Network;

/// <summary>
/// Timer de fechamento da entrada de dungeon aleatória (90s após entrar) — recurso
/// clássico do Albion, útil pro líder do grupo saber até quando vale esperar
/// retardatário antes da porta fechar.
///
/// Baseado no projeto AlbionOnline-StatisticsAnalysis (Triky313, GPL-3.0):
/// a troca de zona (operação ChangeCluster, real=36 — documentado no próprio código
/// do SAT como "253:36"; ainda NÃO confirmado contra captura real nossa, então só
/// ativa quando o padrão bate, sem quebrar nada se a operação vier diferente) traz no
/// parâmetro [0] uma STRING com o tipo da zona, ex: "@RANDOMDUNGEON@<guid>" pra
/// dungeon aleatória, "@ISLAND@<guid>" pra ilha, etc. Não precisa de nenhum arquivo de
/// dados do jogo — é só checar se a string contém "RANDOMDUNGEON".
///
/// Dungeons têm várias salas conectadas por portal (cada uma dispara um ChangeCluster
/// novo, todas com "RANDOMDUNGEON" na string) — o timer NÃO reinicia a cada sala, só
/// na entrada vinda de fora (mundo aberto/cidade) pra dentro da dungeon, igual o jogo
/// de verdade: a entrada que fecha é a porta no mundo aberto, não as salas internas.
/// </summary>
public static class DungeonTimerTracker
{
    private const int OpChangeCluster = 36;
    private const int DungeonClosesAfterSeconds = 90;

    private static bool _inDungeon;

    /// <summary>Dispara ao entrar numa dungeon aleatória vinda de fora (não a cada sala).</summary>
    public static event Action? EnteredDungeon;

    /// <summary>Dispara ao sair da dungeon (trocou pra qualquer outra zona).</summary>
    public static event Action? LeftDungeon;

    public static void HandleOpResponse(PhotonOperationResponse op)
    {
        if (!op.Parameters.TryGetValue(253, out var rc) || rc is null) return;
        int real;
        try { real = Convert.ToInt32(rc); } catch { return; }
        if (real != OpChangeCluster) return;

        if (!op.Parameters.TryGetValue(0, out var clusterObj) || clusterObj is not string clusterStr) return;

        bool isDungeon = clusterStr.Contains("RANDOMDUNGEON", StringComparison.OrdinalIgnoreCase);

        if (isDungeon && !_inDungeon)
        {
            _inDungeon = true;
            EnteredDungeon?.Invoke();
        }
        else if (!isDungeon && _inDungeon)
        {
            _inDungeon = false;
            LeftDungeon?.Invoke();
        }
    }

    public static TimeSpan CloseDuration => TimeSpan.FromSeconds(DungeonClosesAfterSeconds);
}
