using PacketDotNet;
using SharpPcap;

namespace XnomercyApp.Network;

/// <summary>
/// Captura passiva de pacotes UDP nas portas conhecidas do Albion Online (5055-5058).
/// Só LÊ tráfego de rede — nunca envia, modifica ou injeta nada. Não lê memória do
/// processo do jogo nem desenha overlay. Ver Network/README.md para o porquê disso
/// ser aceito pela Sandbox Interactive.
/// </summary>
public sealed class PacketCaptureService : IDisposable
{
    private static readonly int[] AlbionPorts = { 5055, 5056, 5057, 5058 };

    public event Action<PhotonEvent>? EventReceived;
    public event Action<string>? StatusChanged;

    private readonly List<ICaptureDevice> _devices = new();
    private bool _running;

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
            try
            {
                device.Open(DeviceModes.Promiscuous | DeviceModes.DataTransferUdp, 1000);
                device.Filter = filter;
                device.OnPacketArrival += OnPacketArrival;
                device.StartCapture();
                _devices.Add(device);
                opened++;
            }
            catch
            {
                // Adaptador pode estar indisponível (ex: VPN desconectada) — ignora e
                // segue tentando os outros; só falha de verdade se nenhum abrir.
            }
        }

        _running = opened > 0;
        StatusChanged?.Invoke(_running
            ? $"Capturando em {opened} adaptador(es) de rede"
            : "Não foi possível abrir nenhum adaptador de rede");
        return _running;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var udp = packet.Extract<UdpPacket>();
            if (udp?.PayloadData is null || udp.PayloadData.Length == 0) return;

            foreach (var appPayload in EnetPacketParser.ExtractApplicationPayloads(udp.PayloadData))
            {
                if (PhotonMessageParser.TryParseApplicationMessage(appPayload) is PhotonEvent evt)
                    EventReceived?.Invoke(evt);
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
