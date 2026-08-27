using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace XnomercyApp.Network;

/// <summary>
/// Envia diagnóstico (erro/crash do app, ou os mesmos logs de calibração de eventos do
/// Albion que já gravamos localmente) pro site, que repassa pra um canal privado do
/// Discord. Só ativo enquanto testamos com a guild — o usuário aceita uma vez (ver
/// ConsentStore) e a partir daí funciona em segundo plano, sem pedir de novo.
///
/// Limite deliberado: só manda o que já é local (erro do app, eventos do Albion já
/// capturados pra calibração) — nunca nada do sistema, arquivos pessoais, ou qualquer
/// coisa fora desse escopo.
/// </summary>
public static class DiagReporter
{
    private const string SiteUrl = SiteConfig.BaseUrl;
    private const string Endpoint = "/api/app/diag";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // Versão real do build (ex: "1.0.6+a1b2c3d") — vem do -p:Version do publish junto com
    // o hash do commit (SourceLink). Mostra exatamente o que cada tester está rodando no
    // diagnóstico, em vez da versão fixa do assembly (que ficava sempre "1.0.0.0").
    private static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public static void Report(string kind, string content)
    {
        if (!ConsentStore.HasConsented || string.IsNullOrWhiteSpace(content)) return;
        // Fire-and-forget — diagnóstico nunca pode travar ou afetar o uso do app.
        _ = SendAsync(kind, content);
    }

    // Envia os arquivos de calibração que o app gravou (eventos do Albion já capturados:
    // events_diag, named_events, newchar, e erros). Chamado na inicialização, ANTES da
    // captura começar (que é manual). Só esvazia cada arquivo DEPOIS de confirmar que o
    // envio deu certo — assim, se o envio falhar, os dados não se perdem e tentamos de novo
    // na próxima abertura. As falhas ficam registradas em diag_send.log pra depurar.
    public static void ReportDiagFiles()
    {
        if (!ConsentStore.HasConsented) return;
        _ = Task.Run(SendDiagFilesAsync);   // background, não trava a UI
    }

    // Versão aguardável — usada antes de sair de verdade (menu da bandeja "Sair"),
    // pra garantir que o envio termine antes do processo morrer. Sem isso, o
    // fire-and-forget de ReportDiagFiles() ficava pra trás e os dados se perdiam.
    public static Task ReportDiagFilesAsync()
    {
        if (!ConsentStore.HasConsented) return Task.CompletedTask;
        return SendDiagFilesAsync();
    }

    private static async Task SendDiagFilesAsync()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
        var files = new (string File, string Kind)[]
        {
            ("events_diag.txt", "eventos_diag"),
            ("named_events_diag.txt", "named_events"),
            ("newchar_diag.txt", "newchar"),
            ("ops_diag.txt", "ops_diag"),
            ("party_diag.txt", "party_diag"),
            ("errors.log", "errors"),
        };
        // ── Um envio, não seis ───────────────────────────────────────────────
        // Antes cada arquivo virava um POST, e cada POST virava um ANEXO separado
        // no Discord. Com 6 arquivos por sessão e vários testadores, o Oseias
        // ficava baixando anexo por anexo pra conseguir ler qualquer coisa —
        // "está difícil juntar tudo, baixar um a um" (22/08).
        //
        // Agora os 6 são rotacionados, lidos e emendados num arquivo só, com
        // cabeçalho de sessão e um separador por trecho. Uma sessão = um anexo.
        // O conteúdo é o mesmo; o que muda é quantas vezes é preciso clicar.
        //
        // A rotação continua igual e continua sendo por arquivo: renomeia pra
        // .sending ANTES de ler, e a captura segue gravando num arquivo novo e
        // vazio. Antes era ler -> enviar -> esvaziar, e como o envio leva até
        // 10s, tudo gravado nessa janela era apagado sem nunca ter sido enviado.
        var staged = new List<(string Staging, string Kind, string Content)>();
        foreach (var (file, kind) in files)
        {
            try
            {
                var path = System.IO.Path.Combine(dir, file);
                if (!System.IO.File.Exists(path)) continue;

                var staging = path + ".sending";
                try
                {
                    if (System.IO.File.Exists(staging))
                    {
                        // Sobrou de um envio anterior que não terminou (app fechado
                        // no meio) — junta com o atual pra não perder nada.
                        System.IO.File.AppendAllText(staging, System.IO.File.ReadAllText(path));
                        System.IO.File.WriteAllText(path, "");
                    }
                    else
                    {
                        System.IO.File.Move(path, staging);
                    }
                }
                catch (Exception e)
                {
                    LogLocal($"{file}: nao deu pra rotacionar ({e.Message}) — pulando desta vez");
                    continue;
                }

                var content = System.IO.File.ReadAllText(staging);
                if (string.IsNullOrWhiteSpace(content))
                {
                    try { System.IO.File.Delete(staging); } catch { }
                    continue;
                }
                staged.Add((staging, kind, content));
            }
            catch (Exception e) { LogLocal($"erro ao processar {file}: {e.Message}"); }
        }

        if (staged.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append("========================================================\n");
        sb.Append($"sessao {DateTime.Now:yyyy-MM-dd HH:mm:ss}  app {AppVersion}\n");
        sb.Append($"{staged.Count} trecho(s): {string.Join(", ", staged.Select(s => s.Kind))}\n");
        sb.Append("========================================================\n");
        foreach (var (_, kind, content) in staged)
        {
            sb.Append($"\n\n===== {kind} ({content.Length} chars) =====\n");
            sb.Append(content);
            if (!content.EndsWith("\n")) sb.Append('\n');
        }

        bool ok = await SendAsync("sessao", sb.ToString());
        if (ok)
        {
            // Só apaga DEPOIS de o envio confirmar. Falhando, os .sending ficam
            // no disco e são emendados na próxima abertura (ver o merge acima) —
            // nada se perde, e o arquivo seguinte vem com as duas sessões.
            foreach (var (staging, _, _) in staged)
            {
                try { System.IO.File.Delete(staging); } catch { }
            }
        }
        else
        {
            LogLocal($"sessao com {staged.Count} trecho(s) nao foi enviada — "
                     + "os .sending ficaram pra proxima abertura");
        }
    }

    private static async Task<bool> SendAsync(string kind, string content)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { kind, version = AppVersion, content });
            using var req = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(SiteUrl + Endpoint, req);
            if (!resp.IsSuccessStatusCode)
                LogLocal($"{kind}: HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            LogLocal($"{kind}: {e.GetType().Name} - {e.Message}");
            return false;
        }
    }

    // Registra o que deu errado no envio, localmente, pra depurar sem depender do Discord.
    private static void LogLocal(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "diag_send.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }
}

/// <summary>
/// Guarda se o usuário já aceitou enviar diagnóstico — uma vez só, persistido em disco.
/// Sem isso (ou se a pessoa recusar), o DiagReporter nunca envia nada.
/// </summary>
public static class ConsentStore
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp", "diag_consent.txt");

    private static bool? _cached;

    public static bool HasAsked => System.IO.File.Exists(FilePath);

    public static bool HasConsented
    {
        get
        {
            if (_cached.HasValue) return _cached.Value;
            try { _cached = System.IO.File.Exists(FilePath) && System.IO.File.ReadAllText(FilePath).Trim() == "yes"; }
            catch { _cached = false; }
            return _cached.Value;
        }
    }

    public static void SetConsent(bool consented)
    {
        _cached = consented;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(FilePath, consented ? "yes" : "no");
        }
        catch { }
    }
}
