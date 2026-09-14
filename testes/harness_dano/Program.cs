using System.Collections.Concurrent;
using System.Reflection;
using XnomercyApp.Network;

// Exercita o DamageMeterTracker REAL (a DLL compilada), nao uma reimplementacao.
// O estado do PlayerRegistry e privado, entao e ajustado por reflexao.

int falhas = 0;
void Checar(bool cond, string rotulo)
{
    if (!cond) falhas++;
    Console.WriteLine($"  {(cond ? "ok   " : "FALHA")}  {rotulo}");
}

var reg = typeof(PlayerRegistry);
var byId = (ConcurrentDictionary<long, PlayerInfo>)reg
    .GetField("_byId", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
var setSelf = reg.GetProperty("SelfObjectId")!.GetSetMethod(nonPublic: true)!;

void Self(long? id) => setSelf.Invoke(null, new object?[] { id });
void Nome(long id, string nome) => byId[id] = new PlayerInfo { Name = nome };
void Limpar() { byId.Clear(); Self(null); }

// HealthUpdate: [2]=variacao (negativo=dano) [6]=causador [7]=habilidade [252]=6
PhotonEvent Hit(long causador, int dano, int skill = -1) => new(0, new Dictionary<byte, object?>
{
    [2] = (double)(-dano), [6] = causador, [7] = skill, [252] = (short)6,
});

long Dano(DamageMeterTracker t, string nome) =>
    t.Snapshot().FirstOrDefault(e => e.Name == nome)?.Damage ?? 0;
bool Existe(DamageMeterTracker t, string nome) =>
    t.Snapshot().Any(e => e.Name == nome);

// ── 1. O BUG: hit antes do nome chegar ───────────────────────────────────────
Console.WriteLine("\n-- 1. dano causado ANTES do nome chegar nao pode sumir");
Limpar(); Self(1);
var t1 = new DamageMeterTracker();
t1.HandleEvent(Hit(500, 1000));          // nome ainda desconhecido
t1.HandleEvent(Hit(500, 2000));
Nome(500, "Fulano");                     // nome chega
t1.HandleEvent(Hit(500, 300));           // proximo hit, ja com nome
Checar(Dano(t1, "Fulano") == 3300, $"Fulano tem os 3 hits: {Dano(t1, "Fulano")} (esperado 3300; a versao antiga dava 300)");
Checar(!Existe(t1, "#500"), "e nao sobra linha #500 escondida com os 3000 do comeco");

// ── 2. Nome resolve e a pessoa PARA de bater ─────────────────────────────────
Console.WriteLine("\n-- 2. nome resolve depois do ultimo hit (morreu, saiu da luta)");
Limpar(); Self(1);
var t2 = new DamageMeterTracker();
t2.HandleEvent(Hit(600, 5000));
Nome(600, "Beltrano");                   // sem hit nenhum depois
Checar(Dano(t2, "Beltrano") == 5000, $"o Snapshot incorpora mesmo sem hit novo: {Dano(t2, "Beltrano")}");
Checar(!Existe(t2, "#600"), "sem linha #600 sobrando");

// ── 3. VOCE, antes de o app te identificar ───────────────────────────────────
// O caso mais importante: o historico da sessao le a linha "Voce".
Console.WriteLine("\n-- 3. seu dano antes de o app descobrir quem voce e");
Limpar(); Self(null);                    // app ainda nao sabe seu id
var t3 = new DamageMeterTracker();
t3.HandleEvent(Hit(700, 4000));
t3.HandleEvent(Hit(700, 1000));
Self(700);                               // PRIMEIRA descoberta — nao e troca de zona
Checar(Dano(t3, "Você") == 5000, $"a linha Voce tem os 5000: {Dano(t3, "Você")}");
Checar(!Existe(t3, "#700"), "sem #700 escondido (o historico salvaria um numero menor)");

// ── 4. Troca de zona NAO pode misturar pessoas ───────────────────────────────
// A razao de existir o carimbo por nome: o Albion recicla ObjectId por zona.
Console.WriteLine("\n-- 4. ObjectId reciclado em outra zona nao herda dano de outra pessoa");
Limpar(); Self(100);
var t4 = new DamageMeterTracker();
t4.HandleEvent(Hit(800, 9000));          // zona A: #800 e alguem sem nome
Self(200);                               // troca de zona (id conhecido -> outro id conhecido)
Nome(800, "OutraPessoa");                // zona B: o 800 agora e OUTRA pessoa
t4.HandleEvent(Hit(800, 100));
Checar(Dano(t4, "OutraPessoa") == 100, $"OutraPessoa so tem o dela: {Dano(t4, "OutraPessoa")} (nao 9100)");
Checar(Dano(t4, "#800") == 9000, $"os 9000 da zona A continuam como #800: {Dano(t4, "#800")}");

// ── 5. Caminho normal nao mudou ──────────────────────────────────────────────
Console.WriteLine("\n-- 5. nome conhecido desde o comeco continua igual");
Limpar(); Self(1);
Nome(900, "Ciclano");
var t5 = new DamageMeterTracker();
t5.HandleEvent(Hit(900, 700, skill: 3213));
t5.HandleEvent(Hit(900, 300, skill: 3213));
Checar(Dano(t5, "Ciclano") == 1000, $"soma normal: {Dano(t5, "Ciclano")}");
Checar(t5.SnapshotBySpell("Ciclano").Any(s => s.SpellIndex == 3213 && s.Damage == 1000),
       "a quebra por habilidade soma certo");

// ── 6. Quebra por habilidade tambem e incorporada ────────────────────────────
Console.WriteLine("\n-- 6. a quebra por habilidade tambem vem junto quando o nome resolve");
Limpar(); Self(1);
var t6 = new DamageMeterTracker();
t6.HandleEvent(Hit(950, 800, skill: 4053));   // sem nome
Nome(950, "Mago");
t6.HandleEvent(Hit(950, 200, skill: 4053));
var sk = t6.SnapshotBySpell("Mago").FirstOrDefault(s => s.SpellIndex == 4053);
Checar(sk != null && sk.Damage == 1000, $"habilidade 4053 com os dois hits: {sk?.Damage}");

// ── 7. Reset limpa os pendentes ──────────────────────────────────────────────
Console.WriteLine("\n-- 7. zerar limpa tudo, inclusive o que estava pendente");
Limpar(); Self(1);
var t7 = new DamageMeterTracker();
t7.HandleEvent(Hit(990, 5000));
t7.Reset();
Nome(990, "Fantasma");
Checar(Dano(t7, "Fantasma") == 0 && t7.Snapshot().Count == 0,
       "depois de Reset nao ressuscita dano pendente");

Console.WriteLine(falhas == 0 ? "\nOK: nenhum dano se perde e ninguem herda dano de outro"
                              : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;
