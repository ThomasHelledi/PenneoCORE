﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Penneo.Util;
using System.Net.Http;
using System.Text;

namespace Penneo.Connector
{
    internal class ApiConnector : IApiConnector
    {
        /// <summary>
        /// The Penneo server endpoint
        /// </summary>
        private static string _endpoint = "https://sandbox.penneo.com/api/v1";

        /// <summary>
        /// The api connector factory
        /// </summary>
        private static Func<IApiConnector> _factory;

        /// <summary>
        /// The singleton instance
        /// </summary>
        private static IApiConnector _instance;

        /// <summary>
        /// Use proxy settings from Internet Explorer
        /// </summary>
        private static bool _useAutomaticProxy;

        /// <summary>
        /// Success status codes
        /// </summary>
        private readonly List<HttpStatusCode> _successStatusCodes = new List<HttpStatusCode> { HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent };

        /// <summary>
        /// The rest http client
        /// </summary>
        private HttpClient _client;

        /// <summary>
        /// Http headers
        /// </summary>
        private Dictionary<string, string> _headers;

        /// <summary>
        /// The last Http response
        /// </summary>
        private HttpResponseMessage _lastResponse;

        /// <summary>
        /// Rest resources
        /// </summary>
        private RestResources _restResources;

        /// <summary>
        /// Handler
        /// </summary>
        private HttpClientHandler _handler;

        /// <summary>
        /// Authenticator
        /// </summary>
        private WSSEAuthenticator _authenticator;

        /// <summary>
        /// Denotes if the last response received was an error
        /// </summary>
        private bool _wasLastResponseError;

        /// <summary>
        /// The latest server results for each entity processed
        /// </summary>
        private Dictionary<Guid, ServerResult> LatestEntityServerResults { get; set; }

        /// <summary>
        /// 
        /// </summary>
        private Dictionary<string, object> _parameters;

        protected ApiConnector()
        {
            Init();
        }

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static IApiConnector Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                if (!PenneoConnector.IsInitialized)
                {
                    throw new AuthenticationException("The Penneo connector has not been initialized");
                }

                if (_factory != null)
                {
                    _instance = _factory();
                }
                else
                {
                    _instance = new ApiConnector();
                }
                return _instance;
            }
        }

        #region IApiConnector Members
        /// <summary>
        /// <see cref="IApiConnector.WriteObject"/>
        /// </summary>
        public bool WriteObject(Entity obj)
        {
            var result = new ServerResult();

            var data = obj.GetRequestData();
            if (data == null)
            {
                Log.Write("Write Failed for " + obj.GetType().Name + ": Unable to get request data", LogSeverity.Error);
                result.Success = false;
                result.ErrorMessage = "Unable to get request data";
                SetLatestEntityServerResult(obj, result);
                return false;
            }
            if (!obj.IsNew)
            {
                var response = CallServer(obj.RelativeUrl + "/" + obj.Id, data, HttpMethod.Put);
                return ExtractResponse(obj, response, result);
            }
            else
            {
                var response = CallServer(obj.RelativeUrl, data, HttpMethod.Post);
                var successfull = ExtractResponse(obj, response, result);
                if (successfull)
                {
                    //Update id given from server
                    var fromServer = (Entity)JsonConvert.DeserializeObject(response.Content.ReadAsStringAsync().Result, obj.GetType());
                    obj.Id = fromServer.Id;
                }
                return successfull;
            }
        }

        private bool ExtractResponse(Entity obj, HttpResponseMessage response, ServerResult result)
        {
            result.Success = true;
            if (response == null)
            {
                Log.Write("Write Failed for " + obj.GetType().Name + ": Empty response", LogSeverity.Error);
                result.ErrorMessage = "Empty response";
                result.Success = false;
            }
            else
            {
                var content = response.Content.ReadAsStringAsync().Result;
                result.StatusCode = response.StatusCode;
                result.JsonContent = content;
                if (!_successStatusCodes.Contains(response.StatusCode))
                {
                    Log.Write("Write Failed for " + obj.GetType().Name + ": " + content, LogSeverity.Error);
                    result.Success = false;
                }
            }
            SetLatestEntityServerResult(obj, result);
            return result.Success;
        }

        /// <summary>
        /// <see cref="IApiConnector.DeleteObject"/>
        /// </summary>
        public bool DeleteObject(Entity obj)
        {
            var response = CallServer(obj.RelativeUrl + '/' + obj.Id, null, HttpMethod.Delete);
            return response != null && (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent);
        }

        public T ReadObject<T>(Entity parent, int? id, out HttpResponseMessage response)
            where T : Entity
        {
            return ReadObject<T>(parent, id, null, out response);
        }

        public T ReadObject<T>(Entity parent, int? id, string relativeUrl, out HttpResponseMessage response)
            where T : Entity
        {
            var url = !string.IsNullOrEmpty(relativeUrl) ? relativeUrl : ServiceLocator.Instance.GetInstance<RestResources>().GetResource(typeof(T), parent);
            if (id.HasValue)
            {
                url += "/" + id;
            }
            response = CallServer(url);
            if (response == null || response.StatusCode != HttpStatusCode.OK)
            {
                return default(T);
            }
            var obj = JsonConvert.DeserializeObject<T>(response.Content.ReadAsStringAsync().Result);
            if (id.HasValue)
            {
                obj.Id = id;
            }
            return obj;
        }



        /// <summary>
        /// <see cref="IApiConnector.LinkEntity"/>
        /// </summary>
        public bool LinkEntity(Entity parent, Entity child)
        {
            var url = parent.RelativeUrl + "/" + parent.Id + "/" + _restResources.GetResource(child.GetType()) + "/" + child.Id;
            var response = CallServer(url, method: new HttpMethod("LINK"));
            var result = new ServerResult();
            return ExtractResponse(parent, response, result);
        }

        /// <summary>
        /// <see cref="IApiConnector.UnlinkEntity"/>
        /// </summary>
        public bool UnlinkEntity(Entity parent, Entity child)
        {
            var url = parent.RelativeUrl + "/" + parent.Id + "/" + _restResources.GetResource(child.GetType()) + "/" + child.Id;
            var response = CallServer(url, method: new HttpMethod("UNLINK"));
            var result = new ServerResult();
            return ExtractResponse(parent, response, result);
        }

        /// <summary>
        /// <see cref="IApiConnector.GetLinkedEntities{T}"/>
        /// </summary>
        public QueryResult<T> GetLinkedEntities<T>(Entity obj, string url = null)
            where T : Entity
        {
            string actualUrl;
            if (string.IsNullOrEmpty(url))
            {
                actualUrl = obj.RelativeUrl + "/" + obj.Id + "/" + _restResources.GetResource<T>();
            }
            else
            {
                actualUrl = url;
            }

            var response = CallServer(actualUrl);
            var result = new QueryResult<T>();
            if (ExtractResponse(obj, response, result))
            {
                result.Objects = CreateObjects<T>(response.Content.ReadAsStringAsync().Result);
            }
            return result;
        }

        /// <summary>
        /// <see cref="IApiConnector.FindLinkedEntity{T}"/>
        /// </summary>
        public T FindLinkedEntity<T>(Entity obj, int id)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + _restResources.GetResource<T>() + "/" + id;
            var response = CallServer(url);
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                throw new Exception("Penneo: Internal problem encountered");
            }
            return CreateObject<T>(response.Content.ReadAsStringAsync().Result);
        }

        public T GetAsset<T>(Entity obj, string assetName)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + assetName;
            var response = CallServer(url);
            var content = response.Content.ReadAsStringAsync().Result;
            if (response == null || string.IsNullOrEmpty(content) || !_successStatusCodes.Contains(response.StatusCode))
            {
                return default(T);
            }
            return JsonConvert.DeserializeObject<T>(content);
        }

        /// <summary>
        /// <see cref="IApiConnector.GetFileAssets"/>
        /// </summary>
        public byte[] GetFileAssets(Entity obj, string assetName)
        {
            var encoded = GetTextAssets(obj, assetName);
            return Convert.FromBase64String(encoded);
        }

        /// <summary>
        /// <see cref="IApiConnector.GetTextAssets"/>
        /// </summary>
        public string GetTextAssets(Entity obj, string assetName)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + assetName;
            var response = CallServer(url, method: HttpMethod.Get);
            var result = JsonConvert.DeserializeObject<string[]>(response.Content.ReadAsStringAsync().Result);
            return result[0];
        }

        /// <summary>
        /// <see cref="IApiConnector.GetStringListAsset"/>
        /// </summary>
        public IEnumerable<string> GetStringListAsset(Entity obj, string assetName)
        {
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + assetName;
            var response = CallServer(url);
            var result = JsonConvert.DeserializeObject<string[]>(response.Content.ReadAsStringAsync().Result);
            return result;
        }

        /// <summary>
        /// <see cref="IApiConnector.FindBy{T}"/>
        /// </summary>
        public bool FindBy<T>(Dictionary<string, object> query, out IEnumerable<T> objects, out HttpResponseMessage response, int? page = null, int? perPage = null)
            where T : Entity
        {
            var resource = _restResources.GetResource<T>();

            Dictionary<string, Dictionary<string, object>> options = null;
            if (query != null && query.Count > 0)
            {
                options = new Dictionary<string, Dictionary<string, object>>();
                options["query"] = query;
            }
            response = CallServer(resource, null, HttpMethod.Get, options, page: page, perPage: perPage);
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                objects = null;
                return false;
            }

            objects = CreateObjects<T>(response.Content.ReadAsStringAsync().Result);
            return true;
        }

        /// <summary>
        /// <see cref="IApiConnector.PerformAction"/>
        /// </summary>
        public ServerResult PerformAction(Entity obj, string actionName)
        {
            var result = new ServerResult();
            var url = obj.RelativeUrl + "/" + obj.Id + "/" + actionName;
            var response = CallServer(url, method: new HttpMethod("patch"));
            if (response == null || !_successStatusCodes.Contains(response.StatusCode))
            {
                result.Success = false;
                result.StatusCode = response.StatusCode;
                //result.ErrorMessage = response.ErrorMessage;
                return result;
            }
            result.Success = true;
            return result;
        }

        /// <summary>
        /// Was the last response an error
        /// </summary>
        public bool WasLastResponseError
        {
            get { return _wasLastResponseError; }
        }

        /// <summary>
        /// Get the content of the last response
        /// </summary>
        public string LastResponseContent
        {
            get { return _lastResponse != null ? _lastResponse.Content.ReadAsStringAsync().Result : null; }
        }

        #endregion

        /// <summary>
        /// Initializes the API connector with rest client, endpoint, headers and authentication
        /// </summary>
        private void Init()
        {
            if (!string.IsNullOrEmpty(PenneoConnector.Endpoint))
            {
                _endpoint = PenneoConnector.Endpoint;
            }
            _handler = new HttpClientHandler { UseCookies = false };
            _client = new HttpClient(_handler) { BaseAddress = new Uri(_endpoint) };

            _restResources = ServiceLocator.Instance.GetInstance<RestResources>();

            _headers = PenneoConnector.Headers ?? new Dictionary<string, string>();
            //_headers["Content-type"] = "application/json";

            if (!string.IsNullOrEmpty(PenneoConnector.User))
            {
                _headers["penneo-api-user"] = PenneoConnector.User;
            }

            if (PenneoConnector.AuthenticationType == AuthType.WSSE)
            {
                _authenticator = new WSSEAuthenticator(PenneoConnector.Key, PenneoConnector.Secret);
            }
            else
            {
                throw new NotSupportedException("Unknown authentication type " + PenneoConnector.AuthenticationType);
            }
            LatestEntityServerResults = new Dictionary<Guid, ServerResult>();

            PenneoConnector.Reset();
        }

        /// <summary>
        /// Sets a factory for creating a connector.
        /// </summary>
        public static void SetFactory(Func<IApiConnector> factory)
        {
            _factory = factory;

            //Null instance if a new factory is provided
            ResetInstance();
        }

        /// <summary>
        /// Sets whether to use automatic proxy settings from Internet Explorer
        /// </summary>
        public static void SetUseProxySettingsFromInternetExplorer(bool use)
        {
            _useAutomaticProxy = use;
        }

        public static void ResetInstance()
        {
            _instance = null;
        }

        /// <summary>
        /// Prepare a rest request
        /// </summary>
        internal HttpRequestMessage PrepareRequest(string url, Dictionary<string, object> data = null, HttpMethod method = null, Dictionary<string, Dictionary<string, object>> options = null, int? page = null, int? perPage = null)
        {
            if (method == null)
                method = HttpMethod.Get;
            _parameters = new Dictionary<string, object>();

            var request = new HttpRequestMessage(method, url);
            if (_headers != null)
            {
                foreach (var h in _headers)
                {
                    request.Headers.Add(h.Key, h.Value);
                }
            }

            if (page.HasValue)
            {
                request.Headers.Add("x-paginate", "true");
            }

            if (options != null)
            {
                foreach (var entry in options)
                {
                    var key = entry.Key;
                    var o = entry.Value;
                    if (key == "query")
                    {
                        VisitQuery(request, o);
                    }
                }
            }

            if (perPage.HasValue)
            {
                if (perPage <= 0)
                {
                    throw new NotSupportedException("PerPage must be greater than zero");
                }
                _parameters["per_page"] = perPage;
            }
            if (page.HasValue)
            {
                if (page <= 0)
                {
                    throw new NotSupportedException("Page must be greater than zero");
                }
                _parameters["page"] = page;
            }

            if (data != null)
            {
                var formData = data;
                foreach (var item in _parameters)
                {
                    formData[item.Key] = item.Value;
                }

                request.Content = new StringContent(JsonConvert.SerializeObject(formData), Encoding.UTF8, "application/json");
            }
            return request;
        }


        /// <summary>
        /// Parse 'query' options into request data
        /// </summary>
        private void VisitQuery(HttpRequestMessage request, Dictionary<string, object> query)
        {
            foreach (var entry in query)
            {
                _parameters[StringUtil.FirstCharacterToLower(entry.Key)] = entry.Value;
            }
        }

        /// <summary>
        /// Set the proxy information on the rest client
        /// </summary>
        private void SetProxy()
        {
            if (_useAutomaticProxy)
            {
                var proxyWrapper = WebRequest.DefaultWebProxy;
                if (proxyWrapper != null)
                {
                    var proxy = proxyWrapper.GetProxy(new Uri(_endpoint));
                    if (proxy != null && !_endpoint.Equals(proxy.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("Proxy URL: " + proxy, LogSeverity.Information);
                        _handler.Proxy = new WebProxy(proxy);
                    }
                }
            }
            else
            {
                // Removed
                //_handler.Proxy = null;
            }
        }

        /// <summary>
        /// Calls the Penneo server with a rest request
        /// </summary>
        public HttpResponseMessage CallServer(string url, Dictionary<string, object> data = null, HttpMethod method = null, Dictionary<string, Dictionary<string, object>> options = null, string customMethod = null, int? page = null, int? perPage = null)
        {
            if (method == null)
                method = HttpMethod.Get;

            if (!string.IsNullOrWhiteSpace(customMethod))
                method = new HttpMethod(customMethod);

            SetProxy();
            try
            {
                var request = PrepareRequest(url, data, method, options, page, perPage);
                LogRequest(request, url, method.ToString());

                HttpResponseMessage response;

                _authenticator.Authenticate(_client, request);

                response = _client.SendAsync(request).Result;

                LogResponse(response, url, method.ToString());

                _lastResponse = response;
                _wasLastResponseError = !_successStatusCodes.Contains(response.StatusCode);
                return response;
            }
            catch (Exception ex)
            {
                Log.Write(ex.ToString(), LogSeverity.Fatal);
                throw ex;
            }
        }

        private void LogRequest(HttpRequestMessage request, string url, string method)
        {
            Log.Write(string.Format("HTTP REQUEST {0}: {1}{2} ", method, _client.BaseAddress.ToString(), url), LogSeverity.Trace);
            foreach (var p in _parameters)
            {
                Log.Write(string.Format("{0} - {1}", p.Key, p.Value), LogSeverity.Trace);
            }
        }

        private void LogResponse(HttpResponseMessage response, string url, string method)
        {
            Log.Write(string.Format("HTTP RESPONSE {0}: {1}{2} ({3} {4}) ", method, _client.BaseAddress.ToString(), url, (int)response.StatusCode, response.StatusCode), LogSeverity.Trace);
            string content = response.Content.ReadAsStringAsync().Result;
            if (!string.IsNullOrEmpty(content))
            {
                Log.Write(string.Format("Content:  {0}", response.Content), LogSeverity.Trace);
            }
            //if (!string.IsNullOrEmpty(response.ErrorMessage))
            //{
            //    Log.Write(string.Format("Content:  {0}", response.ErrorMessage), LogSeverity.Trace);
            //}
            foreach (var p in response.Headers)
            {
                Log.Write(string.Format("{0} - {1}", p.Key, p.Value), LogSeverity.Trace);
            }
        }

        /// <summary>
        /// Create objects from a json string
        /// </summary>
        private static IEnumerable<T> CreateObjects<T>(string json)
        {
            var direct = JsonConvert.DeserializeObject<List<T>>(json);
            return direct;
        }

        /// <summary>
        /// Creates a single object from a json string
        /// </summary>
        private static T CreateObject<T>(string json)
        {
            var direct = JsonConvert.DeserializeObject<T>(json);
            return direct;
        }

        /// <summary>
        /// Sets the latest server result for a given entity
        /// </summary>
        private void SetLatestEntityServerResult(Entity entity, ServerResult result)
        {
            if (entity == null)
            {
                return;
            }
            lock (LatestEntityServerResults)
            {
                LatestEntityServerResults[entity.InternalIdentifier] = result;
            }
        }

        /// <summary>
        /// Get the latest server result for a given entity
        /// </summary>
        public ServerResult GetLatestEntityServerResult(Entity entity)
        {
            if (entity == null)
            {
                return null;
            }
            lock (LatestEntityServerResults)
            {
                ServerResult result;
                if (LatestEntityServerResults.TryGetValue(entity.InternalIdentifier, out result))
                {
                    return result;
                }
            }
            return null;
        }
    }

    public class PenneoDateConverter : JsonConverter
    {
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var unixTime = TimeUtil.ToUnixTime((DateTime)value);
            writer.WriteValue(unixTime);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var unixTime = Convert.ToInt64(reader.Value);
            return TimeUtil.FromUnixTime(unixTime);
        }

        public override bool CanConvert(Type objectType)
        {
            return true;
        }
    }
}