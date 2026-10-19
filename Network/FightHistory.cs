using System.IO;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Lutas salvas do Medidor de Dano (18/10/2026; o SAT guarda "snapshots" do
/// medidor — DamageMeterSnapshotStorage). Antes a única forma de rever uma CTA era
/// o CSV; agora cada luta fica no app, com o mapa, a hora e o ranking.
///
/// Uma luta é salva quando você aperta "Salvar luta" ou "Reiniciar sessão" (que
/// salvava nada e jogava fora o que estava na tela). Só jogadores com nome entram
/// (#número é mob/invocação na maioria), no máximo 100 por luta, 30 lutas guardadas.
/// </summary>
public sealed class FightHistory
{
    public sealed class Linha
    {
        public string Nome { get; set; } = "";
        public long Dano { get; set; }
        public long Dps { get; set; }
        public long Cura { get; set; }
        public long Overheal { get; set; }
        public long Recebido { get; set; }
    }

    public sealed class Luta
    {
        public DateTime InicioUtc { get; set; }
        public DateTime FimUtc { get; set; }
        public string Mapa { get; set; } = "";
        public long DanoTotal { get; set; }
        public List<Linha> Jogadores { get; set; } = new();
        // Seu personagem quando a luta foi salva: no texto/CSV o "Você" vira esse nome.
        public string Eu { get; set; } = "";

        private string Fora(string nome) => nome == "Você" && Eu.Length > 0 ? Eu : nome;

        public string Titulo =>
            $"{InicioUtc.ToLocalTime():dd/MM HH:mm}" + (Mapa.Length > 0 ? $" · {Mapa}" : "") +
            $" · {Duracao()} · {Jogadores.Count} jogadores · {DanoTotal:N0} de dano";

        public string Duracao()
        {
            var d = FimUtc - InicioUtc;
            return d.TotalMinutes < 1 ? $"{Math.Max(1, (int)d.TotalSeconds)}s" : $"{(int)d.TotalMinutes} min {d.Seconds:00}s";
        }

        public string Texto()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Luta de {InicioUtc.ToLocalTime():dd/MM/yyyy HH:mm}" + (Mapa.Length > 0 ? $" — {Mapa}" : "") + $" ({Duracao()})");
            int pos = 1;
            foreach (var j in Jogadores)
            {
                double pct = DanoTotal > 0 ? j.Dano * 100.0 / DanoTotal : 0;
                sb.AppendLine($"{pos++}. {Fora(j.Nome)} — {j.Dano:N0} ({pct:0.0}%)" +
                              (j.Dps > 0 ? $" | DPS {j.Dps:N0}" : "") +
                              (j.Cura > 0 ? $" | cura {j.Cura:N0}" : "") +
                              (j.Recebido > 0 ? $" | recebeu {j.Recebido:N0}" : ""));
            }
            return sb.ToString();
        }

        public string Csv()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Posicao;Jogador;Dano;DPS;Cura efetiva;Overheal;Dano recebido");
            int pos = 1;
            foreach (var j in Jogadores)
                sb.AppendLine($"{pos++};{(Fora(j.Nome).Contains(';') ? '"' + Fora(j.Nome) + '"' : Fora(j.Nome))};{j.Dano};{j.Dps};{j.Cura};{j.Overheal};{j.Recebido}");
            return sb.ToString();
        }
    }

    public const int MaxLutas = 30, MaxJogadores = 100;
    private readonly object _lock = new();
    private readonly List<Luta> _lutas = new();   // mais nova primeiro
    private readonly string? _arquivo;

    public FightHistory(string? arquivo)
    {
        _arquivo = arquivo;
        if (arquivo == null) return;
        if (!ArquivoSeguro.Ler<List<Luta>>(arquivo, out var lidas)) { _arquivo = null; return; }   // ver ArquivoSeguro
        _lutas.AddRange((lidas ?? new()).Take(MaxLutas));
    }

    public static string CaminhoPadrao => System.IO.Path.Combine(XnomercyApp.Network.PastaDados.Raiz, "lutas.json");

    public List<Luta> Lutas() { lock (_lock) return _lutas.ToList(); }

    /// <summary>Guarda o medidor como está. null se não havia dano de jogador com nome.</summary>
    public Luta? Salvar(IReadOnlyList<DamageMeterEntry> entradas, DateTime? inicioUtc, DateTime? fimUtc, string? mapa)
    {
        var jogadores = entradas
            .Where(e => !e.Name.StartsWith('#') && (e.Damage > 0 || e.Healing > 0))
            .OrderByDescending(e => e.Damage)
            .Take(MaxJogadores)
            .Select(e => new Linha { Nome = e.Name, Dano = e.Damage, Dps = (long)Math.Round(e.Dps), Cura = e.Healing, Overheal = e.Overheal, Recebido = e.Taken })
            .ToList();
        if (jogadores.Count == 0) return null;
        var agora = DateTime.UtcNow;
        var luta = new Luta
        {
            InicioUtc = inicioUtc ?? agora,
            FimUtc = fimUtc ?? agora,
            Mapa = mapa ?? "",
            DanoTotal = jogadores.Sum(j => j.Dano),
            Jogadores = jogadores,
            Eu = PlayerRegistry.SelfName ?? "",
        };
        lock (_lock)
        {
            _lutas.Insert(0, luta);
            if (_lutas.Count > MaxLutas) _lutas.RemoveRange(MaxLutas, _lutas.Count - MaxLutas);
        }
        Gravar();
        return luta;
    }

    public void Apagar(Luta luta)
    {
        lock (_lock) _lutas.Remove(luta);
        Gravar();
    }

    private void Gravar()
    {
        if (_arquivo == null) return;
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_lutas);
        ArquivoSeguro.Gravar(_arquivo, json);
    }
}
