using System.Reflection;
using XnomercyApp.Network;

// Exercita o PartyTracker REAL (a DLL compilada) e o PlayerRegistry.IsInParty que
// o consome — o filtro "Só meu grupo" do Loot Log e do Medidor de Dano.
// Os eventos têm o formato que o código-fonte do SAT (Triky313) documenta.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

byte[] G(int n) { var b = new byte[16]; b[0] = (byte)n; b[15] = 0xAB; return b; }
PhotonEvent Ev(int code, Dictionary<byte, object?> p) { p[252] = (short)code; return new(1, p); }

var setSelfName = typeof(PlayerRegistry).GetProperty("SelfName")!.GetSetMethod(nonPublic: true)!;
setSelfName.Invoke(null, new object?[] { "gayzaoviadao" });

Console.WriteLine("\n-- entrou no grupo: lista completa de uma vez");
PartyTracker.Limpar();
var joined = Ev(230, new() { [8] = G(1).Concat(G(2)).Concat(G(3)).ToArray(), [9] = new string?[] { "gayzaoviadao", "LapreoTheKing", "SrAmok" } });
Checar(PartyTracker.Handle(joined) == "entrou_no_grupo", "PartyJoined (230) reconhecido");
Checar(PlayerRegistry.IsInParty("LapreoTheKing") && PlayerRegistry.IsInParty("srAmok"), "membros da lista estao no grupo (sem esperar rateio de fama)");
Checar(PlayerRegistry.MembrosNoGrupo() == 2, $"conta 2 companheiros (fora voce) ({PlayerRegistry.MembrosNoGrupo()})");
Checar(!PlayerRegistry.IsInParty("Estranho"), "quem nao esta na lista nao esta no grupo");

Console.WriteLine("\n-- tambem no numero +1 (o jogo ganhou um evento no meio), e GUIDs em array");
PartyTracker.Limpar();
var joined231 = Ev(231, new() { [8] = new object?[] { G(1), G(4) }, [9] = new object?[] { "gayzaoviadao", "Gethon" } });
Checar(PartyTracker.Handle(joined231) == "entrou_no_grupo" && PlayerRegistry.IsInParty("Gethon"), "PartyJoined em 231 com GUIDs em array");

Console.WriteLine("\n-- alguem entra e alguem sai, na hora");
Checar(PartyTracker.Handle(Ev(232, new() { [1] = G(5), [2] = "Golldden" })) == "membro_entrou" && PlayerRegistry.IsInParty("Golldden"), "PartyPlayerJoined (232): entrou");
Checar(PartyTracker.Handle(Ev(234, new() { [1] = G(5) })) == "membro_saiu" && !PlayerRegistry.IsInParty("Golldden"), "PartyPlayerLeft (234): saiu NA HORA (antes ficava 2 min)");
Checar(PartyTracker.Handle(Ev(235, new() { [1] = G(4) })) == "membro_saiu" && !PlayerRegistry.IsInParty("Gethon"), "saida tambem no 235");

Console.WriteLine("\n-- o que NAO pode ser confundido com grupo");
var antes = PartyTracker.Nomes().Count;
Checar(PartyTracker.Handle(Ev(229, new() { [1] = G(9), [2] = "Convidador" })) == null && !PlayerRegistry.IsInParty("Convidador"), "convite de grupo (228/229) nao entra");
Checar(PartyTracker.Handle(Ev(234, new() { [1] = G(77) })) == null && PartyTracker.Nomes().Count == antes, "saida de quem nao era do grupo e ignorada");
Checar(PartyTracker.Handle(Ev(230, new() { [8] = G(1).Concat(G(2)).ToArray(), [9] = new string?[] { "so um nome" } })) == null, "lista com quantidades diferentes nao vale");
Checar(PartyTracker.Handle(Ev(29, new() { [1] = G(6), [2] = "NewCharacter" })) == null, "evento de outra faixa com o mesmo formato nao vale");

Console.WriteLine("\n-- eu sai do grupo: o grupo deixa de ser meu");
PartyTracker.Handle(Ev(230, new() { [8] = G(1).Concat(G(2)).ToArray(), [9] = new string?[] { "gayzaoviadao", "LapreoTheKing" } }));
PartyTracker.Handle(Ev(234, new() { [1] = G(1) }));
Checar(!PartyTracker.Ativo && !PlayerRegistry.IsInParty("LapreoTheKing"), "saida do proprio jogador limpa o grupo");

Console.WriteLine("\n-- sem grupo exato, o plano B (rateio de fama) continua valendo");
PartyTracker.Limpar();
var marcar = typeof(PlayerRegistry).GetMethod("MarcarNoGrupo", BindingFlags.NonPublic | BindingFlags.Static)!;
marcar.Invoke(null, new object?[] { "Criminouso" });
Checar(PlayerRegistry.IsInParty("Criminouso"), "rateio de fama ainda marca o grupo quando nao ha lista exata");

Console.WriteLine(falhas == 0 ? "\nOK: grupo exato pelos eventos do jogo" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
