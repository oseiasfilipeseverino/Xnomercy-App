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

    // Dedup entre adaptadores: abrimos TODOS os dispositivos de rede simultaneamente
    // (necessário pra ExitLag/Hamachi, que só mandam o tráfego real pelo loopback) —
    // mas se mais de um adaptador enxergar o MESMO pacote de verdade (VPN, virtual
    // switch, múltiplas NICs ativas), cada evento era processado uma vez por
    // adaptador, duplicando loot/dano/fama no app inteiro. Confirmado com captura
    // real: o mesmo GrabbedLoot aparecia 3x idêntico. Descarta payloads UDP
    // repetidos vistos há poucos milissegundos, não importa qual adaptador entregou.
    private readonly object _dedupLock = new();
    private readonly Dictionary<ulong, long> _recentPayloadHashes = new();
    private const int DedupWindowMs = 500;
    private const int DedupPruneThreshold = 4096;

    // ── Descoberta de porta pelo CONTEÚDO ─────────────────────────────────────
    // Aceleradores de rota (ExitLag e afins) reencaminham o UDP do jogo trocando as
    // portas por valores ALEATÓRIOS a cada sessão. Medido com o Diagnose(): zero
    // pacote em 5055-5058 e o tráfego do jogo saindo como 53812->19324. Ou seja,
    // filtrar por porta fixa nunca vai funcionar nesse cenário — e adicionar as
    // portas "novas" na lista também não, porque elas mudam na próxima vez.
    //
    // Solução: escutar todo UDP e reconhecer o jogo pelo que o pacote É (Photon
    // decodificável), não por onde ele passa. Assim que um par de portas entrega
    // Photon de verdade algumas vezes, ele é "aprendido" e passa a ser rota rápida;
    // enquanto isso, a tentativa de descoberta é limitada pra não gastar CPU
    // tentando decodificar todo tráfego da máquina (Discord, torrent, etc).
    private readonly HashSet<int> _learnedPorts = new();
    private readonly Dictionary<int, int> _discoveryHits = new();
    private readonly object _learnLock = new();
    private int _discoveryAttempts;
    private const int MaxDiscoveryAttempts = 60000;   // teto de tentativas por sessão
    private const int HitsToLearn = 3;                // decodificações válidas p/ confiar na porta

    /// <summary>Portas confirmadas por conteúdo nesta sessão (diagnóstico/UI).</summary>
    public IReadOnlyCollection<int> LearnedPorts
    {
        get { lock (_learnLock) return _learnedPorts.ToList(); }
    }

    private bool IsKnownGamePort(int src, int dst)
    {
        if (Array.IndexOf(AlbionPorts, src) >= 0 || Array.IndexOf(AlbionPorts, dst) >= 0) return true;
        lock (_learnLock) return _learnedPorts.Contains(src) || _learnedPorts.Contains(dst);
    }

    /// <summary>Registra que este par de portas entregou Photon válido. Ao bater
    /// HitsToLearn, a porta entra na rota rápida.</summary>
    private void NoteDiscovery(int src, int dst)
    {
        lock (_learnLock)
        {
            foreach (int p in new[] { src, dst })
            {
                if (_learnedPorts.Contains(p)) continue;
                _discoveryHits[p] = _discoveryHits.GetValueOrDefault(p) + 1;
                if (_discoveryHits[p] >= HitsToLearn)
                {
                    _learnedPorts.Add(p);
                    StatusChanged?.Invoke($"Porta do jogo detectada automaticamente: {p} " +
                                          "(acelerador de rota / porta fora do padrão)");
                }
            }
        }
    }

    /// <summary>
    /// Diagnóstico de rede: abre TODOS os adaptadores com filtro só de "udp" (sem
    /// travar nas portas 5055-5058) e conta, por adaptador e por porta, o que
    /// realmente passa. Devolve um relatório em texto.
    ///
    /// Existe porque a captura não funcionar com acelerador de rota (ExitLag) foi
    /// "consertada" uma vez por hipótese — supondo que o tráfego ia pelo loopback —
    /// e continuou sem funcionar. Sem medir, qualquer correção nova é chute: este
    /// método responde objetivamente (a) quais adaptadores dá pra abrir, (b) se
    /// chega QUALQUER pacote UDP, e (c) em que porta o jogo está de verdade, que é
    /// exatamente o que falta pra saber se o filtro de porta é o problema ou se o
    /// ExitLag encapsula/cifra o tráfego (caso em que nenhum filtro resolveria).
    ///
    /// Não usa o filtro de porta de propósito. Roda por poucos segundos e é
    /// disparado manualmente, então o volume extra é aceitável.
    /// </summary>
    public string Diagnose(int seconds = 20)
    {
        if (_running)
            return "Pare a captura antes de rodar o diagnóstico de rede.";

        var report = new System.Text.StringBuilder();
        report.AppendLine($"=== Diagnostico de rede — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        report.AppendLine($"Janela de escuta: {seconds}s | filtro: 'udp' (SEM filtro de porta)");
        report.AppendLine($"Portas que a captura normal exige: {string.Join(", ", AlbionPorts)}");
        report.AppendLine();

        var devices = CaptureDeviceList.Instance;
        report.AppendLine($"Adaptadores encontrados: {devices.Count}");

        // (descricao do adaptador, porta) -> quantidade
        var tally = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var opened = new List<ICaptureDevice>();

        foreach (var device in devices)
        {
            string desc = string.IsNullOrWhiteSpace(device.Description) ? device.Name : device.Description;
            try
            {
                device.Open(DeviceModes.None, 1000);
                try
                {
                    device.Filter = "udp";
                    PacketArrivalEventHandler handler = (_, e) =>
                    {
                        try
                        {
                            var raw = e.GetPacket();
                            var udp = Packet.ParsePacket(raw.LinkLayerType, raw.Data).Extract<UdpPacket>();
                            if (udp is null) return;
                            // Conta as duas pontas: o jogo pode aparecer como origem
                            // (nosso cliente mandando) ou destino (servidor respondendo).
                            tally.AddOrUpdate($"{desc}|{udp.SourcePort}->{udp.DestinationPort}", 1, (_, v) => v + 1);
                        }
                        catch { /* pacote fora do padrao: ignora */ }
                    };
                    device.OnPacketArrival += handler;
                    device.StartCapture();
                    opened.Add(device);
                    report.AppendLine($"  [OK]    {desc}");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"  [FALHA no filtro/start] {desc} — {ex.Message}");
                    try { device.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  [NAO ABRIU] {desc} — {ex.Message}");
            }
        }

        report.AppendLine();
        if (opened.Count == 0)
        {
            report.AppendLine("NENHUM adaptador pode ser aberto. Npcap instalado? Rodando como administrador?");
            return report.ToString();
        }

        System.Threading.Thread.Sleep(seconds * 1000);

        foreach (var d in opened)
        {
            try { d.StopCapture(); d.Close(); } catch { }
        }

        var rows = tally.OrderByDescending(kv => kv.Value).ToList();
        report.AppendLine($"Fluxos UDP observados: {rows.Count} | pacotes no total: {rows.Sum(r => r.Value)}");
        report.AppendLine();

        if (rows.Count == 0)
        {
            report.AppendLine("ZERO pacote UDP em qualquer adaptador.");
            report.AppendLine("=> O problema NAO e' o filtro de porta: o Npcap nao esta entregando");
            report.AppendLine("   o trafego pra este processo. Suspeitas: falta de permissao de");
            report.AppendLine("   administrador, Npcap instalado sem suporte a loopback, ou o");
            report.AppendLine("   acelerador entregando o trafego por um caminho que o Npcap nao ve.");
            return report.ToString();
        }

        report.AppendLine("--- Top 40 fluxos (adaptador | origem->destino : pacotes) ---");
        foreach (var kv in rows.Take(40))
            report.AppendLine($"  {kv.Key} : {kv.Value}");

        // A pergunta que importa: alguma dessas portas e' das que a captura exige?
        var albionHits = rows.Where(kv => AlbionPorts.Any(p =>
                                  kv.Key.Contains($"|{p}->") || kv.Key.EndsWith($"->{p}")))
                             .ToList();
        report.AppendLine();
        if (albionHits.Count > 0)
        {
            report.AppendLine($"ENCONTRADO trafego nas portas do Albion ({albionHits.Sum(k => k.Value)} pacotes):");
            foreach (var kv in albionHits.Take(10)) report.AppendLine($"  {kv.Key} : {kv.Value}");
            report.AppendLine("=> As portas estao certas. Se a captura normal nao ve nada, o problema");
            report.AppendLine("   esta no adaptador escolhido ou no filtro sendo aplicado.");
        }
        else
        {
            report.AppendLine("NENHUM pacote nas portas 5055-5058, mas HA trafego UDP.");
            report.AppendLine("=> O jogo (via acelerador) NAO esta usando as portas esperadas.");
            report.AppendLine("   Compare a lista acima com o trafego sem o acelerador ligado pra");
            report.AppendLine("   descobrir a porta nova — se for porta fixa, basta incluir no filtro.");
            report.AppendLine("   Se o acelerador encapsular/cifrar, nenhum filtro resolve.");
        }
        return report.ToString();
    }

    public bool Start()
    {
        if (_running) return true;

        var devices = CaptureDeviceList.Instance;
        if (devices.Count == 0)
        {
            StatusChanged?.Invoke("Nenhum adaptador de rede encontrado (Npcap instalado?)");
            return false;
        }

        // Filtro "udp" puro em vez de travado em 5055-5058: com acelerador de rota
        // (ExitLag) o jogo sai em porta ALEATÓRIA — medido: 53812->19324, zero em
        // 5055-5058. O reconhecimento do que é jogo passou pro conteúdo do pacote
        // (ver NoteDiscovery/IsKnownGamePort), então o filtro aqui só evita carregar
        // TCP à toa. Também deixa o app sobreviver se a Sandbox trocar de porta.
        const string filter = "udp";
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
        // Registra QUAIS adaptadores entraram (e quais falharam). Antes só existia a
        // contagem, que não ajudava a diagnosticar nada: "Capturando em 3 adaptador(es)"
        // não diz se o adaptador que o acelerador de rota usa está entre eles.
        LogAdapters();
        StatusChanged?.Invoke(_running
            ? $"Capturando em {opened} adaptador(es)"
            : "Não foi possível abrir nenhum adaptador de rede");
        return _running;
    }

    private void LogAdapters()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
            System.IO.Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Captura iniciada — adaptadores em uso:");
            foreach (var d in _devices)
                sb.AppendLine($"  ATIVO: {(string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description)}");
            foreach (var d in CaptureDeviceList.Instance)
            {
                if (_devices.Any(x => x.Name == d.Name)) continue;
                sb.AppendLine($"  fora:  {(string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description)}");
            }
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ops_diag.txt"), sb.ToString());
        }
        catch { /* diagnostico nunca derruba a captura */ }
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

            int srcPort = udp.SourcePort, dstPort = udp.DestinationPort;
            bool known = IsKnownGamePort(srcPort, dstPort);
            if (!known)
            {
                // Porta desconhecida: só vale tentar decodificar enquanto estamos
                // "procurando" o jogo. Sem esse teto, todo tráfego UDP da máquina
                // (Discord, navegador, torrent...) passaria pelo parser Photon pra
                // sempre, queimando CPU à toa — o filtro agora é só "udp".
                if (_discoveryAttempts >= MaxDiscoveryAttempts) return;
                _discoveryAttempts++;
            }

            if (IsDuplicatePayload(udp.PayloadData)) return;

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
                    // Confirma a porta pelo CONTEÚDO. Exige EventCode >= 0 (ou seja,
                    // o parâmetro 252 presente, que é o código real do evento Albion):
                    // é o que separa um evento de jogo de verdade de um pacote UDP
                    // qualquer que por azar passou pelo parser. Sem essa exigência, um
                    // falso positivo poderia "aprender" a porta do Discord e injetar
                    // lixo no medidor de dano.
                    if (!known && evt.EventCode >= 0) NoteDiscovery(srcPort, dstPort);
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

    // True se este payload UDP (byte a byte) já foi visto há menos de DedupWindowMs —
    // nesse caso é o mesmo pacote de rede chegando por outro adaptador, não um evento
    // novo. Chamado de threads de captura diferentes (uma por adaptador), daí o lock.
    private bool IsDuplicatePayload(byte[] payload)
    {
        ulong hash = Fnv1a64(payload);
        long now = Environment.TickCount64;
        lock (_dedupLock)
        {
            if (_recentPayloadHashes.TryGetValue(hash, out var seenAt) && now - seenAt < DedupWindowMs)
                return true;

            _recentPayloadHashes[hash] = now;
            if (_recentPayloadHashes.Count > DedupPruneThreshold)
            {
                foreach (var key in _recentPayloadHashes
                             .Where(kv => now - kv.Value >= DedupWindowMs)
                             .Select(kv => kv.Key).ToList())
                    _recentPayloadHashes.Remove(key);
            }
            return false;
        }
    }

    private static ulong Fnv1a64(byte[] data)
    {
        const ulong prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
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
        lock (_dedupLock) _recentPayloadHashes.Clear();
        // Portas aprendidas valem só pra sessão: o acelerador sorteia outras na
        // próxima vez, então guardar não ajuda e só arriscaria escutar porta errada.
        lock (_learnLock)
        {
            _learnedPorts.Clear();
            _discoveryHits.Clear();
        }
        _discoveryAttempts = 0;
        _running = false;
    }

    public void Dispose() => Stop();
}
