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
    private const string SiteUrl = "https://nome-xnomercy-site-production.up.railway.app";
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
            ("errors.log", "errors"),
        };
        foreach (var (file, kind) in files)
        {
            try
            {
                var path = System.IO.Path.Combine(dir, file);
                if (!System.IO.File.Exists(path)) continue;
                var content = System.IO.File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content)) continue;
                bool ok = await SendAsync(kind, content);
                if (ok) System.IO.File.WriteAllText(path, "");   // só esvazia se enviou de verdade
            }
            catch (Exception e) { LogLocal($"erro ao processar {file}: {e.Message}"); }
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
