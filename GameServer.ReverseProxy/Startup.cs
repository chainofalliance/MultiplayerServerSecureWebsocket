using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace GameServer.ReverseProxy
{
    /// <summary>
    /// Configured in appsettings.json. Don't check in <c>SecretKey</c> to source control.
    /// </summary>
    public class PlayFabSettings
    {
        public string TitleId { get; set; }
        public string SecretKey { get; set; }
    }

    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(builder =>
                {
                    builder.SetIsOriginAllowed(_ => true)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                });
            });
            services.AddHealthChecks();

            services.AddHttpForwarder();

            services.AddSingleton<ServerEndpointFactory>(context =>
            {
                return new(
                    context.GetRequiredService<ILoggerFactory>(),
                    _configuration
                );
            });
            services.AddReverseProxy().LoadFromConfig(_configuration.GetSection("ReverseProxy"));
        }

        public void Configure(IApplicationBuilder app, IConfiguration configuration, IHttpForwarder forwarder)
        {
            var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = true
            });

            var requestOptions = new ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromSeconds(120)
            };
            var transformer = new GameServerRequestTransformer();

            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ProxyEndpointHandler");

                endpoints.Map("/{matchId:guid}/{queueName}/{**forwardPath}", async context =>
                {
                    var detailsFactory = context.RequestServices.GetRequiredService<ServerEndpointFactory>();
                    await detailsFactory.ValidateEntityToken();

                    var routeValues = context.GetRouteData().Values;

                    // respond with 400 Bad Request when the request path doesn't have the expected format
                    if (!Guid.TryParse(routeValues["matchId"]?.ToString(), out var matchId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;

                        return;
                    }
                    string queueName = routeValues["queueName"]?.ToString();
                    string serverEndpoint = null;

                    try
                    {
                        serverEndpoint = await detailsFactory.GetServerEndpoint(matchId, queueName);
                    }
                    catch (Exception)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    }

                    // We couldn't find a server with this build/session/region
                    // The client should use the 404 status code to display a useful message like "This server was not found or is no longer available"
                    if (serverEndpoint == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;

                        return;
                    }

                    var error = await forwarder.SendAsync(context,
                        serverEndpoint,
                        httpClient, requestOptions,
                        transformer);

                    if (error != ForwarderError.None)
                    {
                        var errorFeature = context.GetForwarderErrorFeature();
                        var exception = errorFeature.Exception;

                        Console.WriteLine(exception);
                    }
                });

                endpoints.Map("/request-match/{matchId:guid}", async context =>
                {
                    var env = _configuration.GetSection("Environment").Get<string>();

                    var routeValues = context.GetRouteData().Values;

                    // respond with 400 Bad Request when the request path doesn't have the expected format
                    if (!Guid.TryParse(routeValues["matchId"]?.ToString(), out var matchId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                    logger.LogDebug($"Request server for matchid {matchId}");

                    var detailsFactory = context.RequestServices.GetRequiredService<ServerEndpointFactory>();
                    await detailsFactory.ValidateEntityToken();

                    string alias = null;
                    try
                    {
                        alias = await detailsFactory.ListBuildAliases(env);
                    }
                    catch (Exception)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    }

                    string serverEndpoint = null;
                    try
                    {
                        serverEndpoint = await detailsFactory.RequestMultiplayerServer(alias, matchId);
                    }
                    catch (Exception)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    }

                    // We couldn't find a server with this build/session/region
                    // The client should use the 404 status code to display a useful message like "This server was not found or is no longer available"
                    if (serverEndpoint == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;

                        return;
                    }

                    var retryCounter = 0;
                    var RETRY_MAX = 10;
                    while(true) {
                        var details = await detailsFactory.GetMultiplayerServerDetails(matchId);
                        logger.LogDebug($"Get server details for {matchId} in state {details.State}");

                        if(details.State == "Terminating" || retryCounter == RETRY_MAX) {
                            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            logger.LogDebug($"Server for matchid {matchId} terminating or retry counter hit..");
                            return;
                        }

                        if(details.State == "Active") {
                            logger.LogDebug($"Server for matchid {matchId} active .. starting forwarder..");
                            var uriBuilder = new UriBuilder(details.FQDN)
                            {
                                Port = ServerEndpointFactory.GetEndpointPortNumber(details.Ports)
                            };

                            serverEndpoint = uriBuilder.ToString();

                            break;
                        }

                        retryCounter++;
                        await Task.Delay(1000);
                    }

                    var error = await forwarder.SendAsync(context,
                        serverEndpoint,
                        httpClient, requestOptions,
                        transformer);

                    if (error != ForwarderError.None)
                    {
                        var errorFeature = context.GetForwarderErrorFeature();
                        var exception = errorFeature.Exception;

                        logger.LogDebug(exception.Message);
                    }
                });
            });

            app.UseHealthChecks("/healthz");
            app.UseCors();
        }
    }

    /// <summary>
    ///     Forwards the request path and query parameters to given game server URL
    /// </summary>
    /// <example><c>/{gameId}/{queueName}/some/path?test=true</c> is mapped to <c>{serverUrl}/some/path?test=true</c></example>
    internal class GameServerRequestTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext,
            HttpRequestMessage proxyRequest, string serverEndpoint)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, serverEndpoint);

            var builder = new UriBuilder(serverEndpoint)
            {
                Query = httpContext.Request.QueryString.ToString()
            };

            var forwardPath = httpContext.GetRouteValue("forwardPath");

            if (forwardPath != null)
            {
                builder.Path = Path.Combine(builder.Path, forwardPath.ToString() ?? string.Empty);
            }

            proxyRequest.RequestUri = builder.Uri;
        }
    }
}