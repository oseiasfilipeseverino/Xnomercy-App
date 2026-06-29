namespace XnomercyApp.Network;

public sealed class MobKillEntry
{
    public string Name { get; init; } = "";
    public int Kills { get; set; }
}

/// <summary>
/// Contador de mobs mortos pelo PRÓPRIO jogador (mundo aberto, dungeon, qualquer
/// lugar), com taxa de mortes/hora — inspirado no OpenWorldController do projeto
/// AlbionOnline-StatisticsAnalysis (Triky313, GPL-3.0), mas bem mais simples: usamos
/// o evento MobKilled (166) que JÁ estava calibrado no app (ver GameEventCodes.cs)
/// em vez de inferir morte a partir de HealthUpdate.
///
/// MobKilled (166): [0] e [4]=ObjectId do mob (repetido) [3]=tag do tipo (ex:
/// "@MOB_UNDEAD_ARCHER_STANDARD") [5]=nome de quem deu o último hit. Só contamos
/// quando [5] é o nosso próprio nome (PlayerRegistry.SelfName) — kills de outros
/// jogadores por perto não entram na sua contagem.
/// </summary>
public sealed class OpenWorldKillTracker
{
    private readonly Dictionary<string, MobKillEntry> _byTag = new();
    private readonly object _lock = new();
    private int _totalKills;
    private DateTime _sessionStart = DateTime.Now;

    public event Action? Updated;

    public void HandleEvent(PhotonEvent evt)
    {
        if (evt.EventCode != GameEventCodes.MobKilled) return;
        if (!evt.Parameters.TryGetValue(5, out var killerObj) || killerObj is not string killerName) return;

        string? selfName = PlayerRegistry.SelfName;
        if (string.IsNullOrEmpty(selfName) || killerName != selfName) return;   // não é você quem matou

        string tag = evt.Parameters.TryGetValue(3, out var t) && t is string ts ? ts : "Desconhecido";
        string displayName = FriendlyMobName(tag);

        lock (_lock)
        {
            if (!_byTag.TryGetValue(tag, out var entry))
            {
                entry = new MobKillEntry { Name = displayName };
                _byTag[tag] = entry;
            }
            entry.Kills++;
            _totalKills++;
        }
        Updated?.Invoke();
    }

    public int TotalKills { get { lock (_lock) return _totalKills; } }

    public double KillsPerHour
    {
        get
        {
            double hours = Math.Max((DateTime.Now - _sessionStart).TotalHours, 1.0 / 60); // mín. 1 min, evita número absurdo no 1º kill
            lock (_lock) return _totalKills / hours;
        }
    }

    public IReadOnlyList<(string Name, int Kills, double KillsPerHour)> Snapshot()
    {
        double hours = Math.Max((DateTime.Now - _sessionStart).TotalHours, 1.0 / 60);
        lock (_lock)
        {
            var list = new List<(string, int, double)>(_byTag.Count);
            foreach (var e in _byTag.Values)
                list.Add((e.Name, e.Kills, e.Kills / hours));
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _byTag.Clear();
            _totalKills = 0;
        }
        _sessionStart = DateTime.Now;
        Updated?.Invoke();
    }

    // "@MOB_UNDEAD_ARCHER_STANDARD" -> "Undead Archer Standard"
    private static string FriendlyMobName(string tag)
    {
        var s = tag.TrimStart('@');
        if (s.StartsWith("MOB_", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Length > 0 ? char.ToUpper(parts[i][0]) + parts[i][1..].ToLower() : parts[i];
        return string.Join(' ', parts);
    }
}
