using System.Collections.Concurrent;

namespace XnomercyApp.Network;

/// <summary>
/// Detecta quando O PRÓPRIO jogador pega um item de um corpo/baú — sem depender do
/// evento de broadcast OtherGrabbedLoot (277), que é a única fonte hoje do Loot Log e
/// que pode falhar pro próprio looter (o servidor às vezes não confirma de volta pra
/// quem pegou, ou manda sem o campo "de quem" preenchido — daí o item simplesmente
/// não aparecia, mesmo o jogador tendo pego de verdade).
///
/// A ideia (baseada no projeto AlbionOnline-StatisticsAnalysis, Triky313, GPL-3.0):
/// em vez de esperar o servidor AVISAR que você pegou algo, escutamos a OPERAÇÃO que
/// o SEU PRÓPRIO cliente manda pro servidor ao arrastar um item do baú pro inventário.
/// Como só capturamos tráfego desta máquina (modo Normal, não Promíscuo — ver
/// PacketCaptureService), toda operação de request que vemos é, por definição, SUA.
/// Não tem nome pra comparar, não tem "from" que pode vir vazio — é 100% confiável.
///
/// Duas operações cobrem os dois jeitos de pegar item:
/// - InventoryMoveItem (real=30, confirmado em captura real: [1] e [4] vêm como
///   byte[16]=GUID, com [4] constante entre vários pickups — é o UserInteractGuid do
///   próprio jogador, [1] muda a cada corpo/baú aberto). Click em UM item por vez.
///   Não carrega o ObjectId do item (só o slot do baú), então não dá pra resolver
///   nome/quantidade sozinho.
/// - InventoryMoveGivenItems (real=39, mesmo padrão de parâmetros do InventoryMoveItem
///   na documentação do SAT; ainda não calibrado com captura real neste app — por
///   isso só dispara o evento SelfLootDetected, nunca derruba nada se estiver errado).
///   "Pegar tudo": carrega os ObjectIds dos itens pegos em [4], permitindo resolver
///   nome/quantidade cruzando com o cache de itens descobertos (NewSimpleItem/32).
/// </summary>
public static class SelfLootDetector
{
    private const int OpInventoryMoveItem = 30;
    private const int OpInventoryMoveGivenItems = 39;

    // ObjectId do item -> (índice, quantidade), alimentado pelo NewSimpleItem (32).
    // Cap pequeno: é só uma janela recente pra cruzar com o "pegar tudo" que chega
    // logo depois. Sem isso a memória cresceria sem limite numa sessão longa de farm.
    private const int MaxDiscoveredItems = 300;
    private static readonly ConcurrentDictionary<long, (int ItemIndex, long Quantity)> _discoveredItems = new();
    private static readonly ConcurrentQueue<long> _discoveredOrder = new();

    /// <summary>Dispara sempre que detectamos um pickup do PRÓPRIO jogador. Item/quantidade
    /// só vêm preenchidos quando deu pra resolver pelo cache (ver acima); senão ficam null —
    /// quem escuta ainda pode usar isso como "sinal de que você pegou algo agora".</summary>
    public static event Action<int?, long?>? SelfLootDetected;

    /// <summary>Alimenta o cache de itens recém-descobertos (chamar pra TODO NewSimpleItem,
    /// não só quando o modo avançado está visível — senão o cruzamento com "pegar tudo" falha).</summary>
    public static void RegisterDiscoveredItem(long objectId, int itemIndex, long quantity)
    {
        if (objectId <= 0) return;
        if (_discoveredItems.TryAdd(objectId, (itemIndex, quantity)))
        {
            _discoveredOrder.Enqueue(objectId);
            while (_discoveredOrder.Count > MaxDiscoveredItems && _discoveredOrder.TryDequeue(out var old))
                _discoveredItems.TryRemove(old, out _);
        }
    }

    public static void HandleOpRequest(PhotonOperationRequest op)
    {
        if (!op.Parameters.TryGetValue(253, out var rc) || rc is null) return;
        int real;
        try { real = Convert.ToInt32(rc); } catch { return; }

        if (real == OpInventoryMoveItem)
        {
            // Click em item único: não dá pra saber QUAL item (só o slot do baú), mas já
            // é certeza de que você acabou de pegar algo — o Loot Log usa isso pra não
            // descartar a linha do evento 277 que vier sem o campo "de quem".
            SelfLootDetected?.Invoke(null, null);
        }
        else if (real == OpInventoryMoveGivenItems)
        {
            if (op.Parameters.TryGetValue(4, out var idsObj) && idsObj is object?[] ids)
            {
                foreach (var idObj in ids)
                {
                    long? id = idObj switch { int i => i, long l => l, short s => s, _ => null };
                    if (id is long itemObjId && _discoveredItems.TryGetValue(itemObjId, out var info))
                    {
                        SelfLootDetected?.Invoke(info.ItemIndex, info.Quantity);
                    }
                    else
                    {
                        SelfLootDetected?.Invoke(null, null);
                    }
                }
            }
            else
            {
                SelfLootDetected?.Invoke(null, null);
            }
        }
    }
}
