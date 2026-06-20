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
        int skipped = 0;

        foreach (var device in devices)
        {
            // Loopback não carrega tráfego do jogo (isso vai pra rede física/wifi) —
            // pular evita abrir adaptador inútil e economiza memória.
            if (device.Description?.Contains("Loopback", StringComparison.OrdinalIgnoreCase) == true)
            {
                skipped++;
                continue;
            }

            try
            {
                device.Open(DeviceModes.None, 1000);
                device.Filter = filter;
                device.OnPacketArrival += OnPacketArrival;
                device.StartCapture();
                _devices.Add(device);
                opened++;
            }
            catch
            {
                // Adaptador pode estar indisponível (ex: VPN desconectada) ou não
                // suportar o filtro — ignora e segue tentando os outros.
            }
        }

        _running = opened > 0;
        StatusChanged?.Invoke(_running
            ? $"Capturando em {opened} adaptador(es) ({skipped} loopback ignorado(s))"
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

                if (PhotonMessageParser.TryParseApplicationMessage(appPayload) is PhotonEvent evt)
                {
                    DiagEventsDecoded++;
                    EventReceived?.Invoke(evt);
                }
            }
        }
        catch
        {
            // Pacote fora do padrão esperado (fragmento, ruído de rede, etc.) —
            // descarta e segue capturando. Nunca deve derrubar o app.
        }
    }

    public void Stop()
    {
        foreach (var device in _devices)
        {
            try
            {
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
