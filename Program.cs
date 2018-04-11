using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace EMSIDotNet
{
    class Program
    {
        static void Main(string[] args)
        {
            Program program = new Program();
            program.FooAsync(CancellationToken.None).Wait();
        }

        public async Task FooAsync(CancellationToken cancellationToken)
        {

            var builder = new UriBuilder("http://169.254.169.254/metadata/identity/oauth2/token");
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["api-version"] = "2018-02-01";
            query["resource1"] = "https://management.azure.com/";
            query["msi_res_id"] = "/subscriptions/ec0aa5f7-9e78-40c9-85cd-535c6305b380/resourcegroups/1c2b4bf7e1f448e/providers/Microsoft.ManagedIdentity/userAssignedIdentities/msi-idd7b42487";
            builder.Query = query.ToString();
            string url = builder.ToString();

            using (HttpRequestMessage msiRequest = new HttpRequestMessage(HttpMethod.Get, url))
            {
                msiRequest.Headers.Add("Metadata", "true");
                using (HttpResponseMessage msiResponse = await (new HttpClient()).SendAsync(msiRequest, cancellationToken))
                {
                    Console.WriteLine("A" + msiResponse.StatusCode);

                    string content = await msiResponse.Content.ReadAsStringAsync();
                    Console.WriteLine("B:" + content);

                    dynamic loginInfo = JsonConvert.DeserializeObject(content);

                    string accessToken = loginInfo.access_token;
                    if (accessToken != null)
                    {
                        Console.WriteLine($"access_token: {accessToken}");
                    }
                    string expiresOn = loginInfo.expires_on;
                    if (expiresOn != null)
                    {
                        Console.WriteLine($"expires_on: {expiresOn}");
                    }
                    string tokenType = loginInfo.token_type;
                    if (tokenType != null)
                    {
                        Console.WriteLine($"token_type: {tokenType}");
                    }
                }
            }

        }
    }
}
