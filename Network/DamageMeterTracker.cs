namespace XnomercyApp.Network;

public sealed class DamageMeterEntry
{
    public string PlayerKey { get; init; } = "";
    public long Damage { get; set; }
    public long Healing { get; set; }
}

/// <summary>
/// Ranking de dano/cura por jogador durante o evento PvE atual. Mesmo aviso do
/// FameSilverTracker: só agrega de fato depois que GameEventCodes.DamageDealt /
/// HealingDone forem calibrados.
/// </summary>
public sealed class DamageMeterTracker
{
    private readonly Dictionary<string, DamageMeterEntry> _entries = new();

    public IReadOnlyCollection<DamageMeterEntry> Entries => _entries.Values;
    public event Action? Updated;

    public void HandleEvent(PhotonEvent evt)
    {
        bool isDamage = evt.Code == GameEventCodes.DamageDealt && GameEventCodes.IsCalibrated(GameEventCodes.DamageDealt);
        bool isHealing = evt.Code == GameEventCodes.HealingDone && GameEventCodes.IsCalibrated(GameEventCodes.HealingDone);
        if (!isDamage && !isHealing) return;

        // Chave do jogador causador e o valor numérico também dependem de qual
        // posição do parâmetro o Albion usa — parte da calibração pendente.
        string playerKey = evt.Parameters.Count > 0 ? evt.Parameters.Keys.First().ToString()! : "?";
        if (!TryGetLong(evt, out long amount)) return;

        if (!_entries.TryGetValue(playerKey, out var entry))
        {
            entry = new DamageMeterEntry { PlayerKey = playerKey };
            _entries[playerKey] = entry;
        }

        if (isDamage) entry.Damage += amount;
        if (isHealing) entry.Healing += amount;
        Updated?.Invoke();
    }

    public void Reset()
    {
        _entries.Clear();
        Updated?.Invoke();
    }

    private static bool TryGetLong(PhotonEvent evt, out long value)
    {
        foreach (var v in evt.Parameters.Values)
        {
            switch (v)
            {
                case int i: value = i; return true;
                case long l: value = l; return true;
                case float f: value = (long)f; return true;
                case double d: value = (long)d; return true;
            }
        }
        value = 0;
        return false;
    }
}
