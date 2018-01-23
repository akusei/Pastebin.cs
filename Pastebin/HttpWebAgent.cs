using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Pastebin
{
    internal sealed class HttpWebAgent
    {
        private const string ApiUrl = "https://pastebin.com/api/api_post.php";
        private const string LoginUrl = "https://pastebin.com/api/api_login.php";

        private const double BurstDuration = 60; // seconds
        private const uint MaxRequestsPerBurst = 30;
        private const double PaceRequestTimeout = 2000;

        private const string UserAgent = "Pastebin.cs v2.5";
        private readonly RateLimitMode _rateLimitMode;

        public readonly string ApiKey;

        private DateTime? _burstStart;
        private DateTime? _lastRequest;
        private uint _requestsThisBurst;

        public string UserKey { get; internal set; }
        public bool Authenticated => this.UserKey != null;

        public HttpWebAgent(string apiKey, RateLimitMode mode)
        {
            this.ApiKey = apiKey;
            this._rateLimitMode = mode;
        }

        public async Task AuthenticateAsync(string username, string password)
        {
            var parameters = new Dictionary<string, object>
            {
                { "api_user_name", username },
                { "api_user_password", password }
            };

            this.UserKey = await this.CreateAndExecuteAsync(HttpWebAgent.LoginUrl, HttpMethod.Post, parameters)
                                     .ConfigureAwait(false);
        }

        public async Task<string> GetAsync(string url, Dictionary<string, object> parameters)
            => await this.CreateAndExecuteAsync(url, HttpMethod.Get, parameters).ConfigureAwait(false);

        public async Task<string> PostAsync(Dictionary<string, object> parameters)
            => await this.CreateAndExecuteAsync(HttpWebAgent.ApiUrl, HttpMethod.Post, parameters).ConfigureAwait(false);

        public async Task<string> PostAsync(string option, Dictionary<string, object> parameters)
        {
            parameters = parameters ?? new Dictionary<string, object>();
            parameters.Add("api_option", option);

            return await this.PostAsync(parameters).ConfigureAwait(false);
        }

        public async Task<XDocument> PostAndReturnXmlAsync(
            string option,
            Dictionary<string, object> parameters = null)
        {
            var xml = await this.PostAsync(option, parameters).ConfigureAwait(false);
            return XDocument.Parse($"<?xml version='1.0' encoding='utf-8'?><result>{xml}</result>");
        }

        public async Task<HttpRequestMessage> CreateRequestAsync(
            string endPoint,
            HttpMethod method,
            Dictionary<string, object> parameters)
        {
            await this.EnforceRateLimitAsync().ConfigureAwait(false);
            var request = this.CreateRequestImpl(endPoint, method, parameters, out var query);

            if (method != HttpMethod.Post) return request;

            request.Content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");

            return request;
        }

        private HttpRequestMessage CreateRequestImpl(
            string endPoint,
            HttpMethod method,
            Dictionary<string, object> parameters,
            out string query)
        {
            parameters = parameters ?? new Dictionary<string, object>();
            parameters.Add("api_dev_key", this.ApiKey);

            if (this.Authenticated)
                parameters.Add("api_user_key", this.UserKey);

            var pairs = new List<string>(parameters.Count);
            pairs.AddRange(
                from pair in parameters
                let key = WebUtility.UrlEncode(pair.Key)
                let value = WebUtility.UrlEncode(pair.Value.ToString())
                select $"{key}={value}"
            );

            query = String.Join("&", pairs);

            var request = new HttpRequestMessage(method, endPoint);
            request.Headers.UserAgent.ParseAdd(HttpWebAgent.UserAgent);

            return request;
        }

        public static async Task<string> ExecuteRequestAsync(HttpRequestMessage request)
        {
            HttpClient client = new HttpClient();
            var response = await client.SendAsync(request);

            string text = await response.Content.ReadAsStringAsync();

            HttpWebAgent.HandleResponseString(text);
            return text;
        }

        private static void HandleResponseString(string text)
        {
            if (!text.StartsWith("Bad API request,")) return;

            var error = text.Substring(text.IndexOf(',') + 2);
            switch (error)
            {
                case "invalid api_user_key":
                    throw new PastebinException(
                        "Invalid user key. Consider logging in again or refreshing your user key");
                case "invalid api_dev_key": throw new PastebinException("Invalid API dev key");

                default: throw new PastebinException(error);
            }
        }

        public async Task<string> CreateAndExecuteAsync(
            string url,
            HttpMethod method,
            Dictionary<string, object> parameters)
        {
            var request = await this.CreateRequestAsync(url, method, parameters).ConfigureAwait(false);
            return await HttpWebAgent.ExecuteRequestAsync(request).ConfigureAwait(false);
        }

        private Task EnforceRateLimitAsync()
        {
            var needsLimit = this.CheckRequestRate(out var duration);
            return needsLimit ? Task.Delay(duration) : Task.CompletedTask;
        }

        private bool CheckRequestRate(out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if ((this._burstStart == null) &&
                (this._lastRequest == null))
            {
                this._lastRequest = DateTime.UtcNow;
                this._burstStart = DateTime.UtcNow;
                return false;
            }

            switch (this._rateLimitMode)
            {
                case RateLimitMode.None:
                case RateLimitMode.Burst:
                    {
                        if (this._burstStart == null)
                        {
                            this._burstStart = DateTime.UtcNow;
                            this._requestsThisBurst = 1;
                            return false;
                        }

                        var diff = DateTime.UtcNow - this._burstStart.Value;
                        if (diff.TotalSeconds >= HttpWebAgent.BurstDuration)
                        {
                            this._burstStart = DateTime.UtcNow;
                            this._requestsThisBurst = 0;
                            return false;
                        }

                        if (this._requestsThisBurst >= HttpWebAgent.MaxRequestsPerBurst)
                        {
                            var timeLeft = TimeSpan.FromSeconds(HttpWebAgent.BurstDuration) -
                                           (DateTime.UtcNow - this._burstStart.Value);

                            if (this._rateLimitMode != RateLimitMode.Burst)
                                throw new PastebinRateLimitException(timeLeft);

                            duration = timeLeft;
                            return true;
                        }

                        ++this._requestsThisBurst;
                        return false;
                    }

                case RateLimitMode.Pace:
                    {
                        if (this._lastRequest == null)
                        {
                            this._lastRequest = DateTime.UtcNow;
                            return false;
                        }

                        var diff = DateTime.UtcNow - this._lastRequest.Value;
                        if (diff.TotalMilliseconds < HttpWebAgent.PaceRequestTimeout)
                        {
                            duration = TimeSpan.FromMilliseconds(HttpWebAgent.PaceRequestTimeout) - diff;
                            return true;
                        }

                        this._lastRequest = DateTime.UtcNow;
                        return false;
                    }

                default: throw new NotSupportedException();
            }
        }
    }
}
