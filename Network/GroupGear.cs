using System.Collections.Concurrent;

namespace XnomercyApp.Network;

/// <summary>
/// Equipamento e IP de quem está no seu grupo (18/10/2026, ideia do painel de grupo
/// do SAT: PartyController + CharacterEquipment).
///
/// De onde vem:
///   29 NewCharacter              [0]=ObjectId [1]=nome [7]=GUID [40]=equipamento
///   90 CharacterEquipmentChanged [0]=ObjectId [2]=equipamento (trocou de item)
///   inspeção (resposta)          [0]=GUID [1]=equipamento [3]=IP REAL (com maestria)
/// Equipamento = lista de índices de item, na ordem: 0 principal, 1 secundária,
/// 2 cabeça, 3 peito, 4 pés, 5 bolsa, 6 capa, 7 montaria... (0 = vazio).
/// Conferido nos pacotes reais de 12/10 (29 e 90 com Int16[10]).
///
/// Seu PRÓPRIO equipamento o jogo não manda no 29 (ninguém recebe NewCharacter de
/// si mesmo): vem do 90 quando você troca algum item, ou da API pública do Albion
/// (a mesma consulta que já traz o ícone da sua arma), que pode estar atrasada.
/// </summary>
public static class GroupGear
{
    public sealed record Linha(string Nome, string? Arma, IReadOnlyList<string?>? Slots, double? IpEquip, double? IpReal, string Fonte);

    private sealed class Info
    {
        public string Nome = "";
        public string Guid = "";
        public int[]? Equip;
    }

    private static readonly ConcurrentDictionary<long, Info> _porId = new();
    private static readonly ConcurrentDictionary<string, long> _idPorNome = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, double> _ipRealPorGuid = new(StringComparer.OrdinalIgnoreCase);
    private static int[]? _meuEquipEvento;
    private static string?[]? _meuEquipApi;
    private const int MaxJogadores = 5000;

    public static event Action? Mudou;

    public static void HandleEvent(PhotonEvent evt)
    {
        var p = evt.Parameters;
        if (evt.EventCode == GameEventCodes.NewCharacter)
        {
            if (!p.TryGetValue(0, out var o) || PhotonParam.ToLong(o) is not long id) return;
            if (!p.TryGetValue(1, out var n) || n is not string nome || nome.Length == 0) return;
            var equip = p.TryGetValue(40, out var e) ? Lista(e) : null;
            string guid = p.TryGetValue(7, out var g) && g is byte[] { Length: 16 } gb ? Convert.ToHexString(gb) : "";
            if (_porId.Count > MaxJogadores) { _porId.Clear(); _idPorNome.Clear(); }
            _porId[id] = new Info { Nome = nome, Guid = guid, Equip = equip };
            _idPorNome[nome] = id;
            Mudou?.Invoke();
        }
        else if (evt.EventCode == GameEventCodes.CharacterEquipmentChanged)
        {
            if (!p.TryGetValue(0, out var o) || PhotonParam.ToLong(o) is not long id) return;
            if (!p.TryGetValue(2, out var e) || Lista(e) is not int[] equip) return;
            if (id == PlayerRegistry.SelfObjectId) _meuEquipEvento = equip;
            else if (_porId.TryGetValue(id, out var info)) info.Equip = equip;
            else return;
            Mudou?.Invoke();
        }
    }

    /// <summary>Resposta de INSPEÇÃO: [0]=GUID [1]=equipamento [3]=IP real. Reconhecida pelo formato.</summary>
    public static bool HandleOpResponse(PhotonOperationResponse op) => HandleInspecao(op.Parameters);

    public static bool HandleInspecao(IReadOnlyDictionary<byte, object?> p)
    {
        if (!p.TryGetValue(0, out var g) || g is not byte[] { Length: 16 } gb) return false;
        if (!p.TryGetValue(1, out var e) || Lista(e) is not { Length: >= 8 }) return false;
        if (!p.TryGetValue(3, out var v) || PhotonParam.ToDouble(v) is not double ip || ip < 100 || ip > 5000) return false;
        _ipRealPorGuid[Convert.ToHexString(gb)] = ip;
        Mudou?.Invoke();
        return true;
    }

    /// <summary>Seu equipamento pela API pública (unique names na ordem dos espaços).</summary>
    public static void DefinirMeuEquipamentoPelaApi(string?[] slots)
    {
        _meuEquipApi = slots;
        Mudou?.Invoke();
    }

    private static int[]? Lista(object? v) => v switch
    {
        short[] s => s.Select(x => (int)x).ToArray(),
        int[] i => i,
        byte[] b => b.Select(x => (int)x).ToArray(),
        object?[] a when a.All(x => PhotonParam.ToLong(x) != null) => a.Select(x => (int)PhotonParam.ToLong(x)!.Value).ToArray(),
        _ => null,
    };

    private static string?[] Nomes(int[] equip) =>
        equip.Select(i => i > 0 ? ItemCatalog.GetUniqueName(i) : null).ToArray();

    /// <summary>Uma linha por pessoa do grupo (você primeiro).</summary>
    public static List<Linha> Grupo()
    {
        var nomes = new List<string>();
        if (PlayerRegistry.SelfName is string eu) nomes.Add(eu);
        foreach (var n in PartyTracker.Ativo ? PartyTracker.Nomes() : PlayerRegistry.NomesNoGrupoPeloRateio())
            if (!nomes.Contains(n, StringComparer.OrdinalIgnoreCase)) nomes.Add(n);
        return nomes.Select(LinhaDe).ToList();
    }

    public static Linha LinhaDe(string nome)
    {
        bool souEu = string.Equals(nome, PlayerRegistry.SelfName, StringComparison.OrdinalIgnoreCase);
        string?[]? slots = null;
        string fonte;
        double? real = null;
        if (souEu)
        {
            if (_meuEquipEvento != null) { slots = Nomes(_meuEquipEvento); fonte = "do jogo (você trocou de item)"; }
            else if (_meuEquipApi != null) { slots = _meuEquipApi; fonte = "da API do Albion (pode estar atrasado)"; }
            else fonte = "o jogo ainda não mostrou o seu equipamento";
        }
        else if (_idPorNome.TryGetValue(nome, out var id) && _porId.TryGetValue(id, out var info) && info.Equip != null)
        {
            slots = Nomes(info.Equip);
            fonte = "do jogo";
            if (info.Guid.Length > 0 && _ipRealPorGuid.TryGetValue(info.Guid, out var r)) real = r;
        }
        else fonte = "fora de alcance — o equipamento aparece quando a pessoa estiver na sua tela";

        return new Linha(nome, slots?.FirstOrDefault(), slots, slots != null ? ItemPowerCatalog.IpMedio(slots) : null, real, fonte);
    }

    /// <summary>Pra testes.</summary>
    public static void Limpar() { _porId.Clear(); _idPorNome.Clear(); _ipRealPorGuid.Clear(); _meuEquipEvento = null; _meuEquipApi = null; }
}
