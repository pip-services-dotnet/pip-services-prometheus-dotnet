﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using PipServices3.Commons.Config;
using PipServices3.Commons.Refer;
using PipServices3.Commons.Run;
using PipServices3.Components.Count;
using PipServices3.Components.Info;
using PipServices3.Components.Log;
using PipServices3.Rpc.Connect;

namespace PipServices3.Prometheus.Count
{
    /// <summary>
    /// Performance counters that send their metrics to Prometheus service.
    /// 
    /// The component is normally used in passive mode conjunction with <a href="https://pip-services3-dotnet.github.io/pip-services3-prometheus-dotnet/class_pip_services_1_1_prometheus_1_1_services_1_1_prometheus_metrics_service.html">PrometheusMetricsService</a>.
    /// Alternatively when connection parameters are set it can push metrics to Prometheus PushGateway.
    /// 
    /// ### Configuration parameters ###
    /// 
    /// connection(s):
    /// - discovery_key:         (optional) a key to retrieve the connection from <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_connect_1_1_i_discovery.html">IDiscovery</a>
    /// - protocol:              connection protocol: http or https
    /// - host:                  host name or IP address
    /// - port:                  port number
    /// - uri:                   resource URI or connection string with all parameters in it
    /// 
    /// options:
    /// - retries:               number of retries(default: 3)
    /// - connect_timeout:       connection timeout in milliseconds(default: 10 sec)
    /// - timeout:               invocation timeout in milliseconds(default: 10 sec)
    /// 
    /// ### References ###
    /// 
    /// - *:logger:*:*:1.0           (optional) <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_log_1_1_i_logger.html">ILogger</a> components to pass log messages
    /// - *:counters:*:*:1.0         (optional) <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_count_1_1_i_counters.html">ICounters</a> components to pass collected measurements
    /// - *:discovery:*:*:1.0        (optional) <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_connect_1_1_i_discovery.html">IDiscovery</a> services to resolve connection
    /// </summary>
    /// <example>
    /// <code>
    /// var counters = new PrometheusCounters();
    /// counters.Configure(ConfigParams.FromTuples(
    /// "connection.protocol", "http",
    /// "connection.host", "localhost",
    /// "connection.port", 8080 ));
    /// 
    /// counters.Open("123");
    /// 
    /// counters.Increment("mycomponent.mymethod.calls");
    /// var timing = counters.BeginTiming("mycomponent.mymethod.exec_time");
    /// try {
    /// ...
    /// } finally {
    /// timing.endTiming();
    /// }
    /// counters.dump();
    /// </code>
    /// </example>
    public class PrometheusCounters : CachedCounters, IReferenceable, IOpenable
    {
        private CompositeLogger _logger = new CompositeLogger();
        private HttpConnectionResolver _connectionResolver = new HttpConnectionResolver();
        private bool _opened;
        private bool _pushEnabled;
        private string _source;
        private string _instance;
        private HttpClient _client;
        private Uri _requestUri;

        /// <summary>
        /// Creates a new instance of the performance counters.
        /// </summary>
        public PrometheusCounters()
        { }

        /// <summary>
        /// Creates a new instance of the performance counters.
        /// </summary>
        /// <param name="config">configuration parameters to be set.</param>
        public override void Configure(ConfigParams config)
        {
            base.Configure(config);

            _connectionResolver.Configure(config);
            _source = config.GetAsStringWithDefault("source", _source);
            _instance = config.GetAsStringWithDefault("instance", _instance);
            _pushEnabled = config.GetAsBooleanWithDefault("push_enabled", true);
        }

        /// <summary>
        /// Sets references to dependent components.
        /// </summary>
        /// <param name="references">references to locate the component dependencies.</param>
        public virtual void SetReferences(IReferences references)
        {
            _logger.SetReferences(references);
            _connectionResolver.SetReferences(references);

            var contextInfo = references.GetOneOptional<ContextInfo>(
                new Descriptor("pip-services3", "context-info", "default", "*", "1.0"));
            if (contextInfo != null && string.IsNullOrEmpty(_source))
                _source = contextInfo.Name;
            if (contextInfo != null && string.IsNullOrEmpty(_instance))
                _instance = contextInfo.ContextId;
        }

        /// <summary>
        /// Checks if the component is opened.
        /// </summary>
        /// <returns>true if the component has been opened and false otherwise.</returns>
        public bool IsOpen()
        {
            return _opened;
        }

        /// <summary>
        /// Opens the component.
        /// </summary>
        /// <param name="correlationId">(optional) transaction id to trace execution through call chain.</param>
        public async Task OpenAsync(string correlationId)
        {
            if (_opened) return;

            try
            {
                var connection = await _connectionResolver.ResolveAsync(correlationId);
                var job = _source ?? "unknown";
                var instance = _instance ?? Environment.MachineName;
                var route = $"{connection.Uri}/metrics/job/{job}/instance/{instance}";
                _requestUri = new Uri(route, UriKind.Absolute);

                _client = new HttpClient();
            }
            catch (Exception ex)
            {
                _client = null;
                _logger.Warn(correlationId, "Connection to Prometheus server is not configured: " + ex.Message);
            }
            finally
            {
                _opened = true;
            }
        }

        /// <summary>
        /// Closes component and frees used resources.
        /// </summary>
        /// <param name="correlationId">(optional) transaction id to trace execution through call chain.</param>
        public async Task CloseAsync(string correlationId)
        {
            _opened = false;
            _client = null;
            _requestUri = null;

            await Task.Delay(0);
        }

        /// <summary>
        /// Saves the current counters measurements.
        /// </summary>
        /// <param name="counters">current counters measurements to be saves.</param>
        protected override void Save(IEnumerable<Counter> counters)
        {
            if (_client == null || !_pushEnabled)
            {
                return;
            }

            try
            {
                var body = PrometheusCounterConverter.ToString(counters, null, null);

                using (HttpContent requestContent = new StringContent(body, Encoding.UTF8, "text/plain"))
                {
                    HttpResponseMessage response = _client.PutAsync(_requestUri, requestContent).Result;
                    if ((int)response.StatusCode >= 400)
                        _logger.Error("prometheus-counters", "Failed to push metrics to prometheus");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("prometheus-counters", ex, "Failed to push metrics to prometheus");
            }
        }
    }
}
