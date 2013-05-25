﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;

namespace SimpleBatch.Client
{
    // Invocation that calls out to a web service.
    // This will ping _serviceUri and pass a function string.
    // The default schema is that the function string is a can be resolved with IFunctionTable.Lookup,
    // and so it's expected to be a FunctionLocation. 
    public class WebFunctionInvoker : FunctionInvoker
    {
        private readonly string _serviceUri;
        private readonly Func<string, string> _functionResolver;

        public WebFunctionInvoker(Func<string,string> functionResolver, string serviceUri)
        {
            if (serviceUri == null)
            {
                throw new ArgumentNullException("serviceUri");
            }

            if (!serviceUri.EndsWith("/"))
            {
                serviceUri += "/";
            }

            _functionResolver = functionResolver;

            _serviceUri = serviceUri;
        }

        protected override Guid MakeWebCall(string functionShortName, IDictionary<string, string> parameters, IEnumerable<Guid> prereqs)
        {
            var function = ResolveFunction(functionShortName);
            string uri = MakeUriRun(function, parameters);

            Guid[] body = prereqs.ToArray();

            try
            {
                var val = Send<BeginRunResult>(uri, "POST", body);
                return val.Instance;
            }
            catch (WebException e)
            {
                // Give a more useful error.
                var x = e.Response as HttpWebResponse;
                if (x != null)
                {
                    if (x.StatusCode == HttpStatusCode.BadRequest)
                    {
                        string message = new StreamReader(x.GetResponseStream()).ReadToEnd();
                        throw new InvalidOperationException("Attempt to invoke function failed: " + message);
                    }
                }
                throw;
            }            
        }

        // Return task that is signaled 
        // $$$ This is sync. Convert to be async. 
        protected override Task WaitOnCall(Guid id)
        {
            string uri = MakeUriGetStatus(id);

            TaskCompletionSource<object> tsc = new TaskCompletionSource<object>();
            

            int wait = 1000;
            int maxwait = 60 * 1000;

            while (true)
            {
                var val = Send<FunctionInstanceStatusResult>(uri, "GET");

                switch (val.Status)
                {
                    case FunctionInstanceStatus.CompletedFailed:
                        string msg = string.Format("Function failed with {0}:{1}", val.ExceptionType, val.ExceptionMessage);
                        var ex = new InvalidOperationException(msg);
                        tsc.SetException(ex);
                        return tsc.Task;
                    
                    case FunctionInstanceStatus.CompletedSuccess:
                        tsc.SetResult(null);
                        return tsc.Task;
                }

                Thread.Sleep(wait);
                wait = Math.Min(maxwait, (int)(wait * 1.1));
            }
        }

        // $$$ Make this virtual to help with unit testing?
        // $$$ Would be nice to unify with Utility, but that would take a dependency reference we can't have. 
        // maybe do a source link instead?
        private static T Send<T>(string uri, string verb, object body = null)
        {            
            // Send 
            WebRequest request = WebRequest.Create(uri);
            request.Method = verb;
            request.ContentType = "application/json";
            request.ContentLength = 0;

            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                request.ContentLength = bytes.Length; // set before writing to stream
                var stream = request.GetRequestStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Close();
            }


            var response = request.GetResponse(); // does the actual web request

            var stream2 = response.GetResponseStream();
            var text = new StreamReader(stream2).ReadToEnd();
            stream2.Close();

            T val = JsonConvert.DeserializeObject<T>(text);
            return val;
        }

        // May convert a shortname to a fully qualified name that we can invoke.        
        string ResolveFunction(string functionShortName)
        {
            string fullName = _functionResolver(functionShortName);
            return fullName;
        }

        #region Web service
        // $$$ These structures must be structurally equivalent to the schema from the web service
        // Don't share an assembly because we want to keep our reference list down.
        // Result we get back from POST to api/execution/run?func={0}
        public class BeginRunResult
        {
            public Guid Instance { get; set; }
        }

        // Result we get back from GET to api/execution/GetStatus
        public class FunctionInstanceStatusResult
        {
            public FunctionInstanceStatus Status { get; set; }

            // For retrieving the console output. 
            // This is incrementally updated.
            public string OutputUrl { get; set; }

            // For failures
            public string ExceptionType { get; set; }
            public string ExceptionMessage { get; set; }
        }

        public enum FunctionInstanceStatus
        {
            None, // shouldn't be used. Can indicate a serialization error.
            Queued, // waiting in the execution queue.
            Running, // Now running. An execution node has picked up ownership.
            CompletedSuccess, // ran to completion, either via success or a user error (threw exception)
            CompletedFailed, // ran to completion, but function through an exception before finishing
        }

        private string MakeUriRun(string function, IDictionary<string, string> parameters)
        {
            // $$$ What about escaping and encoding?
            StringBuilder sb = new StringBuilder();
            sb.Append(_serviceUri);
            sb.Append("api/execution/run");
            sb.AppendFormat("?func={0}", function);

            foreach (var kv in parameters)
            {
                sb.AppendFormat("&{0}={1}", kv.Key, kv.Value);
            }

            return sb.ToString();
        }

        private string MakeUriGetStatus(Guid id)
        {
            return string.Format("{0}api/execution/GetStatus?id={1}", _serviceUri, id);
        }
        #endregion 
    }
}