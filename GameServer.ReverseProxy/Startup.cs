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
                ActivityTimeout = TimeSpan.FromSeconds(5)
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

                    System.Console.WriteLine(queueName);
                    System.Console.WriteLine(matchId);

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

                        System.Console.WriteLine(exception);
                    }
                });

                // deprecated
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

                    var error = await forwarder.SendAsync(context,
                        serverEndpoint,
                        httpClient, requestOptions,
                        transformer);

                    if (error != ForwarderError.None)
                    {
                        var errorFeature = context.GetForwarderErrorFeature();
                        var exception = errorFeature.Exception;

                        System.Console.WriteLine(exception);
                    }
                });

                endpoints.Map("/request-match/{playfabId}/{ticketId}/{queueName}/{matchId:guid}", async context =>
                {
                    var env = _configuration.GetSection("Environment").Get<string>();

                    var routeValues = context.GetRouteData().Values;

                    string playfabId = routeValues["playfabId"]?.ToString();
                    string ticketId = routeValues["ticketId"]?.ToString();
                    string queueName = routeValues["queueName"]?.ToString();
                    string serverEndpoint = null;

                    // respond with 400 Bad Request when the request path doesn't have the expected format
                    if (!Guid.TryParse(routeValues["matchId"]?.ToString(), out var matchId))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }

                    if (playfabId == null || ticketId == null || queueName == null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }

                    var detailsFactory = context.RequestServices.GetRequiredService<ServerEndpointFactory>();
                    await detailsFactory.ValidateEntityToken();

                    try
                    {
                        var ticketResult = await detailsFactory.GetMatchmakingTicket(ticketId, queueName);

                        System.Console.WriteLine("creator: " + ticketResult.Creator.Id);
                        System.Console.WriteLine("playfabId: " + playfabId);
                        System.Console.WriteLine("Status: " + ticketResult.Status);

                        var inTicket = ticketResult.Members.Find(elem => elem.Entity.Id.Equals(playfabId));
                        if (inTicket == null)
                        {
                            throw new Exception("PlayfabId: " + playfabId + " not in ticket");
                        }

                        if (ticketResult.Status.Equals("WaitingForServer") || ticketResult.Status.Equals("Matched"))
                        {
                            serverEndpoint = await detailsFactory.GetServerEndpoint(matchId, queueName);
                        }
                        else if (ticketResult.Status.Equals("WaitingForMatch") || ticketResult.Status.Equals("WaitingForPlayers"))
                        {
                            System.Console.WriteLine("Created: " + ticketResult.Created);
                            var diffInSeconds = (ticketResult.Created - DateTime.Now).TotalSeconds;

                            System.Console.WriteLine("diffInSeconds: " + diffInSeconds);
                            if (diffInSeconds < 30)
                            {
                                throw new Exception("Ticket not old enough to start a server. Diff: " + diffInSeconds);
                            }

                            var isCanceled = await detailsFactory.CancelMatchmakingTicket(ticketId, queueName);

                            if (isCanceled == false)
                            {
                                throw new Exception("Error during canceling ticket");
                            }

                            var alias = await detailsFactory.ListBuildAliases(env);

                            if (alias == null)
                            {
                                throw new Exception("No aliases found");
                            }

                            serverEndpoint = await detailsFactory.RequestMultiplayerServer(alias, matchId);
                        }
                    }
                    catch (Exception e)
                    {
                        System.Console.WriteLine(e.Message);
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        return;
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

                        System.Console.WriteLine(exception);
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