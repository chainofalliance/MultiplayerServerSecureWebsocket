using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PlayFab;
using PlayFab.MultiplayerModels;
using Microsoft.Extensions.Configuration;

namespace GameServer.ReverseProxy
{
    public class ServerEndpointFactory
    {
        public static DateTime TokenExpiration = default;
        private readonly ILogger _logger;
        private PlayFabMultiplayerInstanceAPI _multiplayerApi;
        private readonly IConfiguration _configuration;

        public ServerEndpointFactory(
            ILoggerFactory loggerFactory,
            IConfiguration configuration
        )
        {
            _logger = loggerFactory.CreateLogger<ServerEndpointFactory>();
            _configuration = configuration;
        }

        private PlayFabApiSettings FabSettingAPI()
        {
            var playfabConfig = _configuration.GetSection("PlayFab").Get<PlayFabSettings>();

            return new PlayFabApiSettings
            {
                TitleId = playfabConfig.TitleId,
                DeveloperSecretKey = playfabConfig.SecretKey
            };
        }

        public async Task ValidateEntityToken()
        {
            if (TokenExpiration == default(DateTime)
                || TokenExpiration < DateTime.UtcNow)
            {
                var entityTokenRequest = new PlayFab.AuthenticationModels.GetEntityTokenRequest();
                var authApi = new PlayFab.PlayFabAuthenticationInstanceAPI(FabSettingAPI());
                var entityTokenResult = await authApi.GetEntityTokenAsync(entityTokenRequest);

                if (entityTokenResult.Error != null)
                {
                    _logger.LogError($"Failed to ValidateEntityToken: {entityTokenResult.Error.GenerateErrorReport()}");
                    return;
                }

                var authContext = new PlayFabAuthenticationContext
                {
                    EntityId = entityTokenResult.Result.Entity.Id,
                    EntityType = entityTokenResult.Result.Entity.Type,
                    EntityToken = entityTokenResult.Result.EntityToken
                };

                _multiplayerApi = new PlayFabMultiplayerInstanceAPI(FabSettingAPI(), authContext);

                TokenExpiration = entityTokenResult.Result.TokenExpiration.Value;
                _logger.LogWarning($"Refreshed entity token. Expires at {TokenExpiration}");
            }
        }

        public async Task<string> ListBuildAliases(string environment)
        {
            var response = await _multiplayerApi.ListBuildAliasesAsync(new ListBuildAliasesRequest
            {
            });

            if (response.Error?.Error == PlayFabErrorCode.MultiplayerServerBadRequest)
            {
                _logger.LogError(
                    "Failed to request an alias");
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
                    "Failed to request a multiplayer server");
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