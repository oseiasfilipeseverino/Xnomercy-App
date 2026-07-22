using PacketDotNet;
using SharpPcap;

namespace XnomercyApp.Network;

/// <summary>
/// Captura passiva de pacotes UDP nas portas conhecidas do Albion Online (5055/5056).
/// Só LÊ tráfego de rede — nunca envia, modifica ou injeta nada. Não lê memória do
/// processo do jogo nem desenha overlay. Ver Network/README.md para o porquê disso
/// ser aceito pela Sandbox Interactive.
///
/// IMPORTANTE: usamos modo Normal (não Promíscuo). Promíscuo faz o adaptador entregar
/// todo o tráfego que passa pela rede (de qualquer dispositivo), não só o do nosso PC —
/// isso causava uso enorme de memória/CPU e ainda assim não pegava o tráfego certo do
/// jogo. Modo Normal já basta: só queremos pacotes enviados/recebidos por esta máquina,
/// que é exatamente o que o cliente do Albion troca com o servidor.
/// </summary>
public sealed class PacketCaptureService : IDisposable
{
    private static readonly int[] AlbionPorts = { 5055, 5056, 5057, 5058 };

    public event Action<PhotonEvent>? EventReceived;
    public event Action<PhotonOperationResponse>? OpResponseReceived;
    public event Action<PhotonOperationRequest>? OpRequestReceived;
    public event Action<string>? StatusChanged;

    private readonly List<ICaptureDevice> _devices = new();
    private bool _running;

    // Diagnóstico (calibração) — contadores simples, sem custo de memória relevante.
    public long DiagRawPackets;
    public long DiagAppPayloadsExtracted;
    public long DiagEventsDecoded;
    public readonly List<string> DiagSampleHex = new();
    private const int MaxSamples = 5;

    public bool Start()
    {
        if (_running) return true;

        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
        {
            StatusChanged?.Invoke("Nenhum adaptador de rede encontrado (Npcap instalado?)");
            return false;
        }

        string filter = string.Join(" or ", AlbionPorts.Select(p => $"udp port {p}"));
        int opened = 0;

        foreach (var device in devices)
        {
            // ANTES pulava adaptador de loopback ("nunca carrega tráfego do jogo") —
            // verdade só em conexão direta. Aceleradores de rota tipo ExitLag (e
            // Hamachi/Radmin) rodam um serviço local que intercepta o tráfego do
            // jogo via 127.0.0.1 antes de mandar pela rota otimizada — nesse caso o
            // pacote de verdade (porta 5055-5058, sem criptografia) passa pelo
            // LOOPBACK, não pela placa física. Pular esse adaptador fazia a captura
            // "ligar" (achava a placa física) mas nunca ver nenhum pacote, porque o
            // jogo nem manda tráfego por ela quando o acelerador está ativo.
            // Custo de incluir loopback é baixo: o filtro de porta already reduz
            // o volume entregue ao app, então não há overhead real de desempenho.
            try
            {
                device.Open(DeviceModes.None, 1000);
                try
                {
                    device.Filter = filter;
                    device.OnPacketArrival += OnPacketArrival;
                    device.StartCapture();
                    _devices.Add(device);
                    opened++;
                }
                catch
                {
                    // Abriu mas falhou no filtro/start (ex: driver não suporta o BPF
                    // usado) — sem fechar aqui, o handle nativo do pcap ficava aberto
                    // pro resto do processo (Stop() só itera _devices, e o device nunca
                    // entrou nela). Fecha explicitamente pra não vazar em quem alterna
                    // Iniciar/Parar captura várias vezes na mesma sessão.
                    try { device.Close(); } catch { /* já era */ }
                }
            }
            catch
            {
                // Adaptador pode estar indisponível (ex: VPN desconectada) ou não
                // suportar o Open — ignora e segue tentando os outros.
            }
        }

        _running = opened > 0;
        StatusChanged?.Invoke(_running
            ? $"Capturando em {opened} adaptador(es)"
            : "Não foi possível abrir nenhum adaptador de rede");
        return _running;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        DiagRawPackets++;
        try
        {
            var raw = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var udp = packet.Extract<UdpPacket>();
            if (udp?.PayloadData is null || udp.PayloadData.Length == 0) return;

            foreach (var appPayload in EnetPacketParser.ExtractApplicationPayloads(udp.PayloadData))
            {
                DiagAppPayloadsExtracted++;

                // Amostra dos payloads de comandos SendReliable/SendUnreliable de verdade
                // (não o pacote UDP inteiro) — é aqui dentro que a mensagem Photon
                // (Event/Operation) deveria estar. Grava em arquivo pra ler com precisão.
                if (DiagSampleHex.Count < MaxSamples)
                {
                    string hex = Convert.ToHexString(appPayload);
                    DiagSampleHex.Add(hex);
                    try
                    {
                        var dir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "diag_samples.txt"),
                            $"len={appPayload.Length}  {hex}\n");
                    }
                    catch { /* diagnóstico não pode derrubar a captura */ }
                }

                var msg = PhotonMessageParser.TryParseApplicationMessage(appPayload);
                if (msg is PhotonEvent evt)
                {
                    DiagEventsDecoded++;
                    EventReceived?.Invoke(evt);
                }
                // Operações (request/response) também são decodificadas, mas hoje não são
                // consumidas em runtime — só logadas em beta pra calibrar o evento de grupo
                // (o roster da party vem como operação, não como evento broadcast).
                else if (msg is PhotonOperationRequest req)
                {
                    OpRequestReceived?.Invoke(req);   // self-detection (movimento, op real 24)
                    DiagLogOperation("req", req.OperationCode, req.Parameters);
                }
                else if (msg is PhotonOperationResponse resp)
                {
                    OpResponseReceived?.Invoke(resp);   // self-detection (Join) e futuro grupo
                    DiagLogOperation("resp", resp.OperationCode, resp.Parameters);
                }
            }
        }
        catch
        {
            // Pacote fora do padrão esperado (fragmento, ruído de rede, etc.) —
            // descarta e segue capturando. Nunca deve derrubar o app.
        }
    }

    // Diagnóstico de operações (só beta): grava operações que carregam texto ou listas,
    // que é onde o roster da party deve estar. Capado pra não crescer demais nem virar
    // ruído. Arquivo: %LocalAppData%\XnomercyApp\ops_diag.txt. Some no Release final.
    private static int _opDiagCount;
    private static readonly object _opDiagLock = new();
    [System.Diagnostics.Conditional("DEBUG")]
    [System.Diagnostics.Conditional("BETA")]
    private static void DiagLogOperation(string kind, byte opCode, Dictionary<byte, object?> parms)
    {
        if (_opDiagCount >= 600) return;
        // Loga TODAS as operações (antes filtrava por texto/lista, mas as de party usam
        // GUID de conta em byte[16] e escapavam do filtro). Operações são raras, então o
        // volume é pequeno. Exceção: pula a telemetria de hardware do cliente (op real
        // 300: GPU/CPU/OS) — fora do escopo "app e Albion" combinado e inútil pra grupo.
        // Ignora ruído de alto volume que afoga o log (e estoura o teto antes da operação
        // de grupo aparecer): op 22 = seu próprio movimento (várias/seg). E op 300 =
        // telemetria de hardware (GPU/CPU/OS), fora do escopo combinado.
        if (parms.TryGetValue(253, out var rc) && rc is not null)
        {
            try { int real = Convert.ToInt32(rc); if (real == 22 || real == 300) return; } catch { }
        }
        lock (_opDiagLock)
        {
            if (_opDiagCount >= 600) return;
            _opDiagCount++;
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
                System.IO.Directory.CreateDirectory(dir);
                var text = string.Join(" ", parms.OrderBy(k => k.Key).Select(kv => $"[{kv.Key}]={OpVal(kv.Value)}"));
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ops_diag.txt"),
                    $"{kind} op={opCode} {text}\n");
            }
            catch { }
        }
    }

    private static string OpVal(object? v) => v switch
    {
        null => "null",
        // GUID de conta/objeto vem como byte[16] — mostra o hex (curto) pra dar pra cruzar
        // membros do grupo. Arrays maiores ficam só com o tamanho pra não explodir o log.
        byte[] b when b.Length <= 16 => $"byte[{b.Length}]={Convert.ToHexString(b)}",
        byte[] b => $"byte[{b.Length}]",
        object?[] a => $"arr[{a.Length}]={{{string.Join(",", a.Select(x => x?.ToString() ?? "null"))}}}",
        _ => v.ToString() ?? ""
    };

    public void Stop()
    {
        foreach (var device in _devices)
        {
            try
            {
                // Desregistra ANTES de fechar — sem isso o delegate ficava pendurado no
                // objeto nativo do adaptador, atrasando a liberação do handle em algumas
                // implementações do SharpPcap (relevante pra quem liga/desliga a captura
                // várias vezes na mesma sessão via "Pausar/Iniciar captura").
                device.OnPacketArrival -= OnPacketArrival;
                device.StopCapture();
                device.Close();
            }
            catch { /* ignora erro ao fechar */ }
        }
        _devices.Clear();
        _running = false;
    }

    public void Dispose() => Stop();
}
