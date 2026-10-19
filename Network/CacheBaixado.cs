using System.IO;
using System.Net.Http;

namespace XnomercyApp.Network;

/// <summary>
/// Arquivos de dados do jogo baixados do ao-bin-dumps com cache no PC e ETag (itens,
/// magias, mapas, IP) — 18/10/2026, um lugar só pros quatro catálogos.
///
/// Antes cada catálogo gravava o cache direto (WriteAllText trunca antes de escrever) e
/// o ETag junto. Se o PC desligasse no meio, ficava um cache CORTADO com o ETag certo:
/// na abertura seguinte o servidor respondia "não mudou", o app relia o cache cortado,
/// falhava calado — e assim pra sempre, sem nome de item nenhum. Agora o cache troca de
/// uma vez, o ETag só é gravado depois, e quem não conseguir ler chama Invalidar.
/// </summary>
public static class CacheBaixado
{
    /// <summary>Conteúdo novo do site (ou o do PC, se não mudou ou sem internet). null = nem um nem outro.</summary>
    public static async Task<string?> ObterAsync(string url, string cache, TimeSpan tempoMaximo)
    {
        string etag = cache + ".etag";
        try
        {
            using var http = new HttpClient { Timeout = tempoMaximo };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (File.Exists(etag) && File.Exists(cache))
                req.Headers.TryAddWithoutValidation("If-None-Match", (await File.ReadAllTextAsync(etag)).Trim());
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && File.Exists(cache))
                return await File.ReadAllTextAsync(cache);
            resp.EnsureSuccessStatusCode();
            string conteudo = await resp.Content.ReadAsStringAsync();
            try { File.Delete(etag); } catch { }
            if (ArquivoSeguro.Gravar(cache, conteudo, guardarAnterior: false) && resp.Headers.ETag is not null)
                await File.WriteAllTextAsync(etag, resp.Headers.ETag.ToString());
            return conteudo;
        }
        catch
        {
            // Sem internet / erro do site: usa o que já está no PC.
            try { return File.Exists(cache) ? await File.ReadAllTextAsync(cache) : null; }
            catch { return null; }
        }
    }

    /// <summary>O conteúdo não deu pra ler: apaga cache e ETag — a próxima tentativa baixa inteiro.</summary>
    public static void Invalidar(string cache)
    {
        try { File.Delete(cache + ".etag"); } catch { }
        try { File.Delete(cache); } catch { }
    }

    /// <summary>Tenta de novo daqui a 2 minutos (os catálogos só eram carregados uma vez, ao abrir o app).</summary>
    public static void TentarDeNovo(Func<Task> carregar) =>
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => carregar(), TaskScheduler.Default);
}
