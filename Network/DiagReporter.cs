using System.Net.Http;
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
    private static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public static void Report(string kind, string content)
    {
        if (!ConsentStore.HasConsented || string.IsNullOrWhiteSpace(content)) return;
        // Fire-and-forget — diagnóstico nunca pode travar ou afetar o uso do app.
        _ = SendAsync(kind, content);
    }

    // Envia os arquivos de calibração que o app gravou (eventos do Albion já capturados:
    // events_diag, named_events, newchar, e erros) e os esvazia. Chamado na inicialização,
    // ANTES da captura começar (que é manual) — por isso é seguro ler+limpar sem corrida.
    // É assim que a liderança recebe os dados pra calibrar grupo/mob sem todo mundo online.
    public static void ReportDiagFiles()
    {
        if (!ConsentStore.HasConsented) return;
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XnomercyApp");
        var files = new (string File, string Kind)[]
        {
            ("events_diag.txt", "eventos_diag"),
            ("named_events_diag.txt", "named_events"),
            ("newchar_diag.txt", "newchar"),
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
                Report(kind, content);
                System.IO.File.WriteAllText(path, "");   // esvazia pra não reenviar igual depois
            }
            catch { }
        }
    }

    private static async Task SendAsync(string kind, string content)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { kind, version = AppVersion, content });
            using var req = new StringContent(body, Encoding.UTF8, "application/json");
            await Http.PostAsync(SiteUrl + Endpoint, req);
        }
        catch { /* sem internet, site fora do ar, etc — diagnóstico é best-effort */ }
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
