using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Conteúdo de baú e banco (18/10/2026; SAT: StorageHistory).
///
/// Como o jogo mostra um baú aberto:
///   1. lista das abas do banco/da guild naquele lugar (BankVaultInfo SAT 413 /
///      GuildVaultInfo SAT 412): [1]=lugar ("guid@3005") [2]=GUIDs das abas [3]=nomes
///   2. cada item do baú chega como item novo (30/31/32): [0]=ObjectId [1]=item [2]=qtd
///   3. o baú "abre" (AttachItemContainer SAT 99): [0]=ObjectId do baú
///      [1]=GUID do baú [3]=lista de ObjectIds dos itens, um por espaço (0 = vazio)
/// O app junta os três: guarda o que tinha em cada aba, com o lugar e a hora em que
/// você viu, e o valor estimado (ItemValues). Fica salvo em disco — dá pra conferir o
/// banco de outra cidade sem ir até lá. Seu inventário (o baú com o SEU ObjectId)
/// não entra.
/// </summary>
public sealed class StorageTracker
{
    public const int SatGuildVaultInfo = 412, SatBankVaultInfo = 413;

    public sealed class Item
    {
        public int Indice { get; set; }
        public string Unico { get; set; } = "";
        public long Quantidade { get; set; }
    }

    public sealed class Bau
    {
        public string Guid { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Lugar { get; set; } = "";
        public bool DaGuild { get; set; }
        public DateTime VistoUtc { get; set; }
        public List<Item> Itens { get; set; } = new();
    }

    private readonly object _lock = new();
    // ObjectId -> (índice do item, quantidade), dos eventos de item novo
    private readonly ConcurrentDictionary<long, (int Indice, long Qtd)> _objetos = new();
    // GUID da aba -> (nome, lugar, é da guild)
    private readonly Dictionary<string, (string Nome, string Lugar, bool Guild)> _abas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bau> _baus = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _arquivo;
    private const int MaxObjetos = 30_000, MaxBaus = 200;

    public event Action? Mudou;

    public StorageTracker(string? arquivo)
    {
        _arquivo = arquivo;
        if (arquivo == null || !File.Exists(arquivo)) return;
        try
        {
            foreach (var b in JsonSerializer.Deserialize<List<Bau>>(File.ReadAllText(arquivo)) ?? new())
                _baus[b.Guid] = b;
        }
        catch { /* arquivo estragado: começa vazio */ }
    }

    public static string CaminhoPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "baus.json");

    public List<Bau> Baus() { lock (_lock) return _baus.Values.OrderByDescending(b => b.VistoUtc).ToList(); }

    public void HandleEvent(PhotonEvent evt)
    {
        var p = evt.Parameters;
        int c = evt.EventCode;
        int simples = GameEventCodes.LootPickup;
        if (c >= simples - 2 && c <= simples)
        {
            if (p.TryGetValue(0, out var o) && PhotonParam.ToLong(o) is long obj
                && p.TryGetValue(1, out var i) && PhotonParam.ToLong(i) is long idx && idx > 0
                && !(p.TryGetValue(5, out var t5) && t5 is string))
            {
                long qtd = p.TryGetValue(2, out var q) && PhotonParam.ToLong(q) is long qq && qq > 0 ? qq : 1;
                if (_objetos.Count > MaxObjetos) _objetos.Clear();
                _objetos[obj] = ((int)idx, qtd);
            }
        }
        else if (c == EventOffset.Live(SatBankVaultInfo) || c == EventOffset.Live(SatGuildVaultInfo))
            LerAbas(p, guild: c == EventOffset.Live(SatGuildVaultInfo));
        else if (c == GameEventCodes.AttachItemContainer)
            Abriu(p);
    }

    private void LerAbas(IReadOnlyDictionary<byte, object?> p, bool guild)
    {
        if (!p.TryGetValue(1, out var l) || l?.ToString() is not string lugar) return;
        if (!p.TryGetValue(2, out var g) || Guids(g) is not { Count: > 0 } guids) return;
        var nomes = p.TryGetValue(3, out var n) ? Textos(n) : new List<string>();
        int arroba = lugar.LastIndexOf('@');
        string nomeLugar = MapCatalog.Nome(arroba >= 0 ? lugar[(arroba + 1)..] : lugar);
        lock (_lock)
            for (int i = 0; i < guids.Count; i++)
                _abas[guids[i]] = (i < nomes.Count && nomes[i].Length > 0 ? nomes[i] : $"Aba {i + 1}", nomeLugar, guild);
    }

    private void Abriu(IReadOnlyDictionary<byte, object?> p)
    {
        if (!p.TryGetValue(0, out var o) || PhotonParam.ToLong(o) is not long dono) return;
        if (dono == PlayerRegistry.SelfObjectId) return;                         // seu inventário
        if (!p.TryGetValue(1, out var g) || g is not byte[] { Length: 16 } gb) return;
        if (!p.TryGetValue(3, out var s) || s is not Array espacos) return;
        string guid = Convert.ToHexString(gb);
        var itens = new Dictionary<int, long>();
        foreach (var e in espacos)
            if (PhotonParam.ToLong(e) is long obj && obj > 0 && _objetos.TryGetValue(obj, out var it))
                itens[it.Indice] = itens.GetValueOrDefault(it.Indice) + it.Qtd;
        lock (_lock)
        {
            bool conhecida = _abas.TryGetValue(guid, out var aba);
            if (!conhecida && itens.Count == 0) return;   // nada útil pra guardar
            _baus[guid] = new Bau
            {
                Guid = guid,
                Nome = conhecida ? aba.Nome : "Baú",
                Lugar = conhecida ? aba.Lugar : "",
                DaGuild = conhecida && aba.Guild,
                VistoUtc = DateTime.UtcNow,
                Itens = itens.Select(kv => new Item { Indice = kv.Key, Unico = ItemCatalog.GetUniqueName(kv.Key) ?? "", Quantidade = kv.Value })
                             .OrderByDescending(x => x.Quantidade).ToList(),
            };
            if (_baus.Count > MaxBaus)
                foreach (var velho in _baus.Values.OrderBy(b => b.VistoUtc).Take(_baus.Count - MaxBaus).ToList())
                    _baus.Remove(velho.Guid);
        }
        Gravar();
        CaptureHealth.Marcar("Baús e banco");
        Mudou?.Invoke();
    }

    public void Apagar(string guid)
    {
        lock (_lock) _baus.Remove(guid);
        Gravar();
        Mudou?.Invoke();
    }

    private void Gravar()
    {
        if (_arquivo == null) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_baus.Values.ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", json);
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch { /* melhor esforço */ }
    }

    private static List<string>? Guids(object? v)
    {
        if (v is byte[] b && b.Length > 0 && b.Length % 16 == 0)
            return Enumerable.Range(0, b.Length / 16).Select(i => Convert.ToHexString(b, i * 16, 16)).ToList();
        if (v is object?[] a && a.All(x => x is byte[] { Length: 16 }))
            return a.Select(x => Convert.ToHexString((byte[])x!)).ToList();
        return null;
    }

    private static List<string> Textos(object? v) => v switch
    {
        string?[] s => s.Select(x => x ?? "").ToList(),
        object?[] o => o.Select(x => x?.ToString() ?? "").ToList(),
        string s => new List<string> { s },
        _ => new List<string>(),
    };
}
