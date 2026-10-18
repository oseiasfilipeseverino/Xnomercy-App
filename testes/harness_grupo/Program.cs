using System.Reflection;
using XnomercyApp.Network;

// Exercita o PartyTracker e o EventOffset REAIS (a DLL compilada) e o
// PlayerRegistry.IsInParty que o consome — o filtro "Só meu grupo" do Loot Log e
// do Medidor de Dano. Formatos dos eventos: código-fonte do SAT (Triky313).
// Números: SAT + deslocamento; nos pacotes reais de 12/10 o deslocamento era +1.

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
EventOffset.Reset();

Console.WriteLine("\n-- numeros do jogo (pacotes de 12/10: SAT + 1)");
Checar(EventOffset.Delta == 1 && GameEventCodes.GrabbedLoot == 279, "parte de +1 e loot em 279 (o calibrado em 01/09)");

Console.WriteLine("\n-- entrou no grupo: lista completa de uma vez (231)");
PartyTracker.Limpar();
Checar(PartyTracker.Handle(Ev(231, new() { [8] = G(1).Concat(G(2)).Concat(G(3)).ToArray(), [9] = new string?[] { "gayzaoviadao", "LapreoTheKing", "SrAmok" } })) == "entrou_no_grupo",
       "PartyJoined em 231 reconhecido");
Checar(PlayerRegistry.IsInParty("LapreoTheKing") && PlayerRegistry.IsInParty("srAmok"), "membros da lista estao no grupo (sem esperar rateio de fama)");
Checar(PlayerRegistry.MembrosNoGrupo() == 2, $"conta 2 companheiros (fora voce) ({PlayerRegistry.MembrosNoGrupo()})");
Checar(!PlayerRegistry.IsInParty("Estranho"), "quem nao esta na lista nao esta no grupo");
Checar(PartyTracker.Handle(Ev(230, new() { [8] = G(9), [9] = new string?[] { "Outro" } })) == null && !PlayerRegistry.IsInParty("Outro"),
       "230 (outro evento com +1) nao e' tratado como entrada no grupo");

Console.WriteLine("\n-- alguem entra e alguem sai, na hora");
Checar(PartyTracker.Handle(Ev(233, new() { [1] = G(5), [2] = "Golldden" })) == "membro_entrou" && PlayerRegistry.IsInParty("Golldden"), "PartyPlayerJoined (233): entrou");
Checar(PartyTracker.Handle(Ev(234, new() { [1] = G(5) })) == null && PlayerRegistry.IsInParty("Golldden"),
       "234 = 'grupo mudou de ordem' com +1: NAO tira ninguem (o 1.0.63 tirava)");
Checar(PartyTracker.Handle(Ev(235, new() { [1] = G(5) })) == "membro_saiu" && !PlayerRegistry.IsInParty("Golldden"), "PartyPlayerLeft (235): saiu NA HORA");

Console.WriteLine("\n-- grupo desfeito (232)");
Checar(PartyTracker.Handle(Ev(232, new() { [0] = 1L })) == "grupo_desfeito" && !PartyTracker.Ativo && !PlayerRegistry.IsInParty("LapreoTheKing"),
       "PartyDisbanded zera o grupo");
PartyTracker.Handle(Ev(231, new() { [8] = G(1).Concat(G(2)).ToArray(), [9] = new string?[] { "gayzaoviadao", "LapreoTheKing" } }));
Checar(PartyTracker.Handle(Ev(232, new() { [1] = G(7), [2] = "Fulano" })) == null && PartyTracker.Ativo,
       "232 trazendo membro nao zera (nao e' o 'desfeito')");

Console.WriteLine("\n-- o que NAO pode ser confundido com grupo");
var antes = PartyTracker.Nomes().Count;
Checar(PartyTracker.Handle(Ev(229, new() { [1] = G(9), [2] = "Convidador" })) == null && !PlayerRegistry.IsInParty("Convidador"), "convite de grupo nao entra");
Checar(PartyTracker.Handle(Ev(235, new() { [1] = G(77) })) == null && PartyTracker.Nomes().Count == antes, "saida de quem nao era do grupo e ignorada");
Checar(PartyTracker.Handle(Ev(231, new() { [8] = G(1).Concat(G(2)).ToArray(), [9] = new string?[] { "so um nome" } })) == null, "lista com quantidades diferentes nao vale");

Console.WriteLine("\n-- eu sai do grupo: o grupo deixa de ser meu");
PartyTracker.Handle(Ev(235, new() { [1] = G(1) }));
Checar(!PartyTracker.Ativo && !PlayerRegistry.IsInParty("LapreoTheKing"), "saida do proprio jogador limpa o grupo");

Console.WriteLine("\n-- calibracao automatica: um patch muda a numeracao");
EventOffset.Observe(Ev(209, new() { [0] = 4151L, [4] = new string?[] { "@_Owner", "@_Guild" }, [5] = new string?[] { "owner", "noaccess" } }));
Checar(EventOffset.Delta == 0 && GameEventCodes.GrabbedLoot == 278, "lista de acesso em 209: deslocamento 0, loot vai pra 278 sozinho");
PartyTracker.Limpar();
Checar(PartyTracker.Handle(Ev(230, new() { [8] = G(1).Concat(G(3)).ToArray(), [9] = new string?[] { "gayzaoviadao", "SrAmok" } })) == "entrou_no_grupo",
       "com deslocamento 0, o grupo passa a vir em 230");
EventOffset.Observe(Ev(279, new() { [0] = 1L, [1] = "MOB", [2] = "SrAmok", [3] = false, [4] = 1234, [5] = 1 }));
Checar(EventOffset.Delta == 1 && GameEventCodes.GrabbedLoot == 279, "loot de outro jogador em 279: volta pra +1");
Checar(EventOffset.Observe(Ev(279, new() { [0] = 1L, [2] = "SrAmok", [3] = true, [5] = 12159299 })) == null,
       "rateio de fama (sem [1] nem [4]) nao serve de ancora");
Checar(EventOffset.Observe(Ev(29, new() { [0] = 1L, [1] = "Fulano", [2] = "x", [3] = true, [4] = 1, [5] = 2 })) == null,
       "evento fora da faixa (29) nao mexe na calibracao");

Console.WriteLine("\n-- sem grupo exato, o plano B (rateio de fama) continua valendo");
PartyTracker.Limpar();
var marcar = typeof(PlayerRegistry).GetMethod("MarcarNoGrupo", BindingFlags.NonPublic | BindingFlags.Static)!;
marcar.Invoke(null, new object?[] { "Criminouso" });
Checar(PlayerRegistry.IsInParty("Criminouso"), "rateio de fama ainda marca o grupo quando nao ha lista exata");
EventOffset.Reset();

Console.WriteLine(falhas == 0 ? "\nOK: grupo exato e calibracao automatica" : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
