using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PlayFab;
using PlayFab.MultiplayerModels;

namespace GameServer.ReverseProxy
{
    public class ServerEndpointFactory
    {
        private readonly ILogger _logger;
        private readonly PlayFabMultiplayerInstanceAPI _multiplayerApi;

        public ServerEndpointFactory(ILoggerFactory loggerFactory, PlayFabMultiplayerInstanceAPI multiplayerApi)
        {
            _logger = loggerFactory.CreateLogger<ServerEndpointFactory>();
            _multiplayerApi = multiplayerApi;
        }

        public async Task<string> ListBuildAliases(string environment)
        {
            var response = await _multiplayerApi.ListBuildAliasesAsync(new ListBuildAliasesRequest
            {
            });

            if (response.Error?.Error == PlayFabErrorCode.MultiplayerServerBadRequest)
            {
                _logger.LogError(
                    "Failed to request a aliase");
                return null;
            }

            if (response.Error != null)
            {
                _logger.LogError("{Request} failed: {Message}", nameof(_multiplayerApi.ListBuildAliasesAsync),
                    response.Error.GenerateErrorReport());

                throw new Exception(response.Error.GenerateErrorReport());
            }

            foreach (var alias in response.Result.BuildAliases)
            {
                if (alias.AliasName == environment)
                {
                    return alias.AliasId;
                }
            }

            return null;
        }

        public async Task<GetMatchmakingTicketResult> GetMatchmakingTicket(string ticketId, string queueName)
        {
            var response = await _multiplayerApi.GetMatchmakingTicketAsync(new GetMatchmakingTicketRequest
            {
                TicketId = ticketId,
                QueueName = queueName
            });

            if (response.Error?.Error == PlayFabErrorCode.MatchmakingTicketNotFound)
            {
                _logger.LogError(
                    "Matchmaking ticket not found");
                return null;
            }

            if (response.Error != null)
            {
                _logger.LogError("{Request} failed: {Message}", nameof(_multiplayerApi.GetMatchmakingTicketAsync),
                    response.Error.GenerateErrorReport());

                throw new Exception(response.Error.GenerateErrorReport());
            }

            return response.Result;
        }

        public async Task<bool> CancelMatchmakingTicket(string ticketId, string queueName)
        {
            var response = await _multiplayerApi.CancelMatchmakingTicketAsync(new CancelMatchmakingTicketRequest
            {
                TicketId = ticketId,
                QueueName = queueName
            });

            if (response.Error?.Error == PlayFabErrorCode.MatchmakingTicketNotFound)
            {
                _logger.LogError(
                    "Matchmaking ticket not found");
                return false;
            }

            if (response.Error != null)
            {
                _logger.LogError("{Request} failed: {Message}", nameof(_multiplayerApi.CancelMatchmakingTicketAsync),
                    response.Error.GenerateErrorReport());

                throw new Exception(response.Error.GenerateErrorReport());
            }

            return true;
        }

        public async Task<string> RequestMultiplayerServer(string alias, Guid matchId)
        {
            var response = await _multiplayerApi.RequestMultiplayerServerAsync(new RequestMultiplayerServerRequest
            {
                PreferredRegions = new List<string>() { "NorthEurope" },
                SessionId = matchId.ToString(),
                BuildAliasParams = new BuildAliasParams { AliasId = alias },
                SessionCookie = "AI"
            });

            if (response.Error?.Error == PlayFabErrorCode.MultiplayerServerBadRequest)
            {
                _logger.LogError(
                    "Failed to request a mulitplayer server");
                return null;
            }

            if (response.Error != null)
            {
                _logger.LogError("{Request} failed: {Message}", nameof(_multiplayerApi.RequestMultiplayerServerAsync),
                    response.Error.GenerateErrorReport());

                throw new Exception(response.Error.GenerateErrorReport());
            }

            var uriBuilder = new UriBuilder(response.Result.FQDN)
            {
                Port = GetEndpointPortNumber(response.Result.Ports)
            };

            return uriBuilder.ToString();
        }

        public async Task<string> GetServerEndpoint(Guid matchId, string queueName)
        {
            var response = await _multiplayerApi.GetMatchAsync(new GetMatchRequest
            {
                MatchId = matchId.ToString(),
                QueueName = queueName.ToString()
            });

            if (response.Error?.Error == PlayFabErrorCode.MultiplayerServerNotFound)
            {
                _logger.LogError(
                    "Server not found: Match ID = {MatchId}, Queue Name = {QueueName}",
                    matchId, queueName);

                return null;
            }

            if (response.Error != null)
            {
                _logger.LogError("{Request} failed: {Message}", nameof(_multiplayerApi.GetMatchAsync),
                    response.Error.GenerateErrorReport());

                throw new Exception(response.Error.GenerateErrorReport());
            }

            var uriBuilder = new UriBuilder(response.Result.ServerDetails.Fqdn)
            {
                Port = GetEndpointPortNumber(response.Result.ServerDetails.Ports)
            };

            return uriBuilder.ToString();
        }

        private static int GetEndpointPortNumber(IEnumerable<Port> ports)
        {
            // replace this logic with whatever is configured for your build i.e. getting a port by name
            return ports.First().Num;
        }
    }
}