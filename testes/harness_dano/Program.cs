using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using XnomercyApp.Network;

// Nunca nos dados de verdade: pasta temporaria se o rodar.ps1 nao definiu (Network/PastaDados.cs).
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XNOMERCY_DADOS")))
    Environment.SetEnvironmentVariable("XNOMERCY_DADOS", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xnomercy_teste_" + Guid.NewGuid().ToString("N")));

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

// ── 8. Evento 7: varios golpes no mesmo pacote (18/10/2026) ────────────────
// Ate a 1.0.66 o medidor so lia o 6 e todo dano que o servidor agrupava sumia.
Console.WriteLine("\n-- 8. evento 7 (HealthUpdates): varios golpes num pacote so");
Limpar(); Self(1);
Nome(31, "Arqueiro"); Nome(32, "Lanceiro");
var t8 = new DamageMeterTracker();
t8.HandleEvent(new PhotonEvent(0, new Dictionary<byte, object?>
{
    [0] = 4942L,
    [2] = new float[] { -370f, -73f, -50f },
    [3] = new float[] { 2267f, 2194f, 2144f },
    [6] = new short[] { 31, 32, 31 },
    [7] = new short[] { 2974, 2977, 2974 },
    [252] = (short)7,
}));
Checar(Dano(t8, "Arqueiro") == 420 && Dano(t8, "Lanceiro") == 73,
       $"os 3 golpes contam, cada um pro seu causador: Arqueiro {Dano(t8, "Arqueiro")} (420), Lanceiro {Dano(t8, "Lanceiro")} (73)");
Checar(t8.SnapshotBySpell("Arqueiro").Any(x => x.SpellIndex == 2974 && x.Damage == 420), "a habilidade de cada golpe vem da lista [7]");

// ── 9. Pacotes REAIS (testes/pacotes_reais.txt, capturados em 12/10) ──────────
// Copiados do diag_samples so os eventos sem nome nem texto (6, 7, 91, 96, 123).
Console.WriteLine("\n-- 9. pacotes reais: todo dano dos eventos 6 e 7 entra");
Limpar(); Self(1);
string? Achar(string arq)
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        var f = Path.Combine(d.FullName, arq);
        if (File.Exists(f)) return f;
        f = Path.Combine(d.FullName, "testes", arq);
        if (File.Exists(f)) return f;
    }
    return null;
}
var real = Achar("pacotes_reais.txt");
Checar(real != null, "achei testes/pacotes_reais.txt");
if (real != null)
{
    var t9 = new DamageMeterTracker();
    double esperado = 0; int eventos7 = 0;
    foreach (var linha in File.ReadAllLines(real))
    {
        var msg = PhotonMessageParser.TryParseApplicationMessage(Convert.FromHexString(linha.Split("  ").Last().Trim()));
        if (msg is not PhotonEvent e) continue;
        PlayerRegistry.HandleEvent(e);   // o 123 (mob) passa pelo registro como no app
        if (e.EventCode == 6 && PhotonParam.ToDouble(e.Parameters[2]) is double d && d < 0) esperado += -d;
        if (e.EventCode == 7) { eventos7++; foreach (var x in (Array)e.Parameters[2]!) if (PhotonParam.ToDouble(x) is double v && v < 0) esperado += -v; }
        t9.HandleEvent(e);
    }
    long somado = t9.Snapshot().Sum(x => x.Damage);
    Checar(eventos7 > 0, $"a amostra real tem evento 7 ({eventos7})");
    Checar(somado == (long)esperado && somado == 531, $"medidor somou {somado} de {esperado} (a 1.0.66 somava 88 de 531)");
}

// ── 10. Dano em si mesmo nao conta ───────────────────────────────────────────
Console.WriteLine("\n-- 10. dano em si mesmo (habilidade que custa vida) nao entra");
Limpar(); Self(1);
Nome(40, "Sangrento");
var t10 = new DamageMeterTracker();
t10.HandleEvent(HitEm(40, 40, 600, 1400));
t10.HandleEvent(HitEm(40, 41, 100, 900));
Checar(Dano(t10, "Sangrento") == 100, $"so o golpe em outro conta: {Dano(t10, "Sangrento")} (100)");
Checar(t10.Snapshot().First(x => x.Name == "Sangrento").Taken == 0, "e ele nao 'recebeu' o proprio golpe");

// ── 11. Cura efetiva x overheal ──────────────────────────────────────────────
Console.WriteLine("\n-- 11. cura em quem ja esta cheio vira overheal");
Limpar(); Self(1);
Nome(50, "Tanque"); Nome(60, "Curandeiro");
var t11 = new DamageMeterTracker();
var t0 = new DateTime(2026, 10, 18, 20, 0, 0, DateTimeKind.Utc);
t11.HandleEvent(HitEm(70, 50, 500, 1500), t0);                       // Tanque cai pra 1500
t11.HandleEvent(Cura(60, 50, 300, 1800), t0.AddSeconds(1));          // cura inteira: 1500 -> 1800
Checar(Cura11() == (300, 0), $"cura que sobe a vida conta inteira: {Cura11()}");
t11.HandleEvent(Regen(50, 2000, 2000), t0.AddSeconds(5));            // regenerou ate encher
t11.HandleEvent(Cura(60, 50, 400, 2000), t0.AddSeconds(6));          // cura em quem ja esta cheio
Checar(Cura11() == (300, 400), $"cura em vida cheia e' overheal: {Cura11()}");
t11.HandleEvent(HitEm(70, 50, 100, 1900), t0.AddSeconds(7));         // leva 100
t11.HandleEvent(Cura(60, 50, 300, 2000), t0.AddSeconds(8));          // cura 300, so 100 cabem
Checar(Cura11() == (400, 600), $"cura que passa do maximo: so o que coube conta: {Cura11()}");
(long, long) Cura11() { var e = t11.Snapshot().First(x => x.Name == "Curandeiro"); return (e.Healing, e.Overheal); }
Checar(DamageMeterTracker.CuraEfetiva(300, null, 2000) == 300, "sem a vida de antes, conta a cura inteira (como antes)");

// ── 12. Dano recebido (inclusive de mob) ─────────────────────────────────────
Console.WriteLine("\n-- 12. dano recebido conta mesmo quando quem bateu foi mob");
var registrarMob = typeof(PlayerRegistry).GetMethod("RegisterMob", BindingFlags.NonPublic | BindingFlags.Static)!;
registrarMob.Invoke(null, new object?[] { 70L });
var t12 = new DamageMeterTracker();
t12.HandleEvent(HitEm(70, 50, 250, 1000));
var tanque = t12.Snapshot().FirstOrDefault(x => x.Name == "Tanque");
Checar(tanque != null && tanque.Taken == 250, $"Tanque recebeu 250: {tanque?.Taken}");
Checar(!t12.Snapshot().Any(x => x.Name == "#70"), "e o mob nao entra no ranking de quem causa dano");

// ── 13. DPS pelo tempo em combate ────────────────────────────────────────────
Console.WriteLine("\n-- 13. DPS conta so o tempo batendo");
Limpar(); Self(1);
Nome(80, "Dps");
var t13 = new DamageMeterTracker();
foreach (var seg in new[] { 0, 2, 4, 20 })                           // 3 golpes seguidos e 1 isolado
    t13.HandleEvent(HitEm(80, 81, 100, 1000), t0.AddSeconds(seg));
var dps = t13.Snapshot().First(x => x.Name == "Dps");
Checar(Math.Abs(dps.ActiveSeconds - 6) < 0.01, $"tempo em combate 1+2+2+1 = 6s (buraco de 16s nao conta): {dps.ActiveSeconds}");
Checar(Math.Abs(dps.Dps - 400.0 / 6) < 0.01, $"DPS = 400/6: {dps.Dps:0.00}");

Console.WriteLine(falhas == 0 ? "\nOK: nenhum dano se perde e ninguem herda dano de outro"
                              : $"\nFALHOU: {falhas}");
return falhas == 0 ? 0 : 1;

// Golpe com afetado e vida nova: [0]=afetado [2]=-dano [3]=vida depois [6]=causador.
static PhotonEvent HitEm(long causador, long alvo, int dano, int vidaDepois) => new(0, new Dictionary<byte, object?>
{
    [0] = alvo, [2] = (float)(-dano), [3] = (float)vidaDepois, [6] = causador, [252] = (short)6,
});
static PhotonEvent Cura(long curandeiro, long alvo, int cura, int vidaDepois) => new(0, new Dictionary<byte, object?>
{
    [0] = alvo, [2] = (float)cura, [3] = (float)vidaDepois, [6] = curandeiro, [252] = (short)6,
});
// RegenerationHealthChanged (91): [0]=objeto [2]=vida [3]=vida maxima.
static PhotonEvent Regen(long obj, int vida, int max) => new(0, new Dictionary<byte, object?>
{
    [0] = obj, [2] = (float)vida, [3] = (float)max, [252] = (short)91,
});
