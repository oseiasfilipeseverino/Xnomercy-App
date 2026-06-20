# Captura de rede (Loot Log / Fama & Prata / Medidor de Dano)

## Como funciona

Este módulo só **lê pacotes UDP** que já passam pela rede do próprio jogador, nas portas
conhecidas do Albion (5055-5058). Implementação própria (não copiada de nenhum projeto
de terceiros) do protocolo de transporte do Photon (estilo ENet) e do formato de
serialização "Protocol16" do Photon — ambos formatos de transporte genéricos do motor
Photon, documentados publicamente pelo SDK do Photon, não específicos do Albion.

Pipeline: `PacketCaptureService` (SharpPcap) → `EnetPacketParser` (extrai o payload de
cada comando do envelope UDP) → `PhotonMessageParser` + `Protocol16Deserializer`
(decodifica em `PhotonEvent`/`PhotonOperationRequest`/`PhotonOperationResponse`).

## Por que isso não dá risco de ban

- Só leitura passiva — nunca enviamos, modificamos ou injetamos pacote
- Nenhum overlay sobre a janela do jogo
- Nenhuma automação de ação no jogo
- Nenhuma leitura de memória do processo do Albion
- Mesma técnica usada pelo Statistics Analysis Tool (Triky313) e outras ferramentas
  amplamente usadas pela comunidade há anos sem problema

## O que falta calibrar (importante!)

Os **códigos de evento específicos do Albion** (qual número de `EventCode` corresponde
a "pegou loot do chão", "ganhou fama", "ganhou prata", dano causado/recebido) **não
estão fixados neste código**. Isso é esperado: esses números são internos do jogo,
mudam entre atualizações, e a forma correta de descobri-los é observando tráfego real
— é assim que toda ferramenta da comunidade (incluindo as que pesquisei) funciona.

**Próximo passo prático:** abrir o app com a aba Loot Log ativa enquanto joga uma
sessão curta (ex: abrir um baú, pegar item do chão), observar quais `EventCode`
aparecem no log bruto, e then mapear o código certo no `LootEventCodes.cs` (a ser
criado quando tivermos a confirmação). Até lá, a aba mostra **todos os eventos brutos
decodificados**, sem filtrar — é o ponto de partida pra essa calibração.
