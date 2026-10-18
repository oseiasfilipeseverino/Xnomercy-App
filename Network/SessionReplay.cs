using System.IO;
using System.Text.Json.Nodes;

namespace XnomercyApp.Network;

/// <summary>
/// Repassa uma sessão gravada (SessaoGravada) pelos rastreadores do app, sem janela
/// (18/10/2026). É o teste de regressão com dado REAL: cada arquivo em
/// testes/sessoes/ é rodado em todo release (harness_sessoes), e o que foi
/// reconhecido quando a sessão foi gravada tem que continuar sendo reconhecido.
///
/// A ordem é a mesma do MainWindow.OnCaptureEvent e das operações.
/// </summary>
public sealed class SessionReplay
{
    public readonly DamageMeterTracker Dano = new();
    public readonly FameSilverTracker Fama = new();
    public readonly OpenWorldKillTracker Kills = new();
    public readonly GatheringTracker Coleta = new();
    public readonly FactionTracker Faccao = new();
    public readonly StorageTracker Baus = new(null);
    public readonly MapHistory Mapas = new(null);
    public readonly MarketMailTracker Vendas = new();
    public int Eventos, Operacoes, Erros;

    /// <summary>Zera o estado estático (registro de jogadores, grupo, calibração, saúde).</summary>
    public static void ZerarEstado()
    {
        CaptureHealth.Reset();
        EventOffset.Reset();
        PartyTracker.Limpar();
        PlayerRegistry.Clear();
        GroupGear.Limpar();
    }

    public void Evento(PhotonEvent evt)
    {
        Eventos++;
        try
        {
            EventOffset.Observe(evt);
            CaptureHealth.Observar(evt);
            PartyTracker.Handle(evt);
            Coleta.HandleEvent(evt);
            PlayerRegistry.HandleEvent(evt);
            Fama.HandleEvent(evt);
            Dano.HandleEvent(evt);
            Kills.HandleEvent(evt);
            ItemValues.Observar(evt);
            GroupGear.HandleEvent(evt);
            Faccao.HandleEvent(evt);
            Baus.HandleEvent(evt);
        }
        catch { Erros++; }
    }

    public void Pedido(PhotonOperationRequest op)
    {
        Operacoes++;
        try
        {
            PlayerRegistry.HandleOpRequest(op);
            Vendas.HandleOpRequest(op);
            Coleta.HandleOpRequest(op.Parameters);
        }
        catch { Erros++; }
    }

    public void Resposta(PhotonOperationResponse op)
    {
        Operacoes++;
        try
        {
            PlayerRegistry.HandleOpResponse(op);
            Mapas.HandleOpResponse(op);
            GuildEnergyLog.HandleOpResponse(op);
            Vendas.HandleOpResponse(op);
            GroupGear.HandleOpResponse(op);
        }
        catch { Erros++; }
    }

    /// <summary>Repassa um arquivo exportado. Devolve o "saude" gravado nele (recurso → vistos).</summary>
    public Dictionary<string, long> Arquivo(string caminho)
    {
        var raiz = JsonNode.Parse(File.ReadAllText(caminho))!.AsObject();
        foreach (var a in raiz["amostras"]!.AsArray())
        {
            var o = a!.AsObject();
            var p = new Dictionary<byte, object?>();
            foreach (var kv in o["p"]!.AsObject()) p[byte.Parse(kv.Key)] = SessaoGravada.Ler(kv.Value);
            switch ((string?)o["k"])
            {
                case "evt": Evento(new PhotonEvent(1, p)); break;
                case "req": Pedido(new PhotonOperationRequest(1, p)); break;
                case "resp": Resposta(new PhotonOperationResponse(1, (short)((int?)o["rc"] ?? 0), null, p)); break;
            }
        }
        var gravado = new Dictionary<string, long>();
        foreach (var l in raiz["saude"]?.AsArray() ?? new JsonArray())
            gravado[(string)l!["recurso"]!] = (long)l["vistos"]!;
        return gravado;
    }
}
