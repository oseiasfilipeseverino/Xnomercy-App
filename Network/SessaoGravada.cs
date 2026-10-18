using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XnomercyApp.Network;

/// <summary>
/// "Exportar sessão para análise" (18/10/2026).
///
/// Problema: quase tudo no app foi construído pelo formato que o código do SAT
/// descreve, e só uma parte foi conferida em pacote real (os 60 de 12/10). Pra
/// conferir o resto de uma vez, o app guarda, durante a captura, o FORMATO de cada
/// evento e operação que chega — até 40 exemplos de cada número — e exporta num
/// arquivo. Esse arquivo também vira teste (testes/sessoes/), rodado em todo release
/// (ver SessionReplay).
///
/// O que NÃO vai pro arquivo (anonimizado na hora de guardar, nunca fica na memória):
///   - nomes de jogador e de guild: viram apelidos fixos (Nome1, Nome2...) — o mesmo
///     nome vira sempre o mesmo apelido, pra o teste ainda ver "o mesmo jogador";
///     o seu vira "Voce";
///   - GUIDs (16 bytes): viram GUIDs falsos, também fixos por sessão;
///   - qualquer texto com espaço (frase): vira "&lt;texto N&gt;";
///   - chat (73/74/75): não é guardado.
/// Fica: números, IDs de item ("T6_PLANKS@1"), tags de mob ("@MOB_..."), índices de
/// mapa, tipos de carta do mercado e anúncios do mercado (sem o nome do vendedor).
/// </summary>
public static class SessaoGravada
{
    public const int MaxPorCodigo = 40, MaxTotal = 8000;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _contagem = new();
    private static readonly List<JsonObject> _amostras = new();
    private static readonly Anonimizador _anon = new();
    private static DateTime _inicioUtc = DateTime.UtcNow;

    public static int Amostras { get { lock (_lock) return _amostras.Count; } }

    public static void Iniciar()
    {
        lock (_lock)
        {
            _contagem.Clear();
            _amostras.Clear();
            _anon.Limpar();
            _inicioUtc = DateTime.UtcNow;
        }
    }

    public static void Evento(PhotonEvent evt)
    {
        if (evt.EventCode < 0 || DiagPrivacidade.EhChat(evt.EventCode)) return;
        Guardar("evt", evt.EventCode, null, evt.Parameters);
    }

    public static void Pedido(PhotonOperationRequest op) =>
        Guardar("req", Codigo(op.Parameters), null, op.Parameters);

    public static void Resposta(PhotonOperationResponse op) =>
        Guardar("resp", Codigo(op.Parameters), op.ReturnCode, op.Parameters);

    private static int Codigo(IReadOnlyDictionary<byte, object?> p) =>
        p.TryGetValue(253, out var c) && PhotonParam.ToLong(c) is long v ? (int)v : -1;

    private static void Guardar(string tipo, int codigo, short? retorno, IReadOnlyDictionary<byte, object?> p)
    {
        string chave = $"{tipo}:{codigo}";
        lock (_lock)
        {
            int n = _contagem.GetValueOrDefault(chave) + 1;
            _contagem[chave] = n;
            if (n > MaxPorCodigo || _amostras.Count >= MaxTotal) return;
            var obj = new JsonObject { ["k"] = tipo, ["c"] = codigo, ["ms"] = (long)(DateTime.UtcNow - _inicioUtc).TotalMilliseconds };
            if (retorno != null) obj["rc"] = retorno.Value;
            var parametros = new JsonObject();
            foreach (var kv in p.OrderBy(x => x.Key))
                parametros[kv.Key.ToString()] = Valor(kv.Value, _anon);
            obj["p"] = parametros;
            _amostras.Add(obj);
        }
    }

    /// <summary>Grava o arquivo. Devolve quantas amostras foram.</summary>
    public static int Exportar(string arquivo, string versaoApp)
    {
        JsonObject raiz;
        lock (_lock)
        {
            raiz = new JsonObject
            {
                ["formato"] = 1,
                ["app"] = versaoApp,
                ["gravadoUtc"] = _inicioUtc.ToString("o"),
                ["eu"] = "Voce",
                ["deslocamentos"] = new JsonObject
                {
                    ["alto"] = EventOffset.Delta, ["dano"] = EventOffset.DeltaDano, ["nomes"] = EventOffset.DeltaNomes,
                },
                ["saude"] = new JsonArray(CaptureHealth.Linhas()
                    .Select(l => (JsonNode)new JsonObject { ["recurso"] = l.Recurso, ["vistos"] = l.Vistos, ["estranhos"] = l.Estranhos })
                    .ToArray()),
                ["contagens"] = new JsonObject(_contagem.OrderBy(kv => kv.Key)
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["amostras"] = new JsonArray(_amostras.Select(a => (JsonNode)a.DeepClone()).ToArray()),
            };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(arquivo))!);
        File.WriteAllText(arquivo, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return raiz["amostras"]!.AsArray().Count;
    }

    // ── Valor com o tipo junto (o Photon distingue short de int; o teste precisa) ──
    public static JsonNode? Valor(object? v, Anonimizador anon) => v switch
    {
        null => null,
        string s => Tipado("s", anon.Texto(s)),
        bool b => Tipado("b", b),
        byte b => Tipado("u8", b),
        short s => Tipado("i16", s),
        int i => Tipado("i32", i),
        long l => Tipado("i64", l),
        float f => Tipado("f32", f),
        double d => Tipado("f64", d),
        byte[] b => Tipado("bytes", Convert.ToHexString(anon.Bytes(b))),
        string?[] a => Tipado("s[]", new JsonArray(a.Select(x => (JsonNode?)(x == null ? null : JsonValue.Create(anon.Texto(x)))).ToArray())),
        bool[] a => Tipado("b[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        short[] a => Tipado("i16[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        int[] a => Tipado("i32[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        long[] a => Tipado("i64[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        float[] a => Tipado("f32[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        double[] a => Tipado("f64[]", new JsonArray(a.Select(x => (JsonNode?)x).ToArray())),
        object?[] a => Tipado("o[]", new JsonArray(a.Select(x => Valor(x, anon)).ToArray())),
        Dictionary<object, object?> d => Tipado("dict", new JsonArray(d.Select(kv =>
            (JsonNode?)new JsonObject { ["k"] = Valor(kv.Key, anon), ["v"] = Valor(kv.Value, anon) }).ToArray())),
        _ => Tipado("?", v.GetType().Name),
    };

    private static JsonObject Tipado(string t, JsonNode? v) => new() { ["t"] = t, ["v"] = v };

    /// <summary>Volta do JSON pro objeto que o parser do app entregaria (o teste usa).</summary>
    public static object? Ler(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var v = o["v"];
        return (string?)o["t"] switch
        {
            "s" => (string?)v,
            "b" => (bool)v!,
            "u8" => (byte)(int)v!,
            "i16" => (short)(int)v!,
            "i32" => (int)v!,
            "i64" => (long)v!,
            "f32" => (float)v!,
            "f64" => (double)v!,
            "bytes" => Convert.FromHexString((string)v!),
            "s[]" => v!.AsArray().Select(x => (string?)x).ToArray(),
            "b[]" => v!.AsArray().Select(x => (bool)x!).ToArray(),
            "i16[]" => v!.AsArray().Select(x => (short)(int)x!).ToArray(),
            "i32[]" => v!.AsArray().Select(x => (int)x!).ToArray(),
            "i64[]" => v!.AsArray().Select(x => (long)x!).ToArray(),
            "f32[]" => v!.AsArray().Select(x => (float)x!).ToArray(),
            "f64[]" => v!.AsArray().Select(x => (double)x!).ToArray(),
            "o[]" => v!.AsArray().Select(Ler).ToArray(),
            "dict" => v!.AsArray().Where(x => Ler(x!["k"]) != null)
                         .ToDictionary(x => Ler(x!["k"])!, x => Ler(x!["v"])),
            _ => null,
        };
    }

    /// <summary>Troca nome por apelido, GUID por GUID falso, frase por "&lt;texto&gt;".</summary>
    public sealed class Anonimizador
    {
        private readonly Dictionary<string, string> _nomes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _guids = new(StringComparer.Ordinal);
        private static readonly Regex NomeNoJson = new("\"(SellerName|BuyerName|Name|PlayerName)\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        public void Limpar() { _nomes.Clear(); _guids.Clear(); }

        public string Texto(string s)
        {
            if (s.Length == 0) return s;
            if (s.Contains('{'))   // anúncio do mercado em JSON: mantém, sem nomes
                return NomeNoJson.Replace(s, m => $"\"{m.Groups[1].Value}\":\"{Apelido(m.Groups[2].Value)}\"");
            if (s.Any(char.IsWhiteSpace)) return $"<texto {s.Length}>";
            // Identificador do jogo: item, tag de mob, mapa, tipo de carta, carta do mercado.
            if (s.Contains('_') || s.Contains('@') || s.Contains('|') || s.All(c => char.IsDigit(c) || c == '-' || c == '.')) return s;
            return Apelido(s);
        }

        private string Apelido(string nome)
        {
            if (nome.Length == 0) return nome;
            if (PlayerRegistry.SelfName is string eu && string.Equals(nome, eu, StringComparison.OrdinalIgnoreCase)) return "Voce";
            if (!_nomes.TryGetValue(nome, out var a)) _nomes[nome] = a = $"Nome{_nomes.Count + 1}";
            return a;
        }

        public byte[] Bytes(byte[] b)
        {
            if (b.Length == 0 || b.Length % 16 != 0) return b;
            // Lista de GUIDs colados (grupo): troca um por um.
            var saida = new byte[b.Length];
            for (int i = 0; i < b.Length; i += 16)
            {
                var hex = Convert.ToHexString(b, i, 16);
                if (!_guids.TryGetValue(hex, out var falso))
                {
                    falso = new byte[16];
                    BitConverter.GetBytes(_guids.Count + 1).CopyTo(falso, 0);
                    falso[15] = 0xAA;
                    _guids[hex] = falso;
                }
                falso.CopyTo(saida, i);
            }
            return saida;
        }
    }
}
