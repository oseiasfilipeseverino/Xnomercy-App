using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Histórico de mapas e runs de dungeon (18/10/2026, ideia vinda do SAT:
/// MapHistory + DungeonController).
///
/// Cada entrada numa zona é uma VISITA, com o que você ganhou ali: fama, prata,
/// valor do loot que VOCÊ pegou e mobs que você matou. Salas de dungeon seguidas
/// (do mesmo tipo, sem sair pro mundo aberto) formam uma RUN — é o que responde
/// "quanto rendeu essa dungeon?".
///
/// A troca de zona vem da operação Join (código real 2 no [253]), que o app já usa
/// pra descobrir quem é você: [8] = índice do mapa ("4000", "@RANDOMDUNGEON@guid"...).
/// Índice e formato: JoinResponse do SAT.
///
/// Guardado em disco (mapas.json): o histórico sobrevive a fechar o app.
/// </summary>
public sealed class MapHistory
{
    public sealed class Visita
    {
        public string Indice { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Tipo { get; set; } = "";
        public DateTime InicioUtc { get; set; }
        public DateTime? FimUtc { get; set; }
        public long Fama { get; set; }
        public long Prata { get; set; }
        public long Loot { get; set; }
        public int Itens { get; set; }
        public int Mobs { get; set; }
    }

    public sealed class Run
    {
        public string Tipo { get; set; } = "";
        public DateTime InicioUtc { get; set; }
        public DateTime? FimUtc { get; set; }
        public int Salas { get; set; }
        public long Fama { get; set; }
        public long Prata { get; set; }
        public long Loot { get; set; }
        public int Itens { get; set; }
        public int Mobs { get; set; }
    }

    private sealed class Arquivo
    {
        public List<Visita> Visitas { get; set; } = new();
        public List<Run> Runs { get; set; } = new();
    }

    public const int MaxVisitas = 200, MaxRuns = 60;

    private readonly object _lock = new();
    private readonly List<Visita> _visitas = new();   // mais nova primeiro
    private readonly List<Run> _runs = new();
    private Visita? _atual;
    private Run? _runAtual;
    private readonly string? _arquivo;

    public event Action? Mudou;

    /// <summary>arquivo = null não grava em disco (teste).</summary>
    public MapHistory(string? arquivo)
    {
        _arquivo = arquivo;
        if (arquivo == null || !File.Exists(arquivo)) return;
        try
        {
            var a = JsonSerializer.Deserialize<Arquivo>(File.ReadAllText(arquivo));
            if (a == null) return;
            // Visita/run aberta de uma sessão anterior: fecha com o fim conhecido.
            foreach (var v in a.Visitas) v.FimUtc ??= v.InicioUtc;
            foreach (var r in a.Runs) r.FimUtc ??= r.InicioUtc;
            _visitas.AddRange(a.Visitas.Take(MaxVisitas));
            _runs.AddRange(a.Runs.Take(MaxRuns));
        }
        catch { /* arquivo estragado: começa vazio */ }
    }

    public static string CaminhoPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "mapas.json");

    public string? MapaAtual { get { lock (_lock) return _atual?.Nome; } }

    public List<Visita> Visitas() { lock (_lock) return _visitas.Select(Copia).ToList(); }
    public List<Run> Runs() { lock (_lock) return _runs.Select(Copia).ToList(); }

    /// <summary>Join (op 2): o mapa novo vem no [8]. true se trocou de mapa.</summary>
    public bool HandleOpResponse(PhotonOperationResponse op) => HandleJoin(op.Parameters, DateTime.UtcNow);

    public bool HandleJoin(IReadOnlyDictionary<byte, object?> p, DateTime agoraUtc)
    {
        // Join (op 2): mapa no [8]. Troca de mapa (op 41, conferida nos diagnósticos e
        // na sessão de 18/10): mapa no [0] — chega antes da Join, e às vezes é a única.
        if (!p.TryGetValue(253, out var c)) return false;
        long op = PhotonParam.ToLong(c) ?? -1;
        byte campo = op == 2 ? (byte)8 : op == 41 ? (byte)0 : (byte)255;
        if (campo == 255 || !p.TryGetValue(campo, out var m) || m is null) return false;
        if (op == 41 && m is not string) return false;
        string indice = m is string s ? s : PhotonParam.ToLong(m)?.ToString() ?? "";
        if (indice.Length == 0 || indice.Length > 200) return false;
        Entrou(indice, agoraUtc);
        CaptureHealth.Marcar("Mapa");
        return true;
    }

    public void Entrou(string indice, DateTime agoraUtc)
    {
        lock (_lock)
        {
            if (_atual != null && _atual.Indice == indice) return;   // Join de novo no mesmo mapa
            if (_atual != null) _atual.FimUtc = agoraUtc;

            string tipo = MapCatalog.Tipo(indice);
            _atual = new Visita { Indice = indice, Nome = MapCatalog.Nome(indice), Tipo = tipo, InicioUtc = agoraUtc };
            _visitas.Insert(0, _atual);
            if (_visitas.Count > MaxVisitas) _visitas.RemoveRange(MaxVisitas, _visitas.Count - MaxVisitas);

            if (MapCatalog.EhDungeon(tipo))
            {
                if (_runAtual == null || _runAtual.Tipo != tipo)
                {
                    if (_runAtual != null) _runAtual.FimUtc = agoraUtc;
                    _runAtual = new Run { Tipo = tipo, InicioUtc = agoraUtc };
                    _runs.Insert(0, _runAtual);
                    if (_runs.Count > MaxRuns) _runs.RemoveRange(MaxRuns, _runs.Count - MaxRuns);
                }
                _runAtual.Salas++;
            }
            else if (_runAtual != null)
            {
                _runAtual.FimUtc = agoraUtc;   // saiu pro mundo aberto: a run acabou
                _runAtual = null;
            }
        }
        Salvar();
        Mudou?.Invoke();
    }

    /// <summary>Soma o que foi ganho AGORA no mapa atual (e na run, se houver).</summary>
    public void Somar(long fama = 0, long prata = 0, long loot = 0, int itens = 0, int mobs = 0)
    {
        lock (_lock)
        {
            if (_atual == null) return;   // ainda não sabemos em que mapa está
            _atual.Fama += fama; _atual.Prata += prata; _atual.Loot += loot; _atual.Itens += itens; _atual.Mobs += mobs;
            if (_runAtual != null)
            {
                _runAtual.Fama += fama; _runAtual.Prata += prata; _runAtual.Loot += loot; _runAtual.Itens += itens; _runAtual.Mobs += mobs;
            }
        }
        Mudou?.Invoke();
    }

    public void Limpar()
    {
        lock (_lock) { _visitas.Clear(); _runs.Clear(); if (_atual != null) _visitas.Add(_atual); _runAtual = null; }
        Salvar();
        Mudou?.Invoke();
    }

    public void Salvar()
    {
        if (_arquivo == null) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(new Arquivo { Visitas = _visitas.ToList(), Runs = _runs.ToList() });
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", json);
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch { /* gravar é melhor esforço: o histórico na tela continua */ }
    }

    private static Visita Copia(Visita v) => new()
    {
        Indice = v.Indice, Nome = v.Nome, Tipo = v.Tipo, InicioUtc = v.InicioUtc, FimUtc = v.FimUtc,
        Fama = v.Fama, Prata = v.Prata, Loot = v.Loot, Itens = v.Itens, Mobs = v.Mobs,
    };
    private static Run Copia(Run r) => new()
    {
        Tipo = r.Tipo, InicioUtc = r.InicioUtc, FimUtc = r.FimUtc, Salas = r.Salas,
        Fama = r.Fama, Prata = r.Prata, Loot = r.Loot, Itens = r.Itens, Mobs = r.Mobs,
    };
}
