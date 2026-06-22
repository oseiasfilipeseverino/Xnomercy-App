using System.Collections.Concurrent;
using System.Linq;

namespace XnomercyApp.Network;

/// <summary>
/// Guarda, durante a sessão, quem é quem: ObjectId -> nome, guild e arma principal.
/// Alimentado pelo evento NewCharacter (quando um jogador aparece na cena, o jogo
/// manda nome + guild + equipamento). Serve pra trocar "#73667" pelo nome real no
/// medidor de dano e pra filtrar loot por guild.
/// </summary>
public sealed class PlayerInfo
{
    public string Name = "";
    public string Guild = "";
    public int MainHand = -1;   // índice do item da mão principal (arma)
}

public static class PlayerRegistry
{
    private static readonly ConcurrentDictionary<long, PlayerInfo> _byId = new();
    private static readonly ConcurrentDictionary<long, byte> _mobs = new();

    // Seu próprio ObjectId — descoberto pelos eventos de fama/prata (que são do SEU
    // personagem). O jogo não manda NewCharacter de você mesmo, então é assim que a
    // gente sabe quem é "você" no medidor de dano.
    public static long? SelfObjectId { get; private set; }

    public static void HandleEvent(PhotonEvent evt)
    {
        if ((evt.EventCode == GameEventCodes.FameGain || evt.EventCode == GameEventCodes.SilverGain
             || evt.EventCode == GameEventCodes.SilverTaken)
            && evt.Parameters.TryGetValue(0, out var sid) && ToLong(sid) is long self && self > 0)
        {
            // ObjectId muda de zona (e pode ser pequeno, tipo 112). Atualizamos sempre,
            // pra refletir o ObjectId atual do seu personagem na zona em que você está —
            // é o mesmo que aparece como causador no medidor de dano.
            SelfObjectId = self;
            return;
        }
        if (evt.EventCode == GameEventCodes.NewMob)
        {
            if (evt.Parameters.TryGetValue(0, out var mid) && ToLong(mid) is long m) RegisterMob(m);
            return;
        }
        if (evt.EventCode == GameEventCodes.MobSpeak)
        {
            // 2ª fonte, mais confiável: o mob "falou" (provocação/agro), então o [4] é
            // garantidamente um ObjectId de mob, independente do NewMob ter disparado certo.
            if (evt.Parameters.TryGetValue(4, out var mid) && ToLong(mid) is long m) RegisterMob(m);
            return;
        }
        if (evt.EventCode != GameEventCodes.NewCharacter) return;
        if (!evt.Parameters.TryGetValue(0, out var idObj) || ToLong(idObj) is not long id) return;

        var info = new PlayerInfo();
        if (evt.Parameters.TryGetValue(1, out var n)) info.Name = n?.ToString() ?? "";
        if (evt.Parameters.TryGetValue(8, out var g)) info.Guild = g?.ToString() ?? "";
        if (evt.Parameters.TryGetValue(40, out var eq) && eq is not null) info.MainHand = FirstEquip(eq);
        DiagLogNewCharacter(evt, id, info);   // calibração: ver se [1]/[8]/[40] batem com o jogo real
        if (info.Name.Length > 0)
        {
            _byId[id] = info;
            if (_byId.Count > 20000) _byId.Clear();   // mesmo teto de segurança que os mobs
        }
    }

    // Diagnóstico de calibração: grava TODO NewCharacter recebido (até 300), com todos os
    // parâmetros crus, pra comparar com quem realmente apareceu na tela. Se o nome de um
    // amigo visível não aparecer aqui, o evento não chegou (perda de pacote/filtro errado);
    // se aparecer com [1] vazio/estranho, o índice do nome mudou e precisa ser recalibrado.
    // Arquivo: %LocalAppData%\XnomercyApp\newchar_diag.txt
    private static int _diagCount;
    private static readonly object _diagLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogNewCharacter(PhotonEvent evt, long id, PlayerInfo info)
    {
        if (_diagCount >= 300) return;
        lock (_diagLock)
        {
            if (_diagCount >= 300) return;
            _diagCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var parms = string.Join(" ", evt.Parameters.OrderBy(k => k.Key)
                    .Select(kv => $"[{kv.Key}]={DiagVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "newchar_diag.txt"),
                    $"id={id} resolvedName=\"{info.Name}\" resolvedGuild=\"{info.Guild}\" {parms}\n");
            }
            catch { }
        }
    }

    private static string DiagVal(object? v) => v switch
    {
        null => "null",
        byte[] b => $"byte[{b.Length}]={Convert.ToHexString(b)}",
        object?[] a => $"arr[{a.Length}]={{{string.Join(",", a.Select(x => x?.ToString() ?? "null"))}}}",
        _ => v.ToString() ?? ""
    };

    public static PlayerInfo? Get(long objectId) => _byId.TryGetValue(objectId, out var v) ? v : null;

    public static string NameOf(long objectId)
    {
        if (objectId == SelfObjectId) return "Você";
        return _byId.TryGetValue(objectId, out var v) && v.Name.Length > 0 ? v.Name : $"#{objectId}";
    }

    public static bool IsMob(long objectId) => _mobs.ContainsKey(objectId);

    private static void RegisterMob(long id)
    {
        _mobs[id] = 1;
        // Teto de segurança: numa sessão longa (várias horas, muitas zonas), isso
        // cresceria sem limite. Limpa e deixa repopular — ObjectIds de mob mudam de
        // zona mesmo assim, então não perde nada de relevante mantendo isso pequeno.
        if (_mobs.Count > 20000) _mobs.Clear();
    }

    public static void Clear() { _byId.Clear(); _mobs.Clear(); }

    // O equipamento vem como array; a posição 0 é a mão principal (arma).
    private static int FirstEquip(object eq) => eq switch
    {
        int[] a when a.Length > 0 => a[0],
        short[] a when a.Length > 0 => a[0],
        byte[] a when a.Length > 0 => a[0],
        object?[] a when a.Length > 0 && ToLong(a[0]) is long l => (int)l,
        _ => -1
    };

    private static long? ToLong(object? v) => v switch
    {
        int i => i, long l => l, short s => s, byte b => b, _ => null
    };
}
