using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace EMSIDotNet
{
    class Program
    {
        private readonly IList<int> retrySlots = new List<int>(new int [] { 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597, 2584, 4181, 6765 });
        private readonly int maxRetry;
        private ConcurrentDictionary<string, MISToken> cache = new ConcurrentDictionary<string, MISToken>();
        private static SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        public string UserAssignedIdentityObjectId { get; set; }
        public string UserAssignedIdentityClientId { get; set; }
        public string UserAssignedIdentityResourceId = "/subscriptions/ec0aa5f7-9e78-40c9-85cd-535c6305b380/resourcegroups/1c2b4bf7e1f448e/providers/Microsoft.ManagedIdentity/userAssignedIdentities/msi-idd7b42487";

        private string resource = "https://management.azure.com/";

        public Program()
        {
            this.maxRetry = retrySlots.Count;
        }

        static void Main(string[] args)
        {
            Program program = new Program();

            for (int i = 0; i < 10; i++)
            {
                Program.MISToken msiToken = program.GetTokenFromIMDSEndpointAsync(program.resource, CancellationToken.None).Result;

                Console.WriteLine($"Token: {msiToken.AccessToken}");
                Console.WriteLine($"Tokentype: {msiToken.TokenType}");
                Console.WriteLine($"ExpireOn: {msiToken.ExpireOn}");
                Console.WriteLine($"IsExpired: {msiToken.IsExpired}");
                //
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                //
                Console.WriteLine($"ExpiredOnDate: {epoch.AddSeconds(Int32.Parse(msiToken.ExpireOn))}");
                Console.WriteLine($"NowDate: {DateTime.UtcNow}");
            }
        }

        private async Task<MISToken> GetTokenFromIMDSEndpointAsync(string resource, CancellationToken cancellationToken)
        {
            // First hit cache
            //
            Console.WriteLine("Trying with cache..");

            if (cache.TryGetValue(resource, out MISToken token) == true && !token.IsExpired)
            {
                Console.WriteLine("Found in cache..");

                return token;
            }

            Console.WriteLine("Not in cache..");

            // if cache miss then retrieve from IMDS endpoint with retry
            //
            await semaphoreSlim.WaitAsync();
            try
            {
                // Try hit cache once again in case another thread already updated the cache while this thread was waiting
                //
                Console.WriteLine("Trying cache again..");

                if (cache.TryGetValue(resource, out token) == true && !token.IsExpired)
                {
                    Console.WriteLine("Found in cache..");

                    return token;
                }
                else
                {
                    Console.WriteLine("Not in cache, retriving from endpoint..");

                    token = await RetrieveTokenFromIMDSWithRetryAsync(resource, cancellationToken);
                    cache.AddOrUpdate(resource, token, (key, oldValue) => token);

                    Console.WriteLine("Retrieved from endpoint and stored in cache..");

                    return token;
                }
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private async Task<MISToken> RetrieveTokenFromIMDSWithRetryAsync(string resource, CancellationToken cancellationToken)
        {

            var uriBuilder = new UriBuilder("http://169.254.169.254/metadata/identity/oauth2/token");
            var query = HttpUtility.ParseQueryString(uriBuilder.Query);
            query["api-version"] = "2018-02-01";
            query["resource"] = resource;
            if (this.UserAssignedIdentityObjectId != null)
            {
                query["object_id"] = this.UserAssignedIdentityObjectId;
            }
            else if (this.UserAssignedIdentityClientId != null)
            {
                query["client_id"] = this.UserAssignedIdentityClientId;
            }
            else if (this.UserAssignedIdentityResourceId != null)
            {
                query["msi_res_id"] = this.UserAssignedIdentityResourceId;
            }
            else
            {
                throw new ArgumentException("MSI: UserAssignedIdentityObjectId, UserAssignedIdentityClientId or UserAssignedIdentityResourceId must be set");
            }

            uriBuilder.Query = query.ToString();
            string url = uriBuilder.ToString();

            int retry = 1;
            while (retry <= maxRetry)
            {
                //
                Console.WriteLine($"Sending request to endpoint...");

                using (HttpRequestMessage msiRequest = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    msiRequest.Headers.Add("Metadata", "true");
                    using (HttpResponseMessage msiResponse = await (new HttpClient()).SendAsync(msiRequest, cancellationToken))
                    {
                        int statusCode = ((int)msiResponse.StatusCode);
                        if (ShouldRetry(statusCode))
                        {

                            int retryTimeout = retrySlots[new Random().Next(retry)];

                            Console.WriteLine($"Retrying..waiting for {retryTimeout}");

                            await Task.Delay(retryTimeout * 1000);
                            retry++;
                        }
                        else if (statusCode != 200)
                        {
                            string content = await msiResponse.Content.ReadAsStringAsync();
                            throw new HttpRequestException($"Code: {statusCode} ReasonReasonPhrase: {msiResponse.ReasonPhrase} Body: {content}");
                        }
                        else
                        {
                            string content = await msiResponse.Content.ReadAsStringAsync();
                            dynamic loginInfo = JsonConvert.DeserializeObject(content);
                            if (loginInfo.access_token == null)
                            {
                                throw new InvalidMSITokenException($"Access token not found in the msi token response {content}");
                            }

                            Console.WriteLine($"Got token from endpoint...");

                            MISToken msiToken = new MISToken
                            {
                                AccessToken = loginInfo.access_token,
                                ExpireOn = loginInfo.expires_on,
                                TokenType = loginInfo.token_type
                            };
                            return msiToken;
                        }
                    }
                }
            }
            throw new MSIMaxRetryReachedException(maxRetry);
        }

        private static bool ShouldRetry(int statusCode)
        {
            return (statusCode == 429 || statusCode == 404 || (statusCode >= 500 && statusCode <= 599));
        }

        private class MISToken
        {
            private static DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            public string TokenType { get; set; }

            public string AccessToken { get; set; }

            public string ExpireOn { get; set; }

            public bool IsExpired
            {
                get
                {
                    if (this.ExpireOn == null)
                    {
                        return true;
                    }
                    else if (!Int32.TryParse(this.ExpireOn, out int iexpireOn))
                    {
                        return true;
                    }
                    else
                    {
                        return DateTime.UtcNow.AddMinutes(5).CompareTo(epoch.AddSeconds(iexpireOn)) > 0;
                    }
                }
            }
        }
    }

    public class MSIMaxRetryReachedException : Exception
    {
        public MSIMaxRetryReachedException(int maxRetry) : base($"MSI: Failed to acquire tokens after retrying %{ maxRetry} times")
        {
        }
    }

    public class InvalidMSITokenException: Exception
    {
        public InvalidMSITokenException(string message) : base(message)
        {
        }
    }
}
