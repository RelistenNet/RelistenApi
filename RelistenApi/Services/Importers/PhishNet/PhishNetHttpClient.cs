using System.Net.Http;

namespace Relisten.Import.PhishNet;

public class PhishNetHttpClientFactory
{
    public PhishNetHttpClientFactory()
    {
        HttpClient = new HttpClient();

        // iPhone on iOS 11.4
        HttpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0.1 Mobile/15E148 Safari/604.1");
        HttpClient.DefaultRequestHeaders.Add("Accept", "*/*");
    }

    public HttpClient HttpClient { get; }
}
