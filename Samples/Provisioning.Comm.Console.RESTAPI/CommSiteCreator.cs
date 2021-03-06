﻿using Microsoft.SharePoint.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Provisioning.Comm.Console.RESTAPI
{
    class CommSiteCreator
    {

        #region PRIVATE PROPERTIES

        private ClientContext context;

        #endregion

        #region CONSTRUCTORS

        public CommSiteCreator(ClientContext ctx)
        {
            if (ctx == null)
            {
                throw new ArgumentNullException("Passed ClientContext object cannot be null");
            }
            this.context = ctx;

            // Load URL for the context
            this.Context.Load(this.Context.Web, w => w.Url);
            this.context.ExecuteQuery();
        }

        #endregion

        #region PROPERTIES

        public ClientContext Context
        {
            get
            {
                return this.context;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("Passed ClientContext object cannot be null");
                }
                this.context = value;
            }
        }

        #endregion

        #region PUBLIC METHODS

        public string CanAliasBeUsed(string alias)
        {
            //// Request information about the available client side components from SharePoint
            Task<String> validUrlFromAliasTask = Task.WhenAny(
                GetValidSiteUrlFromAliasAsync(this.Context, alias)
                ).Result;

            if (String.IsNullOrEmpty(validUrlFromAliasTask.Result))
            {
                throw new ArgumentException("We did not get proper result back.");
            }

            // Deserialize the returned data 
            var jsonSerializerSettings = new JsonSerializerSettings();
            jsonSerializerSettings.MissingMemberHandling = MissingMemberHandling.Ignore;
            var groupSiteResponse = JsonConvert.DeserializeObject<SiteAliasResponse>(validUrlFromAliasTask.Result, jsonSerializerSettings);

            // If return value is empty, alias cannot be used
            if (string.IsNullOrEmpty(groupSiteResponse.value))
            {
                // Given alias cannot be used for new group due reason or another
                return "";
            }
            else
            {
                // All good with the alias - can be used for new site
                return groupSiteResponse.value; 
            }
        }

        /// <summary>
        /// Can be used to provision modern SharePoint site with given values
        /// </summary>
        /// <param name="displayName">Display Name for the site / group</param>
        /// <param name="alias">Alias for the site / group. Used in the URL and in the emails</param>
        /// <param name="isPublic">Is site / group public or not</param>
        /// <param name="description">Optional description for the site / group</param>
        /// <param name="AdditionalOwners">Optional list of additional owners. Caller is set owner by default in SPO side</param>
        /// <returns></returns>
        public string CreateSite(string title, string url, string description,
                                    string classification, bool allowFileSharingForGuests = false)
        {

            // Create entity object, which will be serialized for post operation
            var fullRequest = new RootRequest();
            fullRequest.request.Title = title;
            fullRequest.request.Url = url;
            fullRequest.request.Description = description;
            fullRequest.request.Classification = classification;
            fullRequest.request.AllowFileSharingForGuestUsers = allowFileSharingForGuests;
            fullRequest.request.lcid = 1033;
            // Test empty sting for Site Design
            fullRequest.request.SiteDesignId = "00000000-0000-0000-0000-000000000000"; // Topic
            // fullRequest.request.SiteDesignId = "6142d2a0-63a5-4ba0-aede-d9fefca2c767"; // Showcase
            // fullRequest.request.SiteDesignId = "f6cc5403-0d63-442e-96c0-285923709ffc"; // Blank

            // Perform actual post operation for the new site request
            Task<String> createComSiteTask = Task.WhenAny(
                CreateCommsSiteAsync(this.Context, fullRequest)
                ).Result;

            if (String.IsNullOrEmpty(createComSiteTask.Result))
            {
                throw new ArgumentException("Issue while executing REST operation, no response received");
            }


            // Deserialize the returned data to response object
            JObject results = JObject.Parse(createComSiteTask.Result);
            var jsonSerializerSettings = new JsonSerializerSettings();
            jsonSerializerSettings.MissingMemberHandling = MissingMemberHandling.Ignore;
            var comSiteResponse = JsonConvert.DeserializeObject<SiteResponse>(results["d"]["Create"].ToString(), jsonSerializerSettings);
            // Return URL of newly created modern site for the caller
            return comSiteResponse.SiteUrl;
        }

        #endregion

        #region PRIVATE METHODS
        /// <summary>
        /// Actual call for checking if alias is good for usage
        /// </summary>
        /// <param name="context">Client context for authentication</param>
        /// <param name="alias">Alias to check</param>
        /// <returns></returns>
        private async Task<string> GetValidSiteUrlFromAliasAsync(ClientContext context, string alias)
        {
            string responseString = null;

            using (var handler = new HttpClientHandler())
            {
                // Set credentials and cookies for the call
                handler.Credentials = context.Credentials;
                handler.CookieContainer.SetCookies(new Uri(context.Web.Url), (context.Credentials as SharePointOnlineCredentials).GetAuthenticationCookie(new Uri(context.Web.Url)));

                using (var httpClient = new HttpClient(handler))
                {
                    //GET /_api/GroupSiteManager/GetValidSiteUrlFromAlias?alias='aliastocheck' HTTP/1.1

                    string requestUrl = String.Format("{0}/_api/GroupSiteManager/GetValidSiteUrlFromAlias?alias='{1}'", context.Web.Url, alias);
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    request.Headers.Add("accept", "application/json;odata.metadata=minimal");
                    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Add("odata-version", "4.0");

                    // Perform actual GET request
                    HttpResponseMessage response = await httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        // If value empty, URL is taken
                        responseString = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        // Something went wrong...
                        throw new Exception(await response.Content.ReadAsStringAsync());
                    }
                }
                return await Task.Run(() => responseString);
            }
        }

        /// <summary>
        /// Actual POST operation to create a new modern SharePoint site (and a group) with given values
        /// </summary>
        /// <param name="context">Client context</param>
        /// <param name="newSiteRequest">Site request details, which will be serialized</param>
        /// <returns></returns>
        private async Task<string> CreateCommsSiteAsync(ClientContext context, RootRequest newSiteRequest)
        {
            string responseString = null;

            using (var handler = new HttpClientHandler())
            {
                // Set permission setup accordingly for the call
                handler.Credentials = context.Credentials;
                handler.CookieContainer.SetCookies(new Uri(context.Web.Url), (context.Credentials as SharePointOnlineCredentials).GetAuthenticationCookie(new Uri(context.Web.Url)));

                using (var httpClient = new HttpClient(handler))
                {
                    //POST /_api/sitepages/publishingsite/create HTTP/1.1
                    string requestUrl = String.Format("{0}/_api/sitepages/communicationsite/Create", context.Web.Url);

                    // Serialize request object to JSON
                    string jsonModernSite = JsonConvert.SerializeObject(newSiteRequest);
                    HttpContent body = new StringContent(jsonModernSite);

                    // Build Http request
                    MediaTypeHeaderValue sharePointJsonMediaType = null;
                    MediaTypeHeaderValue.TryParse("application/json;odata=verbose", out sharePointJsonMediaType);
                    body.Headers.ContentType = sharePointJsonMediaType;

                    // Get Request Digest needed for post operation
                    Task<String> digestTask = Task.WhenAny(
                        GetRequestDigest(this.Context)
                        ).Result;

                    // Deserialize the Request Digest data for getting formDigestValue
                    var jsonSerializerSettings = new JsonSerializerSettings();
                    jsonSerializerSettings.MissingMemberHandling = MissingMemberHandling.Ignore;
                    var contextInformation = JsonConvert.DeserializeObject<RootObject>(digestTask.Result, jsonSerializerSettings);
                    
                    // Add rest of the needed hearders
                    string formDigestValue = contextInformation.d.GetContextWebInformation.FormDigestValue;
                    // body.Headers.Add("odata-version", "4.0");
                    body.Headers.Add("X-RequestDigest", formDigestValue);

                    // Adding following to ensure that we get our response in json format
                    httpClient.DefaultRequestHeaders.Add("accept", "application/json;odata=verbose");

                    // Perform actual post operation
                    HttpResponseMessage response = await httpClient.PostAsync(requestUrl, body);

                    if (response.IsSuccessStatusCode)
                    {
                        // If value empty, URL is taken
                        responseString = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        // Something went wrong...
                        throw new Exception(await response.Content.ReadAsStringAsync());
                    }
                }
                // Return response string to caller
                return await Task.Run(() => responseString);
            }
        }

        /// <summary>
        /// Used to get the RequestDigest value needed for POST operations
        /// </summary>
        /// <param name="context">Client Context</param>
        /// <returns></returns>
        private async Task<string> GetRequestDigest(ClientContext context)
        {
            using (var handler = new HttpClientHandler())
            {
                string responseString = string.Empty;

                handler.Credentials = context.Credentials;
                handler.CookieContainer.SetCookies(new Uri(context.Web.Url), (context.Credentials as SharePointOnlineCredentials).GetAuthenticationCookie(new Uri(context.Web.Url)));

                using (var httpClient = new HttpClient(handler))
                {
                    //GET /_api/contextinfo HTTP/1.1
                    string requestUrl = String.Format("{0}/_api/contextinfo", context.Web.Url);
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                    request.Headers.Add("accept", "application/json;odata=verbose");

                    // Perform actual GET Operation
                    HttpResponseMessage response = await httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        // If value empty, alias cannot be used due situation in Exchange, AAD or in SPO
                        responseString = await response.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        // Exception - something went wrong
                        throw new Exception(await response.Content.ReadAsStringAsync());
                    }
                }
                return await Task.Run(() => responseString);
            }
        }

        #endregion


    }
}
