﻿using GuardianService.Configs;
using GuardianService.Model;
using GuardianService.Util;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using MySqlX.XDevAPI;
using Org.BouncyCastle.Utilities;
using System.Data;
using YamlDotNet.Serialization.NodeDeserializers;

namespace GuardianService.Services.AWS
{
    public class RDS
    {
        static string connectionString;
        private static MySqlConnection? conn = null;

        static RDS()
        {
            connectionString = $"Server={GUARDIAN_CONFIGS.RDS.SERVER}; " +
                               $"Database={GUARDIAN_CONFIGS.RDS.DATABASE}; " +
                               $"UID={GUARDIAN_CONFIGS.RDS.USERNAME}; " +
                               $"password={GUARDIAN_CONFIGS.RDS.PASSWORD}; " +
                               $"Port={GUARDIAN_CONFIGS.RDS.PORT}; ";
        }
        public static bool CheckAWSRDSConnection()
        {
            if(!string.IsNullOrEmpty(connectionString)) 
            {
                try
                {
                    using(MySqlConnection conn = new MySqlConnection(connectionString))
                    {
                        conn.Open();
                        GLogger.LogGreen("SUCCESS","RDS", "Connected to RDS!");
                        return true;
                    }
                }
                catch (Exception e)
                {
                    GLogger.LogRed("ERR", "RDS", $"Failed to connect: {e}");
                    return false;
                }
            }
            else
            {
                GLogger.LogRed("ERR", "RDS", "Connection string is empty or null");
                return false;
            }
        }

        private static void ShowTables()
        {
            string query = @"SHOW TABLES";
            bool connected = TryConnect();
            if (connected)
            {
                MySqlCommand command = new MySqlCommand(query, conn);
                MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read()) 
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        Console.Write(reader[i].ToString());
                        if (i < reader.FieldCount - 1)
                        {
                            Console.Write(", ");
                        }
                    }
                    Console.WriteLine();
                }
            }
        }

        private static bool TryConnect()
        {
            if(conn !=null && conn.State == ConnectionState.Open)
            {
                return true;
            }

            try
            {
                conn = new MySqlConnection(connectionString);
                conn.Open();
                GLogger.LogGreen("SUCCESS", "RDS", "Connected to RDS!");
                return true;
            }
            catch(Exception e) 
            {
                GLogger.LogRed("ERR", "RDS", $"Failed to connect: {e}");
                return false;
            }
        }

        public static void CloseConnection()
        {
            if(conn != null && conn.State == ConnectionState.Open)
            {
                conn.Close();
                GLogger.LogGreen("info", "RDS", "Connection closed");
            }
        }

        public class Auth
        {
            public static void InsertNewOAuthClient(Model.OAuthClient client)
            {
                string query = @"
                                INSERT INTO `Guardian`.`AuthDB`
                                (clientId, clientSecret, clientName, granttypes, clientStatus, businessCode, isActive)
                                VALUES
                                (@ClientId, @ClientSecret, @ClientName, @GrantTypes, @ClientStatus, @BusinessCode, @IsActive)
                                ";
                bool connected = RDS.TryConnect();
                if(connected) 
                {
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);

                    command.Parameters.AddWithValue("@ClientId", client.clientID);
                    command.Parameters.AddWithValue("@ClientSecret", client.clientSecret);
                    command.Parameters.AddWithValue("@ClientName", client.clientName);
                    command.Parameters.AddWithValue("@GrantTypes", client.grantTypes);
                    command.Parameters.AddWithValue("@ClientStatus", client.clientStatus);
                    command.Parameters.AddWithValue("@BusinessCode", client.businessCode);
                    command.Parameters.AddWithValue("@IsActive", client.isActive);

                    int result = command.ExecuteNonQuery();

                    if(result > 0)
                    {
                        GLogger.LogGreen("SUCCESS", "RDS-CRUD", $"Insert {client.clientID} successfully!");
                    }
                    else
                    {
                        GLogger.LogRed("ERR", "RDS-CRUD", $"Failed to Insert {client.clientID}");
                    }
                }
            }

            public static bool ValidateOAuthClient(string clientID, string clientSecret)
            {
                string query = "SELECT clientSecret " +
                               "FROM AuthDB " +
                               "WHERE clientID = @ClientId LIMIT 1";

                bool connected = RDS.TryConnect();
                if (connected) 
                {
                    GLogger.Log("RDS-VALIDATE-CLIENT", $"Start validate client: {clientID}");
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);
                    command.Parameters.AddWithValue("@ClientId", clientID);

                    string? querySecret = command.ExecuteScalar() as string;

                    if(querySecret != null && querySecret == clientSecret)
                    {
                        GLogger.LogGreen("SUCCESS", "RDS-VALIDATE-CLIENT", $"Validate OAuth client success! {clientID}");
                        return true;
                    }
                    else
                    {
                        GLogger.LogRed("INVALID", "RDS-VALIDATE-CLIENT", $"Failed to validate {clientID}");
                        return false;
                    }
                }
                else
                {
                    GLogger.LogRed("ERR", "RDS-CONN", "Failed to connect to RDS, ABORT validate client action");
                    return false;
                }
            }

            public static KeyValuePair<bool, bool> CheckClientHasLiveToken(string clientId)
            {
                string query = @"SELECT accessToken
                               FROM AccessToken
                               WHERE associatedClient = @ClientId 
                                    AND expirationAt > NOW()
                               LIMIT 1";

                bool connected = RDS.TryConnect();

                if (connected) 
                {
                    GLogger.Log("RDS-VALIDATE-AccessToken", $"Start validate client: {clientId}");
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);
                    command.Parameters.AddWithValue("@ClientId", clientId);

                    using(MySqlDataReader reader = command.ExecuteReader()) 
                    {
                        if(reader.Read()) 
                        {
                            GLogger.Log("Check Existing AccessToken", "Found");
                            return new KeyValuePair<bool, bool>(true, true);
                        }
                        else
                        {
                            GLogger.Log("Check Existing AccessToken", "Not Found");
                            return new KeyValuePair<bool, bool>(true, false);
                        }
                    }
                }
                else
                {
                    GLogger.LogRed("ERROR", "Eheck Existing AccessToken", "Connection Failed");
                    return new KeyValuePair<bool, bool>(false, false); 
                }
            }

            public static void SaveNewAccessToken(Model.AccessToken accessToken)
            {
                string query = @"
                                INSERT INTO `Guardian`.`AccessToken`
                                (accessToken, ssoUsed, isActive, isSSO, createdAt, expirationAt, expirationDuration, scopes, state, associatedClient, issuer, refreshToken)
                                VALUES
                                (@accessToken, @ssoUsed, @isActive, @isSSO, @createdAt, @expirationAt, @expirationDuration, @scopes, @state, @associatedClient, @issuer, @refreshToken)
                                ";
                bool connected = RDS.TryConnect();
                if (connected)
                {
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);

                    command.Parameters.AddWithValue("@accessToken", accessToken.value);
                    command.Parameters.AddWithValue("@ssoUsed", accessToken.ssoUsed);
                    command.Parameters.AddWithValue("@isActive", accessToken.isActive);
                    command.Parameters.AddWithValue("@isSSO", accessToken.isSSO);
                    command.Parameters.AddWithValue("@createdAt", accessToken.createdAt);
                    command.Parameters.AddWithValue("@expirationAt", accessToken.expirationAt);
                    command.Parameters.AddWithValue("@expirationDuration", accessToken.expirationDuration);
                    command.Parameters.AddWithValue("@scopes", accessToken.scopes);
                    command.Parameters.AddWithValue("@state", accessToken.state);
                    command.Parameters.AddWithValue("@associatedClient", accessToken.associatedClient);
                    command.Parameters.AddWithValue("@issuer", accessToken.issuer);
                    command.Parameters.AddWithValue("@refreshToken", accessToken.refreshToken);

                    int result = command.ExecuteNonQuery();

                    if (result > 0)
                    {
                        GLogger.LogGreen("SUCCESS", "RDS-AccessToken", $"Insert {accessToken.associatedClient}'s token: {accessToken.value} successfully!");
                    }
                    else
                    {
                        GLogger.LogRed("ERR", "RDS-CRUD", $"Failed to Insert {accessToken.associatedClient}'s token: {accessToken.value}");
                    }
                }
            }

            public static void SaveNewRefreshToken(Model.RefreshToken refreshToken)
            {
                string query = @"
                                INSERT INTO `Guardian`.`RefreshToken`
                                (value, isActive, createdAt, expirationAt, expirationDuration, associatedClient, issuer, lastAssociateAccessToken)
                                VALUES
                                (@value, @isActive, @createdAt, @expirationAt, @expirationDuration, @associatedClient, @issuer, @lastAssociateAccessToken)
                                ";
                bool connected = RDS.TryConnect();
                if (connected)
                {
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);

                    command.Parameters.AddWithValue("@value", refreshToken.value);
                    command.Parameters.AddWithValue("@isActive", refreshToken.isActive);
                    command.Parameters.AddWithValue("@createdAt", refreshToken.createdAt);
                    command.Parameters.AddWithValue("@expirationAt", refreshToken.expirationAt);
                    command.Parameters.AddWithValue("@expirationDuration", refreshToken.expirationDuration);
                    command.Parameters.AddWithValue("@associatedClient", refreshToken.associatedClient);
                    command.Parameters.AddWithValue("@issuer", refreshToken.issuer);
                    command.Parameters.AddWithValue("@lastAssociateAccessToken", refreshToken.lastAssociatedAccessToken);

                    int result = command.ExecuteNonQuery();

                    if (result > 0)
                    {
                        GLogger.LogGreen("SUCCESS", "RDS-AccessToken", $"Insert {refreshToken.associatedClient}'s token: {refreshToken.value} successfully!");
                    }
                    else
                    {
                        GLogger.LogRed("ERR", "RDS-CRUD", $"Failed to Insert {refreshToken.associatedClient}'s token: {refreshToken.value}");
                    }
                }
            }

            public static async Task<Model.AccessToken> RenewAccessToken (string clientId) 
            {
                string query = "SELECT accessToken, state, ssoUsed, isSSO, scopes, refreshToken, issuer " +
                               "FROM AccessToken " +
                               "WHERE associatedClient = @ClientId " +
                                      "AND expirationAt > NOW()" +
                               "LIMIT 1";
                bool connected = RDS.TryConnect();
                if (connected)
                {
                    GLogger.Log("RDS-AccessToken", $"Start indexing token for client: {clientId}");
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);
                    command.Parameters.AddWithValue("@ClientId", clientId);

                    using (MySqlDataReader reader = command.ExecuteReader())
                    {
                        if(reader.Read())
                        {
                            string? accessTokenValue = reader["accessToken"] as string;
                            string? state = reader["state"] as string;
                            bool ssoUsed = reader.GetBoolean(reader.GetOrdinal("ssoUsed"));
                            bool isSSO = reader.GetBoolean(reader.GetOrdinal("isSSO"));
                            string? scopes = reader["scopes"] as string;
                            string? refreshToken = reader["refreshToken"] as string;
                            string? issuer = reader["issuer"] as string;

                            if (state != GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_REVOKED ||
                               state != GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_SUSPENDED ||
                               state != GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_EXPIRED ||
                               state != GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_LOCKED)
                            {
                                RDS.CloseConnection();
                                RDS.TryConnect();
                                string refreshTokenQuery = @"
                                                            UPDATE AccessToken
                                                            SET state = @NewState, createdAt = @NewCreatedAt, expirationAt = @NewExpiredAt
                                                            WHERE associatedClient = @ClientId
                                                             AND accessToken = @AccessTokenVal";

                                MySqlCommand updateCommand = new MySqlCommand(refreshTokenQuery, RDS.conn);
                                updateCommand.Parameters.AddWithValue("@NewState", GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_REFRESHED);
                                updateCommand.Parameters.AddWithValue("@NewCreatedAt", DateTime.UtcNow);
                                updateCommand.Parameters.AddWithValue("@NewExpiredAt", Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M!));
                                updateCommand.Parameters.AddWithValue("@ClientId", clientId);
                                updateCommand.Parameters.AddWithValue("AccessTokenVal", accessTokenValue);

                                int rowsAffected = updateCommand.ExecuteNonQuery();
                                if(rowsAffected > 0)
                                {

                                    GLogger.LogGreen("SUCCESS", "RDS-AccessToken", $"{clientId} token been refreshed with new expiration time!");
                                    Model.AccessToken refreshedToken = new Model.AccessToken();
                                    refreshedToken.value = accessTokenValue;
                                    refreshedToken.isSSO = isSSO;
                                    refreshedToken.ssoUsed = ssoUsed;
                                    refreshedToken.scopes = scopes;
                                    refreshedToken.issuer = issuer;
                                    refreshedToken.refreshToken = refreshToken;
                                    refreshedToken.expirationAt = Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M!);
                                    refreshedToken.associatedClient = clientId;

                                    return refreshedToken;
                                }
                                else
                                {
                                    GLogger.LogRed("Error", "RDS-AccessToken", "Failed to refresh token, error at failed to update the DB");
                                }
                            }
                            else if(state == GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_REVOKED ||
                                    state == GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_EXPIRED)
                            {
                                Model.AccessToken accessTokenCandidate = new Model.AccessToken();
                                accessTokenCandidate.value = await Services.AWS.KMS.GetAccessToken();
                                accessTokenCandidate.isSSO = isSSO;
                                accessTokenCandidate.ssoUsed = ssoUsed;
                                accessTokenCandidate.scopes = scopes;
                                accessTokenCandidate.issuer = issuer;
                                accessTokenCandidate.refreshToken = refreshToken;
                                accessTokenCandidate.createdAt = DateTime.UtcNow;
                                accessTokenCandidate.expirationAt = Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M!);
                                accessTokenCandidate.expirationDuration = (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M;
                                accessTokenCandidate.isActive = true;
                                accessTokenCandidate.state = GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_ISSUED;
                                accessTokenCandidate.associatedClient = clientId;

                                SaveNewAccessToken(accessTokenCandidate);
                                GLogger.LogGreen("SUCCESS", "RDS-AccessToken", $"New Token Issued to {clientId}, and saved to DB");
                                return accessTokenCandidate;
                            }
                            else
                            {
                                GLogger.LogYellow("WARN", "RDS-AccessToken", "Illegal token status found, Guardian won't refresh/generate new token, please unlock account");
                                return Util.Data.DUMMY_VOID_TOKEN(clientId);
                            }
                        }                      
                    }
                    
                }
                else
                {
                    GLogger.LogRed("ERR", "RDS-CONN", "Failed to connect to RDS, ABORT validate client action");
                    return Util.Data.DUMMY_VOID_TOKEN(clientId); ;
                }
                return Util.Data.DUMMY_VOID_TOKEN(clientId);
            }

            public static bool CheckAndValidateRefreshtokenFromClient(string clientId)
            {
                DeepCleanExpiredRefreshToken();
                string getValidTokenQuery = "SELECT value, isActive, expirationAt, associatedClient, lastAssociateAccessToken " +
                               "FROM RefreshToken " +
                               "WHERE associatedClient = @ClientId " +
                                      "AND expirationAt > NOW()" +
                                      "AND isActive = true" +
                               "LIMIT 1";

                bool connected = RDS.TryConnect();
                if (connected)
                {
                    GLogger.Log("RDS-RefreshToken", $"Start indexing refresh token for client: {clientId}");
                    MySqlCommand command = new MySqlCommand(getValidTokenQuery, RDS.conn);
                    command.Parameters.AddWithValue("@ClientId", clientId);

                    using (MySqlDataReader reader = command.ExecuteReader())
                    {
                        if(reader.Read()) 
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            public static string GetRefreshTokenValueFromClientId(string clientId)
            {

                string query = @"SELECT value FROM RefreshToken WHERE associatedClient = @ClientId LIMIT 1";
                bool connected = RDS.TryConnect();
                if (connected)
                {
                    GLogger.Log("RDS-RefreshToken", $"Start get refresh token value for client: {clientId}");
                    MySqlCommand command = new MySqlCommand(query, RDS.conn);
                    command.Parameters.AddWithValue("@ClientId", clientId);

                    using (MySqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string? refreshTokenValue = reader["value"] as string;
                            return refreshTokenValue!;
                        }
                    }
                }
                GLogger.LogRed("ERR", "RDS-GetRefreshToken", $"Failed to retrieve refresh token value for client {clientId}");
                return "error";
            }

            public static void DeepCleanExpiredRefreshToken()
            {
                string updateQuery = "UPDATE RefreshToken SET isActive = false " +
                                     "WHERE isActive = true " +
                                     "AND expirationAt < NOW()";

                bool connected = RDS.TryConnect();
                if (connected)
                {
                    GLogger.Log("RDS-RefreshToken", "Start updating expired refresh tokens to inactive");

                    MySqlCommand updateCommand = new MySqlCommand(updateQuery, RDS.conn);
                    int rowsAffected = updateCommand.ExecuteNonQuery();

                    GLogger.Log("RDS-RefreshToken", $"Updated {rowsAffected} tokens to inactive");
                }
            }

            public static void DeepCleanExpiredAccessToken()
            {
                string updateQuery = "UPDATE AccessToken SET isActive = false " +
                                     "WHERE isActive = true AND (expirationAt < NOW() " +
                                     "OR state = 'SUSPENDED' " +
                                     "OR state = 'EXPIRED' " +
                                     "OR state = 'REVOKED' " +
                                     "OR state = 'LOCKED')";

                bool connected = RDS.TryConnect();
                if (connected)
                {
                    GLogger.Log("RDS-accessToken", "Start updating expired access tokens to inactive");

                    MySqlCommand updateCommand = new MySqlCommand(updateQuery, RDS.conn);
                    int rowsAffected = updateCommand.ExecuteNonQuery();

                    GLogger.Log("RDS-accessToken", $"Updated {rowsAffected} tokens to inactive");
                }
            }

            public static async Task<Model.AccessToken> CreateNewAccessTokenAttachRefreshToken(string clientId)
            {
                DeepCleanExpiredAccessToken();
                string accessTokenValue = await Services.AWS.KMS.GetAccessToken();
                string newRefreshTokenValue = await Services.AWS.KMS.GetRefreshToken();

                //needs to check if current client has a valid refresh token.
                bool liveRefreshTokenExist = CheckAndValidateRefreshtokenFromClient(clientId);
                if (liveRefreshTokenExist) 
                {
                    string currentRefreshTokenValue = GetRefreshTokenValueFromClientId(clientId);
                    Model.AccessToken accessToken = new Model.AccessToken();
                    accessToken.value = accessTokenValue;
                    accessToken.isSSO = false;
                    accessToken.ssoUsed = false;
                    accessToken.scopes = "currently no scopes";
                    accessToken.issuer = "Guardian";
                    accessToken.refreshToken = currentRefreshTokenValue;
                    accessToken.createdAt = DateTime.UtcNow;
                    accessToken.expirationAt = Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M!);
                    accessToken.expirationDuration = (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M;
                    accessToken.isActive = true;
                    accessToken.state = GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_ISSUED;
                    accessToken.associatedClient = clientId;
                    SaveNewAccessToken(accessToken);

                    return accessToken;

                }
                else
                {
                    Model.RefreshToken newRefreshToken = new Model.RefreshToken();
                    newRefreshToken.value = newRefreshTokenValue;
                    newRefreshToken.isActive = true;
                    newRefreshToken.createdAt = DateTime.UtcNow;
                    newRefreshToken.expirationDuration = (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_30D!;
                    newRefreshToken.expirationAt = Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_30D!);
                    newRefreshToken.associatedClient = clientId;
                    newRefreshToken.issuer = "Guardian";
                    newRefreshToken.lastAssociatedAccessToken = accessTokenValue;
                    SaveNewRefreshToken(newRefreshToken);

                    Model.AccessToken accessToken = new Model.AccessToken();
                    accessToken.value = accessTokenValue;
                    accessToken.isSSO = false;
                    accessToken.ssoUsed = false;
                    accessToken.scopes = "currently no scopes";
                    accessToken.issuer = "Guardian";
                    accessToken.refreshToken = newRefreshTokenValue;
                    accessToken.createdAt = DateTime.UtcNow;
                    accessToken.expirationAt = Services.Auth.CalculateExpirationDateTime(DateTime.UtcNow, (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M!);
                    accessToken.expirationDuration = (int)GUARDIAN_CONFIGS.OAuth.TOKEN_LIFE_SPAN_10M;
                    accessToken.isActive = true;
                    accessToken.state = GUARDIAN_CONFIGS.OAuth.TOKEN_STATE_ISSUED;
                    accessToken.associatedClient = clientId;
                    SaveNewAccessToken(accessToken);

                    return accessToken;
                }
                
            }
        }
    }
}
