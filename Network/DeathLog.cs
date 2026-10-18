using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Perdas na morte (18/10/2026; ideia do LossExplorer do SAT).
///
/// Fonte: o próprio loot social (OtherGrabbedLoot). Quando alguém saqueia o SEU corpo,
/// o evento chega com [1]=de quem = o seu nome — "Fulano pegou X de você". Isso diz
/// O QUE você perdeu e PRA QUEM, com o valor estimado (ItemValues), sem depender de
/// número de evento de morte que ainda não conferimos no jogo.
///
/// Uma morte = saques do seu corpo sem intervalo de mais de 5 minutos. Fica salvo em
/// disco (as 50 últimas). O Kill Feed do Discord mostra a morte; isto mostra o prejuízo.
/// </summary>
public sealed class DeathLog
{
    public sealed class Perda
    {
        public string Quem { get; set; } = "";
        public string Guild { get; set; } = "";
        public int Indice { get; set; }
        public string Unico { get; set; } = "";
        public long Quantidade { get; set; }
        public long Valor { get; set; }
    }

    public sealed class Morte
    {
        public DateTime InicioUtc { get; set; }
        public DateTime UltimoUtc { get; set; }
        public string Mapa { get; set; } = "";
        public List<Perda> Itens { get; set; } = new();
        public long Valor => Itens.Sum(i => i.Valor);
    }

    public static readonly TimeSpan MesmaMorte = TimeSpan.FromMinutes(5);
    public const int MaxMortes = 50;
    private readonly object _lock = new();
    private readonly List<Morte> _mortes = new();   // mais nova primeiro
    private readonly string? _arquivo;

    public event Action? Mudou;

    public DeathLog(string? arquivo)
    {
        _arquivo = arquivo;
        if (arquivo == null || !File.Exists(arquivo)) return;
        try { _mortes.AddRange((JsonSerializer.Deserialize<List<Morte>>(File.ReadAllText(arquivo)) ?? new()).Take(MaxMortes)); }
        catch { /* arquivo estragado: começa vazio */ }
    }

    public static string CaminhoPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "mortes.json");

    public List<Morte> Mortes() { lock (_lock) return _mortes.ToList(); }

    /// <summary>Alguém pegou um item. Se foi do SEU corpo, entra como perda. true se entrou.</summary>
    public bool Registrar(string deQuem, string quemPegou, string guildDeQuemPegou, int indice, long quantidade,
                          long? valor, string? mapa, DateTime agoraUtc)
    {
        if (PlayerRegistry.SelfName is not string eu || !string.Equals(deQuem, eu, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(quemPegou, eu, StringComparison.OrdinalIgnoreCase)) return false;   // pegou de volta
        lock (_lock)
        {
            var atual = _mortes.FirstOrDefault();
            if (atual == null || agoraUtc - atual.UltimoUtc > MesmaMorte)
            {
                atual = new Morte { InicioUtc = agoraUtc, Mapa = mapa ?? "" };
                _mortes.Insert(0, atual);
                if (_mortes.Count > MaxMortes) _mortes.RemoveRange(MaxMortes, _mortes.Count - MaxMortes);
            }
            atual.UltimoUtc = agoraUtc;
            atual.Itens.Add(new Perda
            {
                Quem = quemPegou, Guild = guildDeQuemPegou, Indice = indice,
                Unico = ItemCatalog.GetUniqueName(indice) ?? "", Quantidade = quantidade, Valor = valor ?? 0,
            });
        }
        Gravar();
        Mudou?.Invoke();
        return true;
    }

    /// <summary>Preço que chegou depois (mercado): completa as perdas ainda sem valor.</summary>
    public void PreencherValores()
    {
        bool mudou = false;
        lock (_lock)
            foreach (var m in _mortes)
                foreach (var p in m.Itens)
                    if (p.Valor == 0 && ItemValues.Valor(p.Indice) is long unit) { p.Valor = unit * p.Quantidade; mudou = true; }
        if (mudou) { Gravar(); Mudou?.Invoke(); }
    }

    public void Limpar()
    {
        lock (_lock) _mortes.Clear();
        Gravar();
        Mudou?.Invoke();
    }

    private void Gravar()
    {
        if (_arquivo == null) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_mortes);
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", json);
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch { /* melhor esforço */ }
    }
}
